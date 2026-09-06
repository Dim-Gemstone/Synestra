using System.Text.Json;
using System.Text.Json.Serialization;
using Synestra.Application.Workers;

namespace Synestra.Api.WorkerApi;

public sealed record CompletionResponse(
    Guid JobId, Guid AttemptId, Guid LeaseId, Guid ReportId, string Outcome, DateTime FinishedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExecutionError? Error)
{
    public static CompletionResponse From(ExecutionCompletionSnapshot snapshot)
    {
        using var result = snapshot.Result is null ? null : JsonDocument.Parse(snapshot.Result);
        return new(snapshot.JobId, snapshot.AttemptId, snapshot.LeaseId, snapshot.ReportId, snapshot.Outcome,
            snapshot.FinishedAtUtc, result?.RootElement.Clone(), snapshot.Error);
    }
}

internal static class CompletionContract
{
    public const int MaximumRequestBytes = 68 * 1024;
    // The response envelope adds one level to the allowed result depth.
    public static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = CompletionValidation.MaximumResultDepth + 1
    };

    public static async Task<CompletionReport?> ParseAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.ContentLength > MaximumRequestBytes) return null;
        await using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumRequestBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        try
        {
            return Parse(buffer.ToArray());
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("Request text must contain valid Unicode.", exception);
        }
    }

    private static CompletionReport? Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes,
            new JsonDocumentOptions { MaxDepth = CompletionValidation.MaximumResultDepth + 1 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        Guid? reportId = null;
        string? outcome = null;
        string? result = null;
        ExecutionError? error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return null;
            switch (property.Name)
            {
                case "reportId" when property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var id):
                    reportId = id;
                    break;
                case "outcome" when property.Value.ValueKind == JsonValueKind.String:
                    outcome = property.Value.GetString();
                    break;
                case "result":
                    result = property.Value.GetRawText();
                    break;
                case "error":
                    error = ParseError(property.Value);
                    if (error is null) return null;
                    break;
                default:
                    return null;
            }
        }

        if (seen.Count != 3 || reportId is null || outcome is null) return null;
        return new(reportId.Value, outcome, result, error);
    }

    private static ExecutionError? ParseError(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? code = null;
        string? message = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String) return null;
            switch (property.Name)
            {
                case "code": code = property.Value.GetString(); break;
                case "message": message = property.Value.GetString(); break;
                default: return null;
            }
        }
        return code is null || message is null ? null : new(code, message);
    }
}
