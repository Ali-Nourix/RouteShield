using System.Net;
using RouteShield.Subscriptions;
using Xunit;

namespace RouteShield.Tests;

public class SubscriptionFetchTests
{
    [Theory]
    [InlineData("http://provider.example/sub")]
    [InlineData("https://user:pass@provider.example/sub")]
    public async Task Unsafe_initial_urls_are_rejected_before_network_access(string url)
    {
        using var service = new SubscriptionService(_ => throw new InvalidOperationException("Network must not run"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FetchAsync(url));
    }

    [Fact]
    public async Task An_http_redirect_is_never_followed()
    {
        var handler = new StubHandler(_ => Redirect("http://provider.example/unsafe"));
        using var service = new SubscriptionService(_ => handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync("https://provider.example/sub"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Relative_https_redirects_work()
    {
        var handler = new StubHandler(uri => uri.AbsolutePath == "/sub" ? Redirect("/nodes") : Nodes());
        using var service = new SubscriptionService(_ => handler);
        var result = await service.FetchAsync("https://provider.example/sub");
        Assert.Single(result.Entries);
        Assert.Equal("https://provider.example/nodes", handler.Requests.Last().AbsoluteUri);
    }

    [Fact]
    public async Task Redirect_loops_are_bounded()
    {
        var handler = new StubHandler(_ => Redirect("/loop"));
        using var service = new SubscriptionService(_ => handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync("https://provider.example/sub"));
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Oversized_bodies_without_a_length_header_are_rejected()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(new byte[5 * 1024 * 1024 + 1]))
        });
        using var service = new SubscriptionService(_ => handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FetchAsync("https://provider.example/sub"));
    }

    [Fact]
    public async Task The_deadline_includes_body_reads_after_headers()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        });
        using var service = new SubscriptionService(_ => handler, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.FetchAsync("https://provider.example/sub").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        });
        using var service = new SubscriptionService(_ => handler);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FetchAsync("https://provider.example/sub", cancellationToken: cancelled.Token));
    }

    private static HttpResponseMessage Nodes() => new(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Trojan) };

    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
