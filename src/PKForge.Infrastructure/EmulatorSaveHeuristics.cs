using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Platform-independent detection heuristics. File names are only a cheap pre-filter:
/// the authoritative detector is always an engine parse of the actual bytes.
/// </summary>
public static class EmulatorSaveHeuristics
{
    /// <summary>Extensions emulators use for flat save files (empty = extensionless, e.g. Switch "main").</summary>
    private static readonly string[] CandidateExtensions =
        ["", ".sav", ".srm", ".bin", ".dat", ".gci", ".dsv", ".bak", ".main"];

    /// <summary>Pokémon Switch title IDs, for friendly labels on Eden hits. Display-only.</summary>
    private static readonly Dictionary<string, string> SwitchTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["010003F003A34000"] = "Let's Go Pikachu",
        ["0100187003A36000"] = "Let's Go Eevee",
        ["0100ABF008968000"] = "Sword",
        ["01008DB008C2C000"] = "Shield",
        ["0100000011D90000"] = "Brilliant Diamond",
        ["010018E011D92000"] = "Shining Pearl",
        ["01001F5010B28000"] = "Legends: Arceus",
        ["0100A3D008C5C000"] = "Scarlet",
        ["01008F6008C5E000"] = "Violet",
        ["0100F43008C44000"] = "Legends: Z-A",
    };

    public static bool IsCandidateFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return CandidateExtensions.Contains(extension);
    }

    /// <summary>Matches Eden's "main" / "*.bin" save file names (BDSP writes .bin, others extensionless "main").</summary>
    public static bool IsEdenSaveFileName(string fileName) =>
        fileName == "main" || fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives a game label from a path-like document id by walking its segments backwards
    /// looking for a known Switch title ID directory name.
    /// </summary>
    public static string GuessSwitchGameLabel(string documentPath)
    {
        var parts = documentPath.Replace('\\', '/').Split('/');
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (SwitchTitles.TryGetValue(parts[i], out var name))
                return name;
        }
        return "Switch save";
    }

    /// <summary>NAND/SD-structured saves are the corruption-prone write path and get extra confirmation.</summary>
    public static bool RequiresExtraCare(EmulatorKind kind) =>
        kind is EmulatorKind.Azahar or EmulatorKind.Eden;

    /// <summary>Newest-first, deduplicated by document id (Eden roots and pinned files can overlap).</summary>
    public static List<DetectedSave> Normalize(IEnumerable<DetectedSave> saves)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return saves
            .Where(save => seen.Add(save.DocumentId))
            .OrderByDescending(save => save.LastModified ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
