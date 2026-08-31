using Microsoft.EntityFrameworkCore;
using Synestra.Domain.Jobs;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Jobs;

[Collection(PostgreSqlCollection.Name)]
public sealed class JobDefinitionIdentityTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Database_EnforcesJobDefinitionRelationship()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var migrationContext = new SynestraDbContext(options))
        {
            await migrationContext.Database.MigrateAsync(cancellationToken);
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
            await arrangeContext.SaveChangesAsync(cancellationToken);
        }

        await using (var readContext = new SynestraDbContext(options))
        {
            var persistedJob = await readContext.Jobs.SingleAsync(cancellationToken);
            Assert.Equal(definition.Id, persistedJob.JobDefinitionId);
            Assert.Equal(definition.Type, persistedJob.Type);
        }

        await using (var invalidJobContext = new SynestraDbContext(options))
        {
            invalidJobContext.Jobs.Add(CreateJob(Guid.CreateVersion7(), definition.Type));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => invalidJobContext.SaveChangesAsync(cancellationToken));
        }

        await using (var mismatchedTypeContext = new SynestraDbContext(options))
        {
            mismatchedTypeContext.Jobs.Add(CreateJob(definition.Id, "browser.execute-script"));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => mismatchedTypeContext.SaveChangesAsync(cancellationToken));
        }

        await using (var deleteContext = new SynestraDbContext(options))
        {
            var persistedDefinition = await deleteContext.JobDefinitions.SingleAsync(cancellationToken);
            deleteContext.JobDefinitions.Remove(persistedDefinition);
            await Assert.ThrowsAsync<DbUpdateException>(
                () => deleteContext.SaveChangesAsync(cancellationToken));
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
