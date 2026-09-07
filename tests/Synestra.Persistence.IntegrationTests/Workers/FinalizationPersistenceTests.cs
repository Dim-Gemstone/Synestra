using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Synestra.Application.Executions;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Fact]
    public async Task Finalization_UnknownLeaseDoesNotRequireWorkerProtocolIdentifiers()
    {
        await using var scope = _provider.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>();
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid(), Guid.CreateVersion7() })
            Assert.Equal(FinalizeExpiredExecutionOutcome.Missing, await Finalizer(persistence).ExecuteAsync(id, Token));
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SynestraDbContext>().ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10000000)]
    public async Task Finalization_ExpirationBoundaryAndDecisionTimePersistAcrossScopes(long microseconds)
    {
        var execution = await AcquireAsync();
        var before = await ExecutionRowsAsync();
        var decision = Now.AddSeconds(30).AddTicks(microseconds * 10);
        var result = await FinalizeAsync(execution, decision.AddTicks(9));
        if (microseconds < 0)
        {
            Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, result);
            Assert.Equal(before, await ExecutionRowsAsync());
            await AssertRunningAsync(execution);
        }
        else
        {
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, result);
            await AssertAbandonedAsync(execution, decision.UtcDateTime);
            var terminal = await ExecutionRowsAsync();
            await using var restarted = Provider();
            Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution, Now.AddDays(1), restarted));
            Assert.Equal(terminal, await ExecutionRowsAsync());
        }
    }

    [Theory]
    [InlineData("current-online")]
    [InlineData("current-offline")]
    [InlineData("replaced")]
    [InlineData("legacy-worker")]
    [InlineData("legacy-lease")]
    [InlineData("tokenless")]
    public async Task Finalization_IsIndependentOfLivenessSessionsAndTokens(string state)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        if (state == "current-online")
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE workers SET last_seen_at_utc = {Now.AddSeconds(25).UtcDateTime}", Token);
        if (state == "replaced") await RegisterAsync(execution.Worker with { SessionId = Guid.CreateVersion7() });
        if (state == "legacy-worker")
            await context.Database.ExecuteSqlRawAsync("UPDATE workers SET session_id = NULL, session_started_at_utc = NULL", Token);
        if (state == "legacy-lease")
            await context.Database.ExecuteSqlRawAsync("UPDATE leases SET session_id = NULL, token_hash = NULL", Token);
        if (state == "tokenless") await context.Database.ExecuteSqlRawAsync("UPDATE leases SET token_hash = NULL", Token);
        var workerBefore = await WorkerRowsAsync();
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution, Now.AddSeconds(29)));
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution));
        Assert.Equal(workerBefore, await WorkerRowsAsync());
        await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime, preserveHash: state is not ("legacy-lease" or "tokenless"));
    }

    [Theory]
    [InlineData("UPDATE leases SET released_at_utc = acquired_at_utc")]
    [InlineData("UPDATE jobs SET status = 'Failed'")]
    [InlineData("UPDATE jobs SET completed_at_utc = created_at_utc")]
    [InlineData("UPDATE job_attempts SET status = 'Abandoned'")]
    [InlineData("UPDATE job_attempts SET finished_at_utc = started_at_utc")]
    [InlineData("UPDATE job_attempts SET result = jsonb_build_object()")]
    [InlineData("UPDATE job_attempts SET error_code = 'existing'")]
    [InlineData("UPDATE job_attempts SET error_message = 'existing'")]
    public async Task Finalization_ReleasedTerminalAndInconsistentOutcomesRemainUntouched(string sql)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync(sql, Token);
        var before = await ExecutionRowsAsync();
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Theory]
    [InlineData("UPDATE leases SET expires_at_utc = acquired_at_utc")]
    [InlineData("UPDATE leases SET acquired_at_utc = expires_at_utc + interval '1 minute'")]
    [InlineData("UPDATE job_attempts SET started_at_utc = started_at_utc + interval '1 day'")]
    [InlineData("UPDATE jobs SET created_at_utc = created_at_utc + interval '1 second'")]
    public async Task Finalization_InconsistentLegacyChronologyIsSkippedWithoutRepair(string sql)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync(sql, Token);
        var before = await ExecutionRowsAsync();
        Assert.Equal(FinalizeExpiredExecutionOutcome.Inconsistent, await FinalizeAsync(execution));
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("job")]
    [InlineData("attempt")]
    [InlineData("lease")]
    public async Task Finalization_MissingOrOrphanRowsNeverTriggerRepair(string missing)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        // Deliberately simulate corrupted legacy storage, bypassing FKs only in this isolated test database.
        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            await context.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica", Token);
            await context.Database.ExecuteSqlRawAsync(missing switch
            {
                "worker" => "DELETE FROM workers",
                "job" => "DELETE FROM jobs",
                "attempt" => "DELETE FROM job_attempts",
                _ => "DELETE FROM leases"
            }, Token);
            await transaction.CommitAsync(Token);
        }
        var before = await ExecutionRowsAsync();
        Assert.Equal(missing == "lease" ? FinalizeExpiredExecutionOutcome.Missing : FinalizeExpiredExecutionOutcome.Inconsistent,
            await FinalizeAsync(execution));
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Theory]
    [InlineData("report")]
    [InlineData("snapshot")]
    public async Task Finalization_IndependentlyDetectsReportAndSnapshotWithoutDeserializing(string field)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        // These checks normally prevent the inconsistent shapes. Loss finalization must still skip them.
        await context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE job_attempts DROP CONSTRAINT "CK_job_attempts_completion_snapshot";
            ALTER TABLE job_attempts DROP CONSTRAINT "CK_job_attempts_reported_completion"
            """, Token);
        if (field == "report")
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_attempts SET completion_report_id = {Guid.CreateVersion7()}", Token);
        else await context.Database.ExecuteSqlRawAsync("UPDATE job_attempts SET completion_snapshot = jsonb_build_object('unrecognized', true)", Token);
        var before = await ExecutionRowsAsync();
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Finalization_PreservesCommittedCompletionAndDurableReplay(string outcome)
    {
        var execution = await AcquireAsync();
        var report = Report(outcome);
        var completion = await CompleteAsync(execution, report, Now.AddSeconds(35));
        var before = await ExecutionRowsAsync();
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
        await using var restarted = Provider();
        Assert.Equal(completion.Completion, (await CompleteAsync(execution, report, Now.AddDays(1), restarted)).Completion);
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Fact]
    public async Task Finalization_LegacyMigrationKeepsRowsEligibleAndModelUnchanged()
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync("20260905180053_AddJobSubmissionIdempotency", Token);
        await context.Database.MigrateAsync(Token);
        Assert.Null(await context.Leases.Select(lease => lease.SessionId).SingleAsync(Token));
        Assert.Null(await context.Leases.Select(lease => EF.Property<byte[]?>(lease, "TokenHash")).SingleAsync(Token));
        Assert.Null(await context.Workers.Select(worker => worker.SessionId).SingleAsync(Token));
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution));
        await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime, preserveHash: false);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("save")]
    [InlineData("commit")]
    [InlineData("cancel")]
    public async Task Finalization_RollbackAndFailedSaveOrCommitLeaveScopeReusable(string failure)
    {
        var execution = await AcquireAsync();
        var before = await ExecutionRowsAsync();
        var interceptor = new CommitInterceptor
        {
            Failure = failure switch
            {
                "commit" => new InvalidOperationException("Commit failure"),
                "cancel" => new OperationCanceledException(Token),
                _ => null
            }
        };
        await using var provider = Provider(interceptor);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var persistence = scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>();
        if (failure is "rollback" or "save")
        {
            await using var transaction = await persistence.BeginTransactionAsync(Token);
            var locked = (await transaction.TryLockExecutionAsync(execution.Work.LeaseId, Token)).Execution!;
            locked.Job.AbandonAttempt(locked.Attempt, Now.AddSeconds(40).UtcDateTime);
            locked.Lease.Release(Now.AddSeconds(40).UtcDateTime);
            if (failure == "save")
            {
                context.Entry(locked.Lease).Property<byte[]>("TokenHash").CurrentValue = new byte[31];
                await Assert.ThrowsAsync<DbUpdateException>(() => transaction.CommitAsync(Token));
            }
            else
            {
                await context.SaveChangesAsync(Token);
                Assert.Equal(before, await ExecutionRowsAsync());
            }
        }
        else
        {
            var actual = await Record.ExceptionAsync(() => Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, Token));
            Assert.Same(interceptor.Failure, actual);
        }
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(before, await ExecutionRowsAsync());
        interceptor.Failure = null;
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized,
            await Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, Token));
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime);
    }

    private async Task<FinalizeExpiredExecutionOutcome> FinalizeAsync(Acquired execution, DateTimeOffset? now = null, ServiceProvider? provider = null)
    {
        await using var scope = (provider ?? _provider).CreateAsyncScope();
        return await Finalizer(scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>(), now)
            .ExecuteAsync(execution.Work.LeaseId, Token);
    }
    private static FinalizeExpiredExecution Finalizer(IFinalizeExpiredExecutionPersistence persistence, DateTimeOffset? now = null) =>
        new(persistence, new Clock(now ?? Now.AddSeconds(40)), NullLogger<FinalizeExpiredExecution>.Instance);

    private async Task AssertAbandonedAsync(Acquired execution, DateTime decision, bool preserveHash = true)
    {
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease)
            .SingleAsync(job => job.Id == execution.Work.JobId, Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobAttemptStatus.Abandoned, attempt.Status);
        Assert.Equal(execution.Work.AttemptId, attempt.Id);
        Assert.Equal(decision, job.CompletedAtUtc);
        Assert.Equal(decision, attempt.FinishedAtUtc);
        Assert.Equal(decision, attempt.Lease!.ReleasedAtUtc);
        Assert.Equal(execution.Work.ExpiresAtUtc, attempt.Lease.ExpiresAtUtc);
        Assert.Equal(execution.Work.AcquiredAtUtc, attempt.Lease.AcquiredAtUtc);
        Assert.Equal(execution.Work.AcquiredAtUtc, attempt.StartedAtUtc);
        Assert.Null(attempt.Result);
        Assert.Equal("execution_lease_expired", attempt.ErrorCode);
        Assert.Equal("Execution lease expired before completion was recorded.", attempt.ErrorMessage);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Null(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
        Assert.Equal(preserveHash ? LeaseToken.Hash(execution.Work.LeaseToken) : null,
            context.Entry(attempt.Lease).Property<byte[]?>("TokenHash").CurrentValue);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(Now.UtcDateTime, job.AvailableAtUtc);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    // Includes xmin so repeat/skip assertions detect writes even when values happen to be identical.
    private async Task<string> ExecutionRowsAsync()
    {
        await using var context = Context();
        return await context.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'jobs', (SELECT jsonb_agg(to_jsonb(j) || jsonb_build_object('xmin', j.xmin::text) ORDER BY j.id) FROM jobs j),
              'attempts', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a.id) FROM job_attempts a),
              'leases', (SELECT jsonb_agg(to_jsonb(l) || jsonb_build_object('xmin', l.xmin::text) ORDER BY l.id) FROM leases l)
            )::text AS "Value"
            """).SingleAsync(Token);
    }
    private async Task<string> WorkerRowsAsync()
    {
        await using var context = Context();
        return await context.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'workers', (SELECT jsonb_agg(to_jsonb(w) || jsonb_build_object('xmin', w.xmin::text) ORDER BY w.id) FROM workers w),
              'sessions', (SELECT jsonb_agg(to_jsonb(s) ORDER BY s.worker_id, s.session_id) FROM worker_sessions s),
              'types', (SELECT jsonb_agg(to_jsonb(t) ORDER BY t.worker_id, t.type) FROM worker_supported_types t)
            )::text AS "Value"
            """).SingleAsync(Token);
    }
}
