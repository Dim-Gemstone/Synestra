using Microsoft.EntityFrameworkCore;
using Synestra.Application.Jobs;

namespace Synestra.Persistence.Jobs;

internal sealed class GetJobPersistence(SynestraDbContext dbContext) : IGetJobPersistence
{
    public Task<JobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => new JobDetails(
                job.Id,
                job.Type,
                job.Status,
                job.Priority,
                job.MaxAttempts,
                job.CreatedAtUtc,
                job.AvailableAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
