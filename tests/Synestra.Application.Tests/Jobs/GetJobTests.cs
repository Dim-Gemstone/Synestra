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

    [Theory]
    [InlineData(JobAttemptStatus.Succeeded)]
    [InlineData(JobAttemptStatus.Failed)]
    [InlineData(JobAttemptStatus.Abandoned)]
    public async Task ExecuteAsync_PreservesPersistedCompletion(JobAttemptStatus outcome)
    {
        var completion = new JobCompletionDetails(Guid.CreateVersion7(), 6, outcome,
            outcome == JobAttemptStatus.Succeeded ? "{\"sum\":4}" : null,
            outcome == JobAttemptStatus.Succeeded ? null : new("failure", "Stored failure."));
        var details = Details() with
        {
            Status = outcome == JobAttemptStatus.Succeeded ? JobStatus.Succeeded : JobStatus.Failed,
            CompletedAtUtc = Details().CreatedAtUtc.AddMinutes(10),
            Completion = completion
        };
        var result = await new GetJob(new FakePersistence(details)).ExecuteAsync(
            details.Id, TestContext.Current.CancellationToken);

        Assert.Equal(GetJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(details, result.Job);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetJob(new FakePersistence(null)).ExecuteAsync(Guid.CreateVersion7(), cancellation.Token));
    }

    private static GetJobDetails Details() => new(
        Guid.CreateVersion7(),
        "browser.capture-page",
        JobStatus.Pending,
        0,
        1,
        new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 5, 10, 5, 0, DateTimeKind.Utc), null, null);

    private sealed class FakePersistence(GetJobDetails? details) : IGetJobPersistence
    {
        public Task<GetJobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(details?.Id ?? id, id);
            return Task.FromResult(details);
        }
    }
}
