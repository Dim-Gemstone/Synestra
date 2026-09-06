using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class ExecutionPersistenceTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlTestDatabase _database = null!;
    private ServiceProvider _provider = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        _provider = Provider();
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Claim_StoresOnlyHashOfCanonicalRandomToken()
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        var hash = await context.Leases.Select(lease => EF.Property<byte[]>(lease, "TokenHash")).SingleAsync(Token);
        var bytes = Convert.FromBase64String(execution.Work.LeaseToken.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, bytes.Length);
        Assert.Equal(SHA256.HashData(bytes), hash);
        var json = await context.Database.SqlQueryRaw<string>("SELECT to_jsonb(l)::text AS \"Value\" FROM leases l").SingleAsync(Token);
        Assert.DoesNotContain(execution.Work.LeaseToken, json);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Renewal_IsDurableMonotonicAndIndependentOfWorkerLiveness()
    {
        var execution = await AcquireAsync();
        Assert.Equal(Now.AddSeconds(70).UtcDateTime, (await RenewAsync(execution, Now.AddSeconds(20), 50)).Lease!.ExpiresAtUtc);
        Assert.Equal(Now.AddSeconds(70).UtcDateTime, (await RenewAsync(execution, Now.AddSeconds(40), 1)).Lease!.ExpiresAtUtc);
        await using var context = Context();
        var lease = await context.Leases.SingleAsync(Token);
        Assert.Equal(Now.AddSeconds(70).UtcDateTime, lease.ExpiresAtUtc);
        Assert.Equal(Now.UtcDateTime, lease.AcquiredAtUtc);
        Assert.Equal(Now.UtcDateTime, (await context.JobAttempts.SingleAsync(Token)).StartedAtUtc);
        Assert.Equal(Now.UtcDateTime, (await context.Workers.SingleAsync(Token)).LastSeenAtUtc);
        Assert.Equal(ExecutionOutcome.LeaseExpired, (await RenewAsync(execution, Now.AddSeconds(70))).Outcome);
        Assert.Equal(ClaimWorkOutcome.WorkerOffline, (await ClaimAsync(execution.Worker, Now.AddSeconds(40))).Outcome);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Completion_PersistsTerminalStateAndReplaysOriginalSnapshotAcrossProviders(string outcome)
    {
        var execution = await AcquireAsync();
        var report = Report(outcome);
        var first = await CompleteAsync(execution, report, Now.AddSeconds(10).AddTicks(9));
        Assert.Equal(ExecutionOutcome.Succeeded, first.Outcome);
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(outcome == "succeeded" ? JobStatus.Succeeded : JobStatus.Failed, job.Status);
        Assert.Equal(outcome == "succeeded" ? JobAttemptStatus.Succeeded : JobAttemptStatus.Failed, attempt.Status);
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, job.CompletedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc, attempt.Lease!.ReleasedAtUtc);
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, attempt.Lease.ExpiresAtUtc);
        Assert.Equal(report.ReportId, context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Equal(report.Error?.Code, attempt.ErrorCode);
        Assert.Equal(report.Error?.Message, attempt.ErrorMessage);
        if (outcome == "succeeded")
        {
            AssertJsonEqual(report.Result!, attempt.Result!);
            Assert.Equal("object", await context.Database.SqlQueryRaw<string>("SELECT jsonb_typeof(result) AS \"Value\" FROM job_attempts").SingleAsync(Token));
        }
        else Assert.Null(attempt.Result);

        await using var restarted = Provider();
        var equivalent = outcome == "succeeded" ? report with { Result = "{\"b\":[true,null],\"a\":1e0}" } : report;
        var replay = await CompleteAsync(execution, equivalent, Now.AddDays(1), restarted);
        Assert.Equal(first.Completion, replay.Completion);
        Assert.Equal(first.Completion!.FinishedAtUtc, attempt.Lease.ReleasedAtUtc);
        Assert.Equal(ExecutionOutcome.CompletionReportConflict, (await CompleteAsync(execution,
            outcome == "succeeded" ? report with { Result = "{}" } : report with { Error = new("different", "message") })).Outcome);
        Assert.Equal(ExecutionOutcome.AttemptAlreadyFinalized, (await CompleteAsync(execution, report with { ReportId = Guid.CreateVersion7() })).Outcome);
        Assert.Equal(first.Completion, (await CompleteAsync(execution, report)).Completion);
        Assert.Equal(ExecutionOutcome.LeaseNotActive, (await RenewAsync(execution, Now.AddDays(1))).Outcome);
    }

    [Fact]
    public async Task LateCompletion_IsAcceptedWithoutRenewingOrRetrying()
    {
        var execution = await AcquireAsync();
        var result = await CompleteAsync(execution, Report(), Now.AddSeconds(40));
        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
        await using var context = Context();
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, (await context.Leases.SingleAsync(Token)).ExpiresAtUtc);
        Assert.Equal(1, await context.JobAttempts.CountAsync(Token));
        Assert.Equal(0, await context.Leases.CountAsync(lease => lease.ReleasedAtUtc == null, Token));
    }

    [Fact]
    public async Task TokenlessLegacyMigration_PreservesAllRowsWithoutInventingOwnership()
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync("20260905234102_AddLeaseSessionBinding", Token);
        await context.Database.MigrateAsync(Token);
        Assert.Null(await context.Leases.Select(lease => EF.Property<byte[]?>(lease, "TokenHash")).SingleAsync(Token));
        Assert.Equal(execution.Work.JobId, (await context.Jobs.SingleAsync(Token)).Id);
        Assert.Equal(execution.Work.AttemptId, (await context.JobAttempts.SingleAsync(Token)).Id);
        Assert.Equal(execution.Work.LeaseId, (await context.Leases.SingleAsync(Token)).Id);
        Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(Token)).Status);
        Assert.Equal(ExecutionOutcome.OwnershipLost, (await RenewAsync(execution)).Outcome);
        Assert.Equal(ExecutionOutcome.OwnershipLost, (await CompleteAsync(execution, Report())).Outcome);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("UPDATE leases SET token_hash = decode(repeat('00',31),'hex')")]
    [InlineData("UPDATE leases SET token_hash = decode(repeat('00',33),'hex')")]
    [InlineData("UPDATE job_attempts SET completion_report_id = gen_random_uuid()")]
    [InlineData("UPDATE job_attempts SET completion_report_id = '019ec569-5a00-7000-0000-000000000001'")]
    [InlineData("UPDATE job_attempts SET completion_report_id = NULL")]
    [InlineData("UPDATE job_attempts SET completion_snapshot = NULL")]
    [InlineData("UPDATE job_attempts SET completion_snapshot = '[]'")]
    [InlineData("UPDATE job_attempts SET status = 'Running'")]
    [InlineData("UPDATE job_attempts SET finished_at_utc = NULL")]
    [InlineData("UPDATE job_attempts SET finished_at_utc = started_at_utc - interval '1 second'")]
    [InlineData("UPDATE job_attempts SET result = '[]'")]
    [InlineData("UPDATE job_attempts SET result = NULL")]
    [InlineData("UPDATE job_attempts SET error_code = 'unexpected'")]
    public async Task Constraints_ProtectReportedSuccessAndTokenShape(string sql)
    {
        var execution = await AcquireAsync();
        await CompleteAsync(execution, Report());
        await using var context = Context();
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public async Task Migration_PreservesReportlessLegacyTerminalShapes(string status)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync("20260905234102_AddLeaseSessionBinding", Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = {status}, completed_at_utc = {Now.UtcDateTime};
            UPDATE job_attempts SET status = {status}, finished_at_utc = {Now.UtcDateTime};
            UPDATE leases SET released_at_utc = {Now.UtcDateTime}
            """, Token);
        await context.Database.MigrateAsync(Token);
        Assert.Equal(execution.Work.JobId, (await context.Jobs.SingleAsync(Token)).Id);
        var attempt = await context.JobAttempts.SingleAsync(Token);
        Assert.Equal(execution.Work.AttemptId, attempt.Id);
        Assert.Equal(status, attempt.Status.ToString());
        Assert.Null(attempt.Result);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Equal(execution.Work.LeaseId, (await context.Leases.SingleAsync(Token)).Id);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("{\"n\":1e131071}")]
    [InlineData("{\"n\":1e-16383}")]
    public async Task AcceptedJsonbNumericBoundaries_ArePersistableAndReplayable(string result)
    {
        var execution = await AcquireAsync();
        var report = Report() with { Result = result };
        var first = await CompleteAsync(execution, report);
        Assert.Equal(ExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal(first.Completion, (await CompleteAsync(execution, report)).Completion);
    }

    [Theory]
    [InlineData("UPDATE job_attempts SET result = jsonb_build_object()")]
    [InlineData("UPDATE job_attempts SET error_code = NULL")]
    [InlineData("UPDATE job_attempts SET error_message = NULL")]
    [InlineData("UPDATE job_attempts SET error_code = ' '")]
    [InlineData("UPDATE job_attempts SET error_message = ' '")]
    public async Task Constraints_ProtectReportedFailureShape(string sql)
    {
        var execution = await AcquireAsync();
        await CompleteAsync(execution, Report("failed"));
        await using var context = Context();
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Token))).SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveOrCommit_DetachesTerminalStateAndScopeCanRetry(bool failCommit)
    {
        var execution = await AcquireAsync();
        var interceptor = new CommitInterceptor { Failure = failCommit ? new InvalidOperationException("Commit failure") : null };
        await using var provider = Provider(interceptor);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var persistence = scope.ServiceProvider.GetRequiredService<IReportExecutionCompletionPersistence>();
        var report = Report();
        if (failCommit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportExecutionCompletion(persistence, new Clock(Now))
                .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, report, Token));
        }
        else
        {
            await using var transaction = await persistence.BeginTransactionAsync(Token);
            await transaction.LockWorkerAsync(execution.Worker.WorkerId, Token);
            var locked = (await transaction.LockExecutionAsync(execution.Work.LeaseId, Token))!;
            StageCompletion(transaction, locked, report);
            context.Entry(locked.Lease).Property<byte[]>("TokenHash").CurrentValue = new byte[31];
            await Assert.ThrowsAsync<DbUpdateException>(() => transaction.CommitAsync(Token));
        }
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertRunningAsync(execution);
        interceptor.Failure = null;
        Assert.Equal(ExecutionOutcome.Succeeded, (await new ReportExecutionCompletion(persistence, new Clock(Now))
            .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, report, Token)).Outcome);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task RollbackAfterSave_LeavesRunningStateAndNoReservedReport()
    {
        var execution = await AcquireAsync();
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var persistence = scope.ServiceProvider.GetRequiredService<IReportExecutionCompletionPersistence>();
        await using (var transaction = await persistence.BeginTransactionAsync(Token))
        {
            await transaction.LockWorkerAsync(execution.Worker.WorkerId, Token);
            var locked = (await transaction.LockExecutionAsync(execution.Work.LeaseId, Token))!;
            StageCompletion(transaction, locked, Report());
            await context.SaveChangesAsync(Token);
            await AssertRunningAsync(execution);
        }
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertRunningAsync(execution);
        Assert.Equal(ExecutionOutcome.Succeeded, (await new ReportExecutionCompletion(persistence, new Clock(Now))
            .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, Report("failed"), Token)).Outcome);
    }

    private async Task AssertRunningAsync(Acquired execution)
    {
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(job => job.Id == execution.Work.JobId, Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Null(job.CompletedAtUtc);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null(attempt.Result);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.Lease!.ReleasedAtUtc);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Null(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
        Assert.Equal(LeaseToken.Hash(execution.Work.LeaseToken), context.Entry(attempt.Lease).Property<byte[]>("TokenHash").CurrentValue);
    }

    private static void StageCompletion(IReportExecutionCompletionTransaction transaction, LeaseExecution execution, CompletionReport report)
    {
        execution.Job!.SucceedAttempt(execution.Attempt!, report.Result!, Now.UtcDateTime);
        execution.Lease.Release(Now.UtcDateTime);
        transaction.RecordCompletion(new(execution.Job.Id, execution.Attempt!.Id, execution.Lease.Id,
            report.ReportId, report.Outcome, Now.UtcDateTime, report.Result, report.Error));
    }

    private async Task<Acquired> AcquireAsync()
    {
        var worker = new RegisterWorkerRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 1, ["test"]);
        await RegisterAsync(worker);
        await AddJobAsync();
        return new(worker, (await ClaimAsync(worker)).Work!);
    }

    private async Task RegisterAsync(RegisterWorkerRequest worker)
    {
        await using var scope = _provider.CreateAsyncScope();
        Assert.Equal(RegisterWorkerOutcome.Succeeded, (await new RegisterWorker(scope.ServiceProvider.GetRequiredService<IWorkerPersistence>(), new Clock(Now), new())
            .ExecuteAsync(worker, Token)).Outcome);
    }

    private async Task AddJobAsync()
    {
        await using var context = Context();
        var definition = await context.JobDefinitions.SingleOrDefaultAsync(Token);
        if (definition is null)
        {
            definition = new("test", "test", null, true, Now.UtcDateTime);
            context.JobDefinitions.Add(definition);
        }
        context.Jobs.Add(new(definition.Id, "test", "{}", 0, 1, Now.UtcDateTime, Now.UtcDateTime));
        await context.SaveChangesAsync(Token);
    }

    private async Task<ClaimWorkResult> ClaimAsync(RegisterWorkerRequest worker, DateTimeOffset? now = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new ClaimWork(scope.ServiceProvider.GetRequiredService<IClaimWorkPersistence>(), new Clock(now ?? Now), new(), new())
            .ExecuteAsync(worker.WorkerId, worker.SessionId, Token);
    }
    private async Task<RenewLeaseResult> RenewAsync(Acquired execution, DateTimeOffset? now = null, int duration = 30, ServiceProvider? provider = null)
    {
        await using var scope = (provider ?? _provider).CreateAsyncScope();
        return await new RenewLease(scope.ServiceProvider.GetRequiredService<IRenewLeasePersistence>(), new Clock(now ?? Now), new() { LeaseDurationSeconds = duration })
            .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, Token);
    }
    private async Task<CompletionResult> CompleteAsync(Acquired execution, CompletionReport report, DateTimeOffset? now = null, ServiceProvider? provider = null)
    {
        await using var scope = (provider ?? _provider).CreateAsyncScope();
        return await new ReportExecutionCompletion(scope.ServiceProvider.GetRequiredService<IReportExecutionCompletionPersistence>(), new Clock(now ?? Now))
            .ExecuteAsync(execution.Worker.WorkerId, execution.Worker.SessionId, execution.Work.LeaseId, execution.Work.LeaseToken, report, Token);
    }
    private ServiceProvider Provider(params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddPersistence(_database.ConnectionString);
        if (interceptors.Length > 0) services.AddDbContext<SynestraDbContext>(options => options.AddInterceptors(interceptors));
        return services.BuildServiceProvider();
    }
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private static CompletionReport Report(string outcome = "succeeded") => outcome == "succeeded"
        ? new(Guid.CreateVersion7(), outcome, "{\"a\":1.0,\"b\":[true,null]}") : new(Guid.CreateVersion7(), outcome, Error: new("error", "message"));
    private static void AssertJsonEqual(string left, string right)
    {
        using var first = JsonDocument.Parse(left);
        using var second = JsonDocument.Parse(right);
        Assert.True(JsonElement.DeepEquals(first.RootElement, second.RootElement));
    }
    private sealed record Acquired(RegisterWorkerRequest Worker, ClaimedWork Work);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CommitInterceptor : DbTransactionInterceptor
    {
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            return result;
        }
    }
}
