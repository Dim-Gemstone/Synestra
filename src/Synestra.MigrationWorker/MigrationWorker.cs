using Microsoft.EntityFrameworkCore;
using Synestra.Persistence;

namespace Synestra.MigrationWorker;

public sealed class MigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Applying database migrations");

            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
            var executionStrategy = dbContext.Database.CreateExecutionStrategy();

            await executionStrategy.ExecuteAsync(
                () => dbContext.Database.MigrateAsync(stoppingToken));

            logger.LogInformation("Database migrations applied successfully");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Database migration was cancelled");
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            logger.LogCritical(exception, "Database migration failed");
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}
