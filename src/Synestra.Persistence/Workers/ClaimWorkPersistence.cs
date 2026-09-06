using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Workers;

internal sealed class ClaimWorkPersistence(SynestraDbContext dbContext) : IClaimWorkPersistence
{
    public async Task<IClaimWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        return new ClaimWorkTransaction(dbContext, transaction);
    }

    private sealed class ClaimWorkTransaction(SynestraDbContext dbContext, IDbContextTransaction transaction) : IClaimWorkTransaction
    {
        private Job? _job;
        private Lease? _lease;

        public Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken) =>
            dbContext.Workers
                .FromSqlInterpolated($"SELECT * FROM workers WHERE id = {workerId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        public Task<int> CountActiveLeasesAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken) =>
            dbContext.Leases.CountAsync(lease => lease.WorkerId == workerId
                && lease.ReleasedAtUtc == null && lease.ExpiresAtUtc > serverUtc, cancellationToken);

        public async Task<Job?> LockEligibleJobAsync(Guid workerId, DateTime serverUtc, CancellationToken cancellationToken)
        {
            _job = await dbContext.Jobs.FromSqlInterpolated($"""
                SELECT j.* FROM jobs AS j
                WHERE j.status = 'Pending' AND j.available_at_utc <= {serverUtc}
                  AND EXISTS (
                    SELECT 1 FROM worker_supported_types AS t
                    WHERE t.worker_id = {workerId} AND t.type = j.type COLLATE "C")
                ORDER BY j.priority DESC, j.available_at_utc ASC, j.created_at_utc ASC, j.id ASC
                LIMIT 1 FOR UPDATE OF j SKIP LOCKED
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);

            if (_job is not null)
            {
                dbContext.Attach(_job);
                await dbContext.Entry(_job).Collection(job => job.Attempts).LoadAsync(cancellationToken);
            }

            return _job;
        }

        public void Add(JobAttempt attempt, Lease lease)
        {
            _lease = lease;
            dbContext.JobAttempts.Add(attempt);
            dbContext.Leases.Add(lease);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                if (_lease is not null)
                {
                    dbContext.Entry(_lease).State = EntityState.Detached;
                }

                if (_job is not null)
                {
                    // Detach even after a successful save followed by a failed commit.
                    foreach (var attempt in _job.Attempts.ToArray())
                    {
                        dbContext.Entry(attempt).State = EntityState.Detached;
                    }

                    dbContext.Entry(_job).State = EntityState.Detached;
                }
            }
        }
    }
}
