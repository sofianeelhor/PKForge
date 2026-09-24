using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A roaming legendary as the save stores it.</summary>
/// <param name="Index">Roamer slot within this game (the order <see cref="RoamerService.GetRoamers"/> lists).</param>
/// <param name="Label">Which roamer this slot is for, e.g. "Latios" or "Raikou".</param>
/// <param name="Species">National species stored in the slot; 0 until the game has released it.</param>
/// <param name="IVs">IVs in app order (HP, Atk, Def, SpA, SpD, Spe); empty when the game stores none (Gen 6).</param>
/// <param name="CatchIVs">IVs the caught Pokémon really gets: Ruby/Sapphire/FireRed/LeafGreen copy
/// only one byte of the stored IVs into the encounter (PKHeX Roamer3.IVsGlitch).</param>
/// <param name="Pid">Stored PID; null for Gen 6, whose roamer is generated at encounter time.</param>
/// <param name="State">Plain-words status ("Roaming", "Not released yet", "Captured"...).</param>
/// <param name="CanReroll">PID/IV can be rerolled here while keeping the catch legal.</param>
/// <param name="Note">What the user should know before rerolling, if anything.</param>
public sealed record RoamerInfo(int Index, string Label, ushort Species, string SpeciesName, int Level,
    IReadOnlyList<int> IVs, IReadOnlyList<int> CatchIVs, uint? Pid, bool IsShiny, string? Nature,
    bool IsActive, string State, bool CanReroll, string? Note, int Generation, uint? TimesEncountered = null);

/// <summary>
/// Roaming legendaries, after PKHeX:
///  - Gen 3: WinForms <c>SAV_Roamer3</c> over <c>Roamer3</c> (LargeBlock.RoamerData; RS 0x3144, E 0x31DC, FRLG 0x30D0).
///  - Gen 4: <c>Roamer4</c> slots exposed by SAV4DP (Mesprit, Cresselia), SAV4Pt (+ Moltres, Zapdos, Articuno)
///    and SAV4HGSS (Raikou, Entei, Latias, Latios). PKHeX has no WinForms form for them; the fields are the same.
///  - Gen 5: <c>Encount5.Roamer1/Roamer2</c> (Black/White Tornadus and Thundurus).
///  - Gen 6: WinForms <c>SAV_Roamer6</c> over <c>Encount6.Roamer</c> (X/Y's Articuno/Zapdos/Moltres).
///
/// Rerolls keep the catch legal:
///  - Gen 3/4 roamers are Method 1 (PKHeX EncounterStatic3.IsRoamerPIDIV "Roamer PID/IV is always Method 1";
///    EncounterStatic4.IsCompatible accepts Method_1). We draw a fresh LCRNG seed and derive PID then IVs
///    exactly as Method 1 does, so <c>MethodFinder.Analyze</c> recovers the seed from the caught Pokémon.
///    RS/FRLG keep only the low IV byte at encounter (Method_1_Roamer); the stored IVs stay the full Method 1
///    value, which is what that check reverses.
///  - Gen 5 roamers have no PID/IV correlation (EncounterStatic5.IsWildCorrelationPID is false when IsRoaming),
///    so PID and IVs are independent random values.
///  - Shiny is only ever produced by searching seeds for one whose own PID is shiny - a result the game's
///    RNG can genuinely produce - never by editing the PID after the fact.
/// </summary>
public static class RoamerService
{
    private const int ShinySearchLimit = 1 << 24;

    public static bool IsSupported(ISaveEngineSession session) => Slots(session).Count != 0;

    public static IReadOnlyList<RoamerInfo> GetRoamers(ISaveEngineSession session)
    {
        var save = ((SaveEngineSession)session).SaveFile;
        return Slots(session).Select((slot, i) => slot.Read(save, i)).ToArray();
    }

