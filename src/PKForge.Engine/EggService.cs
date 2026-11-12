using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>An egg, or a Pokémon that could become one, as the egg tools see it.</summary>
/// <param name="HatchCounter">Egg cycles left (the hatch counter, kept in the friendship byte).</param>
/// <param name="MinHatchCounter">The lowest counter the game can leave on an egg (0, or 1 in Gen 2, BDSP and Scarlet/Violet).</param>
/// <param name="MaxHatchCounter">The counter a fresh egg of this species starts at.</param>
/// <param name="StepsLeft">About how many steps hatch it (cycles × the game's steps per cycle).</param>
/// <param name="MakeEggRefusal">Why this Pokémon cannot be turned into an egg, or null when it can.</param>
public sealed record EggStatus(
    bool IsEgg,
    string Species,
    int Generation,
    int HatchCounter,
    int MinHatchCounter,
    int MaxHatchCounter,
    int StepsLeft,
    IReadOnlyList<int> Moves,
    bool Legal,
    string? MakeEggRefusal);

/// <summary>A move an egg can start with, and why.</summary>
public sealed record EggMoveChoice(int Move, string Name, string Source);

/// <summary>
/// The egg tools, after PKHeX's rules for eggs (<see cref="EggStateLegality"/>, its encounter
/// generator and <c>ForceHatchPKM</c>): the hatch counter within what a real egg of the species
/// can hold, hatching at once with the met data the game writes, the moves an egg can start
/// with, and turning a Pokémon into an egg of its line. Every result is checked with PKHeX's
/// legality analysis; a change that would make the Pokémon illegal is refused, not written.
/// </summary>
public static class EggService
{
    /// <summary>Eggs exist from Gen 3 on here, except in Let's Go, Legends: Arceus and Legends: Z-A.</summary>
    public static bool IsSupported(ISaveEngineSession session) =>
        Save(session) is { Generation: >= 3 } save && HasEggs(save.Context);

    public static EggStatus Get(ISaveEngineSession session, int box, int slot)
    {
        var (_, save, pk) = Open(session, box, slot);
        var moves = new ushort[4];
        pk.GetMoves(moves);
        var legal = new LegalityAnalysis(pk).Valid;
        var name = GameInfo.Strings.specieslist[pk.Species];
        if (!pk.IsEgg)
            return new EggStatus(false, name, pk.Format, 0, 0, 0, 0, [.. moves.Where(m => m != 0).Select(m => (int)m)], legal, MakeEggRefusalFor(save, pk, box, slot));
        var counter = pk.OriginalTrainerFriendship;
        return new EggStatus(true, name, pk.Format, counter, EggStateLegality.GetMinimumEggHatchCycles(pk), EggStateLegality.GetMaximumEggHatchCycles(pk),
            (counter + 1) * StepsPerCycle(pk.Format), [.. moves.Where(m => m != 0).Select(m => (int)m)], legal, "It is already an egg.");
    }

    // ── Hatch counter ────────────────────────────────────────────────────────

    /// <summary>Sets the cycles left; refused outside what a real egg of this species can hold.</summary>
    public static GenerationOutcome SetHatchCounter(ISaveEngineSession session, int box, int slot, int cycles)
    {
        var (engine, _, pk) = Open(session, box, slot);
        if (!pk.IsEgg) return new GenerationOutcome(false, "Only an egg has a hatch counter.");
        var min = EggStateLegality.GetMinimumEggHatchCycles(pk);
        var max = EggStateLegality.GetMaximumEggHatchCycles(pk);
        if (cycles < min || cycles > max)
            return new GenerationOutcome(false, $"A {GameInfo.Strings.specieslist[pk.Species]} egg holds {min} to {max} cycles.");
        pk.OriginalTrainerFriendship = (byte)cycles;
        pk.RefreshChecksum();
        engine.SetEntity(box, slot, pk);
        return new GenerationOutcome(true, cycles <= 1 ? "The egg is about to hatch." : $"The egg hatches in {cycles} cycles.");
    }

    // ── Hatch now ────────────────────────────────────────────────────────────

