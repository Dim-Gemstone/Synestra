using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
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
        private readonly List<object> _addedEntities = [];
        private bool _committed;

        public async Task<JobSubmission?> LockIdempotencyKeyAsync(string key, CancellationToken cancellationToken)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("synestra:submit-job:" + key));
            var lockId = BinaryPrimitives.ReadInt64BigEndian(hash);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockId})", cancellationToken);

            // A separate READ COMMITTED statement sees a winner that committed while we waited.
            var record = await dbContext.Set<JobSubmissionRecord>().AsNoTracking()
                .SingleOrDefaultAsync(submission => submission.Key == key, cancellationToken);
            return record is null ? null : new JobSubmission(record.Identity, record.Response);
        }

        public void AddSubmission(string key, JobSubmission submission)
        {
            var record = new JobSubmissionRecord
            {
                Key = key,
                JobId = submission.Job.Id,
                Identity = submission.Identity,
                Response = submission.Job
            };
            dbContext.Set<JobSubmissionRecord>().Add(record);
            _addedEntities.Add(record);
        }

        public Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken)
        {
            return dbContext.JobDefinitions
                .FromSqlInterpolated($"SELECT * FROM job_definitions WHERE type = {type} FOR SHARE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        public void Add(Job job)
        {
            dbContext.Jobs.Add(job);
            _addedEntities.Add(job);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                if (!_committed)
                {
                    // A later submission in this scope must not save a rolled-back insert.
                    foreach (var entity in _addedEntities)
                    {
                        dbContext.Entry(entity).State = EntityState.Detached;
                    }
                }
            }
        }
    }
}
