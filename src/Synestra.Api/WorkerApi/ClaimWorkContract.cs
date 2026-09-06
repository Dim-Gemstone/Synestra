using System.Text.Json;

namespace Synestra.Api.WorkerApi;

public sealed record ClaimWorkResponse(
    Guid JobId, Guid AttemptId, Guid LeaseId, int AttemptNumber, string Type, JsonElement Payload,
    DateTime AcquiredAtUtc, DateTime ExpiresAtUtc);
