namespace Synestra.Worker;

internal sealed class ExecutionBudget(ClaimedExecution execution, TimeProvider timeProvider)
{
    private readonly long _executionStarted = timeProvider.GetTimestamp();
    private DateTime _expiration = execution.ExpiresAtUtc;
    private TimeSpan _leaseBudget = execution.ExpiresAtUtc - execution.AcquiredAtUtc - TimeSpan.FromSeconds(5);

    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Min(
        (_leaseBudget - timeProvider.GetElapsedTime(execution.ClaimStartedTimestamp)).Ticks,
        (TimeSpan.FromSeconds(65) - timeProvider.GetElapsedTime(_executionStarted)).Ticks));

    public TimeSpan RenewalDelay => TimeSpan.FromTicks(Math.Min(
        (execution.ExpiresAtUtc - execution.AcquiredAtUtc).Ticks / 3, Remaining.Ticks));

    public bool Extend(DateTime expiration)
    {
        if (expiration < _expiration) throw new WorkerProtocolException("invalid_renewal_response");
        if (Remaining <= TimeSpan.Zero) return false;
        _leaseBudget += expiration - _expiration;
        _expiration = expiration;
        return true;
    }
}
