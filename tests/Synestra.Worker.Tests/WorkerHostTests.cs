using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class WorkerHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synestra-worker-tests", Guid.NewGuid().ToString("N"));
    private readonly ControlledTimeProvider _clock = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegistrationInitialHeartbeatAndServerCadenceSurviveAgentRestart()
    {
        var requests = new ConcurrentQueue<(Guid Worker, JsonElement Body)>();
        var heartbeatCount = 0;
        using var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Segments[^1] == "claims") return new(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                var worker = Guid.Parse(request.RequestUri!.Segments[^2].TrimEnd('/'));
                requests.Enqueue((worker, body));
                Assert.Equal(1, body.GetProperty("capacity").GetInt32());
                Assert.Equal(WorkerOptions.SupportedType, Assert.Single(body.GetProperty("supportedTypes").EnumerateArray()).GetString());
                Assert.False(request.Headers.Contains("Lease-Token"));
                return Registration(request, body, interval: 7);
            }
            Assert.Equal("heartbeat", request.RequestUri!.Segments[^1]);
            Assert.Equal(requests.Last().Body.GetProperty("sessionId").GetGuid().ToString("D"),
                Assert.Single(request.Headers.GetValues("Worker-Session-Id")));
            Interlocked.Increment(ref heartbeatCount);
            return new(HttpStatusCode.NoContent);
        });
        using (var host = Build(handler))
        {
            await host.StartAsync(Token);
            await _clock.WaitForDelayAsync(TimeSpan.FromSeconds(7), Token);
            Assert.Equal(1, heartbeatCount);
            _clock.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal(1, heartbeatCount);
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _clock.WaitForDelayAsync(TimeSpan.FromSeconds(7), Token);
            Assert.Equal(2, heartbeatCount);
            await StopAsync(host);
            Assert.Equal(0, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
            Assert.Equal(0, _clock.ActiveTimers);
        }
        using (var restarted = Build(handler))
        {
            await restarted.StartAsync(Token);
            await _clock.WaitForDelayAsync(TimeSpan.FromSeconds(7), Token);
            await StopAsync(restarted);
        }
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests.First().Worker, requests.Last().Worker);
        Assert.NotEqual(requests.First().Body.GetProperty("sessionId").GetGuid(), requests.Last().Body.GetProperty("sessionId").GetGuid());
        Assert.All(requests, item => Assert.Equal(7, item.Body.GetProperty("sessionId").GetGuid().Version));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShutdownCancelsInFlightRegistrationOrHeartbeat(bool registration)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, token) =>
        {
            if (!registration && request.Method == HttpMethod.Put)
                return Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            throw new InvalidOperationException();
        });
        using var host = Build(handler);
        await host.StartAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await StopAsync(host);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
        using var identity = WorkerIdentity.Open(_directory);
    }

    [Fact]
    public async Task HeartbeatRequestTimeoutStopsHostWithoutRetryAndDoesNotLogExceptionContents()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
                return Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        var logs = new Logs();
        using var host = Build(handler, logs);
        await host.StartAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await WaitForFailureAsync(host);
        Assert.Equal(1, calls);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData(409, "worker_session_replaced")]
    [InlineData(404, "worker_not_found")]
    [InlineData(400, "invalid_request")]
    [InlineData(500, "internal_error")]
    [InlineData(500, "private response token")]
    public async Task HeartbeatProtocolFailureStopsHostWithoutReregistering(int status, string code)
    {
        var registrations = 0;
        var heartbeats = 0;
        using var handler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                registrations++;
                return Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            }
            heartbeats++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { code, detail = "private response token" }) };
            response.Content.Headers.ContentType!.MediaType = "application/problem+json";
            return response;
        });
        var logs = new Logs();
        using var host = Build(handler, logs);
        await host.StartAsync(Token);
        await WaitForFailureAsync(host);
        Assert.Equal(1, registrations);
        Assert.Equal(1, heartbeats);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("private"));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData("ApiBaseAddress", "")]
    [InlineData("ApiBaseAddress", "ftp://localhost")]
    [InlineData("ApiBaseAddress", "https://user:secret@localhost")]
    [InlineData("ApiBaseAddress", "https://localhost/prefix")]
    [InlineData("ApiBaseAddress", "https://localhost/?query=value")]
    [InlineData("StateDirectory", "relative-directory")]
    [InlineData("Name", " ")]
    [InlineData("Name", "bad\0name")]
    public async Task InvalidConfigurationFailsBeforeIdentityOrNetwork(string setting, string value)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network must not be used."));
        var builder = Builder(handler);
        builder.Configuration["Worker:" + setting] = value;
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task UnsafeExceptionDetailsAreNotLoggedAndIdentityLockIsReleased()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private token connection string"));
        var logs = new Logs();
        using var host = Build(handler, logs);
        await host.StartAsync(Token);
        for (var attempt = 1; attempt < 3; attempt++)
        {
            await _clock.WaitForDelayAsync(TimeSpan.FromSeconds(1), Token);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        await WaitForFailureAsync(host);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("private"));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
        using var identity = WorkerIdentity.Open(_directory);
    }

    private IHost Build(HttpMessageHandler handler, Logs? logs = null) => Builder(handler, logs).Build();
    private HostApplicationBuilder Builder(HttpMessageHandler handler, Logs? logs = null)
    {
        var builder = WorkerHost.CreateBuilder(["--Worker:ApiBaseAddress", "http://localhost", "--Worker:StateDirectory", _directory]);
        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan });
        return builder;
    }

    private async Task WaitForFailureAsync(IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await StopAsync(host);
        Assert.Equal(1, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
    }
    private Task StopAsync(IHost host) => host.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

    internal static HttpResponseMessage Registration(HttpRequestMessage request, JsonElement body, int interval = 10) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                workerId = Guid.Parse(request.RequestUri!.Segments[^2].TrimEnd('/')),
                sessionId = body.GetProperty("sessionId").GetGuid(), name = body.GetProperty("name").GetString(), capacity = 1,
                supportedTypes = new[] { WorkerOptions.SupportedType },
                registeredAtUtc = DateTime.UnixEpoch, sessionStartedAtUtc = DateTime.UnixEpoch, lastSeenAtUtc = DateTime.UnixEpoch,
                heartbeatIntervalSeconds = interval, offlineAfterSeconds = 30
            })
        };

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);
        public void Dispose() { }
        private sealed class CapturedLogger(Logs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((formatter(state, exception), exception));
        }
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
