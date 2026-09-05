using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Application.Tests.Jobs;

public sealed class GetJobTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsPersistedJobDetails()
    {
        var details = Details();
        var result = await new GetJob(new FakePersistence(details)).ExecuteAsync(
            details.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(GetJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(details, result.Job);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNotFoundWhenJobDoesNotExist()
    {
        var result = await new GetJob(new FakePersistence(null)).ExecuteAsync(
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.Equal(GetJobOutcome.NotFound, result.Outcome);
        Assert.Null(result.Job);
    }

    private static JobDetails Details() => new(
        Guid.CreateVersion7(),
        "browser.capture-page",
        JobStatus.Pending,
        0,
        1,
        new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 5, 10, 5, 0, DateTimeKind.Utc));

    private sealed class FakePersistence(JobDetails? details) : IGetJobPersistence
    {
        public Task<JobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(details?.Id ?? id, id);
            return Task.FromResult(details);
        }
    }
}
