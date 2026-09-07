using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class ExecuteClaimedWorkTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdinaryHandlerExceptionBecomesFixedFailureWithoutPrivateDetails()
    {
        var clock = new ControlledTimeProvider();
        using var handler = new WorkerHostTests.Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP."));
        using var http = new HttpClient(handler);
        var runner = new ExecuteClaimedWork(new WorkerApiClient(http, clock), new BoundedSumWorkload(new BrokenDelay()), clock,
            NullLogger<ExecuteClaimedWork>.Instance);
        var report = await runner.ExecuteAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), ExecutionTestProtocol.Execution(clock), Token);
        Assert.NotNull(report);
        Assert.Equal("failed", report.Outcome);
        Assert.Null(report.Result);
        Assert.Equal(WorkloadOutcome.Failed().Error, report.Error);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task WatchdogPreventsLateScheduledHandlerCompletionDespiteLongLease()
    {
        var clock = new ControlledTimeProvider();
        using var handler = new WorkerHostTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var http = new HttpClient(handler);
        var runner = new ExecuteClaimedWork(new WorkerApiClient(http, clock), new BoundedSumWorkload(clock), clock,
            NullLogger<ExecuteClaimedWork>.Instance);
        var execution = runner.ExecuteAsync(Guid.CreateVersion7(), Guid.CreateVersion7(),
            ExecutionTestProtocol.Execution(clock, durationMs: 60000, leaseSeconds: 300), Token);
        await clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(65));
        clock.Advance(TimeSpan.FromSeconds(65));
        Assert.Null(await execution.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(0, clock.ActiveTimers);
    }

    private sealed class BrokenDelay : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("Private input and exception details.");
    }
}
