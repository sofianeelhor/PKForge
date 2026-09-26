namespace PKForge.Domain;

/// <summary>
/// The offline sprite pack: every file the app can download, with the cache path it lands at
/// under the app data directory. The archive built by tools/SpritePack and the per-file
/// fallback both read this list, so the two can never disagree about names.
/// </summary>
public static class SpritePack
{
    /// <summary>PokeAPI/sprites root every remote file is fetched from.</summary>
    public const string RemoteRoot = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/";

    /// <summary>Cache folders the pack writes into, relative to the app data directory.</summary>
    public static IReadOnlyList<string> Folders { get; } = ["home", "showdown", "items"];

    /// <summary>One pack file: its cache path ("home/6-s.png") and the URL it comes from.</summary>
    public sealed record Entry(string CachePath, string Url);

    /// <summary>Every sprite and item icon, deduplicated by cache path, in a stable order.</summary>
    public static IReadOnlyList<Entry> Entries(IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string cachePath, string url)
        {
            if (seen.Add(cachePath)) entries.Add(new Entry(cachePath, url));
        }

        foreach (var look in SpriteCatalog.AllRemoteLooks())
        {
            if (SpriteCatalog.Showdown(look) is { } sd)
                Add("showdown/" + sd.CacheName, RemoteRoot + "pokemon/other/showdown/" + sd.Path);
            if (SpriteCatalog.Home(look) is { } home)
                Add("home/" + home.CacheName, RemoteRoot + "pokemon/other/home/" + home.Path);
        }
        foreach (var name in itemNames)
        {
            var slug = ItemSlug(name);
            if (slug.Length > 0) Add("items/" + slug + ".png", RemoteRoot + "items/" + slug + ".png");
        }
        return entries;
    }

    /// <summary>PokeAPI's item file stem: "Poké Ball" → "poke-ball".</summary>
    public static string ItemSlug(string itemName)
    {
        ArgumentNullException.ThrowIfNull(itemName);
        var folded = itemName.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (char.GetUnicodeCategory(ch) is not System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append('-');
        }
        return sb.ToString().Replace("--", "-").Trim('-');
    }

    /// <summary>
    /// True when an archive entry name is a plain file inside one of the pack's folders
    /// ("home/6-s.png"). Anything else (absolute paths, "..", other folders, odd characters)
    /// is rejected, so a damaged or hostile archive can never write outside the caches.
    /// </summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        var slash = name.IndexOf('/');
        if (slash <= 0 || name.IndexOf('/', slash + 1) >= 0) return false;
        if (!Folders.Contains(name[..slash])) return false;
        var file = name[(slash + 1)..];
        if (file.Length == 0 || file[0] == '.') return false;
        foreach (var ch in file)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')) return false;
        }
        return file.EndsWith(".png", StringComparison.Ordinal) || file.EndsWith(".gif", StringComparison.Ordinal);
    }
}
