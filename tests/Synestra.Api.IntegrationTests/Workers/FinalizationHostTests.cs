using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synestra.Api.Executions;
using Synestra.Api.IntegrationTests.Infrastructure;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    [Fact]
    public async Task HostFinalization_DefaultsRunImmediatelyAndPreserveWorkerErrorPrecedence()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        await clock.NextDelayAsync(Token);
        var options = host.Services.GetRequiredService<IOptions<ExecutionFinalizationOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(5, options.IntervalSeconds);
        Assert.Equal(100, options.BatchSize);
        Assert.Single(probe.DisposedScopes);
        await AssertHostLostAsync(execution, Now.AddSeconds(40));
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
        AssertFields(view, "id", "type", "status", "priority", "maxAttempts", "createdAtUtc", "availableAtUtc", "completedAtUtc", "completion");
        Assert.Equal("failed", view.GetProperty("status").GetString());
        var body = Body(Guid.CreateVersion7());
        await ProblemAsync(await SendAsync(execution, "completion", body, client), 409, "attempt_already_finalized");
        await ProblemAsync(await SendAsync(execution, "renewal", client: client), 409, "lease_not_active");
        await ProblemAsync(await SendAsync(execution with { Secret = LeaseToken.Generate() }, "completion", body, client), 409, "lease_ownership_lost");
        await RegisterAsync(execution.WorkerId, Guid.CreateVersion7());
        await ProblemAsync(await SendAsync(execution, "completion", body, client), 409, "worker_session_replaced");
    }

    [Fact]
    public async Task HostFinalization_TicksNeverFinalizeEarlyAndBoundaryUsesFreshScopes()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(20));
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        await clock.NextDelayAsync(Token);
        await AssertHostRunningAsync(execution);
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.NextDelayAsync(Token);
        await AssertHostRunningAsync(execution);
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.NextDelayAsync(Token);
        await AssertHostLostAsync(execution, Now.AddSeconds(30));
        await using var context = Context();
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, (await context.Leases.SingleAsync(Token)).ExpiresAtUtc);
        Assert.Equal(3, probe.DisposedScopes.Distinct().Count());
        var before = await HostExecutionRowsAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.NextDelayAsync(Token);
        Assert.Equal(before, await HostExecutionRowsAsync());
    }

    [Fact]
    public async Task HostFinalization_IntervalStartsAfterSlowPassWithoutOverlappingOrCatchUp()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = HostedFactory(clock, probe, new() { ["IntervalSeconds"] = "7" });
        using var client = host.CreateClient();
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, probe.Calls);
            Assert.Equal(0, clock.ScheduledCount);
            Assert.Empty(probe.DisposedScopes);
            probe.Gate.TrySetResult();
            var delay = await clock.NextDelayAsync(Token);
            Assert.Equal(Now.AddSeconds(107), delay.DueAt);
            Assert.Single(probe.DisposedScopes);
            await AssertHostLostAsync(execution, Now.AddSeconds(100));
            clock.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal(1, probe.Calls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.NextDelayAsync(Token);
            Assert.Equal(2, probe.Calls);
        }
        finally { probe.Gate.TrySetResult(); }
    }

    [Fact]
    public async Task HostFinalization_ConfiguredBatchAndCursorAdvancePastBusyWorkThenRevisitIt()
    {
        var executions = new[] { await AcquireAsync(), await AcquireAsync(), await AcquireAsync() };
        await using var blocker = Context();
        var firstId = await blocker.Leases.OrderBy(lease => lease.ExpiresAtUtc).ThenBy(lease => lease.Id).Select(lease => lease.Id).FirstAsync(Token);
        var first = executions.Single(execution => execution.LeaseId == firstId);
        await using var transaction = await blocker.Database.BeginTransactionAsync(Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM workers WHERE id = {first.WorkerId} FOR UPDATE", Token);
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe, new() { ["BatchSize"] = "1" });
        using var client = host.CreateClient();
        await clock.NextDelayAsync(Token);
        await transaction.CommitAsync(Token);
        for (var pass = 0; pass < 3; pass++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.NextDelayAsync(Token);
            await AssertHostRunningAsync(first);
        }
        await using var context = Context();
        Assert.Equal(2, await context.Jobs.CountAsync(job => job.Status == JobStatus.Failed, Token));
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.NextDelayAsync(Token);
        await AssertHostLostAsync(first, Now.AddSeconds(60));
        Assert.Equal(5, probe.DisposedScopes.Distinct().Count());
    }

    [Fact]
    public async Task HostFinalization_DisabledModeDoesNotDiscoverOrSchedule()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe, new() { ["Enabled"] = "false" });
        using var client = host.CreateClient();
        var service = host.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "ExpiredExecutionFinalizer");
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, clock.ScheduledCount);
        await AssertHostRunningAsync(execution);
    }

    [Theory]
    [InlineData("BatchSize", "0", "true")]
    [InlineData("BatchSize", "-1", "true")]
    [InlineData("IntervalSeconds", "0", "true")]
    [InlineData("IntervalSeconds", "-1", "true")]
    [InlineData("IntervalSeconds", "2147483647", "true")]
    [InlineData("BatchSize", "0", "false")]
    public async Task HostFinalization_InvalidOptionsFailStartupEvenWhenDisabled(string name, string value, string enabled)
    {
        var clock = new FinalizerTestTimeProvider(Now);
        await using var host = HostedFactory(clock, settings: new() { [name] = value, ["Enabled"] = enabled });
        var exception = Assert.Throws<OptionsValidationException>(() => host.CreateClient());
        Assert.Contains("ExecutionFinalization:" + name, exception.Message);
        Assert.Equal(0, clock.ScheduledCount);
    }

    [Fact]
    public async Task HostFinalization_TemporaryPersistenceFailureWaitsThenSucceedsWithSafeLogging()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe { FailFirst = true };
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        var delay = await clock.NextDelayAsync(Token);
        await AssertHostRunningAsync(execution);
        Assert.Equal(Now.AddSeconds(45), delay.DueAt);
        var warning = Assert.Single(probe.Warnings);
        Assert.Null(warning.Exception);
        Assert.Equal("Loss finalization pass failed. Failure type: InvalidOperationException.", warning.Message);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, probe.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextDelayAsync(Token);
        await AssertHostLostAsync(execution, Now.AddSeconds(45));
        Assert.Equal(2, probe.DisposedScopes.Distinct().Count());
    }

    [Fact]
    public async Task HostFinalization_ShutdownCancelsPendingDelay()
    {
        var clock = new FinalizerTestTimeProvider(Now);
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        var delay = await clock.NextDelayAsync(Token);
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(delay.IsDisposed);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, probe.Calls);
        Assert.Empty(probe.Warnings);
    }

    [Fact]
    public async Task HostFinalization_ShutdownRollsBackActiveTransactionAndRestartRediscoversIt()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var gate = new FinalizationCommitGate();
        var probe = new PassProbe();
        await using var host = HostedFactory(clock, probe, interceptors: [gate]);
        using var client = host.CreateClient();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            await AssertHostRunningAsync(execution);
            Assert.Single(probe.DisposedScopes);
            Assert.Empty(probe.Warnings);
            Assert.Equal(0, clock.ScheduledCount);
        }
        finally { gate.Release.TrySetResult(); }
        var restartedClock = new FinalizerTestTimeProvider(Now.AddSeconds(50));
        await using var restarted = HostedFactory(restartedClock);
        using var restartedClient = restarted.CreateClient();
        await restartedClock.NextDelayAsync(Token);
        await AssertHostLostAsync(execution, Now.AddSeconds(50));
    }

    [Fact]
    public async Task HostFinalization_TwoEnabledHostsProgressIndependentlyAndNeverRewriteTerminalRows()
    {
        var executions = new[] { await AcquireAsync(), await AcquireAsync() };
        var firstClock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var gate = new FinalizationCommitGate();
        await using var first = HostedFactory(firstClock, interceptors: [gate]);
        using var firstClient = first.CreateClient();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var secondClock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
            await using var second = HostedFactory(secondClock);
            using var secondClient = second.CreateClient();
            await secondClock.NextDelayAsync(Token);
            await using var context = Context();
            Assert.Equal(1, await context.Jobs.CountAsync(job => job.Status == JobStatus.Failed, Token));
            Assert.Equal(0, firstClock.ScheduledCount);
            gate.Release.TrySetResult();
            await firstClock.NextDelayAsync(Token);
            foreach (var execution in executions) await AssertHostLostAsync(execution, Now.AddSeconds(40));
            var before = await HostExecutionRowsAsync();
            firstClock.Advance(TimeSpan.FromSeconds(5));
            secondClock.Advance(TimeSpan.FromSeconds(5));
            await firstClock.NextDelayAsync(Token);
            await secondClock.NextDelayAsync(Token);
            Assert.Equal(before, await HostExecutionRowsAsync());
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task HostFinalization_CompletionWinsAfterDiscoveryAndReplaySurvivesEnabledHostRestart()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        var body = Body(Guid.CreateVersion7());
        string snapshot;
        JsonElement observed;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _clock.Now = Now.AddSeconds(35);
            var response = await SendAsync(execution, "completion", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            snapshot = await response.Content.ReadAsStringAsync(Token);
            observed = await client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
            AssertClientCompletion(observed, execution, "succeeded");
            Assert.Equal(Now.AddSeconds(35).UtcDateTime, observed.GetProperty("completedAtUtc").GetDateTime());
            AssertJsonEqual(JsonSerializer.Deserialize<JsonElement>(snapshot).GetProperty("result").GetRawText(),
                observed.GetProperty("completion").GetProperty("result"));
            var before = await HostExecutionRowsAsync();
            probe.Gate.TrySetResult();
            await clock.NextDelayAsync(Token);
            Assert.Equal(before, await HostExecutionRowsAsync());
            Assert.True(JsonElement.DeepEquals(observed, await client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token)));
        }
        finally { probe.Gate.TrySetResult(); }
        await host.DisposeAsync();
        var restartedClock = new FinalizerTestTimeProvider(Now.AddDays(1));
        await using var restarted = HostedFactory(restartedClock);
        using var restartedClient = restarted.CreateClient();
        await restartedClock.NextDelayAsync(Token);
        var replay = await SendAsync(execution, "completion", body, restartedClient);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(snapshot, await replay.Content.ReadAsStringAsync(Token));
        Assert.True(JsonElement.DeepEquals(observed, await restartedClient.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token)));
    }

    [Fact]
    public async Task HostFinalization_CommittedLossWinsAgainstConcurrentWorkerCompletion()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var gate = new FinalizationCommitGate();
        await using var host = HostedFactory(clock, interceptors: [gate]);
        using var client = host.CreateClient();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var completion = SendAsync(execution, "completion", Body(Guid.CreateVersion7()));
            await WaitForBlockedAsync(1);
            gate.Release.TrySetResult();
            await clock.NextDelayAsync(Token);
            await ProblemAsync(await completion, 409, "attempt_already_finalized");
            await AssertHostLostAsync(execution, Now.AddSeconds(40));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task HostFinalization_RenewalWinsAfterDiscoveryAndOnlyNewExpirationCanFinalize()
    {
        var execution = await AcquireAsync();
        var clock = new FinalizerTestTimeProvider(Now.AddSeconds(40));
        var probe = new PassProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = HostedFactory(clock, probe);
        using var client = host.CreateClient();
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _clock.Now = Now.AddSeconds(20);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(execution, "renewal")).StatusCode);
            probe.Gate.TrySetResult();
            await clock.NextDelayAsync(Token);
            await AssertHostRunningAsync(execution);
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.NextDelayAsync(Token);
            await AssertHostRunningAsync(execution);
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.NextDelayAsync(Token);
            await AssertHostLostAsync(execution, Now.AddSeconds(50));
            await ProblemAsync(await SendAsync(execution, "renewal", client: client), 409, "lease_not_active");
        }
        finally { probe.Gate.TrySetResult(); }
    }
}
