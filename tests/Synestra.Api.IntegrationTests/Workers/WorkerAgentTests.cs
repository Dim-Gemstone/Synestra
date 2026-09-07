using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synestra.Api.IntegrationTests.Infrastructure;
using Synestra.Persistence;
using Synestra.Worker;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

[Collection(PostgreSqlCollection.Name)]
public sealed class WorkerAgentTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private readonly ControlledTimeProvider _clock = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synestra-agent-api-tests", Guid.NewGuid().ToString("N"));
    private PostgreSqlTestDatabase _database = null!;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync(Token);
        await using var context = Context();
        await context.Database.MigrateAsync(Token);
    }

    [Fact]
    public async Task WorkerAgent_LivenessSurvivesApiRestartAndWorkerRestartReplacesSession()
    {
        await using var firstApi = Factory();
        using var firstTransport = new HttpMessageInvoker(firstApi.Server.CreateHandler());
        using var routing = new Routing(firstTransport);
        Guid id;
        Guid firstSession;
        DateTime registered;
        using (var agent = Agent(routing))
        {
            await agent.StartAsync(Token);
            await TickCompletedAsync();
            await using (var context = Context())
            {
                var worker = await context.Workers.Include(worker => worker.SupportedTypes).SingleAsync(Token);
                id = worker.Id;
                firstSession = worker.SessionId!.Value;
                registered = worker.RegisteredAtUtc;
                Assert.Equal(1, worker.Capacity);
                Assert.Equal(WorkerOptions.SupportedType, Assert.Single(worker.SupportedTypes).Type);
                Assert.Equal(_clock.GetUtcNow().UtcDateTime, worker.LastSeenAtUtc);
            }
            _clock.Advance(TimeSpan.FromSeconds(10));
            await TickCompletedAsync();
            await AssertLivenessAsync(id, firstSession);

            await firstApi.DisposeAsync();
            await using var restartedApi = Factory();
            using var restartedTransport = new HttpMessageInvoker(restartedApi.Server.CreateHandler());
            routing.Target = restartedTransport;
            _clock.Advance(TimeSpan.FromSeconds(10));
            await TickCompletedAsync();
            await AssertLivenessAsync(id, firstSession);
            Assert.Equal(1, routing.Registrations);
            await agent.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(0, agent.Services.GetRequiredService<WorkerExitStatus>().ExitCode);

            using var newAgent = Agent(routing);
            await newAgent.StartAsync(Token);
            await TickCompletedAsync();
            await using (var context = Context())
            {
                var worker = await context.Workers.SingleAsync(Token);
                Assert.Equal(id, worker.Id);
                Assert.NotEqual(firstSession, worker.SessionId);
                Assert.Equal(registered, worker.RegisteredAtUtc);
                Assert.Equal(_clock.GetUtcNow().UtcDateTime, worker.SessionStartedAtUtc);
                Assert.Empty(await context.Jobs.ToListAsync(Token));
                Assert.Empty(await context.JobAttempts.ToListAsync(Token));
                Assert.Empty(await context.Leases.ToListAsync(Token));
            }
            using var stale = new HttpRequestMessage(HttpMethod.Post, $"http://localhost/api/worker/workers/{id}/heartbeat");
            stale.Headers.Add("Worker-Session-Id", firstSession.ToString());
            using var response = await restartedTransport.SendAsync(stale, Token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await newAgent.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(2, routing.Registrations);
        }
        Assert.Equal(0, _clock.ActiveTimers);
    }

    [Fact]
    public async Task WorkerAgent_ReplacementFencesHeartbeatAndStopsWithoutReplacingTheNewSession()
    {
        await using var api = Factory();
        using var transport = new HttpMessageInvoker(api.Server.CreateHandler());
        using var routing = new Routing(transport);
        using var agent = Agent(routing);
        await agent.StartAsync(Token);
        await TickCompletedAsync();
        Guid id;
        await using (var context = Context()) id = (await context.Workers.SingleAsync(Token)).Id;
        var replacement = Guid.CreateVersion7();
        using var replacementRequest = new HttpRequestMessage(HttpMethod.Put, $"http://localhost/api/worker/workers/{id}/registration")
        {
            Content = JsonContent.Create(new { sessionId = replacement, name = "replacement", capacity = 1, supportedTypes = new[] { WorkerOptions.SupportedType } })
        };
        using var response = await transport.SendAsync(replacementRequest, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = agent.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        _clock.Advance(TimeSpan.FromSeconds(10));
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await agent.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, agent.Services.GetRequiredService<WorkerExitStatus>().ExitCode);
        Assert.Equal(1, routing.Registrations);
        Assert.Equal(0, _clock.ActiveTimers);
        await using var final = Context();
        var worker = await final.Workers.SingleAsync(Token);
        Assert.Equal(replacement, worker.SessionId);
        Assert.Equal("replacement", worker.Name);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(-10).UtcDateTime, worker.LastSeenAtUtc);
    }

    private async Task AssertLivenessAsync(Guid id, Guid session)
    {
        await using var context = Context();
        var worker = await context.Workers.SingleAsync(Token);
        Assert.Equal(id, worker.Id);
        Assert.Equal(session, worker.SessionId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, worker.LastSeenAtUtc);
    }
    private Task TickCompletedAsync() => _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:synestra", _database.ConnectionString)
            .UseSetting("ExecutionFinalization:Enabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock)));
    private IHost Agent(HttpMessageHandler routing)
    {
        var builder = WorkerHost.CreateBuilder(["--Worker:ApiBaseAddress", "http://localhost", "--Worker:StateDirectory", _directory]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(new HttpClient(routing, disposeHandler: false) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan });
        return builder.Build();
    }
    private SynestraDbContext Context() => new(new DbContextOptionsBuilder<SynestraDbContext>().UseNpgsql(_database.ConnectionString).Options);
    private sealed class Routing(HttpMessageInvoker target) : HttpMessageHandler
    {
        public HttpMessageInvoker Target { get; set; } = target;
        public int Registrations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains(request.RequestUri!.Segments[^1], new[] { "registration", "heartbeat", "claims" });
            if (request.Method == HttpMethod.Put) Registrations++;
            return Target.SendAsync(request, cancellationToken);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
