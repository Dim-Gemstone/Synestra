using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public interface IClaimWorkPersistence
{
    Task<IClaimWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IClaimWorkTransaction : IAsyncDisposable
{
    Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken);
    Task<int> CountActiveLeasesAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken);
    // Returns a locked eligible Job with its complete attempt history for domain numbering.
    Task<Job?> LockEligibleJobAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken);
    void Add(JobAttempt attempt, Lease lease);
    Task CommitAsync(CancellationToken cancellationToken);
}
