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

        if (AvailableAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(availableAtUtc), "Availability time cannot precede creation time.");
        }
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

    public JobAttempt StartAttempt(DateTime startedAtUtc)
    {
        DomainValidation.Utc(startedAtUtc, nameof(startedAtUtc));
        if (Status != JobStatus.Pending || CompletedAtUtc is not null)
        {
            throw new InvalidOperationException("Only a pending, incomplete Job can start an attempt.");
        }

        if (startedAtUtc < AvailableAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(startedAtUtc), "An attempt cannot start before the Job is available.");
        }

        var number = _attempts.Count == 0 ? 1 : checked(_attempts.Max(attempt => attempt.Number) + 1);
        var attempt = new JobAttempt(Id, number, startedAtUtc);
        _attempts.Add(attempt);
        Status = JobStatus.Running;
        return attempt;
    }

    public void SucceedAttempt(JobAttempt attempt, string result, DateTime finishedAtUtc)
    {
        ValidateCompletion(attempt, finishedAtUtc);
        attempt.Succeed(result, finishedAtUtc);
        Status = JobStatus.Succeeded;
        CompletedAtUtc = finishedAtUtc;
    }

    public void FailAttempt(JobAttempt attempt, string code, string message, DateTime finishedAtUtc)
    {
        ValidateCompletion(attempt, finishedAtUtc);
        attempt.Fail(code, message, finishedAtUtc);
        Status = JobStatus.Failed;
        CompletedAtUtc = finishedAtUtc;
    }

    public void AbandonAttempt(JobAttempt attempt, DateTime finishedAtUtc)
    {
        ValidateCompletion(attempt, finishedAtUtc);
        attempt.Abandon(finishedAtUtc);
        Status = JobStatus.Failed;
        CompletedAtUtc = finishedAtUtc;
    }

    private void ValidateCompletion(JobAttempt attempt, DateTime finishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        DomainValidation.Utc(finishedAtUtc, nameof(finishedAtUtc));
        if (Status != JobStatus.Running || CompletedAtUtc is not null)
        {
            throw new InvalidOperationException("Only a running, incomplete Job can complete.");
        }

        if (attempt.JobId != Id || !_attempts.Contains(attempt))
        {
            throw new ArgumentException("The attempt must belong to this Job.", nameof(attempt));
        }

        if (finishedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(finishedAtUtc), "Completion cannot precede Job creation.");
        }
    }
}
