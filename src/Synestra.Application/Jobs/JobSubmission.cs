namespace Synestra.Application.Jobs;

public sealed record SubmitJobIdentity(string Type, string Payload, DateTimeOffset? AvailableAtUtc);

public sealed record JobSubmission(SubmitJobIdentity Identity, JobDetails Job);
