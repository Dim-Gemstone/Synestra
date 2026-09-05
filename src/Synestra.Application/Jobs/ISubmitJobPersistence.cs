using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public interface ISubmitJobPersistence
{
    Task<ISubmitJobTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ISubmitJobTransaction : IAsyncDisposable
{
    // Serializes this key until commit or rollback and reads only committed submissions.
    Task<JobSubmission?> LockIdempotencyKeyAsync(string key, CancellationToken cancellationToken);
    void AddSubmission(string key, JobSubmission submission);
    Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken);
    void Add(Job job);
    Task CommitAsync(CancellationToken cancellationToken);
}
