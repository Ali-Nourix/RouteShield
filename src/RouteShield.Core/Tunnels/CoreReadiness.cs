using System.Net.Http.Headers;
using System.Text.Json;

namespace RouteShield.Tunnels;

/// <summary>Waits for the authenticated control API, rather than sleeping for a fixed startup delay.</summary>
public static class CoreReadiness
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 4096
    };

    public static async Task WaitAsync(Uri controlUri, string secret, Func<bool> exited,
        TimeSpan timeout, CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        if (controlUri.Scheme != Uri.UriSchemeHttp || controlUri.Host != "127.0.0.1" || controlUri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Core readiness must use the loopback control API.", nameof(controlUri));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (exited())
                {
                    throw new InvalidOperationException("The core exited before its control API became ready.");
                }

                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controlUri, "version"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    using var response = await (client ?? Client).SendAsync(request, attempt.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token));
                        if (json.RootElement.TryGetProperty("version", out var version)
                            && version.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(version.GetString())
                            && !exited())
                        {
                            return;
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException
                    || exception is OperationCanceledException && !deadline.IsCancellationRequested)
                {
                    // Startup races are normal; never log the control secret or request contents.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The core control API did not become ready within its startup deadline.");
        }
    }
}
