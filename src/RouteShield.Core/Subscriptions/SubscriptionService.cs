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
    private const int MaxBytes = 5 * 1024 * 1024;
    private const string UserAgent = "RouteShield/1.7.0 (sing-box)";

    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly object _gate = new();

    /// <param name="proxy">The tunnel's loopback proxy, when one is up; null to go out directly.</param>
    public async Task<SubscriptionFetch> FetchAsync(
        string url, Uri? proxy = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A subscription URL must be an absolute https:// address.", nameof(url));
        }

        var client = ClientFor(proxy);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = proxy is not null,
                Proxy = proxy is null ? null : new WebProxy(proxy)
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/plain, application/json;q=0.9, */*;q=0.5");

            _clients[key] = client;
            return client;
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
