using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Bundled SteamGridDB art (fetched at build time), keyed by PKHeX game-name slug.</summary>
public static class GameArt
{
    // Bumped whenever bundled art changes so on-device caches never serve stale images.
    private const string AssetVersion = "v10";
    public static Task<string?> GetIconAsync(string gameLabel) => GetAsync("gameart", gameLabel);
    public static Task<string?> GetHeroAsync(string gameLabel) => GetAsync("gamehero", gameLabel);
    public static Task<string?> GetLogoAsync(string gameLabel) => GetAsync("gamelogo", gameLabel);

    private static async Task<string?> GetAsync(string folder, string gameLabel)
    {
        const string prefix = "Pokémon ";
        var name = gameLabel.StartsWith(prefix, StringComparison.Ordinal) ? gameLabel[prefix.Length..] : gameLabel;
        // A pair the save cannot split ("FireRed / LeafGreen") uses its own art when bundled,
        // else the first game's, so an unchosen edition never loses its cartridge.
        return await GetBySlugAsync(folder, GetAssetSlug(name))
            ?? (name.Split(" / ") is [var first, _] ? await GetBySlugAsync(folder, GetAssetSlug(first)) : null);
    }

    private static async Task<string?> GetBySlugAsync(string folder, string slug)
    {
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
/// The second screen's inputs. <see cref="Routes"/> decides WHO owns the lower display
/// (the foreground page or overlay, see <see cref="SecondScreenOwner"/>); the properties
/// below are only the owners' payloads, read while their owner is on top. A stale payload
/// can therefore never show: leaving a surface releases its claim and the screen follows.
/// </summary>
public partial class SecondScreenState : ObservableObject
{
    public SecondScreenState()
    {
        Routes.Changed += () => Owner = Routes.Current;
    }

    /// <summary>The ownership stack pages and overlays claim and release.</summary>
    public SecondScreenRoutes Routes { get; } = new();

    /// <summary>The top claim's owner; the lower screen is a pure function of it.</summary>
    [ObservableProperty] private SecondScreenOwner _owner;

    /// <summary>Home's payload: the shelf's highlighted game (hero art).</summary>
    [ObservableProperty] private DetectedSave? _previewGame;

    /// <summary>The Pokédex picker's payload: its highlighted species.</summary>
    [ObservableProperty] private int? _previewSpecies;

    /// <summary>The Bank's payload: the mon under its cursor, decoded for the inspector
    /// (null summary = the empty-slot card). One record so the screen never sees half an update.</summary>
    [ObservableProperty] private InspectorContent? _inspected;

    /// <summary>The full-screen summary's payload: the box it walks and the mon it shows.</summary>
    [ObservableProperty] private SummaryOverview? _overview;

    /// <summary>The inspector's page; SELECT in the Bank and the tabs on the lower screen turn it.</summary>
    [ObservableProperty] private SummaryPage _inspectorPage;

    /// <summary>The Living Dex Autopilot's payload: its route map.</summary>
    [ObservableProperty] private LivingDexRoute? _autopilotRoute;
}

/// <summary>What the inspector shows for a surface that drives it: the mon (null = empty slot),
/// whether its legality verdict is still being computed, and the context line.</summary>
public sealed record InspectorContent(MonSummary? Summary, bool LegalityPending, string? Caption);

/// <summary>One occupied slot's icon on the summary's box overview.</summary>
public sealed record SlotIcon(int Species, int Form, bool Shiny, bool HasItem = false, SpriteTraits Traits = default)
{
    public SpriteLook Look => new(Species, Form, Shiny, Traits);
}

/// <summary>The box the full-screen summary walks, drawn on the lower screen while the top
/// screen shows the details: every slot's icon and the mon being viewed.</summary>
public sealed record SummaryOverview(string Context, int Count, int Slot, IReadOnlyList<SlotIcon?> Icons, int Position, int Total);
