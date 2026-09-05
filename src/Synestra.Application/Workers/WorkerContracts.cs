namespace Synestra.Application.Workers;

public sealed record RegisterWorkerRequest(
    Guid WorkerId, Guid SessionId, string Name, int Capacity, IReadOnlyCollection<string> SupportedTypes);

public enum RegisterWorkerOutcome { Succeeded, InvalidRequest, SessionReplaced }
public sealed record RegisterWorkerResult(RegisterWorkerOutcome Outcome, WorkerDetails? Worker = null, string? Error = null);

public sealed record WorkerDetails(
    Guid WorkerId, Guid SessionId, string Name, int Capacity, IReadOnlyList<string> SupportedTypes,
    DateTime RegisteredAtUtc, DateTime SessionStartedAtUtc, DateTime LastSeenAtUtc,
    int HeartbeatIntervalSeconds, int OfflineAfterSeconds);

public enum WorkerHeartbeatOutcome { Succeeded, InvalidRequest, WorkerNotFound, SessionReplaced }
