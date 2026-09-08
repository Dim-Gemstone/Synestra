using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synestra.Domain.Jobs;
using Synestra.Worker;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class WorkerExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRegistrationAfterCommittedResponseLossRetainsIdentityAndCannotDisplaceReplacement(bool replaced)
    {
        await using var api = Factory(_clock);
        using var client = api.CreateClient();
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        var bodies = new ConcurrentQueue<string>();
        routing.AfterResponse = async (request, response, token) =>
        {
            if (request.RequestUri!.Segments[^1] != "registration") return;
            bodies.Enqueue(await request.Content!.ReadAsStringAsync(token));
            if (routing.Count("registration") == 1)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                throw new HttpRequestException("Acknowledgement lost after commit.");
            }
        };
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
        Guid id;
        Guid session;
        DateTime registered;
        await using (var context = Context())
        {
            var worker = await context.Workers.SingleAsync(Token);
            id = worker.Id;
            session = worker.SessionId!.Value;
            registered = worker.RegisteredAtUtc;
        }
        var replacement = Guid.CreateVersion7();
        if (replaced)
        {
            using var response = await client.PutAsJsonAsync($"/api/worker/workers/{id}/registration",
                new { sessionId = replacement, name = "replacement", capacity = 1, supportedTypes = new[] { WorkerOptions.SupportedType } }, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        if (replaced) await StopFailedAgentAsync(agent);
        else { await IdleAsync(); await StopAsync(agent); }
        Assert.Equal(2, routing.Count("registration"));
        Assert.Equal(2, bodies.Count);
        Assert.Single(bodies.Distinct());
        await using var final = Context();
        var stored = await final.Workers.SingleAsync(Token);
        Assert.Equal(id, stored.Id);
        Assert.Equal(replaced ? replacement : session, stored.SessionId);
        Assert.Equal(registered, stored.RegisteredAtUtc);
        Assert.Equal(replaced ? 2 : 1, await final.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM worker_sessions").SingleAsync(Token));
        Assert.Empty(await final.JobAttempts.ToListAsync(Token));
        Assert.Equal(replaced ? 0 : 1, routing.Count("heartbeat"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCompletionReplaysCommittedSuccessOrFailureAcrossApiRestartAndExpiry(bool invalid)
    {
        await using var first = Factory(_clock);
        using var firstClient = first.CreateClient();
        var key = Guid.NewGuid().ToString();
        using var submission = Submission(0, invalid, key);
        using var accepted = await firstClient.SendAsync(submission, Token);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var submissionSnapshot = await accepted.Content.ReadAsStringAsync(Token);
        var original = JsonSerializer.Deserialize<JsonElement>(submissionSnapshot);
        Assert.Equal(7, original.EnumerateObject().Count());
        Assert.Equal("pending", original.GetProperty("status").GetString());
        var jobId = original.GetProperty("id").GetGuid();
        using var firstTransport = new HttpMessageInvoker(first.Server.CreateHandler());
        using var routing = new Routing(firstTransport);
        var responses = new ConcurrentQueue<string>();
        routing.AfterResponse = async (request, response, token) =>
        {
            if (request.RequestUri!.Segments[^1] != "completion") return;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            responses.Enqueue(await response.Content.ReadAsStringAsync(token));
            if (routing.Count("completion") == 1) throw new HttpRequestException("Acknowledgement lost after commit.");
        };
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
        Assert.Equal(1, routing.Count("claims"));
        var committed = await ExecutionRowsAsync();
        // The Client can already see a committed outcome while Worker still lacks its acknowledgement.
        var observed = await AssertClientJobAsync(firstClient, jobId, invalid ? "failed" : "succeeded");
        await first.DisposeAsync();
        await using var restarted = Factory(_clock);
        using var restartedTransport = new HttpMessageInvoker(restarted.Server.CreateHandler());
        routing.Target = restartedTransport;
        // Server UTC is now past expiry; the frozen report's monotonic retry budget is unchanged.
        _clock.ShiftUtc(TimeSpan.FromMinutes(1));
        using var client = restarted.CreateClient();
        Assert.True(JsonElement.DeepEquals(observed, await AssertClientJobAsync(client, jobId, invalid ? "failed" : "succeeded")));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1));
        Assert.Equal(committed, await ExecutionRowsAsync());
        Assert.Equal(2, responses.Count);
        Assert.Single(responses.Distinct());
        Assert.Equal(2, routing.Reports.Count);
        Assert.Single(routing.Reports.Select(report => report.GetRawText()).Distinct());
        Assert.Equal(1, routing.Count("registration"));
        Assert.Equal(2, routing.Count("claims"));
        Assert.Equal(0, routing.Count("renewal"));
        Assert.True(JsonElement.DeepEquals(observed, await AssertClientJobAsync(client, jobId, invalid ? "failed" : "succeeded")));
        using var repeatedSubmission = Submission(0, invalid, key);
        using var replay = await client.SendAsync(repeatedSubmission, Token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(accepted.Headers.Location, replay.Headers.Location);
        Assert.Equal(submissionSnapshot, await replay.Content.ReadAsStringAsync(Token));
        Assert.True(JsonElement.DeepEquals(observed, await AssertClientJobAsync(client, jobId, invalid ? "failed" : "succeeded")));
        Assert.Equal(committed, await ExecutionRowsAsync());
        await using (var context = Context())
        {
            var attempt = await context.JobAttempts.SingleAsync(Token);
            Assert.Equal(invalid ? JobAttemptStatus.Failed : JobAttemptStatus.Succeeded, attempt.Status);
            Assert.Equal(routing.Reports.First().GetProperty("reportId").GetGuid(), context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        }
        await StopAsync(agent);
    }

    [Fact]
    public async Task RecoveryRenewalReconfirmationKeepsOriginalHandlerRunningPastInitialDeadline()
    {
        await using var api = Factory(_clock);
        using var client = api.CreateClient();
        var jobId = await SubmitAsync(client, 26000);
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        routing.AfterResponse = (request, response, _) =>
        {
            if (request.RequestUri!.Segments[^1] == "renewal" && routing.Count("renewal") == 1)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                throw new HttpRequestException("Renewal committed but response lost.");
            }
            return Task.CompletedTask;
        };
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(26), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.Equal(1, routing.Count("claims"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9));
        _clock.Advance(TimeSpan.FromSeconds(9));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9));
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        Assert.Equal(3, routing.Count("renewal"));
        Assert.Equal(3, routing.Count("heartbeat"));
        await AssertCompletedAsync(jobId, false, routing, client);
        await StopAsync(agent);
    }

    [Fact]
    public async Task RecoveryRenewalUnknownCommitDoesNotExtendLocalBudgetAndFinalizerRecordsLoss()
    {
        await using var api = Factory(_clock);
        using var client = api.CreateClient();
        await SubmitAsync(client, 40000);
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        routing.BeforeSend = async (request, token) =>
        {
            if (request.RequestUri!.Segments[^1] == "renewal" && routing.Count("renewal") > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        routing.AfterResponse = async (request, response, token) =>
        {
            if (request.RequestUri!.Segments[^1] != "renewal") return;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            committed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await ExecutingAsync();
        _clock.Advance(TimeSpan.FromSeconds(10));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(4));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _clock.Advance(TimeSpan.FromSeconds(3));
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(25), _clock.GetElapsedTime(0));
        Assert.Equal(3, routing.Count("renewal"));
        Assert.Equal(0, routing.Count("completion"));
        Assert.Equal(2, routing.Count("claims"));
        await using (var context = Context())
        {
            var lease = await context.Leases.SingleAsync(Token);
            Assert.Equal(lease.AcquiredAtUtc.AddSeconds(40), lease.ExpiresAtUtc);
            Assert.Null(lease.ReleasedAtUtc);
            Assert.Equal(JobAttemptStatus.Running, (await context.JobAttempts.SingleAsync(Token)).Status);
        }
        await StopAsync(agent);
        await FinalizeStoppedExecutionAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RecoveryNeverRepeatsAmbiguousClaimOrAdoptsItsAttemptAfterAgentRestart()
    {
        await using var api = Factory(_clock);
        using var client = api.CreateClient();
        await SubmitAsync(client, 0);
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        routing.AfterResponse = (request, response, _) =>
        {
            if (request.RequestUri!.Segments[^1] == "claims" && routing.Count("claims") == 1)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                throw new HttpRequestException("Claim committed but token was lost.");
            }
            return Task.CompletedTask;
        };
        Guid session;
        using (var agent = Agent(routing))
        {
            await agent.StartAsync(Token);
            await StopFailedAgentAsync(agent);
            Assert.Equal(1, routing.Count("claims"));
            await using var context = Context();
            session = (await context.Workers.SingleAsync(Token)).SessionId!.Value;
            Assert.Equal(JobAttemptStatus.Running, (await context.JobAttempts.SingleAsync(Token)).Status);
        }
        using (var restarted = Agent(routing))
        {
            await restarted.StartAsync(Token);
            await IdleAsync();
            await using var context = Context();
            Assert.NotEqual(session, (await context.Workers.SingleAsync(Token)).SessionId);
            Assert.Equal(session, (await context.Leases.SingleAsync(Token)).SessionId);
            Assert.Equal(JobAttemptStatus.Running, (await context.JobAttempts.SingleAsync(Token)).Status);
            await StopAsync(restarted);
        }
        Assert.Equal(2, routing.Count("claims"));
        Assert.Equal(0, routing.Count("renewal"));
        Assert.Equal(0, routing.Count("completion"));
        await FinalizeStoppedExecutionAsync(TimeSpan.FromSeconds(30));
    }

    private async Task FinalizeStoppedExecutionAsync(TimeSpan elapsed)
    {
        await using var enabled = Factory(_clock, finalizerEnabled: true);
        using var client = enabled.CreateClient();
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5));
        Guid jobId;
        await using (var before = Context()) jobId = (await before.Jobs.SingleAsync(Token)).Id;
        await AssertClientJobAsync(client, jobId, "running");
        _clock.Advance(elapsed);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5));
        await using var context = Context();
        var attempt = await context.JobAttempts.SingleAsync(Token);
        Assert.Equal(JobAttemptStatus.Abandoned, attempt.Status);
        Assert.Equal("execution_lease_expired", attempt.ErrorCode);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Equal(JobStatus.Failed, (await context.Jobs.SingleAsync(Token)).Status);
        Assert.NotNull((await context.Leases.SingleAsync(Token)).ReleasedAtUtc);
        await AssertClientJobAsync(client, jobId, "abandoned");
    }
    private async Task StopFailedAgentAsync(IHost agent)
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = agent.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await agent.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, agent.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(0, _clock.ActiveTimers);
    }
    private async Task<string> ExecutionRowsAsync()
    {
        await using var context = Context();
        return await context.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object('job', j, 'attempt', a, 'lease', l)::text AS "Value"
            FROM jobs j JOIN job_attempts a ON a.job_id = j.id JOIN leases l ON l.job_attempt_id = a.id
            """).SingleAsync(Token);
    }
}
