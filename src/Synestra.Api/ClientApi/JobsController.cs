using Microsoft.AspNetCore.Mvc;
using Synestra.Application.Jobs;

namespace Synestra.Api.ClientApi;

[ApiController]
[Route("api/client/jobs")]
public sealed class JobsController(SubmitJob submitJob) : ControllerBase
{
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<SubmitJobResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Submit(CancellationToken cancellationToken)
    {
        ParsedSubmitJobRequest? request;
        try
        {
            request = await SubmitJobContract.ParseAsync(Request, cancellationToken);
        }
        catch (RequestBodyTooLargeException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Payload is too large.");
        }
        catch (System.Text.Json.JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request", "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request", "Request body does not match the submit-job contract.");
        }

        var result = await submitJob.ExecuteAsync(
            new SubmitJobRequest(request.Type, request.Payload, request.AvailableAtUtc),
            cancellationToken);

        return result.Outcome switch
        {
            SubmitJobOutcome.Succeeded => Created(result.Job!),
            SubmitJobOutcome.InvalidRequest => Problem(StatusCodes.Status400BadRequest, "invalid_request", result.Error),
            SubmitJobOutcome.PayloadTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large", result.Error),
            SubmitJobOutcome.DefinitionNotFound => Problem(StatusCodes.Status404NotFound, "job_definition_not_found"),
            SubmitJobOutcome.DefinitionDisabled => Problem(StatusCodes.Status409Conflict, "job_definition_disabled"),
            _ => throw new InvalidOperationException($"Unknown submit-job outcome: {result.Outcome}.")
        };
    }

    private ObjectResult Created(Synestra.Domain.Jobs.Job job)
    {
        var response = new SubmitJobResponse(
            job.Id,
            job.Type,
            job.Status.ToString().ToLowerInvariant(),
            job.Priority,
            job.MaxAttempts,
            job.CreatedAtUtc,
            job.AvailableAtUtc);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    private ObjectResult Problem(int status, string code, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code switch
            {
                "invalid_request" => "The request is invalid.",
                "payload_too_large" => "The payload is too large.",
                "job_definition_not_found" => "The job definition was not found.",
                "job_definition_disabled" => "The job definition is disabled.",
                _ => "The request failed."
            },
            Type = $"urn:synestra:problem:{code.Replace('_', '-')}",
            Detail = detail
        };
        problem.Extensions["code"] = code;
        return StatusCode(status, problem);
    }
}
