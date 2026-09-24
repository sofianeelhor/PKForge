using PKForge.Domain;
using PKHeX.Core;
using static PKHeX.Core.GameVersion;

namespace PKForge.Engine;

/// <summary>One in-game box wallpaper choice.</summary>
/// <param name="Index">The value the save stores (0-based).</param>
/// <param name="Name">PKHeX's wallpaper name, or "Wallpaper N" where PKHeX has none.</param>
/// <param name="AssetName">The PKHeX.Drawing.Misc resource name of its picture (bundled as
/// <c>wallpapers/{AssetName}.png</c>), or null when PKHeX ships no picture for it.</param>
public sealed record BoxWallpaperOption(int Index, string Name, string? AssetName);

/// <summary>A box's name and wallpaper as the game shows them.</summary>
public sealed record BoxLayoutEntry(int Box, string Name, int? Wallpaper, string? WallpaperName, string? WallpaperAsset);

/// <summary>
/// Box names and in-game wallpapers, after PKHeX's WinForms <c>SAV_BoxLayout</c>:
/// names through <c>IBoxDetailNameRead</c>/<c>IBoxDetailName.SetBoxName</c>, wallpapers through
/// <c>IBoxDetailWallpaper</c>, the wallpaper list per generation from <c>SAV_BoxLayout.LoadWallpapers</c>
/// and the name length from its <c>TB_BoxName.MaxLength</c> switch. Picture names follow
/// PKHeX.Drawing.Misc <c>WallpaperUtil.GetWallpaperResourceName</c> exactly.
/// </summary>
public static class BoxLayoutService
{
    /// <summary>True when the save stores editable box names (every retail format from Gen 2 on).</summary>
    public static bool SupportsNames(ISaveEngineSession session) => TryGetSave(session) is IBoxDetailName;

    /// <summary>True when the save stores a wallpaper per box (Gen 3 onward, per <c>IBoxDetailWallpaper</c>).</summary>
    public static bool SupportsWallpapers(ISaveEngineSession session) =>
        TryGetSave(session) is { } save && save is IBoxDetailWallpaper && GetWallpaperCount(save) > 0
        // PKHeX's WallpaperUtil draws one fixed picture for Legends: Arceus and Legends: Z-A.
        && save is not SAV8LA and not SAV9ZA;

    public static bool IsSupported(ISaveEngineSession session) => SupportsNames(session) || SupportsWallpapers(session);

    /// <summary>SAV_BoxLayout's <c>TB_BoxName.MaxLength</c>.</summary>
    public static int GetNameMaxLength(ISaveEngineSession session)
    {
        var save = Require(session);
        return save.Generation switch
        {
            2 when save is SAV2 { Japanese: false, Korean: false } => 8 * 2,
            3 when save is SAV3RSBox => 8 + SAV3RSBox.BoxNamePrefix,
            6 or 7 => 14,
            >= 8 => 16,
            _ => 8,
        };
    }

    public static BoxLayoutEntry GetBox(ISaveEngineSession session, int box)
    {
        var save = Require(session);
        ArgumentOutOfRangeException.ThrowIfNegative(box);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(box, save.BoxCount);
        var name = session.GetBoxName(box);
        if (!SupportsWallpapers(session))
            return new BoxLayoutEntry(box, name, null, null, null);
        var index = ((IBoxDetailWallpaper)save).GetBoxWallpaper(box);
        var options = GetWallpapers(session);
        var option = (uint)index < (uint)options.Count ? options[index] : null;
        return new BoxLayoutEntry(box, name, index, option?.Name ?? $"Wallpaper {index + 1}", option?.AssetName);
    }

    /// <summary>Renames a box. Blank names are refused (the games never store one); the name is
    /// cut to <see cref="GetNameMaxLength"/> as PKHeX's text box does.</summary>
    public static BoxLayoutEntry Rename(ISaveEngineSession session, int box, string name)
    {
        var save = Require(session);
        if (save is not IBoxDetailName names)
            throw new NotSupportedException("This game does not store box names.");
        ArgumentOutOfRangeException.ThrowIfNegative(box);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(box, save.BoxCount);
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A box name cannot be empty.", nameof(name));
        var max = GetNameMaxLength(session);
        names.SetBoxName(box, trimmed.Length > max ? trimmed[..max] : trimmed);
        return GetBox(session, box);
    }

