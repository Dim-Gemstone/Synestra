using Microsoft.Extensions.Logging;
using Synestra.Application.Workers;

namespace Synestra.Application.Executions;

public sealed record ExpiredExecutionSweepCursor(ExpiredExecutionCursor After, DateTime CutoffUtc);

public sealed record ExpiredExecutionSweepResult(
    int Inspected, int Finalized, int Skipped, int Failed, ExpiredExecutionSweepCursor? NextCursor);

public sealed class FinalizeExpiredExecutionSweep(
    IExpiredExecutionDiscovery discovery, FinalizeExpiredExecution finalizer,
    TimeProvider timeProvider, ILogger<FinalizeExpiredExecutionSweep> logger)
{
    public async Task<ExpiredExecutionSweepResult> ExecuteAsync(
        int batchSize, ExpiredExecutionSweepCursor? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (cursor is not null && (cursor.CutoffUtc.Kind != DateTimeKind.Utc || cursor.After.ExpiresAtUtc.Kind != DateTimeKind.Utc))
            throw new ArgumentException("The discovery cursor must use UTC.", nameof(cursor));

        // Freeze the traversal horizon so newly expired work cannot indefinitely postpone revisits.
        var cutoff = cursor?.CutoffUtc ?? WorkerTime.UtcNow(timeProvider);
        IReadOnlyList<ExpiredExecutionCursor> candidates;
        try
        {
            candidates = await discovery.FindAsync(cutoff, cursor?.After, batchSize, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("Expired execution discovery failed. Failure type: {FailureType}.", exception.GetType().Name);
            throw;
        }
        var finalized = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await finalizer.ExecuteAsync(candidate.LeaseId, cancellationToken);
                if (outcome == FinalizeExpiredExecutionOutcome.Finalized) finalized++;
                else skipped++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Exception messages/objects can carry database values or execution data.
                logger.LogWarning("Loss finalization failed for Lease {LeaseId}. Failure type: {FailureType}.",
                    candidate.LeaseId, exception.GetType().Name);
                failed++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        // A full page continues even through failures. A short/empty page ends this traversal.
        var next = candidates.Count == batchSize ? new ExpiredExecutionSweepCursor(candidates[^1], cutoff) : null;
        return new(candidates.Count, finalized, skipped, failed, next);
    }
}
