namespace Synestra.Application.Workers;

public sealed class RenewLease(IRenewLeasePersistence persistence, TimeProvider timeProvider, ClaimWorkOptions options)
{
    public async Task<RenewLeaseResult> ExecuteAsync(
        Guid workerId, Guid sessionId, Guid leaseId, string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = ExecutionIdentity.Validate(workerId, sessionId, leaseId, token);
        if (hash is null) return new(ExecutionOutcome.InvalidRequest);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var worker = await transaction.LockWorkerAsync(workerId, cancellationToken);
        if (worker is null) return new(ExecutionOutcome.WorkerNotFound);
        if (worker.SessionId != sessionId) return new(ExecutionOutcome.SessionReplaced);

        var execution = await transaction.LockExecutionAsync(leaseId, cancellationToken);
        if (execution is null) return new(ExecutionOutcome.LeaseNotFound);
        if (!execution.IsOwnedBy(workerId, sessionId, leaseId, hash)) return new(ExecutionOutcome.OwnershipLost);
        if (!execution.IsRunning) return new(ExecutionOutcome.LeaseNotActive);

        var now = WorkerTime.UtcNow(timeProvider);
        if (execution.Lease.ExpiresAtUtc <= now) return new(ExecutionOutcome.LeaseExpired);
        execution.Lease.Renew(now, TimeSpan.FromSeconds(options.LeaseDurationSeconds));
        await transaction.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(ExecutionOutcome.Succeeded, new(leaseId, execution.Lease.ExpiresAtUtc));
    }
}
