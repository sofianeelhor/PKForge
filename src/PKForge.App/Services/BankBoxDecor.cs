using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>
/// The Bank's box names, wallpapers and settings, stored beside its index. One instance for the whole
/// app (sync applies another device's decor through the same objects the pages read).
/// </summary>
public static class BankBoxDecor
{
    private static readonly Lazy<BankDecor> LazyDecor = new(() => new BankDecor(Path.Combine(FileSystem.AppDataDirectory, "bank")));

    public static BankDecor Decor => LazyDecor.Value;
    public static BankBoxNames Names => Decor.Names;
    public static BankBoxWallpapers Wallpapers => Decor.Wallpapers;

    /// <summary>The box's name, or "Box 07" when it has none.</summary>
    public static string Label(int box) => Names.Get(box) ?? $"Box {box + 1:00}";
}
