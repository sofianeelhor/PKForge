using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// The wallpaper chosen for each bank box, kept beside the bank index as
/// <c>box-wallpapers.json</c> (box index → <see cref="BankWallpaper"/> id). Boxes without
/// one keep the palette color of their position. Decoration, never data.
/// </summary>
public sealed class BankBoxWallpapers(string bankRootDirectory)
{
    private readonly BoxKeyedFile _file = new(bankRootDirectory, "box-wallpapers.json");

    /// <summary>The wallpaper chosen for <paramref name="box"/>, or null for its default.</summary>
    public BankWallpaper? Get(int box) => BankWallpaper.Parse(_file.Get(box));

    public void Set(int box, BankWallpaper? wallpaper) => _file.SetMany([(box, wallpaper?.Id)]);

    /// <summary>Wallpapers follow their boxes through <see cref="IBankService.RemapBoxes"/>.</summary>
    public void Remap(BankBoxRemap remap) => _file.Remap(remap);
}
