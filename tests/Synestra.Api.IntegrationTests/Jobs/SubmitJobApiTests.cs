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
