using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Synestra.Domain.Jobs;
using Synestra.Persistence;
using Synestra.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace Synestra.Api.IntegrationTests.Jobs;

[Collection(PostgreSqlCollection.Name)]
public sealed class SubmitJobApiTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private PostgreSqlTestDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        _factory = CreateFactory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task EnabledDefinition_CreatesPersistedJobAndCanRetrieveItAfterApiRestart()
    {
        var definition = await AddDefinitionAsync(enabled: true);

        var response = await PostAsync(JsonSerializer.Serialize(new
        {
            type = definition.Type,
            payload = new { url = "https://example.com" }
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(7, body.EnumerateObject().Count());
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/client/jobs/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(definition.Type, body.GetProperty("type").GetString());
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("priority").GetInt32());
        Assert.Equal(1, body.GetProperty("maxAttempts").GetInt32());
        Assert.True(body.TryGetProperty("id", out _));
        Assert.EndsWith("Z", body.GetProperty("createdAtUtc").GetString());
        Assert.EndsWith("Z", body.GetProperty("availableAtUtc").GetString());
        Assert.False(body.TryGetProperty("payload", out _));

        await using var context = CreateContext();
        var job = await context.Jobs.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(definition.Id, job.JobDefinitionId);
        Assert.Equal(definition.Type, job.Type);

        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = CreateFactory();
        _client = _factory.CreateClient();

        var getResponse = await _client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("application/json", getResponse.Content.Headers.ContentType?.MediaType);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(7, getBody.EnumerateObject().Count());
        Assert.Equal(id, getBody.GetProperty("id").GetGuid());
        Assert.Equal(definition.Type, getBody.GetProperty("type").GetString());
        Assert.Equal("pending", getBody.GetProperty("status").GetString());
        Assert.Equal(0, getBody.GetProperty("priority").GetInt32());
        Assert.Equal(1, getBody.GetProperty("maxAttempts").GetInt32());
        Assert.EndsWith("Z", getBody.GetProperty("createdAtUtc").GetString());
        Assert.EndsWith("Z", getBody.GetProperty("availableAtUtc").GetString());
        Assert.False(getBody.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task MissingJob_ReturnsStableProblem()
    {
        var id = Guid.CreateVersion7();

        var response = await _client.GetAsync($"/api/client/jobs/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("job_not_found", problem.GetProperty("code").GetString());
        Assert.Equal("urn:synestra:problem:job-not-found", problem.GetProperty("type").GetString());
        Assert.Equal((int)HttpStatusCode.NotFound, problem.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("missing.definition", HttpStatusCode.NotFound, "job_definition_not_found")]
    public async Task MissingDefinition_ReturnsStableProblemAndDoesNotPersist(
        string type,
        HttpStatusCode status,
        string code)
    {
        await AssertRejectedAsync(
            JsonSerializer.Serialize(new { type, payload = new { } }),
            status,
            code);
    }

    [Fact]
    public async Task DisabledDefinition_ReturnsStableProblemAndDoesNotPersist()
    {
        var definition = await AddDefinitionAsync(enabled: false);
        await AssertRejectedAsync(
            JsonSerializer.Serialize(new { type = definition.Type, payload = new { } }),
            HttpStatusCode.Conflict,
            "job_definition_disabled");
    }

    [Theory]
    [InlineData("{", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{},\"priority\":1}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{},\"id\":\"01900000-0000-7000-8000-000000000000\"}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{},\"status\":\"pending\"}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{},\"createdAtUtc\":\"2026-09-01T12:00:00Z\"}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{\"value\":1,\"value\":2}}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":[],\"availableAtUtc\":\"2026-09-01T12:00:00Z\"}", HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData("{\"type\":\"test\",\"payload\":{},\"availableAtUtc\":\"2020-01-01T00:00:00Z\"}", HttpStatusCode.BadRequest, "invalid_request")]
    public async Task InvalidRequests_ReturnStableProblemAndDoNotPersist(
        string json,
        HttpStatusCode status,
        string code)
    {
        await AssertRejectedAsync(json, status, code);
    }

    [Fact]
    public async Task OversizedPayload_ReturnsStableProblemAndDoesNotPersist()
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var json = JsonSerializer.Serialize(new
        {
            type = definition.Type,
            payload = new { value = new string('a', 256 * 1024) }
        });
        await AssertRejectedAsync(json, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    [Fact]
    public async Task WithoutKey_RepeatedRequestsCreateSeparateJobs()
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var json = JsonSerializer.Serialize(new { type = definition.Type, payload = new { } });
        var first = await PostAsync(json);
        var second = await PostAsync(json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(first.Headers.Location, second.Headers.Location);
        await AssertCountsAsync(2, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedReplay_ReturnsExactSuccessAndLocationAfterApiRestart(bool explicitAvailability)
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var json = explicitAvailability
            ? JsonSerializer.Serialize(new
            {
                type = definition.Type,
                payload = new { secret = "private input" },
                availableAtUtc = DateTime.UtcNow.AddHours(1).AddTicks(7)
            })
            : JsonSerializer.Serialize(new { type = definition.Type, payload = new { secret = "private input" } });
        var first = await PostWithKeyAsync(json, "request-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("application/json", first.Content.Headers.ContentType?.MediaType);
        var firstText = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize<JsonElement>(firstText);
        Assert.Equal(7, body.EnumerateObject().Count());
        Assert.False(body.TryGetProperty("payload", out _));
        Assert.DoesNotContain("private input", firstText);
        Assert.EndsWith("Z", body.GetProperty("createdAtUtc").GetString());
        Assert.EndsWith("Z", body.GetProperty("availableAtUtc").GetString());
        Assert.Equal($"/api/client/jobs/{body.GetProperty("id").GetGuid()}", first.Headers.Location?.OriginalString);

        var replay = await PostWithKeyAsync(json, "request-1");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(firstText, await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        await AssertCountsAsync(1, 1);

        await using (var context = CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = FALSE", TestContext.Current.CancellationToken);
            // Test snapshot independence without adding execution transitions to this slice.
            await context.Database.ExecuteSqlRawAsync("UPDATE jobs SET status = 'Succeeded'", TestContext.Current.CancellationToken);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = CreateFactory();
        _client = _factory.CreateClient();

        var restartedReplay = await PostWithKeyAsync(json, "request-1");
        Assert.Equal(HttpStatusCode.Created, restartedReplay.StatusCode);
        Assert.Equal("application/json", restartedReplay.Content.Headers.ContentType?.MediaType);
        Assert.Equal(first.Headers.Location, restartedReplay.Headers.Location);
        Assert.Equal(firstText, await restartedReplay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var get = await _client.GetFromJsonAsync<JsonElement>(first.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", get.GetProperty("status").GetString());
        Assert.False(get.TryGetProperty("payload", out _));
        await AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData("{\"a\":1, \"b\":\"a\"}")]
    [InlineData("{\"b\":\"a\",\"a\":1}")]
    [InlineData("{\"a\":1.0,\"b\":\"a\"}")]
    [InlineData("{\"a\":1,\"b\":\"\\u0061\"}")]
    [InlineData("{\"a\":2,\"b\":\"a\"}")]
    public async Task DifferentPayloadSpellingOrValue_Conflicts(string payload)
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var first = await PostWithKeyAsync(JsonSerializer.Serialize(new
        {
            type = definition.Type,
            payload = new { a = 1, b = "a" }
        }), "request-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var conflict = await PostWithKeyAsync($$"""{"type":"{{definition.Type}}","payload":{{payload}}} """, "request-1");
        await AssertConflictAsync(conflict);
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task DifferentTypeOrAvailabilityPresence_ConflictsWithinGlobalScope()
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var first = await PostWithKeyAsync(JsonSerializer.Serialize(new { type = definition.Type, payload = new { } }), "request-1");
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        await AssertConflictAsync(await PostWithKeyAsync(
            "{\"type\":\"missing.type\",\"payload\":{}}", "request-1"));
        await AssertConflictAsync(await PostWithKeyAsync(JsonSerializer.Serialize(new
        {
            type = definition.Type,
            payload = new { },
            availableAtUtc = body.GetProperty("availableAtUtc").GetString()
        }), "request-1"));
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Identity_IgnoresEnvelopeFormattingAndEquivalentUtcSpellingButPreservesTimestampTicks()
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var available = DateTime.UtcNow.AddHours(1);
        var first = await PostWithKeyAsync(
            $$"""{"type":"{{definition.Type}}","payload":{},"availableAtUtc":"{{available:O}}"}""", "request-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var replay = await PostWithKeyAsync(
            $$"""{ "availableAtUtc": "{{new DateTimeOffset(available):O}}", "payload": {}, "type": "{{definition.Type}}" }""", "request-1");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await AssertConflictAsync(await PostWithKeyAsync(
            $$"""{"type":"{{definition.Type}}","payload":{},"availableAtUtc":"{{available.AddTicks(1):O}}"}""", "request-1"));
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task ConcurrentKeyedRequests_AllReturnTheSameJob()
    {
        var definition = await AddDefinitionAsync(enabled: true);
        var json = JsonSerializer.Serialize(new { type = definition.Type, payload = new { } });
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostWithKeyAsync(json, "request-1")));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Single(responses.Select(response => response.Headers.Location).Distinct());
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Single(bodies.Distinct());
        await AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("a,b")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    [InlineData("é")]
    public async Task InvalidKey_ReturnsInvalidRequest(string key)
    {
        var response = await PostWithKeyAsync("{\"type\":\"test\",\"payload\":{}}", key);
        await AssertInvalidKeyAsync(response);
    }

    [Fact]
    public async Task PresentEmptyKey_ReturnsInvalidRequest()
    {
        // HttpClient's TestServer handler omits empty header values; send the server request directly.
        var body = Encoding.UTF8.GetBytes("{\"type\":\"test\",\"payload\":{}}");
        using var stream = new MemoryStream(body);
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/api/client/jobs";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = stream;
            context.Request.Headers["Idempotency-Key"] = new Microsoft.Extensions.Primitives.StringValues(string.Empty);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(400, response.Response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Response.ContentType);
        using var problem = await JsonDocument.ParseAsync(response.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", problem.RootElement.GetProperty("code").GetString());
        await AssertCountsAsync(0, 0);
    }

    [Fact]
    public async Task RepeatedOrOversizedKeys_ReturnInvalidRequest()
    {
        await AssertInvalidKeyAsync(await PostWithKeyAsync("{\"type\":\"test\",\"payload\":{}}", "a", "a"));
        await AssertInvalidKeyAsync(await PostWithKeyAsync("{\"type\":\"test\",\"payload\":{}}", new string('a', 129)));
    }

    [Fact]
    public async Task UnusedKey_PreservesDefinitionErrorsAndCanBeUsedAfterFailure()
    {
        var definition = await AddDefinitionAsync(enabled: false);
        var json = JsonSerializer.Serialize(new { type = definition.Type, payload = new { } });
        var missing = await PostWithKeyAsync("{\"type\":\"missing\",\"payload\":{}}", "request-1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var disabled = await PostWithKeyAsync(json, "request-1");
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        var body = await disabled.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("job_definition_disabled", body.GetProperty("code").GetString());
        await AssertCountsAsync(0, 0);

        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("UPDATE job_definitions SET is_enabled = TRUE", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, (await PostWithKeyAsync(json, "request-1")).StatusCode);
        await AssertCountsAsync(1, 1);
    }

    private async Task AssertInvalidKeyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        await AssertCountsAsync(0, 0);
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("idempotency_key_conflict", body.GetProperty("code").GetString());
        Assert.Equal("urn:synestra:problem:idempotency-key-conflict", body.GetProperty("type").GetString());
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("title").GetString()));
        Assert.False(body.TryGetProperty("payload", out _));
    }

    private async Task AssertCountsAsync(int jobs, int submissions)
    {
        await using var context = CreateContext();
        Assert.Equal(jobs, await context.Jobs.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(submissions, await context.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM job_submissions").SingleAsync(TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> PostWithKeyAsync(string json, params string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/jobs")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", keys));
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task AssertRejectedAsync(string json, HttpStatusCode expectedStatus, string expectedCode)
    {
        var response = await PostAsync(json);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal($"urn:synestra:problem:{expectedCode.Replace('_', '-')}", problem.GetProperty("type").GetString());
        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        await using var context = CreateContext();
        Assert.Empty(await context.Jobs.ToListAsync(TestContext.Current.CancellationToken));
    }

    private Task<HttpResponseMessage> PostAsync(string json) => _client.PostAsync(
        "/api/client/jobs",
        new StringContent(json, Encoding.UTF8, "application/json"),
        TestContext.Current.CancellationToken);

    private async Task<JobDefinition> AddDefinitionAsync(bool enabled)
    {
        var definition = new JobDefinition($"test.{Guid.NewGuid():N}", "Test definition", null, enabled, DateTime.UtcNow);
        await using var context = CreateContext();
        context.JobDefinitions.Add(definition);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return definition;
    }

    private SynestraDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .Options;
        return new SynestraDbContext(options);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:synestra", _database.ConnectionString));
}
