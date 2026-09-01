using System.Text.Json;

namespace Synestra.Api.ClientApi;

public sealed record SubmitJobResponse(
    Guid Id,
    string Type,
    string Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc);

internal sealed record ParsedSubmitJobRequest(string Type, string Payload, DateTimeOffset? AvailableAtUtc);

internal static class SubmitJobContract
{
    public const long MaximumRequestSizeInBytes = 260 * 1024;

    private static readonly HashSet<string> AllowedProperties =
        new(StringComparer.Ordinal) { "type", "payload", "availableAtUtc" };

    public static async Task<ParsedSubmitJobRequest?> ParseAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumRequestSizeInBytes)
        {
            throw new RequestBodyTooLargeException();
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumRequestSizeInBytes)
            {
                throw new RequestBodyTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        using var document = JsonDocument.Parse(
            buffer.ToArray(),
            new JsonDocumentOptions { MaxDepth = Synestra.Application.Jobs.SubmitJob.MaximumPayloadDepth + 1 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? type = null;
        string? payload = null;
        DateTimeOffset? availableAtUtc = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                return null;
            }

            switch (property.Name)
            {
                case "type" when property.Value.ValueKind == JsonValueKind.String:
                    type = property.Value.GetString();
                    break;
                case "payload":
                    payload = property.Value.GetRawText();
                    break;
                case "availableAtUtc" when property.Value.ValueKind == JsonValueKind.String
                                                   && property.Value.TryGetDateTimeOffset(out var value):
                    availableAtUtc = value;
                    break;
                default:
                    return null;
            }
        }

        return type is null || payload is null
            ? null
            : new ParsedSubmitJobRequest(type, payload, availableAtUtc);
    }
}

internal sealed class RequestBodyTooLargeException : Exception;
