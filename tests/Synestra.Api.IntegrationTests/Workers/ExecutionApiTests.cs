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
using Synestra.Persistence;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class ExecutionApiTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly Clock _clock = new(Now);
    private PostgreSqlTestDatabase _database = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
        context.JobDefinitions.Add(new("test", "Private definition", "Private metadata", true, Now.UtcDateTime));
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

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Completion_ExactSnapshotContractSurvivesApiRestart(string outcome)
    {
        var execution = await AcquireAsync();
        var reportId = Guid.CreateVersion7();
        var report = Body(reportId, outcome);
        _clock.Now = Now.AddSeconds(10).AddTicks(9);
        var response = await SendAsync(execution, "completion", report);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        var json = JsonSerializer.Deserialize<JsonElement>(text);
        AssertFields(json, "jobId", "attemptId", "leaseId", "reportId", "outcome", "finishedAtUtc", outcome == "succeeded" ? "result" : "error");
        Assert.Equal(execution.JobId, json.GetProperty("jobId").GetGuid());
        Assert.Equal(execution.AttemptId, json.GetProperty("attemptId").GetGuid());
        Assert.Equal(execution.LeaseId, json.GetProperty("leaseId").GetGuid());
        Assert.Equal(reportId, json.GetProperty("reportId").GetGuid());
        Assert.Equal(outcome, json.GetProperty("outcome").GetString());
        Assert.EndsWith("Z", json.GetProperty("finishedAtUtc").GetString());
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, json.GetProperty("finishedAtUtc").GetDateTime());
        Assert.DoesNotContain(execution.Secret, text);
        if (outcome == "succeeded") AssertJsonEqual("{\"a\":1.0,\"b\":[true,null,\"Привіт\"]}", json.GetProperty("result"));
        else
        {
            AssertFields(json.GetProperty("error"), "code", "message");
            Assert.Equal("navigation_failed", json.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("The page did not load.", json.GetProperty("error").GetProperty("message").GetString());
        }
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = Factory();
        _client = _factory.CreateClient();
        _clock.Now = Now.AddDays(1);
        var replayBody = outcome == "succeeded"
            ? Body(reportId, outcome, "{\"b\":[true,null,\"\\u041fривіт\"],\"a\":1e0}") : report;
        var replay = await SendAsync(execution, "completion", replayBody);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(text, await replay.Content.ReadAsStringAsync(Token));
        var clientView = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
        AssertFields(clientView, "id", "type", "status", "priority", "maxAttempts", "createdAtUtc", "availableAtUtc");
        await ProblemAsync(await SendAsync(execution, "renewal"), 409, "lease_not_active");
        await ProblemAsync(await SendAsync(execution, "completion", Body(Guid.CreateVersion7(), outcome)), 409, "attempt_already_finalized");
        await ProblemAsync(await SendAsync(execution, "completion", Body(reportId, outcome == "succeeded" ? "failed" : "succeeded")), 409, "completion_report_conflict");
    }

    [Fact]
    public async Task Renewal_ExactContractIgnoresBodyAndDoesNotActAsHeartbeat()
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddSeconds(20);
        var response = await SendAsync(execution, "renewal", "not interpreted JSON");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        AssertFields(json, "leaseId", "expiresAtUtc");
        Assert.Equal(execution.LeaseId, json.GetProperty("leaseId").GetGuid());
        Assert.Equal(Now.AddSeconds(50).UtcDateTime, json.GetProperty("expiresAtUtc").GetDateTime());
        Assert.EndsWith("Z", json.GetProperty("expiresAtUtc").GetString());
        _clock.Now = Now.AddSeconds(40);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(execution, "renewal")).StatusCode);
        await using var context = Context();
        Assert.Equal(Now.UtcDateTime, (await context.Workers.SingleAsync(Token)).LastSeenAtUtc);
        using var claim = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{execution.WorkerId}/claims");
        claim.Headers.Add("Worker-Session-Id", execution.SessionId.ToString());
        await ProblemAsync(await _client.SendAsync(claim, Token), 409, "worker_offline");
    }

    [Fact]
    public async Task ExpirationBoundary_RejectsRenewalButAcceptsLateCompletion()
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddSeconds(30);
        await ProblemAsync(await SendAsync(execution, "renewal"), 409, "lease_expired");
        _clock.Now = Now.AddSeconds(31);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(execution, "completion", Body(Guid.CreateVersion7()))).StatusCode);
        await using var context = Context();
        Assert.Equal(Now.AddSeconds(30).UtcDateTime, (await context.Leases.SingleAsync(Token)).ExpiresAtUtc);
        Assert.Equal(Now.AddSeconds(31).UtcDateTime, (await context.Leases.SingleAsync(Token)).ReleasedAtUtc);
    }

    [Fact]
    public async Task LookupAndOwnershipErrors_HaveStablePrecedenceAndRevealNoSecrets()
    {
        var execution = await AcquireAsync();
        var other = await AcquireAsync();
        foreach (var operation in new[] { "renewal", "completion" })
        {
            var body = operation == "completion" ? Body(Guid.CreateVersion7()) : null;
            await ProblemAsync(await SendAsync(execution with { WorkerId = Guid.CreateVersion7(), LeaseId = Guid.CreateVersion7() }, operation, body), 404, "worker_not_found");
            await ProblemAsync(await SendAsync(execution with { SessionId = Guid.CreateVersion7(), LeaseId = Guid.CreateVersion7() }, operation, body), 409, "worker_session_replaced");
            await ProblemAsync(await SendAsync(execution with { LeaseId = Guid.CreateVersion7() }, operation, body), 404, "lease_not_found");
            await ProblemAsync(await SendAsync(execution with { Secret = LeaseToken.Generate() }, operation, body), 409, "lease_ownership_lost");
            await ProblemAsync(await SendAsync(execution with { WorkerId = other.WorkerId, SessionId = other.SessionId }, operation, body), 409, "lease_ownership_lost");
        }
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET token_hash = NULL WHERE id = {execution.LeaseId}", Token);
        await ProblemAsync(await SendAsync(execution, "renewal"), 409, "lease_ownership_lost");
        await ProblemAsync(await SendAsync(execution, "completion", Body(Guid.CreateVersion7())), 409, "lease_ownership_lost");
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE workers SET session_id = NULL, session_started_at_utc = NULL WHERE id = {execution.WorkerId}", Token);
        await ProblemAsync(await SendAsync(execution, "renewal"), 409, "worker_session_replaced");
        await ProblemAsync(await SendAsync(execution, "completion", Body(Guid.CreateVersion7())), 409, "worker_session_replaced");
    }

    [Fact]
    public async Task Replacement_FencesReplayAndOldLeaseSession()
    {
        var execution = await AcquireAsync();
        var report = Body(Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(execution, "completion", report)).StatusCode);
        var replacement = Guid.CreateVersion7();
        await RegisterAsync(execution.WorkerId, replacement);
        foreach (var operation in new[] { "renewal", "completion" })
        {
            await ProblemAsync(await SendAsync(execution, operation, report), 409, "worker_session_replaced");
            await ProblemAsync(await SendAsync(execution with { SessionId = replacement }, operation, report), 409, "lease_ownership_lost");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedPersistenceFailure_ReturnsStable500WithoutDiagnosticValues(bool completion)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IRenewLeasePersistence, FailingPersistence>();
            services.AddScoped<IReportExecutionCompletionPersistence, FailingPersistence>();
        }));
        using var client = factory.CreateClient();
        await ProblemAsync(await SendAsync(Unknown(), completion ? "completion" : "renewal",
            completion ? Body(Guid.CreateVersion7()) : null, client), 500, "internal_error");
    }

    private async Task<Acquired> AcquireAsync()
    {
        var workerId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        await RegisterAsync(workerId, sessionId);
        using var submission = new HttpRequestMessage(HttpMethod.Post, "/api/client/jobs")
        { Content = new StringContent("{\"type\":\"test\",\"payload\":{\"private\":true}}", Encoding.UTF8, "application/json") };
        submission.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(submission, Token)).StatusCode);
        using var claim = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{workerId}/claims");
        claim.Headers.Add("Worker-Session-Id", sessionId.ToString());
        var response = await _client.SendAsync(claim, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        AssertFields(json, "jobId", "attemptId", "leaseId", "leaseToken", "attemptNumber", "type", "payload", "acquiredAtUtc", "expiresAtUtc");
        var secret = json.GetProperty("leaseToken").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.Equal(32, Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=").Length);
        return new(workerId, sessionId, json.GetProperty("leaseId").GetGuid(), secret,
            json.GetProperty("jobId").GetGuid(), json.GetProperty("attemptId").GetGuid());
    }
    private async Task RegisterAsync(Guid workerId, Guid sessionId) =>
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/api/worker/workers/{workerId}/registration",
            new { sessionId, name = "Private worker", capacity = 1, supportedTypes = new[] { "test" } }, Token)).StatusCode);

    private async Task<HttpResponseMessage> SendAsync(Acquired execution, string operation, string? body = null, HttpClient? client = null)
    {
        using var request = Request(execution, operation);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await (client ?? _client).SendAsync(request, Token);
    }
    private static HttpRequestMessage Request(Acquired execution, string operation)
    {
        var request = new HttpRequestMessage(operation == "completion" ? HttpMethod.Put : HttpMethod.Post,
            $"/api/worker/workers/{execution.WorkerId}/leases/{execution.LeaseId}/{operation}");
        request.Headers.Add("Worker-Session-Id", execution.SessionId.ToString());
        request.Headers.Add("Lease-Token", execution.Secret);
        return request;
    }
    private async Task ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal("urn:synestra:problem:" + code.Replace('_', '-'), json.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
        Assert.DoesNotContain("token", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Test failure detail", json.GetRawText());
    }
    private static void AssertFields(JsonElement json, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual));
    }
    private static string Body(Guid reportId, string outcome = "succeeded", string? result = null) => outcome == "succeeded"
        ? "{\"reportId\":\"" + reportId + "\",\"outcome\":\"succeeded\",\"result\":" + (result ?? "{\"a\":1.0,\"b\":[true,null,\"Привіт\"]}") + "}"
        : "{\"reportId\":\"" + reportId + "\",\"outcome\":\"failed\",\"error\":{\"code\":\"navigation_failed\",\"message\":\"The page did not load.\"}}";
    private static Acquired Unknown() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), LeaseToken.Generate(), Guid.CreateVersion7(), Guid.CreateVersion7());
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:synestra", _database.ConnectionString)
            .UseSetting("ExecutionFinalization:Enabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock)));
    private sealed record Acquired(Guid WorkerId, Guid SessionId, Guid LeaseId, string Secret, Guid JobId, Guid AttemptId);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailingPersistence : IRenewLeasePersistence, IReportExecutionCompletionPersistence
    {
        Task<IRenewLeaseTransaction> IRenewLeasePersistence.BeginTransactionAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Test failure detail");
        Task<IReportExecutionCompletionTransaction> IReportExecutionCompletionPersistence.BeginTransactionAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Test failure detail");
    }
}
