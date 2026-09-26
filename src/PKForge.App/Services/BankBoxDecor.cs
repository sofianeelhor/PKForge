using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>The Bank's box names and wallpapers, stored beside its index.</summary>
public static class BankBoxDecor
{
    private static readonly Lazy<BankBoxNames> LazyNames = new(() => new BankBoxNames(Root));
    private static readonly Lazy<BankBoxWallpapers> LazyWallpapers = new(() => new BankBoxWallpapers(Root));

    private static string Root => Path.Combine(FileSystem.AppDataDirectory, "bank");

    public static BankBoxNames Names => LazyNames.Value;

    public static BankBoxWallpapers Wallpapers => LazyWallpapers.Value;

    /// <summary>The box's name, or "Box 07" when it has none.</summary>
    public static string Label(int box) => Names.Get(box) ?? $"Box {box + 1:00}";
}
