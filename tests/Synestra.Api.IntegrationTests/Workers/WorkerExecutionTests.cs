using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synestra.Api.IntegrationTests.Infrastructure;
using Synestra.Domain.Jobs;
using Synestra.Persistence;
using Synestra.Worker;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class WorkerExecutionTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private readonly ControlledTimeProvider _clock = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synestra-worker-execution-api-tests", Guid.NewGuid().ToString("N"));
    private PostgreSqlTestDatabase _database = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
        // Definition preparation belongs to the harness, never to the Worker.
        context.JobDefinitions.Add(new(WorkerOptions.SupportedType, "Bounded test sum", null, true, _clock.GetUtcNow().UtcDateTime));
        await context.SaveChangesAsync(Token);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(40000, false)]
    public async Task AgentExecutesThroughWorkerApiAndClientObservesCompletion(int duration, bool invalid)
    {
        await using var api = Factory(_clock);
        using var client = api.CreateClient();
        var jobId = await SubmitAsync(client, duration, invalid);
        await AssertClientJobAsync(client, jobId, "pending");
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        if (duration > 0)
        {
            for (var elapsed = 0; elapsed < 40; elapsed += 10)
            {
                await ExecutingAsync();
                Assert.Equal(1, routing.Count("claims"));
                await using var context = Context();
                Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(Token)).Status);
                await AssertClientJobAsync(client, jobId, "running");
                _clock.Advance(TimeSpan.FromSeconds(10));
            }
        }
        await IdleAsync();
        await AssertCompletedAsync(jobId, invalid, routing, client);
        if (duration > 0)
        {
            Assert.InRange(routing.Count("renewal"), 3, 4);
            Assert.Equal(5, routing.Count("heartbeat"));
        }
        await StopAsync(agent);
    }

    [Fact]
    public async Task ApiRestartBetweenRequestsPreservesLiveSessionLeaseAndExecution()
    {
        await using var first = Factory(_clock);
        using var firstClient = first.CreateClient();
        var jobId = await SubmitAsync(firstClient, 40000);
        using var firstTransport = new HttpMessageInvoker(first.Server.CreateHandler());
        using var routing = new Routing(firstTransport);
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await ExecutingAsync();
        _clock.Advance(TimeSpan.FromSeconds(10));
        await ExecutingAsync();
        Guid session;
        Guid leaseId;
        await using (var context = Context())
        {
            session = (await context.Workers.SingleAsync(Token)).SessionId!.Value;
            leaseId = (await context.Leases.SingleAsync(Token)).Id;
        }
        await first.DisposeAsync();
        await using var restarted = Factory(_clock);
        using var restartedTransport = new HttpMessageInvoker(restarted.Server.CreateHandler());
        routing.Target = restartedTransport;
        for (var elapsed = 10; elapsed < 40; elapsed += 10)
        {
            await ExecutingAsync();
            _clock.Advance(TimeSpan.FromSeconds(10));
        }
        await IdleAsync();
        using var client = restarted.CreateClient();
        await AssertCompletedAsync(jobId, false, routing, client);
        await using (var context = Context())
        {
            Assert.Equal(session, (await context.Workers.SingleAsync(Token)).SessionId);
            Assert.Equal(leaseId, (await context.Leases.SingleAsync(Token)).Id);
        }
        Assert.Equal(1, routing.Count("registration"));
        await StopAsync(agent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledApiFinalizerWinsBeforeRenewalOrFrozenCompletionWithoutOutcomeOverwrite(bool frozen)
    {
        var serverClock = new FinalizerTestTimeProvider(_clock.GetUtcNow());
        await using var api = Factory(serverClock, finalizerEnabled: true);
        using var client = api.CreateClient();
        await serverClock.NextDelayAsync(Token);
        var jobId = await SubmitAsync(client, frozen ? 0 : 40000);
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport) { HoldCompletion = frozen };
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        if (frozen) await routing.CompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        else await ExecutingAsync();
        await AssertClientJobAsync(client, jobId, "running");

        // Advance the authoritative server UTC independently, then observe a completed host pass.
        serverClock.Advance(TimeSpan.FromSeconds(31));
        await serverClock.NextDelayAsync(Token);
        var lost = await AssertClientJobAsync(client, jobId, "abandoned");
        if (frozen) routing.ReleaseCompletion.TrySetResult();
        else _clock.Advance(TimeSpan.FromSeconds(10));
        await IdleAsync();
        await using (var context = Context())
        {
            var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(Token);
            var attempt = Assert.Single(job.Attempts);
            Assert.Equal(jobId, job.Id);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Equal(JobAttemptStatus.Abandoned, attempt.Status);
            Assert.Equal("execution_lease_expired", attempt.ErrorCode);
            Assert.Null(attempt.Result);
            Assert.Equal(serverClock.GetUtcNow().UtcDateTime, attempt.Lease!.ReleasedAtUtc);
            Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
            Assert.Null(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
        }
        Assert.Equal(frozen ? 1 : 0, routing.Count("completion"));
        Assert.Equal(frozen ? 0 : 1, routing.Count("renewal"));
        Assert.Contains(routing.Responses, response => response.Operation == (frozen ? "completion" : "renewal") && response.Status == HttpStatusCode.Conflict);
        Assert.Equal(2, routing.Count("claims"));
        Assert.True(JsonElement.DeepEquals(lost, await AssertClientJobAsync(client, jobId, "abandoned")));
        await StopAsync(agent);
    }

    [Fact]
    public async Task ShutdownLeavesUnreportedWorkForEnabledHostFinalization()
    {
        var serverClock = new FinalizerTestTimeProvider(_clock.GetUtcNow());
        await using var api = Factory(serverClock, finalizerEnabled: true);
        using var client = api.CreateClient();
        await serverClock.NextDelayAsync(Token);
        var jobId = await SubmitAsync(client, 40000);
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await ExecutingAsync();
        await StopAsync(agent);
        await AssertClientJobAsync(client, jobId, "running");
        Assert.Equal(0, routing.Count("completion"));
        await using (var context = Context())
        {
            Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(Token)).Status);
            Assert.Null((await context.Leases.SingleAsync(Token)).ReleasedAtUtc);
        }
        serverClock.Advance(TimeSpan.FromSeconds(30));
        await serverClock.NextDelayAsync(Token);
        await using var final = Context();
        Assert.Equal(JobAttemptStatus.Abandoned, (await final.JobAttempts.SingleAsync(Token)).Status);
        Assert.Equal(JobStatus.Failed, (await final.Jobs.SingleAsync(Token)).Status);
        Assert.NotNull((await final.Leases.SingleAsync(Token)).ReleasedAtUtc);
        await AssertClientJobAsync(client, jobId, "abandoned");
    }

    private async Task AssertCompletedAsync(Guid jobId, bool invalid, Routing routing, HttpClient client)
    {
        Assert.Equal(1, routing.Count("completion"));
        Assert.Equal(2, routing.Count("claims"));
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        var report = Assert.Single(routing.Reports);
        Assert.Equal(jobId, job.Id);
        Assert.Equal(invalid ? JobStatus.Failed : JobStatus.Succeeded, job.Status);
        Assert.Equal(invalid ? JobAttemptStatus.Failed : JobAttemptStatus.Succeeded, attempt.Status);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, job.CompletedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.Lease!.ReleasedAtUtc);
        Assert.Equal(report.GetProperty("reportId").GetGuid(), context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.NotNull(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
        if (invalid)
        {
            Assert.Equal("invalid_workload_input", attempt.ErrorCode);
            Assert.Null(attempt.Result);
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>("""{"count":4,"sum":3}"""),
                JsonSerializer.Deserialize<JsonElement>(attempt.Result!)));
            Assert.Null(attempt.ErrorCode);
        }
        await AssertClientJobAsync(client, jobId, invalid ? "failed" : "succeeded");
    }

    private async Task<JsonElement> AssertClientJobAsync(HttpClient client, Guid jobId, string outcome)
    {
        using var response = await client.GetAsync($"/api/client/jobs/{jobId}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var observed = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(new[] { "availableAtUtc", "completedAtUtc", "completion", "createdAtUtc", "id", "maxAttempts", "priority", "status", "type" },
            observed.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(jobId, observed.GetProperty("id").GetGuid());
        Assert.Equal(outcome == "abandoned" ? "failed" : outcome, observed.GetProperty("status").GetString());
        var completion = observed.GetProperty("completion");
        if (outcome is "pending" or "running")
        {
            Assert.Equal(JsonValueKind.Null, observed.GetProperty("completedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, completion.ValueKind);
            return observed;
        }

        Assert.Equal(new[] { "attemptId", "attemptNumber", "error", "outcome", "result" },
            completion.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(outcome, completion.GetProperty("outcome").GetString());
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(job => job.Id == jobId, Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(attempt.Id, completion.GetProperty("attemptId").GetGuid());
        Assert.Equal(attempt.Number, completion.GetProperty("attemptNumber").GetInt32());
        Assert.EndsWith("Z", observed.GetProperty("completedAtUtc").GetString());
        Assert.Equal(job.CompletedAtUtc, observed.GetProperty("completedAtUtc").GetDateTime());
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.Lease!.ReleasedAtUtc);
        if (outcome == "succeeded")
        {
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>("""{"count":4,"sum":3}"""), completion.GetProperty("result")));
            Assert.Equal(JsonValueKind.Null, completion.GetProperty("error").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, completion.GetProperty("result").ValueKind);
            var error = completion.GetProperty("error");
            Assert.Equal(new[] { "code", "message" }, error.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(outcome == "abandoned" ? "execution_lease_expired" : "invalid_workload_input", error.GetProperty("code").GetString());
            Assert.Equal(outcome == "abandoned" ? "Execution lease expired before completion was recorded." : "The bounded workload input is invalid.",
                error.GetProperty("message").GetString());
        }
        return observed;
    }

    private async Task<Guid> SubmitAsync(HttpClient client, int duration, bool invalid = false)
    {
        using var request = Submission(duration, invalid, Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }
    private static HttpRequestMessage Submission(int duration, bool invalid, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/jobs")
        {
            Content = JsonContent.Create(new
            {
                type = WorkerOptions.SupportedType,
                payload = new { values = invalid ? Array.Empty<int>() : [1, 2, -1000000, 1000000], durationMs = duration }
            })
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
    private Task ExecutingAsync() => _clock.WaitForTimersAsync(Token,
        TimeSpan.FromSeconds(40) - _clock.GetElapsedTime(0), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    private Task IdleAsync() => _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
    private async Task StopAsync(IHost agent)
    {
        await agent.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, agent.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
    }
    private WebApplicationFactory<Program> Factory(TimeProvider clock, bool finalizerEnabled = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:synestra", _database.ConnectionString)
            .UseSetting("ExecutionFinalization:Enabled", finalizerEnabled.ToString())
            .ConfigureTestServices(services => services.AddSingleton(clock)));
    private IHost Agent(HttpMessageHandler transport)
    {
        var builder = WorkerHost.CreateBuilder(["--Worker:ApiBaseAddress", "http://localhost", "--Worker:StateDirectory", _directory]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(_ => new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan });
        return builder.Build();
    }
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);

    private sealed class Routing(HttpMessageInvoker target) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _calls = new();
        public HttpMessageInvoker Target { get; set; } = target;
        public bool HoldCompletion { get; init; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeSend { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? AfterResponse { get; set; }
        public TaskCompletionSource CompletionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<JsonElement> Reports { get; } = new();
        public ConcurrentQueue<(string Operation, HttpStatusCode Status)> Responses { get; } = new();
        public int Count(string operation) => _calls.Count(value => value == operation);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var operation = request.RequestUri!.Segments[^1];
            _calls.Enqueue(operation);
            Assert.StartsWith("/api/worker/", request.RequestUri.AbsolutePath);
            if (BeforeSend is not null) await BeforeSend(request, cancellationToken);
            if (operation == "completion")
            {
                Reports.Enqueue(await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken));
                CompletionEntered.TrySetResult();
                if (HoldCompletion) await ReleaseCompletion.Task.WaitAsync(cancellationToken);
            }
            var response = await Target.SendAsync(request, cancellationToken);
            Responses.Enqueue((operation, response.StatusCode));
            try { if (AfterResponse is not null) await AfterResponse(request, response, cancellationToken); }
            catch { response.Dispose(); throw; }
            return response;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
