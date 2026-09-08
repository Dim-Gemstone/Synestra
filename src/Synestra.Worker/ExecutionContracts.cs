using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synestra.Worker;

internal sealed record ClaimedExecution(Guid JobId, Guid AttemptId, Guid LeaseId, string LeaseToken,
    int AttemptNumber, JsonElement Payload, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc, long ClaimStartedTimestamp)
{
    public override string ToString() => $"Execution {JobId}/{AttemptId}/{LeaseId}";
}

internal sealed record WorkloadError(string Code, string Message);

internal sealed record CompletionReport(Guid ReportId, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkloadError? Error)
{
    public override string ToString() => $"Completion {ReportId}: {Outcome}";
}

internal sealed record WorkloadOutcome(JsonElement? Result, WorkloadError? Error)
{
    public static WorkloadOutcome InvalidInput() => new(null, new("invalid_workload_input", "The bounded workload input is invalid."));
    public static WorkloadOutcome Failed() => new(null, new("workload_execution_failed", "The bounded workload failed."));
    public CompletionReport Freeze() => new(Guid.CreateVersion7(), Error is null ? "succeeded" : "failed", Result, Error);
}
