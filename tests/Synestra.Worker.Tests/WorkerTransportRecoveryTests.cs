using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class WorkerTransportRecoveryTests
{
    private readonly ControlledTimeProvider _clock = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("registration", "disconnect")]
    [InlineData("registration", "server_error")]
    [InlineData("renewal", "disconnect")]
    [InlineData("renewal", "server_error")]
    [InlineData("completion", "disconnect")]
    [InlineData("completion", "server_error")]
    [InlineData("completion", "body_read")]
    public async Task RecoveryUsesFreshRequestsWithIdenticalIdentityHeadersAndBody(string operation, string failure)
    {
        var execution = ExecutionTestProtocol.Execution(_clock);
        var report = new WorkloadOutcome(JsonSerializer.SerializeToElement(new { count = 1, sum = 42 }), null).Freeze();
        var requests = new List<HttpRequestMessage>();
        var signatures = new List<string>();
        using var handler = new WorkerHostTests.Handler(async (request, token) =>
        {
            requests.Add(request);
            signatures.Add($"{request.Method} {request.RequestUri} {request.Headers} " +
                (request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (requests.Count < 3)
            {
                if (failure == "disconnect") throw new HttpRequestException("private transport contents");
                if (failure == "body_read")
                {
                    var content = new StreamContent(new InterruptedStream());
                    content.Headers.ContentType = new("application/json");
                    return new(HttpStatusCode.OK) { Content = content };
                }
                return Error(500, "internal_error");
            }
            return operation switch
            {
                "registration" => WorkerHostTests.Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token)),
                "renewal" => ExecutionTestProtocol.Json(new { execution.LeaseId, execution.ExpiresAtUtc }),
                _ => ExecutionTestProtocol.Completion(execution, JsonSerializer.SerializeToElement(report, JsonSerializerOptions.Web), _clock)
            };
        });
        using var http = Http(handler);
        var api = new WorkerApiClient(http, _clock);
        var call = InvokeAsync(api, operation, execution, report, Token);
        for (var attempt = 1; attempt < 3; attempt++)
        {
            await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
            Assert.Equal(attempt, requests.Count);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        await call.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(3, requests.Count);
        Assert.Equal(3, requests.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Single(signatures.Distinct());
        Assert.Equal(0, _clock.ActiveTimers);
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("renewal")]
    [InlineData("completion")]
    public async Task RecoveryHasFifteenSecondTotalBudgetIncludingWaitsAndThirdRequest(string operation)
    {
        var calls = 0;
        using var handler = new WorkerHostTests.Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var http = Http(handler);
        var task = InvokeAsync(new WorkerApiClient(http, _clock), operation, ExecutionTestProtocol.Execution(_clock), WorkloadOutcome.InvalidInput().Freeze(), Token);
        for (var attempt = 1; attempt < 3; attempt++)
        {
            await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5));
            Assert.Equal(attempt, calls);
            _clock.ShiftUtc(TimeSpan.FromHours(-1));
            _clock.Advance(TimeSpan.FromSeconds(5));
            await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        Assert.Equal(3, calls);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Equal(TimeSpan.FromSeconds(15), _clock.GetElapsedTime(0));
        Assert.Equal(0, _clock.ActiveTimers);
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(404, "lease_not_found")]
    [InlineData(409, "worker_session_replaced")]
    [InlineData(409, "lease_ownership_lost")]
    [InlineData(409, "lease_expired")]
    [InlineData(409, "lease_not_active")]
    [InlineData(409, "attempt_already_finalized")]
    [InlineData(409, "completion_report_conflict")]
    [InlineData(500, "unknown_private_code")]
    [InlineData(503, "internal_error")]
    public async Task DomainRefusalsAndUnknownResponsesAreNeverRetried(int status, string code)
    {
        var calls = 0;
        using var handler = new WorkerHostTests.Handler((_, _) => { calls++; return Task.FromResult(Error(status, code)); });
        using var http = Http(handler);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => new WorkerApiClient(http, _clock).CompleteAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ExecutionTestProtocol.Execution(_clock), WorkloadOutcome.InvalidInput().Freeze(), Token));
        Assert.Equal(1, calls);
        Assert.Equal(0, _clock.ActiveTimers);
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("renewal")]
    [InlineData("completion")]
    public async Task CallerCancellationDuringRecoveryWaitDoesNotDispatchAnotherRequest(string operation)
    {
        var calls = 0;
        using var handler = new WorkerHostTests.Handler((_, _) => { calls++; throw new IOException("private response"); });
        using var http = Http(handler);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var task = InvokeAsync(new WorkerApiClient(http, _clock), operation, ExecutionTestProtocol.Execution(_clock), WorkloadOutcome.InvalidInput().Freeze(), stop.Token);
        await _clock.WaitForTimersAsync(Token, TimeSpan.FromSeconds(1));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
        Assert.Equal(0, _clock.ActiveTimers);
    }

    private static async Task InvokeAsync(WorkerApiClient api, string operation, ClaimedExecution execution, CompletionReport report, CancellationToken token)
    {
        var worker = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        if (operation == "registration") await api.RegisterAsync(worker, session, "worker", token);
        else if (operation == "renewal") await api.RenewAsync(worker, session, execution, token);
        else await api.CompleteAsync(worker, session, execution, report, token);
    }
    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    private static HttpResponseMessage Error(int status, string code) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/problem+json") };
    private sealed class InterruptedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private truncated response"));
    }
}
