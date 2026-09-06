using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;
using Xunit;

namespace Synestra.Application.Tests.Workers;

public sealed class ClaimWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_UsesTimeAfterLockOptionsAndCommitBeforeReturning()
    {
        var store = new Store();
        var clock = new Clock(Now.AddDays(-1));
        store.OnLock = () => clock.Now = Now.AddSeconds(5).AddTicks(9);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CommitGate = commit.Task;
        var task = new ClaimWork(store, clock, new(), new() { LeaseDurationSeconds = 45 })
            .ExecuteAsync(store.Worker!.Id, store.Worker.SessionId!.Value, Token);
        await store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(task.IsCompleted);
        Assert.False(store.Committed);
        commit.SetResult();
        var result = await task;

        Assert.Equal(ClaimWorkOutcome.Succeeded, result.Outcome);
        Assert.True(store.Committed);
        Assert.True(store.Disposed);
        Assert.Equal(["begin", "worker", "capacity", "job", "add", "commit", "dispose"], store.Calls);
        Assert.Equal(5, store.Tokens.Count);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
        Assert.All(store.Times, time => Assert.Equal(Now.AddSeconds(5).UtcDateTime, time));
        Assert.Equal(Now.AddSeconds(5).UtcDateTime, result.Work!.AcquiredAtUtc);
        Assert.Equal(Now.AddSeconds(50).UtcDateTime, result.Work.ExpiresAtUtc);
        Assert.Equal(store.Job!.Id, result.Work.JobId);
        Assert.Equal(store.Attempt!.Id, result.Work.AttemptId);
        Assert.Equal(store.Lease!.Id, result.Work.LeaseId);
        Assert.Equal(store.Job.Payload, result.Work.Payload);
        Assert.Equal(store.Worker.Id, store.Lease.WorkerId);
        Assert.Equal(store.Worker.SessionId, store.Lease.SessionId);
        Assert.Equal(Now.UtcDateTime, store.Worker.LastSeenAtUtc);
    }

    [Theory]
    [InlineData("missing-job")]
    [InlineData("capacity")]
    [InlineData("unsupported")]
    [InlineData("future")]
    public async Task NoWork_DoesNotCreateOwnership(string reason)
    {
        var store = new Store();
        if (reason == "missing-job") store.Job = null;
        if (reason == "capacity") store.ActiveLeases = store.Worker!.Capacity;
        if (reason == "unsupported") store.Job = Job("Test");
        if (reason == "future") store.Job = Job(available: Now.AddSeconds(1));
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ExecuteAsync(store)).Outcome);
        Assert.Null(store.Attempt);
        Assert.Null(store.Lease);
        Assert.False(store.Committed);
        Assert.True(store.Disposed);
        if (reason == "capacity") Assert.DoesNotContain("job", store.Calls);
    }

    [Theory]
    [InlineData("missing", ClaimWorkOutcome.WorkerNotFound)]
    [InlineData("replaced", ClaimWorkOutcome.SessionReplaced)]
    [InlineData("legacy", ClaimWorkOutcome.SessionReplaced)]
    public async Task WorkerValidation_PrecedesCapacityAndSelection(string state, ClaimWorkOutcome expected)
    {
        var store = new Store();
        var id = store.Worker!.Id;
        var session = store.Worker.SessionId!.Value;
        if (state == "missing") store.Worker = null;
        if (state == "replaced") session = Guid.CreateVersion7();
        if (state == "legacy") typeof(Worker).GetProperty(nameof(Worker.SessionId))!.SetValue(store.Worker, null);
        var result = await new ClaimWork(store, new Clock(Now.AddDays(1)), new(), new()).ExecuteAsync(id, session, Token);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(["begin", "worker", "dispose"], store.Calls);
    }

    [Theory]
    [InlineData(29_999_999, ClaimWorkOutcome.Succeeded)]
    [InlineData(30_000_000, ClaimWorkOutcome.WorkerOffline)]
    [InlineData(30_000_001, ClaimWorkOutcome.WorkerOffline)]
    public async Task Liveness_UsesExactMicrosecondBoundary(long elapsedMicroseconds, ClaimWorkOutcome expected)
    {
        var store = new Store();
        Assert.Equal(expected, (await ExecuteAsync(store, Now.AddMicroseconds(elapsedMicroseconds))).Outcome);
        Assert.Equal(Now.UtcDateTime, store.Worker!.LastSeenAtUtc);
        if (expected == ClaimWorkOutcome.WorkerOffline) Assert.DoesNotContain("capacity", store.Calls);
    }

    [Fact]
    public async Task Liveness_UsesConfiguredThreshold()
    {
        var store = new Store();
        var result = await new ClaimWork(store, new Clock(Now.AddSeconds(10)), new() { OfflineAfterSeconds = 10 }, new())
            .ExecuteAsync(store.Worker!.Id, store.Worker.SessionId!.Value, Token);
        Assert.Equal(ClaimWorkOutcome.WorkerOffline, result.Outcome);
    }

    [Fact]
    public async Task InvalidIdentities_FailBeforeTransaction()
    {
        var store = new Store();
        var useCase = new ClaimWork(store, new Clock(Now), new(), new());
        foreach (var invalid in new[] { Guid.Empty, Guid.NewGuid(), Guid.Parse("019ec569-5a00-7000-0000-000000000001") })
        {
            Assert.Equal(ClaimWorkOutcome.InvalidRequest, (await useCase.ExecuteAsync(invalid, Guid.CreateVersion7(), Token)).Outcome);
            Assert.Equal(ClaimWorkOutcome.InvalidRequest, (await useCase.ExecuteAsync(Guid.CreateVersion7(), invalid, Token)).Outcome);
        }

        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("worker")]
    [InlineData("capacity")]
    [InlineData("job")]
    [InlineData("commit")]
    public async Task ExceptionsAndCancellation_PropagateAtEveryAsyncBoundary(string operation)
    {
        foreach (var error in new Exception[] { new InvalidOperationException("test"), new OperationCanceledException(Token) })
        {
            var store = new Store { FailureAt = operation, Error = error };
            Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(store)));
            Assert.False(store.Committed);
            Assert.Equal(operation != "begin", store.Disposed);
            Assert.All(store.Tokens, token => Assert.Equal(Token, token));
        }
    }

    [Fact]
    public async Task AlreadyCancelledRequest_PropagatesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new Store();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaimWork(store, new Clock(Now), new(), new())
            .ExecuteAsync(store.Worker!.Id, store.Worker.SessionId!.Value, cancelled.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectInvalidDuration(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaimWorkOptions { LeaseDurationSeconds = seconds });

    private Task<ClaimWorkResult> ExecuteAsync(Store store, DateTimeOffset? now = null) =>
        new ClaimWork(store, new Clock(now ?? Now), new(), new()).ExecuteAsync(store.Worker!.Id, store.Worker.SessionId!.Value, Token);
    private static Job Job(string type = "test", DateTimeOffset? available = null) => new(Guid.CreateVersion7(), type, "{}", 0, 1, Now.UtcDateTime, (available ?? Now).UtcDateTime);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : IClaimWorkPersistence, IClaimWorkTransaction
    {
        public Worker? Worker { get; set; } = new(new(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 1, ["test"]), Now.UtcDateTime);
        public Job? Job { get; set; } = ClaimWorkTests.Job();
        public int ActiveLeases { get; set; }
        public JobAttempt? Attempt { get; private set; }
        public Lease? Lease { get; private set; }
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<DateTime> Times { get; } = [];
        public Action? OnLock { get; set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public string? FailureAt { get; init; }
        public Exception? Error { get; init; }
        public Task? CommitGate { get; set; }
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Call(string name, CancellationToken token)
        {
            Calls.Add(name);
            Tokens.Add(token);
            token.ThrowIfCancellationRequested();
            if (FailureAt == name) throw Error!;
        }
        public Task<IClaimWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            Call("begin", cancellationToken);
            return Task.FromResult<IClaimWorkTransaction>(this);
        }
        public Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken)
        {
            Call("worker", cancellationToken);
            Assert.Equal(Worker?.Id ?? workerId, workerId);
            OnLock?.Invoke();
            return Task.FromResult(Worker);
        }
        public Task<int> CountActiveLeasesAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken)
        {
            Call("capacity", cancellationToken);
            Assert.Equal(Worker!.Id, workerId);
            Times.Add(serverUtc);
            return Task.FromResult(ActiveLeases);
        }
        public Task<Job?> LockEligibleJobAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken)
        {
            Call("job", cancellationToken);
            Assert.Equal(Worker!.Id, workerId);
            Times.Add(serverUtc);
            return Task.FromResult(Job is not null && Job.AvailableAtUtc <= serverUtc
                && Worker.SupportedTypes.Any(type => StringComparer.Ordinal.Equals(type.Type, Job.Type)) ? Job : null);
        }
        public void Add(JobAttempt attempt, Lease lease) { Calls.Add("add"); Attempt = attempt; Lease = lease; }
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            Call("commit", cancellationToken);
            CommitEntered.SetResult();
            if (CommitGate is not null) await CommitGate.WaitAsync(cancellationToken);
            Committed = true;
        }
        public ValueTask DisposeAsync() { Calls.Add("dispose"); Disposed = true; return ValueTask.CompletedTask; }
    }
}
