using System.Text;
using System.Text.Json;
using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public sealed class SubmitJob(ISubmitJobPersistence persistence, TimeProvider timeProvider)
{
    public const int MaximumPayloadSizeInBytes = 256 * 1024;
    public const int MaximumPayloadDepth = 32;

    public async Task<SubmitJobResult> ExecuteAsync(SubmitJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > 100)
        {
            return Invalid("Type must be a non-empty string no longer than 100 characters.");
        }

        var payloadError = ValidatePayload(request.Payload);
        if (payloadError is not null)
        {
            return payloadError;
        }

        var createdAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (request.AvailableAtUtc is { Offset.Ticks: not 0 })
        {
            return Invalid("Availability time must have an explicit UTC offset.");
        }

        var availableAtUtc = request.AvailableAtUtc?.UtcDateTime ?? createdAtUtc;
        if (availableAtUtc < createdAtUtc)
        {
            return Invalid("Availability time cannot precede creation time.");
        }

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var definition = await transaction.FindDefinitionForSubmissionAsync(request.Type, cancellationToken);
        if (definition is null)
        {
            return new SubmitJobResult(SubmitJobOutcome.DefinitionNotFound);
        }

        if (!definition.IsEnabled)
        {
            return new SubmitJobResult(SubmitJobOutcome.DefinitionDisabled);
        }

        var job = new Job(definition.Id, definition.Type, request.Payload, 0, 1, createdAtUtc, availableAtUtc);
        transaction.Add(job);
        await transaction.CommitAsync(cancellationToken);
        return new SubmitJobResult(SubmitJobOutcome.Succeeded, job);
    }

    private static SubmitJobResult? ValidatePayload(string? payload)
    {
        if (payload is null)
        {
            return Invalid("Payload is required.");
        }

        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadSizeInBytes)
        {
            return new SubmitJobResult(SubmitJobOutcome.PayloadTooLarge, Error: "Payload cannot exceed 256 KiB in UTF-8.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = MaximumPayloadDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Payload must be a JSON object.");
            }

            if (ContainsDuplicateProperty(document.RootElement))
            {
                return Invalid("Payload cannot contain duplicate property names.");
            }
        }
        catch (JsonException)
        {
            return Invalid("Payload must be valid JSON with a maximum depth of 32.");
        }

        return null;
    }

    private static bool ContainsDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsDuplicateProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static SubmitJobResult Invalid(string error) => new(SubmitJobOutcome.InvalidRequest, Error: error);
}
