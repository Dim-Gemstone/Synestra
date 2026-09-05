using System.Text.Json;
using Synestra.Application.Workers;

namespace Synestra.Api.WorkerApi;

internal static class RegistrationContract
{
    public const int MaximumRequestSizeInBytes = 64 * 1024;

    public static async Task<RegisterWorkerRequest?> ParseAsync(HttpRequest request, Guid workerId, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.ContentLength > MaximumRequestSizeInBytes)
        {
            return null;
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumRequestSizeInBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        try
        {
            return ParseBody(buffer.ToArray(), workerId);
        }
        catch (InvalidOperationException exception)
        {
            // JsonDocument can defer rejecting invalid Unicode until a string/name is decoded.
            throw new JsonException("Request text must contain valid Unicode.", exception);
        }
    }

    private static RegisterWorkerRequest? ParseBody(ReadOnlyMemory<byte> body, Guid workerId)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Guid? sessionId = null;
        string? name = null;
        int? capacity = null;
        List<string>? types = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return null;
            }
            switch (property.Name)
            {
                case "sessionId" when property.Value.ValueKind == JsonValueKind.String
                                      && Guid.TryParse(property.Value.GetString(), out var id):
                    sessionId = id;
                    break;
                case "name" when property.Value.ValueKind == JsonValueKind.String:
                    name = property.Value.GetString();
                    break;
                case "capacity" when property.Value.ValueKind == JsonValueKind.Number
                                     && property.Value.TryGetInt32(out var value):
                    capacity = value;
                    break;
                case "supportedTypes" when property.Value.ValueKind == JsonValueKind.Array:
                    types = [];
                    foreach (var type in property.Value.EnumerateArray())
                    {
                        if (type.ValueKind != JsonValueKind.String || types.Count == 100)
                        {
                            return null;
                        }

                        types.Add(type.GetString()!);
                    }

                    break;
                default:
                    return null;
            }
        }

        return sessionId is null || name is null || capacity is null || types is null
            ? null : new RegisterWorkerRequest(workerId, sessionId.Value, name, capacity.Value, types);
    }
}
