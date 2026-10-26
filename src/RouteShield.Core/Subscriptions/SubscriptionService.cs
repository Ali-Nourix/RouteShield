using System.Net;
using System.Text;

namespace RouteShield.Subscriptions;

/// <summary>
/// Downloads a subscription over HTTPS and hands the body to <see cref="SubscriptionParser"/>.
///
/// The address a subscription lives at is often blocked by the same network the tunnel exists
/// to get around, so when the tunnel is up the request goes through it. The user agent names
/// sing-box because providers serve a different document per client, and the sing-box one is
/// the document this app can actually read.
/// </summary>
public sealed class SubscriptionService : IDisposable
{
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(35);

    private readonly Func<Uri?, HttpMessageHandler>? _handlerFactory;
    private readonly TimeSpan _fetchTimeout = FetchTimeout;

    public SubscriptionService() { }

    internal SubscriptionService(Func<Uri?, HttpMessageHandler> handlerFactory, TimeSpan? fetchTimeout = null)
    {
        _handlerFactory = handlerFactory;
        _fetchTimeout = fetchTimeout ?? FetchTimeout;
    }

    private const int MaxBytes = 5 * 1024 * 1024;
    private const string UserAgent = "RouteShield/1.7.2 (sing-box)";

    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly object _gate = new();

    /// <param name="proxy">The tunnel's loopback proxy, when one is up; null to go out directly.</param>
    public async Task<SubscriptionFetch> FetchAsync(
        string url, Uri? proxy = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("A subscription URL must be an absolute https:// address.", nameof(url));
        }

        // HttpClient.Timeout stops covering the request after headers with ResponseHeadersRead.
        // One linked deadline must include decompression and reading the entire capped body.
        var requestedCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_fetchTimeout);
        cancellationToken = deadline.Token;

        try
        {
            var client = ClientFor(proxy);
            using var response = await SendHttpsAsync(client, uri, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                throw new InvalidOperationException("The subscription response is larger than 5 MB.");
            }

            var body = await ReadCappedAsync(response, cancellationToken);
            var usage = response.Headers.TryGetValues(SubscriptionUsage.HeaderName, out var values)
                ? SubscriptionUsage.Parse(values.FirstOrDefault())
                : null;

            return new SubscriptionFetch(SubscriptionParser.Parse(body), usage);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !requestedCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The subscription download timed out before its complete body was received.");
        }
    }

    /// <summary>One client per route, kept for the session: a new handler per refresh would leak sockets.</summary>
    private HttpClient ClientFor(Uri? proxy)
    {
        var key = proxy?.ToString() ?? string.Empty;

        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var handler = _handlerFactory?.Invoke(proxy) ?? new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = proxy is not null,
                Proxy = proxy is null ? null : new WebProxy(proxy)
            };

            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/plain, application/json;q=0.9, */*;q=0.5");

            _clients[key] = client;
            return client;
        }
    }

    private static async Task<HttpResponseMessage> SendHttpsAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        // Resolve redirects ourselves so every hop is HTTPS, with a bounded redirect chain.
        for (var redirects = 0; ; redirects++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 5 || location is null)
            {
                throw new HttpRequestException("The subscription redirect chain is invalid or too long.");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            {
                throw new HttpRequestException("A subscription redirect must use HTTPS without embedded credentials.");
            }
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffered = new MemoryStream();

        var chunk = new byte[32 * 1024];
        int read;

        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffered.Write(chunk, 0, read);
            if (buffered.Length > MaxBytes)
            {
                throw new InvalidOperationException("The subscription response is larger than 5 MB.");
            }
        }

        return Encoding.UTF8.GetString(buffered.ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}
