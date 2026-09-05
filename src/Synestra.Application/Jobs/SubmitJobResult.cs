namespace Synestra.Application.Jobs;

public enum SubmitJobOutcome
{
    Succeeded,
    InvalidRequest,
    PayloadTooLarge,
    DefinitionNotFound,
    DefinitionDisabled,
    IdempotencyKeyConflict
}

public sealed record SubmitJobResult(SubmitJobOutcome Outcome, JobDetails? Job = null, string? Error = null);
