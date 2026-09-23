using System.IO.Compression;
using System.Text;

namespace PKForge.Domain;

/// <summary>Damage class of a move: the physical/special split arrived in Gen 4.</summary>
public enum MoveCategory : byte { Status, Physical, Special }

/// <summary>
/// One move's reference numbers (latest-generation values) and short effect.
/// Power 0 = no fixed power (status, variable-power moves); Accuracy 0 = never misses.
/// </summary>
public sealed record MoveFact(int Id, int Type, MoveCategory Category, int Power, int Accuracy, string Effect);

/// <summary>
/// The offline reference table behind every info-rich picker: move numbers and effects,
/// ability and item short descriptions (English). Built deterministically by
/// tools/DexFacts/build.py from PokeAPI's data (github.com/PokeAPI/pokeapi, BSD-3-Clause,
/// © 2014 Paul Hallett and PokeAPI contributors) and embedded in this assembly.
/// Items are keyed by normalised English name because item ids differ per generation.
/// </summary>
public sealed class DexFactsTable
{
    private readonly Dictionary<int, MoveFact> _moves;
    private readonly Dictionary<int, string> _abilities;
    private readonly Dictionary<string, string> _items;

    private DexFactsTable(Dictionary<int, MoveFact> moves, Dictionary<int, string> abilities, Dictionary<string, string> items)
    {
        _moves = moves;
        _abilities = abilities;
        _items = items;
    }

    public int MoveCount => _moves.Count;
    public int AbilityCount => _abilities.Count;
    public int ItemCount => _items.Count;

    public MoveFact? Move(int id) => _moves.GetValueOrDefault(id);

    public string? Ability(int id) => _abilities.GetValueOrDefault(id);

    /// <summary>Short effect for an item by its display name in any generation's table.</summary>
    public string? Item(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _items.GetValueOrDefault(DexFacts.NormalizeName(name));

    /// <summary>Reads the gzip'd TSV the build script writes (malformed rows are skipped).</summary>
    public static DexFactsTable Parse(Stream gzip)
    {
        var moves = new Dictionary<int, MoveFact>();
        var abilities = new Dictionary<int, string>();
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        using var inflate = new GZipStream(gzip, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split('\t');
            switch (f[0])
            {
                case "M" when f.Length >= 7 && int.TryParse(f[1], out var id)
                              && int.TryParse(f[2], out var type) && int.TryParse(f[3], out var cat)
                              && int.TryParse(f[4], out var power) && int.TryParse(f[5], out var acc):
                    moves[id] = new MoveFact(id, type, (MoveCategory)Math.Clamp(cat, 0, 2), power, acc, f[6]);
                    break;
                case "A" when f.Length >= 3 && int.TryParse(f[1], out var ability):
                    abilities[ability] = f[2];
                    break;
                case "I" when f.Length >= 3 && f[1].Length > 0:
                    items[f[1]] = f[2];
                    break;
            }
        }
        return new DexFactsTable(moves, abilities, items);
    }
}

/// <summary>The embedded <see cref="DexFactsTable"/>, loaded once on first use.</summary>
public static class DexFacts
{
    private static readonly Lazy<DexFactsTable> Table = new(Load);

    public static DexFactsTable Default => Table.Value;

    public static MoveFact? Move(int id) => Default.Move(id);
    public static string? Ability(int id) => Default.Ability(id);
    public static string? Item(string? name) => Default.Item(name);

