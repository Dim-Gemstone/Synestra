namespace Synestra.Worker;

internal sealed class ExecuteClaimedWork(WorkerApiClient api, BoundedSumWorkload workload, TimeProvider timeProvider,
    ILogger<ExecuteClaimedWork> logger)
{
    public async Task<CompletionReport?> ExecuteAsync(Guid workerId, Guid sessionId, ClaimedExecution execution, CancellationToken token)
    {
        var budget = new ExecutionBudget(execution, timeProvider);
        if (budget.Remaining <= TimeSpan.Zero) return null;
        using var deadline = new CancellationTokenSource(budget.Remaining, timeProvider);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var work = RunHandlerAsync(execution, budget, stop.Token);
        try
        {
            while (!work.IsCompleted)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var delay = Task.Delay(budget.RenewalDelay > TimeSpan.Zero ? budget.RenewalDelay : TimeSpan.Zero, timeProvider, waiting.Token);
                try
                {
                    await Task.WhenAny(work, delay);
                    if (work.IsCompleted) break;
                    await delay;
                }
                finally
                {
                    await waiting.CancelAsync();
                    try { await delay; } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                }
                if (budget.Remaining <= TimeSpan.Zero) break;
                try
                {
                    var expiration = await api.RenewAsync(workerId, sessionId, execution, stop.Token);
                    if (!budget.Extend(expiration) || deadline.IsCancellationRequested) break;
                    deadline.CancelAfter(budget.Remaining);
                }
                catch (WorkerProtocolException exception) when (exception.Code is "lease_expired" or "lease_not_active" or "lease_ownership_lost")
                {
                    logger.LogInformation("Execution {AttemptId} stopped after {Code}.", execution.AttemptId, exception.Code);
                    // A completed report can tolerate expiry, but cannot recover lost ownership.
                    return exception.Code != "lease_ownership_lost" && work.IsCompletedSuccessfully ? work.Result : null;
                }
            }
            return work.IsCompletedSuccessfully ? work.Result : null;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return work.IsCompletedSuccessfully ? work.Result : null;
        }
        finally
        {
            await stop.CancelAsync();
            try { await work; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private async Task<CompletionReport> RunHandlerAsync(ClaimedExecution execution, ExecutionBudget budget, CancellationToken token)
    {
        WorkloadOutcome outcome;
        try { outcome = await workload.ExecuteAsync(execution.Payload, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { outcome = WorkloadOutcome.Failed(); }
        token.ThrowIfCancellationRequested();
        if (budget.Remaining <= TimeSpan.Zero) throw new OperationCanceledException(token);
        return outcome.Freeze();
    }
}
