using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    public static IEnumerable<object[]> ClientResults()
    {
        yield return ["{\"n\":1e131071}"];
        yield return ["{\"n\":1e-16383}"];
        yield return ["{\"a\":[null,true,\"Привіт 👋\"],\"number\":9007199254740993}"];
        yield return [string.Concat(Enumerable.Repeat("{\"v\":", 31)) + "{}" + new string('}', 31)];
        yield return ["{\"v\":\"" + new string('<', 65528) + "\"}"];
    }

    [Theory]
    [MemberData(nameof(ClientResults))]
    public async Task ClientRead_ReturnsAcceptedResultAsObjectWithoutNumericExpansion(string result)
    {
        var execution = await AcquireAsync();
        using var reported = await SendAsync(execution, "completion", Body(Guid.CreateVersion7(), result: result));
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);

        using var response = await _client.GetAsync($"/api/client/jobs/{execution.JobId}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(text);
        var view = document.RootElement;
        AssertClientCompletion(view, execution, "succeeded");
        AssertJsonEqual(result, view.GetProperty("completion").GetProperty("result"));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("completion").GetProperty("error").ValueKind);
        Assert.DoesNotContain(execution.Secret, text);
        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        // Wire escaping may enlarge a 64 KiB result; exponent values must stay compact.
        Assert.True(text.Length < result.Length * 6 + 1024);
    }

    [Fact]
    public async Task ClientRead_ExpiredRunningExecutionRemainsRunningWithoutFinalization()
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddDays(1);
        var view = await _client.GetFromJsonAsync<JsonElement>($"/api/client/jobs/{execution.JobId}", Token);
        Assert.Equal("running", view.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("completion").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("completedAtUtc").ValueKind);
        await using var context = Context();
        var attempt = await context.JobAttempts.SingleAsync(Token);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null((await context.Leases.SingleAsync(Token)).ReleasedAtUtc);
    }

    [Fact]
    public async Task ClientRead_LegacyResultDoesNotRequireNewWorkerDepthRules()
    {
        var execution = await AcquireAsync();
        var result = string.Concat(Enumerable.Repeat("{\"v\":", 70)) + "{}" + new string('}', 70);
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = 'Succeeded', completed_at_utc = {Now.UtcDateTime};
            UPDATE job_attempts SET status = 'Succeeded', finished_at_utc = {Now.UtcDateTime}, result = {result}::jsonb;
            """, Token);

        using var response = await _client.GetAsync($"/api/client/jobs/{execution.JobId}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = new JsonDocumentOptions { MaxDepth = 100 };
        using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token), options);
        using var expected = JsonDocument.Parse(result, options);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement.GetProperty("completion").GetProperty("result")));
    }

    private static void AssertClientCompletion(JsonElement view, Acquired execution, string outcome)
    {
        AssertFields(view, "id", "type", "status", "priority", "maxAttempts", "createdAtUtc", "availableAtUtc", "completedAtUtc", "completion");
        Assert.Equal(execution.JobId, view.GetProperty("id").GetGuid());
        Assert.Equal(outcome == "succeeded" ? "succeeded" : "failed", view.GetProperty("status").GetString());
        Assert.EndsWith("Z", view.GetProperty("completedAtUtc").GetString());
        var completion = view.GetProperty("completion");
        AssertFields(completion, "attemptId", "attemptNumber", "outcome", "result", "error");
        Assert.Equal(execution.AttemptId, completion.GetProperty("attemptId").GetGuid());
        Assert.Equal(1, completion.GetProperty("attemptNumber").GetInt32());
        Assert.Equal(outcome, completion.GetProperty("outcome").GetString());
    }
}
