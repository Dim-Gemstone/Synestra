using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Synestra.Worker;

internal sealed partial class WorkerApiClient(HttpClient http, TimeProvider timeProvider)
{
    private const int MaximumResponseBytes = 1024 * 1024;

    public async Task<TimeSpan> RegisterAsync(Guid workerId, Guid sessionId, string name, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/worker/workers/{workerId:D}/registration")
        {
            Content = JsonContent.Create(new { sessionId, name, capacity = 1, supportedTypes = new[] { WorkerOptions.SupportedType } })
        };
        var bytes = await SendAsync(request, HttpStatusCode.OK, token);
        try
        {
            using var document = JsonDocument.Parse(bytes!, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var fields = root.EnumerateObject().Select(property => property.Name).ToArray();
            string[] expected = ["workerId", "sessionId", "name", "capacity", "supportedTypes", "registeredAtUtc",
                "sessionStartedAtUtc", "lastSeenAtUtc", "heartbeatIntervalSeconds", "offlineAfterSeconds"];
            if (!fields.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))
                || root.GetProperty("workerId").GetGuid() != workerId || root.GetProperty("sessionId").GetGuid() != sessionId
                || root.GetProperty("name").GetString() != name || root.GetProperty("capacity").GetInt32() != 1)
                throw new WorkerProtocolException("invalid_registration_response");
            var types = root.GetProperty("supportedTypes");
            if (types.GetArrayLength() != 1 || types[0].GetString() != WorkerOptions.SupportedType)
                throw new WorkerProtocolException("invalid_registration_response");
            foreach (var field in new[] { "registeredAtUtc", "sessionStartedAtUtc", "lastSeenAtUtc" })
                if (root.GetProperty(field).GetDateTime().Kind != DateTimeKind.Utc)
                    throw new WorkerProtocolException("invalid_registration_response");
            var interval = root.GetProperty("heartbeatIntervalSeconds").GetInt32();
            var offline = root.GetProperty("offlineAfterSeconds").GetInt32();
            if (interval <= 0 || interval > (uint.MaxValue - 1) / 1000 || offline <= interval)
                throw new WorkerProtocolException("invalid_registration_response");
            return TimeSpan.FromSeconds(interval);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new WorkerProtocolException("invalid_registration_response");
        }
    }

    public async Task HeartbeatAsync(Guid workerId, Guid sessionId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/worker/workers/{workerId:D}/heartbeat");
        request.Headers.Add("Worker-Session-Id", sessionId.ToString("D"));
        await SendAsync(request, HttpStatusCode.NoContent, token);
    }

    private async Task<byte[]?> SendAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken token,
        bool allowNoContent = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await ReadResponseAsync(request, expected, linked.Token, allowNoContent); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("Worker API request timed out.");
        }
    }

    private async Task<byte[]?> ReadResponseAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken token,
        bool allowNoContent)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NoContent && (expected == HttpStatusCode.NoContent || allowNoContent)) return null;
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media != (response.StatusCode == expected ? "application/json" : "application/problem+json"))
            throw new WorkerProtocolException("unexpected_response");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new WorkerProtocolException("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new WorkerProtocolException("response_too_large");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (response.StatusCode == expected) return bytes;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var code = document.RootElement.GetProperty("code").GetString();
            // Only known protocol codes may reach diagnostics; response text is untrusted.
            if ((response.StatusCode, code) is (HttpStatusCode.Conflict, "worker_session_replaced")
                or (HttpStatusCode.NotFound, "worker_not_found") or (HttpStatusCode.BadRequest, "invalid_request")
                or (HttpStatusCode.InternalServerError, "internal_error")
                or (HttpStatusCode.NotFound, "lease_not_found")
                or (HttpStatusCode.Conflict, "worker_offline" or "lease_expired" or "lease_ownership_lost"
                    or "lease_not_active" or "attempt_already_finalized" or "completion_report_conflict"))
                throw new WorkerProtocolException(code!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { }
        throw new WorkerProtocolException("unexpected_response");
    }
}

internal sealed class WorkerProtocolException(string code) : Exception("Worker API protocol failure.")
{
    public string Code { get; } = code;
}
