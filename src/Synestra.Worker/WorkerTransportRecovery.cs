using System.Net;

namespace Synestra.Worker;

internal sealed partial class WorkerApiClient
{
    // Only registration, renewal and frozen completion opt in to transport recovery.
    private async Task<byte[]?> SendRecoverableAsync(Func<HttpRequestMessage> createRequest, CancellationToken token)
    {
        var started = timeProvider.GetTimestamp();
        var duration = TimeSpan.FromSeconds(15);
        using var deadline = new CancellationTokenSource(duration, timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                bounded.Token.ThrowIfCancellationRequested();
                if (timeProvider.GetElapsedTime(started) >= duration) throw Exhausted();
                try
                {
                    using var request = createRequest();
                    var response = await SendAsync(request, HttpStatusCode.OK, bounded.Token);
                    bounded.Token.ThrowIfCancellationRequested();
                    if (timeProvider.GetElapsedTime(started) >= duration) throw Exhausted();
                    return response;
                }
                catch (Exception exception) when (attempt < 3 && !bounded.IsCancellationRequested && IsRecoverable(exception))
                {
                    if (duration - timeProvider.GetElapsedTime(started) <= TimeSpan.FromSeconds(1)) throw Exhausted();
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, bounded.Token);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw Exhausted();
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is HttpRequestException or IOException or TimeoutException
        or WorkerProtocolException { Code: "internal_error" };

    private static TimeoutException Exhausted() => new("Worker API recovery budget exhausted.");
}
