using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RouteShield.Tunnels;

namespace RouteShield.Services;

public sealed record LatencyResult(Guid ProfileId, int? DelayMilliseconds, string? Failure);

/// <summary>
/// Times a request through every profile at once. A throwaway core carries each profile as
/// its own node, and the Clash API's delay test does the measuring — the same test every
/// sing-box front end shows next to a node.
/// </summary>
public sealed class LatencyTester
{
    private const int Parallelism = 6;
    private static readonly TimeSpan PerProfileTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ApiReadyTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task RunAsync(
        IReadOnlyList<(Guid Id, ParsedTunnel Tunnel)> candidates,
        Action<LatencyResult> report,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var controlPort = RuntimeConfigBuilder.ReserveLoopbackPorts(1)[0];
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var controller = new Uri($"http://127.0.0.1:{controlPort}/");

        AppPaths.Ensure();
        await File.WriteAllTextAsync(
            AppPaths.ProbeConfig,
            LatencyProbeConfigBuilder.Build(candidates, controlPort, secret),
            new UTF8Encoding(false),
            cancellationToken);

        await using var core = new CoreProcessService("latency probe");
        await core.StartAsync(AppPaths.ProbeConfig);
        await WaitForApiAsync(controller, secret, cancellationToken);

        AppLog.Write(LogCategory.Network, $"Latency test started for {candidates.Count} profile(s)");

        using var slots = new SemaphoreSlim(Parallelism);
        var tests = candidates.Select(async candidate =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                report(await MeasureAsync(controller, secret, candidate.Id, cancellationToken));
            }
            finally
            {
                slots.Release();
            }
        });

        await Task.WhenAll(tests);
        AppLog.Write(LogCategory.Network, "Latency test finished");
    }

    private async Task WaitForApiAsync(Uri controller, string secret, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + ApiReadyTimeout;

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                using var request = Authorized(HttpMethod.Get, new Uri(controller, "version"), secret);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));

                using var response = await _client.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new TimeoutException("The latency probe did not come up in time.");
    }

    private async Task<LatencyResult> MeasureAsync(Uri controller, string secret, Guid profileId, CancellationToken cancellationToken)
    {
        var tag = LatencyProbeConfigBuilder.TagFor(profileId);
        var query = $"proxies/{tag}/delay?url={Uri.EscapeDataString(LatencyProbeConfigBuilder.TestUrl)}&timeout={(int)PerProfileTimeout.TotalMilliseconds}";

        try
        {
            using var request = Authorized(HttpMethod.Get, new Uri(controller, query), secret);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PerProfileTimeout + TimeSpan.FromSeconds(2));

            using var response = await _client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (response.IsSuccessStatusCode && root.TryGetProperty("delay", out var delay))
            {
                return new LatencyResult(profileId, delay.GetInt32(), null);
            }

            var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
            return new LatencyResult(profileId, null, Describe(message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LatencyResult(profileId, null, "Timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return new LatencyResult(profileId, null, "Unreachable");
        }
    }

    private static string Describe(string? message) => message switch
    {
        null or "" => "Failed",
        "Timeout" => "Timeout",
        _ => "Unreachable"
    };

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string secret)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }
}