    /// <summary>Lower-case ASCII letters and digits only ("Poké Ball" → "pokeball"); the build script mirrors it.</summary>
    public static string NormalizeName(string name)
    {
        var folded = name.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
            if (char.IsAscii(ch) && char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static DexFactsTable Load()
    {
        using var stream = typeof(DexFacts).Assembly.GetManifestResourceStream("PKForge.Domain.dexfacts.tsv.gz")
            ?? throw new InvalidOperationException("dexfacts.tsv.gz is not embedded");
        return DexFactsTable.Parse(stream);
    }
}

/// <summary>
/// Type ids 0-17 in the games' order (Normal … Fairy; Tera Stellar is 99), their names,
/// and the pre-Gen 4 rule where a damaging move's type decided physical or special.
/// </summary>
public static class TypeFacts
{
    public const int Count = 18;
    public const int Stellar = 99;

    public static readonly IReadOnlyList<string> Names =
    [
        "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
        "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark", "Fairy",
    ];

    public static bool IsValid(int type) => type is >= 0 and < Count || type == Stellar;

    public static string Name(int type) => type == Stellar ? "Stellar" : (uint)type < Count ? Names[type] : "???";

    /// <summary>Gen 1-3: Fire, Water, Grass, Electric, Psychic, Ice, Dragon and Dark were special.</summary>
    public static bool IsSpecialBeforeSplit(int type) => type is >= 9 and <= 16;

    /// <summary>The category a move has in a game of <paramref name="generation"/>.</summary>
    public static MoveCategory CategoryIn(int generation, MoveCategory modern, int typeInGame)
    {
        if (generation >= 4 || modern == MoveCategory.Status) return modern;
        return IsSpecialBeforeSplit(typeInGame) ? MoveCategory.Special : MoveCategory.Physical;
    }

    public static string CategoryName(MoveCategory category) => category switch
    {
        MoveCategory.Physical => "Physical",
        MoveCategory.Special => "Special",
        _ => "Status",
    };
}

/// <summary>How a Pokémon can know a move in the open game, most useful first.</summary>
public enum LearnKind : byte { None, LevelUp, Evolution, Machine, Tutor, Egg, Relearn, Special }

/// <summary>A learn source with its level-up level (0 when the level is not known).</summary>
public readonly record struct MoveLearn(LearnKind Kind, int Level = 0)
{
    public bool IsLegal => Kind != LearnKind.None;

    /// <summary>The picker tag: "Lv 32", "Evo", "TM", "Tutor", "Egg", "Relearn", "Special".</summary>
    public string Label => Kind switch
    {
        LearnKind.LevelUp => Level > 0 ? $"Lv {Level}" : "Lv",
        LearnKind.Evolution => "Evo",
        LearnKind.Machine => "TM",
        LearnKind.Tutor => "Tutor",
        LearnKind.Egg => "Egg",
        LearnKind.Relearn => "Relearn",
        LearnKind.Special => "Special",
        _ => "",
    };

    /// <summary>A sentence for the preview panel ("Learned at level 32").</summary>
    public string Sentence => Kind switch
    {
        LearnKind.LevelUp => Level > 0 ? $"Learned by level-up at Lv {Level}" : "Learned by level-up",
        LearnKind.Evolution => "Learned on evolution",
        LearnKind.Machine => "Taught by TM / TR / HM",
        LearnKind.Tutor => "Taught by a move tutor",
        LearnKind.Egg => "Egg move (breeding)",
        LearnKind.Relearn => "Relearnable / from its encounter",
        LearnKind.Special => "From its encounter or event",
        _ => "Not learnable legally in this game",
    };
}

/// <summary>One row of a move picker: in-game type and PP, reference numbers, learn source.</summary>
public sealed record MoveChoice(int Id, int Type, MoveCategory Category, int Power, int Accuracy, int PP, MoveLearn Learn, string Effect);

/// <summary>An ability the species can have and the slot it sits in ("1", "2", "Hidden").</summary>
public sealed record AbilityChoice(int Id, string Slot, string Effect);

/// <summary>A species' identity card before it is created: types, base stats, abilities, gender ratio.</summary>
public sealed record SpeciesCard(int Species, int Form, IReadOnlyList<int> Types, BaseStats BaseStats,
    IReadOnlyList<AbilityChoice> Abilities, GenderRatio? Gender)
{
    public int Total => BaseStats.Hp + BaseStats.Atk + BaseStats.Def + BaseStats.SpA + BaseStats.SpD + BaseStats.Spe;
}

/// <summary>The games' gender byte: 0 always male, 254 always female, 255 genderless, else female share = value/254.</summary>
public readonly record struct GenderRatio(int Value)
{
    public bool Genderless => Value == 255;
    public bool MaleOnly => Value == 0;
    public bool FemaleOnly => Value == 254;

    /// <summary>Female share in percent (0-100), null when genderless.</summary>
    public double? FemalePercent => Genderless ? null : MaleOnly ? 0 : FemaleOnly ? 100 : Math.Round(Value * 100.0 / 254 / 12.5) * 12.5;

    public string Label => Genderless ? "Genderless"
        : MaleOnly ? "Always ♂" : FemaleOnly ? "Always ♀"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"♂ {100 - FemalePercent!.Value:0.#}% · ♀ {FemalePercent!.Value:0.#}%");
}

/// <summary>
/// Effort points against their caps: Gen 3+ allow 510 in total (252 per stat from Gen 6,
/// 255 before), Gen 1/2 stat experience and Let's Go AVs have no shared total.
/// </summary>
public static class TrainingBudget
{
    public static int? TotalCap(int generation, TrainingCaps caps) =>
        generation >= 3 && caps.EvMax is 252 or 255 ? 510 : null;

    public static int Total(IReadOnlyList<int> values) => values.Sum();

