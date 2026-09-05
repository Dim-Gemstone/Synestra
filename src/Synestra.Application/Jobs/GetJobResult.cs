using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public enum GetJobOutcome
{
    Succeeded,
    NotFound
}

public sealed record GetJobResult(GetJobOutcome Outcome, JobDetails? Job = null);

public sealed record JobDetails(
    Guid Id,
    string Type,
    JobStatus Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc);
