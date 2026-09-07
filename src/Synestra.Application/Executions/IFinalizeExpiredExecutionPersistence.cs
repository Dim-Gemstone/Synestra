using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;

namespace Synestra.Application.Executions;

public interface IFinalizeExpiredExecutionPersistence
{
    Task<IFinalizeExpiredExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IFinalizeExpiredExecutionTransaction : IAsyncDisposable
{
    Task<FinalizationLockResult> TryLockExecutionAsync(Guid leaseId, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

public enum FinalizationLockOutcome { Locked, Missing, Busy, Inconsistent }

public sealed record FinalizationLockResult(FinalizationLockOutcome Outcome, ExpiredExecution? Execution = null);

// Only supplied after ordered locks and locator relationship revalidation. No Worker credentials.
public sealed record ExpiredExecution(Job Job, JobAttempt Attempt, Lease Lease, bool HasCompletion);
