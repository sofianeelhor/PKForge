using System.Text.Json;
using PKForge.Domain;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class SyncValuesTests
{
    [Fact]
    public void AMonRoundTripsThroughItsValue()
    {
        var entry = new BankEntry(Guid.NewGuid(), 3, 17, TestData.Info(25, "Sparky") with { HeldItem = 4, Traits = new SpriteTraits(Female: true) }, TestData.T0);
        var moved = TestData.T0.AddHours(2);

        var value = SyncValues.Mon(entry, new string('a', 64), moved);
        var mon = SyncValues.ReadMon(value);

        Assert.Equal(entry, SyncValues.ToEntry(entry.Id, mon));
        Assert.Equal(new string('a', 64), mon.Blob);
        Assert.Equal(moved, mon.MovedUtc);
    }

    [Fact]
    public void KeysSplitBackIntoKindAndId()
    {
        var id = Guid.NewGuid();
        Assert.Equal((ItemKinds.Mon, id.ToString("D")), SyncValues.SplitKey(SyncValues.MonKey(id)));
        Assert.Equal(id, SyncValues.MonId(SyncValues.MonKey(id)));
        Assert.True(SyncValues.IsMon(SyncValues.MonKey(id)));
        Assert.False(SyncValues.IsMon(SyncValues.BoxesKey));
    }

    [Fact]
    public void BoxesKeepNamesAndWallpapersByIndexWithinTheCount()
    {
        var value = SyncValues.Boxes(4, new Dictionary<int, string> { [1] = "Shinies", [9] = "Gone" }, new Dictionary<int, string> { [3] = "color:2" });

        var boxes = SyncValues.ReadBoxes(value);

        Assert.Equal(4, boxes.Count);
        Assert.Equal([null, "Shinies"], boxes.Names!);
        Assert.Equal(new Dictionary<int, string> { [1] = "Shinies" }, SyncValues.FromIndexed(boxes.Names));
        Assert.Equal(new Dictionary<int, string> { [3] = "color:2" }, SyncValues.FromIndexed(boxes.Wallpapers));
    }

    [Fact]
    public void FingerprintsIgnoreEscapingAndFormattingButNotContent()
    {
        var plain = JsonDocument.Parse("""{"nickname":"Flabébé +1"}""").RootElement;
        var escaped = JsonDocument.Parse("""{ "nickname" : "Flabébé +1" }""").RootElement;
        var other = JsonDocument.Parse("""{"nickname":"Flabebe +1"}""").RootElement;

        Assert.Equal(SyncValues.Fingerprint(plain), SyncValues.Fingerprint(escaped));
        Assert.NotEqual(SyncValues.Fingerprint(plain), SyncValues.Fingerprint(other));
        Assert.Equal(SyncValues.Deleted, SyncValues.Fingerprint(null));
    }

    [Fact]
    public void SameExceptPositionComparesEverythingButWhereItSits()
    {
        var entry = new BankEntry(Guid.NewGuid(), 0, 0, TestData.Info(25, "Sparky"), TestData.T0);
        var blob = new string('b', 64);
        var here = SyncValues.Mon(entry, blob, TestData.T0);
        var moved = SyncValues.Mon(entry with { Box = 2, Slot = 9 }, blob, TestData.T0.AddMinutes(5));
        var renamed = SyncValues.Mon(entry with { Info = entry.Info with { Nickname = "Volt" } }, blob, TestData.T0);

        Assert.True(SyncValues.SameExceptPosition(here, moved));
        Assert.False(SyncValues.SameExceptPosition(here, renamed));
    }
}
