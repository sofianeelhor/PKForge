using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Whole-team / whole-box Showdown import: the paste is cut into sets exactly where PKHeX's
/// ShowdownParsing.GetShowdownSets cuts it (blank lines; "=== team ===" headers dropped),
/// every set is legalized offline as a dry run so the preview shows its real verdict, and
/// only the chosen candidates are placed, into slots that are still empty.
/// </summary>
public static class ShowdownTeamService
{
    private static readonly GameStrings Strings = GameInfo.GetStrings("en");

    /// <summary>The paste cut into (text, parsed set) pairs; sets without a species are dropped.</summary>
    public static IReadOnlyList<(string Text, ShowdownSet Set)> Split(string paste)
    {
        ArgumentNullException.ThrowIfNull(paste);
        var lines = paste.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(line => !line.TrimStart().StartsWith("===", StringComparison.Ordinal));
        var text = string.Join('\n', lines);
        var result = new List<(string, ShowdownSet)>();
        var start = 0;
        while (start < text.Length)
        {
            var slice = text.AsSpan(start);
            var set = ShowdownParsing.GetShowdownSet(slice, out var length);
            if (length <= 0) break;
            var chunk = slice[..Math.Min(length, slice.Length)].ToString().Trim();
            start += length;
            if (set.Species != 0 && chunk.Length > 0) result.Add((chunk, set));
        }
        return result;
    }

    /// <summary>
    /// Legalizes every set as a dry run (nothing is placed). Stock sessions get the
    /// generated candidate and its LegalityAnalysis verdict; ROM hack sessions generate
    /// straight into the slot, so their preview can only report the parse.
    /// </summary>
    public static IReadOnlyList<ShowdownSetPreview> Preview(ISaveEngineSession session, ILegalizerService legalizer, string paste,
        bool allowUnsupportedSpecies = false, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(legalizer);
        var sets = Split(paste);
        var previews = new List<ShowdownSetPreview>(sets.Count);
        for (var i = 0; i < sets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, set) = sets[i];
            var title = Title(set);
            var problems = set.InvalidLines.Select(line => $"{line.Type}: {line.Value}").ToList();
            if (session is not SaveEngineSession engine)
            {
                previews.Add(new ShowdownSetPreview(i, text, title, set.Species, set.Form, set.Shiny, false, false,
                    "Checked when placed (this game builds sets in place)", problems, default));
            }
            else
            {
                var generated = legalizer.GenerateDataFromShowdown(session, text, allowUnsupportedSpecies);
                var pk = generated is null ? null : Materialize(engine.SaveFile, generated.Data);
                if (pk is null)
                {
                    previews.Add(new ShowdownSetPreview(i, text, title, set.Species, set.Form, set.Shiny, false, false,
                        set.Species > engine.SaveFile.MaxSpeciesID
                            ? "This game cannot store this species"
                            : "No legal combination found in this game", problems, default));
                }
                else
                {
                    var la = new LegalityAnalysis(pk);
                    previews.Add(new ShowdownSetPreview(i, text, title, pk.Species, pk.Form, pk.IsShiny, true, la.Valid,
                        la.Valid ? $"Legal · Lv. {pk.CurrentLevel}" : "Generated, legality imperfect",
                        problems, generated!.Data));
                }
            }
            onProgress?.Invoke(i + 1, sets.Count);
        }
        return previews;
    }

    /// <summary>Empty slots to fill, in order: the given box only, or from it onward through
    /// every later box. Box -1 means the party (its free places, up to six).</summary>
    public static IReadOnlyList<SlotRef> EmptySlots(ISaveEngineSession session, int startBox, bool onlyThisBox)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Snapshot.Slots;
        if (startBox == -1)
        {
            var used = snapshot.Count(s => s.Box == -1 && s.Species is not null);
            return Enumerable.Range(used, Math.Max(0, 6 - used)).Select(slot => new SlotRef(-1, slot)).ToList();
        }
        return snapshot
            .Where(s => s.Box >= startBox && (!onlyThisBox || s.Box == startBox) && s.Species is null)
            .OrderBy(s => s.Box).ThenBy(s => s.Slot)
            .Select(s => new SlotRef(s.Box, s.Slot))
            .ToList();
    }

    /// <summary>
    /// Places the chosen sets into <paramref name="targets"/> in order. A target is re-checked
    /// live and skipped when something moved in since the preview; stock sessions place the
    /// previewed candidate bytes, ROM hack sessions generate in place.
    /// </summary>
    public static GenerationOutcome Place(ISaveEngineSession session, ILegalizerService legalizer,
        IReadOnlyList<ShowdownSetPreview> sets, IReadOnlyList<SlotRef> targets, bool allowUnsupportedSpecies = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var queue = new Queue<SlotRef>(targets);
        var placed = 0;
        var failed = 0;
        foreach (var set in sets)
        {
            SlotRef? target = null;
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next.Box == -1 || session.ReadEntity(next.Box, next.Slot).IsEmpty) { target = next; break; }
            }
            if (target is not { } slot) break;

            bool ok;
            if (session is SaveEngineSession engine && !set.Candidate.IsEmpty)
            {
                var pk = Materialize(engine.SaveFile, set.Candidate.ToArray());
                ok = pk is not null && PlaceEntity(engine.SaveFile, slot, pk);
            }
            else if (session is not SaveEngineSession)
            {
                ok = legalizer.GenerateFromShowdown(session, slot.Box, slot.Slot, set.Text, allowUnsupportedSpecies).Success;
            }
            else
            {
                ok = false;
            }
            if (ok) placed++;
            else { failed++; queue = new Queue<SlotRef>(queue.Prepend(slot)); }
        }

        var unplaced = sets.Count - placed - failed;
        var tail = (failed > 0 ? $" {failed} could not be built." : "") + (unplaced > 0 ? $" {unplaced} did not fit: no empty slot left." : "");
        return placed > 0
            ? new GenerationOutcome(true, $"Imported {placed} Pokémon from Showdown.{tail}")
            : new GenerationOutcome(false, $"Nothing was imported.{tail}");
    }

    private static bool PlaceEntity(SaveFile save, SlotRef slot, PKM pk)
    {
        if (slot.Box == -1)
        {
            if (save.PartyCount >= 6) return false;
            save.SetPartySlotAtIndex(pk, save.PartyCount, EntityImportSettings.None);
            return true;
        }
        save.SetBoxSlotAtIndex(pk, slot.Box, slot.Slot, EntityImportSettings.None);
        return true;
    }

    /// <summary>The legalizer's output bytes back as the save's own entity type.</summary>
    private static PKM? Materialize(SaveFile save, byte[] data)
    {
        var pk = EntityFormat.GetFromBytes(data.ToArray(), save.Context);
        if (pk is null) return null;
        if (pk.GetType() == save.PKMType) return pk;
        var converted = EntityConverter.ConvertToType(pk, save.PKMType, out var result);
        return result == EntityConverterResult.Success ? converted : null;
    }

    private static string Title(ShowdownSet set)
    {
        var species = (uint)set.Species < (uint)Strings.specieslist.Length ? Strings.specieslist[set.Species] : $"#{set.Species}";
        var form = set.FormName is { Length: > 0 } f ? $"-{f}" : "";
        var name = set.Nickname is { Length: > 0 } nick && !nick.Equals(species, StringComparison.OrdinalIgnoreCase)
            ? $"{nick} ({species}{form})" : species + form;
        return set.Shiny ? $"{name} ★" : name;
    }
}
