using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class SpritePackTests
{
    [Fact]
    public void EveryEntryIsUniqueAndPassesTheArchiveNameCheck()
    {
        var entries = SpritePack.Entries(["Master Ball", "Poké Doll", "Master Ball", "???", ""]);
        Assert.Equal(entries.Count, entries.Select(e => e.CachePath).Distinct().Count());
        Assert.All(entries, e => Assert.True(SpritePack.IsSafeEntryName(e.CachePath), e.CachePath));
        Assert.All(entries, e => Assert.StartsWith(SpritePack.RemoteRoot, e.Url, StringComparison.Ordinal));
        Assert.Contains(entries, e => e.CachePath == "items/master-ball.png");
        Assert.Contains(entries, e => e.CachePath == "items/poke-doll.png");
        Assert.Contains(entries, e => e.CachePath.StartsWith("home/", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.CachePath.StartsWith("showdown/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.CachePath == "items/.png");
    }

    [Theory]
    [InlineData("home/6-s.png", true)]
    [InlineData("showdown/10035-f-s.gif", true)]
    [InlineData("items/poke-ball.png", true)]
    [InlineData("../home/6.png", false)]
    [InlineData("home/../../x.png", false)]
    [InlineData("/home/6.png", false)]
    [InlineData("home/sub/6.png", false)]
    [InlineData("other/6.png", false)]
    [InlineData("home/.hidden.png", false)]
    [InlineData("home/6.exe", false)]
    [InlineData("home\\6.png", false)]
    [InlineData("home/", false)]
    [InlineData("", false)]
    public void TheArchiveNameCheckOnlyAcceptsPlainCacheFiles(string name, bool safe) =>
        Assert.Equal(safe, SpritePack.IsSafeEntryName(name));
}
