using Microsoft.Extensions.Options;
using Synestra.Application.Executions;

namespace Synestra.Api.Executions;

internal sealed class ExpiredExecutionFinalizer(
    IServiceScopeFactory scopes, IOptions<ExecutionFinalizationOptions> options,
    TimeProvider timeProvider, IHostApplicationLifetime lifetime,
    ILogger<ExpiredExecutionFinalizer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);

            ExpiredExecutionSweepCursor? cursor = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<FinalizeExpiredExecutionSweep>()
                        .ExecuteAsync(settings.BatchSize, cursor, stoppingToken);
                    cursor = result.NextCursor;
                    logger.LogDebug("Loss finalization pass: {Inspected} inspected, {Finalized} finalized, {Skipped} skipped, {Failed} failed.",
                        result.Inspected, result.Finalized, result.Skipped, result.Failed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // Database/exception messages may contain execution data or credentials.
                    logger.LogWarning("Loss finalization pass failed. Failure type: {FailureType}.", exception.GetType().Name);
                }

                // The scope is disposed before waiting; slow passes cannot overlap or cause catch-up ticks.
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown, including while waiting for startup or the next pass.
        }
    }
}
