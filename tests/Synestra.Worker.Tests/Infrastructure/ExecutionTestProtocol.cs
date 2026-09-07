using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Synestra.Worker.Testing;

namespace Synestra.Worker.Tests;

internal static class ExecutionTestProtocol
{
    public static ClaimedExecution Execution(ControlledTimeProvider clock, int durationMs = 40000, int leaseSeconds = 30) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), new string('A', 43), 1,
            JsonSerializer.SerializeToElement(new { values = new[] { 1, -2, 3 }, durationMs }),
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().AddSeconds(leaseSeconds).UtcDateTime, clock.GetTimestamp());

    public static HttpResponseMessage Claim(ClaimedExecution execution) => Json(new
    {
        execution.JobId, execution.AttemptId, execution.LeaseId, execution.LeaseToken, execution.AttemptNumber,
        type = WorkerOptions.SupportedType, execution.Payload, execution.AcquiredAtUtc, execution.ExpiresAtUtc
    });

    public static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    public static HttpResponseMessage Problem(string code) => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { code }), System.Text.Encoding.UTF8, "application/problem+json")
    };

    public static HttpResponseMessage Completion(ClaimedExecution execution, JsonElement report, ControlledTimeProvider clock)
    {
        var body = new Dictionary<string, object?>
        {
            ["jobId"] = execution.JobId, ["attemptId"] = execution.AttemptId, ["leaseId"] = execution.LeaseId,
            ["reportId"] = report.GetProperty("reportId"), ["outcome"] = report.GetProperty("outcome"),
            ["finishedAtUtc"] = clock.GetUtcNow().UtcDateTime
        };
        var data = report.GetProperty("outcome").GetString() == "succeeded" ? "result" : "error";
        body[data] = report.GetProperty(data);
        return Json(body);
    }
}
