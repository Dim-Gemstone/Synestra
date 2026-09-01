using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Jobs;

internal sealed class SubmitJobPersistence(SynestraDbContext dbContext) : ISubmitJobPersistence
{
    public async Task<ISubmitJobTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        return new SubmitJobTransaction(dbContext, transaction);
    }

    private sealed class SubmitJobTransaction(SynestraDbContext dbContext, IDbContextTransaction transaction)
        : ISubmitJobTransaction
    {
        public Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken)
        {
            return dbContext.JobDefinitions
                .FromSqlInterpolated($"SELECT * FROM job_definitions WHERE type = {type} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        public void Add(Job job) => dbContext.Jobs.Add(job);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
