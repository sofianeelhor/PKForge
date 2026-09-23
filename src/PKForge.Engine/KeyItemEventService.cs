using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Where a key-item distribution event stands in a save.</summary>
public enum KeyItemEventState
{
    /// <summary>Neither the item nor the event state is present.</summary>
    NotEnabled,

    /// <summary>Half-armed: the item is in the bag without the event flags/vars (or vice versa).
    /// The game ignores it - this is the "I added the ticket but nothing happens" case.</summary>
    Partial,

    /// <summary>Item and every flag/var the real distribution writes are present.</summary>
    Enabled,

    /// <summary>The player already showed the ticket to the sailor (Gen 3 "shown" flag).</summary>
    Used,

    /// <summary>The event legendary was caught or defeated.</summary>
    Completed,
}

/// <summary>One key-item event as it currently reads in the save.</summary>
public sealed record KeyItemEventStatus(
    string Id,
    string Title,
    string Destination,
    string ItemName,
    bool HasItem,
    bool EventArmed,
    bool Used,
    bool Completed,
    KeyItemEventState State,
    string? Prerequisite);

/// <summary>
/// Key-item Mystery Gift events for the retail Gen 3/4 games: the ticket/flute/card plus the
/// exact flags and vars the official distribution writes. Putting the item in the bag alone
/// does not open the event: every harbor/sailor script checks an enable flag (Gen 3) or a
/// magic-number var (Gen 4) that only the distribution sets.
///
/// Sources (numbers are cross-checked against both where both exist):
///  - PKHeX event labels: PKHeX.Core/Resources/text/script/gen3/flags_{rs,e,frlg}_en.txt,
///    gen3/const_frlg_en.txt, gen4/const_{dp,pt}_en.txt, gen4/flags_{dp,pt}_en.txt, and
///    PKHeX.WinForms SAV_Misc3 (ticket item ids 0x113/0x172/0x173/0x178, ferry flags 0x8B3/0x8D5/0x8D6/0x8E0).
///  - pret disassemblies: pokeemerald data/scripts/gift_{aurora_ticket,mystic_ticket,old_sea_map}.inc,
///    data/maps/LilycoveCity_Harbor/scripts.inc, src/record_mixing.c; pokefirered
///    include/constants/flags.h + data/maps/VermilionCity/scripts.inc; pokeruby flags.h +
///    LilycoveCity_Harbor scripts; pokeplatinum src/scrcmd_mystery_gift.c + src/system_vars.c.
///
/// Gen 3 note: the real Wonder Card delivers a RAM script that the Pokémon Center 2F deliveryman
/// runs (giveitem + setflag). We write the state that script leaves behind rather than a RAM
/// script, which is byte-for-byte what the save looks like after the player talked to him.
/// </summary>
public static class KeyItemEventService
{
    private const string KeyPouch = nameof(InventoryType.KeyItems);

    /// <summary>A Gen 4 var the distribution sets to a value; <see cref="OnlyIfZero"/> mirrors
    /// pokeplatinum's "if (state == 0) set(1)" guard so a later story state is never rewound.</summary>
    private sealed record WorkValue(int Index, ushort Value, bool OnlyIfZero = false);

    private sealed record Definition(
        string Id,
        string Title,
        string Destination,
        ushort Item,
        int[] EnableFlags,
        WorkValue[] EnableWork,
        int? UsedFlag,
        int[] CompletionFlags,
        Func<SaveFile, string?>? Prerequisite = null);

    // ── Gen 3 ──

    // pokeruby: FLAG_SYS_GAME_CLEAR = SYSTEM_FLAGS(0x800) + 0x04; FLAG_SYS_HAS_EON_TICKET = SYSTEM_FLAGS + 0x53.
    // PKHeX flags_rs: 0x804 "Entered Hall of Fame", 0x853 "Received Eon Ticket", 0xCE "Captured/Defeated Lati@s".
    private static readonly Definition[] RubySapphire =
    [
        new("rs-eon", "Eon Ticket", "Southern Island", 275, [0x853], [], null, [0xCE], GameClear(0x804)),
    ];

