using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public interface IRenewLeasePersistence
{
    Task<IRenewLeaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IRenewLeaseTransaction : IAsyncDisposable
{
    Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken);
    Task<LeaseExecution?> LockExecutionAsync(Guid leaseId, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}
