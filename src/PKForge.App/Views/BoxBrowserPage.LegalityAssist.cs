using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Lot B surfaces of the box browser, kept out of the main page file: the previewed
/// Showdown team import, the multi-select bulk actions and the legality fix report. Every
/// write goes through the view model's RunMutationAsync: Hardcore-guarded,
/// validated, backed up (restore point) and written once.
/// </summary>
public sealed partial class BoxBrowserPage
{
    // ── Bulk actions (multi-select) ──

    private const string BulkLegalize = "Legalize selection";
    private const string BulkShiny = "Make shiny";
    private const string BulkUnshiny = "Remove shiny";
    private const string BulkHandler = "Set me as handler";
    private const string BulkHeal = "Heal selection";
    private const string BulkClones = "Delete clones";

    /// <summary>The bulk entries for the organizer menu, without the ones Hardcore refuses.</summary>
    private IEnumerable<PadOption> BulkOptions()
    {
        var session = _sessionsFor();
        var stock = session is SaveEngineSession && session.SupportsLegalityAnalysis;
        return Menu(
            stock ? Allowed(SaveAction.BatchEdit, new(BulkLegalize, IconPath: "fix")) : null,
            stock ? Allowed(SaveAction.BatchEdit, new(BulkShiny, IconPath: "shiny")) : null,
            stock ? Allowed(SaveAction.BatchEdit, new(BulkUnshiny, IconPath: "shiny")) : null,
            stock && session!.Generation >= 6 ? Allowed(SaveAction.BatchEdit, new(BulkHandler, IconPath: "trainer")) : null,
            session is SaveEngineSession ? Allowed(SaveAction.BatchEdit, new(BulkHeal, IconPath: "heal")) : null,
            Allowed(SaveAction.Release, new(BulkClones, IconPath: "release")));
    }

    /// <summary>Runs a bulk entry; false when <paramref name="choice"/> is not one.</summary>
    private async Task<bool> TryRunBulkAsync(string? choice)
    {
        switch (choice)
        {
            case BulkLegalize:
            {
                var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
                if (legalizer is null) return true;
                await RunBulkAsync(BulkLegalize, "Every illegal marked Pokémon is rewritten to its closest legal version; legal ones are left alone.",
                    "Legalizing…", (s, slots, report) => legalizer.LegalizeSlots(s, slots, report));
                return true;
            }
            case BulkShiny or BulkUnshiny:
            {
                var shiny = choice == BulkShiny;
                var hax = HaXMode.IsOn;
                await RunBulkAsync(choice, (shiny
                        ? "Marked Pokémon become shiny. "
                        : "Marked Pokémon lose their shininess. ") +
                    (hax ? "HaX mode: the PID is rewritten even when that breaks legality."
                        : "A legal Pokémon is only changed when a legal version of it exists (shiny-locked ones stay as they are)."),
                    shiny ? "Making shiny…" : "Removing shiny…",
                    (s, slots, report) => BulkBoxActions.SetShiny(s, slots, shiny, keepLegal: !hax, report)
                        .ToOutcome(shiny ? "Made shiny:" : "Removed shiny from"));
                return true;
            }
            case BulkHandler:
                await RunBulkAsync(BulkHandler, "Your trainer becomes the current handler of the marked Gen 6+ Pokémon, as a real trade does (name, gender, friendship, trade memory). Your own Pokémon return to you as OT; any handover that would break legality is skipped.",
                    null, (s, slots, _) => BulkBoxActions.SetCurrentTrainerAsHandler(s, slots).ToOutcome("Handed over"));
                return true;
            case BulkHeal:
                await RunBulkAsync(BulkHeal, "Party members get full HP, status and PP; boxed Pokémon get their PP back.",
                    null, (s, slots, _) => BulkBoxActions.Heal(s, slots).ToOutcome("Healed"));
                return true;
            case BulkClones:
                await DeleteClonesAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task RunBulkAsync(string verb, string explanation, string? overlayTitle,
        Func<ISaveEngineSession, IReadOnlyList<(int Box, int Slot)>, Action<int, int>, GenerationOutcome> operation)
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var marked = _viewModel.MarkedSlots.ToList();
        if (marked.Count == 0) return;
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, $"{verb}?",
            $"{explanation}\n{marked.Count} marked. The current state stays available as a restore point.", verb);
        if (!confirmed) return;

        var overlay = overlayTitle is null ? null : LoadingOverlay.Show(_hostGrid, overlayTitle, $"{marked.Count} Pokémon, one backed-up write.");
        try
        {
            var ok = await _viewModel.RunMutationAsync(s => operation(s, marked, (done, total) => overlay?.Report(done, total)),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"{verb} ({marked.Count} marked)", action: SaveAction.BatchEdit);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
            if (!ok) await PadMenu.ShowAsync(_hostGrid, verb, _viewModel.Status, "OK");
        }
        finally
        {
            overlay?.Close();
        }
    }

