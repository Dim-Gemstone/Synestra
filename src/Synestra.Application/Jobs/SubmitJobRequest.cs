namespace Synestra.Application.Jobs;

public sealed record SubmitJobRequest(
    string Type,
    string Payload,
    DateTimeOffset? AvailableAtUtc = null,
    string? IdempotencyKey = null);
