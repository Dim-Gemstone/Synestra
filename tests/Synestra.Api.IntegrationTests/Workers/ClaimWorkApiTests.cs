using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Api.IntegrationTests.Infrastructure;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Synestra.Persistence;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClaimWorkApiTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly Clock _clock = new(Now.AddTicks(7));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private PostgreSqlTestDatabase _database = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
        context.JobDefinitions.Add(new("test", "Private definition metadata", "Must not be returned", true, Now.UtcDateTime));
        await context.SaveChangesAsync(Token);
        _factory = Factory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task SubmitRegisterClaim_PreservesExactResponseShapePayloadAndOwnershipAcrossRestart()
    {
        const string payload = """{ "url": "https://example.com", "values": [1.25, true, null, {"text":"Привіт"}] }""";
        var jobId = await SubmitAsync(payload);
        var worker = await RegisterAsync();
        var response = await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(new[] { "acquiredAtUtc", "attemptId", "attemptNumber", "expiresAtUtc", "jobId", "leaseId", "leaseToken", "payload", "type" },
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(jobId, body.GetProperty("jobId").GetGuid());
        Assert.Equal(7, body.GetProperty("attemptId").GetGuid().Version);
        Assert.Equal(7, body.GetProperty("leaseId").GetGuid().Version);
        Assert.Equal(1, body.GetProperty("attemptNumber").GetInt32());
        Assert.Equal("test", body.GetProperty("type").GetString());
        using var submitted = JsonDocument.Parse(payload);
        Assert.True(JsonElement.DeepEquals(submitted.RootElement, body.GetProperty("payload")));
        Assert.EndsWith("Z", body.GetProperty("acquiredAtUtc").GetString());
        Assert.EndsWith("Z", body.GetProperty("expiresAtUtc").GetString());
        Assert.Equal(Now.UtcDateTime, body.GetProperty("acquiredAtUtc").GetDateTime());
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, body.GetProperty("expiresAtUtc").GetDateTime());

        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = Factory();
        _client = _factory.CreateClient();
        await AssertNoWorkAsync(await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString()));
        var observed = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{jobId}", Token);
        Assert.Equal("running", observed.GetProperty("status").GetString());
        await using var context = Context();
        var job = await context.Jobs.Include(x => x.Attempts).ThenInclude(x => x.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(body.GetProperty("attemptId").GetGuid(), attempt.Id);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Equal(body.GetProperty("leaseId").GetGuid(), attempt.Lease!.Id);
        Assert.Equal(worker.SessionId, attempt.Lease.SessionId);
        Assert.Equal(worker.WorkerId, attempt.Lease.WorkerId);
        Assert.Equal(attempt.StartedAtUtc, attempt.Lease.AcquiredAtUtc);
        Assert.Null(job.CompletedAtUtc);
        Assert.Equal(Now.UtcDateTime, (await context.Workers.SingleAsync(Token)).LastSeenAtUtc);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("capacity")]
    [InlineData("future")]
    [InlineData("unsupported")]
    public async Task AllNoWorkReasons_ReturnSameEmpty204(string reason)
    {
        var worker = await RegisterAsync(types: reason == "unsupported" ? ["Test"] : ["test"]);
        if (reason != "empty") await SubmitAsync(available: reason == "future" ? Now.AddSeconds(1) : null);
        if (reason == "capacity")
        {
            Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString())).StatusCode);
            await SubmitAsync();
        }

        await AssertNoWorkAsync(await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString()));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-0000-000000000001")]
    public async Task InvalidWorkerIdentity_Returns400BeforeLookup(string workerId) =>
        await AssertProblemAsync(await ClaimAsync(workerId, Guid.CreateVersion7().ToString()), HttpStatusCode.BadRequest, "invalid_request");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-0000-000000000001")]
    [InlineData("019ec569-5a00-7000-8000-000000000001,019ec569-5a00-7000-8000-000000000002")]
    public async Task InvalidSessionHeader_Returns400BeforeLookup(string? header) =>
        await AssertProblemAsync(await ClaimAsync(Guid.CreateVersion7().ToString(), header), HttpStatusCode.BadRequest, "invalid_request");

    [Fact]
    public async Task RepeatedSessionHeader_IsInvalidEvenWithSameValues()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{Guid.CreateVersion7()}/claims");
        var session = Guid.CreateVersion7().ToString();
        request.Headers.TryAddWithoutValidation("Worker-Session-Id", new[] { session, session });
        await AssertProblemAsync(await _client.SendAsync(request, Token), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task MissingWorker_Returns404() =>
        await AssertProblemAsync(await ClaimAsync(Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString()), HttpStatusCode.NotFound, "worker_not_found");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedAndLegacySessions_Return409AndCreateNoOwnership(bool legacy)
    {
        await SubmitAsync();
        var worker = await RegisterAsync();
        if (legacy)
        {
            await using var context = Context();
            await context.Database.ExecuteSqlRawAsync("UPDATE workers SET session_id = NULL, session_started_at_utc = NULL", Token);
        }
        else
        {
            await RegisterAsync(worker.WorkerId, Guid.CreateVersion7());
        }

        await AssertProblemAsync(await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString()), HttpStatusCode.Conflict, "worker_session_replaced");
        await using var reader = Context();
        Assert.Empty(await reader.JobAttempts.ToListAsync(Token));
        Assert.Empty(await reader.Leases.ToListAsync(Token));
    }

    [Fact]
    public async Task OfflineAtThreshold_Returns409AndHeartbeatRestoresEligibility()
    {
        await SubmitAsync();
        var worker = await RegisterAsync();
        _clock.Now = Now.AddSeconds(30);
        await AssertProblemAsync(await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString()), HttpStatusCode.Conflict, "worker_offline");
        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{worker.WorkerId}/heartbeat");
        heartbeat.Headers.Add("Worker-Session-Id", worker.SessionId.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(heartbeat, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(worker.WorkerId.ToString(), worker.SessionId.ToString())).StatusCode);
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsStable500Problem()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IClaimWorkPersistence, FailingPersistence>()));
        using var client = factory.CreateClient();
        await AssertProblemAsync(await ClaimAsync(Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString(), client), HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task TwoApiInstances_RaceWithoutDuplicateOwnership()
    {
        var workers = new[] { await RegisterAsync(), await RegisterAsync() };
        var jobId = await SubmitAsync();
        await using var otherFactory = Factory();
        using var otherClient = otherFactory.CreateClient();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE", Token);
        var first = ClaimAsync(workers[0].WorkerId.ToString(), workers[0].SessionId.ToString());
        var second = ClaimAsync(workers[1].WorkerId.ToString(), workers[1].SessionId.ToString(), otherClient);
        await WaitForBlockedAsync(2);
        await gate.CommitAsync(Token);
        var results = await Task.WhenAll(first, second);
        var success = Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        await AssertNoWorkAsync(Assert.Single(results, response => response.StatusCode == HttpStatusCode.NoContent));
        var body = await success.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(jobId, body.GetProperty("jobId").GetGuid());
        Assert.Single(await context.JobAttempts.ToListAsync(Token));
        Assert.Single(await context.Leases.ToListAsync(Token));
    }

    private async Task AssertNoWorkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Null(response.Content.Headers.ContentType);
    }

    private async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal($"urn:synestra:problem:{code.Replace('_', '-')}", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
    }

    private async Task<Guid> SubmitAsync(string payload = "{}", DateTimeOffset? available = null)
    {
        var fields = new Dictionary<string, object> { ["type"] = "test", ["payload"] = JsonSerializer.Deserialize<JsonElement>(payload) };
        if (available.HasValue) fields["availableAtUtc"] = available.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/jobs")
        {
            Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private async Task<RegisterWorkerRequest> RegisterAsync(Guid? id = null, Guid? session = null, string[]? types = null)
    {
        var worker = new RegisterWorkerRequest(id ?? Guid.CreateVersion7(), session ?? Guid.CreateVersion7(), "Internal worker name", 1, types ?? ["test"]);
        var response = await _client.PutAsJsonAsync($"/api/worker/workers/{worker.WorkerId}/registration",
            new { sessionId = worker.SessionId, name = worker.Name, capacity = worker.Capacity, supportedTypes = worker.SupportedTypes }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return worker;
    }

    private async Task<HttpResponseMessage> ClaimAsync(string workerId, string? session, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{workerId}/claims");
        if (session is not null) request.Headers.TryAddWithoutValidation("Worker-Session-Id", session);
        return await (client ?? _client).SendAsync(request, Token);
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

    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:synestra", _database.ConnectionString)
            .ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock)));
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailingPersistence : IClaimWorkPersistence
    {
        public Task<IClaimWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Test failure.");
    }
}
