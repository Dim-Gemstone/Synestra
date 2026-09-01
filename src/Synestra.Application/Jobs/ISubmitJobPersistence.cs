using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public interface ISubmitJobPersistence
{
    Task<ISubmitJobTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ISubmitJobTransaction : IAsyncDisposable
{
    Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken);
    void Add(Job job);
    Task CommitAsync(CancellationToken cancellationToken);
}
