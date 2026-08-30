using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Adapts PKHeX.Core's embedded Mystery Gift database (every real distribution,
/// bundled offline). Gifts are matched to the open save's entity context so only
/// receivable cards are offered.
/// </summary>
public sealed class EventDatabaseService : IEventDatabaseService
{
    public IReadOnlyList<EventGift> GetGifts(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engineSession) return [];
        return GetRawGifts(engineSession.SaveFile)
            .Select((gift, index) => (Gift: gift, Index: index))
            .Where(x => x.Gift.IsEntity && x.Gift.Species > 0)
            .Select(x => new EventGift(
                x.Index,
                x.Gift.CardTitle.Replace('　', ' ').Trim(),
                x.Gift.CardHeader,
                x.Gift.Species,
                x.Gift.LevelMin,
                x.Gift.IsShiny))
            .ToList();
    }

    public GenerationOutcome Receive(ISaveEngineSession session, int giftId, int box, int slot)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var gifts = GetRawGifts(save);
        if (giftId < 0 || giftId >= gifts.Count)
            return new GenerationOutcome(false, "Unknown gift.");

        var gift = gifts[giftId];
        var created = gift.ConvertToPKM(save);
        if (created.Species == 0)
            return new GenerationOutcome(false, "This gift could not be converted for your save.");

        save.SetBoxSlotAtIndex(created, box, slot, EntityImportSettings.None);
        return new GenerationOutcome(true, "Here's your gift. Take good care of it!");
    }

    /// <summary>The context-correct gift table, in a stable order (indices are gift ids).</summary>
    private static IReadOnlyList<MysteryGift> GetRawGifts(SaveFile save) => save.Context switch
    {
        // MGDB is PKHeX's embedded database; EGDB holds the EventsGallery folders loaded
        // at runtime (EventArchive), which carry every language and later additions.
        EntityContext.Gen4 => [.. EncounterEvent.MGDB_G4, .. EncounterEvent.EGDB_G4],
        EntityContext.Gen5 => [.. EncounterEvent.MGDB_G5, .. EncounterEvent.EGDB_G5],
        EntityContext.Gen6 => [.. EncounterEvent.MGDB_G6, .. EncounterEvent.EGDB_G6],
        EntityContext.Gen7 => [.. EncounterEvent.MGDB_G7, .. EncounterEvent.EGDB_G7],
        EntityContext.Gen7b => [.. EncounterEvent.MGDB_G7GG, .. EncounterEvent.EGDB_G7GG],
        EntityContext.Gen8 => [.. EncounterEvent.MGDB_G8, .. EncounterEvent.EGDB_G8],
        EntityContext.Gen8a => [.. EncounterEvent.MGDB_G8A, .. EncounterEvent.EGDB_G8A],
        EntityContext.Gen8b => [.. EncounterEvent.MGDB_G8B, .. EncounterEvent.EGDB_G8B],
        EntityContext.Gen9 => [.. EncounterEvent.MGDB_G9, .. EncounterEvent.EGDB_G9],
        _ => [],
    };
}
