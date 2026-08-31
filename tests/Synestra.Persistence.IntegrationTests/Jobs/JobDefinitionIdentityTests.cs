using Microsoft.EntityFrameworkCore;
using Synestra.Domain.Jobs;
using Testcontainers.PostgreSql;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Jobs;

public sealed class JobDefinitionIdentityTests
{
    [Fact]
    public async Task Database_EnforcesJobDefinitionRelationship()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using (var migrationContext = new SynestraDbContext(options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var definition = new JobDefinition(
            "browser.capture-page",
            "Capture page",
            description: null,
            isEnabled: true,
            createdAtUtc: DateTime.UtcNow);

        await using (var arrangeContext = new SynestraDbContext(options))
        {
            arrangeContext.JobDefinitions.Add(definition);
            arrangeContext.Jobs.Add(CreateJob(definition.Id, definition.Type));
            await arrangeContext.SaveChangesAsync();
        }

        await using (var readContext = new SynestraDbContext(options))
        {
            var persistedJob = await readContext.Jobs.SingleAsync();
            Assert.Equal(definition.Id, persistedJob.JobDefinitionId);
            Assert.Equal(definition.Type, persistedJob.Type);
        }

        await using (var invalidJobContext = new SynestraDbContext(options))
        {
            invalidJobContext.Jobs.Add(CreateJob(Guid.CreateVersion7(), definition.Type));
            await Assert.ThrowsAsync<DbUpdateException>(() => invalidJobContext.SaveChangesAsync());
        }

        await using (var mismatchedTypeContext = new SynestraDbContext(options))
        {
            mismatchedTypeContext.Jobs.Add(CreateJob(definition.Id, "browser.execute-script"));
            await Assert.ThrowsAsync<DbUpdateException>(() => mismatchedTypeContext.SaveChangesAsync());
        }

        await using (var deleteContext = new SynestraDbContext(options))
        {
            var persistedDefinition = await deleteContext.JobDefinitions.SingleAsync();
            deleteContext.JobDefinitions.Remove(persistedDefinition);
            await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        }
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
