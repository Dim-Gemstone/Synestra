using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public sealed class ClaimWork(
    IClaimWorkPersistence persistence, TimeProvider timeProvider,
    WorkerLivenessOptions livenessOptions, ClaimWorkOptions options)
{
    public async Task<ClaimWorkResult> ExecuteAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            WorkerRegistration.ValidateIdentity(workerId, nameof(workerId));
            WorkerRegistration.ValidateIdentity(sessionId, nameof(sessionId));
        }
        catch (ArgumentException)
        {
            return new(ClaimWorkOutcome.InvalidRequest);
        }

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var worker = await transaction.LockWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            return new(ClaimWorkOutcome.WorkerNotFound);
        }

        if (worker.SessionId != sessionId)
        {
            return new(ClaimWorkOutcome.SessionReplaced);
        }

        var now = WorkerTime.UtcNow(timeProvider);
        if (livenessOptions.IsOffline(worker.LastSeenAtUtc, now))
        {
            return new(ClaimWorkOutcome.WorkerOffline);
        }

        if (await transaction.CountActiveLeasesAsync(workerId, now, cancellationToken) >= worker.Capacity)
        {
            return new(ClaimWorkOutcome.NoWork);
        }

        var job = await transaction.LockEligibleJobAsync(workerId, now, cancellationToken);
        if (job is null)
        {
            return new(ClaimWorkOutcome.NoWork);
        }

        var attempt = job.StartAttempt(now);
        var lease = new Lease(attempt.Id, workerId, sessionId, now, TimeSpan.FromSeconds(options.LeaseDurationSeconds));
        transaction.Add(attempt, lease);
        await transaction.CommitAsync(cancellationToken);
        return new(ClaimWorkOutcome.Succeeded, new ClaimedWork(
            job.Id, attempt.Id, lease.Id, attempt.Number, job.Type, job.Payload,
            lease.AcquiredAtUtc, lease.ExpiresAtUtc));
    }
}
