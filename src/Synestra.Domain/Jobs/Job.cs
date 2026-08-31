namespace Synestra.Domain.Jobs;

public sealed class Job
{
    private readonly List<JobAttempt> _attempts = [];

    private Job()
    {
    }

    public Job(
        Guid jobDefinitionId,
        string type,
        string payload,
        int priority,
        int maxAttempts,
        DateTime createdAtUtc,
        DateTime availableAtUtc)
    {
        if (jobDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Job definition ID cannot be empty.", nameof(jobDefinitionId));
        }

        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Maximum attempts must be greater than zero.");
        }

        Id = Guid.CreateVersion7();
        JobDefinitionId = jobDefinitionId;
        Type = DomainValidation.RequiredText(type, 100, nameof(type));
        Payload = DomainValidation.RequiredText(payload, int.MaxValue, nameof(payload));
        Priority = priority;
        MaxAttempts = maxAttempts;
        Status = JobStatus.Pending;
        CreatedAtUtc = DomainValidation.Utc(createdAtUtc, nameof(createdAtUtc));
        AvailableAtUtc = DomainValidation.Utc(availableAtUtc, nameof(availableAtUtc));
    }

    public Guid Id { get; private set; }
    public Guid JobDefinitionId { get; private set; }
    public string Type { get; private set; } = null!;
    public JobStatus Status { get; private set; }
    public int Priority { get; private set; }
    public string Payload { get; private set; } = null!;
    public int MaxAttempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime AvailableAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public IReadOnlyCollection<JobAttempt> Attempts => _attempts.AsReadOnly();
}
