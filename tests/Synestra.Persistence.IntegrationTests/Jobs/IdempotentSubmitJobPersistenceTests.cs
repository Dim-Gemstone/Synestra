using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Jobs;

[Collection(PostgreSqlCollection.Name)]
public sealed class IdempotentSubmitJobPersistenceTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
    private PostgreSqlTestDatabase _database = null!;
    private ServiceProvider _provider = null!;
    private JobDefinition _definition = null!;
    private CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(CancellationToken);
        var services = new ServiceCollection();
        services.AddPersistence(_database.ConnectionString);
        _provider = services.BuildServiceProvider();
        await using var context = CreateContext();
        await context.Database.MigrateAsync(CancellationToken);
        _definition = new JobDefinition("test.work", "Test work", null, true, Now.UtcDateTime);
        context.JobDefinitions.Add(_definition);
        await context.SaveChangesAsync(CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Replay_FromNewScopePreservesOriginalIdentityAndTimestampPrecision()
    {
        var request = Request("{ \"b\": 2, \"a\": \"\\u0061\" }") with { AvailableAtUtc = Now.AddMinutes(1) };
        var original = await ExecuteAsync(request);
        Assert.Equal(SubmitJobOutcome.Succeeded, original.Outcome);

        await using (var context = CreateContext())
        {
            Assert.Equal(original.Job!.Id, (await context.Jobs.SingleAsync(CancellationToken)).Id);
            Assert.Equal(1, await SubmissionCountAsync(context));
            // Simulate later persisted state without inventing Domain execution transitions.
            await context.Database.ExecuteSqlRawAsync("UPDATE jobs SET status = 'Succeeded'", CancellationToken);
            await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = FALSE", CancellationToken);
        }

        var replay = await ExecuteAsync(request, Now.AddDays(1));
        Assert.Equal(original, replay);
        Assert.Equal(7, replay.Job!.CreatedAtUtc.Ticks % 10);
        Assert.Equal(7, replay.Job.AvailableAtUtc.Ticks % 10);
        var reformatted = await ExecuteAsync(request with { Payload = "{\"a\":\"a\",\"b\":2}" });
        Assert.Equal(SubmitJobOutcome.IdempotencyKeyConflict, reformatted.Outcome);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequests_CreateExactlyOneJob()
    {
        await using var gateScope = _provider.CreateAsyncScope();
        await using var gate = await Persistence(gateScope).BeginTransactionAsync(CancellationToken);
        Assert.Null(await gate.LockIdempotencyKeyAsync("request-1", CancellationToken));

        var requests = Enumerable.Range(0, 8).Select(_ => ExecuteAsync(Request())).ToArray();
        await WaitForAdvisoryWaitersAsync(requests.Length);
        await gate.CommitAsync(CancellationToken);
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome));
        Assert.Single(results.Select(result => result.Job).Distinct());
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task ConcurrentConflictingRequests_AcceptOneIdentityAndRejectTheOther()
    {
        await using var gateScope = _provider.CreateAsyncScope();
        await using var gate = await Persistence(gateScope).BeginTransactionAsync(CancellationToken);
        Assert.Null(await gate.LockIdempotencyKeyAsync("request-1", CancellationToken));

        var identities = new[] { Request("{\"value\":1}"), Request("{\"value\":2}") };
        var requests = identities.Select(request => ExecuteAsync(request)).ToArray();
        await WaitForAdvisoryWaitersAsync(requests.Length);
        await gate.CommitAsync(CancellationToken);
        var results = await Task.WhenAll(requests);

        Assert.Single(results, result => result.Outcome == SubmitJobOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == SubmitJobOutcome.IdempotencyKeyConflict);
        for (var index = 0; index < identities.Length; index++)
        {
            Assert.Equal(results[index], await ExecuteAsync(identities[index]));
        }

        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Rollback_RemovesJobAndIdentityAndLetsWaitingRequestCreateJob()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        Task<SubmitJobResult> waiting;
        Guid rolledBackId;
        await using (var transaction = await Persistence(scope).BeginTransactionAsync(CancellationToken))
        {
            Assert.Null(await transaction.LockIdempotencyKeyAsync("request-1", CancellationToken));
            var job = NewJob();
            rolledBackId = job.Id;
            transaction.Add(job);
            transaction.AddSubmission("request-1", Submission(job));
            await context.SaveChangesAsync(CancellationToken);
            Assert.Equal(1, await SubmissionCountAsync(context));
            await AssertCountsAsync(0, 0);

            waiting = ExecuteAsync(Request());
            await WaitForAdvisoryWaitersAsync(1);
        }

        var result = await waiting;
        Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome);
        Assert.NotEqual(rolledBackId, result.Job!.Id);
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task FailedSave_DoesNotReserveKeyOrLeakTrackedInsertsIntoNextSubmission()
    {
        await using var scope = _provider.CreateAsyncScope();
        var useCase = new SubmitJob(Persistence(scope), new FixedTimeProvider(Now));
        // Valid JSON that PostgreSQL jsonb cannot store forces a real database write failure.
        await Assert.ThrowsAsync<DbUpdateException>(() => useCase.ExecuteAsync(
            Request("{\"value\":\"\\u0000\"}"), CancellationToken));
        await AssertCountsAsync(0, 0);

        var successful = await useCase.ExecuteAsync(Request(), CancellationToken);
        Assert.Equal(SubmitJobOutcome.Succeeded, successful.Outcome);
        Assert.Equal(successful, await ExecuteAsync(Request()));
        await AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData("missing.work", SubmitJobOutcome.DefinitionNotFound)]
    [InlineData("test.work", SubmitJobOutcome.DefinitionDisabled)]
    public async Task FailedEligibility_DoesNotReserveKey(string type, SubmitJobOutcome expected)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = FALSE", CancellationToken);
        var result = await ExecuteAsync(Request() with { Type = type });
        Assert.Equal(expected, result.Outcome);
        await AssertCountsAsync(0, 0);

        await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = TRUE", CancellationToken);
        Assert.Equal(SubmitJobOutcome.Succeeded, (await ExecuteAsync(Request())).Outcome);
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task DifferentKeysAndUnkeyedRequests_CreateIndependentJobs()
    {
        foreach (var key in new string?[] { "request-1", "Request-1", null, null })
        {
            Assert.Equal(SubmitJobOutcome.Succeeded, (await ExecuteAsync(Request() with { IdempotencyKey = key })).Outcome);
        }

        await AssertCountsAsync(4, 2);
    }

    [Fact]
    public async Task Constraints_EnforceUniqueKeyUniqueJobAndRequiredExistingJob()
    {
        var first = await ExecuteAsync(Request());
        await using var context = CreateContext();
        var anotherJob = NewJob();
        context.Jobs.Add(anotherJob);
        await context.SaveChangesAsync(CancellationToken);

        await AssertInsertRejectedAsync("request-1", anotherJob.Id, PostgresErrorCodes.UniqueViolation,
            "PK_job_submissions");
        await AssertInsertRejectedAsync("request-2", first.Job!.Id, PostgresErrorCodes.UniqueViolation,
            "IX_job_submissions_job_id");
        await AssertInsertRejectedAsync("request-2", Guid.CreateVersion7(), PostgresErrorCodes.ForeignKeyViolation,
            "FK_job_submissions_jobs_job_id");
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM jobs WHERE id = {first.Job.Id}", CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deletion.SqlState);
        var nullIdentity = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE job_submissions SET request_identity = NULL", CancellationToken));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nullIdentity.SqlState);
        var nullResponse = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE job_submissions SET response = NULL", CancellationToken));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nullResponse.SqlState);
        await AssertCountsAsync(2, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("a,b")]
    [InlineData("a\nb")]
    [InlineData("é")]
    public async Task Constraints_RejectInvalidKeys(string key)
    {
        await ExecuteAsync(Request());
        await AssertInsertRejectedAsync(key, Guid.CreateVersion7(), PostgresErrorCodes.CheckViolation,
            "CK_job_submissions_key_format");
    }

    private async Task AssertInsertRejectedAsync(string key, Guid jobId, string sqlState, string constraint)
    {
        await using var context = CreateContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO job_submissions (key, job_id, request_identity, response)
            SELECT {key}, {jobId}, request_identity, response FROM job_submissions LIMIT 1
            """, CancellationToken));
        Assert.Equal(sqlState, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    private async Task WaitForAdvisoryWaitersAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var context = CreateContext();
        while (await context.Database.SqlQueryRaw<int>("""
                   SELECT count(*)::int AS "Value" FROM pg_locks
                   WHERE locktype = 'advisory' AND NOT granted
                     AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                   """).SingleAsync(timeout.Token) < count)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private SubmitJobRequest Request(string payload = "{}") => new(_definition.Type, payload, IdempotencyKey: "request-1");

    private async Task<SubmitJobResult> ExecuteAsync(SubmitJobRequest request, DateTimeOffset? now = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new SubmitJob(Persistence(scope), new FixedTimeProvider(now ?? Now)).ExecuteAsync(request, CancellationToken);
    }

    private static ISubmitJobPersistence Persistence(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ISubmitJobPersistence>();

    private Job NewJob() => new(_definition.Id, _definition.Type, "{}", 0, 1, Now.UtcDateTime, Now.UtcDateTime);

    private static JobSubmission Submission(Job job) => new(
        new SubmitJobIdentity(job.Type, job.Payload, null),
        new JobDetails(job.Id, job.Type, job.Status, job.Priority, job.MaxAttempts, job.CreatedAtUtc, job.AvailableAtUtc));

    private async Task AssertCountsAsync(int jobs, int submissions)
    {
        await using var context = CreateContext();
        Assert.Equal(jobs, await context.Jobs.CountAsync(CancellationToken));
        Assert.Equal(submissions, await SubmissionCountAsync(context));
    }

    private Task<int> SubmissionCountAsync(SynestraDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM job_submissions").SingleAsync(CancellationToken);

    private SynestraDbContext CreateContext() => new(
        new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
