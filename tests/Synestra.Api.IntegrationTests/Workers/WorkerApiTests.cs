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
public sealed class WorkerApiTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
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
    public async Task RegistrationAndHeartbeat_PreserveContractAcrossApiRestartAndSessionReplacement()
    {
        var id = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var response = await PutAsync(id.ToString(), Body(session));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(new[] { "capacity", "heartbeatIntervalSeconds", "lastSeenAtUtc", "name", "offlineAfterSeconds", "registeredAtUtc", "sessionId", "sessionStartedAtUtc", "supportedTypes", "workerId" },
            body.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id, body.GetProperty("workerId").GetGuid());
        Assert.Equal(session, body.GetProperty("sessionId").GetGuid());
        Assert.Equal(4, body.GetProperty("capacity").GetInt32());
        Assert.Equal("worker-01", body.GetProperty("name").GetString());
        Assert.Equal(["A", "browser.capture-page"], body.GetProperty("supportedTypes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(10, body.GetProperty("heartbeatIntervalSeconds").GetInt32());
        Assert.Equal(30, body.GetProperty("offlineAfterSeconds").GetInt32());
        foreach (var field in new[] { "registeredAtUtc", "sessionStartedAtUtc", "lastSeenAtUtc" })
        {
            Assert.EndsWith("Z", body.GetProperty(field).GetString());
            Assert.Equal(Now.UtcDateTime, body.GetProperty(field).GetDateTime());
        }

        _clock.Now = Now.AddSeconds(10);
        var updated = await DetailsAsync(await PutAsync(id.ToString(), Body(session, "updated", 2, ["new"])));
        Assert.Equal("updated", updated.Name);
        Assert.Equal(2, updated.Capacity);
        Assert.Equal(["new"], updated.SupportedTypes);
        Assert.Equal(Now.UtcDateTime, updated.RegisteredAtUtc);
        Assert.Equal(Now.UtcDateTime, updated.SessionStartedAtUtc);
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, updated.LastSeenAtUtc);
        var repeated = await DetailsAsync(await PutAsync(id.ToString(), Body(session, "updated", 2, ["new"])));
        Assert.Equal(JsonSerializer.Serialize(updated), JsonSerializer.Serialize(repeated));

        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = Factory();
        _client = _factory.CreateClient();
        _clock.Now = Now.AddSeconds(20);
        var heartbeat = await HeartbeatAsync(id.ToString(), session.ToString());
        Assert.Equal(HttpStatusCode.NoContent, heartbeat.StatusCode);
        Assert.Empty(await heartbeat.Content.ReadAsByteArrayAsync(Token));
        await using (var context = Context())
        {
            var worker = await context.Workers.Include(x => x.SupportedTypes).SingleAsync(Token);
            Assert.Equal(Now.AddSeconds(20).UtcDateTime, worker.LastSeenAtUtc);
            Assert.Equal("updated", worker.Name);
            Assert.Equal(2, worker.Capacity);
            Assert.Equal("new", Assert.Single(worker.SupportedTypes).Type);
        }

        var newSession = Guid.CreateVersion7();
        var replacement = await DetailsAsync(await PutAsync(id.ToString(), Body(newSession, "replacement", 1, ["replacement"])));
        Assert.Equal(Now.UtcDateTime, replacement.RegisteredAtUtc);
        Assert.Equal(Now.AddSeconds(20).UtcDateTime, replacement.SessionStartedAtUtc);
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = Factory();
        _client = _factory.CreateClient();
        _clock.Now = Now.AddDays(1);
        await AssertProblemAsync(await PutAsync(id.ToString(), Body(session)), HttpStatusCode.Conflict, "worker_session_replaced");
        await AssertProblemAsync(await HeartbeatAsync(id.ToString(), session.ToString()), HttpStatusCode.Conflict, "worker_session_replaced");
        await using (var context = Context())
            Assert.Equal(replacement.LastSeenAtUtc, (await context.Workers.SingleAsync(Token)).LastSeenAtUtc);
        Assert.Equal(HttpStatusCode.NoContent, (await HeartbeatAsync(id.ToString(), newSession.ToString())).StatusCode);
        await AssertProblemAsync(await HeartbeatAsync(Guid.CreateVersion7().ToString(), newSession.ToString()), HttpStatusCode.NotFound, "worker_not_found");
    }

    public static IEnumerable<object[]> InvalidBodies()
    {
        var session = Guid.CreateVersion7();
        foreach (var value in new[] { "", "{", "null", "[]", "{}", "true", "{\"sessionId\":1}", Body(Guid.NewGuid()), Body(Guid.Empty) }) yield return [value];
        foreach (var name in new[] { "", " \t", new string('n', 201), "a\0b" }) yield return [Body(session, name)];
        yield return [Body(session, capacity: 0)];
        yield return [Body(session, capacity: -1)];
        foreach (var types in new string[][] { [], ["a", "a"], [null!], [" "], [new string('t', 101)], Enumerable.Range(0, 101).Select(x => x.ToString()).ToArray() })
            yield return [Body(session, types: types)];
        var valid = Body(session);
        yield return [valid[..^1] + ",\"unknown\":true}"];
        yield return [valid[..^1] + ",\"name\":\"duplicate\"}"];
        yield return [valid.Replace("\"capacity\":4", "\"capacity\":1.5")];
        yield return [valid.Replace("\"capacity\":4", "\"capacity\":2147483648")];
        yield return [valid.Replace("\"capacity\":4", "\"capacity\":null")];
        yield return [valid.Replace("\"name\":\"worker-01\"", "\"name\":null")];
        yield return [valid.Replace("worker-01", "\\uD800")];
        yield return [valid.Replace("browser.capture-page", "\\uDC00")];
        yield return [valid.Replace("\"sessionId\"", "\"SessionId\"")];
        yield return [valid.Replace(session.ToString(), "malformed")];
        yield return [valid.Replace(session.ToString(), "019ec569-5a00-7000-0000-000000000001")];
        foreach (var field in new[] { "sessionId", "name", "capacity", "supportedTypes" })
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valid)!;
            fields.Remove(field);
            yield return [JsonSerializer.Serialize(fields)];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidRegistration_ReturnsStableProblemWithoutCreatingWorker(string body)
    {
        await AssertProblemAsync(await PutAsync(Guid.CreateVersion7().ToString(), body), HttpStatusCode.BadRequest, "invalid_request");
        await using var context = Context();
        Assert.Empty(await context.Workers.ToListAsync(Token));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-0000-000000000001")]
    public async Task InvalidWorkerIdentity_Returns400ForBothEndpoints(string id)
    {
        await AssertProblemAsync(await PutAsync(id, Body(Guid.CreateVersion7())), HttpStatusCode.BadRequest, "invalid_request");
        await AssertProblemAsync(await HeartbeatAsync(id, Guid.CreateVersion7().ToString()), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-8000-000000000001,019ec569-5a00-7000-8000-000000000002")]
    public async Task InvalidSessionHeader_Returns400BeforeLookup(string? header) =>
        await AssertProblemAsync(await HeartbeatAsync(Guid.CreateVersion7().ToString(), header), HttpStatusCode.BadRequest, "invalid_request");

    [Fact]
    public async Task RepeatedSessionHeaders_Return400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{Guid.CreateVersion7()}/heartbeat");
        request.Headers.TryAddWithoutValidation("Worker-Session-Id", new[] { Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString() });
        await AssertProblemAsync(await _client.SendAsync(request, Token), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task BodyLimit_AppliesWithAndWithoutContentLengthAndAllowsMaximumEscapedTypes()
    {
        var oversized = Body(Guid.CreateVersion7()) + new string(' ', 64 * 1024);
        await AssertProblemAsync(await PutAsync(Guid.CreateVersion7().ToString(), oversized), HttpStatusCode.BadRequest, "invalid_request");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(oversized));
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = "PUT";
            context.Request.Path = $"/api/worker/workers/{Guid.CreateVersion7()}/registration";
            context.Request.ContentType = "application/json";
            context.Request.Body = stream;
            context.Request.ContentLength = null;
        }, Token);
        Assert.Equal(400, response.Response.StatusCode);
        using var problem = await JsonDocument.ParseAsync(response.Response.Body, cancellationToken: Token);
        Assert.Equal("invalid_request", problem.RootElement.GetProperty("code").GetString());

        var types = Enumerable.Range(0, 100).Select(index => new string('ж', 97) + index.ToString("D3")).ToArray();
        var maximum = Body(Guid.CreateVersion7(), new string('ж', 200), int.MaxValue, types);
        Assert.True(Encoding.UTF8.GetByteCount(maximum) < 64 * 1024);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Guid.CreateVersion7().ToString(), maximum)).StatusCode);
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsStableInternalError()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IWorkerPersistence, FailingPersistence>()));
        using var client = factory.CreateClient();
        var response = await client.PutAsync($"/api/worker/workers/{Guid.CreateVersion7()}/registration",
            new StringContent(Body(Guid.CreateVersion7()), Encoding.UTF8, "application/json"), Token);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task UnsupportedMediaType_ReturnsInvalidRequest()
    {
        var response = await _client.PutAsync($"/api/worker/workers/{Guid.CreateVersion7()}/registration", new StringContent(Body(Guid.CreateVersion7())), Token);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
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
        Assert.False(problem.TryGetProperty("payload", out _));
    }
    private async Task<WorkerDetails> DetailsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkerDetails>(Token))!;
    }
    private static string Body(Guid session, string name = "worker-01", int capacity = 4, string[]? types = null) =>
        JsonSerializer.Serialize(new { sessionId = session, name, capacity, supportedTypes = types ?? ["browser.capture-page", "A"] });
    private Task<HttpResponseMessage> PutAsync(string id, string body) => _client.PutAsync($"/api/worker/workers/{id}/registration", new StringContent(body, Encoding.UTF8, "application/json"), Token);
    private async Task<HttpResponseMessage> HeartbeatAsync(string id, string? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{id}/heartbeat");
        if (session is not null) request.Headers.TryAddWithoutValidation("Worker-Session-Id", session);
        return await _client.SendAsync(request, Token);
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
    private sealed class FailingPersistence : IWorkerPersistence
    {
        public Task<IWorkerTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Test failure.");
    }
}
