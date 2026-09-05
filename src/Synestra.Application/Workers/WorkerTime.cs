namespace Synestra.Application.Workers;

internal static class WorkerTime
{
    public static DateTime UtcNow(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Match PostgreSQL precision so a repeated PUT preserves session timestamps exactly.
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
