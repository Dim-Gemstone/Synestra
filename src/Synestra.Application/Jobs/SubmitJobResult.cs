using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public enum SubmitJobOutcome
{
    Succeeded,
    InvalidRequest,
    PayloadTooLarge,
    DefinitionNotFound,
    DefinitionDisabled
}

public sealed record SubmitJobResult(SubmitJobOutcome Outcome, Job? Job = null, string? Error = null);
