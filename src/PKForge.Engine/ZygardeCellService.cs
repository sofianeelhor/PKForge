using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Zygarde Cell or Core spot, in the save's order.</summary>
/// <param name="State">0 not placed yet, 1 available in the world, 2 picked up.</param>
public sealed record ZygardeCellSpot(int Index, string Location, int State)
{
    public bool IsCollected => State == ZygardeCellService.StateReceived;
}

/// <summary>The Zygarde Cell counters and every spot. <paramref name="Total"/> counts every
/// cell ever collected (cells handed to Zygarde included); <paramref name="Collected"/> is the
/// game's "cells in the Zygarde Cube" counter.</summary>
public sealed record ZygardeCellSummary(int Collected, int Total, IReadOnlyList<ZygardeCellSpot> Spots)
{
    public int PickedUp => Spots.Count(s => s.IsCollected);
}

/// <summary>
/// Zygarde Cells for Sun/Moon (95 spots) and Ultra Sun/Ultra Moon (100 spots), after PKHeX's
/// WinForms <c>SAV_ZygardeCell</c>: spot states and both counters live in <c>EventWork7</c>;
/// US/UM also mirror the collected count into record 72, as the form's save does. PKHeX does not
/// tell Cells and Cores apart, so neither do we. Location names are the form's own lists.
/// </summary>
public static class ZygardeCellService
{
    public const int StateNone = 0;
    public const int StateAvailable = 1;
    public const int StateReceived = 2;
    public const int MaxCounter = ushort.MaxValue;

    /// <summary>SAV_ZygardeCell: record 72 holds the cell count in Ultra Sun/Ultra Moon.</summary>
    public const int RecordCellsUsum = 72;

    public static readonly IReadOnlyList<string> StateNames = ["Not placed", "Available", "Picked up"];

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    public static ZygardeCellSummary Get(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var ew = save.EventWork;
        var names = save is SAV7USUM ? LocationsUSUM : LocationsSM;
        var spots = new ZygardeCellSpot[ew.TotalZygardeCellCount];
        for (var i = 0; i < spots.Length; i++)
            spots[i] = new ZygardeCellSpot(i, i < names.Length ? names[i] : $"Spot {i + 1}", ew.GetZygardeCell(i));
        return new ZygardeCellSummary(ew.ZygardeCellCount, ew.ZygardeCellTotal, spots);
    }

    /// <summary>
    /// SAV_ZygardeCell.B_GiveAll_Click then save: every spot becomes "picked up", the collected
    /// counter grows by the spots that were not, and so does the total in Sun/Moon (US/UM keep
    /// the total, and record 72 follows the collected count). Returns how many were added.
    /// </summary>
    public static int CollectAll(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var ew = save.EventWork;
        var added = 0;
        for (var i = 0; i < ew.TotalZygardeCellCount; i++)
        {
            if (ew.GetZygardeCell(i) == StateReceived) continue;
            ew.SetZygardeCell(i, StateReceived);
            added++;
        }
        ew.ZygardeCellCount = Clamp(ew.ZygardeCellCount + added);
        if (save is not SAV7USUM)
            ew.ZygardeCellTotal = Clamp(ew.ZygardeCellTotal + added);
        SyncRecord(save);
        return added;
    }

    /// <summary>
    /// The reverse: every picked-up spot is put back as available in the world (never "not
    /// placed", which the story may not set again), and the counters drop by as many, floored at
    /// zero. Returns how many spots were put back.
    /// </summary>
    public static int Reset(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var ew = save.EventWork;
        var removed = 0;
        for (var i = 0; i < ew.TotalZygardeCellCount; i++)
        {
            if (ew.GetZygardeCell(i) != StateReceived) continue;
            ew.SetZygardeCell(i, StateAvailable);
            removed++;
        }
        ew.ZygardeCellCount = Clamp(ew.ZygardeCellCount - removed);
        if (save is not SAV7USUM)
            ew.ZygardeCellTotal = Clamp(ew.ZygardeCellTotal - removed);
        SyncRecord(save);
        return removed;
    }

