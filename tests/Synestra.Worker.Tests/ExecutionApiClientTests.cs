using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class ExecutionApiClientTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("attempt")]
    [InlineData("payload")]
    [InlineData("token")]
    [InlineData("token_padding_bits")]
    [InlineData("expiration")]
    [InlineData("timestamp")]
    public async Task InvalidClaimSuccessIsAmbiguousAndNeverTreatedAsEmpty204(string invalid)
    {
        var clock = new ControlledTimeProvider();
        var execution = ExecutionTestProtocol.Execution(clock);
        using var valid = ExecutionTestProtocol.Claim(execution);
        var original = await valid.Content.ReadAsStringAsync(Token);
        var json = JsonNode.Parse(original)!;
        switch (invalid)
        {
            case "missing": json.AsObject().Remove("payload"); break;
            case "type": json["type"] = "unsupported"; break;
            case "id": json["attemptId"] = Guid.NewGuid(); break;
            case "attempt": json["attemptNumber"] = 0; break;
            case "payload": json["payload"] = new JsonArray(); break;
            case "token": json["leaseToken"] = "private"; break;
            case "token_padding_bits": json["leaseToken"] = new string('A', 42) + "B"; break;
            case "expiration": json["expiresAtUtc"] = execution.AcquiredAtUtc; break;
            case "timestamp": json["acquiredAtUtc"] = "2026-09-07T12:00:00"; break;
        }
        var body = invalid switch
        {
            "empty" => "", "duplicate" => original.Replace("\"attemptNumber\":1", "\"attemptNumber\":1,\"attemptNumber\":1"),
            _ => json.ToJsonString()
        };
        using var handler = new WorkerHostTests.Handler((_, _) => Task.FromResult(Response(body)));
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() => new WorkerApiClient(http, clock)
            .ClaimAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Token));
        Assert.Equal("invalid_claim_response", error.Code);
    }

    [Fact]
    public async Task NoContentIsTheOnlyEmptyClaimAndLeaseOperationsCarryBothCredentials()
    {
        var clock = new ControlledTimeProvider();
        var execution = ExecutionTestProtocol.Execution(clock);
        var worker = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var report = WorkloadOutcome.InvalidInput().Freeze();
        using var handler = new WorkerHostTests.Handler((request, _) =>
        {
            Assert.Equal(session.ToString("D"), Assert.Single(request.Headers.GetValues("Worker-Session-Id")));
            var operation = request.RequestUri!.Segments[^1];
            if (operation == "claims")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Null(request.Content);
                Assert.False(request.Headers.Contains("Lease-Token"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Assert.Equal(execution.LeaseToken, Assert.Single(request.Headers.GetValues("Lease-Token")));
            Assert.Equal($"/api/worker/workers/{worker:D}/leases/{execution.LeaseId:D}/{operation}", request.RequestUri.AbsolutePath);
            if (operation == "renewal")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Null(request.Content);
                return Task.FromResult(ExecutionTestProtocol.Json(new { execution.LeaseId, execution.ExpiresAtUtc }));
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            return Task.FromResult(ExecutionTestProtocol.Completion(execution,
                JsonSerializer.SerializeToElement(report, JsonSerializerOptions.Web), clock));
        });
        using var http = Http(handler);
        var api = new WorkerApiClient(http, clock);
        Assert.Null(await api.ClaimAsync(worker, session, Token));
        Assert.Equal(execution.ExpiresAtUtc, await api.RenewAsync(worker, session, execution, Token));
        await api.CompleteAsync(worker, session, execution, report, Token);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData("renewal", "leaseId")]
    [InlineData("renewal", "expiresAtUtc")]
    [InlineData("completion", "reportId")]
    [InlineData("completion", "jobId")]
    [InlineData("completion", "attemptId")]
    [InlineData("completion", "outcome")]
    [InlineData("completion", "result")]
    [InlineData("completion", "finishedAtUtc")]
    public async Task AcknowledgementMustMatchExecutionReportAndUtcContract(string operation, string field)
    {
        var clock = new ControlledTimeProvider();
        var execution = ExecutionTestProtocol.Execution(clock);
        var report = new WorkloadOutcome(JsonSerializer.SerializeToElement(new { count = 1, sum = 1 }), null).Freeze();
        using var valid = operation == "renewal"
            ? ExecutionTestProtocol.Json(new { execution.LeaseId, execution.ExpiresAtUtc })
            : ExecutionTestProtocol.Completion(execution, JsonSerializer.SerializeToElement(report, JsonSerializerOptions.Web), clock);
        var json = JsonNode.Parse(await valid.Content.ReadAsStringAsync(Token))!;
        if (field.EndsWith("Id", StringComparison.Ordinal)) json[field] = Guid.CreateVersion7();
        else if (field.EndsWith("Utc", StringComparison.Ordinal)) json[field] = "2026-09-07T12:00:00";
        else if (field == "result") json[field] = new JsonObject { ["count"] = 1, ["sum"] = 2 };
        else json[field] = "failed";
        using var handler = new WorkerHostTests.Handler((_, _) => Task.FromResult(Response(json.ToJsonString())));
        using var http = Http(handler);
        var api = new WorkerApiClient(http, clock);
        var error = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
        {
            if (operation == "renewal") await api.RenewAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), execution, Token);
            else await api.CompleteAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), execution, report, Token);
        });
        Assert.Equal($"invalid_{operation}_response", error.Code);
    }

    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
