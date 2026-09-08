using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synestra.Worker.Testing;
using Xunit;

namespace Synestra.Worker.Tests;

public sealed class WorkerApiClientTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("worker")]
    [InlineData("session")]
    [InlineData("capacity")]
    [InlineData("type")]
    [InlineData("zero_interval")]
    [InlineData("offline_boundary")]
    [InlineData("timer_overflow")]
    [InlineData("timestamp")]
    [InlineData("array")]
    [InlineData("malformed")]
    public async Task InvalidRegistrationResponseCannotStartHeartbeat(string invalid)
    {
        using var handler = new WorkerHostTests.Handler(async (request, token) =>
        {
            var response = WorkerHostTests.Registration(request, await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            var original = await response.Content.ReadAsStringAsync(token);
            var json = JsonNode.Parse(original)!;
            switch (invalid)
            {
                case "missing": json.AsObject().Remove("heartbeatIntervalSeconds"); break;
                case "worker": json["workerId"] = Guid.CreateVersion7(); break;
                case "session": json["sessionId"] = Guid.CreateVersion7(); break;
                case "capacity": json["capacity"] = 2; break;
                case "type": json["supportedTypes"] = new JsonArray("unsupported"); break;
                case "zero_interval": json["heartbeatIntervalSeconds"] = 0; break;
                case "offline_boundary": json["heartbeatIntervalSeconds"] = 30; break;
                case "timer_overflow": json["heartbeatIntervalSeconds"] = 5000000; break;
                case "timestamp": json["lastSeenAtUtc"] = "2026-09-07T12:00:00"; break;
            }
            var body = invalid switch
            {
                "duplicate" => original.Replace("\"capacity\":1", "\"capacity\":1,\"capacity\":1"),
                "array" => "[]",
                "malformed" => "{",
                _ => json.ToJsonString()
            };
            response.Content.Dispose();
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new WorkerApiClient(http, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => api.RegisterAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", Token));
        Assert.Equal("invalid_registration_response", failure.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseSizeIsBoundedWithAndWithoutContentLength(bool knownLength)
    {
        using var handler = new WorkerHostTests.Handler((_, _) =>
        {
            HttpContent content = knownLength ? new ByteArrayContent(new byte[1024 * 1024 + 1]) : new UnknownLengthContent();
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new WorkerApiClient(http, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => api.RegisterAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", Token));
        Assert.Equal("response_too_large", failure.Code);
    }

    [Fact]
    public async Task DeadlineAlsoCancelsReadingAResponseBody()
    {
        var clock = new ControlledTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new WorkerHostTests.Handler((_, _) =>
        {
            var content = new StreamContent(new WaitingStream(entered));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        var api = new WorkerApiClient(http, clock);
        var claim = api.ClaimAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => claim);
        Assert.Equal(0, clock.ActiveTimers);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[1024 * 1024 + 1]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(new byte[1024 * 1024 + 1]));
    }
    private sealed class WaitingStream(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
