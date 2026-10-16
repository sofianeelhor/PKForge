using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Everything about the Bank besides its Pokémon (box names, wallpapers, settings), kept in its folder.</summary>
public sealed class BankDecor(string bankRootDirectory)
{
    public BankBoxNames Names { get; } = new(bankRootDirectory);
    public BankBoxWallpapers Wallpapers { get; } = new(bankRootDirectory);
    public BankSettings Settings { get; } = new(bankRootDirectory);

    /// <summary>Reads every file again, after the Bank folder was put back from a copy.</summary>
    public void Reload()
    {
        Names.Reload();
        Wallpapers.Reload();
        Settings.Reload();
    }

    /// <summary>Moves names, wallpapers and the living dex region with their boxes.</summary>
    public void Remap(BankBoxRemap remap)
    {
        Names.Remap(remap);
        Wallpapers.Remap(remap);
        Settings.Remap(remap);
    }
}
