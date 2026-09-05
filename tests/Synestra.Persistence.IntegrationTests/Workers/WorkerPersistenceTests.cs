using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synestra.Application.Workers;
using Synestra.Domain.Workers;
using Synestra.Persistence.Extensions;
using Synestra.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed class WorkerPersistenceTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
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
    public async Task Registrations_PersistCompleteStateAcrossScopesAndFenceStaleHeartbeats()
    {
        var request = Request();
        var original = await RegisterAsync(request);
        await AssertStateAsync(original.Worker!);
        var updated = await RegisterAsync(request with { Name = "updated", Capacity = 2, SupportedTypes = ["b", "new"] }, Now.AddSeconds(10));
        await AssertStateAsync(updated.Worker!);
        Assert.Equal(original.Worker!.SessionStartedAtUtc, updated.Worker!.SessionStartedAtUtc);
        var replacement = await RegisterAsync(request with { SessionId = Guid.CreateVersion7(), Name = "replacement", Capacity = 1, SupportedTypes = ["replacement"] }, Now.AddSeconds(20));
        await AssertStateAsync(replacement.Worker!);
        Assert.Equal(original.Worker.RegisteredAtUtc, replacement.Worker!.RegisteredAtUtc);
        Assert.Equal(Now.AddSeconds(20).UtcDateTime, replacement.Worker.SessionStartedAtUtc);
        Assert.Equal(WorkerHeartbeatOutcome.SessionReplaced, await HeartbeatAsync(request.WorkerId, request.SessionId, Now.AddDays(1)));
        await AssertStateAsync(replacement.Worker);
        Assert.Equal(WorkerHeartbeatOutcome.Succeeded, await HeartbeatAsync(request.WorkerId, replacement.Worker.SessionId, Now.AddSeconds(30)));
        Assert.Equal(WorkerHeartbeatOutcome.Succeeded, await HeartbeatAsync(request.WorkerId, replacement.Worker.SessionId, Now));
        await AssertStateAsync(replacement.Worker with { LastSeenAtUtc = Now.AddSeconds(30).UtcDateTime });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRegistrations_SerializeAbsenceAndExistingRowWithCompleteLastCommittedState(bool exists)
    {
        var initial = Request();
        if (exists) await RegisterAsync(initial);
        await using var scope = _provider.CreateAsyncScope();
        await using var gate = await Store(scope).BeginTransactionAsync(Token);
        await gate.LockRegistrationAsync(initial.WorkerId, Token);
        var requests = Enumerable.Range(1, 4).Select(index => initial with
        {
            SessionId = Guid.CreateVersion7(), Name = $"worker-{index}", Capacity = index,
            SupportedTypes = [$"type-{index}", $"extra-{index}"]
        }).ToArray();
        var tasks = requests.Select(request => RegisterAsync(request)).ToArray();
        await WaitForLocksAsync("locktype = 'advisory'", tasks.Length);
        await gate.CommitAsync(Token);
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(RegisterWorkerOutcome.Succeeded, result.Outcome));
        await using var context = Context();
        var worker = await context.Workers.SingleAsync(Token);
        var winner = Assert.Single(results, result => result.Worker!.SessionId == worker.SessionId).Worker!;
        await AssertStateAsync(winner);
        foreach (var result in results)
        {
            var expected = result.Worker!.SessionId == worker.SessionId ? WorkerHeartbeatOutcome.Succeeded : WorkerHeartbeatOutcome.SessionReplaced;
            Assert.Equal(expected, await HeartbeatAsync(worker.Id, result.Worker.SessionId, Now));
            if (result.Worker.SessionId != worker.SessionId)
            {
                var stale = requests.Single(request => request.SessionId == result.Worker.SessionId);
                Assert.Equal(RegisterWorkerOutcome.SessionReplaced, (await RegisterAsync(stale, Now.AddDays(1))).Outcome);
            }
        }

        await AssertStateAsync(winner);
    }

    [Fact]
    public async Task DifferentWorkerIds_ProceedWhileAnotherRegistrationIsBlocked()
    {
        var first = Request();
        await using var scope = _provider.CreateAsyncScope();
        await using var gate = await Store(scope).BeginTransactionAsync(Token);
        await gate.LockRegistrationAsync(first.WorkerId, Token);
        var waiting = RegisterAsync(first);
        await WaitForLocksAsync("locktype = 'advisory'", 1);
        var independent = await RegisterAsync(Request()).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RegisterWorkerOutcome.Succeeded, independent.Outcome);
        Assert.False(waiting.IsCompleted);
        await gate.CommitAsync(Token);
        Assert.Equal(RegisterWorkerOutcome.Succeeded, (await waiting).Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rollback_HidesUncommittedStateAndPreservesPreviousRegistration(bool exists, bool replaceSession)
    {
        var request = Request();
        var original = exists ? (await RegisterAsync(request)).Worker : null;
        await using var scope = _provider.CreateAsyncScope();
        var store = Store(scope);
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var changed = request with
        {
            SessionId = replaceSession ? Guid.CreateVersion7() : request.SessionId,
            Name = "uncommitted", Capacity = 8, SupportedTypes = ["changed"]
        };
        await using (var transaction = await store.BeginTransactionAsync(Token))
        {
            var worker = await transaction.LockRegistrationAsync(request.WorkerId, Token);
            if (worker?.SessionId != changed.SessionId) transaction.AddSession(changed.WorkerId, changed.SessionId);
            if (worker is null) transaction.Add(new Worker(Registration(changed), Now.UtcDateTime));
            else worker.UpdateRegistration(Registration(changed), Now.AddSeconds(10).UtcDateTime);
            await context.SaveChangesAsync(Token);
            if (original is not null) await AssertStateAsync(original);
            else
            {
                await using var reader = Context();
                Assert.Empty(await reader.Workers.ToListAsync(Token));
                Assert.Empty(await reader.Set<WorkerSupportedType>().ToListAsync(Token));
            }

            await using var historyReader = Context();
            Assert.Equal(exists ? 1 : 0, await SessionCountAsync(historyReader));
        }

        Assert.Empty(context.ChangeTracker.Entries());
        if (original is not null) await AssertStateAsync(original);
        var afterRollback = await new RegisterWorker(store, new Clock(Now), new()).ExecuteAsync(changed, Token);
        await AssertStateAsync(afterRollback.Worker!);
    }

    [Fact]
    public async Task FailedSave_RollsBackBothConfigurationAndTypesAndScopeCanBeReused()
    {
        var request = Request();
        var original = (await RegisterAsync(request)).Worker!;
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var replacement = request with { SessionId = Guid.CreateVersion7(), Name = "changed", SupportedTypes = ["other"] };
        await using (var transaction = await Store(scope).BeginTransactionAsync(Token))
        {
            var worker = (await transaction.LockRegistrationAsync(request.WorkerId, Token))!;
            transaction.AddSession(request.WorkerId, replacement.SessionId);
            worker.UpdateRegistration(Registration(replacement), Now.AddSeconds(10).UtcDateTime);
            context.Entry(worker).Property(x => x.Capacity).CurrentValue = 0;
            await Assert.ThrowsAsync<DbUpdateException>(() => transaction.CommitAsync(Token));
        }

        Assert.Empty(context.ChangeTracker.Entries());
        await AssertStateAsync(original);
        Assert.Equal(1, await SessionCountAsync(context));
        await new RegisterWorker(Store(scope), new Clock(Now), new()).ExecuteAsync(request, Token);
        await AssertStateAsync(original);
        Assert.Equal(RegisterWorkerOutcome.Succeeded,
            (await new RegisterWorker(Store(scope), new Clock(Now), new()).ExecuteAsync(replacement, Token)).Outcome);
    }

    [Fact]
    public async Task HeartbeatBehindReplacement_SeesCommittedSessionAndCannotWriteStaleTime()
    {
        var request = Request();
        await RegisterAsync(request);
        await using var scope = _provider.CreateAsyncScope();
        await using var transaction = await Store(scope).BeginTransactionAsync(Token);
        var worker = (await transaction.LockRegistrationAsync(request.WorkerId, Token))!;
        var replacement = request with { SessionId = Guid.CreateVersion7(), SupportedTypes = ["new"] };
        transaction.AddSession(request.WorkerId, replacement.SessionId);
        worker.UpdateRegistration(Registration(replacement), Now.AddSeconds(10).UtcDateTime);
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var heartbeat = HeartbeatAsync(request.WorkerId, request.SessionId, Now.AddDays(1));
        await WaitForLocksAsync("locktype = 'transactionid'", 1);
        await transaction.CommitAsync(Token);
        Assert.Equal(WorkerHeartbeatOutcome.SessionReplaced, await heartbeat);
        await using var reader = Context();
        var committed = await reader.Workers.SingleAsync(Token);
        Assert.Equal(replacement.SessionId, committed.SessionId);
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, committed.LastSeenAtUtc);
    }

    [Fact]
    public async Task RegistrationBehindHeartbeat_PreservesCommittedMonotonicTime()
    {
        var request = Request();
        await RegisterAsync(request);
        await using var scope = _provider.CreateAsyncScope();
        await using var transaction = await Store(scope).BeginTransactionAsync(Token);
        var worker = (await transaction.FindForHeartbeatAsync(request.WorkerId, Token))!;
        Assert.True(worker.RecordHeartbeat(request.SessionId, Now.AddSeconds(20).UtcDateTime));
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);
        var registration = RegisterAsync(request with { SessionId = Guid.CreateVersion7() }, Now.AddSeconds(10));
        await WaitForLocksAsync("locktype = 'transactionid'", 1);
        await transaction.CommitAsync(Token);
        var result = await registration;
        Assert.Equal(Now.AddSeconds(20).UtcDateTime, result.Worker!.LastSeenAtUtc);
        await AssertStateAsync(result.Worker);
    }

    [Fact]
    public async Task Migration_PreservesLegacyRowsAndRegistrationAdoptsThem()
    {
        await using var context = Context();
        var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260905180053_AddJobSubmissionIdempotency", Token);
        var request = Request();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workers (id, name, capacity, registered_at_utc, last_seen_at_utc)
            VALUES ({request.WorkerId}, 'legacy', 1, {Now.UtcDateTime}, {Now.UtcDateTime})
            """, Token);
        await context.Database.MigrateAsync(Token);
        var legacy = await context.Workers.AsNoTracking().SingleAsync(Token);
        Assert.Null(legacy.SessionId);
        Assert.Null(legacy.SessionStartedAtUtc);
        Assert.Equal(WorkerHeartbeatOutcome.SessionReplaced, await HeartbeatAsync(request.WorkerId, request.SessionId, Now));
        var adopted = await RegisterAsync(request, Now.AddDays(1));
        Assert.Equal(Now.UtcDateTime, adopted.Worker!.RegisteredAtUtc);
        Assert.Equal(Now.AddDays(1).UtcDateTime, adopted.Worker.SessionStartedAtUtc);
        await AssertStateAsync(adopted.Worker);
    }

    [Theory]
    [InlineData("UPDATE workers SET capacity = 0", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE workers SET capacity = -1", PostgresErrorCodes.CheckViolation)]
    [InlineData("INSERT INTO workers SELECT * FROM workers LIMIT 1", PostgresErrorCodes.UniqueViolation)]
    [InlineData("UPDATE workers SET session_id = NULL", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE workers SET session_started_at_utc = NULL", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE worker_supported_types SET type = ''", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE worker_supported_types SET type = NULL", PostgresErrorCodes.NotNullViolation)]
    [InlineData("UPDATE worker_supported_types SET type = E' \\t\\n'", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE worker_supported_types SET type = repeat('x', 101)", PostgresErrorCodes.StringDataRightTruncation)]
    [InlineData("INSERT INTO worker_supported_types SELECT * FROM worker_supported_types LIMIT 1", PostgresErrorCodes.UniqueViolation)]
    [InlineData("UPDATE worker_supported_types SET worker_id = gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("INSERT INTO worker_sessions SELECT * FROM worker_sessions LIMIT 1", PostgresErrorCodes.UniqueViolation)]
    [InlineData("UPDATE worker_sessions SET worker_id = gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task Constraints_ProtectWorkerAndCapabilities(string sql, string expected)
    {
        await RegisterAsync(Request());
        await using var context = Context();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Equal(expected, exception.SqlState);
    }

    [Fact]
    public async Task Capabilities_AreOrdinalIndependentOfDefinitionsAndCascadeWithWorker()
    {
        var request = Request() with { SupportedTypes = ["missing", "Missing", "disabled"] };
        await using var context = Context();
        context.JobDefinitions.Add(new("disabled", "Disabled", null, false, Now.UtcDateTime));
        await context.SaveChangesAsync(Token);
        await AssertStateAsync((await RegisterAsync(request)).Worker!);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM workers", Token);
        Assert.Empty(await context.Set<WorkerSupportedType>().ToListAsync(Token));
        Assert.Equal(0, await SessionCountAsync(context));
        Assert.Single(await context.JobDefinitions.ToListAsync(Token));
    }

    private async Task AssertStateAsync(WorkerDetails expected)
    {
        await using var context = Context();
        var worker = await context.Workers.Include(x => x.SupportedTypes).SingleAsync(x => x.Id == expected.WorkerId, Token);
        Assert.Equal(expected.SessionId, worker.SessionId);
        Assert.Equal(expected.Name, worker.Name);
        Assert.Equal(expected.Capacity, worker.Capacity);
        Assert.Equal(expected.RegisteredAtUtc, worker.RegisteredAtUtc);
        Assert.Equal(expected.SessionStartedAtUtc, worker.SessionStartedAtUtc);
        Assert.Equal(expected.LastSeenAtUtc, worker.LastSeenAtUtc);
        Assert.Equal(expected.SupportedTypes, worker.SupportedTypes.Select(x => x.Type).Order(StringComparer.Ordinal));
    }

    private async Task WaitForLocksAsync(string predicate, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var context = Context();
        // Predicate is test-owned SQL; database filters also cover transaction-id locks without a database OID.
        var sql = $"""
            SELECT count(*)::int AS "Value" FROM pg_locks
            WHERE {predicate} AND NOT granted AND pid IN
                (SELECT pid FROM pg_stat_activity WHERE datname = current_database())
            """;
        while (await context.Database.SqlQueryRaw<int>(sql).SingleAsync(timeout.Token) < count)
            await Task.Delay(20, timeout.Token);
    }

    private async Task<RegisterWorkerResult> RegisterAsync(RegisterWorkerRequest request, DateTimeOffset? now = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new RegisterWorker(Store(scope), new Clock(now ?? Now), new()).ExecuteAsync(request, Token);
    }
    private async Task<WorkerHeartbeatOutcome> HeartbeatAsync(Guid id, Guid session, DateTimeOffset now)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await new RecordWorkerHeartbeat(Store(scope), new Clock(now)).ExecuteAsync(id, session, Token);
    }
    private static RegisterWorkerRequest Request() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", 4, ["a", "b"]);
    private Task<int> SessionCountAsync(SynestraDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM worker_sessions").SingleAsync(Token);
    private static WorkerRegistration Registration(RegisterWorkerRequest request) => new(request.WorkerId, request.SessionId, request.Name, request.Capacity, request.SupportedTypes);
    private static IWorkerPersistence Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkerPersistence>();
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
