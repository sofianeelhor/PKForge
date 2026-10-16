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

    /// <summary>Raised after this device changed a wallpaper (not when another device's wallpapers are applied).</summary>
    public event EventHandler? Changed;

    /// <summary>The wallpaper chosen for <paramref name="box"/>, or null for its default.</summary>
    public BankWallpaper? Get(int box) => BankWallpaper.Parse(_file.Get(box));

    public void Set(int box, BankWallpaper? wallpaper)
    {
        _file.SetMany([(box, wallpaper?.Id)]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wallpapers follow their boxes through <see cref="IBankService.RemapBoxes"/>.</summary>
    public void Remap(BankBoxRemap remap)
    {
        _file.Remap(remap);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the file again, after it was put back from a copy.</summary>
    public void Reload() => _file.Reload();

    /// <summary>Every chosen wallpaper's stored id, by box.</summary>
    public IReadOnlyDictionary<int, string> AllIds() => _file.All();

    /// <summary>Replaces every wallpaper choice in one write.</summary>
    public void ReplaceAllIds(IReadOnlyDictionary<int, string> ids) => _file.ReplaceAll(ids);
}
