namespace Synestra.Application.Jobs;

public interface IGetJobPersistence
{
    Task<GetJobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
