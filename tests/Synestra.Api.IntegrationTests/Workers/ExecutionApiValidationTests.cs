using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Synestra.Application.Workers;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    [Fact]
    public async Task Completion_StrictDiscriminatorAndNestedErrorValidationPrecedeLookup()
    {
        var id = Guid.CreateVersion7().ToString();
        var bodies = new[]
        {
            "", "null", "[]", "{}", "{", "{\"reportId\":\"ID\",\"outcome\":\"succeeded\"}",
            "{\"outcome\":\"succeeded\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"Succeeded\",\"result\":{}}",
            "{\"ReportId\":\"ID\",\"outcome\":\"succeeded\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"Outcome\":\"succeeded\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"Result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"result\":{},\"extra\":true}",
            "{\"reportId\":\"ID\",\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"outcome\":\"succeeded\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"result\":{},\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"result\":{},\"error\":null}",
            "{\"reportId\":\"ID\",\"outcome\":\"succeeded\",\"error\":{\"code\":\"x\",\"message\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"result\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":null}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"code\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"message\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"Code\":\"x\",\"message\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"code\":\"x\",\"message\":\"x\",\"extra\":1}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"code\":\"x\",\"code\":\"x\",\"message\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"code\":null,\"message\":\"x\"}}",
            "{\"reportId\":\"ID\",\"outcome\":\"failed\",\"error\":{\"code\":\"x\",\"message\":3}}"
        };
        foreach (var body in bodies)
            await ProblemAsync(await SendAsync(Unknown(), "completion", body.Replace("ID", id, StringComparison.Ordinal)), 400, "invalid_request");
    }

    [Fact]
    public async Task ResultAndErrorLimits_AreEnforcedBeforePersistence()
    {
        var id = Guid.CreateVersion7();
        foreach (var result in new[]
        {
            "null", "[]", "true", "42", "\"string\"", "{\"x\":1,\"x\":2}",
            "{\"x\":[{\"a\":1,\"\\u0061\":2}]}", "{\"x\":\"\\u0000\"}",
            "{\"x\":\"\\uD800\"}", "{\"x\":1e131072}", "{\"x\":1e-16384}",
            "{\"x\":\"" + new string('x', 65529) + "\"}",
            "{\"x\":\"" + new string('я', 32765) + "\"}",
            "{\"x\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 11000)) + "\"}",
            "{\"x\":" + new string('[', 32) + "0" + new string(']', 32) + "}"
        })
            await ProblemAsync(await SendAsync(Unknown(), "completion", Body(id, result: result)), 400, "invalid_request");
        foreach (var field in new[] { "code", "message" })
        foreach (var value in new[] { "", " ", "\u2000", "a\0", new string('x', field == "code" ? 101 : 2001) })
        {
            var error = new Dictionary<string, string> { ["code"] = "error", ["message"] = "message", [field] = value };
            await ProblemAsync(await SendAsync(Unknown(), "completion", JsonSerializer.Serialize(new { reportId = id, outcome = "failed", error })), 400, "invalid_request");
        }
        var execution = await AcquireAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(execution, "completion",
            Body(id, result: "{\"x\":\"" + new string('x', 65528) + "\"}"))).StatusCode);
        var deep = await AcquireAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(deep, "completion",
            Body(Guid.CreateVersion7(), result: "{\"x\":" + new string('[', 31) + "0" + new string(']', 31) + "}"))).StatusCode);
    }

    [Fact]
    public async Task Transport_IsBoundedWithOrWithoutContentLengthAndRejectsInvalidUtf8()
    {
        foreach (var unknownLength in new[] { false, true })
        {
            using var request = Request(Unknown(), "completion");
            var bytes = Encoding.UTF8.GetBytes(Body(Guid.CreateVersion7()) + new string(' ', 68 * 1024));
            request.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            await ProblemAsync(await _client.SendAsync(request, Token), 400, "invalid_request");
        }
        using var malformed = Request(Unknown(), "completion");
        var invalidUtf8 = Encoding.UTF8.GetBytes(Body(Guid.CreateVersion7(), result: "{\"x\":\"?\"}"));
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'?')] = 0xff;
        malformed.Content = new ByteArrayContent(invalidUtf8);
        malformed.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await ProblemAsync(await _client.SendAsync(malformed, Token), 400, "invalid_request");
        using var wrongMedia = Request(Unknown(), "completion");
        wrongMedia.Content = new StringContent(Body(Guid.CreateVersion7()), Encoding.UTF8, "text/plain");
        await ProblemAsync(await _client.SendAsync(wrongMedia, Token), 400, "invalid_request");
    }

    [Fact]
    public async Task PathSessionAndReportIdentities_RequireRfcUuidV7()
    {
        foreach (var invalid in new[] { "malformed", Guid.Empty.ToString(), Guid.NewGuid().ToString(), "019ec569-5a00-7000-0000-000000000001" })
        foreach (var operation in new[] { "renewal", "completion" })
        foreach (var field in new[] { "worker", "lease", "session", "report" })
        {
            if (field == "report" && operation != "completion") continue;
            var execution = Unknown();
            using var request = Request(execution, operation);
            request.Content = new StringContent(Body(Guid.CreateVersion7()), Encoding.UTF8, "application/json");
            if (field is "worker" or "lease")
                request.RequestUri = new Uri($"/api/worker/workers/{(field == "worker" ? invalid : execution.WorkerId.ToString())}/leases/{(field == "lease" ? invalid : execution.LeaseId.ToString())}/{operation}", UriKind.Relative);
            if (field == "session")
            {
                request.Headers.Remove("Worker-Session-Id");
                request.Headers.TryAddWithoutValidation("Worker-Session-Id", invalid);
            }
            if (field == "report")
                request.Content = new StringContent("{\"reportId\":\"" + invalid + "\",\"outcome\":\"succeeded\",\"result\":{}}", Encoding.UTF8, "application/json");
            await ProblemAsync(await _client.SendAsync(request, Token), 400, "invalid_request");
        }
    }

    [Fact]
    public async Task Headers_RejectMissingRepeatedMalformedAndNonCanonicalValues()
    {
        foreach (var operation in new[] { "renewal", "completion" })
        foreach (var header in new[] { "Worker-Session-Id", "Lease-Token" })
        {
            var valid = header == "Lease-Token" ? LeaseToken.Generate() : Guid.CreateVersion7().ToString();
            foreach (var values in new string[]?[] { null, [""], ["malformed"], [valid, valid], [valid + "," + valid], [valid + "="], [new string('A', 42) + "B"] })
            {
                using var request = Request(Unknown(), operation);
                request.Headers.Remove(header);
                if (values is not null) request.Headers.TryAddWithoutValidation(header, values);
                if (operation == "completion") request.Content = new StringContent(Body(Guid.CreateVersion7()), Encoding.UTF8, "application/json");
                await ProblemAsync(await _client.SendAsync(request, Token), 400, "invalid_request");
            }
        }
    }

    [Fact]
    public async Task Claim_CannotAcceptWorkerChosenToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{Guid.CreateVersion7()}/claims");
        request.Headers.Add("Worker-Session-Id", Guid.CreateVersion7().ToString());
        request.Headers.Add("Lease-Token", LeaseToken.Generate());
        await ProblemAsync(await _client.SendAsync(request, Token), 400, "invalid_request");
    }

    [Fact]
    public async Task ReleasedReportlessLease_IsNotActiveAndCannotBeFinalizedAgain()
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE leases SET released_at_utc = {Now.UtcDateTime}", Token);
        _clock.Now = Now.AddSeconds(31);
        await ProblemAsync(await SendAsync(execution, "renewal"), 409, "lease_not_active");
        await ProblemAsync(await SendAsync(execution, "completion", Body(Guid.CreateVersion7())), 409, "attempt_already_finalized");
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