    /// <summary>New legal PID/IV (and full HP). <paramref name="shiny"/> searches for a shiny result.</summary>
    public static RoamerInfo Reroll(ISaveEngineSession session, int index, bool shiny)
    {
        var slots = Slots(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, slots.Count);
        var save = ((SaveEngineSession)session).SaveFile;
        var before = slots[index].Read(save, index);
        if (!before.CanReroll)
            throw new InvalidOperationException(before.Note ?? "This roamer cannot be rerolled.");
        slots[index].Reroll(save, shiny);
        return slots[index].Read(save, index);
    }

    /// <summary>X/Y only: the roam state PKHeX's SAV_Roamer6 offers (Inactive, Roaming, Stationary, Defeated, Captured).</summary>
    public static IReadOnlyList<string> Gen6States => ["Inactive", "Roaming", "Stationary", "Defeated", "Captured"];

    public static RoamerInfo SetGen6State(ISaveEngineSession session, int state, uint? timesEncountered = null)
    {
        if (session is not SaveEngineSession { SaveFile: SAV6XY xy })
            throw new NotSupportedException("Roam states can be set only for X and Y.");
        ArgumentOutOfRangeException.ThrowIfNegative(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state, (int)Roamer6State.Captured);
        var roamer = xy.Encount.Roamer;
        roamer.RoamStatus = (Roamer6State)state;
        if (timesEncountered is { } times)
            roamer.TimesEncountered = times;
        return GetRoamers(session)[0];
    }

    // ── Slots ──

    private abstract record Slot(string Label)
    {
        public abstract RoamerInfo Read(SaveFile save, int index);
        public abstract void Reroll(SaveFile save, bool shiny);
    }

