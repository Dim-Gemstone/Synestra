using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoApiInstances_CompletionRacePersistsOneSnapshot(bool conflicting)
    {
        var execution = await AcquireAsync();
        var id = Guid.CreateVersion7();
        await using var factory = Factory();
        using var other = factory.CreateClient();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE", Token);
        var first = SendAsync(execution, "completion", Body(id));
        var second = SendAsync(execution, "completion", Body(id, conflicting ? "failed" : "succeeded"), other);
        await WaitForBlockedAsync(2);
        await gate.CommitAsync(Token);
        var responses = await Task.WhenAll(first, second);
        if (conflicting)
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ProblemAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict), 409, "completion_report_conflict");
        }
        else
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(await responses[0].Content.ReadAsStringAsync(Token), await responses[1].Content.ReadAsStringAsync(Token));
        }
        var body = await responses.First(response => response.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>(Token);
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease).SingleAsync(Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(body.GetProperty("finishedAtUtc").GetDateTime(), job.CompletedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.Lease!.ReleasedAtUtc);
    }

    [Fact]
    public async Task TwoApiInstances_RenewalCompletionRaceRemainsConsistent()
    {
        var execution = await AcquireAsync();
        _clock.Now = Now.AddSeconds(10);
        await using var factory = Factory();
        using var other = factory.CreateClient();
        await using var context = Context();
        await using var gate = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlRawAsync("SELECT * FROM workers FOR UPDATE", Token);
        var renewal = SendAsync(execution, "renewal");
        var completion = SendAsync(execution, "completion", Body(Guid.CreateVersion7()), other);
        await WaitForBlockedAsync(2);
        await gate.CommitAsync(Token);
        Assert.Equal(HttpStatusCode.OK, (await completion).StatusCode);
        var renewed = await renewal;
        var lease = await context.Leases.SingleAsync(Token);
        Assert.Equal(Now.AddSeconds(10).UtcDateTime, lease.ReleasedAtUtc);
        if (renewed.StatusCode == HttpStatusCode.OK)
            Assert.Equal(Now.AddSeconds(40).UtcDateTime, lease.ExpiresAtUtc);
        else
        {
            await ProblemAsync(renewed, 409, "lease_not_active");
            Assert.Equal(Now.AddSeconds(30).UtcDateTime, lease.ExpiresAtUtc);
        }
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
}
