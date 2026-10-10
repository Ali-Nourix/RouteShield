using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using RouteShield.Tunnels;

namespace RouteShield.Services;

public sealed record BridgeSnapshot(bool Connected, string? ActiveProfile, IReadOnlyList<BridgeBinding> Routes);

/// <summary>
/// The read-only loopback API the browser extensions discover the bridge proxies from.
///
/// It listens on 127.0.0.1 only, answers a single GET, sets no CORS headers (so a web page can
/// never read it), and refuses any request whose Host header is not the loopback address it is
/// bound to — which is what closes the DNS-rebinding route to a "local" API. Nothing it returns
/// is secret: profile names and port numbers, no credentials.
/// </summary>
public sealed class BrowserBridgeServer : IDisposable
{
    public const string Route = "/v1/state";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<BridgeSnapshot> _snapshot;
    private HttpListener? _listener;
    private CancellationTokenSource? _lifetime;

    public BrowserBridgeServer(Func<BridgeSnapshot> snapshot)
    {
        _snapshot = snapshot;
    }

    public int? Port { get; private set; }

    public string? LastError { get; private set; }

    public bool IsRunning => _listener is { IsListening: true };

    public void Start(int port)
    {
        Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            LastError = $"Port {port} could not be opened: {exception.Message}";
            AppLog.Write(LogCategory.Network, $"Browser bridge: {LastError}");
            return;
        }

        _listener = listener;
        _lifetime = new CancellationTokenSource();
        Port = port;
        LastError = null;

        _ = ServeAsync(listener, _lifetime.Token);
        AppLog.Write(LogCategory.Network, $"Browser bridge listening on 127.0.0.1:{port}");
    }

    public void Stop()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        if (_listener is not null)
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            _listener = null;
        }

        Port = null;
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Answer(context), CancellationToken.None);
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            response.Headers["Cache-Control"] = "no-store";

            var expectedHost = $"127.0.0.1:{Port}";
            if (!string.Equals(request.Headers["Host"], expectedHost, StringComparison.Ordinal))
            {
                Write(response, 421, "{\"error\":\"misdirected\"}");
                return;
            }

            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != Route)
            {
                Write(response, 404, "{\"error\":\"not found\"}");
                return;
            }

            var snapshot = _snapshot();
            var payload = new
            {
                app = "RouteShield",
                version = DiagnosticsExporter.AppVersion,
                connected = snapshot.Connected,
                active = snapshot.ActiveProfile,
                routes = snapshot.Routes.Select(route => new
                {
                    kind = route.Kind.ToString().ToLowerInvariant(),
                    id = route.ProfileId,
                    name = route.Name,
                    port = route.Port
                })
            };

            Write(response, 200, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser hung up first; nothing to answer.
        }
    }

    private static void Write(HttpListenerResponse response, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    public void Dispose() => Stop();
}