    private static IReadOnlyList<Slot> Slots(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engine)
            return [];
        return engine.SaveFile switch
        {
            SAV3RS => [new Gen3Slot("Latios / Latias")],
            SAV3E => [new Gen3Slot("Latios / Latias")],
            SAV3FRLG => [new Gen3Slot("Raikou / Entei / Suicune")],
            SAV4Pt => [new Gen4Slot("Mesprit", s => ((SAV4Pt)s).RoamerMesprit), new Gen4Slot("Cresselia", s => ((SAV4Pt)s).RoamerCresselia),
                new Gen4Slot("Moltres", s => ((SAV4Pt)s).RoamerMoltres), new Gen4Slot("Zapdos", s => ((SAV4Pt)s).RoamerZapdos),
                new Gen4Slot("Articuno", s => ((SAV4Pt)s).RoamerArticuno)],
            SAV4DP => [new Gen4Slot("Mesprit", s => ((SAV4DP)s).RoamerMesprit), new Gen4Slot("Cresselia", s => ((SAV4DP)s).RoamerCresselia)],
            SAV4HGSS => [new Gen4Slot("Raikou", s => ((SAV4HGSS)s).RoamerRaikou), new Gen4Slot("Entei", s => ((SAV4HGSS)s).RoamerEntei),
                new Gen4Slot("Latias", s => ((SAV4HGSS)s).RoamerLatias), new Gen4Slot("Latios", s => ((SAV4HGSS)s).RoamerLatios)],
            SAV5BW => [new Gen5Slot("Roamer 1", s => ((SAV5BW)s).Encount.Roamer1), new Gen5Slot("Roamer 2", s => ((SAV5BW)s).Encount.Roamer2)],
            SAV6XY => [new Gen6Slot()],
            _ => [],
        };
    }

    private sealed record Gen3Slot(string Label) : Slot(Label)
    {
        private static Roamer3 Get(SaveFile save) => new(((SAV3)save).LargeBlock);

        public override RoamerInfo Read(SaveFile save, int index)
        {
            var roamer = Get(save);
            var ivs = AppOrder(roamer.IV_HP, roamer.IV_ATK, roamer.IV_DEF, roamer.IV_SPA, roamer.IV_SPD, roamer.IV_SPE);
            var glitch = roamer.IsGlitched ? AppOrderFromIV32(roamer.IV32 & 0xFF) : ivs;
            var started = roamer.Species != 0;
            return new RoamerInfo(index, Label, roamer.Species, Name(roamer.Species), roamer.CurrentLevel, ivs, glitch,
                roamer.PID, Roamer3.IsShiny(roamer.PID, save), started ? NatureName(roamer.PID % 25) : null, roamer.IsActive,
                !started ? "Not released yet" : roamer.IsActive ? "Roaming" : "Not roaming (caught or fainted)",
                started, started ? GlitchNote(roamer) : "The game has not released this roamer yet (it appears after the Elite Four).", 3);
        }

        private static string? GlitchNote(Roamer3 roamer) => roamer.IsGlitched
            ? "Ruby, Sapphire, FireRed and LeafGreen copy only one byte of these IVs into the battle: the caught Pokémon gets the \"catch IVs\" shown, as PKHeX expects."
            : null;

        public override void Reroll(SaveFile save, bool shiny)
        {
            var roamer = Get(save);
            var (pid, iv32) = Method1(save.ID32, shiny);
            roamer.PID = pid;
            roamer.IV32 = iv32;
            roamer.HP_Current = MaxHp(save, roamer.Species, roamer.CurrentLevel, roamer.IV_HP);
        }
    }

    private sealed record Gen4Slot(string Label, Func<SaveFile, Roamer4> Get) : Slot(Label)
    {
        public override RoamerInfo Read(SaveFile save, int index)
        {
            var roamer = Get(save);
            var started = roamer.Species != 0;
            var ivs = AppOrder(roamer.IV_HP, roamer.IV_ATK, roamer.IV_DEF, roamer.IV_SPA, roamer.IV_SPD, roamer.IV_SPE);
            return new RoamerInfo(index, Label, roamer.Species, Name(roamer.Species), roamer.Level, ivs, ivs, roamer.PID,
                ShinyUtil.GetIsShiny3(save.ID32, roamer.PID), started ? NatureName(roamer.PID % 25) : null, roamer.IsActive,
                !started ? "Not released yet" : roamer.IsActive ? "Roaming" : "Not roaming (caught or fainted)",
                started, started ? null : "The game has not released this roamer yet.", 4);
        }

        public override void Reroll(SaveFile save, bool shiny)
        {
            var roamer = Get(save);
            var (pid, iv32) = Method1(save.ID32, shiny);
            roamer.PID = pid;
            roamer.IV32 = iv32;
            roamer.Stat_HPCurrent = MaxHp(save, roamer.Species, roamer.Level, roamer.IV_HP);
        }
    }

    private sealed record Gen5Slot(string Label, Func<SaveFile, Roamer5> Get) : Slot(Label)
    {
        public override RoamerInfo Read(SaveFile save, int index)
        {
            var roamer = Get(save);
            var started = roamer.Species != 0;
            var ivs = AppOrder(roamer.IV_HP, roamer.IV_ATK, roamer.IV_DEF, roamer.IV_SPA, roamer.IV_SPD, roamer.IV_SPE);
            return new RoamerInfo(index, started ? Name(roamer.Species) : Label, roamer.Species, Name(roamer.Species), roamer.Level, ivs, ivs,
                roamer.PID, ShinyUtil.GetIsShiny3(save.ID32, roamer.PID), started ? NatureName(roamer.Nature) : null, roamer.IsActive,
                !started ? "Not released yet" : roamer.IsActive ? "Roaming" : "Not roaming (caught or fainted)",
                started, started ? null : "Tornadus or Thundurus starts roaming after the rain scene on Route 7.", 5);
        }

        public override void Reroll(SaveFile save, bool shiny)
        {
            var roamer = Get(save);
            var rand = Util.Rand;
            var pid = Util.Rand32();
            for (var i = 0; shiny && !ShinyUtil.GetIsShiny3(save.ID32, pid); i++)
            {
                if (i >= ShinySearchLimit) throw new InvalidOperationException("No shiny PID found.");
                pid = Util.Rand32();
            }
            roamer.PID = pid;
            roamer.SetIVs([rand.Next(32), rand.Next(32), rand.Next(32), rand.Next(32), rand.Next(32), rand.Next(32)]);
            roamer.Stat_HPCurrent = MaxHp(save, roamer.Species, roamer.Level, roamer.IV_HP);
        }
    }

    private sealed record Gen6Slot() : Slot("Kanto bird")
    {
        public override RoamerInfo Read(SaveFile save, int index)
        {
            var roamer = ((SAV6XY)save).Encount.Roamer;
            // SAV_Roamer6.GetInitialIndex: before the League the species is unset and follows the
            // starter choice (EventWork 48): 144 + choice.
            var species = roamer.Species != 0 ? roamer.Species : (ushort)(144 + ((SAV6XY)save).EventWork.GetWork(48));
            var state = (int)roamer.RoamStatus is var s && s < Gen6States.Count ? Gen6States[s] : $"State {s}";
            return new RoamerInfo(index, Name(species), species, Name(species), roamer.CurrentLevel, [], [], null, false, null,
                roamer.RoamStatus is Roamer6State.Roaming or Roamer6State.Stationary, state, false,
                "X and Y create the roaming bird's PID and IVs when you meet it, so the save holds none to reroll. You can set its roam state.",
                6, roamer.TimesEncountered);
        }

        public override void Reroll(SaveFile save, bool shiny) =>
            throw new NotSupportedException("X and Y roamers have no stored PID or IVs.");
    }

    // ── Method 1 ──

    /// <summary>Method 1 from a random seed: PID low, PID high, IV1 (HP/Atk/Def), IV2 (Spe/SpA/SpD) -
    /// the order PKHeX's MethodFinder reverses (GetLCRNGMatch; Method_1_Roamer reads Next3 as IV1).</summary>
    internal static (uint Pid, uint IV32) Method1(uint seed)
    {
        var pidLow = LCRNG.Next16(ref seed);
        var pidHigh = LCRNG.Next16(ref seed);
        var iv1 = LCRNG.Next15(ref seed);
        var iv2 = LCRNG.Next15(ref seed);
        return ((pidHigh << 16) | pidLow, iv1 | (iv2 << 15));
    }

    private static (uint Pid, uint IV32) Method1(uint id32, bool shiny)
    {
        for (var i = 0; i < ShinySearchLimit; i++)
        {
            var result = Method1(Util.Rand32());
            if (!shiny || ShinyUtil.GetIsShiny3(id32, result.Pid))
                return result;
        }
        throw new InvalidOperationException("No shiny Method 1 seed found.");
    }

    private static ushort MaxHp(SaveFile save, ushort species, int level, int ivHp)
    {
        // Gen 3-5 HP with zero EVs: floor((2 * base + IV) * level / 100) + level + 10.
        var baseHp = save.Personal[species].HP;
        return (ushort)(((2 * baseHp + ivHp) * level / 100) + level + 10);
    }

    private static int[] AppOrder(int hp, int atk, int def, int spa, int spd, int spe) => [hp, atk, def, spa, spd, spe];

    /// <summary>IV32 in the Gen 3/4 layout (HP, Atk, Def, Spe, SpA, SpD) to app order.</summary>
    private static int[] AppOrderFromIV32(uint iv32)
    {
        int Iv(int shift) => (int)(iv32 >> shift) & 0x1F;
        return AppOrder(Iv(0), Iv(5), Iv(10), Iv(20), Iv(25), Iv(15));
    }

    private static string Name(ushort species) => species == 0 ? "—" : SpeciesName.GetSpeciesName(species, (int)LanguageID.English);

    private static string NatureName(uint nature) => GameInfo.GetStrings("en").natures[(int)(nature % 25)];
}
