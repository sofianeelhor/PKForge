using System.Text.RegularExpressions;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Entree Forest area (Gen 5 Dream World visitors).</summary>
public sealed record EntreeArea(string Name, bool Unlocked, IReadOnlyList<string> Visitors);

public sealed record FunfestMission(int Index, string Name, bool Unlocked);

public sealed record EntralinkState(
    int WhiteForestLevel,
    int BlackCityLevel,
    int UnlockedAreas,
    bool NinthAreaUnlocked,
    IReadOnlyList<EntreeArea> Areas,
    int VisitorCount,
    bool IsB2W2,
    IReadOnlyList<string> PassPowers,
    IReadOnlyList<FunfestMission> Missions);

/// <summary>
/// Gen 5 Entralink, after PKHeX's WinForms <c>SAV_Misc5</c> (Entralink and Entree Forest tabs):
///  - Entralink levels: <c>SAV5.Entralink.WhiteForestLevel/BlackCityLevel</c> (0-999, the form's NUD range).
///  - Entree Forest: <c>SAV5.EntreeForest</c> - 530 encrypted slots; areas 1-2 are always open,
///    <c>Unlock38Areas</c> opens 3-8 (0-6) and <c>Unlock9thArea</c> the 9th. <c>StartAccess</c> decrypts;
///    SAV5.GetFinalData calls <c>EndAccess</c> before writing, and we end access after every edit too.
///  - B2W2 Pass Powers: <c>Entralink5B2W2.PassPower1-3</c> over <c>PassPower5</c>; Funfest missions:
///    <c>FestaBlock5.IsFunfestMissionUnlocked/UnlockAllFunfestMissions</c>.
///
/// Dream World legality: a Pokémon met in the Entree Forest is legal only when it matches one of PKHeX's
/// <c>EncounterStatic5Entree</c> templates (Encounters5BW.DreamWorld_BW / Encounters5B2W2.DreamWorld_B2W2 +
/// Encounters5DR.DreamWorld_Common: species, form, fixed gender and its one Dream World move). The only
/// forest fill offered is SAV_Misc5.B_RandForest_Click's: every slot drawn from those templates, so
/// anything caught there passes PKHeX. Free-form slot editing is deliberately not offered.
///
/// The C-Gear and Pokédex skins (<c>SAV_DLC5</c>) need image files from the Global Link that are not
/// available offline, so they are not edited here.
/// </summary>
public static class EntralinkService
{
    /// <summary>SAV_Misc5.Designer NUD_EntreeWhiteLV/NUD_EntreeBlackLV Maximum.</summary>
    public const int MaxLevel = 999;

    /// <summary>Areas 1-2 plus <c>Unlock38Areas</c> (max 6): 8 regular areas (SAV_Misc5 NUD_Unlocked 2-8).</summary>
    public const int MaxRegularAreas = 8;

    private static readonly (EntreeForestArea Flag, string Name)[] AreaNames =
    [
        (EntreeForestArea.Deepest, "Deepest area"), (EntreeForestArea.First, "Area 1"), (EntreeForestArea.Second, "Area 2"),
        (EntreeForestArea.Third, "Area 3"), (EntreeForestArea.Fourth, "Area 4"), (EntreeForestArea.Fifth, "Area 5"),
        (EntreeForestArea.Sixth, "Area 6"), (EntreeForestArea.Seventh, "Area 7"), (EntreeForestArea.Eighth, "Area 8"),
        (EntreeForestArea.Ninth, "Area 9"),
    ];

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    public static EntralinkState GetState(ISaveEngineSession session)
    {
        var save = Require(session);
        var forest = save.EntreeForest;
        forest.StartAccess();
        try
        {
            var slots = forest.Slots;
            var unlocked = forest.Unlock38Areas + 2;
            var ninth = forest.Unlock9thArea;
            var areas = AreaNames.Select((area, i) => new EntreeArea(area.Name,
                area.Flag == EntreeForestArea.Deepest || (area.Flag == EntreeForestArea.Ninth ? ninth : i <= unlocked),
                slots.Where(s => (s.Area & area.Flag) != 0 && s.Species != 0).Select(s => Name(s.Species)).ToArray())).ToArray();
            var b2w2 = save as SAV5B2W2;
            IReadOnlyList<string> powers = b2w2 is null ? [] :
            [
                PassPowerName(((Entralink5B2W2)save.Entralink).PassPower1),
                PassPowerName(((Entralink5B2W2)save.Entralink).PassPower2),
                PassPowerName(((Entralink5B2W2)save.Entralink).PassPower3),
            ];
            IReadOnlyList<FunfestMission> missions = b2w2 is null ? [] : Enumerable.Range(0, FestaBlock5.MaxMissionIndex + 1)
                .Select(i => new FunfestMission(i, Pretty(((Funfest5Mission)i).ToString()), b2w2.Festa.IsFunfestMissionUnlocked(i))).ToArray();
            return new EntralinkState(save.Entralink.WhiteForestLevel, save.Entralink.BlackCityLevel, unlocked, ninth, areas,
                slots.Count(s => s.Species != 0), b2w2 is not null, powers, missions);
        }
        finally
        {
            forest.EndAccess();
        }
    }

