using System.Globalization;
using System.Text;

namespace PKForge.Domain;

/// <summary>The three images a player can give a game of their own.</summary>
public enum GameArtKind
{
    /// <summary>The cartridge / box art on the Home shelf tile.</summary>
    Cartridge = 0,
    /// <summary>The title logo (second screen banner).</summary>
    Logo = 1,
    /// <summary>A wide banner shown where a big image of the game fits.</summary>
    Hero = 2,
}

/// <summary>
/// Naming rules for the player's own game art. Each game identity owns a key (a readable slug,
/// "emerald-rogue"); its images live flat in PKForge's private data as <c>key--kind.png</c> and
/// mirror as <c>Games/key/kind.png</c>. Keys and kinds are a closed alphabet, so a name from a
/// folder or an archive can never point anywhere else.
/// </summary>
public static class CustomGameArt
{
    public const string Folder = "game-art";
    public const string MirrorPrefix = "Games/";
    private const string Separator = "--";
    private const int MaxKeyLength = 40;

    public static IReadOnlyList<GameArtKind> Kinds { get; } = [GameArtKind.Cartridge, GameArtKind.Logo, GameArtKind.Hero];

    public static string NameOf(GameArtKind kind) => kind switch
    {
        GameArtKind.Logo => "logo",
        GameArtKind.Hero => "hero",
        _ => "cartridge",
    };

    public static GameArtKind? KindOf(string name) => name switch
    {
        "cartridge" => GameArtKind.Cartridge,
        "logo" => GameArtKind.Logo,
        "hero" => GameArtKind.Hero,
        _ => null,
    };

    /// <summary>Lowercase ASCII letters, digits and single dashes; no leading/trailing dash.</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength }
        && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
        && key[0] != '-' && key[^1] != '-' && !key.Contains(Separator, StringComparison.Ordinal);

    /// <summary>A fresh key from a game's name ("Pokémon Emerald Rogue" -> "emerald-rogue"), suffixed until free.</summary>
    public static string NewKey(string? name, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var folded = (name ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var slug = new StringBuilder();
        foreach (var c in folded)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var text = slug.ToString().Trim('-');
        if (text.StartsWith("pokemon-", StringComparison.Ordinal)) text = text["pokemon-".Length..];
        if (text.Length > MaxKeyLength - 4) text = text[..(MaxKeyLength - 4)].Trim('-');
        if (text.Length == 0) text = "game";
        var key = text;
        for (var n = 2; taken(key); n++) key = $"{text}-{n.ToString(CultureInfo.InvariantCulture)}";
        return key;
    }

    /// <summary>The private file name: "emerald-rogue--hero.png".</summary>
    public static string FileName(string key, GameArtKind kind) => $"{key}{Separator}{NameOf(kind)}.png";

    /// <summary>Parses a private file name; null for anything that is not one.</summary>
    public static (string Key, GameArtKind Kind)? ParseFileName(string fileName)
    {
        if (!fileName.EndsWith(".png", StringComparison.Ordinal)) return null;
        var stem = fileName[..^4];
        var at = stem.LastIndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0) return null;
        var key = stem[..at];
        return IsValidKey(key) && KindOf(stem[(at + Separator.Length)..]) is { } kind ? (key, kind) : null;
    }

    /// <summary>"emerald-rogue--hero.png" -> "emerald-rogue/hero.png" (under <see cref="MirrorPrefix"/>).</summary>
    public static string? ToMirrorName(string fileName) =>
        ParseFileName(fileName) is var (key, kind) ? $"{key}/{NameOf(kind)}.png" : null;

    /// <summary>"emerald-rogue/hero.png" -> "emerald-rogue--hero.png"; null for any other shape.</summary>
    public static string? FromMirrorName(string mirrorName)
    {
        var parts = mirrorName.Split('/');
        if (parts is not [var key, var file] || !IsValidKey(key) || !file.EndsWith(".png", StringComparison.Ordinal)) return null;
        return KindOf(file[..^4]) is { } kind ? FileName(key, kind) : null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when the bytes start with the PNG signature (the only format stored).</summary>
    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) => bytes.Length > 8 && bytes[..8].SequenceEqual(PngSignature);
}

/// <summary>The player's own game images, keyed by <see cref="CustomGameArt"/> keys.</summary>
public interface ICustomGameArtStore
{
    /// <summary>The stored image's path, or null when the game uses its default.</summary>
    string? PathOf(string? key, GameArtKind kind);

    /// <summary>Stores a PNG (already normalized by the caller). Throws <see cref="InvalidDataException"/> for non-PNG bytes.</summary>
    void Put(string key, GameArtKind kind, byte[] png);

    /// <summary>Back to the default image for one kind.</summary>
    void Remove(string key, GameArtKind kind);

    /// <summary>True when any image exists under the key (keys stay reserved while used).</summary>
    bool HasAny(string key);

    /// <summary>Raised with the key after any change.</summary>
    event Action<string>? Changed;
}
