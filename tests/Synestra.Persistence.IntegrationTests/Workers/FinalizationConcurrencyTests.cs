using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Synestra.Application.Executions;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Theory]
    [InlineData("workers")]
    [InlineData("jobs")]
    [InlineData("job_attempts")]
    [InlineData("leases")]
    public async Task Finalization_BusyRowSkipsInOrderAndReleasesEarlierLocksForScopeReuse(string table)
    {
        var execution = await AcquireAsync();
        var before = await ExecutionRowsAsync();
        await using var blocker = Context();
        await using var gate = await blocker.Database.BeginTransactionAsync(Token);
        var sql = table switch
        {
            "workers" => "SELECT * FROM workers FOR UPDATE",
            "jobs" => "SELECT * FROM jobs FOR UPDATE",
            "job_attempts" => "SELECT * FROM job_attempts FOR UPDATE",
            _ => "SELECT * FROM leases FOR UPDATE"
        };
        await blocker.Database.ExecuteSqlRawAsync(sql, Token);
        var commands = new FinalizationCommandGate();
        await using var provider = Provider(commands);
        await using var scope = provider.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>();
        Assert.Equal(FinalizeExpiredExecutionOutcome.Busy,
            await Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        string[] ordered = ["workers", "jobs", "job_attempts", "leases"];
        Assert.Equal(ordered.Take(Array.IndexOf(ordered, table) + 1), commands.LockedTables);
        Assert.All(commands.IsolationLevels, level => Assert.Equal(IsolationLevel.ReadCommitted, level));
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SynestraDbContext>().ChangeTracker.Entries());
        Assert.Equal(before, await ExecutionRowsAsync());
        if (table != "workers")
        {
            // The skipped finalizer must already have released its Worker lock.
            await using var probe = Context();
            await using var probeTransaction = await probe.Database.BeginTransactionAsync(Token);
            await probe.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE NOWAIT", Token);
        }
        await gate.CommitAsync(Token);
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized,
            await Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, Token));
        await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime);
    }

    [Fact]
    public async Task Finalization_TwoFinalizersCommitExactlyOneMutation()
    {
        var execution = await AcquireAsync();
        var gate = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(gate);
        var first = FinalizeAsync(execution, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await AssertRunningAsync(execution);
            Assert.Equal(FinalizeExpiredExecutionOutcome.Busy,
                await FinalizeAsync(execution, Now.AddSeconds(50)).WaitAsync(TimeSpan.FromSeconds(10), Token));
            gate.Gate.SetResult();
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await first);
            var committed = await ExecutionRowsAsync();
            Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution, Now.AddSeconds(50)));
            Assert.Equal(committed, await ExecutionRowsAsync());
            await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime);
        }
        finally { gate.Gate.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalization_CompletionRaceRespectsBothCommittedOrders(bool finalizerFirst)
    {
        var execution = await AcquireAsync();
        var report = Report();
        var gate = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(gate);
        var firstFinalization = finalizerFirst ? FinalizeAsync(execution, provider: provider) : null;
        var firstCompletion = finalizerFirst ? null : CompleteAsync(execution, report, Now.AddSeconds(35), provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (finalizerFirst)
            {
                var completion = CompleteAsync(execution, report, Now.AddSeconds(50));
                await WaitForBlockedAsync(1);
                gate.Gate.SetResult();
                Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await firstFinalization!);
                Assert.Equal(ExecutionOutcome.AttemptAlreadyFinalized, (await completion).Outcome);
                Assert.Equal(ExecutionOutcome.LeaseNotActive, (await RenewAsync(execution, Now.AddSeconds(50))).Outcome);
                await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime);
            }
            else
            {
                Assert.Equal(FinalizeExpiredExecutionOutcome.Busy, await FinalizeAsync(execution));
                gate.Gate.SetResult();
                var completed = await firstCompletion!;
                Assert.Equal(ExecutionOutcome.Succeeded, completed.Outcome);
                var before = await ExecutionRowsAsync();
                Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
                Assert.Equal(completed.Completion, (await CompleteAsync(execution, report, Now.AddDays(1))).Completion);
                Assert.Equal(before, await ExecutionRowsAsync());
            }
        }
        finally { gate.Gate.TrySetResult(); }
    }

    [Fact]
    public async Task Finalization_RenewalCommitMakesOldExpirationIneligible()
    {
        var execution = await AcquireAsync();
        var gate = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(gate);
        var renewal = RenewAsync(execution, Now.AddSeconds(20), 60, provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(FinalizeExpiredExecutionOutcome.Busy, await FinalizeAsync(execution));
            gate.Gate.SetResult();
            Assert.Equal(ExecutionOutcome.Succeeded, (await renewal).Outcome);
            var before = await ExecutionRowsAsync();
            Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await FinalizeAsync(execution));
            Assert.Equal(before, await ExecutionRowsAsync());
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution, Now.AddSeconds(80)));
            await AssertAbandonedAsync(execution with { Work = execution.Work with { ExpiresAtUtc = Now.AddSeconds(80).UtcDateTime } },
                Now.AddSeconds(80).UtcDateTime);
        }
        finally { gate.Gate.TrySetResult(); }
    }

    [Theory]
    [InlineData("renewal")]
    [InlineData("completion")]
    [InlineData("replacement")]
    public async Task Finalization_RevalidatesStateChangedAfterReadOnlyLocator(string change)
    {
        var execution = await AcquireAsync();
        var gate = new FinalizationCommandGate { PauseBefore = "workers" };
        await using var provider = Provider(gate);
        var finalization = FinalizeAsync(execution, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (change == "renewal") Assert.Equal(ExecutionOutcome.Succeeded, (await RenewAsync(execution, Now.AddSeconds(20), 60)).Outcome);
            if (change == "completion") Assert.Equal(ExecutionOutcome.Succeeded, (await CompleteAsync(execution, Report(), Now.AddSeconds(35))).Outcome);
            if (change == "replacement") await RegisterAsync(execution.Worker with { SessionId = Guid.CreateVersion7() });
            var before = await ExecutionRowsAsync();
            var workers = await WorkerRowsAsync();
            gate.Release.TrySetResult();
            Assert.Equal(change == "replacement" ? FinalizeExpiredExecutionOutcome.Finalized : FinalizeExpiredExecutionOutcome.NotEligible,
                await finalization);
            if (change != "replacement") Assert.Equal(before, await ExecutionRowsAsync());
            Assert.Equal(workers, await WorkerRowsAsync());
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("job")]
    [InlineData("attempt")]
    [InlineData("deleted-lease")]
    public async Task Finalization_RevalidatesEveryLocatorRelationshipAfterConcurrentChange(string relationship)
    {
        var execution = await AcquireAsync();
        var other = await AcquireAsync();
        var gate = new FinalizationCommandGate { PauseBefore = "workers" };
        await using var provider = Provider(gate);
        var finalization = FinalizeAsync(execution, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await using var context = Context();
            if (relationship == "worker")
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET worker_id = {other.Worker.WorkerId}, session_id = {other.Worker.SessionId} WHERE id = {execution.Work.LeaseId}", Token);
            if (relationship == "job")
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_attempts SET job_id = {other.Work.JobId}, number = 2 WHERE id = {execution.Work.AttemptId}", Token);
            if (relationship == "attempt")
            {
                var attemptId = Guid.CreateVersion7();
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO job_attempts (id, job_id, number, status, started_at_utc)
                    VALUES ({attemptId}, {other.Work.JobId}, 2, 'Running', {Now.UtcDateTime});
                    UPDATE leases SET job_attempt_id = {attemptId} WHERE id = {execution.Work.LeaseId}
                    """, Token);
            }
            if (relationship == "deleted-lease")
                await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM leases WHERE id = {execution.Work.LeaseId}", Token);
            var before = await ExecutionRowsAsync();
            gate.Release.TrySetResult();
            Assert.Equal(relationship == "deleted-lease" ? FinalizeExpiredExecutionOutcome.Missing : FinalizeExpiredExecutionOutcome.Inconsistent,
                await finalization);
            Assert.Equal(before, await ExecutionRowsAsync());
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Finalization_DifferentWorkersHaveNoGlobalSerialization()
    {
        var firstExecution = await AcquireAsync();
        var independent = await AcquireAsync();
        var gate = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(gate);
        var first = FinalizeAsync(firstExecution, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized,
                await FinalizeAsync(independent).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(first.IsCompleted);
            gate.Gate.SetResult();
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await first);
        }
        finally { gate.Gate.TrySetResult(); }
    }

    [Fact]
    public async Task Finalization_OldAttemptDoesNotAffectNewClaimOrReleaseCapacityTwice()
    {
        var execution = await AcquireAsync();
        await AddJobAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE workers SET last_seen_at_utc = {Now.AddSeconds(30).UtcDateTime}", Token);
        var next = await ClaimAsync(execution.Worker, Now.AddSeconds(30));
        Assert.Equal(ClaimWorkOutcome.Succeeded, next.Outcome);
        Assert.NotEqual(execution.Work.JobId, next.Work!.JobId);
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(execution));
        await AddJobAsync();
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(execution.Worker, Now.AddSeconds(40))).Outcome);
        Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(job => job.Id == next.Work.JobId, Token)).Status);
        var lease = await context.Leases.SingleAsync(lease => lease.Id == next.Work.LeaseId, Token);
        Assert.Null(lease.ReleasedAtUtc);
        Assert.Equal(next.Work.ExpiresAtUtc, lease.ExpiresAtUtc);
        Assert.Equal(2, await context.JobAttempts.CountAsync(Token));
        Assert.Equal(2, await context.Leases.CountAsync(Token));
        Assert.Equal(1, await context.Leases.CountAsync(lease => lease.ReleasedAtUtc == null && lease.ExpiresAtUtc > Now.AddSeconds(40).UtcDateTime, Token));
    }

    [Fact]
    public async Task Finalization_UsesActualClockAfterLastLock()
    {
        var execution = await AcquireAsync();
        var gate = new FinalizationCommandGate { PauseBefore = "leases" };
        var clock = new FinalizationClock(Now.AddSeconds(29));
        await using var provider = Provider(gate);
        await using var scope = provider.CreateAsyncScope();
        var useCase = new FinalizeExpiredExecution(scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>(),
            clock, NullLogger<FinalizeExpiredExecution>.Instance);
        var finalization = useCase.ExecuteAsync(execution.Work.LeaseId, Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(0, clock.Reads);
            clock.Now = Now.AddSeconds(45).AddTicks(9);
            gate.Release.TrySetResult();
            Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await finalization);
            await AssertAbandonedAsync(execution, Now.AddSeconds(45).UtcDateTime);
            Assert.Equal(["workers", "jobs", "job_attempts", "leases"], gate.LockedTables);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Finalization_ShutdownCancellationReleasesHeldLocksAndScopeCanRetry()
    {
        var execution = await AcquireAsync();
        var before = await ExecutionRowsAsync();
        var gate = new FinalizationCommandGate { PauseBefore = "leases" };
        await using var provider = Provider(gate);
        await using var scope = provider.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var finalization = Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, shutdown.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalization);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SynestraDbContext>().ChangeTracker.Entries());
        Assert.Equal(before, await ExecutionRowsAsync());
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await Finalizer(persistence).ExecuteAsync(execution.Work.LeaseId, Token));
    }

    private sealed class FinalizationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }

    private sealed class FinalizationCommandGate : DbCommandInterceptor
    {
        public string? PauseBefore { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> LockedTables { get; } = [];
        public List<IsolationLevel> IsolationLevels { get; } = [];
        private bool _paused;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FOR UPDATE SKIP LOCKED", StringComparison.Ordinal)) return result;
            var table = new[] { "workers", "jobs", "job_attempts", "leases" }
                .Single(table => command.CommandText.Contains("SELECT * FROM " + table + " WHERE", StringComparison.Ordinal));
            LockedTables.Add(table);
            IsolationLevels.Add(command.Transaction!.IsolationLevel);
            if (PauseBefore == table && !_paused)
            {
                _paused = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
