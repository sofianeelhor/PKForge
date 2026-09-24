namespace PKForge.Domain;

/// <summary>The pages of the Pokémon summary, in the order the tabs and page buttons walk them.</summary>
public enum SummaryPage { Info, Stats, Moves, Origin, Legality }

/// <summary>One move slot as the summary's MOVES page shows it: in-game type and category, numbers, PP.</summary>
public sealed record SummaryMove(
    int Id, string Name, int Type, MoveCategory Category, int Power, int Accuracy,
    int PP, int MaxPP, int PPUps, string Effect);

/// <summary>
/// Everything the read-only summary shows about one Pokémon, decoded once from a save
/// slot or a bank entry's own throwaway session. Every section is best-effort: a format
/// that does not keep a field (Gen 1 natures, romhack met data) leaves it null or empty,
/// and the page hides the row instead of inventing a value.
/// </summary>
public sealed record MonSummary(
    int Species,
    int Form,
    string SpeciesName,
    string FormName,
    string Nickname,
    bool IsEgg,
    bool IsShiny,
    int Gender,
    int Level,
    IReadOnlyList<int> Types,
    int Generation,
    string Format,
    // ── Info
    string OriginalTrainer,
    int? TrainerId,
    int? SecretId,
    int? Nature,
    string? NatureName,
    int? Ability,
    string? AbilityName,
    string? AbilityEffect,
    int HeldItem,
    string? HeldItemName,
    string? HeldItemEffect,
    int Ball,
    string BallName,
    int Friendship,
    PokerusInfo? Pokerus,
    IReadOnlyList<CosmeticMarking> Markings,
    // ── Stats
    IReadOnlyList<int> Stats,
    IReadOnlyList<int> BaseStats,
    IReadOnlyList<int> IVs,
    IReadOnlyList<int> EVs,
    TrainingCaps Caps,
    int? HiddenPowerType,
    string? Characteristic,
    string? TeraTypeName,
    int? TeraType,
    // ── Moves
    IReadOnlyList<SummaryMove> Moves,
    IReadOnlyList<string> RelearnMoves,
    // ── Origin / ribbons
    MetInfo? Met,
    int RibbonCount,
    int MarkCount,
    IReadOnlyList<string> RibbonNames,
    // ── Legality (null = not analyzed / not supported)
    bool? Legal,
    IReadOnlyList<string> LegalityLines,
    // ── Per-format extras (form argument, mint, handler, memories, TRs, PID); null when not decoded
    MonFieldSummary? Fields = null,
    // ── Sprite key beyond species/form/shiny (gender art, Alcremie sweet, cosplay)
    SpriteTraits Traits = default)
{
    /// <summary>The sprite key of this Pokémon.</summary>
    public SpriteLook Look => new(Species, Form, IsShiny, Traits);

    /// <summary>The display name: the nickname when it is set, otherwise the species.</summary>
    public string DisplayName => Nickname is { Length: > 0 } nick ? nick : SpeciesName;

    /// <summary>Gen 1/2 carry DVs (0-15) and stat experience instead of IVs/EVs.</summary>
    public bool ClassicTraining => Caps.IvMax == 15;

    /// <summary>The nature's boosted stat (display order HP..Spe), or null for neutral / Gen 1-2.</summary>
    public int? NatureUp => Nature is { } n ? NatureFacts.Raised(n) : null;

    /// <summary>The nature's lowered stat (display order HP..Spe), or null for neutral / Gen 1-2.</summary>
    public int? NatureDown => Nature is { } n ? NatureFacts.Lowered(n) : null;

    /// <summary>Per-slot legality of <see cref="Moves"/>' underlying four move slots (index = move
    /// slot, empty slots included), or null when legality was not analyzed.</summary>
    public MoveLegality? MoveVerdicts { get; init; }

    public int IvTotal => IVs.Sum();
    public int EvTotal => EVs.Sum();
    public int BaseTotal => BaseStats.Sum();

    /// <summary>A copy carrying a legality verdict (the analysis runs after the rest is shown).</summary>
    public MonSummary WithLegality(bool? legal, IReadOnlyList<string> lines) => this with { Legal = legal, LegalityLines = lines };
}

