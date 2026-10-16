using Xunit;

namespace PKForge.Sync.Tests;

public sealed class PairingEntryTests
{
    [Theory]
    [InlineData("pkforge.example.com K7QM-4XTP", "https://pkforge.example.com/", "K7QM4XTP")]
    [InlineData("https://host.example.com:8080 k7qm4xtp", "https://host.example.com:8080/", "K7QM4XTP")]
    [InlineData("https://host:8080 k7qm4xtp", "https://host:8080/", "K7QM4XTP")]
    [InlineData("host  K7QM 4XTP", "http://host/", "K7QM4XTP")]
    [InlineData("  192.168.1.10:8080\tk7qm-4xtp  ", "http://192.168.1.10:8080/", "K7QM4XTP")]
    [InlineData("sync.example.com/pkforge K7QM-4XTP", "https://sync.example.com/pkforge/", "K7QM4XTP")]
    [InlineData("pkforge-sync://pair?server=http%3A%2F%2F192.168.1.10%3A8080&code=ABCD2345", "http://192.168.1.10:8080/", "ABCD2345")]
    [InlineData("pkforge-sync://pair?server=https%3A%2F%2Fsync.example.com&code=abcd-2345", "https://sync.example.com/", "ABCD2345")]
    public void ServerAndCode(string text, string server, string code)
    {
        Assert.True(PairingEntry.TryParse(text, out var parsedServer, out var parsedCode, out var error), error);
        Assert.Equal(server, parsedServer!.ToString());
        Assert.Equal(code, parsedCode);
    }

    [Theory]
    [InlineData("K7QM-4XTP")]
    [InlineData("k7qm4xtp")]
    [InlineData(" K7QM 4XTP ")]
    [InlineData("k7-qm-4x-tp")]
    public void CodeOnly(string text)
    {
        Assert.True(PairingEntry.TryParse(text, out var server, out var code));
        Assert.Null(server);
        Assert.Equal("K7QM4XTP", code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("K7QM-4XT")]                         // too short
    [InlineData("K7QM-4XTPP")]                       // too long
    [InlineData("K7QM-4XT0")]                        // 0 is not in the alphabet
    [InlineData("IL1O-4XTP")]                        // nor I, L, 1, O
    [InlineData("K7QM_4XTP")]                        // only spaces and dashes are dropped
    [InlineData("sync.example.com")]                 // server without a code
    [InlineData("http://sync.example.com K7QM-4XTP")] // plain http off the home network
    [InlineData("ftp://nas K7QM-4XTP")]
    [InlineData("https://sync.example.com/?x=1 K7QM-4XTP")]
    [InlineData("one two K7QM-4XTP")]                // more than one word before the code
    [InlineData("pkforge-sync://pair?server=https%3A%2F%2Fsync.example.com&code=NOPE")]
    [InlineData("pkforge-sync://pair?server=http%3A%2F%2Fevil.example.com&code=K7QM4XTP")]
    public void Rejected(string text)
    {
        Assert.False(PairingEntry.TryParse(text, out var server, out var code, out var error));
        Assert.Null(server);
        Assert.Equal("", code);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://sync.example.com/", "sync.example.com  K7QM-4XTP")]
    [InlineData("https://sync.example.com:8443/", "sync.example.com:8443  K7QM-4XTP")]
    [InlineData("https://sync.example.com/pkforge/", "sync.example.com/pkforge  K7QM-4XTP")]
    [InlineData("http://192.168.1.10:8080/", "http://192.168.1.10:8080  K7QM-4XTP")]
    [InlineData("http://nas/", "http://nas  K7QM-4XTP")]
    [InlineData("https://nas.local/", "https://nas.local  K7QM-4XTP")]
    [InlineData("http://[fd00::5]:8080/", "http://[fd00::5]:8080  K7QM-4XTP")]
    public void TheShownLineTypesBackToTheSameServerAndCode(string server, string expectedLine)
    {
        var line = PairingEntry.Format(new Uri(server), "K7QM4XTP");
        Assert.Equal(expectedLine, line);
        Assert.True(PairingEntry.TryParse(line, out var parsedServer, out var code, out var error), error);
        Assert.Equal(server, parsedServer!.ToString());
        Assert.Equal("K7QM4XTP", code);
    }
}
