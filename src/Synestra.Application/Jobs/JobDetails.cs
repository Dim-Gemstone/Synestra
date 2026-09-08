using Synestra.Domain.Jobs;

namespace Synestra.Application.Jobs;

public sealed record JobDetails(
    Guid Id,
    string Type,
    JobStatus Status,
    int Priority,
    int MaxAttempts,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc);
