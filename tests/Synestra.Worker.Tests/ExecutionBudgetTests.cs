using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class ExecutionBudgetTests
{
    [Fact]
    public void ClaimLatencyAndWallClockRegressionCannotExtendConfirmedOwnership()
    {
        var clock = new ControlledTimeProvider();
        var claimed = ExecutionTestProtocol.Execution(clock);
        clock.Advance(TimeSpan.FromSeconds(3));
        var budget = new ExecutionBudget(claimed, clock);
        Assert.Equal(TimeSpan.FromSeconds(22), budget.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(10), budget.RenewalDelay);
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.Equal(TimeSpan.FromSeconds(22), budget.Remaining);
        Assert.True(budget.Extend(claimed.ExpiresAtUtc.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(32), budget.Remaining);
        Assert.True(budget.Extend(claimed.ExpiresAtUtc.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(32), budget.Remaining);
        clock.Advance(TimeSpan.FromSeconds(32));
        Assert.False(budget.Extend(claimed.ExpiresAtUtc.AddSeconds(100)));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }

    [Fact]
    public void RenewalCannotRegressExpirationOrExtendExecutionPastWatchdog()
    {
        var clock = new ControlledTimeProvider();
        var claimed = ExecutionTestProtocol.Execution(clock);
        var budget = new ExecutionBudget(claimed, clock);
        Assert.Equal("invalid_renewal_response", Assert.Throws<WorkerProtocolException>(() => budget.Extend(claimed.ExpiresAtUtc.AddTicks(-1))).Code);
        Assert.True(budget.Extend(claimed.ExpiresAtUtc.AddDays(1)));
        Assert.Equal(TimeSpan.FromSeconds(65), budget.Remaining);
        clock.Advance(TimeSpan.FromSeconds(65));
        Assert.False(budget.Extend(claimed.ExpiresAtUtc.AddDays(2)));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }
}
