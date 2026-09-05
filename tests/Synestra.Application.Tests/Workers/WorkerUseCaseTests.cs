using Synestra.Application.Workers;
using Synestra.Domain.Workers;
using Xunit;

namespace Synestra.Application.Tests.Workers;

public sealed class WorkerUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_CreatesUpdatesAndReplacesUsingTimeAfterLockAndPropagatesCancellation()
    {
        var store = new MemoryPersistence();
        var clock = new Clock(Now.AddTicks(9));
        var useCase = new RegisterWorker(store, clock, new WorkerLivenessOptions { HeartbeatIntervalSeconds = 5, OfflineAfterSeconds = 20 });
        var request = Request();
        var first = await useCase.ExecuteAsync(request, Token);
        Assert.Equal(RegisterWorkerOutcome.Succeeded, first.Outcome);
        Assert.Equal(request.WorkerId, first.Worker!.WorkerId);
        Assert.Equal(Now.UtcDateTime, first.Worker.RegisteredAtUtc);
        Assert.Equal(5, first.Worker.HeartbeatIntervalSeconds);
        Assert.Equal(20, first.Worker.OfflineAfterSeconds);
        Assert.Equal(["B", "a"], first.Worker.SupportedTypes);
        Assert.True(store.Committed);

        store.OnLock = () => clock.Now = Now.AddSeconds(10);
        var updated = await useCase.ExecuteAsync(request with { Name = "updated", Capacity = 2, SupportedTypes = ["c"] }, Token);
        Assert.Equal("updated", updated.Worker!.Name);
        Assert.Equal(2, updated.Worker.Capacity);
        Assert.Equal(["c"], updated.Worker.SupportedTypes);
        Assert.Equal(Now.UtcDateTime, updated.Worker.SessionStartedAtUtc);
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, updated.Worker.LastSeenAtUtc);
        store.OnLock = () => clock.Now = Now.AddSeconds(20);
        var replacement = await useCase.ExecuteAsync(request with { SessionId = Guid.CreateVersion7() }, Token);
        Assert.NotEqual(request.SessionId, replacement.Worker!.SessionId);
        Assert.Equal(Now.AddSeconds(20).UtcDateTime, replacement.Worker.SessionStartedAtUtc);
        Assert.Equal(Now.UtcDateTime, replacement.Worker.RegisteredAtUtc);
        Assert.Equal(3, store.Disposals);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
        var beforeReplay = store.Worker;
        Assert.Equal(RegisterWorkerOutcome.SessionReplaced, (await useCase.ExecuteAsync(request, Token)).Outcome);
        Assert.Equal(replacement.Worker.SessionId, beforeReplay!.SessionId);
        Assert.Equal(2, store.Sessions.Count);
    }

    [Fact]
    public async Task InvalidRegistration_DoesNotStartTransaction()
    {
        var valid = Request();
        var invalid = new[]
        {
            valid with { WorkerId = Guid.Empty }, valid with { WorkerId = Guid.NewGuid() },
            valid with { SessionId = Guid.NewGuid() }, valid with { Name = null! },
            valid with { Name = " " }, valid with { Name = new string('n', 201) },
            valid with { Capacity = 0 }, valid with { Capacity = -1 },
            valid with { SupportedTypes = null! }, valid with { SupportedTypes = [] },
            valid with { SupportedTypes = ["a", "a"] }, valid with { SupportedTypes = [null!] },
            valid with { SupportedTypes = [new string('t', 101)] }, valid with { SupportedTypes = [" "] },
            valid with { SupportedTypes = Enumerable.Range(0, 101).Select(x => x.ToString()).ToArray() }
        };
        var store = new MemoryPersistence();
        var useCase = new RegisterWorker(store, new Clock(Now), new());
        foreach (var request in invalid)
            Assert.Equal(RegisterWorkerOutcome.InvalidRequest, (await useCase.ExecuteAsync(request, Token)).Outcome);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task Heartbeat_ReportsMissingInvalidAndReplacedSessionsAndUsesServerTime()
    {
        var store = new MemoryPersistence();
        var clock = new Clock(Now);
        var useCase = new RecordWorkerHeartbeat(store, clock);
        var request = Request();
        Assert.Equal(WorkerHeartbeatOutcome.InvalidRequest, await useCase.ExecuteAsync(Guid.Empty, request.SessionId, Token));
        Assert.Empty(store.Tokens);
        Assert.Equal(WorkerHeartbeatOutcome.InvalidRequest, await useCase.ExecuteAsync(request.WorkerId, Guid.NewGuid(), Token));
        Assert.Equal(WorkerHeartbeatOutcome.WorkerNotFound, await useCase.ExecuteAsync(request.WorkerId, request.SessionId, Token));
        store.Worker = new Worker(new(request.WorkerId, request.SessionId, request.Name, request.Capacity, request.SupportedTypes), Now.UtcDateTime);
        Assert.Equal(WorkerHeartbeatOutcome.SessionReplaced, await useCase.ExecuteAsync(request.WorkerId, Guid.CreateVersion7(), Token));
        Assert.False(store.Committed);
        store.OnLock = () => clock.Now = Now.AddSeconds(10);
        Assert.Equal(WorkerHeartbeatOutcome.Succeeded, await useCase.ExecuteAsync(request.WorkerId, request.SessionId, Token));
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, store.Worker.LastSeenAtUtc);
        Assert.Equal(request.Name, store.Worker.Name);
        Assert.Equal(request.Capacity, store.Worker.Capacity);
        Assert.Equal(2, store.Worker.SupportedTypes.Count);
        Assert.All(store.Tokens, token => Assert.Equal(Token, token));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void Liveness_DerivesOfflineAtExactThreshold(int elapsed, bool offline)
    {
        var options = new WorkerLivenessOptions();
        Assert.Equal(10, options.HeartbeatIntervalSeconds);
        Assert.Equal(30, options.OfflineAfterSeconds);
        Assert.Equal(offline, options.IsOffline(Now.UtcDateTime, Now.AddSeconds(elapsed).UtcDateTime));
    }

    [Fact]
    public async Task Cancellation_IsNotConvertedToAnOutcome()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new MemoryPersistence();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RegisterWorker(store, new Clock(Now), new()).ExecuteAsync(Request(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecordWorkerHeartbeat(store, new Clock(Now)).ExecuteAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), cancelled.Token));
    }

    private static RegisterWorkerRequest Request() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 4, ["a", "B"]);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryPersistence : IWorkerPersistence, IWorkerTransaction
    {
        public Worker? Worker { get; set; }
        public List<CancellationToken> Tokens { get; } = [];
        public bool Committed { get; private set; }
        public int Disposals { get; private set; }
        public Action? OnLock { get; set; }
        public HashSet<(Guid WorkerId, Guid SessionId)> Sessions { get; } = [];
        public Task<IWorkerTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tokens.Add(cancellationToken);
            return Task.FromResult<IWorkerTransaction>(this);
        }
        public Task<Worker?> LockRegistrationAsync(Guid workerId, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            OnLock?.Invoke();
            return Task.FromResult(Worker);
        }
        public Task<Worker?> FindForHeartbeatAsync(Guid workerId, CancellationToken cancellationToken) => LockRegistrationAsync(workerId, cancellationToken);
        public void Add(Worker worker) => Worker = worker;
        public Task<bool> HasRegisteredSessionAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult(Sessions.Contains((workerId, sessionId)));
        }
        public void AddSession(Guid workerId, Guid sessionId) => Sessions.Add((workerId, sessionId));
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            Committed = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
