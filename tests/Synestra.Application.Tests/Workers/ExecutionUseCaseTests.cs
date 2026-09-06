using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;
using Xunit;

namespace Synestra.Application.Tests.Workers;

public sealed class ExecutionUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Renewal_UsesClockAfterLocksOptionsAndCommitWithoutChangingLiveness()
    {
        var store = new Store();
        var clock = new Clock(Now);
        store.OnExecutionLock = () => clock.Now = Now.AddSeconds(20).AddTicks(9);
        store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Renew(store, clock, 45);
        await store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(task.IsCompleted);
        store.Gate.SetResult();
        var result = await task;
        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(Now.AddSeconds(65).UtcDateTime, result.Lease!.ExpiresAtUtc);
        Assert.Equal(store.Lease.Id, result.Lease.LeaseId);
        Assert.Equal(Now.UtcDateTime, store.Lease.AcquiredAtUtc);
        Assert.Equal(Now.UtcDateTime, store.Attempt.StartedAtUtc);
        Assert.Equal(Now.UtcDateTime, store.Worker!.LastSeenAtUtc);
        Assert.Equal(["begin", "worker", "execution", "commit", "dispose"], store.Calls);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
        Assert.True(store.Committed);
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task OfflineRenewal_PreservesLongerExpirationAndLastSeen()
    {
        var store = new Store();
        store.Lease.Renew(Now.UtcDateTime, TimeSpan.FromSeconds(100));
        var result = await Renew(store, new Clock(Now.AddSeconds(40)));
        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(Now.AddSeconds(100).UtcDateTime, result.Lease!.ExpiresAtUtc);
        Assert.Equal(Now.UtcDateTime, store.Worker!.LastSeenAtUtc);
    }

    [Theory]
    [InlineData("missing-worker", ExecutionOutcome.WorkerNotFound)]
    [InlineData("stale", ExecutionOutcome.SessionReplaced)]
    [InlineData("legacy-session", ExecutionOutcome.SessionReplaced)]
    [InlineData("missing-lease", ExecutionOutcome.LeaseNotFound)]
    [InlineData("wrong-worker", ExecutionOutcome.OwnershipLost)]
    [InlineData("wrong-session", ExecutionOutcome.OwnershipLost)]
    [InlineData("wrong-token", ExecutionOutcome.OwnershipLost)]
    [InlineData("legacy-token", ExecutionOutcome.OwnershipLost)]
    [InlineData("changed-locator", ExecutionOutcome.OwnershipLost)]
    [InlineData("changed-job", ExecutionOutcome.OwnershipLost)]
    public async Task SharedValidation_HasStablePrecedenceForBothOperations(string state, ExecutionOutcome expected)
    {
        foreach (var completion in new[] { false, true })
        {
            var store = new Store();
            if (state == "missing-worker") store.Worker = null;
            if (state == "stale") store.SessionId = Guid.CreateVersion7();
            if (state == "legacy-session") Set(store.Worker!, nameof(Worker.SessionId), null);
            if (state == "missing-lease") store.MissingLease = true;
            if (state == "wrong-worker") Set(store.Lease, nameof(Lease.WorkerId), Guid.CreateVersion7());
            if (state == "wrong-session") Set(store.Lease, nameof(Lease.SessionId), Guid.CreateVersion7());
            if (state == "wrong-token") store.Hash = LeaseToken.Hash(LeaseToken.Generate());
            if (state == "legacy-token") store.Hash = null;
            if (state == "changed-locator") store.LocatorMatches = false;
            if (state == "changed-job") Set(store.Attempt, nameof(JobAttempt.JobId), Guid.CreateVersion7());
            // Ownership/session validation must win over terminal state and expiration.
            store.Lease.Release(Now.UtcDateTime);
            var outcome = completion ? (await Complete(store)).Outcome : (await Renew(store, new Clock(Now.AddDays(1)))).Outcome;
            Assert.Equal(expected, outcome);
            Assert.False(store.Committed);
            Assert.Null(store.Completion);
            if (expected is ExecutionOutcome.WorkerNotFound or ExecutionOutcome.SessionReplaced)
                Assert.DoesNotContain("execution", store.Calls);
        }
    }

    [Theory]
    [InlineData("boundary", ExecutionOutcome.LeaseExpired)]
    [InlineData("expired", ExecutionOutcome.LeaseExpired)]
    [InlineData("released", ExecutionOutcome.LeaseNotActive)]
    [InlineData("job", ExecutionOutcome.LeaseNotActive)]
    [InlineData("attempt", ExecutionOutcome.LeaseNotActive)]
    public async Task Renewal_LifecyclePrecedesExpiration(string state, ExecutionOutcome expected)
    {
        var store = new Store();
        if (state == "released") store.Lease.Release(Now.UtcDateTime);
        if (state == "job") Set(store.Job, nameof(Job.Status), JobStatus.Failed);
        if (state == "attempt") Set(store.Attempt, nameof(JobAttempt.Status), JobAttemptStatus.Failed);
        Assert.Equal(expected, (await Renew(store, new Clock(Now.AddSeconds(state == "boundary" ? 30 : 31)))).Outcome);
        Assert.False(store.Committed);
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, store.Lease.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Completion_UsesOneServerTimeAndWaitsForCommitIncludingLateReports(string outcome)
    {
        var store = new Store();
        var report = Report(outcome);
        store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Complete(store, report, new Clock(Now.AddSeconds(40).AddTicks(9)));
        await store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(task.IsCompleted);
        Assert.Null(store.Completion);
        store.Gate.SetResult();
        var result = await task;
        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(store.Completion, result.Completion);
        Assert.Equal(Now.AddSeconds(40).UtcDateTime, result.Completion!.FinishedAtUtc);
        Assert.Equal(result.Completion.FinishedAtUtc, store.Job.CompletedAtUtc);
        Assert.Equal(store.Job.CompletedAtUtc, store.Attempt.FinishedAtUtc);
        Assert.Equal(store.Attempt.FinishedAtUtc, store.Lease.ReleasedAtUtc);
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, store.Lease.ExpiresAtUtc);
        Assert.Equal(report.Result, store.Attempt.Result);
        Assert.Equal(report.Error?.Code, store.Attempt.ErrorCode);
        Assert.Equal(report.Error?.Message, store.Attempt.ErrorMessage);
        Assert.Equal(["begin", "worker", "execution", "record", "commit", "dispose"], store.Calls);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
    }

    [Fact]
    public async Task Replay_PreservesOriginalSnapshotAndTimesButStillChecksOwnership()
    {
        var store = new Store();
        var report = Report() with { Result = "{\"b\":[1.0,-0,true],\"a\":\"\\u0061\"}" };
        var first = await Complete(store, report);
        var replay = await Complete(store, report with { Result = " { \"a\":\"a\", \"b\":[1e0,0,true] } " }, new Clock(Now.AddDays(1)));
        Assert.Equal(first.Completion, replay.Completion);
        Assert.Equal(Now.UtcDateTime, store.Lease.ReleasedAtUtc);
        Assert.Equal(ExecutionOutcome.CompletionReportConflict,
            (await Complete(store, report with { Result = "{\"b\":[1,0,false],\"a\":\"a\"}" })).Outcome);
        Assert.Equal(ExecutionOutcome.AttemptAlreadyFinalized, (await Complete(store, report with { ReportId = Guid.CreateVersion7() })).Outcome);
        Assert.Equal(ExecutionOutcome.CompletionReportConflict, (await Complete(store, Report("failed") with { ReportId = report.ReportId })).Outcome);
        store.SessionId = Guid.CreateVersion7();
        Assert.Equal(ExecutionOutcome.SessionReplaced, (await Complete(store, report)).Outcome);
        Assert.Equal(first.Completion, store.Completion);
    }

    [Fact]
    public async Task FailureReplay_RequiresExactDecodedErrorStrings()
    {
        var store = new Store();
        var report = Report("failed");
        var first = await Complete(store, report);
        Assert.Equal(first.Completion, (await Complete(store, report, new Clock(Now.AddDays(1)))).Completion);
        Assert.Equal(ExecutionOutcome.CompletionReportConflict, (await Complete(store,
            report with { Error = report.Error! with { Code = "ERROR" } })).Outcome);
        Assert.Equal(ExecutionOutcome.CompletionReportConflict, (await Complete(store,
            report with { Error = report.Error! with { Message = "message " } })).Outcome);
    }

    [Fact]
    public async Task StructuralReplay_DistinguishesExactNumbersArrayOrderAndUnicode()
    {
        var store = new Store();
        var report = Report() with { Result = "{\"n\":9007199254740993,\"array\":[1,2],\"text\":\"é\"}" };
        var first = await Complete(store, report);
        Assert.Equal(first.Completion, (await Complete(store, report with
        {
            Result = "{\"text\":\"\\u00e9\",\"array\":[1.0,2e0],\"n\":9007199254740993.0}"
        })).Completion);
        foreach (var result in new[]
        {
            "{\"n\":9007199254740992,\"array\":[1,2],\"text\":\"é\"}",
            "{\"n\":9007199254740993,\"array\":[2,1],\"text\":\"é\"}",
            "{\"n\":9007199254740993,\"array\":[1,2],\"text\":\"e\\u0301\"}"
        })
            Assert.Equal(ExecutionOutcome.CompletionReportConflict, (await Complete(store, report with { Result = result })).Outcome);
    }

    [Fact]
    public async Task InvalidCompletionData_IsRejectedBeforeOpeningTransaction()
    {
        var store = new Store();
        foreach (var report in new[]
        {
            Report() with { ReportId = Guid.NewGuid() }, Report() with { Result = "null" },
            Report() with { Result = "{\"a\":1,\"a\":2}" },
            Report("failed") with { Error = new("error", new string('x', 2001)) }
        })
            Assert.Equal(ExecutionOutcome.InvalidRequest, (await Complete(store, report)).Outcome);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData("released")]
    [InlineData("job")]
    [InlineData("attempt")]
    public async Task Completion_RejectsReportlessTerminalState(string state)
    {
        var store = new Store();
        if (state == "released") store.Lease.Release(Now.UtcDateTime);
        if (state == "job") Set(store.Job, nameof(Job.Status), JobStatus.Failed);
        if (state == "attempt") Set(store.Attempt, nameof(JobAttempt.Status), JobAttemptStatus.Failed);
        Assert.Equal(ExecutionOutcome.AttemptAlreadyFinalized, (await Complete(store)).Outcome);
        Assert.Null(store.Completion);
    }

    [Fact]
    public async Task InvalidIdentityAndToken_FailBeforeTransaction()
    {
        foreach (var completion in new[] { false, true })
        foreach (var field in new[] { "worker", "session", "lease", "token" })
        {
            var store = new Store();
            if (field == "worker") store.WorkerId = Guid.NewGuid();
            if (field == "session") store.SessionId = Guid.Empty;
            if (field == "lease") store.LeaseId = Guid.Parse("019ec569-5a00-7000-0000-000000000001");
            if (field == "token") store.Secret += "=";
            Assert.Equal(ExecutionOutcome.InvalidRequest, completion ? (await Complete(store)).Outcome : (await Renew(store)).Outcome);
            Assert.Empty(store.Calls);
        }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("worker")]
    [InlineData("execution")]
    [InlineData("commit")]
    public async Task ExceptionsAndCancellation_PropagateWithoutReservingReport(string operation)
    {
        foreach (var completion in new[] { false, true })
        foreach (var error in new Exception[] { new InvalidOperationException("test"), new OperationCanceledException(Token) })
        {
            var store = new Store { FailureAt = operation, Error = error };
            Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                if (completion) await Complete(store); else await Renew(store);
            }));
            Assert.False(store.Committed);
            Assert.Null(store.Completion);
            Assert.Equal(operation != "begin", store.Disposed);
            Assert.All(store.Tokens, token => Assert.Equal(Token, token));
        }
    }

    [Fact]
    public async Task CancelledInputAndRegressedClock_CannotBecomeOutcome()
    {
        var store = new Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RenewLease(store, new Clock(Now), new())
            .ExecuteAsync(store.WorkerId, store.SessionId, store.LeaseId, store.Secret, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReportExecutionCompletion(store, new Clock(Now))
            .ExecuteAsync(store.WorkerId, store.SessionId, store.LeaseId, store.Secret, Report(), cancellation.Token));
        Assert.Empty(store.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(store, clock: new Clock(Now.AddSeconds(-1))));
        Assert.Equal(JobStatus.Running, store.Job.Status);
        Assert.Null(store.Lease.ReleasedAtUtc);
    }

    private Task<RenewLeaseResult> Renew(Store store, Clock? clock = null, int duration = 30) =>
        new RenewLease(store, clock ?? new Clock(Now), new() { LeaseDurationSeconds = duration })
            .ExecuteAsync(store.WorkerId, store.SessionId, store.LeaseId, store.Secret, Token);
    private Task<CompletionResult> Complete(Store store, CompletionReport? report = null, Clock? clock = null) =>
        new ReportExecutionCompletion(store, clock ?? new Clock(Now))
            .ExecuteAsync(store.WorkerId, store.SessionId, store.LeaseId, store.Secret, report ?? Report(), Token);
    private static CompletionReport Report(string outcome = "succeeded") => outcome == "succeeded"
        ? new(Guid.CreateVersion7(), outcome, "{}") : new(Guid.CreateVersion7(), outcome, Error: new("error", "message"));
    private static void Set(object entity, string property, object? value) => entity.GetType().GetProperty(property)!.SetValue(entity, value);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : IRenewLeasePersistence, IReportExecutionCompletionPersistence,
        IRenewLeaseTransaction, IReportExecutionCompletionTransaction
    {
        public Store()
        {
            Worker = new(new(WorkerId, SessionId, "test", 1, ["test"]), Now.UtcDateTime);
            Attempt = Job.StartAttempt(Now.UtcDateTime);
            Lease = new(Attempt.Id, WorkerId, SessionId, Now.UtcDateTime, TimeSpan.FromSeconds(30));
            LeaseId = Lease.Id;
            Hash = LeaseToken.Hash(Secret);
        }
        public Guid WorkerId { get; set; } = Guid.CreateVersion7();
        public Guid SessionId { get; set; } = Guid.CreateVersion7();
        public Guid LeaseId { get; set; }
        public string Secret { get; set; } = LeaseToken.Generate();
        public byte[]? Hash { get; set; }
        public Worker? Worker { get; set; }
        public Job Job { get; } = new(Guid.CreateVersion7(), "test", "{}", 0, 1, Now.UtcDateTime, Now.UtcDateTime);
        public JobAttempt Attempt { get; }
        public Lease Lease { get; }
        public bool MissingLease { get; set; }
        public bool LocatorMatches { get; set; } = true;
        public ExecutionCompletionSnapshot? Completion { get; private set; }
        private ExecutionCompletionSnapshot? _pending;
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Action? OnExecutionLock { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailureAt { get; init; }
        public Exception? Error { get; init; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        private void Call(string name, CancellationToken token)
        {
            Calls.Add(name);
            Tokens.Add(token);
            token.ThrowIfCancellationRequested();
            if (name == FailureAt) throw Error!;
        }
        Task<IRenewLeaseTransaction> IRenewLeasePersistence.BeginTransactionAsync(CancellationToken cancellationToken)
        { Call("begin", cancellationToken); return Task.FromResult<IRenewLeaseTransaction>(this); }
        Task<IReportExecutionCompletionTransaction> IReportExecutionCompletionPersistence.BeginTransactionAsync(CancellationToken cancellationToken)
        { Call("begin", cancellationToken); return Task.FromResult<IReportExecutionCompletionTransaction>(this); }
        public Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken)
        { Call("worker", cancellationToken); Assert.Equal(WorkerId, workerId); return Task.FromResult(Worker); }
        public Task<LeaseExecution?> LockExecutionAsync(Guid leaseId, CancellationToken cancellationToken)
        {
            Call("execution", cancellationToken);
            Assert.Equal(LeaseId, leaseId);
            OnExecutionLock?.Invoke();
            return Task.FromResult(MissingLease ? null : new LeaseExecution(Job, Attempt, Lease, Hash, Completion, LocatorMatches));
        }
        public void RecordCompletion(ExecutionCompletionSnapshot snapshot) { Calls.Add("record"); _pending = snapshot; }
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            Call("commit", cancellationToken);
            CommitEntered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            Completion = _pending ?? Completion;
            Committed = true;
        }
        public ValueTask DisposeAsync() { Calls.Add("dispose"); _pending = null; Disposed = true; return ValueTask.CompletedTask; }
    }
}
