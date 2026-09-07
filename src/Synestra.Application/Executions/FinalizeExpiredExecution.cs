using Microsoft.Extensions.Logging;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;

namespace Synestra.Application.Executions;

public enum FinalizeExpiredExecutionOutcome { Finalized, NotEligible, Missing, Busy, Inconsistent }

public sealed class FinalizeExpiredExecution(
    IFinalizeExpiredExecutionPersistence persistence, TimeProvider timeProvider,
    ILogger<FinalizeExpiredExecution> logger)
{
    public async Task<FinalizeExpiredExecutionOutcome> ExecuteAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var locked = await transaction.TryLockExecutionAsync(leaseId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        switch (locked.Outcome)
        {
            case FinalizationLockOutcome.Missing: return FinalizeExpiredExecutionOutcome.Missing;
            case FinalizationLockOutcome.Busy: return FinalizeExpiredExecutionOutcome.Busy;
            case FinalizationLockOutcome.Inconsistent: return Inconsistent(leaseId);
            case FinalizationLockOutcome.Locked: break;
            default: throw new InvalidOperationException("Unknown finalization lock outcome.");
        }

        var execution = locked.Execution ?? throw new InvalidOperationException("Locked execution is missing.");
        var (job, attempt, lease, hasCompletion) = execution;
        if (job.Status != JobStatus.Running || job.CompletedAtUtc is not null
            || attempt.Status != JobAttemptStatus.Running || attempt.FinishedAtUtc is not null
            || attempt.Result is not null || attempt.ErrorCode is not null || attempt.ErrorMessage is not null
            || lease.ReleasedAtUtc is not null || hasCompletion)
            return FinalizeExpiredExecutionOutcome.NotEligible;

        var now = WorkerTime.UtcNow(timeProvider);
        if (lease.ExpiresAtUtc > now) return FinalizeExpiredExecutionOutcome.NotEligible;
        if (lease.ExpiresAtUtc <= lease.AcquiredAtUtc || attempt.StartedAtUtc < job.CreatedAtUtc
            || now < lease.AcquiredAtUtc || now < attempt.StartedAtUtc || now < job.CreatedAtUtc)
            return Inconsistent(leaseId);

        job.AbandonAttempt(attempt, now);
        lease.Release(now);
        await transaction.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FinalizeExpiredExecutionOutcome.Finalized;
    }

    private FinalizeExpiredExecutionOutcome Inconsistent(Guid leaseId)
    {
        logger.LogWarning("Skipping inconsistent execution for Lease {LeaseId} during loss finalization.", leaseId);
        return FinalizeExpiredExecutionOutcome.Inconsistent;
    }
}
