using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>
/// The box manager's bulk actions over a marked selection. Every action edits the live
/// session only; the caller persists once through the backed-up write path. Rewrites that
/// would turn a legal Pokémon illegal are refused per mon (HaX mode lifts that for shiny),
/// so a bulk action never silently breaks what was fine.
/// </summary>
public static class BulkBoxActions
{
    /// <summary>
    /// Shiny or unshiny every selected mon. PKHeX's SetShiny/SetUnshiny rewrite the PID;
    /// when that breaks a legal mon (PID/IV-correlated origins), Auto-Legality rebuilds it
    /// from its own set with the requested shininess (kept only when it is still the same
    /// trainer's mon of the same species), and a mon that still cannot be legal that way
    /// (shiny-locked, fixed-PID gifts) is left untouched.
    /// </summary>
    public static BulkOutcome SetShiny(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots, bool shiny,
        bool keepLegal = true, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var (engine, save) = Require(session);
        var changed = 0;
        var notes = new List<string>();
        var skipped = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (box, slot) = slots[i];
            var pk = engine.GetEntity(box, slot);
            onProgress?.Invoke(i + 1, slots.Count);
            if (pk.Species == 0 || pk.IsShiny == shiny) continue;

            var wasLegal = new LegalityAnalysis(pk).Valid;
            PKM? candidate = pk.Clone();
            if (shiny) candidate.SetShiny(); else candidate.SetUnshiny();
            candidate.RefreshChecksum();
            if (keepLegal && wasLegal && !new LegalityAnalysis(candidate).Valid)
            {
                // A rebuild is a shiny toggle only when it stays this trainer's mon of this
                // species; Auto-Legality may otherwise answer with a different origin
                // (a shiny event gift under the event's OT), which is a replacement.
                var rebuilt = save.Legalize(candidate);
                var sameMon = rebuilt.Species == pk.Species && rebuilt.ID32 == pk.ID32
                    && rebuilt.OriginalTrainerName == pk.OriginalTrainerName;
                candidate = sameMon && rebuilt.IsShiny == shiny && new LegalityAnalysis(rebuilt).Valid ? rebuilt : null;
            }
            if (candidate is null || candidate.IsShiny != shiny)
            {
                skipped++;
                AddNote(notes, shiny ? "no legal shiny version exists" : "no legal non-shiny version exists");
                continue;
            }
            LegalityAssistService.Place(save, box, slot, candidate);
            changed++;
        }
        return new BulkOutcome(changed, skipped, notes);
    }

    /// <summary>
    /// Makes the open save's trainer the current handler of every selected Gen 6+ mon,
    /// through PKHeX's own trade routine (IHandlerUpdate.UpdateHandler): the OT regains the
    /// Pokémon when it is theirs, otherwise the handler name, gender, friendship and trade
    /// memory are set as a real trade would. Older formats keep no handler.
    /// </summary>
    public static BulkOutcome SetCurrentTrainerAsHandler(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots)
    {
        var (engine, save) = Require(session);
        var changed = 0;
        var skipped = 0;
        var notes = new List<string>();
        foreach (var (box, slot) in slots)
        {
            var pk = engine.GetEntity(box, slot);
            if (pk.Species == 0) continue;
            if (pk.Format < 6 || pk is not IHandlerUpdate)
            {
                skipped++;
                AddNote(notes, "formats before Generation 6 keep no handler");
                continue;
            }
            var wasLegal = new LegalityAnalysis(pk).Valid;
            var candidate = pk.Clone();
            ((IHandlerUpdate)candidate).UpdateHandler(save);
            candidate.RefreshChecksum();
            if (candidate.Data.SequenceEqual(pk.Data)) continue;
            if (wasLegal && !new LegalityAnalysis(candidate).Valid)
            {
                skipped++;
                AddNote(notes, "the handover would make it illegal");
                continue;
            }
            LegalityAssistService.Place(save, box, slot, candidate);
            changed++;
        }
        return new BulkOutcome(changed, skipped, notes);
    }

    /// <summary>Restores HP, status and PP (PKM.Heal) for party members; boxed mons get their PP back.</summary>
    public static BulkOutcome Heal(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots)
    {
        var (engine, save) = Require(session);
        var changed = 0;
        foreach (var (box, slot) in slots)
        {
            var pk = engine.GetEntity(box, slot);
            if (pk.Species == 0 || pk.IsEgg) continue;
            var candidate = pk.Clone();
            if (box == -1) candidate.Heal(); else candidate.HealPP();
            candidate.RefreshChecksum();
            if (candidate.Data.SequenceEqual(pk.Data)) continue;
            LegalityAssistService.Place(save, box, slot, candidate);
            changed++;
        }
        return new BulkOutcome(changed, 0, []);
    }

    /// <summary>
    /// The clones inside the selection, grouped with <see cref="CollectionAudit.GroupClones"/>.
    /// One member of each group stays (a party member when there is one, otherwise the first
    /// by box and slot); the extras are returned for release. Party slots are never released
    /// here: the party compacts, and a clone kept there is the one the player carries.
    /// </summary>
    public static IReadOnlyList<(int Box, int Slot)> FindCloneExtras(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bySlot = new Dictionary<string, (int Box, int Slot)>(StringComparer.Ordinal);
        var fingerprints = new List<MonFingerprint>();
        foreach (var (box, slot) in slots.OrderBy(s => s.Box == -1 ? -1 : s.Box).ThenBy(s => s.Slot))
        {
            var detail = session.ReadEntity(box, slot);
            if (detail.IsEmpty) continue;
            var rng = session.GetRngInfo(box, slot);
            var tid = session.GetMetInfo(box, slot).TID;
            var label = box == -1 ? $"Party {slot + 1}" : $"B{box + 1}-{slot + 1}";
            bySlot[label] = (box, slot);
            fingerprints.Add(new MonFingerprint(rng.Pid, rng.EncryptionConstant, detail.OriginalTrainer, tid, label,
                detail.Nickname is { Length: > 0 } ? detail.Nickname : detail.SpeciesName));
        }

        var extras = new List<(int Box, int Slot)>();
        foreach (var group in CollectionAudit.GroupClones(fingerprints))
        {
            var members = group.Members.Select(m => bySlot[m.SlotLabel]).ToList();
            var keep = members.Any(m => m.Box == -1) ? members.First(m => m.Box == -1) : members[0];
            extras.AddRange(members.Where(m => m != keep && m.Box != -1));
        }
        return extras;
    }

    private static void AddNote(List<string> notes, string note)
    {
        if (!notes.Contains(note)) notes.Add(note);
    }

    private static (SaveEngineSession Engine, SaveFile Save) Require(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine)
            throw new NotSupportedException("Bulk edits need a stock game session; this ROM hack keeps its own format.");
        return (engine, engine.SaveFile);
    }
}
