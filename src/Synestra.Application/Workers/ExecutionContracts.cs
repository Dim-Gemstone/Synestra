using System.Security.Cryptography;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public enum ExecutionOutcome
{
    Succeeded, InvalidRequest, WorkerNotFound, SessionReplaced, LeaseNotFound,
    OwnershipLost, LeaseExpired, LeaseNotActive, CompletionReportConflict, AttemptAlreadyFinalized
}

public sealed record RenewLeaseResult(ExecutionOutcome Outcome, RenewedLease? Lease = null);
public sealed record RenewedLease(Guid LeaseId, DateTime ExpiresAtUtc);
public sealed record ExecutionError(string Code, string Message);
public sealed record CompletionReport(Guid ReportId, string Outcome, string? Result = null, ExecutionError? Error = null);
public sealed record CompletionResult(ExecutionOutcome Outcome, ExecutionCompletionSnapshot? Completion = null);
public sealed record ExecutionCompletionSnapshot(
    Guid JobId, Guid AttemptId, Guid LeaseId, Guid ReportId, string Outcome,
    DateTime FinishedAtUtc, string? Result, ExecutionError? Error);

// Read under ordered row locks; protocol fields deliberately stay outside Domain.
public sealed record LeaseExecution(
    Job? Job, JobAttempt? Attempt, Lease Lease, byte[]? TokenHash,
    ExecutionCompletionSnapshot? Completion, bool LocatorMatches)
{
    public bool IsOwnedBy(Guid workerId, Guid sessionId, Guid leaseId, byte[] tokenHash) =>
        LocatorMatches && Job is not null && Attempt is not null
        && Attempt.JobId == Job.Id && Lease.JobAttemptId == Attempt.Id
        && Lease.Id == leaseId && Lease.WorkerId == workerId && Lease.SessionId == sessionId
        && TokenHash is { Length: 32 } && CryptographicOperations.FixedTimeEquals(TokenHash, tokenHash);

    public bool IsRunning => Job is { Status: JobStatus.Running, CompletedAtUtc: null }
        && Attempt is { Status: JobAttemptStatus.Running, FinishedAtUtc: null, Result: null, ErrorCode: null, ErrorMessage: null }
        && Lease.ReleasedAtUtc is null && Completion is null;
}

internal static class ExecutionIdentity
{
    public static byte[]? Validate(Guid workerId, Guid sessionId, Guid leaseId, string? token)
    {
        try
        {
            WorkerRegistration.ValidateIdentity(workerId, nameof(workerId));
            WorkerRegistration.ValidateIdentity(sessionId, nameof(sessionId));
            WorkerRegistration.ValidateIdentity(leaseId, nameof(leaseId));
            return LeaseToken.Hash(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
