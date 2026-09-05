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
        var job = Assert.IsType<JobDetails>(result.Job);
        Assert.Equal(definition.Id, persistence.Transaction.AddedJob!.JobDefinitionId);
        Assert.Equal(definition.Type, job.Type);
        Assert.Equal(0, job.Priority);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(Now.UtcDateTime, job.CreatedAtUtc);
        Assert.Equal(job.CreatedAtUtc, job.AvailableAtUtc);
        Assert.Empty(persistence.Transaction.AddedJob.Attempts);
        Assert.False(persistence.Transaction.KeyLocked);
        Assert.Null(persistence.Transaction.AddedSubmission);
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

    [Fact]
    public async Task KeyedSubmission_CreatesJobAndStoresIdentityWithSuccess()
    {
        var persistence = new FakePersistence(Definition(true));
        var request = new SubmitJobRequest("browser.capture-page", "{ \"value\": 1 }", Now.AddMinutes(1), "request-1");

        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now))
            .ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome);
        Assert.True(persistence.Transaction.KeyLocked);
        Assert.True(persistence.Transaction.Committed);
        Assert.Equal(result.Job, persistence.Transaction.AddedSubmission!.Job);
        Assert.Equal(new SubmitJobIdentity(request.Type, request.Payload, request.AvailableAtUtc),
            persistence.Transaction.AddedSubmission.Identity);
        Assert.Equal(result.Job!.Id, persistence.Transaction.AddedJob!.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replay_ReturnsOriginalSuccessWithoutRecheckingTimeOrDefinition(bool definitionExists)
    {
        var original = new JobSubmission(
            new SubmitJobIdentity("browser.capture-page", "{}", Now),
            new JobDetails(Guid.CreateVersion7(), "browser.capture-page", JobStatus.Pending, 0, 1,
                Now.UtcDateTime, Now.UtcDateTime));
        var persistence = new FakePersistence(definitionExists ? Definition(false) : null, original);

        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now.AddDays(1))).ExecuteAsync(
            new SubmitJobRequest(original.Identity.Type, "{}", Now, "request-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(original.Job, result.Job);
        Assert.False(persistence.Transaction.DefinitionRead);
        Assert.Null(persistence.Transaction.AddedJob);
        Assert.Null(persistence.Transaction.AddedSubmission);
        Assert.False(persistence.Transaction.Committed);
    }

    [Theory]
    [InlineData("another.type", "{}", false)]
    [InlineData("browser.capture-page", "{ }", false)]
    [InlineData("browser.capture-page", "{\"value\":1}", false)]
    [InlineData("browser.capture-page", "{}", true)]
    public async Task ConflictingReplay_HasSeparateOutcome(string type, string payload, bool explicitAvailability)
    {
        var original = new JobSubmission(
            new SubmitJobIdentity("browser.capture-page", "{}", null),
            new JobDetails(Guid.CreateVersion7(), "browser.capture-page", JobStatus.Pending, 0, 1,
                Now.UtcDateTime, Now.UtcDateTime));
        var persistence = new FakePersistence(null, original);

        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now.AddDays(1))).ExecuteAsync(
            new SubmitJobRequest(type, payload, explicitAvailability ? Now : null, "request-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.IdempotencyKeyConflict, result.Outcome);
        Assert.Null(result.Job);
        Assert.False(persistence.Transaction.DefinitionRead);
        Assert.Null(persistence.Transaction.AddedJob);
        Assert.False(persistence.Transaction.Committed);
    }

    [Theory]
    [InlineData(true, SubmitJobOutcome.DefinitionDisabled)]
    [InlineData(false, SubmitJobOutcome.DefinitionNotFound)]
    public async Task UnusedKey_PreservesDefinitionErrors(bool definitionExists, SubmitJobOutcome outcome)
    {
        var persistence = new FakePersistence(definitionExists ? Definition(false) : null);
        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", IdempotencyKey: "request-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.Null(persistence.Transaction.AddedJob);
        Assert.Null(persistence.Transaction.AddedSubmission);
        Assert.False(persistence.Transaction.Committed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("with space")]
    [InlineData("a,b")]
    [InlineData("a\tb")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\u007fb")]
    [InlineData("é")]
    public async Task InvalidKey_IsRejectedBeforePersistence(string key)
    {
        var persistence = new FakePersistence(Definition(true));
        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", IdempotencyKey: key),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.InvalidRequest, result.Outcome);
        Assert.False(persistence.BeganTransaction);
    }

    [Theory]
    [InlineData(128, SubmitJobOutcome.Succeeded)]
    [InlineData(129, SubmitJobOutcome.InvalidRequest)]
    public async Task KeyLength_IsBounded(int length, SubmitJobOutcome expected)
    {
        var result = await new SubmitJob(new FakePersistence(Definition(true)), new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", IdempotencyKey: new string('a', length)),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task Replay_StillRejectsInvalidPayloadBeforePersistence()
    {
        var persistence = new FakePersistence(Definition(true));
        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{\"a\":1,\"a\":2}", IdempotencyKey: "request-1"),
            TestContext.Current.CancellationToken);
        Assert.Equal(SubmitJobOutcome.InvalidRequest, result.Outcome);
        Assert.False(persistence.BeganTransaction);
    }

    [Fact]
    public async Task UnusedKey_RejectsPastAvailabilityWithoutReservingKey()
    {
        var persistence = new FakePersistence(Definition(true));
        var result = await new SubmitJob(persistence, new FixedTimeProvider(Now)).ExecuteAsync(
            new SubmitJobRequest("browser.capture-page", "{}", Now.AddTicks(-1), "request-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SubmitJobOutcome.InvalidRequest, result.Outcome);
        Assert.True(persistence.Transaction.KeyLocked);
        Assert.False(persistence.Transaction.DefinitionRead);
        Assert.Null(persistence.Transaction.AddedSubmission);
        Assert.Null(persistence.Transaction.AddedJob);
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

    private sealed class FakePersistence(JobDefinition? definition, JobSubmission? existing = null) : ISubmitJobPersistence
    {
        public FakeTransaction Transaction { get; } = new(definition) { ExistingSubmission = existing };
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
        public JobSubmission? ExistingSubmission { get; init; }
        public JobSubmission? AddedSubmission { get; private set; }
        public bool KeyLocked { get; private set; }
        public bool DefinitionRead { get; private set; }
        public bool Committed { get; private set; }
        public Job? AddedJob { get; private set; }

        public Task<JobDefinition?> FindDefinitionForSubmissionAsync(string type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DefinitionRead = true;
            return Task.FromResult(definition);
        }

        public Task<JobSubmission?> LockIdempotencyKeyAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyLocked = true;
            return Task.FromResult(ExistingSubmission);
        }

        public void AddSubmission(string key, JobSubmission submission) => AddedSubmission = submission;

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
