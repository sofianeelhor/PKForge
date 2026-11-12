using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Display conventions every ROM hack engine shares with the retail editor, so a hack save
/// reads like any other: "no item" is PKHeX's own "(None)", never a hack table's "NONE",
/// "None", "#0" or a blank that falls back to a bare 0.
/// </summary>
internal static class HackNames
{
    private static readonly Lazy<string> NoItem = new(() => GameInfo.GetStrings("en").itemlist[0]);

    /// <summary>A copy of <paramref name="names"/> whose id 0 reads as the retail "(None)".</summary>
    public static string[] WithNoItem(IReadOnlyList<string> names)
    {
        var copy = names.ToArray();
        if (copy.Length > 0) copy[0] = NoItem.Value;
        return copy;
    }

    /// <summary>
    /// PKHeX's modern item names, kept only where the hack has the item
    /// (<paramref name="fromNational"/> gives its ROM id, 0 when it has none): the table for
    /// held items an engine bridges to modern ids.
    /// </summary>
    public static string[] NationalItems(Func<int, int> fromNational)
    {
        var modern = GameInfo.GetStrings("en").itemlist;
        var names = new string[modern.Length];
        names[0] = NoItem.Value;
        for (var id = 1; id < modern.Length; id++)
            names[id] = modern[id].Length > 0 && fromNational(id) > 0 ? modern[id] : string.Empty;
        return names;
    }

    /// <summary>
    /// The display names of four moves whose hack ids have no PKHeX twin (their national id
    /// reads 0), or null when every move maps. <paramref name="national"/> and
    /// <paramref name="name"/> take the hack's own id.
    /// </summary>
    public static IReadOnlyList<string?>? UnmappedMoves(IReadOnlyList<int> raw, Func<int, int> national, Func<int, string> name)
    {
        string?[]? names = null;
        for (var i = 0; i < raw.Count && i < 4; i++)
        {
            if (raw[i] == 0 || national(raw[i]) != 0) continue;
            names ??= new string?[4];
            names[i] = name(raw[i]);
        }
        return names;
    }
}