    public static EntralinkState SetLevels(ISaveEngineSession session, int whiteForest, int blackCity)
    {
        var save = Require(session);
        save.Entralink.WhiteForestLevel = (ushort)Math.Clamp(whiteForest, 0, MaxLevel);
        save.Entralink.BlackCityLevel = (ushort)Math.Clamp(blackCity, 0, MaxLevel);
        return GetState(session);
    }

    /// <summary>EntreeForest.UnlockAllAreas: areas 3-8 and the 9th area open.</summary>
    public static EntralinkState UnlockAllAreas(ISaveEngineSession session)
    {
        var forest = Require(session).EntreeForest;
        forest.StartAccess();
        forest.UnlockAllAreas();
        forest.EndAccess();
        return GetState(session);
    }

    /// <summary>SAV_Misc5.B_RandForest_Click: every slot filled from the legal Dream World templates
    /// (no template used twice), a random learnable Dream World move each, and all areas open.</summary>
    public static EntralinkState FillForestLegally(ISaveEngineSession session)
    {
        var save = Require(session);
        var source = (save is SAV5BW ? Encounters5BW.DreamWorld_BW : Encounters5B2W2.DreamWorld_B2W2)
            .Concat(Encounters5DR.DreamWorld_Common).ToList();
        var rnd = Util.Rand;
        var forest = save.EntreeForest;
        forest.StartAccess();
        foreach (var slot in forest.Slots)
        {
            // PKHeX removes each template it uses; 530 slots outnumber the templates on no game, but guard anyway.
            if (source.Count == 0) { slot.Delete(); continue; }
            var template = source[rnd.Next(source.Count)];
            source.Remove(template);
            slot.Species = template.Species;
            slot.Form = template.Form;
            slot.Gender = !((IFixedGender)template).IsFixedGender ? PersonalTable.B2W2[template.Species].RandomGender() : template.Gender;
            ReadOnlySpan<ushort> moves = template.Moves;
            var count = moves.Length - moves.Count<ushort>(0);
            slot.Move = count == 0 ? (ushort)0 : moves[rnd.Next(count)];
        }
        forest.UnlockAllAreas();
        forest.EndAccess();
        return GetState(session);
    }

    /// <summary>EntreeForest.DeleteAll: an empty forest.</summary>
    public static EntralinkState ClearForest(ISaveEngineSession session)
    {
        var forest = Require(session).EntreeForest;
        forest.StartAccess();
        forest.DeleteAll();
        forest.EndAccess();
        return GetState(session);
    }

    /// <summary>The Pass Powers a B2W2 slot can hold, in <c>PassPower5</c> order.</summary>
    public static IReadOnlyList<(int Value, string Name)> GetPassPowerChoices() =>
        Enum.GetValues<PassPower5>().Select(p => ((int)p, PassPowerName((byte)p))).ToArray();

    public static EntralinkState SetPassPower(ISaveEngineSession session, int slot, int power)
    {
        if (Require(session) is not SAV5B2W2 b2w2)
            throw new NotSupportedException("Pass Powers exist only in Black 2 and White 2.");
        if (!Enum.IsDefined((PassPower5)power))
            throw new ArgumentOutOfRangeException(nameof(power));
        var entralink = (Entralink5B2W2)b2w2.Entralink;
        switch (slot)
        {
            case 0: entralink.PassPower1 = (byte)power; break;
            case 1: entralink.PassPower2 = (byte)power; break;
            case 2: entralink.PassPower3 = (byte)power; break;
            default: throw new ArgumentOutOfRangeException(nameof(slot));
        }
        return GetState(session);
    }

    /// <summary>SAV_Misc5.B_FunfestMissions_Click: <c>FestaBlock5.UnlockAllFunfestMissions</c>
    /// (sets the Funfest flag and each mission's prerequisite event flags).</summary>
    public static EntralinkState UnlockAllMissions(ISaveEngineSession session)
    {
        if (Require(session) is not SAV5B2W2 b2w2)
            throw new NotSupportedException("Funfest missions to unlock exist only in Black 2 and White 2.");
        b2w2.Festa.UnlockAllFunfestMissions();
        return GetState(session);
    }

    /// <summary>"EncounterPlus2" → "Encounter Power ↑↑", "CaptureMAX" → "Capture Power MAX".</summary>
    public static string PassPowerName(byte value)
    {
        var power = (PassPower5)value;
        if (!Enum.IsDefined(power))
            return $"Unknown ({value})";
        if (power == PassPower5.None)
            return "None";
        var name = power.ToString();
        var suffix = "";
        foreach (var (tail, label) in (ReadOnlySpan<(string, string)>)[("Plus1", "↑"), ("Plus2", "↑↑"), ("Plus3", "↑↑↑"),
                     ("Negative1", "↓"), ("Negative2", "↓↓"), ("Negative3", "↓↓↓"), ("MAX", "MAX"), ("S", "S")])
        {
            if (!name.EndsWith(tail, StringComparison.Ordinal)) continue;
            name = name[..^tail.Length];
            suffix = label;
            break;
        }
        var words = Pretty(name) switch
        {
            "HP" => "HP Restoring",
            "HP Full Recovery" => "Full Recovery",
            "PP" => "PP Restoring",
            var other => other,
        };
        return $"{words} Power {suffix}".TrimEnd();
    }

    private static string Pretty(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    private static string Name(ushort species) => SpeciesName.GetSpeciesName(species, (int)LanguageID.English);

    private static SAV5? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV5 : null;

    private static SAV5 Require(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("The Entralink exists only in Black, White, Black 2 and White 2.");
}