    public static BoxLayoutEntry SetWallpaper(ISaveEngineSession session, int box, int wallpaper)
    {
        var save = Require(session);
        if (!SupportsWallpapers(session))
            throw new NotSupportedException("This game does not store box wallpapers.");
        ArgumentOutOfRangeException.ThrowIfNegative(box);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(box, save.BoxCount);
        ArgumentOutOfRangeException.ThrowIfNegative(wallpaper);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wallpaper, GetWallpaperCount(save));
        ((IBoxDetailWallpaper)save).SetBoxWallpaper(box, wallpaper);
        return GetBox(session, box);
    }

    /// <summary>The wallpapers this game offers, in stored order.</summary>
    public static IReadOnlyList<BoxWallpaperOption> GetWallpapers(ISaveEngineSession session)
    {
        var save = Require(session);
        var count = GetWallpaperCount(save);
        var names = GameInfo.GetStrings("en").wallpapernames;
        // SAV_BoxLayout: SWSH and SV use placeholders ("Wallpaper N") rather than the Gen 3-7 names.
        var named = save.Generation < 8 || save is SAV8BS;
        return Enumerable.Range(0, count).Select(i => new BoxWallpaperOption(i,
            named && i < names.Length ? names[i] : $"Wallpaper {i + 1}",
            HasPicture(save.Version, i) ? GetWallpaperResourceName(save.Version, i) : null)).ToArray();
    }

    /// <summary>SAV_BoxLayout.LoadWallpapers counts per generation.</summary>
    private static int GetWallpaperCount(SaveFile save) => save.Generation switch
    {
        3 when save is SAV3 or SAV3RSBox => 16,
        4 or 5 or 6 => 24,
        7 => 16,
        8 when save is SAV8BS => 32,
        8 => 19,
        9 => 20,
        _ => 0,
    };

    /// <summary>Only the pictures PKHeX.Drawing.Misc/Resources/img/box ships: rs/e (16), frlg (13-16),
    /// dp (24), pt/hgss (17-24), bw (24), b2w2/ao (17-24), xy (24), bdsp (32), swsh (19), sv (21).</summary>
    private static bool HasPicture(GameVersion version, int index) => version.Context switch
    {
        EntityContext.Gen3 => index < 16,
        EntityContext.Gen4 or EntityContext.Gen5 or EntityContext.Gen6 => index < 24,
        EntityContext.Gen7 => index < 16,
        EntityContext.Gen8b => index < 32,
        EntityContext.Gen8 => index < 19,
        EntityContext.Gen9 => index < 20,
        _ => false,
    };

    /// <summary>Verbatim port of PKHeX.Drawing.Misc WallpaperUtil.GetWallpaperResourceName (that
    /// assembly is System.Drawing-bound, so it cannot be referenced from the engine).</summary>
    public static string GetWallpaperResourceName(GameVersion version, int index)
    {
        index++; // start indexes at 1
        var suffix = GetResourceSuffix(version, index);
        var variant = version switch
        {
            SL when index is 20 => "_n", // Naranja
            VL when index is 20 => "_u", // Uva
            _ => string.Empty,
        };
        return $"box_wp{index:00}{suffix}{variant}";
    }

    private static string GetResourceSuffix(GameVersion version, int index) => version.Context switch
    {
        EntityContext.Gen3 when version == E => "e",
        EntityContext.Gen3 when FRLG.Contains(version) && index > 12 => "frlg",
        EntityContext.Gen3 => "rs",

        EntityContext.Gen4 when index <= 16 => "dp",
        EntityContext.Gen4 when version == Pt => "pt",
        EntityContext.Gen4 when HGSS.Contains(version) => "hgss",

        EntityContext.Gen5 => B2W2.Contains(version) && index > 16 ? "b2w2" : "bw",
        EntityContext.Gen6 => ORAS.Contains(version) && index > 16 ? "ao" : "xy",
        EntityContext.Gen7 => "xy",
        EntityContext.Gen8b => "bdsp",
        EntityContext.Gen8 => "swsh",
        EntityContext.Gen9 => "sv",
        _ => string.Empty,
    };

    private static SaveFile? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile : null;

    private static SaveFile Require(ISaveEngineSession session) =>
        TryGetSave(session) is { } save && (save is IBoxDetailName || save is IBoxDetailWallpaper)
            ? save
            : throw new NotSupportedException("This game has no box names or wallpapers to edit.");
}
