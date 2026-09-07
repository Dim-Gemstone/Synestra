using Microsoft.EntityFrameworkCore;
using Synestra.Application.Executions;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Executions;

internal sealed class ExpiredExecutionDiscovery(SynestraDbContext dbContext) : IExpiredExecutionDiscovery
{
    public async Task<IReadOnlyList<ExpiredExecutionCursor>> FindAsync(
        DateTime cutoffUtc, ExpiredExecutionCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (cutoffUtc.Kind != DateTimeKind.Utc || after is not null && after.ExpiresAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Discovery times must use UTC.");

        var leases = after is null
            ? dbContext.Leases.Where(lease => lease.ReleasedAtUtc == null && lease.ExpiresAtUtc <= cutoffUtc)
            : dbContext.Leases.FromSqlInterpolated($"""
                SELECT * FROM leases
                WHERE released_at_utc IS NULL AND expires_at_utc <= {cutoffUtc}
                  AND (expires_at_utc, id) > ({after.ExpiresAtUtc}, {after.LeaseId})
                """);

        return await (from lease in leases.AsNoTracking()
                      join attempt in dbContext.JobAttempts on lease.JobAttemptId equals attempt.Id
                      join job in dbContext.Jobs on attempt.JobId equals job.Id
                      join worker in dbContext.Workers on lease.WorkerId equals worker.Id
                      where job.Status == JobStatus.Running && job.CompletedAtUtc == null
                          && attempt.Status == JobAttemptStatus.Running && attempt.FinishedAtUtc == null
                          && attempt.Result == null && attempt.ErrorCode == null && attempt.ErrorMessage == null
                          && EF.Property<Guid?>(attempt, "CompletionReportId") == null
                          && EF.Property<string?>(attempt, "CompletionSnapshot") == null
                          && lease.ExpiresAtUtc > lease.AcquiredAtUtc
                          && attempt.StartedAtUtc >= job.CreatedAtUtc && attempt.StartedAtUtc <= cutoffUtc
                      orderby lease.ExpiresAtUtc, lease.Id
                      select new ExpiredExecutionCursor(lease.ExpiresAtUtc, lease.Id))
            .Take(limit).ToListAsync(cancellationToken);
    }
}
