using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Synestra.Application.Executions;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Xunit;

namespace Synestra.Application.Tests.Executions;

public sealed class FinalizeExpiredExecutionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyDiscoveryResetsCursorWithoutOpeningTransaction()
    {
        var scenario = new Scenario();
        var after = new ExpiredExecutionSweepCursor(new(Now.AddSeconds(-1).UtcDateTime, Guid.CreateVersion7()), Now.UtcDateTime);
        Assert.Equal(new(0, 0, 0, 0, null), await scenario.Sweep.ExecuteAsync(100, after, Token));
        Assert.Equal(after.After, Assert.Single(scenario.Discovery.Calls).After);
        Assert.Equal(0, scenario.Store.Begun);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task FixedMicrosecondCutoffAndExplicitCursorBoundOnePass(int limit, bool full)
    {
        var scenario = new Scenario(3);
        scenario.Clock.Now = Now.AddTicks(9);
        scenario.Store.OnCommit = () => scenario.Clock.Now = Now.AddMinutes(1);
        var result = await scenario.Sweep.ExecuteAsync(limit, null, Token);
        var discovery = Assert.Single(scenario.Discovery.Calls);
        Assert.Equal(Now.UtcDateTime, discovery.Cutoff);
        Assert.Null(discovery.After);
        Assert.Equal(limit, discovery.Limit);
        Assert.Equal(Token, discovery.Token);
        Assert.Equal(new(3, 3, 0, 0, full ? new(scenario.Discovery.Items[^1], Now.UtcDateTime) : null), result);
        Assert.Equal(3, scenario.Store.Begun);
        Assert.Equal(3, scenario.Store.Disposed);
        Assert.All(scenario.Store.Tokens, token => Assert.Equal(Token, token));
    }

    [Fact]
    public async Task OutcomesAreCountedSeparatelyAndFailureDoesNotHideFollowingCandidatesOrLeakData()
    {
        var scenario = new Scenario(6);
        scenario.Store.Items[0].Failure = new InvalidOperationException("private payload result token hash");
        scenario.Store.Items[1].Outcome = FinalizationLockOutcome.Busy;
        scenario.Store.Items[2].Outcome = FinalizationLockOutcome.Missing;
        scenario.Store.Items[3].Outcome = FinalizationLockOutcome.Inconsistent;
        scenario.Store.Items[4].HasCompletion = true;
        var result = await scenario.Sweep.ExecuteAsync(6, null, Token);
        Assert.Equal(new(6, 1, 4, 1, new(scenario.Discovery.Items[^1], Now.UtcDateTime)), result);
        Assert.True(scenario.Store.Items[5].Committed);
        Assert.Equal(6, scenario.Store.Disposed);
        var log = Assert.Single(scenario.Log.Entries);
        Assert.Null(log.Exception);
        Assert.Contains(scenario.Store.Items[0].Lease.Id.ToString(), log.Message);
        Assert.Contains(nameof(InvalidOperationException), log.Message);
        Assert.DoesNotContain("private", log.Message);
        Assert.DoesNotContain("payload", log.Message);
        Assert.DoesNotContain("token", log.Message);
        Assert.DoesNotContain("hash", log.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorAdvancesPastTheLastCandidateEvenWhenItIsSkippedOrFails(bool failed)
    {
        var scenario = new Scenario(1);
        if (failed) scenario.Store.Items[0].Failure = new InvalidOperationException();
        else scenario.Store.Items[0].Outcome = FinalizationLockOutcome.Busy;
        Assert.Equal(new(scenario.Discovery.Items[0], Now.UtcDateTime), (await scenario.Sweep.ExecuteAsync(1, null, Token)).NextCursor);
    }

    [Fact]
    public async Task CancellationBetweenCandidatesPreservesPrecedingCommitAndStopsThePass()
    {
        var scenario = new Scenario(3);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        scenario.Store.OnDispose = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Sweep.ExecuteAsync(3, null, cancellation.Token));
        Assert.True(scenario.Store.Items[0].Committed);
        Assert.False(scenario.Store.Items[1].Committed);
        Assert.Equal(1, scenario.Store.Begun);
        Assert.Equal(1, scenario.Store.Disposed);
        Assert.Empty(scenario.Log.Entries);
    }

    [Fact]
    public async Task CandidateCancellationPropagatesWithoutBecomingFailedOrContinuing()
    {
        var scenario = new Scenario(2);
        var failure = new OperationCanceledException(Token);
        scenario.Store.Items[0].Failure = failure;
        Assert.Same(failure, await Record.ExceptionAsync(() => scenario.Sweep.ExecuteAsync(2, null, Token)));
        Assert.Equal(1, scenario.Store.Begun);
        Assert.Equal(1, scenario.Store.Disposed);
        Assert.Empty(scenario.Log.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryFailurePropagatesWithoutOpeningTransaction(bool canceled)
    {
        var scenario = new Scenario();
        scenario.Discovery.Failure = canceled ? new OperationCanceledException(Token) : new InvalidOperationException("private payload result token hash");
        Assert.Same(scenario.Discovery.Failure, await Record.ExceptionAsync(() => scenario.Sweep.ExecuteAsync(100, null, Token)));
        Assert.Equal(0, scenario.Store.Begun);
        if (canceled) Assert.Empty(scenario.Log.Entries);
        else
        {
            var log = Assert.Single(scenario.Log.Entries);
            Assert.Null(log.Exception);
            Assert.Equal("Expired execution discovery failed. Failure type: InvalidOperationException.", log.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidBatchSizeIsRejectedBeforeDiscovery(int size)
    {
        var scenario = new Scenario();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scenario.Sweep.ExecuteAsync(size, null, Token));
        Assert.Empty(scenario.Discovery.Calls);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task CursorRequiresUtc(DateTimeKind kind)
    {
        var scenario = new Scenario();
        var cursor = new ExpiredExecutionSweepCursor(new(DateTime.SpecifyKind(Now.UtcDateTime, kind), Guid.NewGuid()), Now.UtcDateTime);
        await Assert.ThrowsAsync<ArgumentException>(() => scenario.Sweep.ExecuteAsync(1, cursor, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => scenario.Sweep.ExecuteAsync(1,
            new(new(Now.UtcDateTime, Guid.NewGuid()), DateTime.SpecifyKind(Now.UtcDateTime, kind)), Token));
        Assert.Empty(scenario.Discovery.Calls);
    }

    [Fact]
    public async Task PreCanceledPassNeverDiscovers()
    {
        var scenario = new Scenario();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Sweep.ExecuteAsync(1, null, new CancellationToken(true)));
        Assert.Empty(scenario.Discovery.Calls);
    }

    [Fact]
    public async Task ContinuationRetainsTraversalCutoffUntilTheCursorResets()
    {
        var scenario = new Scenario(1);
        scenario.Store.Items[0].Outcome = FinalizationLockOutcome.Busy;
        var first = await scenario.Sweep.ExecuteAsync(1, null, Token);
        scenario.Clock.Now = Now.AddHours(1);
        scenario.Discovery.Items = [];
        var second = await scenario.Sweep.ExecuteAsync(1, first.NextCursor, Token);
        Assert.Null(second.NextCursor);
        Assert.Equal(Now.UtcDateTime, scenario.Discovery.Calls[1].Cutoff);
        Assert.Equal(first.NextCursor!.After, scenario.Discovery.Calls[1].After);
        await scenario.Sweep.ExecuteAsync(1, second.NextCursor, Token);
        Assert.Equal(Now.AddHours(1).UtcDateTime, scenario.Discovery.Calls[2].Cutoff);
    }

    private sealed class Scenario
    {
        public Scenario(int count = 0)
        {
            Store.Items.AddRange(Enumerable.Range(0, count).Select(_ => new Item()));
            Discovery.Items = Store.Items.Select(item => new ExpiredExecutionCursor(item.Lease.ExpiresAtUtc, item.Lease.Id)).ToArray();
            Sweep = new(Discovery, new(Store, Clock, NullLogger<FinalizeExpiredExecution>.Instance), Clock, Log);
        }
        public Discovery Discovery { get; } = new();
        public Store Store { get; } = new();
        public Clock Clock { get; } = new();
        public RecordingLogger Log { get; } = new();
        public FinalizeExpiredExecutionSweep Sweep { get; }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = FinalizeExpiredExecutionSweepTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Discovery : IExpiredExecutionDiscovery
    {
        public IReadOnlyList<ExpiredExecutionCursor> Items { get; set; } = [];
        public Exception? Failure { get; set; }
        public List<(DateTime Cutoff, ExpiredExecutionCursor? After, int Limit, CancellationToken Token)> Calls { get; } = [];
        public Task<IReadOnlyList<ExpiredExecutionCursor>> FindAsync(DateTime cutoffUtc, ExpiredExecutionCursor? after, int limit, CancellationToken cancellationToken)
        {
            Calls.Add((cutoffUtc, after, limit, cancellationToken));
            if (Failure is not null) throw Failure;
            return Task.FromResult(Items);
        }
    }
    private sealed class Item
    {
        public Item()
        {
            Job = new(Guid.CreateVersion7(), "test", "{}", 0, 1, Now.AddMinutes(-1).UtcDateTime, Now.AddMinutes(-1).UtcDateTime);
            Attempt = Job.StartAttempt(Now.AddMinutes(-1).UtcDateTime);
            Lease = new(Attempt.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), Attempt.StartedAtUtc, TimeSpan.FromSeconds(30));
        }
        public Job Job { get; }
        public JobAttempt Attempt { get; }
        public Lease Lease { get; }
        public FinalizationLockOutcome Outcome { get; set; } = FinalizationLockOutcome.Locked;
        public bool HasCompletion { get; set; }
        public Exception? Failure { get; set; }
        public bool Committed { get; set; }
    }
    private sealed class Store : IFinalizeExpiredExecutionPersistence
    {
        public List<Item> Items { get; } = [];
        public int Begun { get; private set; }
        public int Disposed { get; private set; }
        public List<CancellationToken> Tokens { get; } = [];
        public Action? OnCommit { get; set; }
        public Action? OnDispose { get; set; }
        public Task<IFinalizeExpiredExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(Begun, Disposed);
            Begun++;
            Tokens.Add(cancellationToken);
            return Task.FromResult<IFinalizeExpiredExecutionTransaction>(new Transaction(this));
        }
        private sealed class Transaction(Store store) : IFinalizeExpiredExecutionTransaction
        {
            private Item _item = null!;
            public Task<FinalizationLockResult> TryLockExecutionAsync(Guid leaseId, CancellationToken cancellationToken)
            {
                store.Tokens.Add(cancellationToken);
                _item = store.Items.Single(item => item.Lease.Id == leaseId);
                return Task.FromResult(new FinalizationLockResult(_item.Outcome, new(_item.Job, _item.Attempt, _item.Lease, _item.HasCompletion)));
            }
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                store.Tokens.Add(cancellationToken);
                if (_item.Failure is not null) throw _item.Failure;
                _item.Committed = true;
                store.OnCommit?.Invoke();
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                store.Disposed++;
                store.OnDispose?.Invoke();
                return ValueTask.CompletedTask;
            }
        }
    }
    private sealed class RecordingLogger : ILogger<FinalizeExpiredExecutionSweep>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }
}
