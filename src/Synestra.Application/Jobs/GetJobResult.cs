using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public enum GetJobOutcome
{
    Succeeded,
    NotFound
}

public sealed record GetJobResult(GetJobOutcome Outcome, GetJobDetails? Job = null);

public sealed record GetJobDetails(
    Guid Id,
    string Type,
    JobStatus Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc,
    DateTime? CompletedAtUtc,
    JobCompletionDetails? Completion);

public sealed record JobCompletionDetails(
    Guid AttemptId,
    int AttemptNumber,
    JobAttemptStatus Outcome,
    string? Result,
    JobCompletionError? Error);

public sealed record JobCompletionError(string Code, string Message);
