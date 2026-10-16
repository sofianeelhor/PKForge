using Xunit;

namespace PKForge.Sync.Tests;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("https://sync.example.com", "https://sync.example.com/")]
    [InlineData("https://sync.example.com/pkforge/", "https://sync.example.com/pkforge/")]
    [InlineData("sync.example.com", "https://sync.example.com/")]
    [InlineData("192.168.1.10:8080", "http://192.168.1.10:8080/")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080/")]
    [InlineData("http://nas:8080", "http://nas:8080/")]
    [InlineData("http://nas.local", "http://nas.local/")]
    [InlineData("http://100.101.102.103", "http://100.101.102.103/")]
    [InlineData(" http://localhost:5000 ", "http://localhost:5000/")]
    public void AcceptedAddresses(string input, string expected)
    {
        Assert.True(ServerAddress.TryParse(input, out var address, out var error), error);
        Assert.Equal(expected, address!.ToString());
    }

    [Theory]
    [InlineData("http://sync.example.com")]
    [InlineData("http://8.8.8.8")]
    [InlineData("ftp://nas")]
    [InlineData("")]
    [InlineData("https://sync.example.com/?x=1")]
    public void RefusedAddresses(string input)
    {
        Assert.False(ServerAddress.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PairingLinksCarryTheServerAndCode()
    {
        Assert.True(PairingLink.TryParse("pkforge-sync://pair?server=http%3A%2F%2F192.168.1.10%3A8080&code=ABCD2345", out var server, out var code));
        Assert.Equal("http://192.168.1.10:8080/", server!.ToString());
        Assert.Equal("ABCD2345", code);
        Assert.False(PairingLink.TryParse("https://example.com/pair?code=X", out _, out _));
        Assert.False(PairingLink.TryParse("pkforge-sync://pair?server=http%3A%2F%2Fevil.example.com&code=X", out _, out _));
    }
}