    /// <summary>One spot's state (SAV_ZygardeCell's three choices); the counters are left alone, as in the form.</summary>
    public static void SetState(ISaveEngineSession session, int index, int state)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, save.EventWork.TotalZygardeCellCount);
        ArgumentOutOfRangeException.ThrowIfNegative(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state, StateReceived);
        save.EventWork.SetZygardeCell(index, (ushort)state);
    }

    /// <summary>Sets both counters (SAV_ZygardeCell's two number boxes), clamped to a u16.</summary>
    public static void SetCounters(ISaveEngineSession session, int collected, int total)
    {
        var save = RequireSave(session);
        save.EventWork.ZygardeCellCount = Clamp(collected);
        save.EventWork.ZygardeCellTotal = Clamp(total);
        SyncRecord(save);
    }

    private static void SyncRecord(SAV7 save)
    {
        if (save is SAV7USUM)
            save.SetRecord(RecordCellsUsum, save.EventWork.ZygardeCellCount);
    }

    private static ushort Clamp(int value) => (ushort)Math.Clamp(value, 0, MaxCounter);

    private static SAV7? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV7 : null;

    private static SAV7 RequireSave(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("Zygarde Cells exist only in Sun, Moon, Ultra Sun and Ultra Moon saves.");

    // Spot names verbatim from PKHeX's SAV_ZygardeCell, index = save order.
    private static readonly string[] LocationsSM =
    [
        "Verdant Cave - Trial Site",
        "Ruins of Conflict - Outside",
        "Route 1 (Day)",
        "Route 3",
        "Route 3 (Day)",
        "Kala'e Bay",
        "Hau'oli Cemetery",
        "Route 2",
        "Route 1 - Trainer School (Night)",
        "Hau'oli City - Shopping District",
        "Route 1 - Outskirts",
        "Hau'oli City - Shopping District (Night)",
        "Route 1",
        "Iki Town (Night)",
        "Route 4",
        "Paniola Ranch (Night)",
        "Paniola Ranch (Day)",
        "Wela Volcano Park - Top",
        "Lush Jungle - Cave",
        "Route 7",
        "Akala Outskirts",
        "Royal Avenue (Day)",
        "Royal Avenue (Night)",
        "Konikoni City (Night)",
        "Heahea City (Night)",
        "Route 8",
        "Route 8 (Day)",
        "Route 5",
        "Hano Beach (Day)",
        "Heahea City",
        "Diglett's Tunnel",
        "Hano Beach",
        "Malie Garden",
        "Malie City - Community Center (Night)",
        "Malie City (Day)",
        "Malie City - Outer Cape (Day)",
        "Route 11 (Night)",
        "Route 12 (Day)",
        "Route 12",
        "Secluded Shore (Night)",
        "Blush Mountain",
        "Route 13",
        "Haina Desert",
        "Ruins of Abundance - Outside",
        "Route 14",
        "Route 14 (Night)",
        "Tapu Village",
        "Route 15",
        "Aether House (Day)",
        "Ula'ula Meadow - Boardwalk",
        "Route 16 (Day)",
        "Ula'ula Meadow - Grass",
        "Route 17 - Building",
        "Route 17 - Ledge",
        "Po Town (Night)",
        "Route 10 (Day)",
        "Hokulani Observatory (Night)",
        "Mount Lanakila - Mountainside",
        "Mount Lanakila - High Mountainside",
        "Secluded Shore (Day)",
        "Route 13 (Night)",
        "Po Town (Day)",
        "Seafolk Village - Blue Food Boat",
        "Seafolk Village - Unbuilt House",
        "Poni Wilds (Day)",
        "Poni Wilds (Night)",
        "Poni Wilds",
        "Ancient Poni Path - Near Well (Day)",
        "Ancient Poni Path (Night)",
        "Poni Breaker Coast (Day)",
        "Ruins of Hope",
        "Poni Grove - Mountain Corner",
        "Poni Grove - Near a Bush",
        "Poni Plains (Day)",
        "Poni Plains (Night)",
        "Poni Plains",
        "Poni Meadow",
        "Poni Coast (Night)",
        "Poni Coast",
        "Poni Gauntlet - On Bridge",
        "Poni Gauntlet - Island w/ Trainer",
        "Resolution Cave - 1F (Day)",
        "Resolution Cave - B1F (Night)",
        "Vast Poni Canyon - 3F",
        "Vast Poni Canyon - 2F",
        "Vast Poni Canyon - Top",
        "Vast Poni Canyon - Inside",
        "Ancient Poni Path - Brickwall (Day)",
        "Poni Breaker Coast (Night)",
        "Resolution Cave - B1F",
        "Aether Foundation B2F - Right Hallway",
        "Aether Foundation 1F - Outside - Right Side",
        "Aether Foundation 1F - Outside (Day)",
        "Aether Foundation 1F - Entrance (Night)",
        "Aether Foundation 1F - Main Building",
    ];

    private static readonly string[] LocationsUSUM =
    [
        "Hau'oli City (Shopping) - Salon (Outside)",
        "Hau'oli City (Shopping) - Malasada Shop (Outside)",
        "Hau'oli City (Shopping) - Ilima's House (2F)",
        "Malie City - Library (1F)",
        "Hau'oli City (Marina) - Pier",
        "Route 2 - Southeast House",
        "Hau'oli City (Shopping) - Ilima's House (Outside)",
        "Hau'oli City (Shopping) - City Hall",
        "Heahea City - Hotel (3F)",
        "Route 2 - Berry Fields House",
        "Route 2 - Berry Fields House (Outside)",
        "Royal Avenue - Northeast",
        "Hau'oli City (Shopping) - Pokemon Center (Outside)",
        "Royal Avenue - South",
        "Hokulani Observatory - Room",
        "Hokulani Observatory - Reception",
        "Hau'oli City (Shopping) - City Hall (Outside)",
        "Konikoni City - Olivia's Jewelry Shop (2F)",
        "Heahea City - Surfboard (Outside)",
        "Po Town - Southwest",
        "Hano Resort Lobby - Southwest Water",
        "Hau'oli City (Shopping) - Northwest of Police Station",
        "Hau'oli City (Marina) - Ferry Terminal (Outside)",
        "Route 2 - Southeast House (Outside)",
        "Route 2 - Pokemon Center (Outside)",
        "Heahea City - West",
        "Heahea City - Hotel West (Outside)",
        "Heahea City - Hotel East (Outside)",
        "Heahea City - Research Lab East (Outside)",
        "Heahea City - Research Lab South (Outside)",
        "Heahea City - Game Freak",
        "Hokulani Observatory - Dead End",
        "Heahea City - Game Freak Building (3F)",
        "Heahea City - Research Lab",
        "Heahea City - Hotel (1F)",
        "Battle Royal Dome - 2F",
        "Paniola Town - West",
        "Paniola Town - Kiawe's House (1F)",
        "Paniola Town - Kiawe's House (2F)",
        "Paniola Ranch - Northwest",
        "Paniola Ranch - Southeast",
        "Hano Beach",
        "Hano Resort - South",
        "Hano Resort - North",
        "Konikoni City Lighthouse (Through Diglett's Tunnel)",
        "Battle Royal Dome - 1F",
        "Route 8 - Aether Base (Outside)",
        "Route 8 - Fossil Restoration Center (Outside)",
        "Konikoni City - West",
        "Konikoni City - Restaurant (1F)",
        "Iki Town - Southwest",
        "Hau'oli City (Shopping) - Ilima's House Pool",
        "Wela Volcano Park - Rocks Behind Sign",
        "Route 5 - South of Pokemon Center",
        "Hano Beach - Below Sandygast",
        "Malie City (Outer Cape) - Recycling Plant (Outside)",
        "Malie City - Ferry Terminal (Outside)",
        "Malie City - Apparel Shop (Outside)",
        "Malie City - Salon (Outside)",
        "Route 16 - Aether Base (Outside)",
        "Blush Mountain - Power Plant (Outside)",
        "Malie City - Library (2F)",
        "Malie Garden - Northeast",
        "Malie City - CommunityCenter",
        "Hokulani Observatory - Outside",
        "Mount Hokulani",
        "Blush Mountain - Power Plant",
        "Route 13",
        "Route 14 - Front of Abandoned Megamart",
        "Route 14 - North",
        "Route 15 - Islet Surfboard (Outside)",
        "Route 17 - Police Station (Outside)",
        "Route 17 - Police Station",
        "Po Town - Pokemon Center (Outside)",
        "Exeggutor Island - Under Rock",
        "Po Town - Shady House East (Outside)",
        "Po Town - Pokemon Center",
        "Po Town - Shady House (1F)",
        "Route 13 - Motel (Outside)",
        "Po Town - Shady House 2F (Outside)",
        "Route 17 - South of Po Town",
        "Ula'ula Meadow",
        "Po Town - Shady House West Rocks (Outside) 1",
        "Po Town - Shady House West Rocks (Outside) 2",
        "Po Town - Shady House West Rocks (Outside) 3",
        "Seafolk Village - Southeast Whiscash (Mina's Ship) (Outside)",
        "Seafolk Village - Southwest Huntail",
        "Seafolk Village - Southwest Huntail (Outside)",
        "Seafolk Village - Southeast Whiscash (Mina's Ship)",
        "Seafolk Village - West Wailord (Restaurant)",
        "Seafolk Village - East Steelix",
        "Poni Wilds - Southeast",
        "Ancient Poni Path - Hapu's House (Kitchen)",
        "Seafolk Village - Northeast",
        "Ancient Poni Path - Hapu's House (Bedroom)",
        "Ancient Poni Path - Southwest",
        "Ancient Poni Path - Hapu's House (Courtyard)",
        "Ancient Poni Path - Hapu's House (Outside Behind Well)",
        "Ancient Poni Path - Northeast",
        "Battle Tree - Entrance",
    ];
}
