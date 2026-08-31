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
