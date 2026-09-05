using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public sealed class RegisterWorker(IWorkerPersistence persistence, TimeProvider timeProvider, WorkerLivenessOptions options)
{
    public async Task<RegisterWorkerResult> ExecuteAsync(RegisterWorkerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkerRegistration registration;
        try
        {
            registration = new WorkerRegistration(request.WorkerId, request.SessionId, request.Name, request.Capacity, request.SupportedTypes);
        }
        catch (ArgumentException exception)
        {
            return new RegisterWorkerResult(RegisterWorkerOutcome.InvalidRequest, Error: exception.Message);
        }

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var worker = await transaction.LockRegistrationAsync(registration.WorkerId, cancellationToken);
        if (worker?.SessionId != registration.SessionId)
        {
            if (worker is not null && await transaction.HasRegisteredSessionAsync(worker.Id, registration.SessionId, cancellationToken))
            {
                return new RegisterWorkerResult(RegisterWorkerOutcome.SessionReplaced);
            }

            transaction.AddSession(registration.WorkerId, registration.SessionId);
        }

        var now = WorkerTime.UtcNow(timeProvider);
        if (worker is null)
        {
            worker = new Worker(registration, now);
            transaction.Add(worker);
        }
        else
        {
            worker.UpdateRegistration(registration, now);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RegisterWorkerResult(RegisterWorkerOutcome.Succeeded, new WorkerDetails(
            worker.Id, worker.SessionId!.Value, worker.Name, worker.Capacity,
            worker.SupportedTypes.Select(item => item.Type).Order(StringComparer.Ordinal).ToArray(),
            worker.RegisteredAtUtc, worker.SessionStartedAtUtc!.Value, worker.LastSeenAtUtc,
            options.HeartbeatIntervalSeconds, options.OfflineAfterSeconds));
    }
}
