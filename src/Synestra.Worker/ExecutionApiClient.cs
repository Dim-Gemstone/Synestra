using System.Net;
using System.Text.Json;

namespace Synestra.Worker;

internal sealed partial class WorkerApiClient
{
    public async Task<ClaimedExecution?> ClaimAsync(Guid workerId, Guid sessionId, CancellationToken token)
    {
        using var request = SessionRequest(HttpMethod.Post, $"/api/worker/workers/{workerId:D}/claims", sessionId);
        var started = timeProvider.GetTimestamp();
        var bytes = await SendAsync(request, HttpStatusCode.OK, token, allowNoContent: true);
        if (bytes is null) return null;
        return ReadExecutionResponse(bytes, "invalid_claim_response", root =>
        {
            RequireFields(root, "jobId", "attemptId", "leaseId", "leaseToken", "attemptNumber", "type", "payload", "acquiredAtUtc", "expiresAtUtc");
            var jobId = ReadId(root, "jobId");
            var attemptId = ReadId(root, "attemptId");
            var leaseId = ReadId(root, "leaseId");
            var leaseToken = root.GetProperty("leaseToken").GetString();
            var attemptNumber = root.GetProperty("attemptNumber").GetInt32();
            var acquired = ReadUtc(root, "acquiredAtUtc");
            var expires = ReadUtc(root, "expiresAtUtc");
            if (root.GetProperty("type").GetString() != WorkerOptions.SupportedType || attemptNumber <= 0 || expires <= acquired
                || root.GetProperty("payload").ValueKind != JsonValueKind.Object || !IsCanonicalToken(leaseToken))
                throw new JsonException();
            return new ClaimedExecution(jobId, attemptId, leaseId, leaseToken!, attemptNumber,
                root.GetProperty("payload").Clone(), acquired, expires, started);
        });
    }

    public async Task<DateTime> RenewAsync(Guid workerId, Guid sessionId, ClaimedExecution execution, CancellationToken token)
    {
        var bytes = await SendRecoverableAsync(() => LeaseRequest(HttpMethod.Post, workerId, sessionId, execution, "renewal"), token);
        return ReadExecutionResponse(bytes!, "invalid_renewal_response", root =>
        {
            RequireFields(root, "leaseId", "expiresAtUtc");
            if (ReadId(root, "leaseId") != execution.LeaseId) throw new JsonException();
            return ReadUtc(root, "expiresAtUtc");
        });
    }

    public async Task CompleteAsync(Guid workerId, Guid sessionId, ClaimedExecution execution,
        CompletionReport report, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(report, JsonSerializerOptions.Web);
        var bytes = await SendRecoverableAsync(() =>
        {
            var request = LeaseRequest(HttpMethod.Put, workerId, sessionId, execution, "completion");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            return request;
        }, token);
        ReadExecutionResponse(bytes!, "invalid_completion_response", root =>
        {
            var dataField = report.Outcome == "succeeded" ? "result" : "error";
            RequireFields(root, "jobId", "attemptId", "leaseId", "reportId", "outcome", "finishedAtUtc", dataField);
            if (ReadId(root, "jobId") != execution.JobId || ReadId(root, "attemptId") != execution.AttemptId
                || ReadId(root, "leaseId") != execution.LeaseId || ReadId(root, "reportId") != report.ReportId
                || root.GetProperty("outcome").GetString() != report.Outcome || ReadUtc(root, "finishedAtUtc") < execution.AcquiredAtUtc)
                throw new JsonException();
            if (report.Result is { } result)
            {
                if (!JsonElement.DeepEquals(result, root.GetProperty("result"))) throw new JsonException();
            }
            else
            {
                var error = root.GetProperty("error");
                RequireFields(error, "code", "message");
                if (error.GetProperty("code").GetString() != report.Error!.Code
                    || error.GetProperty("message").GetString() != report.Error.Message) throw new JsonException();
            }
            return true;
        });
    }

    private static HttpRequestMessage SessionRequest(HttpMethod method, string path, Guid sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Worker-Session-Id", sessionId.ToString("D"));
        return request;
    }
    private static HttpRequestMessage LeaseRequest(HttpMethod method, Guid workerId, Guid sessionId, ClaimedExecution execution, string operation)
    {
        var request = SessionRequest(method, $"/api/worker/workers/{workerId:D}/leases/{execution.LeaseId:D}/{operation}", sessionId);
        request.Headers.Add("Lease-Token", execution.LeaseToken);
        return request;
    }
    private static T ReadExecutionResponse<T>(byte[] bytes, string code, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 33 });
            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new WorkerProtocolException(code);
        }
    }
    private static void RequireFields(JsonElement root, params string[] fields)
    {
        if (!root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal))) throw new JsonException();
    }
    private static Guid ReadId(JsonElement root, string field)
    {
        var id = root.GetProperty(field).GetGuid();
        if (id.Version != 7 || (id.Variant & 0b1100) != 0b1000) throw new JsonException();
        return id;
    }
    private static DateTime ReadUtc(JsonElement root, string field)
    {
        var value = root.GetProperty(field).GetDateTime();
        if (value.Kind != DateTimeKind.Utc) throw new JsonException();
        return value;
    }
    private static bool IsCanonicalToken(string? token)
    {
        if (token is not { Length: 43 } || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == token;
    }
}