    // pokeemerald flags.h (SYSTEM_FLAGS = 0x860): FLAG_SYS_GAME_CLEAR 0x864,
    // FLAG_ENABLE_SHIP_SOUTHERN_ISLAND 0x8B3, _BIRTH_ISLAND 0x8D5, _FARAWAY_ISLAND 0x8D6, _NAVEL_ROCK 0x8E0,
    // FLAG_RECEIVED_AURORA_TICKET 0x13A, _MYSTIC_TICKET 0x13B, _OLD_SEA_MAP 0x13C,
    // FLAG_SHOWN_EON_TICKET 0x1AE, _AURORA 0x1AF, _OLD_SEA_MAP 0x1B0, _MYSTIC 0x1DB,
    // FLAG_DEFEATED_DEOXYS 0x1AC, FLAG_BATTLED_DEOXYS 0x1AD, FLAG_DEFEATED_MEW 0x1C7, FLAG_CAUGHT_MEW 0x1CA,
    // FLAG_DEFEATED_LATIAS_OR_LATIOS 0x1C8, FLAG_CAUGHT_LATIAS_OR_LATIOS 0x1C9,
    // FLAG_CAUGHT_LUGIA 0x91, FLAG_CAUGHT_HO_OH 0x92, FLAG_DEFEATED_HO_OH 0x1DC, FLAG_DEFEATED_LUGIA 0x1DD.
    // The gift_*.inc scripts do "giveitem X; setflag FLAG_ENABLE_SHIP_*; setflag FLAG_RECEIVED_*".
    // The Eon Ticket has no Emerald gift script: record_mixing.c sets only FLAG_ENABLE_SHIP_SOUTHERN_ISLAND.
    private static readonly Definition[] Emerald =
    [
        new("e-eon", "Eon Ticket", "Southern Island", 275, [0x8B3], [], 0x1AE, [0x1C8, 0x1C9], GameClear(0x864)),
        new("e-aurora", "Aurora Ticket", "Birth Island", 371, [0x8D5, 0x13A], [], 0x1AF, [0x1AC, 0x1AD], GameClear(0x864)),
        new("e-oldseamap", "Old Sea Map", "Faraway Island", 376, [0x8D6, 0x13C], [], 0x1B0, [0x1C7, 0x1CA], GameClear(0x864)),
        new("e-mystic", "Mystic Ticket", "Navel Rock", 370, [0x8E0, 0x13B], [], 0x1DB, [0x91, 0x92, 0x1DC, 0x1DD], GameClear(0x864)),
    ];

    // pokefirered flags.h: FLAG_RECEIVED_AURORA_TICKET 0x2A7 (679), _MYSTIC_TICKET 0x2A8 (680),
    // FLAG_ENABLE_SHIP_NAVEL_ROCK 0x84A (2122), _BIRTH_ISLAND 0x84B (2123), FLAG_FOUGHT_DEOXYS 0x2E4,
    // FLAG_SHOWN_MYSTIC_TICKET 0x2F0 (752), FLAG_SHOWN_AURORA_TICKET 0x2F1 (753).
    // PKHeX flags_frlg: 740 (= 0x2E4) "Captured Deoxys", 754/755 "Captured Lugia/Ho-Oh", 757/758/759 "Defeated Lugia/Ho-Oh/Deoxys".
    // VermilionCity/scripts.inc only offers the Seagallop when VAR_MAP_SCENE_VERMILION_CITY (0x407E) == 3.
    private static readonly Definition[] FireRedLeafGreen =
    [
        new("frlg-aurora", "Aurora Ticket", "Birth Island", 371, [0x84B, 0x2A7], [], 0x2F1, [0x2E4, 759], VermilionSeagallop),
        new("frlg-mystic", "Mystic Ticket", "Navel Rock", 370, [0x84A, 0x2A8], [], 0x2F0, [754, 755, 757, 758], VermilionSeagallop),
    ];

