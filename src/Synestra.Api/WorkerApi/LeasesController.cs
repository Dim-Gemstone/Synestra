using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Synestra.Application.Workers;

namespace Synestra.Api.WorkerApi;

[ApiController]
[Route("api/worker/workers/{workerId}/leases/{leaseId}")]
public sealed class LeasesController(RenewLease renewLease, ReportExecutionCompletion reportCompletion) : ControllerBase
{
    [HttpPost("renewal")]
    [ProducesResponseType<RenewedLease>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Renew(string workerId, string leaseId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(workerId, leaseId, out var worker, out var session, out var lease, out var token))
            return Error(ExecutionOutcome.InvalidRequest);
        var result = await renewLease.ExecuteAsync(worker, session, lease, token, cancellationToken);
        return result.Outcome == ExecutionOutcome.Succeeded ? Ok(result.Lease) : Error(result.Outcome);
    }

    [HttpPut("completion")]
    [ProducesResponseType<CompletionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Complete(string workerId, string leaseId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(workerId, leaseId, out var worker, out var session, out var lease, out var token))
            return Error(ExecutionOutcome.InvalidRequest);
        CompletionReport? report;
        try
        {
            report = await CompletionContract.ParseAsync(Request, cancellationToken);
        }
        catch (JsonException)
        {
            return Error(ExecutionOutcome.InvalidRequest);
        }
        if (report is null) return Error(ExecutionOutcome.InvalidRequest);
        var result = await reportCompletion.ExecuteAsync(worker, session, lease, token, report, cancellationToken);
        return result.Outcome == ExecutionOutcome.Succeeded
            ? new JsonResult(CompletionResponse.From(result.Completion!), CompletionContract.ResponseOptions)
            : Error(result.Outcome);
    }

    private bool TryIdentity(string workerId, string leaseId, out Guid worker, out Guid session, out Guid lease, out string? token)
    {
        worker = session = lease = default;
        token = null;
        if (!Guid.TryParse(workerId, out worker) || !Guid.TryParse(leaseId, out lease)
            || !Request.Headers.TryGetValue("Worker-Session-Id", out var sessions) || sessions.Count != 1
            || !Guid.TryParse(sessions[0], out session)
            || !Request.Headers.TryGetValue("Lease-Token", out var tokens) || tokens.Count != 1)
            return false;
        token = tokens[0];
        return true;
    }

    private ObjectResult Error(ExecutionOutcome outcome)
    {
        var (status, code, title) = outcome switch
        {
            ExecutionOutcome.InvalidRequest => (400, "invalid_request", "The request is invalid."),
            ExecutionOutcome.WorkerNotFound => (404, "worker_not_found", "The worker was not found."),
            ExecutionOutcome.SessionReplaced => (409, "worker_session_replaced", "The worker session was replaced."),
            ExecutionOutcome.LeaseNotFound => (404, "lease_not_found", "The lease was not found."),
            ExecutionOutcome.OwnershipLost => (409, "lease_ownership_lost", "Lease ownership was lost."),
            ExecutionOutcome.LeaseExpired => (409, "lease_expired", "The lease has expired."),
            ExecutionOutcome.LeaseNotActive => (409, "lease_not_active", "The lease is not active."),
            ExecutionOutcome.CompletionReportConflict => (409, "completion_report_conflict", "The completion report conflicts with the accepted report."),
            ExecutionOutcome.AttemptAlreadyFinalized => (409, "attempt_already_finalized", "The attempt is already finalized."),
            _ => throw new InvalidOperationException($"Unknown execution outcome: {outcome}.")
        };
        var problem = new ProblemDetails { Status = status, Type = $"urn:synestra:problem:{code.Replace('_', '-')}", Title = title };
        problem.Extensions["code"] = code;
        return StatusCode(status, problem);
    }
}
