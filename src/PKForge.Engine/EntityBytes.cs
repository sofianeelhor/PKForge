using System.Text.RegularExpressions;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The one way PKForge turns stored entity bytes (bank entries, exports, loose files) back into
/// a <see cref="PKM"/>. Raw bytes alone are ambiguous: PK8 and PB8 are both 0x148 bytes, PK6
/// and PK7 both 0xE8/0x104, PK9 and PA9 both 0x148, and PKHeX's context-free read resolves
/// each collision to one side by default (PB8 → PK8, PK6 → PK7, PA9 → PK9). The exact format is
/// therefore recorded with the bytes (<see cref="BankEntryInfo.Format"/>,
/// <see cref="SlotExport.Format"/>) and every parse honours it.
/// </summary>
public static partial class EntityBytes
{
    /// <summary>Formats PKForge constructs directly when the recorded format disagrees with
    /// PKHeX's content heuristics (the same-size collision families, plus the other fixed-size
    /// box formats so a recorded name is always authoritative).</summary>
    private static readonly Dictionary<string, (Func<byte[], PKM> Create, EntityContext Context)> Direct =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(PK3)] = (b => new PK3(b), EntityContext.Gen3),
            [nameof(CK3)] = (b => new CK3(b), EntityContext.Gen3),
            [nameof(XK3)] = (b => new XK3(b), EntityContext.Gen3),
            [nameof(PK4)] = (b => new PK4(b), EntityContext.Gen4),
            [nameof(BK4)] = (b => new BK4(b), EntityContext.Gen4),
            [nameof(RK4)] = (b => new RK4(b), EntityContext.Gen4),
            [nameof(PK5)] = (b => new PK5(b), EntityContext.Gen5),
            [nameof(PK6)] = (b => new PK6(b), EntityContext.Gen6),
            [nameof(PK7)] = (b => new PK7(b), EntityContext.Gen7),
            [nameof(PB7)] = (b => new PB7(b), EntityContext.Gen7b),
            [nameof(PK8)] = (b => new PK8(b), EntityContext.Gen8),
            [nameof(PB8)] = (b => new PB8(b), EntityContext.Gen8b),
            [nameof(PA8)] = (b => new PA8(b), EntityContext.Gen8a),
            [nameof(PK9)] = (b => new PK9(b), EntityContext.Gen9),
            [nameof(PA9)] = (b => new PA9(b), EntityContext.Gen9a),
        };

    /// <summary>Formats read by content alone (Gen 1/2 list records, Stadium 2): no size collision.</summary>
    private static readonly HashSet<string> ByContent = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PK1), nameof(PK2), nameof(SK2),
    };

    /// <summary>The format name recorded for <paramref name="pk"/> ("PB8", "PK6", ...).</summary>
    public static string FormatOf(PKM pk) => pk.GetType().Name;

    /// <summary>
    /// A canonical format name from a type name ("pb8"), an extension (".pb8") or a file name
    /// ("Pikachu.pb8"); null when it names no PKForge-readable entity format.
    /// </summary>
    public static string? Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var name = format.Trim();
        if (!Direct.ContainsKey(name) && !ByContent.Contains(name))
            name = Path.GetExtension(name).TrimStart('.');
        if (Direct.ContainsKey(name)) return Direct.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ByContent.Contains(name)) return ByContent.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>The context a format belongs to (None when unknown or context-free).</summary>
    public static EntityContext ContextOf(string? format) =>
        Normalize(format) is { } name && Direct.TryGetValue(name, out var known) ? known.Context : EntityContext.None;

    /// <summary>Parses a bank entry's bytes as the format recorded at deposit.</summary>
    public static PKM? Parse(BankEntry entry, byte[] bytes) => Parse(bytes, entry.Info.Format);

    /// <summary>
    /// Parses <paramref name="bytes"/> as <paramref name="format"/> when given. A recorded format
    /// is authoritative: if PKHeX's heuristics read the bytes as a sibling, the recorded type is
    /// constructed directly (when the byte length fits it). Without a format, PKHeX's heuristics
    /// decide, with <paramref name="prefer"/> breaking same-size ties. Null when unreadable.
    /// </summary>
    public static PKM? Parse(byte[] bytes, string? format, EntityContext prefer = EntityContext.None)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var name = Normalize(format);
        var hint = name is not null && Direct.TryGetValue(name, out var direct) ? direct.Context : prefer;
        PKM? detected;
        try { detected = EntityFormat.GetFromBytes(bytes.ToArray(), hint); }
        catch (Exception) { detected = null; }

        if (name is null || ByContent.Contains(name) || (detected is not null && FormatOf(detected) == name))
            return detected;

        var (create, _) = Direct[name];
        try
        {
            var pk = create(bytes.ToArray());
            return bytes.Length == pk.SIZE_PARTY || bytes.Length == pk.SIZE_STORED ? pk : detected;
        }
        catch (Exception)
        {
            return detected; // wrong length for the recorded type: the bytes are not that format
        }
    }

    /// <summary>
    /// The PKHeX types these bytes could honestly be: one entry when unambiguous, two for the
    /// same-size collisions PKHeX cannot resolve from content (PK6/PK7, PK8/PB8, PK9/PA9).
    /// </summary>
    public static IReadOnlyList<string> Candidates(byte[] bytes)
    {
        var found = new List<string>();
        foreach (var prefer in new[] { EntityContext.None, EntityContext.Gen6, EntityContext.Gen7, EntityContext.Gen8, EntityContext.Gen8b, EntityContext.Gen9, EntityContext.Gen9a })
        {
            try
            {
                if (EntityFormat.GetFromBytes(bytes.ToArray(), prefer) is { } pk && !found.Contains(FormatOf(pk)))
                    found.Add(FormatOf(pk));
            }
            catch (Exception) { /* unreadable under this hint */ }
        }
        return found;
    }

    /// <summary>
    /// Migration rule for an entry deposited before <see cref="BankEntryInfo.Format"/> existed.
    /// In order: (1) the bytes admit a single format; (2) the recorded generation picks one
    /// candidate that is not merely PKHeX's context-free default (a recorded 6 proves PK6
    /// because the old deposit read would otherwise have said 7; a recorded 7 for PK6/PK7 is
    /// no evidence, since that was the default); (3) the recorded source name names exactly one
    /// candidate's games. Otherwise null: the entry keeps reading by heuristics, never a guess.
    /// </summary>
    public static string? InferFormat(BankEntryInfo info, byte[] bytes)
    {
        var candidates = Candidates(bytes);
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // The old deposit path read the bytes context-free; that is the format it recorded
        // the generation of, so the generation only proves something when it disagrees.
        string? contextFree = null;
        try { contextFree = EntityFormat.GetFromBytes(bytes.ToArray()) is { } pk ? FormatOf(pk) : null; }
        catch (Exception) { /* no default read */ }
        var byGeneration = candidates
            .Where(c => c != contextFree && GenerationOf(c) == info.Generation)
            .ToList();
        if (byGeneration.Count == 1 && candidates.Where(c => GenerationOf(c) == info.Generation).Count() == 1)
            return byGeneration[0];

        var bySource = candidates.Where(c => SourceNames(c, info.SourceName)).ToList();
        return bySource.Count == 1 ? bySource[0] : null;
    }

    /// <summary>
    /// Fills <see cref="BankEntryInfo.Format"/> for every entry that lacks it (one index write).
    /// Entries whose format cannot be proven stay null. Returns (migrated, unresolved).
    /// </summary>
    public static (int Migrated, int Unresolved) MigrateFormats(IBankService bank)
    {
        var updates = new List<(Guid, BankEntryInfo)>();
        var unresolved = 0;
        foreach (var entry in bank.GetAll())
        {
            if (entry.Info.Format is not null) continue;
            byte[] bytes;
            try { bytes = bank.GetData(entry.Id); }
            catch (Exception) { unresolved++; continue; }
            if (WithFormat(entry.Info, bytes) is not { } fixedInfo) { unresolved++; continue; }
            updates.Add((entry.Id, fixedInfo));
        }
        return (updates.Count == 0 ? 0 : bank.UpdateInfo(updates), unresolved);
    }

    /// <summary>
    /// Fills <see cref="BankEntryInfo.Traits"/> (gender art, Alcremie decoration, Gen 6 cosplay)
    /// for every entry indexed before the field existed, read from its stored bytes (one index
    /// write). Unreadable entries stay null and draw with plain traits. Returns how many changed.
    /// </summary>
    public static int MigrateSpriteTraits(IBankService bank)
    {
        var updates = new List<(Guid, BankEntryInfo)>();
        foreach (var entry in bank.GetAll())
        {
            if (entry.Info.Traits is not null) continue;
            try
            {
                if (WithTraits(entry, bank.GetData(entry.Id)) is { } fixedInfo)
                    updates.Add((entry.Id, fixedInfo));
            }
            catch (Exception) { /* unreadable: keeps plain traits */ }
        }
        return updates.Count == 0 ? 0 : bank.UpdateInfo(updates);
    }

    /// <summary>The index migration version <see cref="MigrateBank"/> brings a bank to.</summary>
    public const int BankMigrationVersion = 1;

    /// <summary>
    /// Runs every index migration (<see cref="MigrateFormats"/> then <see cref="MigrateSpriteTraits"/>)
    /// once per bank: each legacy entry's bytes are read a single time, the fixes and the
    /// version marker land in one index write, and entries that could not be resolved stay
    /// null without being retried on the next launch. Safe off the UI thread: a fix only
    /// applies to an entry nobody changed meanwhile. Returns how many entries changed.
    /// </summary>
    public static int MigrateBank(IBankService bank)
    {
        if (bank.MigrationVersion >= BankMigrationVersion) return 0;
        var updates = new List<(Guid, BankEntryInfo, BankEntryInfo)>();
        foreach (var entry in bank.GetAll())
        {
            if (entry.Info.Format is not null && entry.Info.Traits is not null) continue;
            byte[] bytes;
            try { bytes = bank.GetData(entry.Id); }
            catch (Exception) { continue; }
            var info = entry.Info;
            if (info.Format is null && WithFormat(info, bytes) is { } withFormat) info = withFormat;
            if (info.Traits is null)
            {
                try
                {
                    if (WithTraits(entry with { Info = info }, bytes) is { } withTraits) info = withTraits;
                }
                catch (Exception) { /* unreadable: keeps plain traits */ }
            }
            if (info != entry.Info) updates.Add((entry.Id, entry.Info, info));
        }
        return bank.CompleteMigration(BankMigrationVersion, updates);
    }

    private static BankEntryInfo? WithFormat(BankEntryInfo info, byte[] bytes)
    {
        if (InferFormat(info, bytes) is not { } format) return null;
        var generation = Parse(bytes, format)?.Format ?? info.Generation;
        return info with { Format = format, Generation = generation };
    }

    private static BankEntryInfo? WithTraits(BankEntry entry, byte[] bytes) =>
        Parse(entry, bytes) is { } pk ? entry.Info with { Traits = EntitySprite.Traits(pk) } : null;

    private static int GenerationOf(string format) => format switch
    {
        nameof(PK6) => 6,
        nameof(PK7) or nameof(PB7) => 7,
        nameof(PK8) or nameof(PB8) or nameof(PA8) => 8,
        nameof(PK9) or nameof(PA9) => 9,
        _ => 0,
    };

    /// <summary>True when the source label names a game that stores this format.</summary>
    private static bool SourceNames(string format, string source)
    {
        var pattern = format switch
        {
            nameof(PK6) => SourceGen6(),
            nameof(PK7) => SourceGen7(),
            nameof(PK8) => SourceSwsh(),
            nameof(PB8) => SourceBdsp(),
            nameof(PK9) => SourceSv(),
            nameof(PA9) => SourceZa(),
            _ => null,
        };
        return pattern is not null && pattern.IsMatch(source);
    }

    [GeneratedRegex(@"\b(Pok[eé]mon (X|Y)|X ?/ ?Y|XY|Omega Ruby|Alpha Sapphire|ORAS|Gen ?(VI|6))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceGen6();
    [GeneratedRegex(@"\b(Sun|Moon|USUM|Gen ?(VII|7))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceGen7();
    [GeneratedRegex(@"\b(Sword|Shield|SwSh|SWSH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceSwsh();
    [GeneratedRegex(@"\b(Brilliant Diamond|Shining Pearl|BDSP|BD ?/ ?SP)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceBdsp();
    [GeneratedRegex(@"\b(Scarlet|Violet|SV)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceSv();
    [GeneratedRegex(@"(Legends:? ?Z-?A|\bZ-A\b|\bPLZA\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceZa();
}
