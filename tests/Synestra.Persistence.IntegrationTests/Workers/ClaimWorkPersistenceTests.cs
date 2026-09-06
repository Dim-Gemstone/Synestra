using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClaimWorkPersistenceTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlTestDatabase _database = null!;
    private ServiceProvider _provider = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        var services = new ServiceCollection();
        services.AddPersistence(_database.ConnectionString);
        _provider = services.BuildServiceProvider();
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Claim_PersistsRunningAttemptLeaseAndUnchangedSubmissionAcrossScopes()
    {
        var worker = await RegisterAsync();
        var original = await AddJobAsync();
        var result = await ClaimAsync(worker);
        Assert.Equal(ClaimWorkOutcome.Succeeded, result.Outcome);
        await using var context = Context();
        var job = await context.Jobs.Include(x => x.Attempts).ThenInclude(x => x.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        var lease = Assert.IsType<Lease>(attempt.Lease);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Equal(1, attempt.Number);
        Assert.Equal(worker.WorkerId, lease.WorkerId);
        Assert.Equal(worker.SessionId, lease.SessionId);
        Assert.Equal(attempt.Id, result.Work!.AttemptId);
        Assert.Equal(lease.Id, result.Work.LeaseId);
        Assert.Equal(job.Id, result.Work.JobId);
        Assert.Equal(Now.UtcDateTime, lease.AcquiredAtUtc);
        Assert.Equal(lease.AcquiredAtUtc, attempt.StartedAtUtc);
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, lease.ExpiresAtUtc);
        Assert.Null(job.CompletedAtUtc);
        Assert.Equal(original.JobDefinitionId, job.JobDefinitionId);
        Assert.Equal(original.Type, job.Type);
        Assert.Equal(original.Payload, job.Payload);
        Assert.Equal(Now.UtcDateTime, (await context.Workers.SingleAsync(Token)).LastSeenAtUtc);
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("test.child")]
    [InlineData("test*")]
    [InlineData("tést")]
    public async Task Capabilities_RequireExactCaseSensitiveType(string type)
    {
        var worker = await RegisterAsync();
        await AddJobAsync(type);
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(worker)).Outcome);
        await AssertCountsAsync(0, 0);
        var exact = await AddJobAsync();
        Assert.Equal(exact.Id, (await ClaimAsync(worker)).Work!.JobId);
    }

    [Fact]
    public async Task MissingCapabilityDefinitionAndDisabledAcceptedDefinition_DoNotBlockClaim()
    {
        var worker = await RegisterAsync(Request(types: ["missing-definition", "test"]));
        var job = await AddJobAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = false", Token);
        Assert.Equal(job.Id, (await ClaimAsync(worker)).Work!.JobId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM job_definitions", Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task FutureAvailability_IsEligibleAtExactBoundary()
    {
        var worker = await RegisterAsync();
        var job = await AddJobAsync(available: Now.AddSeconds(1));
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(worker)).Outcome);
        Assert.Equal(job.Id, (await ClaimAsync(worker, Now.AddSeconds(1))).Work!.JobId);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task NonPendingJobs_AreNeverReclaimed(string status)
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE jobs SET status = {status}", Token);
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(worker)).Outcome);
        await AssertCountsAsync(0, 0);
    }

    [Fact]
    public async Task Ordering_UsesEveryKeyIncludingNativeUuidTieBreaker()
    {
        var worker = await RegisterAsync(Request(capacity: 5));
        var priority = await AddJobAsync(priority: 10, created: Now.AddSeconds(-5));
        var availability = await AddJobAsync(created: Now.AddSeconds(-10), available: Now.AddSeconds(-5));
        var creation = await AddJobAsync(created: Now.AddSeconds(-10));
        var higherUuid = await AddJobAsync(id: Guid.Parse("019ec569-5a00-7000-8000-0000000000ff"));
        var lowerUuid = await AddJobAsync(id: Guid.Parse("019ec569-5a00-7000-8000-000000000001"));
        foreach (var expected in new[] { priority, availability, creation, lowerUuid, higherUuid })
            Assert.Equal(expected.Id, (await ClaimAsync(worker)).Work!.JobId);
    }

    [Fact]
    public async Task ExistingAttemptHistory_UsesMaximumNumberWithoutOneAttemptPerJobConstraint()
    {
        var worker = await RegisterAsync(Request(capacity: 2));
        var job = await AddJobAsync();
        Assert.Equal(1, (await ClaimAsync(worker)).Work!.AttemptNumber);
        await using var context = Context();
        // Fixture represents a Pending historical Job without adding a retry operation.
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE jobs SET status = 'Pending' WHERE id = {job.Id}", Token);
        await context.Database.ExecuteSqlRawAsync("UPDATE job_attempts SET number = 5", Token);
        Assert.Equal(6, (await ClaimAsync(worker)).Work!.AttemptNumber);
        await AssertCountsAsync(2, 2);
    }

    [Fact]
    public async Task TwoWorkersRacingForOneJob_CreateExactlyOneOwnership()
    {
        var workers = new[] { await RegisterAsync(), await RegisterAsync() };
        await AddJobAsync();
        await using var gate = Context();
        await using var transaction = await gate.Database.BeginTransactionAsync(Token);
        await gate.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE", Token);
        var claims = workers.Select(worker => ClaimAsync(worker)).ToArray();
        await WaitForBlockedAsync(2);
        await transaction.CommitAsync(Token);
        var results = await Task.WhenAll(claims);
        Assert.Single(results, result => result.Outcome == ClaimWorkOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == ClaimWorkOutcome.NoWork);
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task MultipleWorkers_ClaimDifferentJobsWithoutDuplicateOwnership()
    {
        var workers = new List<RegisterWorkerRequest>();
        for (var i = 0; i < 8; i++)
        {
            workers.Add(await RegisterAsync());
            await AddJobAsync();
        }

        var results = await Task.WhenAll(workers.Select(worker => ClaimAsync(worker)));
        Assert.All(results, result => Assert.Equal(ClaimWorkOutcome.Succeeded, result.Outcome));
        Assert.Equal(8, results.Select(result => result.Work!.JobId).Distinct().Count());
        await AssertCountsAsync(8, 8);
    }

    [Fact]
    public async Task LockedCandidate_IsSkippedWhileNextJobCanBeClaimed()
    {
        var worker = await RegisterAsync();
        var locked = await AddJobAsync(priority: 10);
        var available = await AddJobAsync();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM jobs WHERE id = {locked.Id} FOR UPDATE", Token);
        var result = await ClaimAsync(worker).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(available.Id, result.Work!.JobId);
        Assert.Equal(JobStatus.Pending, (await context.Jobs.SingleAsync(x => x.Id == locked.Id, Token)).Status);
    }

    [Fact]
    public async Task ConcurrentClaimsForOneWorker_NeverExceedCapacity()
    {
        var worker = await RegisterAsync(Request(capacity: 2));
        for (var i = 0; i < 6; i++) await AddJobAsync();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE", Token);
        var claims = Enumerable.Range(0, 6).Select(_ => ClaimAsync(worker)).ToArray();
        await WaitForBlockedAsync(6);
        await gate.CommitAsync(Token);
        var results = await Task.WhenAll(claims);
        Assert.Equal(2, results.Count(result => result.Outcome == ClaimWorkOutcome.Succeeded));
        Assert.Equal(4, results.Count(result => result.Outcome == ClaimWorkOutcome.NoWork));
        await AssertCountsAsync(2, 2);
        Assert.Equal(4, await context.Jobs.CountAsync(job => job.Status == JobStatus.Pending, Token));
    }

    [Fact]
    public async Task DifferentWorkerClaims_ProceedWithoutGlobalOrRegistrationAdvisoryLock()
    {
        var blocked = await RegisterAsync();
        var independent = await RegisterAsync();
        await AddJobAsync();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        await using var gate = await scope.ServiceProvider.GetRequiredService<IWorkerPersistence>().BeginTransactionAsync(Token);
        await gate.LockRegistrationAsync(blocked.WorkerId, Token);
        var waiting = ClaimAsync(blocked);
        await WaitForBlockedAsync(1);
        Assert.Equal(ClaimWorkOutcome.Succeeded, (await ClaimAsync(independent).WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome);
        Assert.False(waiting.IsCompleted);
        await using var reader = Context();
        Assert.Equal(0, await reader.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
              AND pid IN (SELECT pid FROM pg_stat_activity WHERE datname = current_database())
            """).SingleAsync(Token));
        await gate.CommitAsync(Token);
        Assert.Equal(ClaimWorkOutcome.Succeeded, (await waiting).Outcome);
    }

    [Theory]
    [InlineData("active", ClaimWorkOutcome.NoWork)]
    [InlineData("expired", ClaimWorkOutcome.Succeeded)]
    [InlineData("boundary", ClaimWorkOutcome.Succeeded)]
    [InlineData("released", ClaimWorkOutcome.Succeeded)]
    [InlineData("legacy", ClaimWorkOutcome.NoWork)]
    public async Task Capacity_CountsAuthoritativeLeasesWithStrictExpiration(string state, ClaimWorkOutcome expected)
    {
        var worker = await RegisterAsync();
        var first = await AddJobAsync();
        await ClaimAsync(worker);
        await AddJobAsync();
        await using var context = Context();
        if (state == "expired") await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.AddMicroseconds(-1).UtcDateTime}", Token);
        if (state == "boundary") await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET expires_at_utc = {Now.UtcDateTime}", Token);
        if (state == "released") await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET released_at_utc = {Now.UtcDateTime}", Token);
        if (state == "legacy") await context.Database.ExecuteSqlRawAsync("UPDATE leases SET session_id = NULL", Token);
        Assert.Equal(expected, (await ClaimAsync(worker)).Outcome);
        Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(job => job.Id == first.Id, Token)).Status);
        Assert.All(await context.JobAttempts.ToListAsync(Token), attempt => Assert.Equal(JobAttemptStatus.Running, attempt.Status));
    }

    [Fact]
    public async Task LowerCapacityAndReplaceSession_PreserveLeasesAndCountPreviousSessionSlots()
    {
        var worker = await RegisterAsync(Request(capacity: 2));
        for (var i = 0; i < 3; i++) await AddJobAsync();
        await ClaimAsync(worker);
        await ClaimAsync(worker);
        await RegisterAsync(worker with { Capacity = 1 });
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(worker)).Outcome);
        var replacement = await RegisterAsync(worker with { SessionId = Guid.CreateVersion7(), Capacity = 1 });
        Assert.Equal(ClaimWorkOutcome.SessionReplaced, (await ClaimAsync(worker)).Outcome);
        Assert.Equal(ClaimWorkOutcome.NoWork, (await ClaimAsync(replacement)).Outcome);
        await using var context = Context();
        Assert.All(await context.Leases.ToListAsync(Token), lease => Assert.Equal(worker.SessionId, lease.SessionId));
        await AssertCountsAsync(2, 2);
    }

    [Fact]
    public async Task ClaimBehindReplacement_SeesCommittedSessionAndCreatesNothing()
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkerPersistence>();
        await using var gate = await store.BeginTransactionAsync(Token);
        var entity = (await gate.LockRegistrationAsync(worker.WorkerId, Token))!;
        var replacement = worker with { SessionId = Guid.CreateVersion7() };
        gate.AddSession(worker.WorkerId, replacement.SessionId);
        entity.UpdateRegistration(new(replacement.WorkerId, replacement.SessionId, replacement.Name, replacement.Capacity, replacement.SupportedTypes), Now.UtcDateTime);
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var claim = ClaimAsync(worker);
        await WaitForBlockedAsync(1);
        await gate.CommitAsync(Token);
        Assert.Equal(ClaimWorkOutcome.SessionReplaced, (await claim).Outcome);
        await AssertCountsAsync(0, 0);
        Assert.Equal(ClaimWorkOutcome.Succeeded, (await ClaimAsync(replacement)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationOrHeartbeatBehindClaim_LeavesCommittedOwnershipWithOriginalSession(bool replaceSession)
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        await using var claim = await Store(scope).BeginTransactionAsync(Token);
        var lease = await CreateOwnershipAsync(claim, worker);
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var next = replaceSession ? worker with { SessionId = Guid.CreateVersion7() } : worker;
        Task waiting = replaceSession ? RegisterAsync(next) : HeartbeatAsync(next, Now.AddSeconds(10));
        await WaitForBlockedAsync(1);
        await claim.CommitAsync(Token);
        await waiting;
        await using var reader = Context();
        Assert.Equal(worker.SessionId, (await reader.Leases.SingleAsync(Token)).SessionId);
        Assert.Equal(lease.Id, (await reader.Leases.SingleAsync(Token)).Id);
        Assert.Equal(next.SessionId, (await reader.Workers.SingleAsync(Token)).SessionId);
    }

    [Fact]
    public async Task ClaimBehindHeartbeat_UsesCommittedLivenessAtThreshold()
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        await using var gate = await scope.ServiceProvider.GetRequiredService<IWorkerPersistence>().BeginTransactionAsync(Token);
        var entity = (await gate.FindForHeartbeatAsync(worker.WorkerId, Token))!;
        Assert.True(entity.RecordHeartbeat(worker.SessionId, Now.AddSeconds(20).UtcDateTime));
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var claim = ClaimAsync(worker, Now.AddSeconds(30));
        await WaitForBlockedAsync(1);
        await gate.CommitAsync(Token);
        Assert.Equal(ClaimWorkOutcome.Succeeded, (await claim).Outcome);
        await using var reader = Context();
        Assert.Equal(Now.AddSeconds(20).UtcDateTime, (await reader.Workers.SingleAsync(Token)).LastSeenAtUtc);
    }

    [Fact]
    public async Task FirstUncommittedRegistration_MayReturn404ThenSucceedAfterCommit()
    {
        var worker = Request();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        await using var registration = await scope.ServiceProvider.GetRequiredService<IWorkerPersistence>().BeginTransactionAsync(Token);
        Assert.Null(await registration.LockRegistrationAsync(worker.WorkerId, Token));
        registration.AddSession(worker.WorkerId, worker.SessionId);
        registration.Add(new(new(worker.WorkerId, worker.SessionId, worker.Name, worker.Capacity, worker.SupportedTypes), Now.UtcDateTime));
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        Assert.Equal(ClaimWorkOutcome.WorkerNotFound, (await ClaimAsync(worker).WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome);
        await registration.CommitAsync(Token);
        Assert.Equal(ClaimWorkOutcome.Succeeded, (await ClaimAsync(worker)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackOrFailedSave_DetachesOwnershipAndSameScopeCanClaimAgain(bool failSave)
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        await using (var transaction = await Store(scope).BeginTransactionAsync(Token))
        {
            var lease = await CreateOwnershipAsync(transaction, worker);
            if (failSave)
            {
                context.Entry(lease).Property(x => x.SessionId).CurrentValue = Guid.CreateVersion7();
                await Assert.ThrowsAsync<DbUpdateException>(() => transaction.CommitAsync(Token));
            }
            else
            {
                await context.SaveChangesAsync(Token);
                await AssertCountsAsync(0, 0);
            }
        }

        Assert.Empty(context.ChangeTracker.Entries());
        await AssertCountsAsync(0, 0);
        Assert.Equal(JobStatus.Pending, (await context.Jobs.AsNoTracking().SingleAsync(Token)).Status);
        var result = await new ClaimWork(Store(scope), new Clock(Now), new(), new()).ExecuteAsync(worker.WorkerId, worker.SessionId, Token);
        Assert.Equal(ClaimWorkOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.Work!.AttemptNumber);
        Assert.Empty(context.ChangeTracker.Entries());
        await AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData("UPDATE leases SET job_attempt_id = gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("UPDATE leases SET worker_id = gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("UPDATE leases SET session_id = gen_random_uuid()", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE leases SET session_id = '019ec569-5a00-7000-0000-000000000001'", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE leases SET session_id = '019ec569-5a00-7000-8000-000000000001'", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("INSERT INTO leases SELECT gen_random_uuid(), job_attempt_id, worker_id, acquired_at_utc, expires_at_utc, released_at_utc, session_id FROM leases LIMIT 1", PostgresErrorCodes.UniqueViolation)]
    [InlineData("INSERT INTO job_attempts SELECT gen_random_uuid(), job_id, number, status, started_at_utc, finished_at_utc, error_code, error_message FROM job_attempts LIMIT 1", PostgresErrorCodes.UniqueViolation)]
    [InlineData("UPDATE job_attempts SET job_id = gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("DELETE FROM worker_sessions", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task Constraints_ProtectRelationshipsSessionBindingAndAttemptNumbers(string sql, string expected)
    {
        var worker = await RegisterAsync();
        await AddJobAsync();
        await ClaimAsync(worker);
        await using var context = Context();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Equal(expected, exception.SqlState);
    }

    [Fact]
    public async Task SessionBinding_CannotReferenceAnotherWorkersRegisteredSession()
    {
        var worker = await RegisterAsync();
        var other = await RegisterAsync();
        await AddJobAsync();
        await ClaimAsync(worker);
        await using var context = Context();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET session_id = {other.SessionId}", Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Migration_PreservesLegacyLeasesAndDocumentsNullableSqlLimitation()
    {
        var worker = await RegisterAsync();
        var job = await AddJobAsync();
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync("20260905190046_AddWorkerRegistration", Token);
        var attemptId = Guid.CreateVersion7();
        var leaseId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_attempts (id, job_id, number, status, started_at_utc)
            VALUES ({attemptId}, {job.Id}, 1, 'Running', {Now.UtcDateTime});
            INSERT INTO leases (id, job_attempt_id, worker_id, acquired_at_utc, expires_at_utc)
            VALUES ({leaseId}, {attemptId}, {worker.WorkerId}, {Now.UtcDateTime}, {Now.AddSeconds(30).UtcDateTime})
            """, Token);
        await context.Database.MigrateAsync(Token);
        var legacy = await context.Leases.AsNoTracking().SingleAsync(Token);
        Assert.Equal(leaseId, legacy.Id);
        Assert.Null(legacy.SessionId);
        Assert.Equal(worker.WorkerId, legacy.WorkerId);
        Assert.Equal(Now.UtcDateTime, legacy.AcquiredAtUtc);
        var anotherAttempt = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_attempts (id, job_id, number, status, started_at_utc)
            VALUES ({anotherAttempt}, {job.Id}, 2, 'Running', {Now.UtcDateTime});
            INSERT INTO leases (id, job_attempt_id, worker_id, acquired_at_utc, expires_at_utc, session_id)
            VALUES ({Guid.CreateVersion7()}, {anotherAttempt}, {worker.WorkerId}, {Now.UtcDateTime}, {Now.AddSeconds(30).UtcDateTime}, NULL)
            """, Token);
        Assert.Equal(2, await context.Leases.CountAsync(lease => lease.SessionId == null, Token));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private async Task<Lease> CreateOwnershipAsync(IClaimWorkTransaction transaction, RegisterWorkerRequest worker)
    {
        Assert.NotNull(await transaction.LockWorkerAsync(worker.WorkerId, Token));
        var job = (await transaction.LockEligibleJobAsync(worker.WorkerId, Now.UtcDateTime, Token))!;
        var attempt = job.StartAttempt(Now.UtcDateTime);
        var lease = new Lease(attempt.Id, worker.WorkerId, worker.SessionId, Now.UtcDateTime, TimeSpan.FromSeconds(30));
        transaction.Add(attempt, lease);
        return lease;
    }

    private async Task AssertCountsAsync(int attempts, int leases)
    {
        await using var context = Context();
        Assert.Equal(attempts, await context.JobAttempts.CountAsync(Token));
        Assert.Equal(leases, await context.Leases.CountAsync(Token));
    }

    private async Task WaitForBlockedAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var context = Context();
        while (await context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
            """).SingleAsync(timeout.Token) < count)
            await Task.Delay(20, timeout.Token);
    }

    private async Task<Job> AddJobAsync(string type = "test", int priority = 0, DateTimeOffset? created = null, DateTimeOffset? available = null, Guid? id = null)
    {
        await using var context = Context();
        var definition = await context.JobDefinitions.SingleOrDefaultAsync(x => x.Type == type, Token);
        if (definition is null)
        {
            definition = new(type, "Test definition", null, true, Now.UtcDateTime);
            context.JobDefinitions.Add(definition);
        }

        var job = new Job(definition.Id, type, "{\"value\": 42}", priority, 1, (created ?? Now).UtcDateTime, (available ?? Now).UtcDateTime);
        context.Jobs.Add(job);
        if (id.HasValue) context.Entry(job).Property(x => x.Id).CurrentValue = id.Value;
        await context.SaveChangesAsync(Token);
        return job;
    }

    private async Task<RegisterWorkerRequest> RegisterAsync(RegisterWorkerRequest? request = null)
    {
        request ??= Request();
        await using var scope = _provider.CreateAsyncScope();
        Assert.Equal(RegisterWorkerOutcome.Succeeded, (await new RegisterWorker(scope.ServiceProvider.GetRequiredService<IWorkerPersistence>(), new Clock(Now), new())
            .ExecuteAsync(request, Token)).Outcome);
        return request;
    }

    private async Task<WorkerHeartbeatOutcome> HeartbeatAsync(RegisterWorkerRequest worker, DateTimeOffset now)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new RecordWorkerHeartbeat(scope.ServiceProvider.GetRequiredService<IWorkerPersistence>(), new Clock(now))
            .ExecuteAsync(worker.WorkerId, worker.SessionId, Token);
    }

    private async Task<ClaimWorkResult> ClaimAsync(RegisterWorkerRequest worker, DateTimeOffset? now = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new ClaimWork(Store(scope), new Clock(now ?? Now), new(), new()).ExecuteAsync(worker.WorkerId, worker.SessionId, Token);
    }

    private static RegisterWorkerRequest Request(int capacity = 1, string[]? types = null) => new(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", capacity, types ?? ["test"]);
    private static IClaimWorkPersistence Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IClaimWorkPersistence>();
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
