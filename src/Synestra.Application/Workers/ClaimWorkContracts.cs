namespace Synestra.Application.Workers;

public enum ClaimWorkOutcome { Succeeded, NoWork, InvalidRequest, WorkerNotFound, SessionReplaced, WorkerOffline }

public sealed record ClaimWorkResult(ClaimWorkOutcome Outcome, ClaimedWork? Work = null);

public sealed record ClaimedWork(
    Guid JobId, Guid AttemptId, Guid LeaseId, string LeaseToken, int AttemptNumber, string Type, string Payload,
    DateTime AcquiredAtUtc, DateTime ExpiresAtUtc);
