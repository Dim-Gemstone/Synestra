using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Jobs;

[Collection(PostgreSqlCollection.Name)]
public sealed class SubmitJobPersistenceTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task SubmitJob_PersistsJobWithoutAttemptInOneTransaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(cancellationToken);
        await MigrateAsync(database.ConnectionString, cancellationToken);
        var definition = await AddDefinitionAsync(database.ConnectionString, enabled: true, cancellationToken);

        await using var provider = CreateProvider(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var useCase = new SubmitJob(
            scope.ServiceProvider.GetRequiredService<ISubmitJobPersistence>(),
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));

        var result = await useCase.ExecuteAsync(
            new SubmitJobRequest(definition.Type, "{\"url\":\"https://example.com\"}"),
            cancellationToken);

        Assert.Equal(SubmitJobOutcome.Succeeded, result.Outcome);
        await using var readContext = CreateContext(database.ConnectionString);
        var job = await readContext.Jobs.Include(x => x.Attempts).SingleAsync(x => x.Id == result.Job!.Id, cancellationToken);
        Assert.Equal(definition.Id, job.JobDefinitionId);
        Assert.Equal(definition.Type, job.Type);
        Assert.Empty(job.Attempts);
    }

    [Fact]
    public async Task SubmitJob_DoesNotPersistForMissingOrDisabledDefinition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(cancellationToken);
        await MigrateAsync(database.ConnectionString, cancellationToken);
        var disabled = await AddDefinitionAsync(database.ConnectionString, enabled: false, cancellationToken);
        await using var provider = CreateProvider(database.ConnectionString);

        var disabledResult = await ExecuteAsync(provider, disabled.Type, cancellationToken);
        var missingResult = await ExecuteAsync(provider, $"missing-{Guid.NewGuid():N}", cancellationToken);

        Assert.Equal(SubmitJobOutcome.DefinitionDisabled, disabledResult.Outcome);
        Assert.Equal(SubmitJobOutcome.DefinitionNotFound, missingResult.Outcome);
        await using var readContext = CreateContext(database.ConnectionString);
        Assert.False(await readContext.Jobs.AnyAsync(x => x.Type == disabled.Type, cancellationToken));
    }

    [Fact]
    public async Task DefinitionShareLock_OrdersConcurrentDisableBeforeLaterSubmission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(cancellationToken);
        await MigrateAsync(database.ConnectionString, cancellationToken);
        var definition = await AddDefinitionAsync(database.ConnectionString, enabled: true, cancellationToken);
        await using var provider = CreateProvider(database.ConnectionString);
        await using var submissionScope = provider.CreateAsyncScope();
        var persistence = submissionScope.ServiceProvider.GetRequiredService<ISubmitJobPersistence>();
        await using var submission = await persistence.BeginTransactionAsync(cancellationToken);

        var lockedDefinition = await submission.FindDefinitionForSubmissionAsync(definition.Type, cancellationToken);
        Assert.True(lockedDefinition!.IsEnabled);

        await using var disableContext = CreateContext(database.ConnectionString);
        var disableTask = disableContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE job_definitions SET is_enabled = FALSE WHERE id = {definition.Id}",
            cancellationToken);
        await Task.Delay(200, cancellationToken);
        Assert.False(disableTask.IsCompleted);

        var job = new Job(definition.Id, definition.Type, "{}", 0, 1, DateTime.UtcNow, DateTime.UtcNow);
        submission.Add(job);
        await submission.CommitAsync(cancellationToken);
        Assert.Equal(1, await disableTask);

        var laterResult = await ExecuteAsync(provider, definition.Type, cancellationToken);
        Assert.Equal(SubmitJobOutcome.DefinitionDisabled, laterResult.Outcome);
        await using var readContext = CreateContext(database.ConnectionString);
        Assert.Equal(1, await readContext.Jobs.CountAsync(x => x.Type == definition.Type, cancellationToken));
    }

    private static async Task<SubmitJobResult> ExecuteAsync(
        ServiceProvider provider,
        string type,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        var useCase = new SubmitJob(
            scope.ServiceProvider.GetRequiredService<ISubmitJobPersistence>(),
            TimeProvider.System);
        return await useCase.ExecuteAsync(new SubmitJobRequest(type, "{}"), cancellationToken);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPersistence(connectionString);
        return services.BuildServiceProvider();
    }

    private static SynestraDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SynestraDbContext(options);
    }

    private static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task<JobDefinition> AddDefinitionAsync(
        string connectionString,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var definition = new JobDefinition(
            $"test.{Guid.NewGuid():N}",
            "Test definition",
            null,
            enabled,
            DateTime.UtcNow);
        await using var context = CreateContext(connectionString);
        context.JobDefinitions.Add(definition);
        await context.SaveChangesAsync(cancellationToken);
        return definition;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
