namespace PKForge.Domain;

/// <summary>
/// A bank box's wallpaper: one of the storage palette's flat colors, or an in-game box
/// wallpaper picture (a bundled PKHeX asset name such as "box_wp01xy").
/// </summary>
public sealed record BankWallpaper
{
    private const string ColorPrefix = "color:";
    private const string ArtPrefix = "art:";

    private BankWallpaper(int? color, string? art) => (ColorIndex, ArtAsset) = (color, art);

    /// <summary>Index into the storage palette, when this is a flat color.</summary>
    public int? ColorIndex { get; }

    /// <summary>Bundled wallpaper asset name, when this is a picture.</summary>
    public string? ArtAsset { get; }

    /// <summary>The stored form: "color:3" or "art:box_wp01xy".</summary>
    public string Id => ColorIndex is { } color ? ColorPrefix + color : ArtPrefix + ArtAsset;

    public static BankWallpaper Color(int index) =>
        index >= 0 ? new BankWallpaper(index, null) : throw new ArgumentOutOfRangeException(nameof(index));

    public static BankWallpaper Art(string asset) =>
        IsAssetName(asset) ? new BankWallpaper(null, asset) : throw new ArgumentException($"Not a wallpaper asset name: {asset}", nameof(asset));

    /// <summary>Reads a stored id; anything malformed reads as "no wallpaper chosen".</summary>
    public static BankWallpaper? Parse(string? id)
    {
        if (id is null) return null;
        if (id.StartsWith(ColorPrefix, StringComparison.Ordinal))
            return int.TryParse(id.AsSpan(ColorPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var color)
                ? Color(color) : null;
        if (id.StartsWith(ArtPrefix, StringComparison.Ordinal) && IsAssetName(id[ArtPrefix.Length..]))
            return Art(id[ArtPrefix.Length..]);
        return null;
    }

    /// <summary>A game's box wallpapers, in the order the game lists them.</summary>
    public sealed record ArtGroup(string Title, IReadOnlyList<string> Assets);

    /// <summary>Every bundled in-game wallpaper, grouped by game, oldest first.</summary>
    public static IReadOnlyList<ArtGroup> ArtCatalog { get; } =
    [
        new("Ruby & Sapphire", Range("rs", 1, 16)),
        new("Emerald", Range("e", 1, 16)),
        new("FireRed & LeafGreen", Range("frlg", 13, 16)),
        new("Diamond & Pearl", Range("dp", 1, 24)),
        new("Platinum", Range("pt", 17, 24)),
        new("HeartGold & SoulSilver", Range("hgss", 17, 24)),
        new("Black & White", Range("bw", 1, 24)),
        new("Black 2 & White 2", Range("b2w2", 17, 24)),
        new("X & Y", Range("xy", 1, 24)),
        new("Omega Ruby & Alpha Sapphire", Range("ao", 17, 24)),
        new("Sword & Shield", Range("swsh", 1, 19)),
        new("Brilliant Diamond & Shining Pearl", Range("bdsp", 1, 32)),
        new("Scarlet & Violet", [.. Range("sv", 1, 19), "box_wp20sv_n", "box_wp20sv_u"]),
    ];

    private static string[] Range(string game, int first, int last) =>
        [.. Enumerable.Range(first, last - first + 1).Select(n => $"box_wp{n:00}{game}")];

    // Asset names become file paths: letters, digits and underscores only.
    private static bool IsAssetName(string name) =>
        name.Length is > 0 and <= 64 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
}
