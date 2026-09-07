using Microsoft.Extensions.Options;

namespace Synestra.Worker;

internal sealed class WorkerAgent(
    IOptions<WorkerOptions> options, WorkerApiClient api, ExecuteClaimedWork execute, TimeProvider timeProvider,
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
            await RunSessionAsync(identity.WorkerId, sessionId, interval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && exitStatus.ExitCode == 0)
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

    private async Task RunSessionAsync(Guid workerId, Guid sessionId, TimeSpan interval, CancellationToken stoppingToken)
    {
        using var failure = new CancellationTokenSource();
        using var running = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, failure.Token);
        using var shutdownDeadline = new CancellationTokenSource();
        await using var shutdownTimer = timeProvider.CreateTimer(_ => shutdownDeadline.Cancel(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        using var shutdownRegistration = stoppingToken.Register(() =>
            shutdownTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(failure.Token, shutdownDeadline.Token);
        using var heartbeatGate = new SemaphoreSlim(1, 1);
        var lastHeartbeat = timeProvider.GetTimestamp();

        async Task HeartbeatAsync(bool onlyWhenDue = false)
        {
            await heartbeatGate.WaitAsync(running.Token);
            try
            {
                if (onlyWhenDue && timeProvider.GetElapsedTime(lastHeartbeat) < interval) return;
                await api.HeartbeatAsync(workerId, sessionId, running.Token);
                Volatile.Write(ref lastHeartbeat, timeProvider.GetTimestamp());
            }
            finally { heartbeatGate.Release(); }
        }

        async Task MaintainLivenessAsync()
        {
            while (true)
            {
                var delay = interval - timeProvider.GetElapsedTime(Volatile.Read(ref lastHeartbeat));
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, timeProvider, running.Token);
                await HeartbeatAsync(onlyWhenDue: true);
            }
        }

        async Task RunWorkAsync()
        {
            while (true)
            {
                running.Token.ThrowIfCancellationRequested();
                ClaimedExecution? claimed;
                try { claimed = await api.ClaimAsync(workerId, sessionId, running.Token); }
                catch (WorkerProtocolException exception) when (exception.Code == "worker_offline")
                {
                    await HeartbeatAsync();
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, running.Token);
                    continue;
                }
                if (claimed is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, running.Token);
                    continue;
                }
                var report = await execute.ExecuteAsync(workerId, sessionId, claimed, running.Token);
                if (report is null) continue;
                // A frozen outcome may finish reporting during graceful shutdown.
                try
                {
                    reporting.Token.ThrowIfCancellationRequested();
                    await api.CompleteAsync(workerId, sessionId, claimed, report, reporting.Token);
                }
                catch (OperationCanceledException) when (shutdownDeadline.IsCancellationRequested && !failure.IsCancellationRequested)
                {
                    throw new TimeoutException("Completion shutdown budget exhausted.");
                }
                catch (WorkerProtocolException exception) when (exception.Code == "attempt_already_finalized")
                {
                    logger.LogInformation("Completion for {AttemptId} rejected after finalization.", claimed.AttemptId);
                }
            }
        }

        async Task GuardAsync(Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) when (running.IsCancellationRequested) { }
            catch
            {
                exitStatus.Fail();
                await failure.CancelAsync();
                throw;
            }
        }

        await Task.WhenAll(GuardAsync(MaintainLivenessAsync), GuardAsync(RunWorkAsync));
    }
}
