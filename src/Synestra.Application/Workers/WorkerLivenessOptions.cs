namespace Synestra.Application.Workers;

public sealed class WorkerLivenessOptions
{
    public int HeartbeatIntervalSeconds { get; init; } = 10;
    public int OfflineAfterSeconds { get; init; } = 30;

    public bool IsOffline(DateTime lastSeenAtUtc, DateTime serverUtc) =>
        serverUtc - lastSeenAtUtc >= TimeSpan.FromSeconds(OfflineAfterSeconds);
}
