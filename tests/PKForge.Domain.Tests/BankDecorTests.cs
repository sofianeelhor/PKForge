using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankDecorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NamesAndWallpapersAreReadAndReplacedWhole()
    {
        var decor = new BankDecor(_root);
        decor.Names.SetMany([(0, "Starters"), (2, "Legends")]);
        decor.Wallpapers.Set(1, BankWallpaper.Color(3));

        Assert.Equal(new Dictionary<int, string> { [0] = "Starters", [2] = "Legends" }, decor.Names.All());
        Assert.Equal(new Dictionary<int, string> { [1] = "color:3" }, decor.Wallpapers.AllIds());

        decor.Names.ReplaceAll(new Dictionary<int, string> { [1] = " Shinies ", [4] = "  " });
        decor.Wallpapers.ReplaceAllIds(new Dictionary<int, string> { [0] = "color:5" });

        var reopened = new BankDecor(_root);
        Assert.Equal(new Dictionary<int, string> { [1] = "Shinies" }, reopened.Names.All());
        Assert.Equal(BankWallpaper.Color(5), reopened.Wallpapers.Get(0));
        Assert.Null(reopened.Wallpapers.Get(1));
    }

    [Fact]
    public void TheLivingDexStartBoxPersistsAndFollowsItsBox()
    {
        var decor = new BankDecor(_root);
        decor.Settings.LivingDexStartBox = 2;

        decor.Remap(BankBoxRemap.Insert(5, 0));
        Assert.Equal(3, new BankDecor(_root).Settings.LivingDexStartBox);

        decor.Remap(BankBoxRemap.Remove(6, 3));
        Assert.Null(new BankDecor(_root).Settings.LivingDexStartBox);
    }

    [Fact]
    public void SettingsRoundTripAsJsonKeepingKeysThisVersionDoesNotKnow()
    {
        var decor = new BankDecor(_root);
        decor.Settings.Replace(JsonSerializer.SerializeToElement(new { livingDexStartBox = 4, futureSetting = "kept" }));

        decor.Settings.LivingDexStartBox = 7;

        var json = new BankDecor(_root).Settings.ToJson();
        Assert.Equal(7, json.GetProperty("livingDexStartBox").GetInt32());
        Assert.Equal("kept", json.GetProperty("futureSetting").GetString());
    }

    [Fact]
    public void AnUnreadableSettingsFileReadsAsEmpty()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bank-settings.json"), "{not json");

        var settings = new BankSettings(_root);

        Assert.Null(settings.LivingDexStartBox);
        Assert.Equal(JsonValueKind.Object, settings.ToJson().ValueKind);
    }
}
