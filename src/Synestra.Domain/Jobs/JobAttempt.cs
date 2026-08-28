using Synestra.Domain.Leases;

namespace Synestra.Domain.Jobs;

public sealed class JobAttempt
{
    private JobAttempt()
    {
    }

    public JobAttempt(Guid jobId, int number, DateTime startedAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id cannot be empty.", nameof(jobId));
        }

        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Attempt number must be greater than zero.");
        }

        Id = Guid.CreateVersion7();
        JobId = jobId;
        Number = number;
        Status = JobAttemptStatus.Running;
        StartedAtUtc = DomainValidation.Utc(startedAtUtc, nameof(startedAtUtc));
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public int Number { get; private set; }
    public JobAttemptStatus Status { get; private set; }
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public Lease? Lease { get; private set; }
}
