using System.Text.Json;
using Synestra.Application.Jobs;

namespace Synestra.Api.ClientApi;

public sealed record GetJobResponse(
    Guid Id,
    string Type,
    string Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc,
    DateTime? CompletedAtUtc,
    JobCompletionResponse? Completion)
{
    // Legacy JSON is byte-bounded, but predates the Worker result depth limit.
    public static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 65536 + 2
    };

    public static GetJobResponse From(GetJobDetails job)
    {
        JobCompletionResponse? completion = null;
        if (job.Completion is { } details)
        {
            using var result = details.Result is null ? null : JsonDocument.Parse(details.Result,
                new JsonDocumentOptions { MaxDepth = ResponseOptions.MaxDepth });
            completion = new(details.AttemptId, details.AttemptNumber,
                details.Outcome.ToString().ToLowerInvariant(), result?.RootElement.Clone(),
                details.Error is { } error ? new JobCompletionErrorResponse(error.Code, error.Message) : null);
        }
        return new(job.Id, job.Type, job.Status.ToString().ToLowerInvariant(), job.Priority,
            job.MaxAttempts, job.CreatedAtUtc, job.AvailableAtUtc, job.CompletedAtUtc, completion);
    }
}

public sealed record JobCompletionResponse(
    Guid AttemptId, int AttemptNumber, string Outcome, JsonElement? Result, JobCompletionErrorResponse? Error);

public sealed record JobCompletionErrorResponse(string Code, string Message);
