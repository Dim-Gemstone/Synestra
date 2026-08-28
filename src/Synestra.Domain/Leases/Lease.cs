namespace Synestra.Domain.Leases;

public sealed class Lease
{
    private Lease()
    {
    }

    public Lease(Guid jobAttemptId, Guid workerId, DateTime acquiredAtUtc, DateTime expiresAtUtc)
    {
        if (jobAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Job attempt id cannot be empty.", nameof(jobAttemptId));
        }

        if (workerId == Guid.Empty)
        {
            throw new ArgumentException("Worker id cannot be empty.", nameof(workerId));
        }

        Id = Guid.CreateVersion7();
        JobAttemptId = jobAttemptId;
        WorkerId = workerId;
        AcquiredAtUtc = DomainValidation.Utc(acquiredAtUtc, nameof(acquiredAtUtc));
        ExpiresAtUtc = DomainValidation.Utc(expiresAtUtc, nameof(expiresAtUtc));

        if (ExpiresAtUtc <= AcquiredAtUtc)
        {
            throw new ArgumentException("Lease expiration must be later than acquisition.", nameof(expiresAtUtc));
        }
    }

    public Guid Id { get; private set; }
    public Guid JobAttemptId { get; private set; }
    public Guid WorkerId { get; private set; }
    public DateTime AcquiredAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }
}
