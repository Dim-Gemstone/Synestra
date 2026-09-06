using Synestra.Domain.Workers;

namespace Synestra.Domain.Leases;

public sealed class Lease
{
    private Lease()
    {
    }

    public Lease(Guid jobAttemptId, Guid workerId, Guid sessionId, DateTime acquiredAtUtc, TimeSpan duration)
    {
        if (jobAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Job attempt id cannot be empty.", nameof(jobAttemptId));
        }

        WorkerRegistration.ValidateIdentity(workerId, nameof(workerId));
        WorkerRegistration.ValidateIdentity(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        Id = Guid.CreateVersion7();
        JobAttemptId = jobAttemptId;
        WorkerId = workerId;
        SessionId = sessionId;
        AcquiredAtUtc = DomainValidation.Utc(acquiredAtUtc, nameof(acquiredAtUtc));
        ExpiresAtUtc = AcquiredAtUtc.Add(duration);
    }

    public Guid Id { get; private set; }
    public Guid JobAttemptId { get; private set; }
    public Guid WorkerId { get; private set; }
    public Guid? SessionId { get; private set; }
    public DateTime AcquiredAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }
}
