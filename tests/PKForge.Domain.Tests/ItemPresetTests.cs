using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ItemPresetTests
{
    [Fact]
    public void CompatibilityRejectsOtherGenerationsWrongNamesAndIllegalItems()
    {
        var preset = new ItemPreset("test", "Supplies", 3,
        [
            new("Items", 1, "Potion", 20),
            new("Items", 2, "Wrong generation name", 10),
            new("Items", 3, "Key item", 1),
            new("Items", 4, "Missing", 1),
        ]);
        string[] names = ["", "Potion", "Super Potion", "Key item"];
        Assert.Empty(preset.CompatibleItems(4, names, _ => [1, 2]));
        var compatible = Assert.Single(preset.CompatibleItems(3, names, _ => [1, 2]));
        Assert.Equal(1, compatible.ItemId);
        Assert.Equal(20, compatible.Count);
    }

    [Fact]
    public void PersonalPresetsPersistRenameAndDeleteWithoutChangingAnotherPreset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pkforge-presets-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "presets.json");
        try
        {
            var store = new ItemPresetStore(path);
            var first = new ItemPreset("a", "Healing", 3, [new("Items", 13, "Potion", 999)]);
            store.Save(first);
            store.Save(new ItemPreset("b", "Balls", 1, [new("Items", 4, "Poké Ball", 20)]));
            store = new ItemPresetStore(path);
            Assert.Equal(2, store.Read().Count);
            Assert.Equal(999, store.Read()[0].Items[0].Count);
            Assert.Throws<ArgumentException>(() => store.Save(first with { Name = "balls" }));
            store.Save(first with { Name = "Medicine" });
            Assert.Equal("Medicine", store.Read()[0].Name);
            store.Delete("a");
            Assert.Equal("b", Assert.Single(store.Read()).Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CorruptLibraryIsNotOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pkforge-presets-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "invalid data");
            var store = new ItemPresetStore(path);
            Assert.Throws<System.Text.Json.JsonException>(() => store.Save(new("a", "Supplies", 3, [])));
            Assert.Equal("invalid data", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
