using System.Text.Json;

namespace Synestra.Application.Workers;

public sealed class ReportExecutionCompletion(IReportExecutionCompletionPersistence persistence, TimeProvider timeProvider)
{
    public async Task<CompletionResult> ExecuteAsync(
        Guid workerId, Guid sessionId, Guid leaseId, string? token, CompletionReport report,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = ExecutionIdentity.Validate(workerId, sessionId, leaseId, token);
        if (hash is null || !CompletionValidation.IsValid(report)) return new(ExecutionOutcome.InvalidRequest);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var worker = await transaction.LockWorkerAsync(workerId, cancellationToken);
        if (worker is null) return new(ExecutionOutcome.WorkerNotFound);
        if (worker.SessionId != sessionId) return new(ExecutionOutcome.SessionReplaced);

        var execution = await transaction.LockExecutionAsync(leaseId, cancellationToken);
        if (execution is null) return new(ExecutionOutcome.LeaseNotFound);
        if (!execution.IsOwnedBy(workerId, sessionId, leaseId, hash)) return new(ExecutionOutcome.OwnershipLost);

        if (execution.Completion is { } previous)
        {
            if (previous.ReportId != report.ReportId) return new(ExecutionOutcome.AttemptAlreadyFinalized);
            if (!Equivalent(previous, report)) return new(ExecutionOutcome.CompletionReportConflict);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(ExecutionOutcome.Succeeded, previous);
        }

        if (!execution.IsRunning) return new(ExecutionOutcome.AttemptAlreadyFinalized);
        var now = WorkerTime.UtcNow(timeProvider);
        // Validate the shared timestamp before either entity can mutate.
        if (now < execution.Lease.AcquiredAtUtc || now < execution.Attempt!.StartedAtUtc)
            throw new InvalidOperationException("Server time precedes execution acquisition.");

        if (report.Outcome == "succeeded")
            execution.Job!.SucceedAttempt(execution.Attempt!, report.Result!, now);
        else
            execution.Job!.FailAttempt(execution.Attempt!, report.Error!.Code, report.Error.Message, now);
        execution.Lease.Release(now);
        var snapshot = new ExecutionCompletionSnapshot(execution.Job.Id, execution.Attempt!.Id, leaseId,
            report.ReportId, report.Outcome, now, execution.Attempt.Result, report.Error);
        transaction.RecordCompletion(snapshot);
        await transaction.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(ExecutionOutcome.Succeeded, snapshot);
    }

    private static bool Equivalent(ExecutionCompletionSnapshot previous, CompletionReport report)
    {
        if (previous.Outcome != report.Outcome || previous.Error != report.Error) return false;
        if (previous.Result is null || report.Result is null) return previous.Result == report.Result;
        using var left = JsonDocument.Parse(previous.Result);
        using var right = JsonDocument.Parse(report.Result);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }
}
