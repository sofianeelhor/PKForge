using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// Species traits a CFRU hack shares with the modern national tables: base stats,
/// types, gender ratio, abilities and growth curve, all resolved through a NATIONAL
/// species id (each hack bridges its own ids first). An unknown species (national 0:
/// fakemon, form slots the bridge misses) degrades to neutral fallbacks instead of
/// borrowing another species' data.
/// </summary>
internal static class CfruNationalTraits
{
    private static PersonalInfo? PersonalOf(int national) =>
        national > 0 ? PersonalTable.SV.GetFormEntry((ushort)national, 0) : null;

    /// <summary>Base stats in app order: HP, Atk, Def, SpA, SpD, Spe.</summary>
    public static int[] BaseStats(int national)
    {
        var info = PersonalOf(national);
        return info is null ? [50, 50, 50, 50, 50, 50] : [info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE];
    }

    /// <summary>Primary + secondary type ids (modern national numbering).</summary>
    public static int[] TypesOf(int national)
    {
        var info = PersonalOf(national);
        return info is null ? [0] : [info.Type1, info.Type2];
    }

    /// <summary>Gender from the PID low byte and the national gender ratio:
    /// 0 male, 1 female, 2 genderless.</summary>
    public static int GenderOf(uint pid, int national)
    {
        var info = PersonalOf(national);
        if (info is null) return 2;
        return info.Gender switch
        {
            255 => 2,
            254 => 1,
            0 => 0,
            var threshold => (pid & 0xFF) < (uint)threshold ? 1 : 0,
        };
    }

    /// <summary>The gender threshold for the PID solver (0 male-only, 254/255 special).</summary>
    public static int GenderThreshold(int national) => PersonalOf(national)?.Gender ?? 255;

    /// <summary>Ability ids (modern numbering): slot 1, slot 2 (0 when the species has
    /// one ability), hidden. Slot selection in the save is the PID's low bit plus the
    /// hidden-ability flag in the IV word.</summary>
    public static (int A1, int A2, int Hidden) AbilityIds(int national)
    {
        var info = PersonalOf(national);
        if (info is null) return (0, 0, 0);
        var a1 = info.GetAbilityAtIndex(0);
        var a2 = info.AbilityCount > 1 ? info.GetAbilityAtIndex(1) : 0;
        var hidden = info.AbilityCount > 2 ? info.GetAbilityAtIndex(2) : a1;
        return (a1, a2, hidden);
    }

    /// <summary>The ability actually active on a mon, honoring the hidden-ability flag.</summary>
    public static int ActiveAbility(RadicalRedMon mon, int national)
    {
        var (a1, a2, hidden) = AbilityIds(national);
        if (mon.HiddenAbility) return hidden;
        return (mon.Pid & 1) == 1 && a2 != 0 ? a2 : a1;
    }

    public static uint ExperienceAtLevel(int national, int level) =>
        Experience.GetEXP((byte)Math.Clamp(level, 1, 100), PersonalOf(national)?.EXPGrowth ?? 0);

    public static int LevelForExperience(int national, uint experience)
    {
        var info = PersonalOf(national);
        if (info is null)
            return experience >= 1_000_000 ? 100 : 50; // unknown growth: rough cubic estimate
        return Experience.GetLevel(experience, info.EXPGrowth);
    }

    /// <summary>Battle stats for a PC mon (party mons carry theirs in the save tail).</summary>
    public static int[] ComputeStats(RadicalRedMon mon, int national)
    {
        var baseStats = BaseStats(national);
        var ivs = mon.IVs;
        var evs = mon.EVs;
        var level = mon.Level;

        static int NatureBoost(int nature, int storageIndex)
        {
            // G3 nature table over the non-HP storage stats (Atk=1, Def=2, Spe=3,
            // SpA=4, SpD=5): nature/5 is the boosted row, nature%5 the dropped
            // column; equal indices are the five neutral natures.
            var up = nature / 5 + 1;
            var down = nature % 5 + 1;
            if (up == down) return 100;
            if (storageIndex == up) return 110;
            if (storageIndex == down) return 90;
            return 100;
        }

        var result = new int[6];
        result[0] = (2 * baseStats[0] + ivs[0] + evs[0] / 4) * level / 100 + level + 10;
        for (var i = 1; i < 6; i++)
        {
            // App order (HP, Atk, Def, SpA, SpD, Spe) -> storage order (Spe=3, SpA=4, SpD=5).
            var storageIndex = i is 3 ? 4 : i is 5 ? 3 : i;
            var raw = (2 * baseStats[i] + ivs[i] + evs[i] / 4) * level / 100 + 5;
            result[i] = raw * NatureBoost(mon.Nature, storageIndex) / 100;
        }
        return result;
    }
}
