using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Executions;
using Synestra.Application.Workers;
using Synestra.Persistence;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    [Fact]
    public async Task Finalization_WorkerTerminalErrorsAndPrecedenceSurviveRestart()
    {
        var execution = await AcquireAsync();
        var other = await AcquireAsync();
        var submissionIdentity = await SubmissionRowsAsync();
        _clock.Now = Now.AddSeconds(40);
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution));
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = Factory();
        _client = _factory.CreateClient();
        var report = Body(Guid.CreateVersion7());
        foreach (var operation in new[] { "renewal", "completion" })
        {
            await ProblemAsync(await SendAsync(execution, operation, report), 409,
                operation == "renewal" ? "lease_not_active" : "attempt_already_finalized");
            await ProblemAsync(await SendAsync(execution with { Secret = "invalid" }, operation, report), 400, "invalid_request");
            await ProblemAsync(await SendAsync(execution with { WorkerId = Guid.CreateVersion7(), LeaseId = Guid.CreateVersion7() }, operation, report), 404, "worker_not_found");
            await ProblemAsync(await SendAsync(execution with { SessionId = Guid.CreateVersion7(), LeaseId = Guid.CreateVersion7() }, operation, report), 409, "worker_session_replaced");
            await ProblemAsync(await SendAsync(execution with { LeaseId = Guid.CreateVersion7() }, operation, report), 404, "lease_not_found");
            await ProblemAsync(await SendAsync(execution with { Secret = LeaseToken.Generate() }, operation, report), 409, "lease_ownership_lost");
            await ProblemAsync(await SendAsync(execution with { WorkerId = other.WorkerId, SessionId = other.SessionId }, operation, report), 409, "lease_ownership_lost");
        }
        await ProblemAsync(await SendAsync(execution, "completion", "{}"), 400, "invalid_request");
        var view = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
        AssertClientCompletion(view, execution, "abandoned");
        Assert.Equal(Now.AddSeconds(40).UtcDateTime, view.GetProperty("completedAtUtc").GetDateTime());
        var completion = view.GetProperty("completion");
        Assert.Equal(JsonValueKind.Null, completion.GetProperty("result").ValueKind);
        AssertFields(completion.GetProperty("error"), "code", "message");
        Assert.Equal("execution_lease_expired", completion.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Execution lease expired before completion was recorded.", completion.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(submissionIdentity, await SubmissionRowsAsync());
        var replacement = Guid.CreateVersion7();
        await RegisterAsync(execution.WorkerId, replacement);
        foreach (var operation in new[] { "renewal", "completion" })
        {
            await ProblemAsync(await SendAsync(execution, operation, report), 409, "worker_session_replaced");
            await ProblemAsync(await SendAsync(execution with { SessionId = replacement }, operation, report), 409, "lease_ownership_lost");
        }
        await using var context = Context();
        var attempt = await context.JobAttempts.SingleAsync(attempt => attempt.Id == execution.AttemptId, Token);
        Assert.Equal(Now.AddSeconds(40).UtcDateTime, attempt.FinishedAtUtc);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Null(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Finalization_CommittedWorkerSnapshotRemainsReplayableAcrossHostRestart(string outcome)
    {
        var execution = await AcquireAsync();
        var reportId = Guid.CreateVersion7();
        _clock.Now = Now.AddSeconds(35);
        var first = await SendAsync(execution, "completion", Body(reportId, outcome));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var snapshot = await first.Content.ReadAsStringAsync(Token);
        _clock.Now = Now.AddSeconds(40);
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
        await using var restarted = Factory();
        using var client = restarted.CreateClient();
        var replay = await SendAsync(execution, "completion", Body(reportId, outcome), client);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(snapshot, await replay.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalization_ConcurrentWorkerCompletionObservesBothCommittedOrders(bool finalizerFirst)
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddSeconds(40);
        var gate = new FinalizationCommitGate();
        await using var gatedHost = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<SynestraDbContext>(options => options.AddInterceptors(gate))));
        using var gatedClient = gatedHost.CreateClient();
        var report = Body(Guid.CreateVersion7());
        var firstFinalization = finalizerFirst ? FinalizeAsync(execution, gatedHost) : null;
        var firstCompletion = finalizerFirst ? null : SendAsync(execution, "completion", report, gatedClient);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var running = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token)
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal("running", running.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, running.GetProperty("completedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, running.GetProperty("completion").ValueKind);
            if (finalizerFirst)
            {
                var completion = SendAsync(execution, "completion", report);
                await WaitForBlockedAsync(1);
                gate.Release.TrySetResult();
                Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await firstFinalization!);
                await ProblemAsync(await completion, 409, "attempt_already_finalized");
            }
            else
            {
                Assert.Equal(FinalizeExpiredExecutionOutcome.Busy, await FinalizeAsync(execution));
                gate.Release.TrySetResult();
                var response = await firstCompletion!;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
                var replay = await SendAsync(execution, "completion", report);
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal(await response.Content.ReadAsStringAsync(Token), await replay.Content.ReadAsStringAsync(Token));
            }
            var view = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
            AssertClientCompletion(view, execution, finalizerFirst ? "abandoned" : "succeeded");
            Assert.Equal(Now.AddSeconds(40).UtcDateTime, view.GetProperty("completedAtUtc").GetDateTime());
            var completionView = view.GetProperty("completion");
            if (finalizerFirst)
            {
                Assert.Equal(JsonValueKind.Null, completionView.GetProperty("result").ValueKind);
                AssertJsonEqual("""{"code":"execution_lease_expired","message":"Execution lease expired before completion was recorded."}""",
                    completionView.GetProperty("error"));
            }
            else
            {
                AssertJsonEqual(JsonSerializer.Deserialize<JsonElement>(report).GetProperty("result").GetRawText(), completionView.GetProperty("result"));
                Assert.Equal(JsonValueKind.Null, completionView.GetProperty("error").ValueKind);
            }
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Finalization_RechecksExpirationCommittedByWorkerRenewal()
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddSeconds(20);
        var gate = new FinalizationCommitGate();
        await using var gatedHost = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<SynestraDbContext>(options => options.AddInterceptors(gate))));
        using var gatedClient = gatedHost.CreateClient();
        var renewal = SendAsync(execution, "renewal", client: gatedClient);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _clock.Now = Now.AddSeconds(40);
            Assert.Equal(FinalizeExpiredExecutionOutcome.Busy, await FinalizeAsync(execution));
            gate.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await renewal).StatusCode);
            Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
            _clock.Now = Now.AddSeconds(50);
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution));
            await ProblemAsync(await SendAsync(execution, "renewal"), 409, "lease_not_active");
        }
        finally { gate.Release.TrySetResult(); }
    }

    private async Task<FinalizeExpiredExecutionOutcome> FinalizeAsync(Acquired execution, WebApplicationFactory<Program>? factory = null)
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FinalizeExpiredExecution>().ExecuteAsync(execution.LeaseId, Token);
    }

    private async Task<string> SubmissionRowsAsync()
    {
        await using var context = Context();
        return await context.Database.SqlQueryRaw<string>("""
            SELECT jsonb_agg(to_jsonb(s) || jsonb_build_object('xmin', s.xmin::text) ORDER BY s.key)::text AS "Value"
            FROM job_submissions s
            """).SingleAsync(Token);
    }

    private sealed class FinalizationCommitGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
