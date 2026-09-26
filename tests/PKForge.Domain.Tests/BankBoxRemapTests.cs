using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankBoxRemapTests
{
    [Fact]
    public void SwapTradesTwoBoxes()
    {
        var swap = BankBoxRemap.Swap(4, 1, 3);
        Assert.Equal([0, 3, 2, 1], swap.NewIndexOf);
        Assert.Equal(4, swap.NewCount);
        Assert.True(BankBoxRemap.Swap(4, 2, 2).IsIdentity);
    }

    [Fact]
    public void MoveSlidesTheBoxesInBetween()
    {
        Assert.Equal([3, 0, 1, 2, 4], BankBoxRemap.Move(5, 0, 3).NewIndexOf);
        Assert.Equal([1, 2, 3, 0, 4], BankBoxRemap.Move(5, 3, 0).NewIndexOf);
        Assert.True(BankBoxRemap.Move(5, 2, 2).IsIdentity);
    }

    [Fact]
    public void InsertAndRemoveShiftTheRest()
    {
        var insert = BankBoxRemap.Insert(3, 1);
        Assert.Equal([0, 2, 3], insert.NewIndexOf);
        Assert.Equal(4, insert.NewCount);
        Assert.Equal([0, 1, 2], BankBoxRemap.Insert(3, 3).NewIndexOf);

        var remove = BankBoxRemap.Remove(4, 1);
        Assert.Equal([0, -1, 1, 2], remove.NewIndexOf);
        Assert.Equal(3, remove.NewCount);
        Assert.Null(remove.Map(1));
        Assert.Equal(1, remove.Map(2));
        Assert.Null(remove.Map(9));
        Assert.Throws<InvalidOperationException>(() => BankBoxRemap.Remove(1, 0));
    }

    [Fact]
    public void EveryBuiltRemapIsAValidPermutation()
    {
        for (var count = 1; count <= 6; count++)
        for (var a = 0; a < count; a++)
        {
            BankBoxRemap.Insert(count, a).Validate(count);
            if (count > 1) BankBoxRemap.Remove(count, a).Validate(count);
            for (var b = 0; b < count; b++)
            {
                BankBoxRemap.Swap(count, a, b).Validate(count);
                BankBoxRemap.Move(count, a, b).Validate(count);
            }
        }
    }

    [Theory]
    [InlineData("color:3", "color:3")]
    [InlineData("art:box_wp01xy", "art:box_wp01xy")]
    [InlineData("art:../evil", null)]
    [InlineData("art:", null)]
    [InlineData("color:-1", null)]
    [InlineData("color:x", null)]
    [InlineData("paint:1", null)]
    [InlineData(null, null)]
    public void WallpaperIdsRoundTripAndRejectJunk(string? stored, string? expected) =>
        Assert.Equal(expected, BankWallpaper.Parse(stored)?.Id);

    [Fact]
    public void EveryCatalogWallpaperIsABundledAsset()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PKForge.sln"))) root = Path.GetDirectoryName(root)!;
        var bundled = Directory.EnumerateFiles(Path.Combine(root, "external/PKHeX/PKHeX.Drawing.Misc/Resources/img/box"), "*.png", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var listed = BankWallpaper.ArtCatalog.SelectMany(g => g.Assets).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.All(listed, asset => Assert.Contains(asset, bundled));
        Assert.All(listed, asset => Assert.Equal("art:" + asset, BankWallpaper.Art(asset).Id));
        Assert.Equal(bundled.Count, listed.Count);
    }
}
