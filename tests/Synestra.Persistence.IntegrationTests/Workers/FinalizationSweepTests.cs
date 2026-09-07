using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Synestra.Application.Executions;
using Synestra.Application.Extensions;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Extensions;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Fact]
    public async Task Sweep_RegisteredUseCaseDrainsBacklogAcrossFreshScopesAndResetsAtEnd()
    {
        await AcquireManyAsync(7);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication().AddPersistence(_database.ConnectionString);
        services.AddSingleton<TimeProvider>(new Clock(Now.AddSeconds(40)));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        ExpiredExecutionSweepCursor? cursor = null;
        foreach (var expected in new[] { 3, 3, 1 })
        {
            await using var scope = provider.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<FinalizeExpiredExecutionSweep>().ExecuteAsync(3, cursor, Token);
            Assert.Equal(expected, result.Inspected);
            Assert.Equal(expected, result.Finalized);
            Assert.Equal(0, result.Skipped + result.Failed);
            Assert.Equal(expected == 3, result.NextCursor is not null);
            cursor = result.NextCursor;
            Assert.Empty(scope.ServiceProvider.GetRequiredService<SynestraDbContext>().ChangeTracker.Entries());
        }
        await using var context = Context();
        Assert.Equal(7, await context.JobAttempts.CountAsync(attempt => attempt.Status == JobAttemptStatus.Abandoned, Token));
        Assert.Equal(new(0, 0, 0, 0, null), await SweepAsync(3, cursor));
    }

    [Fact]
    public async Task Sweep_ExactBatchBoundaryReturnsCursorThenEmptyPassResetsIt()
    {
        await AcquireManyAsync(2);
        var result = await SweepAsync(2);
        Assert.Equal(2, result.Finalized);
        Assert.NotNull(result.NextCursor);
        Assert.Equal(new(0, 0, 0, 0, null), await SweepAsync(2, result.NextCursor));
    }

    [Fact]
    public async Task Sweep_BusyFirstCandidateDoesNotHideBacklogAndIsRevisitedAfterWrap()
    {
        var executions = await AcquireManyAsync(5);
        var ordered = await DiscoverAsync();
        var busy = executions.Single(item => item.Work.LeaseId == ordered[0].LeaseId);
        await using var blocker = Context();
        await using var gate = await blocker.Database.BeginTransactionAsync(Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {busy.Worker.WorkerId} FOR UPDATE", Token);
        var first = await SweepAsync(2).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(new(2, 1, 1, 0, SweepCursor(ordered[1])), first);
        var second = await SweepAsync(2, first.NextCursor);
        Assert.Equal(new(2, 2, 0, 0, SweepCursor(ordered[3])), second);
        var third = await SweepAsync(2, second.NextCursor);
        Assert.Equal(new(1, 1, 0, 0, null), third);
        await AssertRunningAsync(busy);
        await gate.CommitAsync(Token);
        Assert.Equal(new(1, 1, 0, 0, null), await SweepAsync(2, third.NextCursor));
        await AssertAbandonedAsync(busy, Now.AddSeconds(40).UtcDateTime);
    }

    [Fact]
    public async Task Sweep_NewlyExpiredTailCannotPostponeRevisitingBusyCandidate()
    {
        var executions = await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        var busy = executions.Single(item => item.Work.LeaseId == ordered[0].LeaseId);
        await using var blocker = Context();
        await using var gate = await blocker.Database.BeginTransactionAsync(Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {busy.Worker.WorkerId} FOR UPDATE", Token);
        var first = await SweepAsync(2);
        Assert.Equal(new(2, 1, 1, 0, SweepCursor(ordered[1])), first);
        var newTail = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.AddSeconds(41).UtcDateTime} WHERE id = {newTail.Work.LeaseId}", Token);
        var clock = new Clock(Now.AddSeconds(100));
        Assert.Equal(new(1, 1, 0, 0, null), await SweepAsync(2, first.NextCursor, clock: clock));
        Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(job => job.Id == newTail.Work.JobId, Token)).Status);
        await gate.CommitAsync(Token);
        var nextCycle = await SweepAsync(2, clock: clock);
        Assert.Equal(2, nextCycle.Finalized);
        Assert.Equal(Now.AddSeconds(100).UtcDateTime, nextCycle.NextCursor!.CutoffUtc);
        await AssertAbandonedAsync(busy, Now.AddSeconds(100).UtcDateTime);
    }

    [Fact]
    public async Task Sweep_FailedMiddleCommitPreservesEarlierCommitAndContinuesInReusableScope()
    {
        var executions = await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        var failure = new SweepCommitFailure { FailAt = 2 };
        await using var provider = Provider(failure);
        await using var scope = provider.CreateAsyncScope();
        var sweep = Sweep(scope.ServiceProvider);
        var first = await sweep.ExecuteAsync(3, null, Token);
        Assert.Equal(new(3, 2, 0, 1, SweepCursor(ordered[^1])), first);
        Assert.Equal(3, failure.Transactions.Count);
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
        var failed = executions.Single(item => item.Work.LeaseId == ordered[1].LeaseId);
        await AssertRunningAsync(failed);
        foreach (var execution in executions.Where(item => item != failed))
            await AssertAbandonedAsync(execution, Now.AddSeconds(40).UtcDateTime);
        Assert.Equal(new(0, 0, 0, 0, null), await sweep.ExecuteAsync(3, first.NextCursor, Token));
        Assert.Equal(new(1, 1, 0, 0, null), await sweep.ExecuteAsync(3, null, Token));
        await AssertAbandonedAsync(failed, Now.AddSeconds(40).UtcDateTime);
    }

    [Fact]
    public async Task Sweep_StaleDiscoveryAfterRenewalAndCompletionIsRevalidated()
    {
        var executions = await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        var renewed = executions.Single(item => item.Work.LeaseId == ordered[0].LeaseId);
        var completed = executions.Single(item => item.Work.LeaseId == ordered[1].LeaseId);
        var gate = new FinalizationCommandGate { PauseBefore = "workers" };
        await using var provider = Provider(gate);
        var pass = SweepAsync(3, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(ExecutionOutcome.Succeeded, (await RenewAsync(renewed, Now.AddSeconds(20), 60)).Outcome);
            var report = Report();
            var completion = await CompleteAsync(completed, report, Now.AddSeconds(35));
            gate.Release.TrySetResult();
            Assert.Equal(new(3, 1, 2, 0, SweepCursor(ordered[^1])), await pass);
            Assert.Equal(completion.Completion, (await CompleteAsync(completed, report, Now.AddDays(1))).Completion);
            await using var context = Context();
            var lease = await context.Leases.SingleAsync(lease => lease.Id == renewed.Work.LeaseId, Token);
            Assert.Null(lease.ReleasedAtUtc);
            Assert.Equal(Now.AddSeconds(80).UtcDateTime, lease.ExpiresAtUtc);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Sweep_FixedDiscoveryCutoffDoesNotExpandAsDecisionTimeAdvances()
    {
        await AcquireManyAsync(3);
        var later = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.AddSeconds(41).UtcDateTime} WHERE id = {later.Work.LeaseId}", Token);
        var clock = new FinalizationClock(Now.AddSeconds(40).AddTicks(9));
        var gate = new FinalizationCommandGate { PauseBefore = "workers" };
        await using var provider = Provider(gate);
        var pass = SweepAsync(10, provider: provider, clock: clock);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            clock.Now = Now.AddSeconds(100);
            gate.Release.TrySetResult();
            Assert.Equal(new(3, 3, 0, 0, null), await pass);
            Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(job => job.Id == later.Work.JobId, Token)).Status);
            Assert.Equal(3, await context.Jobs.CountAsync(job => job.CompletedAtUtc == Now.AddSeconds(100).UtcDateTime, Token));
            Assert.Equal(new(1, 1, 0, 0, null), await SweepAsync(10, clock: clock));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Sweep_ConcurrentScopesCommitEachExecutionOnlyOnceWithoutGlobalSerialization()
    {
        await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        var gate = new CommitInterceptor { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = Provider(gate);
        var first = SweepAsync(3, provider: provider);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var second = await SweepAsync(3).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(new(3, 2, 1, 0, SweepCursor(ordered[^1])), second);
            Assert.False(first.IsCompleted);
            gate.Gate.TrySetResult();
            Assert.Equal(new(3, 1, 2, 0, SweepCursor(ordered[^1])), await first);
            var before = await ExecutionRowsAsync();
            Assert.Equal(new(0, 0, 0, 0, null), await SweepAsync(3));
            Assert.Equal(before, await ExecutionRowsAsync());
            await using var context = Context();
            Assert.Equal(3, await context.JobAttempts.CountAsync(attempt => attempt.Status == JobAttemptStatus.Abandoned, Token));
        }
        finally { gate.Gate.TrySetResult(); }
    }

    [Fact]
    public async Task Sweep_ResetAfterProviderRestartRevisitsPersistedSkippedWork()
    {
        var executions = await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        var busy = executions.Single(item => item.Work.LeaseId == ordered[0].LeaseId);
        await using (var blocker = Context())
        await using (var gate = await blocker.Database.BeginTransactionAsync(Token))
        {
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {busy.Worker.WorkerId} FOR UPDATE", Token);
            Assert.Equal(new(2, 1, 1, 0, SweepCursor(ordered[1])), await SweepAsync(2));
        }
        await using var restarted = Provider();
        Assert.Equal(2, (await SweepAsync(10, after: null, provider: restarted)).Finalized);
        await AssertAbandonedAsync(busy, Now.AddSeconds(40).UtcDateTime);
    }

    [Fact]
    public async Task Sweep_CancellationBetweenCandidatesKeepsCommitAndCanResumeInSameScope()
    {
        await AcquireManyAsync(3);
        var ordered = await DiscoverAsync();
        await using var scope = _provider.CreateAsyncScope();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var persistence = scope.ServiceProvider.GetRequiredService<IFinalizeExpiredExecutionPersistence>();
        var wrapper = new CancelAfterDispose(persistence, cancellation);
        var sweep = Sweep(scope.ServiceProvider, persistence: wrapper);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.ExecuteAsync(3, null, cancellation.Token));
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
        var finished = await context.Leases.AsNoTracking().Where(lease => lease.ReleasedAtUtc != null).Select(lease => lease.Id).ToListAsync(Token);
        Assert.Equal(ordered[0].LeaseId, Assert.Single(finished));
        Assert.Equal(new(2, 2, 0, 0, null), await sweep.ExecuteAsync(3, null, Token));
    }

    [Fact]
    public async Task Sweep_DiscoveryFailureDoesNotAdvanceCursorOrLosePersistedCandidates()
    {
        await AcquireManyAsync(3);
        var before = await ExecutionRowsAsync();
        var failure = new DiscoveryFailure();
        await using var provider = Provider(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SweepAsync(2, provider: provider));
        Assert.Equal(before, await ExecutionRowsAsync());
        var retry = await SweepAsync(2, provider: provider);
        Assert.Equal(2, retry.Finalized);
        Assert.Equal(new(1, 1, 0, 0, null), await SweepAsync(2, retry.NextCursor, provider));
    }

    private async Task<Acquired[]> AcquireManyAsync(int count)
    {
        var executions = new Acquired[count];
        for (var index = 0; index < count; index++) executions[index] = await AcquireAsync();
        return executions;
    }
    private static ExpiredExecutionSweepCursor SweepCursor(ExpiredExecutionCursor after) => new(after, Now.AddSeconds(40).UtcDateTime);
    private async Task<ExpiredExecutionSweepResult> SweepAsync(int batchSize = 100, ExpiredExecutionSweepCursor? after = null,
        ServiceProvider? provider = null, TimeProvider? clock = null)
    {
        await using var scope = (provider ?? _provider).CreateAsyncScope();
        return await Sweep(scope.ServiceProvider, clock).ExecuteAsync(batchSize, after, Token);
    }
    private static FinalizeExpiredExecutionSweep Sweep(IServiceProvider services, TimeProvider? clock = null,
        IFinalizeExpiredExecutionPersistence? persistence = null)
    {
        clock ??= new Clock(Now.AddSeconds(40));
        var finalizer = new FinalizeExpiredExecution(persistence ?? services.GetRequiredService<IFinalizeExpiredExecutionPersistence>(),
            clock, NullLogger<FinalizeExpiredExecution>.Instance);
        return new(services.GetRequiredService<IExpiredExecutionDiscovery>(), finalizer, clock, NullLogger<FinalizeExpiredExecutionSweep>.Instance);
    }

    private sealed class SweepCommitFailure : DbTransactionInterceptor
    {
        public int FailAt { get; init; }
        public HashSet<Guid> Transactions { get; } = [];
        private int _commits;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Transactions.Add(eventData.TransactionId);
            if (++_commits == FailAt) throw new InvalidOperationException("Private payload/result/token/hash must not be logged.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class DiscoveryFailure : DbCommandInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("Discovery failed.");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelAfterDispose(IFinalizeExpiredExecutionPersistence inner, CancellationTokenSource cancellation)
        : IFinalizeExpiredExecutionPersistence
    {
        public async Task<IFinalizeExpiredExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), cancellation);
        private sealed class Transaction(IFinalizeExpiredExecutionTransaction inner, CancellationTokenSource cancellation)
            : IFinalizeExpiredExecutionTransaction
        {
            public Task<FinalizationLockResult> TryLockExecutionAsync(Guid leaseId, CancellationToken cancellationToken) =>
                inner.TryLockExecutionAsync(leaseId, cancellationToken);
            public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                cancellation.Cancel();
            }
        }
    }
}