    /// <summary>
    /// Hatches the egg at once, as the game would: the species name back, the friendship a newly
    /// hatched Pokémon has, the met place and date the game writes for a hatch in this save's game
    /// (PKHeX's ForceHatchPKM), and the Pokédex entry the hatch registers.
    /// </summary>
    public static GenerationOutcome Hatch(ISaveEngineSession session, int box, int slot)
    {
        var (engine, save, pk) = Open(session, box, slot);
        if (!pk.IsEgg) return new GenerationOutcome(false, "This Pokémon is not an egg.");
        // Gen 3 keeps an egg in Japanese until it hatches; the game then writes its own language.
        if (pk.Format == 3 && save.Language > 0) pk.Language = save.Language;
        pk.ForceHatchPKM(save);
        pk.OriginalTrainerFriendship = EggStateLegality.GetEggHatchFriendship(pk.Context);
        if (box == -1) pk.ResetPartyStats();
        pk.RefreshChecksum();
        var legality = new LegalityAnalysis(pk);
        engine.SetEntity(box, slot, pk, registerDex: true);
        var name = GameInfo.Strings.specieslist[pk.Species];
        var where = pk.MetLocation is var location && LocationNames.Of(pk, location, false) is { Length: > 0 } place ? $" at {place}" : "";
        return new GenerationOutcome(true, legality.Valid
            ? $"{name} hatched{where}."
            : $"{name} hatched{where}, but PKHeX finds it illegal: check its met data.");
    }

