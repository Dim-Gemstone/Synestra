namespace Synestra.Api.ClientApi;

public sealed record GetJobResponse(
    Guid Id,
    string Type,
    string Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc);
