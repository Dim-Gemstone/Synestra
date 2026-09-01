using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Domain.Tests.Jobs;

public sealed class JobTests
{
    [Fact]
    public void Constructor_PreservesDefinitionIdentityAndTypeSnapshot()
    {
        var definitionId = Guid.CreateVersion7();

        var job = CreateJob(definitionId, "browser.capture-page");

        Assert.Equal(definitionId, job.JobDefinitionId);
        Assert.Equal("browser.capture-page", job.Type);
    }

    [Fact]
    public void Constructor_RejectsEmptyDefinitionId()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateJob(Guid.Empty, "browser.capture-page"));

        Assert.Equal("jobDefinitionId", exception.ParamName);
    }

    [Fact]
    public void Constructor_CreatesPendingJobWithoutAttemptsUsingVersion7Id()
    {
        var job = CreateJob(Guid.CreateVersion7(), "browser.capture-page");

        Assert.Equal(7, job.Id.Version);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Empty(job.Attempts);
        Assert.Null(job.CompletedAtUtc);
    }

    [Fact]
    public void Constructor_RejectsAvailabilityBeforeCreation()
    {
        var createdAtUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new Job(
            Guid.CreateVersion7(),
            "browser.capture-page",
            "{}",
            0,
            1,
            createdAtUtc,
            createdAtUtc.AddTicks(-1)));

        Assert.Equal("availableAtUtc", exception.ParamName);
    }

    private static Job CreateJob(Guid definitionId, string type)
    {
        return new Job(
            definitionId,
            type,
            "{}",
            priority: 0,
            maxAttempts: 1,
            createdAtUtc: DateTime.UtcNow,
            availableAtUtc: DateTime.UtcNow);
    }
}
