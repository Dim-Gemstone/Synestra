using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Jobs;

[Collection(PostgreSqlCollection.Name)]
public sealed class GetJobPersistenceTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task GetJob_ReturnsPersistedJobFromPostgreSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(cancellationToken);
        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        var definition = new JobDefinition(
            $"test.{Guid.NewGuid():N}",
            "Test definition",
            null,
            true,
            DateTime.UtcNow);
        var createdAtUtc = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var availableAtUtc = createdAtUtc.AddMinutes(5);
        var job = new Job(definition.Id, definition.Type, "{\"secret\":\"value\"}", 0, 1, createdAtUtc, availableAtUtc);
        await using (var writeContext = CreateContext(database.ConnectionString))
        {
            writeContext.JobDefinitions.Add(definition);
            writeContext.Jobs.Add(job);
            await writeContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddPersistence(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await new GetJob(scope.ServiceProvider.GetRequiredService<IGetJobPersistence>())
            .ExecuteAsync(job.Id, cancellationToken);

        Assert.Equal(GetJobOutcome.Succeeded, result.Outcome);
        var details = Assert.IsType<JobDetails>(result.Job);
        Assert.Equal(job.Id, details.Id);
        Assert.Equal(definition.Type, details.Type);
        Assert.Equal(JobStatus.Pending, details.Status);
        Assert.Equal(0, details.Priority);
        Assert.Equal(1, details.MaxAttempts);
        Assert.Equal(createdAtUtc, details.CreatedAtUtc);
        Assert.Equal(availableAtUtc, details.AvailableAtUtc);
    }

    private static SynestraDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SynestraDbContext(options);
    }
}
