using System.Text.Json;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class BoundedSumWorkloadTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"values\":[],\"durationMs\":0}")]
    [InlineData("{\"values\":[1],\"durationMs\":-1}")]
    [InlineData("{\"values\":[1],\"durationMs\":60001}")]
    [InlineData("{\"values\":[1],\"durationMs\":1.1}")]
    [InlineData("{\"values\":[1000001],\"durationMs\":0}")]
    [InlineData("{\"values\":[-1000001],\"durationMs\":0}")]
    [InlineData("{\"values\":[1.01],\"durationMs\":0}")]
    [InlineData("{\"values\":[1e-1000],\"durationMs\":0}")]
    [InlineData("{\"values\":[1.000000000000000000000000000001],\"durationMs\":0}")]
    [InlineData("{\"values\":[true],\"durationMs\":0}")]
    [InlineData("{\"values\":[null],\"durationMs\":0}")]
    [InlineData("{\"values\":[\"private input\"],\"durationMs\":0}")]
    [InlineData("{\"values\":[1],\"durationMs\":0,\"extra\":1}")]
    [InlineData("{\"values\":[1],\"durationMs\":0,\"durationMs\":1}")]
    public async Task InvalidInputProducesOnlyFixedFailureWithoutSchedulingWork(string json)
    {
        var clock = new ControlledTimeProvider();
        var outcome = await new BoundedSumWorkload(clock).ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json), Token);
        Assert.Null(outcome.Result);
        Assert.Equal(WorkloadOutcome.InvalidInput().Error, outcome.Error);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(-1000000)]
    [InlineData(1000000)]
    public async Task MaximumArrayHasExactInt64Result(int value)
    {
        var workload = new BoundedSumWorkload(TimeProvider.System);
        var payload = JsonSerializer.SerializeToElement(new { values = Enumerable.Repeat(value, 1024), durationMs = 0 });
        var outcome = await workload.ExecuteAsync(payload, Token);
        Assert.Null(outcome.Error);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new { count = 1024, sum = value * 1024L }), outcome.Result!.Value));
        var oversized = JsonSerializer.SerializeToElement(new { values = Enumerable.Repeat(value, 1025), durationMs = 0 });
        Assert.Equal(WorkloadOutcome.InvalidInput().Error, (await workload.ExecuteAsync(oversized, Token)).Error);
    }

    [Fact]
    public async Task EquivalentJsonbIntegerSpellingsAndMaximumDurationRemainDeterministic()
    {
        var clock = new ControlledTimeProvider();
        var workload = new BoundedSumWorkload(clock);
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"values":[1.0,2e0,-0,1000000,-1000000],"durationMs":6e4}""");
        var execution = workload.ExecuteAsync(payload, Token);
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>("""{"count":5,"sum":3}"""), result.Result!.Value));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task LocalStopCancelsDelayWithoutAnOutcome()
    {
        var clock = new ControlledTimeProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var work = new BoundedSumWorkload(clock).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { values = new[] { 1 }, durationMs = 60000 }), stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(0, clock.ActiveTimers);
    }
}