    // ── Gen 4 ──

    // pokeplatinum system_vars.c sDistributionEventMagicNumbers: DARKRAI 0x1209 (4617), SHAYMIN 0x1112 (4370),
    // ARCEUS 0x1123 (4387), ROTOM 0x1103 (4355), stored at VAR_DISTRIBUTION_EVENT_DARKRAI + id; PKHeX
    // const_pt/const_dp place those vars at work 67/68/69/70. scrcmd_mystery_gift.c Init*Event: add item +
    // SetDistributionEventMagic; InitShayminEvent additionally sets VAR_SHAYMIN_EVENT_STATE (work 87,
    // right after VAR_ARCEUS_EVENT_STATE = PKHeX const_pt 0086) to 1 when it is 0.
    // Completion (PKHeX flags_dp/flags_pt): 344 Darkrai captured, 291 Shaymin captured, 286 Arceus captured,
    // 710 Rotom's Room unlocked (Pt).
    private static readonly Definition[] Platinum =
    [
        new("pt-member", "Member Card", "Newmoon Island (Darkrai)", 454, [], [new(67, 0x1209)], null, [344]),
        new("pt-oak", "Oak's Letter", "Flower Paradise (Shaymin)", 452, [], [new(68, 0x1112), new(87, 1, OnlyIfZero: true)], null, [291]),
        new("pt-azure", "Azure Flute", "Hall of Origin (Arceus)", 455, [], [new(69, 0x1123)], null, [286]),
        new("pt-secret", "Secret Key", "Rotom's Room", 467, [], [new(70, 0x1103)], null, [710]),
    ];

    // Diamond/Pearl share the magic vars 67-69 (PKHeX const_dp). The DP Shaymin state var is not
    // mapped by PKHeX or a disassembly, so only the magic var is written there.
    private static readonly Definition[] DiamondPearl =
    [
        new("dp-member", "Member Card", "Newmoon Island (Darkrai)", 454, [], [new(67, 0x1209)], null, [344]),
        new("dp-oak", "Oak's Letter", "Flower Paradise (Shaymin)", 452, [], [new(68, 0x1112)], null, [291]),
        new("dp-azure", "Azure Flute", "Hall of Origin (Arceus)", 455, [], [new(69, 0x1123)], null, [286]),
    ];

    private static Func<SaveFile, string?> GameClear(int flag) => save =>
        ((IEventFlagArray)save).GetEventFlag(flag) ? null : "Needs the Hall of Fame cleared before the ferry sails.";

    private static string? VermilionSeagallop(SaveFile save) =>
        ((SAV3)save).GetWork(0x7E) >= 3 ? null : "The Seagallop only runs after the S.S. Anne has left Vermilion.";

    /// <summary>True when the session is a retail game with a known event table.</summary>
    public static bool IsSupported(ISaveEngineSession session) => DefinitionsFor(session).Length != 0;

    public static IReadOnlyList<KeyItemEventStatus> GetEvents(ISaveEngineSession session)
    {
        var definitions = DefinitionsFor(session);
        if (definitions.Length == 0)
            return [];
        var save = ((SaveEngineSession)session).SaveFile;
        var names = session.GetItemNames();
        return definitions.Select(definition => Read(session, save, names, definition)).ToList();
    }

    /// <summary>Gives the item and writes every flag/var the official distribution writes.</summary>
    public static GenerationOutcome Enable(ISaveEngineSession session, string id)
    {
        if (!TryFind(session, id, out var save, out var definition))
            return new GenerationOutcome(false, "That event is not available for this game.");

        if (!HasItem(session, definition.Item) && session.SetItemCount(KeyPouch, definition.Item, 1) < 1)
            return new GenerationOutcome(false, $"This save's Key Items pouch cannot hold the {definition.Title}.");

        var flags = (IEventFlagArray)save;
        foreach (var flag in definition.EnableFlags)
            flags.SetEventFlag(flag, true);
        var work = (IEventWorkArray<ushort>)save;
        foreach (var value in definition.EnableWork)
        {
            if (!value.OnlyIfZero || work.GetWork(value.Index) == 0)
                work.SetWork(value.Index, value.Value);
        }
        return new GenerationOutcome(true, $"{definition.Title} event enabled: {definition.Destination}.");
    }

