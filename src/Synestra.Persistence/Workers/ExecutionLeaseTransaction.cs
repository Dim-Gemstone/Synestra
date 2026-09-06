using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Synestra.Application.Workers;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Workers;

// These two use cases share the same concrete row-lock protocol, including future finalization.
internal sealed class ExecutionLeaseTransaction(SynestraDbContext dbContext, IDbContextTransaction transaction)
    : IRenewLeaseTransaction, IReportExecutionCompletionTransaction
{
    private readonly List<object> _tracked = [];
    private LeaseExecution? _execution;

    public Task<Worker?> LockWorkerAsync(Guid workerId, CancellationToken cancellationToken) =>
        dbContext.Workers.FromSqlInterpolated($"SELECT * FROM workers WHERE id = {workerId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    public async Task<LeaseExecution?> LockExecutionAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var locator = await (from lease in dbContext.Leases.AsNoTracking()
                             join attempt in dbContext.JobAttempts on lease.JobAttemptId equals attempt.Id
                             where lease.Id == leaseId
                             select new { attempt.JobId, AttemptId = attempt.Id }).SingleOrDefaultAsync(cancellationToken);
        if (locator is null) return null;

        var job = await dbContext.Jobs.FromSqlInterpolated($"SELECT * FROM jobs WHERE id = {locator.JobId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var attemptRow = await dbContext.JobAttempts
            .FromSqlInterpolated($"SELECT * FROM job_attempts WHERE id = {locator.AttemptId} FOR UPDATE")
            .AsNoTracking().Select(attempt => new
            {
                Attempt = attempt,
                ReportId = EF.Property<Guid?>(attempt, "CompletionReportId"),
                Snapshot = EF.Property<string?>(attempt, "CompletionSnapshot")
            }).SingleOrDefaultAsync(cancellationToken);
        var leaseRow = await dbContext.Leases.FromSqlInterpolated($"SELECT * FROM leases WHERE id = {leaseId} FOR UPDATE")
            .AsNoTracking().Select(lease => new { Lease = lease, Hash = EF.Property<byte[]?>(lease, "TokenHash") })
            .SingleOrDefaultAsync(cancellationToken);
        if (leaseRow is null) return null;

        var matches = job is not null && attemptRow is not null
            && attemptRow.Attempt.JobId == locator.JobId && leaseRow.Lease.JobAttemptId == locator.AttemptId;
        var snapshot = attemptRow?.Snapshot is { } json
            ? JsonSerializer.Deserialize<ExecutionCompletionSnapshot>(json)
                ?? throw new InvalidOperationException("Missing persisted completion snapshot.")
            : null;
        if (matches)
        {
            Track(job!);
            Track(attemptRow!.Attempt);
            Track(leaseRow.Lease);
            dbContext.Entry(attemptRow.Attempt).Property<Guid?>("CompletionReportId").CurrentValue = attemptRow.ReportId;
            dbContext.Entry(attemptRow.Attempt).Property<string?>("CompletionSnapshot").CurrentValue = attemptRow.Snapshot;
            dbContext.Entry(leaseRow.Lease).Property<byte[]?>("TokenHash").CurrentValue = leaseRow.Hash;
            foreach (var entity in _tracked) dbContext.Entry(entity).State = EntityState.Unchanged;
        }

        _execution = new(job, attemptRow?.Attempt, leaseRow.Lease, leaseRow.Hash, snapshot, matches);
        return _execution;
    }

    public void RecordCompletion(ExecutionCompletionSnapshot snapshot)
    {
        if (_execution?.Job?.Id != snapshot.JobId || _execution.Attempt?.Id != snapshot.AttemptId
            || _execution.Lease.Id != snapshot.LeaseId)
            throw new InvalidOperationException("Completion must identify the locked execution.");
        var entry = dbContext.Entry(_execution.Attempt!);
        entry.Property<Guid?>("CompletionReportId").CurrentValue = snapshot.ReportId;
        entry.Property<string?>("CompletionSnapshot").CurrentValue = JsonSerializer.Serialize(snapshot);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void Track(object entity)
    {
        dbContext.Attach(entity);
        _tracked.Add(entity);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await transaction.DisposeAsync();
        }
        finally
        {
            // SaveChanges may succeed while commit fails: discard even Unchanged terminal entities.
            foreach (var entity in _tracked.AsEnumerable().Reverse())
                dbContext.Entry(entity).State = EntityState.Detached;
        }
    }
}
