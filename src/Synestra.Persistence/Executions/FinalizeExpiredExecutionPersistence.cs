using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Synestra.Application.Executions;

namespace Synestra.Persistence.Executions;

internal sealed class FinalizeExpiredExecutionPersistence(SynestraDbContext dbContext) : IFinalizeExpiredExecutionPersistence
{
    public async Task<IFinalizeExpiredExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new FinalizationTransaction(dbContext,
            await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken));

    private sealed class FinalizationTransaction(SynestraDbContext dbContext, IDbContextTransaction transaction)
        : IFinalizeExpiredExecutionTransaction
    {
        private readonly List<object> _tracked = [];

        public async Task<FinalizationLockResult> TryLockExecutionAsync(Guid leaseId, CancellationToken cancellationToken)
        {
            var locator = await dbContext.Leases.AsNoTracking().Where(lease => lease.Id == leaseId)
                .Select(lease => new
                {
                    lease.WorkerId, AttemptId = lease.JobAttemptId,
                    JobId = dbContext.JobAttempts.Where(attempt => attempt.Id == lease.JobAttemptId)
                        .Select(attempt => (Guid?)attempt.JobId).SingleOrDefault()
                }).SingleOrDefaultAsync(cancellationToken);
            if (locator is null) return new(FinalizationLockOutcome.Missing);
            if (locator.JobId is null) return new(FinalizationLockOutcome.Inconsistent);

            var worker = await dbContext.Workers
                .FromSqlInterpolated($"SELECT * FROM workers WHERE id = {locator.WorkerId} FOR UPDATE SKIP LOCKED")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (worker is null)
                return new(await dbContext.Workers.AnyAsync(row => row.Id == locator.WorkerId, cancellationToken)
                    ? FinalizationLockOutcome.Busy : FinalizationLockOutcome.Inconsistent);

            var job = await dbContext.Jobs
                .FromSqlInterpolated($"SELECT * FROM jobs WHERE id = {locator.JobId} FOR UPDATE SKIP LOCKED")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (job is null)
                return new(await dbContext.Jobs.AnyAsync(row => row.Id == locator.JobId, cancellationToken)
                    ? FinalizationLockOutcome.Busy : FinalizationLockOutcome.Inconsistent);

            var attemptRow = await dbContext.JobAttempts
                .FromSqlInterpolated($"SELECT * FROM job_attempts WHERE id = {locator.AttemptId} FOR UPDATE SKIP LOCKED")
                .AsNoTracking().Select(attempt => new
                {
                    Attempt = attempt,
                    HasCompletion = EF.Property<Guid?>(attempt, "CompletionReportId") != null
                        || EF.Property<string?>(attempt, "CompletionSnapshot") != null
                }).SingleOrDefaultAsync(cancellationToken);
            if (attemptRow is null)
                return new(await dbContext.JobAttempts.AnyAsync(row => row.Id == locator.AttemptId, cancellationToken)
                    ? FinalizationLockOutcome.Busy : FinalizationLockOutcome.Inconsistent);

            var lease = await dbContext.Leases
                .FromSqlInterpolated($"SELECT * FROM leases WHERE id = {leaseId} FOR UPDATE SKIP LOCKED")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (lease is null)
                return new(await dbContext.Leases.AnyAsync(row => row.Id == leaseId, cancellationToken)
                    ? FinalizationLockOutcome.Busy : FinalizationLockOutcome.Missing);

            var attempt = attemptRow.Attempt;
            if (lease.WorkerId != worker.Id || lease.JobAttemptId != attempt.Id || attempt.JobId != job.Id)
                return new(FinalizationLockOutcome.Inconsistent);

            // Attach only fresh locked rows. Shadow completion/token values are never written here.
            Track(job);
            Track(attempt);
            Track(lease);
            return new(FinalizationLockOutcome.Locked, new(job, attempt, lease, attemptRow.HasCompletion));
        }

        private void Track(object entity)
        {
            dbContext.Attach(entity);
            _tracked.Add(entity);
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
                // A failed commit can leave successfully saved terminal entities marked Unchanged.
                foreach (var entity in _tracked.AsEnumerable().Reverse())
                    dbContext.Entry(entity).State = EntityState.Detached;
            }
        }
    }
}
