using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public interface IWorkerPersistence
{
    Task<IWorkerTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IWorkerTransaction : IAsyncDisposable
{
    // Serializes registration, including first insertion, until the transaction ends.
    Task<Worker?> LockRegistrationAsync(Guid workerId, CancellationToken cancellationToken);
    Task<Worker?> FindForHeartbeatAsync(Guid workerId, CancellationToken cancellationToken);
    Task<bool> HasRegisteredSessionAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken);
    void AddSession(Guid workerId, Guid sessionId);
    void Add(Worker worker);
    Task CommitAsync(CancellationToken cancellationToken);
}
