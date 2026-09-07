using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class WorkerExecutionTests : IDisposable
{
    private readonly ControlledTimeProvider _clock = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synestra-execution-tests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<JsonElement> _reports = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateSuccessOrInputFailureIsReportedThenSlotCanClaimAgain(bool invalid)
    {
        var execution = ExecutionTestProtocol.Execution(_clock, durationMs: 0);
        if (invalid) execution = execution with { Payload = JsonSerializer.Deserialize<JsonElement>("""{"values":[],"durationMs":0}""") };
        using var host = Host(execution);
        await host.StartAsync(Token);
        await IdleAsync();
        Assert.Equal(new[] { "registration", "heartbeat", "claims", "completion", "claims" }, _calls);
        var report = Assert.Single(_reports);
        Assert.Equal(7, report.GetProperty("reportId").GetGuid().Version);
        Assert.Equal(invalid ? "failed" : "succeeded", report.GetProperty("outcome").GetString());
        Assert.Equal(new[] { invalid ? "error" : "result", "outcome", "reportId" }.Order(), report.EnumerateObject().Select(p => p.Name).Order());
        if (invalid) Assert.Equal("invalid_workload_input", report.GetProperty("error").GetProperty("code").GetString());
        else Assert.Equal(2, report.GetProperty("result").GetProperty("sum").GetInt64());
        await StopAsync(host);
    }

    [Fact]
    public async Task FortySecondWorkRenewsAndHeartbeatsWithoutPrefetchOrOverlappingCompletion()
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock));
        await host.StartAsync(Token);
        for (var seconds = 0; seconds < 40; seconds += 10)
        {
            await _clock.WaitForTimersAsync(Token, Seconds(40 - seconds), Seconds(10), Seconds(10));
            Assert.Equal(1, Count("claims"));
            Assert.Empty(_reports);
            _clock.Advance(Seconds(10));
        }
        await IdleAsync();
        Assert.InRange(Count("renewal"), 3, 4);
        Assert.Equal(5, Count("heartbeat"));
        Assert.Equal(2, Count("claims"));
        Assert.Equal(2, Assert.Single(_reports).GetProperty("result").GetProperty("sum").GetInt64());
        var calls = _calls.ToArray();
        Assert.DoesNotContain("renewal", calls.Skip(Array.IndexOf(calls, "completion")));
        await StopAsync(host);
    }

    [Fact]
    public async Task HandlerFinishingDuringRenewalWaitsForThatRequestBeforeReporting()
    {
        var arrived = Signal();
        var release = Signal();
        var execution = ExecutionTestProtocol.Execution(_clock, durationMs: 12000);
        using var host = Host(execution, async (operation, _, token) =>
        {
            if (operation != "renewal") return null;
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return ExecutionTestProtocol.Json(new { execution.LeaseId, expiresAtUtc = _clock.GetUtcNow().AddSeconds(30).UtcDateTime });
        });
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(12), Seconds(10), Seconds(10));
        _clock.Advance(Seconds(10));
        await WaitAsync(arrived.Task);
        _clock.Advance(Seconds(2));
        Assert.Equal(0, Count("completion"));
        Assert.Equal(1, Count("claims"));
        release.TrySetResult();
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        Assert.Single(_reports);
        Assert.Equal(1, Count("renewal"));
        await StopAsync(host);
    }

    [Fact]
    public async Task EmptyClaimsWaitOneSecondAndOfflineRequiresHeartbeatBeforePolling()
    {
        using var host = Host(null, (operation, _, _) => Task.FromResult<HttpResponseMessage?>(
            operation == "claims" && Count("claims") == 1 ? ExecutionTestProtocol.Problem("worker_offline") : null));
        await host.StartAsync(Token);
        await IdleAsync();
        Assert.Equal(new[] { "registration", "heartbeat", "claims", "heartbeat" }, _calls);
        _clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, Count("claims"));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        Assert.Equal(2, Count("claims"));
        await StopAsync(host);
    }

    [Fact]
    public async Task OfflineHeartbeatResetsPeriodicCadence()
    {
        using var host = Host(null, (operation, _, _) => Task.FromResult<HttpResponseMessage?>(
            operation == "claims" && Count("claims") == 2 ? ExecutionTestProtocol.Problem("worker_offline") : null));
        await host.StartAsync(Token);
        await IdleAsync();
        _clock.Advance(Seconds(5));
        await _clock.WaitForTimersAsync(Token, Seconds(5), Seconds(1));
        Assert.Equal(2, Count("heartbeat"));
        _clock.Advance(Seconds(5));
        await _clock.WaitForTimersAsync(Token, Seconds(5), Seconds(1));
        Assert.Equal(2, Count("heartbeat"));
        _clock.Advance(Seconds(5));
        await IdleAsync();
        Assert.Equal(3, Count("heartbeat"));
        await StopAsync(host);
    }

    [Fact]
    public async Task ClaimArrivingAfterConservativeDeadlineNeverExecutes()
    {
        var arrived = Signal();
        var release = Signal();
        var execution = ExecutionTestProtocol.Execution(_clock, durationMs: 0, leaseSeconds: 6);
        using var host = Host(execution, async (operation, _, token) =>
        {
            if (operation != "claims" || Count("claims") != 1) return null;
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return ExecutionTestProtocol.Claim(execution);
        });
        await host.StartAsync(Token);
        await WaitAsync(arrived.Task);
        _clock.Advance(Seconds(2));
        release.TrySetResult();
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        Assert.Equal(2, Count("claims"));
        Assert.Empty(_reports);
        Assert.Equal(0, Count("renewal"));
        await StopAsync(host);
    }

    [Theory]
    [InlineData("lease_expired")]
    [InlineData("lease_not_active")]
    [InlineData("lease_ownership_lost")]
    public async Task RenewalConflictStopsHandlerWithoutInventingCompletion(string code)
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock), (operation, _, _) =>
            Task.FromResult<HttpResponseMessage?>(operation == "renewal" ? ExecutionTestProtocol.Problem(code) : null));
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(10), Seconds(10));
        _clock.Advance(Seconds(10));
        await IdleAsync();
        Assert.Empty(_reports);
        Assert.Equal(1, Count("renewal"));
        Assert.Equal(2, Count("claims"));
        await StopAsync(host);
    }

    [Fact]
    public async Task UnextendedLeaseStopsAtLocalDeadline()
    {
        var execution = ExecutionTestProtocol.Execution(_clock);
        using var host = Host(execution, (operation, _, _) => Task.FromResult<HttpResponseMessage?>(operation == "renewal"
            ? ExecutionTestProtocol.Json(new { execution.LeaseId, execution.ExpiresAtUtc }) : null));
        await host.StartAsync(Token);
        for (var i = 0; i < 2; i++)
        {
            await _clock.WaitForTimersAsync(Token, Seconds(10), Seconds(10));
            _clock.Advance(Seconds(10));
        }
        await _clock.WaitForTimersAsync(Token, Seconds(5), Seconds(5), Seconds(10));
        _clock.Advance(Seconds(5));
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        Assert.Empty(_reports);
        Assert.Equal(2, Count("claims"));
        Assert.Equal(2, Count("renewal"));
        await StopAsync(host);
    }

    [Theory]
    [InlineData("claims")]
    [InlineData("renewal")]
    [InlineData("completion")]
    public async Task AmbiguousOperationStopsAgentWithoutRepeatingIt(string operation)
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock, operation == "completion" ? 0 : 40000), (current, _, _) =>
            current == operation ? throw new HttpRequestException("private body token") : Task.FromResult<HttpResponseMessage?>(null));
        await host.StartAsync(Token);
        if (operation == "renewal")
        {
            await _clock.WaitForTimersAsync(Token, Seconds(10), Seconds(10));
            _clock.Advance(Seconds(10));
        }
        await FailureAsync(host);
        Assert.Equal(1, Count(operation));
        Assert.Equal(1, Count("claims"));
        Assert.Equal(1, Count("registration"));
    }

    [Fact]
    public async Task SessionReplacementWhileExecutingStopsBothLoops()
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock), (operation, _, _) => Task.FromResult<HttpResponseMessage?>(
            operation == "heartbeat" && Count("heartbeat") == 2 ? ExecutionTestProtocol.Problem("worker_session_replaced") : null));
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(10), Seconds(10));
        _clock.Advance(Seconds(10));
        await FailureAsync(host);
        Assert.Empty(_reports);
        Assert.Equal(1, Count("registration"));
        Assert.Equal(1, Count("claims"));
    }

    [Theory]
    [InlineData("attempt_already_finalized", false)]
    [InlineData("completion_report_conflict", true)]
    public async Task CompletionConflictNeverOverwritesOrReplays(string code, bool fatal)
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock, durationMs: 0), (operation, _, _) =>
            Task.FromResult<HttpResponseMessage?>(operation == "completion" ? ExecutionTestProtocol.Problem(code) : null));
        await host.StartAsync(Token);
        if (fatal) await FailureAsync(host);
        else { await IdleAsync(); await StopAsync(host); }
        Assert.Equal(1, Count("completion"));
    }

    [Fact]
    public async Task ShutdownStopsActiveWorkAndReleasesAllOwnedResources()
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock));
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(40), Seconds(25), Seconds(10), Seconds(10));
        await StopAsync(host);
        Assert.Empty(_reports);
        Assert.Equal(1, Count("claims"));
        using var identity = WorkerIdentity.Open(_directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenReportDrainsOnShutdownWithinHttpDeadline(bool timeout)
    {
        var arrived = Signal();
        var release = Signal();
        var execution = ExecutionTestProtocol.Execution(_clock, durationMs: 0);
        CancellationToken requestToken = default;
        using var host = Host(execution, async (operation, request, token) =>
        {
            if (operation != "completion") return null;
            requestToken = token;
            var report = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return ExecutionTestProtocol.Completion(execution, report, _clock);
        });
        await host.StartAsync(Token);
        await WaitAsync(arrived.Task);
        var stopped = host.StopAsync(Token);
        Assert.False(requestToken.IsCancellationRequested);
        Assert.False(stopped.IsCompleted);
        if (timeout) _clock.Advance(Seconds(5));
        else release.TrySetResult();
        await WaitAsync(stopped);
        Assert.Equal(timeout ? 1 : 0, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(1, Count("completion"));
        Assert.Equal(1, Count("claims"));
        Assert.Equal(0, _clock.ActiveTimers);
    }

    private IHost Host(ClaimedExecution? execution,
        Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? intercept = null)
    {
        var handler = new WorkerHostTests.Handler(async (request, token) =>
        {
            var operation = request.RequestUri!.Segments[^1];
            _calls.Enqueue(operation);
            if (intercept is not null && await intercept(operation, request, token) is { } response) return response;
            switch (operation)
            {
                case "registration": return WorkerHostTests.Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token));
                case "heartbeat": return new(HttpStatusCode.NoContent);
                case "claims": return execution is not null && Count("claims") == 1 ? ExecutionTestProtocol.Claim(execution) : new(HttpStatusCode.NoContent);
                case "renewal": return ExecutionTestProtocol.Json(new { execution!.LeaseId, expiresAtUtc = _clock.GetUtcNow().AddSeconds(30).UtcDateTime });
                case "completion":
                    var report = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                    _reports.Enqueue(report);
                    return ExecutionTestProtocol.Completion(execution!, report, _clock);
                default: throw new InvalidOperationException("Unexpected operation.");
            }
        });
        var builder = WorkerHost.CreateBuilder(["--Worker:ApiBaseAddress", "http://localhost", "--Worker:StateDirectory", _directory]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(_ => new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan });
        return builder.Build();
    }

    private Task IdleAsync() => _clock.WaitForTimersAsync(Token, Seconds(10), Seconds(1));
    private int Count(string operation) => _calls.Count(value => value == operation);
    private static TimeSpan Seconds(int value) => TimeSpan.FromSeconds(value);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task WaitAsync(Task task) => task.WaitAsync(Seconds(10), Token);
    private async Task FailureAsync(IHost host)
    {
        var stopped = Signal();
        using var registration = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopped.TrySetResult());
        await WaitAsync(stopped.Task);
        await WaitAsync(host.StopAsync(Token));
        Assert.Equal(1, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
    }
    private async Task StopAsync(IHost host)
    {
        await WaitAsync(host.StopAsync(Token));
        Assert.Equal(0, host.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
