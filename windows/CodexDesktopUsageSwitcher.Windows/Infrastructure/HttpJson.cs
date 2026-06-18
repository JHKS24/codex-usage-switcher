using System.Net.Http;
using System.Text;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal readonly record struct HttpJsonRequest(
    HttpMethod Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? JsonBody,
    TimeSpan Timeout);

// TransportOk=false means the request never produced an HTTP response (DNS, TLS, offline, or our
// own per-request timeout) — callers treat it as a transient "network" condition. Otherwise
// StatusCode + Body are the server's response. SendAsync never throws except for caller
// cancellation, so a single failed call can't take down a whole snapshot.
internal readonly record struct HttpJsonResponse(bool TransportOk, int StatusCode, string Body, string? TransportError)
{
    public bool IsSuccess => TransportOk && StatusCode is >= 200 and < 300;
}

internal interface IHttpJsonClient
{
    Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken);
}

// One shared HttpClient (the guidance is to never create one per call) with per-request timeouts
// enforced via a linked CTS, so different endpoints can keep their own budgets. Token-bearing
// headers pass through TryAddWithoutValidation but their values are never logged here.
internal sealed class HttpJsonClient : IHttpJsonClient
{
    private static readonly HttpClient Shared = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        using var message = new HttpRequestMessage(request.Method, request.Url);
        foreach (var header in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.JsonBody is not null)
        {
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await Shared.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return new HttpJsonResponse(true, (int)response.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        {
            // Distinguish caller cancellation (propagate) from our own timeout / a transport error
            // (degrade to a network failure the caller can render and retry later).
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpJsonResponse(false, 0, "", PathRedaction.Scrub(ex.Message));
        }
    }
}
