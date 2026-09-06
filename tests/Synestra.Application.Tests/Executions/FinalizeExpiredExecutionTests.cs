using Microsoft.Extensions.Logging;
using Synestra.Application.Executions;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Xunit;

namespace Synestra.Application.Tests.Executions;

public sealed class FinalizeExpiredExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-1, FinalizeExpiredExecutionOutcome.NotEligible)]
    [InlineData(0, FinalizeExpiredExecutionOutcome.Finalized)]
    [InlineData(1, FinalizeExpiredExecutionOutcome.Finalized)]
    public async Task Expiration_IsInclusiveAndUsesMicroseconds(long microseconds, FinalizeExpiredExecutionOutcome expected)
    {
        var store = new Store();
        var outcome = await Execute(store, new Clock(Now.AddSeconds(30).AddTicks(microseconds * 10 + 9)));
        Assert.Equal(expected, outcome);
        Assert.Equal(expected == FinalizeExpiredExecutionOutcome.Finalized, store.Committed);
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, store.Lease.ExpiresAtUtc);
        Assert.Equal(expected == FinalizeExpiredExecutionOutcome.Finalized
            ? Now.AddSeconds(30).AddTicks(microseconds * 10).UtcDateTime : (DateTime?)null, store.Job.CompletedAtUtc);
        Assert.Equal(store.Job.CompletedAtUtc, store.Attempt.FinishedAtUtc);
        Assert.Equal(store.Attempt.FinishedAtUtc, store.Lease.ReleasedAtUtc);
    }

    [Fact]
    public async Task TimeIsSampledAfterLocksAndFinalizedIsReturnedOnlyAfterCommit()
    {
        var store = new Store { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var clock = new Clock(Now);
        store.OnLock = () => clock.Now = Now.AddSeconds(50).AddTicks(9);
        var task = Execute(store, clock);
        await store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(task.IsCompleted);
        store.Gate.SetResult();
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await task);
        Assert.Equal(Now.AddSeconds(50).UtcDateTime, store.Job.CompletedAtUtc);
        Assert.Equal(["begin", "lock", "commit", "dispose"], store.Calls);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
    }

    [Theory]
    [InlineData(FinalizationLockOutcome.Missing, FinalizeExpiredExecutionOutcome.Missing)]
    [InlineData(FinalizationLockOutcome.Busy, FinalizeExpiredExecutionOutcome.Busy)]
    [InlineData(FinalizationLockOutcome.Inconsistent, FinalizeExpiredExecutionOutcome.Inconsistent)]
    public async Task UnavailableExecution_SkipsWithoutClockOrMutation(FinalizationLockOutcome locked, FinalizeExpiredExecutionOutcome expected)
    {
        var store = new Store { LockOutcome = locked };
        var logger = new RecordingLogger();
        var clock = new Clock(Now);
        Assert.Equal(expected, await Execute(store, clock, logger));
        Assert.Equal(0, clock.Reads);
        Assert.Equal(["begin", "lock", "dispose"], store.Calls);
        Assert.Null(store.Job.CompletedAtUtc);
        Assert.Equal(expected == FinalizeExpiredExecutionOutcome.Inconsistent ? 1 : 0, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
        {
            Assert.Contains(store.Lease.Id.ToString(), message);
            Assert.DoesNotContain("private-payload", message);
        });
    }

    [Theory]
    [InlineData("job-status")]
    [InlineData("attempt-status")]
    [InlineData("completed")]
    [InlineData("finished")]
    [InlineData("released")]
    [InlineData("result")]
    [InlineData("code")]
    [InlineData("message")]
    [InlineData("report-or-snapshot")]
    public async Task ExistingOutcomeOrRelease_IsNeverOverwritten(string state)
    {
        var store = new Store();
        switch (state)
        {
            case "job-status": Set(store.Job, "Status", JobStatus.Failed); break;
            case "attempt-status": Set(store.Attempt, "Status", JobAttemptStatus.Abandoned); break;
            case "completed": Set(store.Job, "CompletedAtUtc", Now.UtcDateTime); break;
            case "finished": Set(store.Attempt, "FinishedAtUtc", Now.UtcDateTime); break;
            case "released": store.Lease.Release(Now.UtcDateTime); break;
            case "result": Set(store.Attempt, "Result", "{}"); break;
            case "code": Set(store.Attempt, "ErrorCode", "existing"); break;
            case "message": Set(store.Attempt, "ErrorMessage", "existing"); break;
            case "report-or-snapshot": store.HasCompletion = true; break;
        }
        Assert.Equal(FinalizeExpiredExecutionOutcome.NotEligible, await Execute(store));
        Assert.False(store.Committed);
        Assert.Equal(["begin", "lock", "dispose"], store.Calls);
    }

    [Theory]
    [InlineData("acquisition")]
    [InlineData("start")]
    [InlineData("creation")]
    public async Task InconsistentChronology_IsDiagnosedWithoutPartialMutation(string state)
    {
        var store = new Store();
        if (state == "acquisition") Set(store.Lease, "AcquiredAtUtc", Now.AddMinutes(2).UtcDateTime);
        if (state == "start") Set(store.Attempt, "StartedAtUtc", Now.AddMinutes(2).UtcDateTime);
        if (state == "creation") Set(store.Job, "CreatedAtUtc", Now.AddSeconds(1).UtcDateTime);
        var log = new RecordingLogger();
        Assert.Equal(FinalizeExpiredExecutionOutcome.Inconsistent, await Execute(store, logger: log));
        Assert.Single(log.Messages);
        Assert.Null(store.Job.CompletedAtUtc);
        Assert.Null(store.Attempt.ErrorCode);
        Assert.Null(store.Lease.ReleasedAtUtc);
        Assert.False(store.Committed);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("lock")]
    [InlineData("commit")]
    public async Task FailuresAndCancellationPropagateAtEveryAsyncBoundary(string stage)
    {
        foreach (var cancellation in new[] { false, true })
        {
            var failure = cancellation ? (Exception)new OperationCanceledException(Token) : new InvalidOperationException("Failure");
            var store = new Store { FailureStage = stage, Failure = failure };
            var actual = await Record.ExceptionAsync(() => Execute(store));
            Assert.Same(failure, actual);
            Assert.False(store.Committed);
            Assert.Equal(stage != "begin", store.Calls.Contains("dispose"));
        }
    }

    [Fact]
    public async Task PreCanceledCallNeverOpensTransaction()
    {
        var store = new Store();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FinalizeExpiredExecution(store, new Clock(Now), new RecordingLogger()).ExecuteAsync(store.Lease.Id, canceled.Token));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task CancellationAfterCommitStillPropagatesInsteadOfReturningFinalized()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var store = new Store { OnCommit = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FinalizeExpiredExecution(store, new Clock(Now.AddSeconds(40)), new RecordingLogger())
                .ExecuteAsync(store.Lease.Id, cancellation.Token));
        Assert.True(store.Committed);
        Assert.Contains("dispose", store.Calls);
    }

    private Task<FinalizeExpiredExecutionOutcome> Execute(Store store, Clock? clock = null, RecordingLogger? logger = null) =>
        new FinalizeExpiredExecution(store, clock ?? new Clock(Now.AddSeconds(40)), logger ?? new RecordingLogger())
            .ExecuteAsync(store.Lease.Id, Token);
    private static void Set(object entity, string property, object value) => entity.GetType().GetProperty(property)!.SetValue(entity, value);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }
    private sealed class RecordingLogger : ILogger<FinalizeExpiredExecution>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
    private sealed class Store : IFinalizeExpiredExecutionPersistence, IFinalizeExpiredExecutionTransaction
    {
        public Store()
        {
            Job = new(Guid.CreateVersion7(), "test", "private-payload", 0, 1, Now.UtcDateTime, Now.UtcDateTime);
            Attempt = Job.StartAttempt(Now.UtcDateTime);
            Lease = new(Attempt.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, TimeSpan.FromSeconds(30));
        }
        public Job Job { get; }
        public JobAttempt Attempt { get; }
        public Lease Lease { get; }
        public bool HasCompletion { get; set; }
        public bool Committed { get; private set; }
        public FinalizationLockOutcome LockOutcome { get; init; } = FinalizationLockOutcome.Locked;
        public Action? OnLock { get; set; }
        public Action? OnCommit { get; init; }
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailureStage { get; init; }
        public Exception? Failure { get; init; }
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Task<IFinalizeExpiredExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            Call("begin", cancellationToken);
            return Task.FromResult<IFinalizeExpiredExecutionTransaction>(this);
        }
        public Task<FinalizationLockResult> TryLockExecutionAsync(Guid leaseId, CancellationToken cancellationToken)
        {
            Call("lock", cancellationToken);
            Assert.Equal(Lease.Id, leaseId);
            OnLock?.Invoke();
            return Task.FromResult(new FinalizationLockResult(LockOutcome, new(Job, Attempt, Lease, HasCompletion)));
        }
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            Call("commit", cancellationToken);
            CommitEntered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            Committed = true;
            OnCommit?.Invoke();
        }
        public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
        private void Call(string stage, CancellationToken cancellationToken)
        {
            Calls.Add(stage);
            Tokens.Add(cancellationToken);
            if (stage == FailureStage) throw Failure!;
        }
    }
}
