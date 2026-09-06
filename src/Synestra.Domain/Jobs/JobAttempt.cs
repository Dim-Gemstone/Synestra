using Synestra.Domain.Leases;

namespace Synestra.Domain.Jobs;

public sealed class JobAttempt
{
    private JobAttempt()
    {
    }

    internal JobAttempt(Guid jobId, int number, DateTime startedAtUtc)
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
    public string? Result { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public Lease? Lease { get; private set; }

    internal void Succeed(string result, DateTime finishedAtUtc)
    {
        ValidateCompletion(finishedAtUtc);
        DomainValidation.RequiredText(result, int.MaxValue, nameof(result));
        Result = result;
        Status = JobAttemptStatus.Succeeded;
        FinishedAtUtc = finishedAtUtc;
    }

    internal void Fail(string code, string message, DateTime finishedAtUtc)
    {
        ValidateCompletion(finishedAtUtc);
        DomainValidation.RequiredText(code, 100, nameof(code));
        DomainValidation.RequiredText(message, 2000, nameof(message));
        if (code.Contains('\0') || message.Contains('\0'))
        {
            throw new ArgumentException("Error text cannot contain NUL.");
        }

        ErrorCode = code;
        ErrorMessage = message;
        Status = JobAttemptStatus.Failed;
        FinishedAtUtc = finishedAtUtc;
    }

    private void ValidateCompletion(DateTime finishedAtUtc)
    {
        DomainValidation.Utc(finishedAtUtc, nameof(finishedAtUtc));
        if (Status != JobAttemptStatus.Running || FinishedAtUtc is not null
            || Result is not null || ErrorCode is not null || ErrorMessage is not null)
        {
            throw new InvalidOperationException("Only a running, incomplete attempt can complete.");
        }

        if (finishedAtUtc < StartedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(finishedAtUtc), "Completion cannot precede the attempt start.");
        }
    }
}