/// <summary>Builds <see cref="MonSummary"/> models from a live engine session.</summary>
public interface IMonSummaryService
{
    /// <summary>The summary of the mon at (box, slot); null for an empty slot.
    /// <paramref name="analyzeLegality"/> runs the (CPU-heavy) legality check too.</summary>
    MonSummary? Build(ISaveEngineSession session, int box, int slot, bool analyzeLegality = false);
}

/// <summary>
/// The summary's L/R walk: the previous or next occupied slot of the same box, skipping
/// empties and wrapping around the ends, like the games' own summary screens.
/// </summary>
public static class SummaryNavigation
{
    /// <summary>
    /// The occupied slot <paramref name="direction"/> steps away from <paramref name="current"/>
    /// in a box of <paramref name="count"/> slots, wrapping; null when no other slot is occupied.
    /// </summary>
    public static int? Step(int current, int direction, int count, Func<int, bool> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        if (count <= 0 || direction == 0) return null;
        var step = Math.Sign(direction);
        var start = Math.Clamp(current, 0, count - 1);
        for (var i = 1; i < count; i++)
        {
            var candidate = ((start + step * i) % count + count) % count;
            if (occupied(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>1-based position of <paramref name="current"/> among the box's occupied slots
    /// and how many there are ("3 / 12" in the summary header). Position 0 when empty.</summary>
    public static (int Position, int Total) Position(int current, int count, Func<int, bool> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        var total = 0;
        var position = 0;
        for (var i = 0; i < count; i++)
        {
            if (!occupied(i)) continue;
            total++;
            if (i == current) position = total;
        }
        return (position, total);
    }

    /// <summary>The page after (or before) <paramref name="page"/>, wrapping.</summary>
    public static SummaryPage Turn(SummaryPage page, int direction)
    {
        var pages = Enum.GetValues<SummaryPage>();
        var index = ((int)page + Math.Sign(direction)) % pages.Length;
        return pages[(index + pages.Length) % pages.Length];
    }
}

/// <summary>
/// The Gen 4+ "characteristic" line ("Likes to run"): the highest IV decides the stat,
/// ties are broken starting from the stat the personality picks (EC in Gen 6+, PID in
/// Gen 4/5), and the IV modulo 5 picks the phrase.
/// </summary>
public static class Characteristics
{
    // Rows in the games' own order: HP, Atk, Def, Spe, SpA, SpD.
    private static readonly string[][] Phrases =
    [
        ["Loves to eat", "Takes plenty of siestas", "Nods off a lot", "Scatters things often", "Likes to relax"],
        ["Proud of its power", "Likes to thrash about", "A little quick tempered", "Likes to fight", "Quick tempered"],
        ["Sturdy body", "Capable of taking hits", "Highly persistent", "Good endurance", "Good perseverance"],
        ["Likes to run", "Alert to sounds", "Impetuous and silly", "Somewhat of a clown", "Quick to flee"],
        ["Highly curious", "Mischievous", "Thoroughly cunning", "Often lost in thought", "Very finicky"],
        ["Strong willed", "Somewhat vain", "Strongly defiant", "Hates to lose", "Somewhat stubborn"],
    ];

    /// <summary>Display order (HP, Atk, Def, SpA, SpD, Spe) index for each games'-order row.</summary>
    private static readonly int[] GameToDisplay = [0, 1, 2, 5, 3, 4];

    /// <param name="ivs">IVs in display order (HP, Atk, Def, SpA, SpD, Spe).</param>
    /// <param name="personality">The encryption constant (Gen 6+) or PID (Gen 4/5).</param>
    /// <returns>The phrase, or null before Gen 4 or for malformed input.</returns>
    public static string? Describe(int generation, IReadOnlyList<int> ivs, uint personality)
    {
        if (generation < 4 || ivs is not { Count: 6 }) return null;
        var start = (int)(personality % 6);
        var best = -1;
        var bestIv = -1;
        for (var i = 0; i < 6; i++)
        {
            var row = (start + i) % 6;
            var iv = ivs[GameToDisplay[row]];
            if (iv > bestIv) { bestIv = iv; best = row; }
        }
        return Phrases[best][bestIv % 5];
    }
}
