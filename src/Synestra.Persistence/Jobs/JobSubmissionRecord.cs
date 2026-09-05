using Synestra.Application.Jobs;

namespace Synestra.Persistence.Jobs;

internal sealed class JobSubmissionRecord
{
    public required string Key { get; init; }
    public required Guid JobId { get; init; }
    public required SubmitJobIdentity Identity { get; init; }
    public required JobDetails Response { get; init; }
}