    // ── Egg moves ────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves an egg of this species can start with in this game: its level-up moves at the egg
    /// level, its egg moves, and the level-up moves a parent can pass on. Whether a given set
    /// works together is PKHeX's call, made by <see cref="SetEggMoves"/>.
    /// </summary>
    public static IReadOnlyList<EggMoveChoice> GetEggMoveChoices(ISaveEngineSession session, int box, int slot)
    {
        var (_, save, pk) = Open(session, box, slot);
        if (!pk.IsEgg) return [];
        var version = EggVersion(save, pk);
        var source = GameData.GetLearnSource(version);
        var names = GameInfo.Strings.movelist;
        var choices = new Dictionary<int, EggMoveChoice>();
        void Add(int move, string why)
        {
            if (move > 0 && move < names.Length && !choices.ContainsKey(move)) choices[move] = new EggMoveChoice(move, names[move], why);
        }
        var level = EggStateLegality.GetEggLevel(pk.Generation is > 0 and var g ? g : save.Generation);
        var learnset = source.GetLearnset(pk.Species, pk.Form);
        foreach (var move in learnset.GetMoveRange(level)) Add(move, $"Level {level}");
        foreach (var move in source.GetEggMoves(pk.Species, pk.Form)) Add(move, "Egg move");
        foreach (var move in source.GetInheritMoves(pk.Species, pk.Form)) Add(move, "From a parent");
        var current = new ushort[4];
        pk.GetMoves(current);
        foreach (var move in current) Add(move, "Known now");
        return [.. choices.Values.OrderBy(c => c.Source switch { "Known now" => 0, "Egg move" => 1, _ when c.Source.StartsWith("Level", StringComparison.Ordinal) => 2, _ => 3 })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gives the egg these moves (up to four); refused when PKHeX finds the set illegal for an egg.</summary>
    public static GenerationOutcome SetEggMoves(ISaveEngineSession session, int box, int slot, IReadOnlyList<int> moves)
    {
        var (engine, _, pk) = Open(session, box, slot);
        if (!pk.IsEgg) return new GenerationOutcome(false, "Only an egg's starting moves are set here; use the move editor for a hatched Pokémon.");
        var set = moves.Where(m => m > 0).Distinct().Take(4).Select(m => (ushort)m).ToArray();
        if (set.Length == 0) return new GenerationOutcome(false, "An egg needs at least one move.");
        var candidate = pk.Clone();
        candidate.SetMoves(set);
        // From Gen 6 the moves an egg starts with are also its relearn moves.
        if (candidate.Format >= 6) candidate.SetRelearnMoves(set);
        candidate.RefreshChecksum();
        var legality = new LegalityAnalysis(candidate);
        var bad = new List<string>();
        for (var i = 0; i < legality.Info.Moves.Length && i < set.Length; i++)
            if (!legality.Info.Moves[i].Valid) bad.Add(GameInfo.Strings.movelist[set[i]]);
        if (bad.Count == 0 && legality.Info.Relearn.Any(r => !r.Valid)) bad.Add("its relearn moves");
        if (bad.Count != 0)
            return new GenerationOutcome(false, $"An egg cannot start with {string.Join(", ", bad)} here. Nothing was changed.");
        engine.SetEntity(box, slot, candidate);
        return new GenerationOutcome(true, $"The egg starts with {string.Join(", ", set.Select(m => GameInfo.Strings.movelist[m]))}.");
    }

    // ── Make an egg ──────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a Pokémon into a fresh egg of its line (Charizard gives a Charmander egg) for the
    /// save's trainer, keeping its nature, IVs, gender and shininess when a legal egg allows them,
    /// and its ball where it can be inherited.
    /// </summary>
    public static GenerationOutcome MakeEgg(ISaveEngineSession session, int box, int slot)
    {
        var (engine, save, pk) = Open(session, box, slot);
        if (MakeEggRefusalFor(save, pk, box, slot) is { } refusal) return new GenerationOutcome(false, refusal);
        var (species, form) = EvolutionTree.GetEvolutionTree(pk.Context).GetBaseSpeciesForm(pk.Species, pk.Form);
        var ivs = new int[6];
        pk.GetIVs(ivs);
        var criteria = new EncounterCriteria
        {
            Nature = pk.Nature,
            Gender = pk.Gender switch { 0 => Gender.Male, 1 => Gender.Female, _ => Gender.Random },
            Shiny = pk.IsShiny ? Shiny.Always : Shiny.Never,
            IV_HP = (sbyte)ivs[0], IV_ATK = (sbyte)ivs[1], IV_DEF = (sbyte)ivs[2],
            IV_SPE = (sbyte)ivs[3], IV_SPA = (sbyte)ivs[4], IV_SPD = (sbyte)ivs[5],
        };
        if (CreateEgg(save, species, form, criteria, out var kept) is not { } egg)
            return new GenerationOutcome(false, $"No legal {GameInfo.Strings.specieslist[species]} egg can be made in this game.");
        var ball = egg.Ball;
        egg.Ball = pk.Ball;
        egg.RefreshChecksum();
        if (!new LegalityAnalysis(egg).Valid) egg.Ball = ball;
        egg.RefreshChecksum();
        engine.SetEntity(box, slot, egg);
        var name = GameInfo.Strings.specieslist[species];
        return new GenerationOutcome(true, kept
            ? $"{GameInfo.Strings.specieslist[pk.Species]} is now a {name} egg, with its nature, IVs and shininess."
            : $"{GameInfo.Strings.specieslist[pk.Species]} is now a {name} egg; a legal egg could not keep all of its nature, IVs and shininess.");
    }

    /// <summary>
    /// A legal, unhatched egg of <paramref name="species"/> for the save's trainer, built from
    /// the game's own egg encounter (as the Day Care hands it over). <paramref name="kept"/> says
    /// whether <paramref name="criteria"/> (nature, IVs, shininess) could be honored; when not,
    /// a plain legal egg is returned. Null when the game has no such egg.
    /// </summary>
    internal static PKM? CreateEgg(SaveFile save, ushort species, byte form, EncounterCriteria criteria, out bool kept)
    {
        kept = false;
        if (!HasEggs(save.Context) || !Breeding.CanHatchAsEgg(species) || !Breeding.CanHatchAsEgg(species, form, save.Context))
            return null;
        var version = save.Version.IsValidSavedVersion() ? save.Version : save.Context.GetSingleGameVersion();
        var template = save.BlankPKM;
        template.Species = species;
        template.Form = form;
        template.CurrentLevel = EggStateLegality.GetEggLevel(save.Generation);
        template.Version = version;
        template.Language = save.Language > 0 ? save.Language : (int)LanguageID.English;
        var encounters = EncounterMovesetGenerator.GenerateEncounters(template, save, ReadOnlyMemory<ushort>.Empty, version)
            .Where(e => e.IsEgg).Take(4).ToArray();
        foreach (var (wanted, attempt) in new[] { (true, criteria), (false, EncounterCriteria.Unrestricted) })
        {
            foreach (var encounter in encounters)
            {
                PKM egg;
                try { egg = encounter.ConvertToPKM(save, attempt); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { continue; }
                Unhatched(egg);
                if (!new LegalityAnalysis(egg).Valid)
                {
                    // Sword/Shield write "Egg" over the species name and keep the rest; Brilliant
                    // Diamond/Shining Pearl clear it: the other way round is tried before giving up.
                    egg.NicknameTrash.Clear();
                    egg.Nickname = SpeciesName.GetEggName(egg.Language, egg.Format);
                    egg.RefreshChecksum();
                    if (!new LegalityAnalysis(egg).Valid) continue;
                }
                kept = wanted && Honors(egg, criteria);
                return egg;
            }
        }
        return null;
    }

    /// <summary>An egg still in the bag, as the Day Care hands it over (the steps AutoMod's egg generator takes).</summary>
    internal static void Unhatched(PKM egg)
    {
        egg.IsEgg = true;
        egg.OriginalTrainerFriendship = (byte)EggStateLegality.GetMaximumEggHatchCycles(egg);
        if (egg.Format >= 4) egg.MetLocation = LocationEdits.GetNoneLocation(egg);
        egg.IsNicknamed = EggStateLegality.IsNicknameFlagSet(egg);
        egg.Nickname = SpeciesName.GetEggName(egg.Language, egg.Format);
        if (egg.Format >= 4) egg.MetDate = null;
        if (egg.Format >= 6) egg.ClearMemories();
        if (egg is PK9) egg.Version = 0;
        egg.RefreshChecksum();
    }

    private static bool Honors(PKM egg, EncounterCriteria criteria)
    {
        if (criteria.Nature != Nature.Random && egg.Nature != criteria.Nature) return false;
        if (criteria.Shiny == Shiny.Always && !egg.IsShiny) return false;
        var ivs = new int[6];
        egg.GetIVs(ivs);
        ReadOnlySpan<int> wanted = [criteria.IV_HP, criteria.IV_ATK, criteria.IV_DEF, criteria.IV_SPE, criteria.IV_SPA, criteria.IV_SPD];
        for (var i = 0; i < 6; i++)
            if (wanted[i] >= 0 && ivs[i] != wanted[i]) return false;
        return true;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private static string? MakeEggRefusalFor(SaveFile save, PKM pk, int box, int slot)
    {
        if (pk.IsEgg) return "It is already an egg.";
        if (!HasEggs(save.Context) || save.Generation < 3) return "This game has no eggs.";
        var (species, form) = EvolutionTree.GetEvolutionTree(pk.Context).GetBaseSpeciesForm(pk.Species, pk.Form);
        if (!Breeding.CanHatchAsEgg(species) || !Breeding.CanHatchAsEgg(species, form, pk.Context))
            return $"{GameInfo.Strings.specieslist[pk.Species]} never comes from an egg.";
        if (box == -1)
        {
            var others = Enumerable.Range(0, save.PartyCount).Where(i => i != slot).Select(save.GetPartySlotAtIndex);
            if (!others.Any(p => p.Species != 0 && !p.IsEgg))
                return "The party needs at least one Pokémon that is not an egg.";
        }
        return null;
    }

    private static GameVersion EggVersion(SaveFile save, PKM pk) =>
        pk.Version.IsValidSavedVersion() ? pk.Version
        : save.Version.IsValidSavedVersion() ? save.Version
        : save.Context.GetSingleGameVersion();

    /// <summary>Steps per egg cycle: 256 up to Gen 3, 255 in Gen 4, 257 after; Scarlet/Violet counts in 128s.</summary>
    private static int StepsPerCycle(int format) => format switch
    {
        <= 3 => 256,
        4 => 255,
        9 => 128,
        _ => 257,
    };

    private static bool HasEggs(EntityContext context) => context is not (EntityContext.Gen7b or EntityContext.Gen8a or EntityContext.Gen9a);

    private static SaveFile? Save(ISaveEngineSession session) => session is SaveEngineSession engine ? engine.SaveFile : null;

    private static (SaveEngineSession Engine, SaveFile Save, PKM Pk) Open(ISaveEngineSession session, int box, int slot)
    {
        if (session is not SaveEngineSession engine) throw new InvalidOperationException("Eggs are edited in retail saves only.");
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0) throw new InvalidOperationException("That slot is empty.");
        return (engine, engine.SaveFile, pk);
    }
}
