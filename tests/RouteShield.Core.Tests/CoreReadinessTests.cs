using System.Net;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class CoreReadinessTests
{
    [Fact]
    public async Task Ready_core_needs_one_authenticated_request_and_no_fixed_sleep()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-only", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"1.14.0\"}") });
        }));
        await CoreReadiness.WaitAsync(new Uri("http://127.0.0.1:40000/"), "test-only", () => false, TimeSpan.FromSeconds(1), client: client);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Process_exit_is_reported_without_waiting_for_a_deadline()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoreReadiness.WaitAsync(new Uri("http://127.0.0.1:40000/"), "test-only", () => true, TimeSpan.FromSeconds(1)));
    }
    [Fact]
    public async Task Unresponsive_api_has_a_deadline()
    {
        using var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); }));
        await Assert.ThrowsAsync<TimeoutException>(() => CoreReadiness.WaitAsync(new Uri("http://127.0.0.1:40000/"), "test-only", () => false, TimeSpan.FromMilliseconds(100), client: client));
    }
    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoreReadiness.WaitAsync(new Uri("http://127.0.0.1:40000/"), "test-only", () => false, TimeSpan.FromSeconds(1), cancellation.Token));
    }
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://127.0.0.1/")]
    public async Task The_token_is_never_sent_to_a_remote_address(string url) =>
        await Assert.ThrowsAsync<ArgumentException>(() => CoreReadiness.WaitAsync(new Uri(url), "test-only", () => false, TimeSpan.FromSeconds(1)));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => answer(request, token);
    }
}
