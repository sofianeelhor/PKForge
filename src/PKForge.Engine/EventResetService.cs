using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A one-time encounter or gift that can be put back in the world.</summary>
/// <param name="Id">Stable key (PKHeX's G1OverworldSpawner property name, or "c-gsball").</param>
/// <param name="Title">"Mewtwo", "Voltorb 3"...</param>
/// <param name="Where">Where it waits in the game.</param>
/// <param name="Done">True when the game has already used it up (the Pokémon is gone from the map).</param>
public sealed record ResettableEvent(string Id, string Title, string Where, bool Done);

/// <summary>
/// Re-arms one-time encounters:
///  - Gen 1: PKHeX's WinForms <c>SAV_EventReset1</c> over <c>G1OverworldSpawner</c>. Each entry is an
///    event flag plus a map "hide object" flag; <c>FlagPairG1Detail.Reset</c> clears both so the legendary,
///    the Power Plant Voltorb/Electrode, the fossils and the gifts appear again.
///  - Crystal GS Ball: the event-flag state PKHeX labels in flags_c_en.txt (0190 "Kurt can check GS Ball",
///    0191 "Kurt ready to return GS Ball", 0192 "GS Ball can be inserted into Ilex Forest Shrine",
///    0832 "Received GS Ball") and const_c_en.txt work 30 "Azalea Town Event" (2 = Kurt will give the GS Ball).
///    Resetting clears them, takes the GS Ball back and re-arms the Pokémon Center 2F delivery
///    (<c>SAV2.EnableGSBallMobileEvent</c>), so Celebi's shrine event can be played again. Enabling the
///    event for the first time stays in <see cref="KeyItemEventService"/>.
///
/// A second copy of a one-time Pokémon is each legal on its own; owning two is the only tell.
/// </summary>
public static class EventResetService
{
    public const string GsBallId = "c-gsball";

    private const int FlagKurtChecks = 190, FlagKurtReturns = 191, FlagShrineReady = 192, FlagReceived = 832;
    private const int WorkAzalea = 30;
    private const byte AzaleaKurtGivesGsBall = 2;

    // Where each G1OverworldSpawner entry lives (Red/Blue/Yellow maps).
    private static readonly Dictionary<string, string> Gen1Places = new()
    {
        ["Mewtwo"] = "Cerulean Cave", ["Articuno"] = "Seafoam Islands", ["Zapdos"] = "Power Plant",
        ["Moltres"] = "Victory Road", ["Voltorb"] = "Power Plant (disguised as an item)", ["Electrode"] = "Power Plant (disguised as an item)",
        ["Hitmonchan"] = "Saffron Fighting Dojo (gift)", ["Hitmonlee"] = "Saffron Fighting Dojo (gift)",
        ["Eevee"] = "Celadon Mansion (gift)", ["Kabuto"] = "Mt. Moon (Dome Fossil)", ["Omanyte"] = "Mt. Moon (Helix Fossil)",
        ["Aerodactyl"] = "Pewter Museum (Old Amber)", ["Bulbasaur"] = "Cerulean City (Yellow gift)",
        ["Squirtle"] = "Vermilion City, Officer Jenny (Yellow gift)", ["Charmander"] = "Route 24 (Yellow gift)",
    };

    public static bool IsSupported(ISaveEngineSession session) => GetEvents(session).Count != 0;

    public static IReadOnlyList<ResettableEvent> GetEvents(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engine)
            return [];
        switch (engine.SaveFile)
        {
            case SAV1 sav1:
                return new G1OverworldSpawner(sav1).GetFlagPairs()
                    .Select(pair => Describe(pair))
                    .OrderBy(e => e.Title, StringComparer.Ordinal)
                    .ToArray();
            case SAV2 { Version: GameVersion.C } sav2:
                var used = sav2.GetEventFlag(FlagReceived) || sav2.GetEventFlag(FlagKurtChecks) || sav2.GetEventFlag(FlagShrineReady);
                return [new ResettableEvent(GsBallId, "GS Ball (Celebi)", "Goldenrod Pokémon Center 2F → Kurt → Ilex Forest shrine", used)];
            default:
                return [];
        }
    }

    public static GenerationOutcome Reset(ISaveEngineSession session, string id)
    {
        if (session is not SaveEngineSession engine)
            return new GenerationOutcome(false, "This game has no resettable events.");
        switch (engine.SaveFile)
        {
            case SAV1 sav1:
            {
                var spawner = new G1OverworldSpawner(sav1);
                var pair = spawner.GetFlagPairs().FirstOrDefault(p => p.Name == id);
                if (pair is null)
                    return new GenerationOutcome(false, "That encounter does not exist in this game.");
                pair.Reset();
                spawner.Save(); // SAV_EventReset1 writes the flag arrays back on close.
                var e = Describe(pair);
                return new GenerationOutcome(true, $"{e.Title} is back at {e.Where}.");
            }
            case SAV2 { Version: GameVersion.C } sav2 when id == GsBallId:
            {
                foreach (var flag in (int[])[FlagKurtChecks, FlagKurtReturns, FlagShrineReady, FlagReceived])
                    sav2.SetEventFlag(flag, false);
                if (sav2.GetWork(WorkAzalea) == AzaleaKurtGivesGsBall)
                    sav2.SetWork(WorkAzalea, 0);
                var names = session.GetItemNames();
                var gsBall = Enumerable.Range(0, names.Count).FirstOrDefault(i => names[i] == "GS Ball");
                if (gsBall > 0)
                    session.SetItemCount(nameof(InventoryType.KeyItems), (ushort)gsBall, 0);
                sav2.EnableGSBallMobileEvent();
                return new GenerationOutcome(true, "GS Ball event reset: collect it again at the Goldenrod Pokémon Center 2F.");
            }
            default:
                return new GenerationOutcome(false, "That event cannot be reset in this game.");
        }
    }

    private static ResettableEvent Describe(FlagPairG1Detail pair)
    {
        // SAV_EventReset1.InitializeButtons: strip "Flag", species before '_', suffix after it.
        var name = pair.Name[G1OverworldSpawner.FlagPropertyPrefix.Length..];
        var underscore = name.IndexOf('_');
        var species = underscore < 0 ? name : name[..underscore];
        var title = underscore < 0 ? species : $"{species} {name[(underscore + 1)..]}";
        return new ResettableEvent(pair.Name, title, Gen1Places.GetValueOrDefault(species, "Kanto"), pair.IsHidden);
    }
}