    /// <summary>Clone groups inside the selection (CollectionAudit keys): one of each stays,
    /// the rest are released in one backed-up write. Locked and party slots are never touched.</summary>
    private async Task DeleteClonesAsync()
    {
        if (Denied(SaveAction.Release)) return;
        var session = _sessionsFor();
        var docId = DocumentId;
        if (session is null) return;
        var extras = BulkBoxActions.FindCloneExtras(session, _viewModel.MarkedSlots.ToList());
        var locked = docId is null ? [] : extras
            .Where(e => !Protection.CanRelease(docId, e.Box, e.Slot, session.GetRngInfo(e.Box, e.Slot).Pid)).ToList();
        var targets = extras.Except(locked).ToList();
        if (targets.Count == 0)
        {
            _viewModel.Status = extras.Count == 0 ? "No clones among the marked Pokémon." : "Every extra clone is locked against release.";
            return;
        }
        var lockedNote = locked.Count > 0 ? $" {locked.Count} locked clone(s) stay." : "";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Delete clones?",
            $"{targets.Count} duplicate(s) share an identity (EC + PID, or PID + OT + TID) with another marked Pokémon. One of each is kept (a party member first).{lockedNote} The current state stays available as a restore point.",
            "Delete clones");
        if (!confirmed) return;
        await _viewModel.BulkReleaseAsync(targets);
        _canvas.InvalidateSurface();
    }

    // ── Showdown team / box import ──

    private const string ImportThisBox = "This box";
    private const string ImportOnward = "This box and the next ones";
    private const string ImportPickBox = "Choose a box…";
    private const string ImportParty = "The party";

    /// <summary>
    /// Paste many sets, see each one's legality after an offline dry run, choose which to
    /// keep and where they go, then place them all in one backed-up write.
    /// </summary>
    private async Task ImportShowdownPreviewedAsync(string title)
    {
        if (Denied(SaveAction.CreateMon)) return;
        var session = _sessionsFor();
        var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
        if (session is null || legalizer is null) return;
        var text = await TextPopup.ShowAsync(_hostGrid, title,
            "Paste a whole team or box: one Showdown set per Pokémon, a blank line between sets.");
        if (string.IsNullOrWhiteSpace(text)) return;

        var hax = HaXMode.IsOn;
        IReadOnlyList<ShowdownSetPreview> previews;
        var overlay = LoadingOverlay.Show(_hostGrid, "Reading the sets…", "Each set is legalized offline as a dry run; nothing is placed yet.");
        try
        {
            previews = await Task.Run(() => ShowdownTeamService.Preview(session, legalizer, text, hax,
                (done, total) => overlay.Report(done, total), overlay.Cancellation.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            _viewModel.Status = error.Message;
            return;
        }
        finally { overlay.Close(); }
        if (previews.Count == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, title, "No Showdown set was found in that text.", "OK");
            return;
        }

        var stock = session is SaveEngineSession;
        var chosen = previews.Select(p => p.Generated || !stock).ToArray();
        while (true)
        {
            var count = chosen.Count(c => c);
            var importLabel = $"Import {count} set(s)…";
            var options = new List<PadOption>();
            if (count > 0) options.Add(new PadOption(importLabel, Glyph: "●", Accent: UiTokens.Green, Detail: "Choose where they go next"));
            for (var i = 0; i < previews.Count; i++)
                options.Add(SetRow(previews[i], chosen[i]));
            var choice = await PadMenu.ShowAsync(_hostGrid, $"{title} · {previews.Count} set(s)",
                Note("A: include or leave out a set. Legality comes from the offline dry run."), options.ToArray());
            if (choice is null) return;
            if (choice == importLabel) break;
            var index = options.FindIndex(o => o.Label == choice) - (count > 0 ? 1 : 0);
            if ((uint)index >= (uint)previews.Count) continue;
            if (stock && !previews[index].Generated)
            {
                await PadMenu.ShowAsync(_hostGrid, previews[index].Title, $"{previews[index].Verdict}. This set cannot be imported.", "OK");
                continue;
            }
            chosen[index] = !chosen[index];
        }

        var picked = previews.Where((_, i) => chosen[i]).ToList();
        var targets = await PickImportTargetsAsync(session, picked.Count);
        if (targets is null) return;
        if (targets.Count == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, title, "There is no empty slot there.", "OK");
            return;
        }
        var fits = Math.Min(picked.Count, targets.Count);
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Import sets?",
            $"{fits} Pokémon go into empty slots" + (picked.Count > targets.Count ? $"; {picked.Count - targets.Count} do not fit and are skipped" : "") +
            ". Nothing is overwritten. The current state stays available as a restore point.", "Import");
        if (!confirmed) return;

        var place = LoadingOverlay.Show(_hostGrid, "Placing the sets…", "One backed-up write.");
        try
        {
            await _viewModel.RunMutationAsync(s => ShowdownTeamService.Place(s, legalizer, picked, targets, hax),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"Import {fits} Showdown set(s)", action: SaveAction.CreateMon);
            _viewModel.RefreshAllSlots(includeEmptyPartySlots: targets.Any(t => t.Box == -1));
            _canvas.InvalidateSurface();
        }
        finally { place.Close(); }
    }

    private static PadOption SetRow(ShowdownSetPreview set, bool included)
    {
        var accent = !set.Generated ? set.Candidate.IsEmpty && set.Verdict.StartsWith("Checked", StringComparison.Ordinal) ? UiTokens.Blueprint : UiTokens.Bad
            : set.Legal ? UiTokens.Green : UiTokens.Warn;
        var problems = set.ParseProblems.Count > 0 ? $" · {set.ParseProblems.Count} unread line(s): {set.ParseProblems[0]}" : "";
        return new PadOption($"{set.Index + 1}. {set.Title}", Glyph: included ? "●" : "○", Accent: accent,
            Detail: (included ? "" : "Left out · ") + set.Verdict + problems);
    }

    private async Task<IReadOnlyList<SlotRef>?> PickImportTargetsAsync(ISaveEngineSession session, int needed)
    {
        var box = _viewModel.BoxIndex;
        if (box == -1)
        {
            var party = ShowdownTeamService.EmptySlots(session, -1, onlyThisBox: true);
            var partyChoice = await PadMenu.ShowAsync(_hostGrid, "Import where?", null,
                new PadOption($"{ImportParty} · {party.Count} free", IconPath: "party"),
                new PadOption($"{ImportPickBox}", IconPath: "box"));
            if (partyChoice is null) return null;
            if (partyChoice.StartsWith(ImportParty, StringComparison.Ordinal)) return party;
            return await PickBoxAsync(session);
        }

        var here = ShowdownTeamService.EmptySlots(session, box, onlyThisBox: true);
        var onward = ShowdownTeamService.EmptySlots(session, box, onlyThisBox: false);
        var choice = await PadMenu.ShowAsync(_hostGrid, "Import where?", $"{needed} set(s) to place into empty slots.",
            new PadOption($"{ImportThisBox} · {here.Count} empty", IconPath: "box", Detail: $"Box {box + 1:00} only"),
            new PadOption($"{ImportOnward} · {onward.Count} empty", IconPath: "all", Detail: $"From box {box + 1:00} onward"),
            new PadOption(ImportPickBox, IconPath: "move"));
        if (choice is null) return null;
        if (choice.StartsWith(ImportOnward, StringComparison.Ordinal)) return onward;
        if (choice.StartsWith(ImportThisBox, StringComparison.Ordinal)) return here;
        return await PickBoxAsync(session);
    }

    private async Task<IReadOnlyList<SlotRef>?> PickBoxAsync(ISaveEngineSession session)
    {
        var boxes = Enumerable.Range(0, _viewModel.BoxCount)
            .Select(b => new PadOption($"Box {b + 1:00}", Detail: $"{ShowdownTeamService.EmptySlots(session, b, onlyThisBox: true).Count} empty"))
            .ToArray();
        var target = await PadMenu.ShowAsync(_hostGrid, "Import into which box?", null, boxes);
        if (target is null) return null;
        var index = Array.FindIndex(boxes, b => b.Label == target);
        return index < 0 ? null : ShowdownTeamService.EmptySlots(session, index, onlyThisBox: true);
    }

    // ── Legality report with fixes ──

    /// <summary>The grouped report with per-check fixes; Hardcore mode (or a game without
    /// legality tables) keeps the read-only text report instead.</summary>
    private async Task<bool> TryShowLegalityFixesAsync()
    {
        var session = _sessionsFor();
        if (HardcoreMode.IsOn || session is null || !session.SupportsLegalityAnalysis) return false;
        await RunSubEditorAsync(LegalityReportEditor.ShowAsync, "Legality fixes applied");
        _canvas.InvalidateSurface();
        return true;
    }
}
