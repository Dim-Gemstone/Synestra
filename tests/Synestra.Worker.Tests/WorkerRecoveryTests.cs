using Xunit;

namespace Synestra.Worker.Tests;

public sealed partial class WorkerExecutionTests
{
    [Fact]
    public async Task RecoveryOfRenewalKeepsHandlerAndHeartbeatRunningWithinConfirmedLease()
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock, durationMs: 12000), (operation, _, _) =>
            operation == "renewal" && Count("renewal") == 1 ? throw new HttpRequestException("connection lost") : Task.FromResult<HttpResponseMessage?>(null));
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(12), Seconds(10), Seconds(10));
        _clock.Advance(Seconds(10));
        await _clock.WaitForTimersAsync(Token, Seconds(1), Seconds(10));
        Assert.Equal(2, Count("heartbeat"));
        Assert.Equal(1, Count("claims"));
        _clock.Advance(Seconds(1));
        await _clock.WaitForTimersAsync(Token, Seconds(1), Seconds(10), Seconds(9));
        _clock.Advance(Seconds(1));
        await _clock.WaitForTimersAsync(Token, Seconds(1), Seconds(8));
        Assert.Equal(2, Count("renewal"));
        Assert.Single(_reports);
        Assert.Equal(2, Count("claims"));
        await StopAsync(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryReplaysFrozenCompletionWithoutRenewalOrPrefetchIncludingShutdown(bool shutdown)
    {
        var bodies = new List<string>();
        using var host = Host(ExecutionTestProtocol.Execution(_clock, durationMs: 0), async (operation, request, token) =>
        {
            if (operation != "completion") return null;
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (bodies.Count < 3) throw new HttpRequestException("lost acknowledgement");
            return null;
        });
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        var stopping = shutdown ? host.StopAsync(Token) : null;
        for (var attempt = 1; attempt < 3; attempt++)
        {
            await _clock.WaitForTimersAsync(Token, Seconds(1));
            Assert.Equal(attempt, bodies.Count);
            Assert.Equal(1, Count("claims"));
            Assert.Equal(0, Count("renewal"));
            _clock.Advance(Seconds(1));
        }
        if (stopping is not null) await WaitAsync(stopping);
        else await _clock.WaitForTimersAsync(Token, Seconds(1), Seconds(8));
        Assert.Equal(3, bodies.Count);
        Assert.Single(bodies.Distinct());
        Assert.Single(_reports);
        Assert.Equal(shutdown ? 1 : 2, Count("claims"));
        await StopAsync(host);
    }

    [Theory]
    [InlineData("worker_session_replaced", true)]
    [InlineData("completion_report_conflict", true)]
    [InlineData("attempt_already_finalized", false)]
    public async Task RecoveryStopsRepeatingWhenCompletionGetsDefinitiveConflict(string code, bool fatal)
    {
        using var host = Host(ExecutionTestProtocol.Execution(_clock, durationMs: 0), (operation, _, _) =>
        {
            if (operation != "completion") return Task.FromResult<HttpResponseMessage?>(null);
            if (Count("completion") == 1) throw new IOException("lost body");
            return Task.FromResult<HttpResponseMessage?>(ExecutionTestProtocol.Problem(code));
        });
        await host.StartAsync(Token);
        await _clock.WaitForTimersAsync(Token, Seconds(1));
        _clock.Advance(Seconds(1));
        if (fatal) await FailureAsync(host);
        else { await _clock.WaitForTimersAsync(Token, Seconds(1), Seconds(9)); await StopAsync(host); }
        Assert.Equal(2, Count("completion"));
        Assert.Equal(1, Count("registration"));
        Assert.Equal(0, Count("renewal"));
    }
}
