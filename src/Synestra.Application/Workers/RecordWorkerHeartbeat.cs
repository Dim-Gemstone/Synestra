using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public sealed class RecordWorkerHeartbeat(IWorkerPersistence persistence, TimeProvider timeProvider)
{
    public async Task<WorkerHeartbeatOutcome> ExecuteAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            WorkerRegistration.ValidateIdentity(workerId, nameof(workerId));
            WorkerRegistration.ValidateIdentity(sessionId, nameof(sessionId));
        }
        catch (ArgumentException)
        {
            return WorkerHeartbeatOutcome.InvalidRequest;
        }

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var worker = await transaction.FindForHeartbeatAsync(workerId, cancellationToken);
        if (worker is null)
        {
            return WorkerHeartbeatOutcome.WorkerNotFound;
        }

        if (!worker.RecordHeartbeat(sessionId, WorkerTime.UtcNow(timeProvider)))
        {
            return WorkerHeartbeatOutcome.SessionReplaced;
        }

        await transaction.CommitAsync(cancellationToken);
        return WorkerHeartbeatOutcome.Succeeded;
    }
}
