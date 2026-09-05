using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Synestra.Application.Workers;

namespace Synestra.Api.WorkerApi;

[ApiController]
[Route("api/worker/workers/{workerId}")]
public sealed class WorkersController(RegisterWorker registerWorker, RecordWorkerHeartbeat recordHeartbeat) : ControllerBase
{
    [HttpPut("registration")]
    [ProducesResponseType<WorkerDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(string workerId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workerId, out var id))
        {
            return Error(400, "invalid_request");
        }

        RegisterWorkerRequest? request;
        try
        {
            request = await RegistrationContract.ParseAsync(Request, id, cancellationToken);
        }
        catch (JsonException)
        {
            return Error(400, "invalid_request");
        }

        if (request is null)
        {
            return Error(400, "invalid_request");
        }

        var result = await registerWorker.ExecuteAsync(request, cancellationToken);
        return result.Outcome switch
        {
            RegisterWorkerOutcome.Succeeded => Ok(result.Worker),
            RegisterWorkerOutcome.InvalidRequest => Error(400, "invalid_request", result.Error),
            RegisterWorkerOutcome.SessionReplaced => Error(409, "worker_session_replaced"),
            _ => throw new InvalidOperationException($"Unknown registration outcome: {result.Outcome}.")
        };
    }

    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Heartbeat(string workerId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workerId, out var id)
            || !Request.Headers.TryGetValue("Worker-Session-Id", out var sessions)
            || sessions.Count != 1 || !Guid.TryParse(sessions[0], out var sessionId))
        {
            return Error(400, "invalid_request");
        }

        return await recordHeartbeat.ExecuteAsync(id, sessionId, cancellationToken) switch
        {
            WorkerHeartbeatOutcome.Succeeded => NoContent(),
            WorkerHeartbeatOutcome.InvalidRequest => Error(400, "invalid_request"),
            WorkerHeartbeatOutcome.WorkerNotFound => Error(404, "worker_not_found"),
            WorkerHeartbeatOutcome.SessionReplaced => Error(409, "worker_session_replaced"),
            var outcome => throw new InvalidOperationException($"Unknown heartbeat outcome: {outcome}.")
        };
    }

    private ObjectResult Error(int status, string code, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"urn:synestra:problem:{code.Replace('_', '-')}",
            Title = code switch
            {
                "invalid_request" => "The request is invalid.",
                "worker_not_found" => "The worker was not found.",
                "worker_session_replaced" => "The worker session was replaced.",
                _ => "The request failed."
            },
            Detail = detail
        };
        problem.Extensions["code"] = code;
        return StatusCode(status, problem);
    }
}
