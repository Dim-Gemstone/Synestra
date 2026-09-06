using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public interface IReportExecutionCompletionPersistence
{
    Task<IReportExecutionCompletionTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IReportExecutionCompletionTransaction : IAsyncDisposable
{
    Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken);
    Task<LeaseExecution?> LockExecutionAsync(Guid leaseId, CancellationToken cancellationToken);
    void RecordCompletion(ExecutionCompletionSnapshot snapshot);
    Task CommitAsync(CancellationToken cancellationToken);
}
