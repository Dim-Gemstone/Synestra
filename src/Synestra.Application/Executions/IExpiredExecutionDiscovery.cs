namespace Synestra.Application.Executions;

public sealed record ExpiredExecutionCursor(DateTime ExpiresAtUtc, Guid LeaseId);

public interface IExpiredExecutionDiscovery
{
    Task<IReadOnlyList<ExpiredExecutionCursor>> FindAsync(
        DateTime cutoffUtc, ExpiredExecutionCursor? after, int limit, CancellationToken cancellationToken);
}
