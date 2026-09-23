namespace PKForge.Domain;

/// <summary>
/// Nature game data (Gen 3+): which stat each nature raises and lowers by 10%, and the
/// berry flavours it likes and dislikes. Stat indexes use the app's display order
/// HP/Atk/Def/SpA/SpD/Spe (0-5). The mapping follows the games' nature id layout,
/// raised = id / 5 and lowered = id % 5 over Atk/Def/Spe/SpA/SpD - the same table
/// as PKHeX's NatureAmp (verified against it in the engine tests).
/// </summary>
public static class NatureFacts
{
    public const int Count = 25;

    /// <summary>Display names in display stat order (HP/Atk/Def/SpA/SpD/Spe).</summary>
    public static readonly IReadOnlyList<string> StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    // The games' internal amp order (Atk, Def, Spe, SpA, SpD) mapped to display indexes.
    private static readonly int[] AmpToDisplay = [1, 2, 5, 3, 4];

    // Flavour liked by the stat a nature raises (disliked by the one it lowers), amp order.
    private static readonly string[] Flavors = ["Spicy", "Sour", "Sweet", "Dry", "Bitter"];

    public static bool IsValid(int nature) => nature is >= 0 and < Count;

    /// <summary>Neutral natures (Hardy, Docile, Serious, Bashful, Quirky) raise and lower the same stat.</summary>
    public static bool IsNeutral(int nature) => IsValid(nature) && nature / 5 == nature % 5;

    /// <summary>Display index of the raised stat, or null for neutral/invalid natures.</summary>
    public static int? Raised(int nature) => IsValid(nature) && !IsNeutral(nature) ? AmpToDisplay[nature / 5] : null;

    /// <summary>Display index of the lowered stat, or null for neutral/invalid natures.</summary>
    public static int? Lowered(int nature) => IsValid(nature) && !IsNeutral(nature) ? AmpToDisplay[nature % 5] : null;

    /// <summary>Stat multiplier in tenths (11 = +10%, 9 = -10%, 10 = none) for a display stat index.</summary>
    public static int Multiplier(int nature, int displayStat) =>
        Raised(nature) == displayStat ? 11 : Lowered(nature) == displayStat ? 9 : 10;

    /// <summary>"+Atk −SpA", or "neutral".</summary>
    public static string EffectLabel(int nature) =>
        Raised(nature) is { } up && Lowered(nature) is { } down
            ? $"+{StatNames[up]} −{StatNames[down]}"
            : IsValid(nature) ? "neutral" : "";

    /// <summary>"likes Spicy · dislikes Dry"; neutral natures have no preference.</summary>
    public static string FlavorLabel(int nature) =>
        !IsValid(nature) ? ""
        : IsNeutral(nature) ? "no flavor preference"
        : $"likes {Flavors[nature / 5]} · dislikes {Flavors[nature % 5]}";
}

/// <summary>
/// The Gen 3+ battle-stat formula, for previews that have no stored Pokémon behind them
/// (the generate wizard) or sessions without a PKHeX entity (romhacks). Stored mons use
/// the engine's own per-format stat calculation instead.
/// </summary>
public static class NatureStatMath
{
    /// <summary>Stats in display order (HP/Atk/Def/SpA/SpD/Spe).</summary>
    public static int[] Compute(BaseStats baseStats, int level, IReadOnlyList<int> ivs, IReadOnlyList<int> evs, int nature)
    {
        ArgumentNullException.ThrowIfNull(baseStats);
        level = Math.Clamp(level, 1, 100);
        int[] bases = [baseStats.Hp, baseStats.Atk, baseStats.Def, baseStats.SpA, baseStats.SpD, baseStats.Spe];
        var result = new int[6];
        for (var i = 0; i < 6; i++)
        {
            var iv = i < ivs.Count ? ivs[i] : 0;
            var ev = i < evs.Count ? evs[i] : 0;
            var core = (2 * bases[i] + iv + ev / 4) * level / 100;
            result[i] = i == 0
                ? bases[0] == 1 ? 1 : core + level + 10 // Shedinja is always 1 HP
                : (core + 5) * NatureFacts.Multiplier(nature, i) / 10;
        }
        return result;
    }

    /// <summary>A preview for every nature from the plain formula.</summary>
    public static NatureStatPreview Preview(BaseStats baseStats, int level, IReadOnlyList<int> ivs, IReadOnlyList<int> evs,
        int currentNature, string basis)
    {
        var byNature = Enumerable.Range(0, NatureFacts.Count)
            .Select(n => (IReadOnlyList<int>)Compute(baseStats, level, ivs, evs, n)).ToArray();
        var current = NatureFacts.IsValid(currentNature) ? byNature[currentNature] : Compute(baseStats, level, ivs, evs, 0);
        return new NatureStatPreview(currentNature, null, basis, current, byNature);
    }
}

/// <summary>
/// A Pokémon's battle stats now and after picking each nature, in display order
/// (HP/Atk/Def/SpA/SpD/Spe). <paramref name="StatNatureLock"/> is set on Gen 8+ mons
/// whose stats follow a mint that differs from their nature: picking a nature then
/// leaves the stats alone. <paramref name="Basis"/> says what the numbers assume
/// ("Lv.50 · stored IVs/EVs").
/// </summary>
public sealed record NatureStatPreview(
    int CurrentNature,
    int? StatNatureLock,
    string Basis,
    IReadOnlyList<int> Current,
    IReadOnlyList<IReadOnlyList<int>> ByNature)
{
    public IReadOnlyList<int> StatsFor(int nature) => NatureFacts.IsValid(nature) && nature < ByNature.Count ? ByNature[nature] : Current;

    /// <summary>Per-stat change from the current stats if <paramref name="nature"/> were picked.</summary>
    public IReadOnlyList<int> DeltaFor(int nature)
    {
        var next = StatsFor(nature);
        return Enumerable.Range(0, 6).Select(i => next[i] - Current[i]).ToArray();
    }
}

/// <summary>Pending, unsaved editor values the preview should use instead of the stored ones.</summary>
public sealed record StatPreviewOverrides(
    int? Species = null,
    int? Level = null,
    IReadOnlyList<int>? IVs = null,
    IReadOnlyList<int>? EVs = null);

/// <summary>
/// Live "what would this nature do" numbers for the nature pickers. Pure reads: the
/// stored Pokémon is never touched (engine sessions compute on a clone).
/// </summary>
public interface IStatPreviewService
{
    /// <summary>Preview for a stored mon; null for empty slots and Gen 1/2 (no natures).</summary>
    NatureStatPreview? PreviewSlot(ISaveEngineSession session, int box, int slot, StatPreviewOverrides? overrides = null);

    /// <summary>Preview for a mon that does not exist yet (generation): 31 IVs, 0 EVs.
    /// Null on Gen 1/2 saves.</summary>
    NatureStatPreview? PreviewSpecies(ISaveEngineSession session, int species, int form, int level);
}