    /// <summary>Takes the item and clears the distribution flags/vars. History (shown/caught
    /// flags, story state vars) is left alone: it records what the player actually did.</summary>
    public static GenerationOutcome Disable(ISaveEngineSession session, string id)
    {
        if (!TryFind(session, id, out var save, out var definition))
            return new GenerationOutcome(false, "That event is not available for this game.");

        if (HasItem(session, definition.Item))
            session.SetItemCount(KeyPouch, definition.Item, 0);
        var flags = (IEventFlagArray)save;
        foreach (var flag in definition.EnableFlags)
            flags.SetEventFlag(flag, false);
        var work = (IEventWorkArray<ushort>)save;
        foreach (var value in definition.EnableWork.Where(value => !value.OnlyIfZero))
            work.SetWork(value.Index, 0);
        return new GenerationOutcome(true, $"{definition.Title} event disabled.");
    }

    private static KeyItemEventStatus Read(ISaveEngineSession session, SaveFile save, IReadOnlyList<string> names, Definition definition)
    {
        var flags = (IEventFlagArray)save;
        var work = (IEventWorkArray<ushort>)save;
        var hasItem = HasItem(session, definition.Item);
        // "OnlyIfZero" state vars advance with the story, so they are not part of the armed check.
        var armed = definition.EnableFlags.All(flags.GetEventFlag)
            && definition.EnableWork.Where(value => !value.OnlyIfZero).All(value => work.GetWork(value.Index) == value.Value);
        var anyArmed = definition.EnableFlags.Any(flags.GetEventFlag)
            || definition.EnableWork.Any(value => !value.OnlyIfZero && work.GetWork(value.Index) == value.Value);
        var used = definition.UsedFlag is { } usedFlag && flags.GetEventFlag(usedFlag);
        var completed = definition.CompletionFlags.Any(flags.GetEventFlag);

        var state = completed ? KeyItemEventState.Completed
            : used ? KeyItemEventState.Used
            : hasItem && armed ? KeyItemEventState.Enabled
            : hasItem || anyArmed ? KeyItemEventState.Partial
            : KeyItemEventState.NotEnabled;
        var itemName = definition.Item < names.Count && names[definition.Item].Length != 0 ? names[definition.Item] : definition.Title;
        return new KeyItemEventStatus(definition.Id, definition.Title, definition.Destination, itemName,
            hasItem, armed, used, completed, state, definition.Prerequisite?.Invoke(save));
    }

    private static bool HasItem(ISaveEngineSession session, ushort item) => session.GetBag()
        .Any(pouch => pouch.Items.Any(entry => entry.Id == item && entry.Count > 0));

    private static bool TryFind(ISaveEngineSession session, string id, out SaveFile save, out Definition definition)
    {
        definition = DefinitionsFor(session).FirstOrDefault(candidate => candidate.Id == id)!;
        save = definition is null ? null! : ((SaveEngineSession)session).SaveFile;
        return definition is not null;
    }

    private static Definition[] DefinitionsFor(ISaveEngineSession session)
    {
        // ROM hacks (Unbound, Radical Red) use their own session types and their own flag maps.
        if (session is not SaveEngineSession engine)
            return [];
        return engine.SaveFile switch
        {
            SAV3E => Emerald,
            SAV3RS => RubySapphire,
            SAV3FRLG => FireRedLeafGreen,
            SAV4Pt => Platinum,
            SAV4DP => DiamondPearl,
            _ => [],
        };
    }
}
