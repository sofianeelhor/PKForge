using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BatchForecastTests
{
    private static EntityDetail Mon(int level = 34, int friendship = 70, int item = 0, string nickname = "Sparky") =>
        new(0, 0, false, 25, "Pikachu", 0, nickname, level, 0, 0, item, 0, 0, 0, 0,
            [10, 20, 30, 31, 31, 5], [0, 0, 0, 0, 0, 0], false, 4, "Red",
            Types: [12], Friendship: friendship, Stats: [80, 50, 40, 50, 50, 90], CurrentHp: 12);

    [Fact]
    public void LevelAndFriendshipShowBeforeAndAfter()
    {
        var lines = BatchForecast.Of(Mon(), ["Level=100", "Friendship=255"]);
        Assert.Equal(new BatchFieldChange("Level", "Lv. 34", "Lv. 100"), lines[0]);
        Assert.Equal(new BatchFieldChange("Friendship", "70", "255"), lines[1]);
        Assert.All(lines, l => Assert.True(l.Changes));
    }

    [Fact]
    public void SixIvLinesFoldIntoOneSpread()
    {
        var lines = BatchForecast.Of(Mon(), ["IV_HP=31", "IV_ATK=31", "IV_DEF=31", "IV_SPA=31", "IV_SPD=31", "IV_SPE=31"]);
        var iv = Assert.Single(lines);
        Assert.Equal("10/20/30/31/31/5", iv.Before);
        Assert.Equal("31/31/31/31/31/31", iv.After);
    }

    [Fact]
    public void AMonThatAlreadyMatchesDoesNotChange()
    {
        Assert.False(BatchForecast.Changes(Mon(level: 100), ["Level=100"]));
        Assert.False(BatchForecast.Changes(Mon(nickname: "Pikachu"), ["IsNicknamed=false"]));
        Assert.True(BatchForecast.Changes(Mon(), ["IsNicknamed=false"]));
    }

    [Fact]
    public void HealAndItemsAndUnknownLines()
    {
        var lines = BatchForecast.Of(Mon(item: 234), ["Heal", "HeldItem=0", "Shiny=Yes"], id => id == 234 ? "Leftovers" : "");
        Assert.Equal(new BatchFieldChange("HP", "12/80", "80/80"), lines[0]);
        Assert.Equal(new BatchFieldChange("Held item", "Leftovers", "none"), lines[1]);
        Assert.False(lines[2].Known);
        Assert.True(lines[2].Changes);
    }

    [Fact]
    public void MonPresetsPersistRenameAndDelete()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pkforge-monpresets-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "mon-presets.json");
        try
        {
            var store = new MonPresetStore(path);
            store.Save(new MonPreset("a", "Speed 0", ["IV_SPE=0", " "]));
            store.Save(new MonPreset("b", "Lv 5", ["Level=5"]));
            store = new MonPresetStore(path);
            Assert.Equal(["IV_SPE=0"], store.Read()[0].Lines);
            Assert.Throws<ArgumentException>(() => store.Save(new MonPreset("a", "lv 5", ["Level=5"])));
            Assert.Throws<ArgumentException>(() => store.Save(new MonPreset("c", "Empty", [])));
            store.Save(store.Read()[0] with { Name = "Trick Room" });
            Assert.Equal("Trick Room", store.Read()[0].Name);
            store.Delete("a");
            Assert.Equal("b", Assert.Single(store.Read()).Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
