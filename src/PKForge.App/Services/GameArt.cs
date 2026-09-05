using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Bundled SteamGridDB art (fetched at build time), keyed by PKHeX game-name slug.</summary>
public static class GameArt
{
    // Bumped whenever bundled art changes so on-device caches never serve stale images.
    private const string AssetVersion = "v6";

    public static Task<string?> GetIconAsync(string gameLabel) => GetAsync("gameart", gameLabel);
    public static Task<string?> GetHeroAsync(string gameLabel) => GetAsync("gamehero", gameLabel);
    public static Task<string?> GetLogoAsync(string gameLabel) => GetAsync("gamelogo", gameLabel);

    private static async Task<string?> GetAsync(string folder, string gameLabel)
    {
        const string prefix = "Pokémon ";
        var name = gameLabel.StartsWith(prefix, StringComparison.Ordinal) ? gameLabel[prefix.Length..] : gameLabel;
        var slug = GetAssetSlug(name);
        var cache = Path.Combine(FileSystem.CacheDirectory, $"{folder}-{AssetVersion}-{slug}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            await using var asset = await FileSystem.OpenAppPackageFileAsync($"{folder}/{slug}.png");
            await using var output = File.Create(cache);
            await asset.CopyToAsync(output);
            return cache;
        }
        catch
        {
            return null; // no bundled art for this game
        }
    }

    private static string GetAssetSlug(string name) => name switch
    {
        _ => string.Concat(name.ToLowerInvariant().Select(c => char.IsAscii(c) && char.IsLetterOrDigit(c) ? c : '-')),
    };
}

/// <summary>
/// What the lower screen should show when no Pokémon is selected: the shelf's highlighted
/// game (hero art), or the Pokédex picker's highlighted species (red dex view).
/// </summary>
public partial class SecondScreenState : ObservableObject
{
    [ObservableProperty] private DetectedSave? _previewGame;

    /// <summary>Species highlighted in the Pokédex picker; the dex view outranks everything while set.</summary>
    [ObservableProperty] private int? _previewSpecies;
}
