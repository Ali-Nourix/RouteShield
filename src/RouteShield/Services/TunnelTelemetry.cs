using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RouteShield.Services;

public sealed record TunnelProbe(string ExitIp, long LatencyMs);

/// <summary>
/// Confirms the tunnel actually carries traffic by fetching the exit address through the core's
/// own loopback inbound. A direct request would answer even with the tunnel down, so the probe
/// deliberately goes through the proxy.
/// </summary>
public sealed class NetworkProbe
{
    private const string ExitAddressService = "https://api.ipify.org";

    public async Task<TunnelProbe> RunAsync(Uri proxy, CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxy),
            UseProxy = true
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RouteShield/1.1");

        var stopwatch = Stopwatch.StartNew();
        var body = (await client.GetStringAsync(ExitAddressService, cancellationToken)).Trim();
        stopwatch.Stop();

        if (!IPAddress.TryParse(body, out _))
        {
            throw new InvalidOperationException("The exit-address service returned an unexpected response.");
        }

        return new TunnelProbe(body, stopwatch.ElapsedMilliseconds);
    }
}

public sealed record TrafficSample(long UploadBytesPerSecond, long DownloadBytesPerSecond)
{
    public double TotalMegabytesPerSecond => (UploadBytesPerSecond + DownloadBytesPerSecond) / (1024d * 1024d);
}

/// <summary>
/// Reads per-second throughput from the core's Clash API. The endpoint streams one JSON object
/// per second, so the meter holds the request open instead of polling.
/// </summary>
public sealed class TrafficMeter : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _cancellation;

    public event Action<TrafficSample>? SampleReceived;

    public void Start(Uri controller, string secret)
    {
        Stop();

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        _ = Task.Run(() => ReadAsync(controller, secret, cancellation.Token), cancellation.Token);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task ReadAsync(Uri controller, string secret, CancellationToken cancellationToken)
    {
        // The core needs a moment after start before it accepts control connections.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, "traffic"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (TryReadSample(line, out var sample))
                    {
                        SampleReceived?.Invoke(sample);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static bool TryReadSample(string line, out TrafficSample sample)
    {
        sample = new TrafficSample(0, 0);

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            sample = new TrafficSample(
                root.TryGetProperty("up", out var up) ? up.GetInt64() : 0,
                root.TryGetProperty("down", out var down) ? down.GetInt64() : 0);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }
}
