using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Executions;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Fact]
    public async Task Discovery_EmptyDatabaseReturnsNoCandidates()
    {
        Assert.Empty(await DiscoverAsync());
        Assert.Empty(await DiscoverAsync(after: new(Now.UtcDateTime, Guid.NewGuid())));
    }

    [Fact]
    public async Task Discovery_ExactCutoffAndNativeUuidOrderingUseExclusiveKeyset()
    {
        var first = await AcquireAsync();
        var boundaryHigh = await AcquireAsync();
        var boundaryLow = await AcquireAsync();
        var future = await AcquireAsync();
        var lowId = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var highId = Guid.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff");
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.AddSeconds(20).UtcDateTime} WHERE id = {first.Work.LeaseId}", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET id = {highId} WHERE id = {boundaryHigh.Work.LeaseId}", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET id = {lowId} WHERE id = {boundaryLow.Work.LeaseId}", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.AddSeconds(30).AddTicks(10).UtcDateTime} WHERE id = {future.Work.LeaseId}", Token);
        var firstPage = await DiscoverAsync(Now.AddSeconds(30).UtcDateTime, limit: 2);
        Assert.Equal([first.Work.LeaseId, lowId], firstPage.Select(item => item.LeaseId));
        var next = await DiscoverAsync(Now.AddSeconds(30).UtcDateTime, firstPage[^1], 2);
        Assert.Equal(highId, Assert.Single(next).LeaseId);
        Assert.Empty(await DiscoverAsync(Now.AddSeconds(30).UtcDateTime, next[^1], 2));
        Assert.Equal(4, (await DiscoverAsync(Now.AddSeconds(30).AddTicks(10).UtcDateTime)).Count);
    }

    [Theory]
    [InlineData("leases", "UPDATE leases SET released_at_utc = acquired_at_utc WHERE id = {0}")]
    [InlineData("leases", "UPDATE leases SET expires_at_utc = acquired_at_utc WHERE id = {0}")]
    [InlineData("jobs", "UPDATE jobs SET status = 'Pending' WHERE id = {0}")]
    [InlineData("jobs", "UPDATE jobs SET completed_at_utc = created_at_utc WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET status = 'Abandoned' WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET finished_at_utc = started_at_utc WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET result = jsonb_build_object() WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET error_code = 'existing' WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET error_message = 'existing' WHERE id = {0}")]
    [InlineData("job_attempts", "UPDATE job_attempts SET started_at_utc = started_at_utc + interval '1 day' WHERE id = {0}")]
    public async Task Discovery_ExcludesIneligiblePersistedState(string table, string sql)
    {
        var excluded = await AcquireAsync();
        var eligible = await AcquireAsync();
        var id = table switch { "jobs" => excluded.Work.JobId, "job_attempts" => excluded.Work.AttemptId, _ => excluded.Work.LeaseId };
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync(sql, [id], Token);
        Assert.Equal(eligible.Work.LeaseId, Assert.Single(await DiscoverAsync()).LeaseId);
    }

    [Theory]
    [InlineData("report")]
    [InlineData("snapshot")]
    public async Task Discovery_ExcludesEitherCompletionMarkerWithoutParsingSnapshot(string marker)
    {
        await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE job_attempts DROP CONSTRAINT "CK_job_attempts_completion_snapshot";
            ALTER TABLE job_attempts DROP CONSTRAINT "CK_job_attempts_reported_completion"
            """, Token);
        if (marker == "report")
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE job_attempts SET completion_report_id = {Guid.CreateVersion7()}", Token);
        else await context.Database.ExecuteSqlRawAsync("UPDATE job_attempts SET completion_snapshot = jsonb_build_object('legacy', true)", Token);
        Assert.Empty(await DiscoverAsync());
    }

    [Theory]
    [InlineData("workers")]
    [InlineData("jobs")]
    [InlineData("job_attempts")]
    public async Task Discovery_ExcludesOrphanRelationshipsWithoutRepair(string table)
    {
        await AcquireAsync();
        await using var context = Context();
        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            await context.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica", Token);
            await context.Database.ExecuteSqlRawAsync(table switch
            {
                "workers" => "DELETE FROM workers",
                "jobs" => "DELETE FROM jobs",
                _ => "DELETE FROM job_attempts"
            }, Token);
            await transaction.CommitAsync(Token);
        }
        var before = await ExecutionRowsAsync();
        Assert.Empty(await DiscoverAsync());
        Assert.Equal(before, await ExecutionRowsAsync());
    }

    [Fact]
    public async Task Discovery_IncludesOfflineReplacedAndLegacyOwnership()
    {
        var offline = await AcquireAsync();
        var replaced = await AcquireAsync();
        await RegisterAsync(replaced.Worker with { SessionId = Guid.CreateVersion7() });
        var legacy = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET session_id = NULL, token_hash = NULL WHERE id = {legacy.Work.LeaseId}", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE workers SET session_id = NULL, session_started_at_utc = NULL WHERE id = {legacy.Worker.WorkerId}", Token);
        Assert.Equal(new[] { offline.Work.LeaseId, replaced.Work.LeaseId, legacy.Work.LeaseId }.Order(),
            (await DiscoverAsync()).Select(item => item.LeaseId).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_SelectsOnlyBoundedKeysWithoutTrackingTransactionOrRowLocks(bool continuation)
    {
        var first = await AcquireAsync();
        await AcquireAsync();
        await AcquireAsync();
        var cursor = continuation ? (await DiscoverAsync(limit: 1))[0] : null;
        var before = await ExecutionRowsAsync();
        await using var blocker = Context();
        await using var locks = await blocker.Database.BeginTransactionAsync(Token);
        await blocker.Database.ExecuteSqlRawAsync("""
            SELECT * FROM workers FOR UPDATE;
            SELECT * FROM jobs FOR UPDATE;
            SELECT * FROM job_attempts FOR UPDATE;
            SELECT * FROM leases FOR UPDATE
            """, Token);
        var capture = new DiscoveryCapture();
        await using var provider = Provider(capture);
        await using var scope = provider.CreateAsyncScope();
        var candidates = await scope.ServiceProvider.GetRequiredService<IExpiredExecutionDiscovery>()
            .FindAsync(Now.AddSeconds(40).UtcDateTime, cursor, 1, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Single(candidates);
        Assert.Equal(1, capture.Commands);
        Assert.Equal(2, capture.Columns);
        Assert.False(capture.HasTransaction);
        Assert.Contains("LIMIT", capture.Sql);
        Assert.Contains(1, capture.Parameters);
        Assert.DoesNotContain("OFFSET", capture.Sql);
        Assert.DoesNotContain("FOR UPDATE", capture.Sql);
        Assert.DoesNotContain("pg_advisory", capture.Sql);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SynestraDbContext>().ChangeTracker.Entries());
        Assert.Equal(before, await ExecutionRowsAsync());
        await AssertRunningAsync(first);
    }

    [Fact]
    public async Task Discovery_IndexMigrationPreservesLegacyAndReportedRowsAndModel()
    {
        var legacy = await AcquireAsync();
        var completed = await AcquireAsync();
        await CompleteAsync(completed, Report());
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync("20260906112910_AddLeaseRenewalAndExecutionCompletion", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET session_id = NULL, token_hash = NULL WHERE id = {legacy.Work.LeaseId}", Token);
        var before = await ExecutionRowsAsync();
        var workers = await WorkerRowsAsync();
        await context.Database.MigrateAsync(Token);
        Assert.Equal(before, await ExecutionRowsAsync());
        Assert.Equal(workers, await WorkerRowsAsync());
        var definition = await context.Database.SqlQueryRaw<string>("""
            SELECT indexdef AS "Value" FROM pg_indexes
            WHERE schemaname = 'public' AND indexname = 'IX_leases_expiration_unreleased'
            """).SingleAsync(Token);
        Assert.Contains("(expires_at_utc, id)", definition);
        Assert.Contains("WHERE (released_at_utc IS NULL)", definition);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(legacy.Work.LeaseId, Assert.Single(await DiscoverAsync()).LeaseId);
        Assert.Equal(FinalizeExpiredExecutionOutcome.Finalized, await FinalizeAsync(legacy));
        await AssertAbandonedAsync(legacy, Now.AddSeconds(40).UtcDateTime, preserveHash: false);
    }

    private async Task<IReadOnlyList<ExpiredExecutionCursor>> DiscoverAsync(
        DateTime? cutoff = null, ExpiredExecutionCursor? after = null, int limit = 100)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExpiredExecutionDiscovery>()
            .FindAsync(cutoff ?? Now.AddSeconds(40).UtcDateTime, after, limit, Token);
    }

    private sealed class DiscoveryCapture : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public string Sql { get; private set; } = "";
        public object?[] Parameters { get; private set; } = [];
        public int Columns { get; private set; }
        public bool HasTransaction { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Commands++;
            Sql = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray();
            Columns = result.FieldCount;
            HasTransaction = command.Transaction is not null;
            return ValueTask.FromResult(result);
        }
    }
}