    /// <summary>"508 / 510" style label, or "Total 1200" without a cap.</summary>
    public static string Label(IReadOnlyList<int> values, int? cap) =>
        cap is { } c ? $"Total {Total(values)} / {c}" : $"Total {Total(values)}";

    public static bool IsOver(IReadOnlyList<int> values, int? cap) => cap is { } c && Total(values) > c;
}

/// <summary>What a level edit means: EXP for the level, EXP to the next, and the met-level floor.</summary>
public sealed record LevelInfo(int Level, long ExpAtLevel, long ExpForNext, int MetLevel, IReadOnlyList<int> StatsNow, IReadOnlyList<int> StatsAtLevel)
{
    /// <summary>EXP needed from the start of this level to reach the next (0 at Lv 100).</summary>
    public long ExpToNext => Level >= 100 ? 0 : Math.Max(0, ExpForNext - ExpAtLevel);

    /// <summary>A Pokémon can never be below the level it was met (or hatched) at.</summary>
    public bool BelowMetLevel => MetLevel > 0 && Level < MetLevel;
}

/// <summary>A batch edit rehearsed on copies: what would change and what would break.</summary>
public sealed record BatchDryRun(int Targeted, int Affected, int BecomeIllegal, int AlreadyIllegal);

/// <summary>
/// Read-only facts for the info-rich editors. Nothing here writes: callers apply picks
/// through their usual guarded paths, so Hardcore mode can still refuse the write while
/// the information stays visible.
/// </summary>
public interface IMonInfoService
{
    /// <summary>Every move of the open game with its in-game type/PP and how this mon (or <paramref name="species"/>) learns it.</summary>
    IReadOnlyList<MoveChoice> GetMoveChoices(ISaveEngineSession session, int box, int slot, int? species = null, int? form = null);

    /// <summary>One move's in-game type, category and PP with its reference numbers (no learn data); null for unknown ids.</summary>
    MoveChoice? GetMove(ISaveEngineSession session, int move);

    /// <summary>The move's maximum PP with 0, 1, 2 and 3 PP Ups, by the slot's own format rules.</summary>
    IReadOnlyList<int> GetMaxPPByUps(ISaveEngineSession session, int box, int slot, int move);

    /// <summary>The species' abilities with their slots and descriptions.</summary>
    IReadOnlyList<AbilityChoice> GetAbilityChoices(ISaveEngineSession session, int species, int form);

    /// <summary>Item ids the open game lets a Pokémon hold.</summary>
    IReadOnlyList<int> GetHeldItems(ISaveEngineSession session);

    /// <summary>Hidden Power type (0-17) for IVs in display order, or null where the move does not exist (Gen 1, Gen 8+).</summary>
    int? GetHiddenPowerType(ISaveEngineSession session, IReadOnlyList<int> ivs);

    /// <summary>IVs (display order) closest to <paramref name="ivs"/> that give Hidden Power <paramref name="type"/>; null if impossible.</summary>
    IReadOnlyList<int>? GetIVsForHiddenPower(ISaveEngineSession session, IReadOnlyList<int> ivs, int type);

    /// <summary>EXP and stat effects of moving the slot's mon to <paramref name="level"/>.</summary>
    LevelInfo? GetLevelInfo(ISaveEngineSession session, int box, int slot, int level, StatPreviewOverrides? pending = null);

    /// <summary>Types, base stats, abilities and gender ratio of a species/form in the open game.</summary>
    SpeciesCard? GetSpeciesCard(ISaveEngineSession session, int species, int form);

    /// <summary>Runs batch instructions on copies of the targets; nothing is written.</summary>
    BatchDryRun DryRunBatch(ISaveEngineSession session, IReadOnlyList<string> instructions,
        IReadOnlyList<int>? boxes = null, IReadOnlyList<(int Box, int Slot)>? slots = null);
}

/// <summary>
/// Picker order for moves: legal ones first the way the games list them (level-up by
/// level, then evolution, machines, tutors, eggs, relearn, special), then every other
/// move alphabetically. Names come from the caller's (localised) move table.
/// </summary>
public static class MoveChoiceOrder
{
    public static List<MoveChoice> Sort(IEnumerable<MoveChoice> moves, Func<int, string> name) =>
        moves.OrderBy(m => m.Learn.IsLegal ? 0 : 1)
            .ThenBy(m => m.Learn.IsLegal ? (int)m.Learn.Kind : 0)
            .ThenBy(m => m.Learn.Kind == LearnKind.LevelUp ? m.Learn.Level : 0)
            .ThenBy(m => name(m.Id), StringComparer.OrdinalIgnoreCase)
            .ToList();
}
