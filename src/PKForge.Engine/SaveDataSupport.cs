using PKForge.Domain;

namespace PKForge.Engine;

/// <summary>Which of the core Save data entries an open save can back.</summary>
public sealed record SaveDataCoreSupport(bool TrainerCard, bool Pokedex, bool TrainerRecords, bool Bag, bool GiftInbox)
{
    public static SaveDataCoreSupport All { get; } = new(true, true, true, true, true);
}

/// <summary>
/// The core Save data entries (card, Pokédex, records, bag, stored gifts) for any session.
/// A retail save keeps all of them, as before. A ROM hack session is not PKHeX-backed: it
/// reports what its own engine reads, and an entry it has nothing for is left out instead
/// of opening onto an empty or refusing screen.
/// </summary>
public static class SaveDataSupport
{
    public static SaveDataCoreSupport Of(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        // A retail save keeps card, Pokédex and bag. Records and stored gifts are hidden where
        // the game has none (Emerald offered both and each only said "not supported", QA 2026-11-12).
        if (session is SaveEngineSession)
            return SaveDataCoreSupport.All with
            {
                TrainerRecords = Probe(() => session.GetTrainerRecords().Supported),
                GiftInbox = Probe(() => MysteryGiftSlotService.IsSupported(session) || session.GetMysteryGiftInbox().Supported),
            };
        return new SaveDataCoreSupport(
            // Every engine reads the name, IDs and money (GetTrainer); the card shows them.
            TrainerCard: true,
            // Engines with no dex cells report a total of 0 (Unbound keeps 1 as a placeholder
            // and drops every edit), so a real dex has more than one species.
            Pokedex: Probe(() => session.GetDexProgress().Total > 1),
            TrainerRecords: Probe(() => session.GetTrainerRecords().Supported),
            Bag: Probe(() => session.GetBag().Count > 0),
            GiftInbox: Probe(() => MysteryGiftSlotService.IsSupported(session)));
    }

    // A probe that throws means "not offered": the hub must still open.
    private static bool Probe(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception error) when (error is NotSupportedException or NotImplementedException or InvalidOperationException
            or InvalidCastException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
