namespace Synestra.Application.Jobs;

public interface IGetJobPersistence
{
    Task<JobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
