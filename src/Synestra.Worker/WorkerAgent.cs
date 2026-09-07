using Microsoft.Extensions.Options;

namespace Synestra.Worker;

internal sealed class WorkerAgent(
    IOptions<WorkerOptions> options, WorkerApiClient api, TimeProvider timeProvider,
    WorkerExitStatus exitStatus, IHostApplicationLifetime lifetime, ILogger<WorkerAgent> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            using var identity = WorkerIdentity.Open(options.Value.StateDirectory);
            var sessionId = Guid.CreateVersion7();
            var interval = await api.RegisterAsync(identity.WorkerId, sessionId, options.Value.Name, stoppingToken);
            logger.LogInformation("Worker {WorkerId} registered session {SessionId}.", identity.WorkerId, sessionId);
            await api.HeartbeatAsync(identity.WorkerId, sessionId, stoppingToken);
            while (true)
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                await api.HeartbeatAsync(identity.WorkerId, sessionId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown does not report a workload outcome.
        }
        catch (Exception exception)
        {
            exitStatus.Fail();
            logger.LogError("Worker stopped. Failure: {Failure}.",
                exception is WorkerProtocolException protocol ? protocol.Code : exception.GetType().Name);
            lifetime.StopApplication();
        }
    }
}
