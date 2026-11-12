using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One raid list in the save: a Sword/Shield den area or a Scarlet/Violet map.</summary>
/// <param name="Count">Dens or crystals the game actually uses in this area.</param>
public sealed record RaidArea(int Index, string Name, int Count);

/// <summary>One den (Sword/Shield) or Tera crystal spot (Scarlet/Violet) as the editor lists it.</summary>
/// <param name="Special">Fixed by the game (the Watchtower den): bulk actions leave it alone and it cannot be edited here.</param>
/// <param name="WattsReady">Sword/Shield only: the den's watts have not been collected yet.</param>
public sealed record RaidDen(int Area, int Index, bool Active, ulong Seed, string Detail, bool Special, bool WattsReady);

/// <summary>A Scarlet/Violet 7-star raid the save has met, keyed by its distribution date.</summary>
public sealed record SevenStarRaid(int Index, uint Identifier, bool Captured, bool Defeated);

/// <summary>
/// Max Raid dens (Sword/Shield) and Tera raid crystals (Scarlet/Violet), over the same PKHeX.Core
/// lists PKHeX.WinForms SAV_Raid8, SAV_Raid9 and SAV_RaidSevenStar9 edit. The actions are what
/// players want from those screens: light dens up with fresh seeds, put them out, give the watts
/// back, and let a 7-star raid be caught again.
/// </summary>
public static class RaidService
{
    // RaidSpawnList8.ActivateAllRaids skips this Galar den too: the Watchtower den is scripted.
    private const int WatchtowerDen = 16;

    public static bool IsSupported(ISaveEngineSession session) => Save(session) is SAV8SWSH or SAV9SV;

    public static bool HasSevenStar(ISaveEngineSession session) =>
        Save(session) is SAV9SV sv && GetSevenStar(sv).Count != 0;

    public static IReadOnlyList<RaidArea> GetAreas(ISaveEngineSession session)
    {
        var areas = new List<RaidArea>();
        switch (Save(session))
        {
            case SAV8SWSH swsh:
                string[] names = ["Wild Area", "Isle of Armor", "Crown Tundra"];
                for (var i = 0; i < names.Length; i++)
                {
                    // A save from before a DLC has an empty block for it.
                    var list = List8(swsh, i);
                    if (list.CountAll != 0) areas.Add(new RaidArea(i, names[i], Math.Min(list.CountUsed, list.CountAll)));
                }
                break;
            case SAV9SV sv:
                string[] maps = ["Paldea", "Kitakami", "Blueberry Academy"];
                for (var i = 0; i < maps.Length; i++)
                {
                    var list = List9(sv, i);
                    if (list.CountAll != 0) areas.Add(new RaidArea(i, maps[i], Math.Min(list.CountUsed, list.CountAll)));
                }
                break;
        }
        return areas;
    }

    public static IReadOnlyList<RaidDen> GetDens(ISaveEngineSession session, int area)
    {
        var count = Area(session, area).Count;
        return [.. Enumerable.Range(0, count).Select(i => GetDen(session, area, i))];
    }

    public static RaidDen GetDen(ISaveEngineSession session, int area, int index)
    {
        switch (RequireSave(session))
        {
            case SAV8SWSH swsh:
            {
                var den = List8(swsh, area).GetRaid(index);
                var detail = den.IsActive
                    ? $"{den.Stars + 1}★ · {KindName(den.DenType)}{(den.WattsHarvested ? "" : " · watts ready")}"
                    : "No beam";
                return new RaidDen(area, index, den.IsActive, den.Seed, detail, IsWatchtower(area, index),
                    den.IsActive && !den.WattsHarvested);
            }
            case SAV9SV sv:
            {
                var raid = List9(sv, area).GetRaid(index);
                // A spot with no area has no crystal position: the game never fills it.
                var special = raid.AreaID == 0;
                var detail = raid.IsEnabled ? $"{ContentName(raid.Content)} · area {raid.AreaID}" : "No crystal";
                return new RaidDen(area, index, raid.IsEnabled, raid.Seed, detail, special, false);
            }
            default:
                throw Unsupported();
        }
    }

    /// <summary>Lights the den up with a new seed (and, on Sword/Shield, a random star roll),
    /// watts and League Points ready to collect again.</summary>
    public static RaidDen Activate(ISaveEngineSession session, int area, int index)
    {
        RequireEditable(session, area, index);
        Activate(RequireSave(session), area, index, Util.Rand);
        return GetDen(session, area, index);
    }

    /// <summary>Puts the den out (Sword/Shield) or removes the crystal (Scarlet/Violet).</summary>
    public static RaidDen Deactivate(ISaveEngineSession session, int area, int index)
    {
        RequireEditable(session, area, index);
        Deactivate(RequireSave(session), area, index);
        return GetDen(session, area, index);
    }

    /// <summary>Activates every editable den in the area; returns how many.</summary>
    public static int ActivateAll(ISaveEngineSession session, int area)
    {
        var save = RequireSave(session);
        var dens = Editable(session, area);
        foreach (var den in dens)
            Activate(save, area, den.Index, Util.Rand);
        return dens.Count;
    }

    /// <summary>Deactivates every editable den in the area; returns how many.</summary>
    public static int DeactivateAll(ISaveEngineSession session, int area)
    {
        var save = RequireSave(session);
        var dens = Editable(session, area);
        foreach (var den in dens)
            Deactivate(save, area, den.Index);
        return dens.Count;
    }

