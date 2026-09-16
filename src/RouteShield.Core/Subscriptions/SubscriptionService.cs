using System.Text;

namespace RouteShield.Subscriptions;

/// <summary>Downloads a subscription over HTTPS and hands the body to <see cref="SubscriptionParser"/>.</summary>
public sealed class SubscriptionService : IDisposable
{
    private const int MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(35) };

    public SubscriptionService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RouteShield/1.1");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/plain, application/json;q=0.9, */*;q=0.5");
    }

    public async Task<IReadOnlyList<SubscriptionEntry>> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A subscription URL must be an absolute https:// address.", nameof(url));
        }

        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            throw new InvalidOperationException("The subscription response is larger than 5 MB.");
        }

        var body = await ReadCappedAsync(response, cancellationToken);
        return SubscriptionParser.Parse(body);
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

    public void Dispose() => _client.Dispose();
}
