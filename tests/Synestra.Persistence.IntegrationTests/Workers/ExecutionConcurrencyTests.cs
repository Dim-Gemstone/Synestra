using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Theory]
    [InlineData("identical")]
    [InlineData("conflicting-result")]
    [InlineData("success-failure")]
    [InlineData("different-report")]
    public async Task ConcurrentReports_CommitExactlyOneTerminalSnapshot(string race)
    {
        var execution = await AcquireAsync();
        var firstReport = Report();
        var secondReport = race switch
        {
            "conflicting-result" => firstReport with { Result = "{}" },
            "success-failure" => Report("failed") with { ReportId = firstReport.ReportId },
            "different-report" => firstReport with { ReportId = Guid.CreateVersion7() },
            _ => firstReport with { Result = "{\"b\":[true,null],\"a\":1e0}" }
        };
        await using var gateContext = Context();
        await using var gate = await gateContext.Database.BeginTransactionAsync(Token);
        await gateContext.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {execution.Worker.WorkerId} FOR UPDATE", Token);
        var first = CompleteAsync(execution, firstReport, Now.AddSeconds(10));
        var second = CompleteAsync(execution, secondReport, Now.AddSeconds(20));
        await WaitForBlockedAsync(2);
        await gate.CommitAsync(Token);
        var results = await Task.WhenAll(first, second);
        if (race == "identical")
        {
            Assert.All(results, result => Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome));
            Assert.Equal(results[0].Completion, results[1].Completion);
        }
        else
        {
            Assert.Single(results, result => result.Outcome == ExecutionOutcome.Succeeded);
            Assert.Single(results, result => result.Outcome == (race == "different-report"
                ? ExecutionOutcome.AttemptAlreadyFinalized : ExecutionOutcome.CompletionReportConflict));
        }
        var winner = results.First(result => result.Outcome == ExecutionOutcome.Succeeded).Completion!;
        await using var reader = Context();
        var job = await reader.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(winner.FinishedAtUtc, job.CompletedAtUtc);
        Assert.Equal(winner.FinishedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(winner.FinishedAtUtc, attempt.Lease!.ReleasedAtUtc);
        Assert.Equal(winner.Outcome == "succeeded" ? JobStatus.Succeeded : JobStatus.Failed, job.Status);
    }

    [Fact]
    public async Task ConcurrentRenewals_CannotLoseLongerExtension()
    {
        var execution = await AcquireAsync();
        await using var gateContext = Context();
        await using var gate = await gateContext.Database.BeginTransactionAsync(Token);
        await gateContext.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {execution.Worker.WorkerId} FOR UPDATE", Token);
        var first = RenewAsync(execution, Now.AddSeconds(20), 50);
        var second = RenewAsync(execution, Now.AddSeconds(10), 30);
        await WaitForBlockedAsync(2);
        await gate.CommitAsync(Token);
        Assert.All(await Task.WhenAll(first, second), result => Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome));
        await using var reader = Context();
        Assert.Equal(Now.AddSeconds(70).UtcDateTime, (await reader.Leases.SingleAsync(Token)).ExpiresAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenewalCompletionRace_ObservesFirstCommit(bool renewFirst)
    {
        var execution = await AcquireAsync();
        var interceptor = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(interceptor);
        var firstRenewal = renewFirst ? RenewAsync(execution, Now.AddSeconds(10), provider: provider) : null;
        var firstCompletion = renewFirst ? null : CompleteAsync(execution, Report(), Now.AddSeconds(10), provider);
        try
        {
            await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var secondRenewal = renewFirst ? null : RenewAsync(execution, Now.AddSeconds(20));
            var secondCompletion = renewFirst ? CompleteAsync(execution, Report(), Now.AddSeconds(20)) : null;
            await WaitForBlockedAsync(1);
            interceptor.Gate.SetResult();
            if (renewFirst)
            {
                Assert.Equal(ExecutionOutcome.Succeeded, (await firstRenewal!).Outcome);
                Assert.Equal(ExecutionOutcome.Succeeded, (await secondCompletion!).Outcome);
            }
            else
            {
                Assert.Equal(ExecutionOutcome.Succeeded, (await firstCompletion!).Outcome);
                Assert.Equal(ExecutionOutcome.LeaseNotActive, (await secondRenewal!).Outcome);
            }
        }
        finally { interceptor.Gate.TrySetResult(); }
        await using var reader = Context();
        var lease = await reader.Leases.SingleAsync(Token);
        Assert.Equal(Now.AddSeconds(renewFirst ? 40 : 30).UtcDateTime, lease.ExpiresAtUtc);
        Assert.Equal(Now.AddSeconds(renewFirst ? 20 : 10).UtcDateTime, lease.ReleasedAtUtc);
        Assert.Equal(JobStatus.Succeeded, (await reader.Jobs.SingleAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionReplacementCommittedFirst_FencesWaitingOperation(bool completion)
    {
        var execution = await AcquireAsync();
        var replacement = execution.Worker with { SessionId = Guid.CreateVersion7() };
        await using var scope = _provider.CreateAsyncScope();
        await using var gate = await scope.ServiceProvider.GetRequiredService<IWorkerPersistence>().BeginTransactionAsync(Token);
        var worker = (await gate.LockRegistrationAsync(execution.Worker.WorkerId, Token))!;
        gate.AddSession(worker.Id, replacement.SessionId);
        worker.UpdateRegistration(new(worker.Id, replacement.SessionId, replacement.Name, replacement.Capacity, replacement.SupportedTypes), Now.UtcDateTime);
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var renewal = completion ? null : RenewAsync(execution);
        var report = completion ? CompleteAsync(execution, Report()) : null;
        await WaitForBlockedAsync(1);
        await gate.CommitAsync(Token);
        Assert.Equal(ExecutionOutcome.SessionReplaced, completion ? (await report!).Outcome : (await renewal!).Outcome);
        await AssertRunningAsync(execution);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationCommittedFirst_PreservesResultBeforeSessionReplacement(bool completion)
    {
        var execution = await AcquireAsync();
        var interceptor = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(interceptor);
        var renewal = completion ? null : RenewAsync(execution, Now.AddSeconds(10), provider: provider);
        var report = completion ? CompleteAsync(execution, Report(), Now.AddSeconds(10), provider) : null;
        try
        {
            await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var replacement = RegisterAsync(execution.Worker with { SessionId = Guid.CreateVersion7() });
            await WaitForBlockedAsync(1);
            interceptor.Gate.SetResult();
            Assert.Equal(ExecutionOutcome.Succeeded, completion ? (await report!).Outcome : (await renewal!).Outcome);
            await replacement;
        }
        finally { interceptor.Gate.TrySetResult(); }
        await using var reader = Context();
        var lease = await reader.Leases.SingleAsync(Token);
        Assert.Equal(execution.Worker.SessionId, lease.SessionId);
        Assert.NotEqual(execution.Worker.SessionId, (await reader.Workers.SingleAsync(Token)).SessionId);
        Assert.Equal(completion ? Now.AddSeconds(10).UtcDateTime : (DateTime?)null, lease.ReleasedAtUtc);
        Assert.Equal(Now.AddSeconds(completion ? 30 : 40).UtcDateTime, lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task CompletionAndClaim_CapacityChangesOnlyAtCommit()
    {
        var execution = await AcquireAsync();
        await AddJobAsync();
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(execution.Worker)).Outcome);
        var interceptor = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(interceptor);
        var completion = CompleteAsync(execution, Report(), provider: provider);
        try
        {
            await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await using var reader = Context();
            Assert.Equal(1, await reader.Leases.CountAsync(lease => lease.ReleasedAtUtc == null && lease.ExpiresAtUtc > Now.UtcDateTime, Token));
            Assert.Equal(JobStatus.Running, (await reader.Jobs.SingleAsync(job => job.Id == execution.Work.JobId, Token)).Status);
            var claim = ClaimAsync(execution.Worker);
            await WaitForBlockedAsync(1);
            Assert.False(claim.IsCompleted);
            interceptor.Gate.SetResult();
            Assert.Equal(ExecutionOutcome.Succeeded, (await completion).Outcome);
            var next = await claim;
            Assert.Equal(ClaimWorkOutcome.Succeeded, next.Outcome);
            Assert.NotEqual(execution.Work.JobId, next.Work!.JobId);
            Assert.Equal(1, await reader.Leases.CountAsync(lease => lease.ReleasedAtUtc == null, Token));
        }
        finally { interceptor.Gate.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentWorkers_ProceedWithoutGlobalSerialization(bool completion)
    {
        var blocked = await AcquireAsync();
        var independent = await AcquireAsync();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {blocked.Worker.WorkerId} FOR UPDATE", Token);
        var renewal = completion ? null : RenewAsync(blocked);
        var report = completion ? CompleteAsync(blocked, Report()) : null;
        await WaitForBlockedAsync(1);
        var result = completion
            ? (await CompleteAsync(independent, Report()).WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome
            : (await RenewAsync(independent).WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome;
        Assert.Equal(ExecutionOutcome.Succeeded, result);
        await gate.CommitAsync(Token);
        Assert.Equal(ExecutionOutcome.Succeeded, completion ? (await report!).Outcome : (await renewal!).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtCommit_RollsBackAndLeavesScopeReusable(bool completion)
    {
        var execution = await AcquireAsync();
        var interceptor = new CommitInterceptor { Failure = new OperationCanceledException(Token) };
        await using var provider = Provider(interceptor);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (completion)
                await new ReportExecutionCompletion(scope.ServiceProvider.GetRequiredService<IReportExecutionCompletionPersistence>(), new Clock(Now))
                    .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, Report(), Token);
            else
                await new RenewLease(scope.ServiceProvider.GetRequiredService<IRenewLeasePersistence>(), new Clock(Now.AddSeconds(10)), new())
                    .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, Token);
        });
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertRunningAsync(execution);
        interceptor.Failure = null;
        Assert.Equal(ExecutionOutcome.Succeeded, (await new RenewLease(scope.ServiceProvider.GetRequiredService<IRenewLeasePersistence>(), new Clock(Now.AddSeconds(10)), new())
            .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, Token)).Outcome);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private async Task WaitForBlockedAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var context = Context();
        while (await context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
            """).SingleAsync(timeout.Token) < count)
            await Task.Delay(20, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocatorRelationships_AreRevalidatedAfterWaitingForJobLock(bool completion)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM jobs WHERE id = {execution.Work.JobId} FOR UPDATE", Token);
        var renewal = completion ? null : RenewAsync(execution);
        var report = completion ? CompleteAsync(execution, Report()) : null;
        await WaitForBlockedAsync(1);
        var newAttempt = Guid.CreateVersion7();
        // Simulate a direct SQL relationship change while the read-only locator is stale.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_attempts (id, job_id, number, status, started_at_utc)
            VALUES ({newAttempt}, {execution.Work.JobId}, 2, 'Running', {Now.UtcDateTime});
            UPDATE leases SET job_attempt_id = {newAttempt} WHERE id = {execution.Work.LeaseId}
            """, Token);
        await gate.CommitAsync(Token);
        Assert.Equal(ExecutionOutcome.OwnershipLost, completion ? (await report!).Outcome : (await renewal!).Outcome);
        await using var reader = Context();
        Assert.All(await reader.JobAttempts.ToListAsync(Token), attempt => Assert.Equal(JobAttemptStatus.Running, attempt.Status));
        Assert.Null((await reader.Leases.SingleAsync(Token)).ReleasedAtUtc);
    }
}