    public static IReadOnlyList<SevenStarRaid> GetSevenStar(ISaveEngineSession session) =>
        Save(session) is SAV9SV sv ? GetSevenStar(sv) : [];

    /// <summary>Clears captured and defeated so the 7-star raid can be fought and caught again.</summary>
    public static SevenStarRaid ResetSevenStar(ISaveEngineSession session, int index)
    {
        var sv = RequireSevenStarSave(session);
        var raid = sv.RaidSevenStar.GetRaid(index);
        if (raid.Identifier == 0)
            throw new ArgumentOutOfRangeException(nameof(index), "This 7-star raid slot is empty.");
        raid.Captured = false;
        raid.Defeated = false;
        return GetSevenStar(sv).Single(r => r.Index == index);
    }

    /// <summary>Resets every 7-star raid the save has met; returns how many were captured or defeated.</summary>
    public static int ResetAllSevenStar(ISaveEngineSession session)
    {
        var sv = RequireSevenStarSave(session);
        var done = GetSevenStar(sv).Where(r => r.Captured || r.Defeated).ToList();
        foreach (var r in done)
        {
            var raid = sv.RaidSevenStar.GetRaid(r.Index);
            raid.Captured = false;
            raid.Defeated = false;
        }
        return done.Count;
    }

    private static List<SevenStarRaid> GetSevenStar(SAV9SV sv)
    {
        var all = sv.RaidSevenStar;
        var result = new List<SevenStarRaid>();
        for (var i = 0; i < all.CountAll; i++)
        {
            var raid = all.GetRaid(i);
            // Unused slots are zeroed; an identifier is the date the raid was first distributed.
            if (raid.Identifier != 0)
                result.Add(new SevenStarRaid(i, raid.Identifier, raid.Captured, raid.Defeated));
        }
        return result;
    }

    private static void Activate(SaveFile save, int area, int index, Random rnd)
    {
        switch (save)
        {
            case SAV8SWSH swsh:
            {
                var den = List8(swsh, area).GetRaid(index);
                // The same rolls PKHeX's "activate all" makes: stars 0-4, encounter roll 1-100.
                den.Activate((byte)rnd.Next(5), (byte)(1 + rnd.Next(100)));
                den.Seed = (ulong)rnd.NextInt64();
                den.WattsHarvested = false;
                break;
            }
            case SAV9SV sv:
            {
                var raid = List9(sv, area).GetRaid(index);
                raid.IsEnabled = true;
                raid.Seed = (uint)rnd.NextInt64(0, 1L << 32);
                raid.IsClaimedLeaguePoints = false;
                // A distribution or Might raid's content needs event data the save may no longer
                // have; a fresh crystal is a regular one, as the game's daily reroll makes.
                if (raid.Content is TeraRaidContentType.Distribution or TeraRaidContentType.Might7)
                    raid.Content = TeraRaidContentType.Base05;
                break;
            }
        }
    }

    private static void Deactivate(SaveFile save, int area, int index)
    {
        switch (save)
        {
            case SAV8SWSH swsh:
                var den = List8(swsh, area).GetRaid(index);
                den.Deactivate();
                den.WattsHarvested = false;
                break;
            case SAV9SV sv:
                List9(sv, area).GetRaid(index).IsEnabled = false;
                break;
        }
    }

    private static List<RaidDen> Editable(ISaveEngineSession session, int area) =>
        [.. GetDens(session, area).Where(d => !d.Special)];

    private static void RequireEditable(ISaveEngineSession session, int area, int index)
    {
        var count = Area(session, area).Count;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        if (GetDen(session, area, index).Special)
            throw new InvalidOperationException("The game fixes this den itself; it is left as it is.");
    }

    private static RaidArea Area(ISaveEngineSession session, int area) =>
        GetAreas(session).FirstOrDefault(a => a.Index == area)
        ?? throw new ArgumentOutOfRangeException(nameof(area), "This save has no such raid area.");

    private static bool IsWatchtower(int area, int index) => area == 0 && index == WatchtowerDen;

    private static RaidSpawnList8 List8(SAV8SWSH save, int area) => area switch
    {
        0 => save.RaidGalar,
        1 => save.RaidArmor,
        2 => save.RaidCrown,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    private static RaidSpawnList9 List9(SAV9SV save, int area) => area switch
    {
        0 => save.RaidPaldea,
        1 => save.RaidKitakami,
        2 => save.RaidBlueberry,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    private static string KindName(RaidType type) => type switch
    {
        RaidType.Rare => "rare beam",
        RaidType.CommonWish => "Wishing Piece",
        RaidType.RareWish => "rare Wishing Piece",
        RaidType.Event => "event",
        RaidType.DynamaxCrystal => "Dynamax Crystal",
        _ => "common",
    };

    private static string ContentName(TeraRaidContentType content) => content switch
    {
        TeraRaidContentType.Black6 => "6★ black crystal",
        TeraRaidContentType.Distribution => "event crystal",
        TeraRaidContentType.Might7 => "7★ crystal",
        _ => "1-5★ crystal",
    };

    private static SAV9SV RequireSevenStarSave(ISaveEngineSession session) =>
        Save(session) as SAV9SV ?? throw new NotSupportedException("7-star raids exist only in Scarlet and Violet.");

    private static SaveFile RequireSave(ISaveEngineSession session) =>
        Save(session) is { } save && save is SAV8SWSH or SAV9SV ? save : throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("Raids are edited only in Sword/Shield and Scarlet/Violet saves.");

    private static SaveFile? Save(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile : null;
}
