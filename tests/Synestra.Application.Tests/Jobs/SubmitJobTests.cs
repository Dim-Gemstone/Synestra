using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Application.Tests.Jobs;

public sealed class SubmitJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 11, 55, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_CreatesJobWithServerValuesAndCommits()
    {
        var definition = Definition(isEnabled: true);
        var persistence = new FakePersistence(definition);
        var useCase = new SubmitJob(persistence, new FixedTimeProvider(Now));

        var result = await useCase.ExecuteAsync(
            new SubmitJobRequest(definition.Type, "{\"url\":\"https://example.com\"}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome);
        var job = Assert.IsType<Job>(result.Job);
        Assert.Equal(definition.Id, job.JobDefinitionId);
        Assert.Equal(definition.Type, job.Type);
        Assert.Equal(0, job.Priority);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(Now.UtcDateTime, job.CreatedAtUtc);
        Assert.Equal(job.CreatedAtUtc, job.AvailableAtUtc);
        Assert.Empty(job.Attempts);
        Assert.True(persistence.Transaction.Committed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{\"value\":1,\"value\":2}")]
    public async Task ExecuteAsync_RejectsInvalidPayload(string payload)
    {
        var persistence = new FakePersistence(Definition(isEnabled: true));
        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", payload),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.InvalidRequest, result.Outcome);
        Assert.False(persistence.BeganTransaction);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsOversizedUtf8Payload()
    {
        var payload = "{\"value\":\"" + new string('é', SubmitJob.MaximumPayloadSizeInBytes / 2) + "\"}";
        var result = await ExecuteAsync(Definition(true), payload);
        Assert.Equal(SubmitJobOutcome.PayloadTooLarge, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_DistinguishesMissingAndDisabledDefinitions()
    {
        var missing = await ExecuteAsync(null, "{}");
        var disabled = await ExecuteAsync(Definition(false), "{}");

        Assert.Equal(SubmitJobOutcome.DefinitionNotFound, missing.Outcome);
        Assert.Equal(SubmitJobOutcome.DefinitionDisabled, disabled.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNonUtcOrPastAvailability()
    {
        var useCase = new SubmitJob(new FakePersistence(Definition(true)), new FixedTimeProvider(Now));
        var nonUtc = await useCase.ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", Now.ToOffset(TimeSpan.FromHours(2))),
            TestContext.Current.CancellationToken);
        var past = await useCase.ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", Now.AddTicks(-1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.InvalidRequest, nonUtc.Outcome);
        Assert.Equal(SubmitJobOutcome.InvalidRequest, past.Outcome);
    }

    private static async Task<SubmitJobResult> ExecuteAsync(JobDefinition? definition, string payload)
    {
        return await new SubmitJob(new FakePersistence(definition), new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", payload),
            TestContext.Current.CancellationToken);
    }

    private static JobDefinition Definition(bool isEnabled) =>
        new("browser.capture-page", "Capture page", null, isEnabled, Now.UtcDateTime);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakePersistence(JobDefinition? definition) : ISubmitJobPersistence
    {
        public FakeTransaction Transaction { get; } = new(definition);
        public bool BeganTransaction { get; private set; }

        public Task<ISubmitJobTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeganTransaction = true;
            return Task.FromResult<ISubmitJobTransaction>(Transaction);
        }
    }

    private sealed class FakeTransaction(JobDefinition? definition) : ISubmitJobTransaction
    {
        public bool Committed { get; private set; }
        public Job? AddedJob { get; private set; }

        public Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(definition);
        }

        public void Add(Job job) => AddedJob = job;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
