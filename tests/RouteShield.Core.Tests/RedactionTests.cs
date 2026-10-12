using Xunit;

namespace RouteShield.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData("{\"password\":\"sensitive value\"}", "sensitive")]
    [InlineData("{\"private_key\":\"private-material\"}", "private-material")]
    [InlineData("password='secret phrase'", "secret phrase")]
    [InlineData("Authorization: Bearer sensitive-token", "sensitive-token")]
    [InlineData("access_token=sensitive-token", "sensitive-token")]
    [InlineData("anytls://sensitive-password@relay.example:443#node", "sensitive-password")]
    [InlineData("https://name:sensitive-password@provider.example", "sensitive-password")]
    [InlineData("https://provider.example?key=sensitive-token", "sensitive-token")]
    [InlineData("https://provider.example/sub/sensitive-token", "sensitive-token")]
    [InlineData("uuid=00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000001")]
    public void Secrets_do_not_survive_diagnostics(string input, string secret)
    {
        Assert.DoesNotContain(secret, AppLog.Redact(input));
    }

    [Fact]
    public void Hosts_and_ordinary_diagnostics_survive()
    {
        Assert.Equal("https://provider.example/<redacted>", AppLog.Redact("https://provider.example/sub?token=credential"));
        Assert.Equal("Connected on adapter Wi-Fi", AppLog.Redact("Connected on adapter Wi-Fi"));
    }
}
