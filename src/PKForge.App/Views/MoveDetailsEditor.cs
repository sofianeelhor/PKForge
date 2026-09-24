using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// Edits the battle metadata stored alongside the four selected moves. Each move and relearn
/// slot wears its legality verdict (green legal, red not legal, with the engine's reason), and
/// "Suggest moves" / "Suggest relearn moves" preview PKHeX's suggestions before writing.
/// </summary>
public static class MoveDetailsEditor
{
    private const string SuggestMoves = "Suggest moves";
    private const string SuggestRelearn = "Suggest relearn moves";

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        while (true)
        {
            var details = session.GetMoveDetails(box, slot);
            var legality = await LegalityAssistUi.MoveLegalityAsync(session, box, slot);
            var options = new List<PadOption>();
            for (var i = 0; i < details.Moves.Count; i++)
            {
                var move = details.Moves[i];
                // "Move 1 · Thunderbolt" over "Electric · Special · PP 15/24 · Ups 3/3 · Legal · Level Up".
                var name = MoveName(data, move.Move);
                var facts = move.Move == 0 ? null : InfoPickers.Info?.GetMove(session, move.Move);
                var kind = facts is null ? "" : $"{TypeFacts.Name(facts.Type)} · {TypeFacts.CategoryName(facts.Category)} · ";
                var verdict = legality.Move(i);
                var typeColor = facts is null ? null : InfoKit.TypeColor(facts.Type);
                var detail = move.Move == 0 ? "Empty slot" : $"{kind}PP {move.PP}/{move.MaxPP} · PP Ups {move.PPUps}/3";
                if (verdict is { Move: > 0 }) detail = verdict.Valid ? $"{detail} · {LegalityAssistUi.VerdictText(verdict)}" : $"{LegalityAssistUi.VerdictText(verdict)} · {detail}";
                options.Add(new PadOption($"Move {i + 1} · {name}", Accent: LegalityAssistUi.VerdictColor(verdict, typeColor),
                    Glyph: verdict is { Valid: false } ? "!" : facts is null ? null : "●", Detail: detail));
            }
            if (details.SupportsRelearn)
            {
                var badRelearn = legality.Relearn.Count(v => !v.Valid);
                options.Add(new PadOption($"Relearn moves · {details.RelearnMoves.Count(move => move != 0)}/4",
                    Glyph: badRelearn > 0 ? "!" : null, Accent: badRelearn > 0 ? UiTokens.Bad : null,
                    Detail: !legality.Supported ? null : badRelearn > 0 ? $"{badRelearn} slot(s) not legal" : "All relearn slots legal"));
            }
            if (legality.Supported)
            {
                var badMoves = legality.Moves.Count(v => !v.Valid);
                options.Add(new PadOption(SuggestMoves, Glyph: "●", Accent: UiTokens.Blueprint,
                    Detail: badMoves > 0 ? $"A legal moveset for its level ({badMoves} move(s) not legal now)" : "A legal level-up moveset; preview first"));
                if (details.SupportsRelearn)
                    options.Add(new PadOption(SuggestRelearn, Glyph: "●", Accent: UiTokens.Blueprint,
                        Detail: "The relearn moves its encounter expects; preview first"));
            }

            var choice = await EditorMenu.ShowAsync(host, "Move details", null, options.ToArray());
            if (choice is null) return dirty;
            if (choice == SuggestMoves)
            {
                if (await LegalityAssistUi.OfferAsync(host, session, box, slot, LegalityFix.CurrentMoves)) dirty = true;
            }
            else if (choice == SuggestRelearn)
            {
                if (await LegalityAssistUi.OfferAsync(host, session, box, slot, LegalityFix.RelearnMoves)) dirty = true;
            }
            else if (choice.StartsWith("Move ", StringComparison.Ordinal))
            {
                var index = choice[5] - '1';
                if ((uint)index < 4 && await EditMoveAsync(host, session, box, slot, index)) dirty = true;
            }
            else if (choice.StartsWith("Relearn moves", StringComparison.Ordinal) && await EditRelearnAsync(host, session, box, slot)) dirty = true;
        }
    }

    private static string MoveName(IGameDataService data, int id) =>
        id == 0 ? "(none)" : (uint)id < (uint)data.MoveNames.Count && data.MoveNames[id].Length > 0 ? data.MoveNames[id] : $"#{id}";

    private static async Task<bool> EditMoveAsync(Grid host, ISaveEngineSession session, int box, int slot, int index)
    {
        var detail = session.GetMoveDetails(box, slot).Moves[index];
        if (detail.Move == 0)
        {
            await EditorMenu.ShowAsync(host, "Move details", "Choose a move first, then set its PP or PP Ups here.", "OK");
            return false;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var facts = InfoPickers.Info?.GetMove(session, detail.Move);
        var summary = facts is null ? null
            : $"{TypeFacts.Name(facts.Type)} · {TypeFacts.CategoryName(facts.Category)} · {InfoKit.MoveNumbers(facts with { PP = detail.MaxPP })}";
        var choice = await EditorMenu.ShowAsync(host, $"Move {index + 1} · {MoveName(data, detail.Move)}", summary,
            new PadOption($"PP · {detail.PP}/{detail.MaxPP}", Detail: "Current PP left"),
            new PadOption($"PP Ups · {detail.PPUps}/3", Detail: $"Max PP {detail.MaxPP}"),
            new PadOption("Restore PP", Detail: detail.PP == detail.MaxPP ? "Already full" : $"{detail.PP} → {detail.MaxPP}"));
        if (choice is null) return false;
        if (choice == "Restore PP")
        {
            if (detail.PP == detail.MaxPP) return false;
            var pp = session.GetMoveDetails(box, slot).Moves.Select(move => move.PP).ToArray();
            pp[index] = detail.MaxPP;
            session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PP: pp));
            return true;
        }

        if (choice.StartsWith("PP Ups", StringComparison.Ordinal)
            && InfoPickers.Info?.GetMaxPPByUps(session, box, slot, detail.Move) is { Count: 4 } byUps)
        {
            // Each option previews the max PP it gives: "PP Ups 3" over "Max PP 15 → 24".
            var upOptions = Enumerable.Range(0, 4).Select(ups => new PadOption($"PP Ups {ups}",
                Glyph: ups == detail.PPUps ? "●" : null, Accent: UiTokens.Blueprint,
                Detail: ups == detail.PPUps ? $"Max PP {byUps[ups]} (current)" : $"Max PP {detail.MaxPP} → {byUps[ups]}")).ToArray();
            var upChoice = await EditorMenu.ShowAsync(host, "PP ups", null, upOptions);
            if (upChoice is null || !int.TryParse(upChoice[^1..], out var chosen) || chosen == detail.PPUps) return false;
            var upsNow = session.GetMoveDetails(box, slot).Moves.Select(move => move.PPUps).ToArray();
            upsNow[index] = chosen;
            session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PPUps: upsNow));
            return true;
        }

        var max = choice.StartsWith("PP Ups", StringComparison.Ordinal) ? 3 : detail.MaxPP;
        var current = choice.StartsWith("PP Ups", StringComparison.Ordinal) ? detail.PPUps : detail.PP;
        var picked = await StatsPopup.ShowSingleAsync(host, choice.StartsWith("PP Ups", StringComparison.Ordinal) ? "PP UPS" : "PP", current, max);
        if (picked is not { } value || value == current) return false;

        var currentDetails = session.GetMoveDetails(box, slot);
        if (choice.StartsWith("PP Ups", StringComparison.Ordinal))
        {
            var ups = currentDetails.Moves.Select(move => move.PPUps).ToArray();
            ups[index] = value;
            session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PPUps: ups));
        }
        else
        {
            var pp = currentDetails.Moves.Select(move => move.PP).ToArray();
            pp[index] = value;
            session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PP: pp));
        }
        return true;
    }

    private static async Task<bool> EditRelearnAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var details = session.GetMoveDetails(box, slot);
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        string Name(int id) => id == 0 ? "(none)" : (uint)id < (uint)data.MoveNames.Count ? data.MoveNames[id] : $"#{id}";
        var legality = await LegalityAssistUi.MoveLegalityAsync(session, box, slot);
        var choices = details.RelearnMoves.Select((move, i) =>
        {
            var verdict = legality.RelearnAt(i);
            return new PickItem(i, $"Slot {i + 1} · {Name(move)}", Detail: LegalityAssistUi.VerdictText(verdict) is { Length: > 0 } text ? text : null)
            {
                Tag = verdict is null || move == 0 ? null : verdict.Valid ? "Legal" : "Not legal",
                TagColor = verdict is { Valid: false } ? UiTokens.Bad : UiTokens.Green,
            };
        }).ToList();
        var slotChoice = await PickerMenu.ShowAsync(host, "Relearn moves", choices);
        if (slotChoice is null) return false;

        var moves = new List<PickItem> { new(0, "(none)") };
        moves.AddRange(Enumerable.Range(1, data.MoveNames.Count - 1)
            .Where(id => data.MoveNames[id].Length != 0).Select(id => new PickItem(id, data.MoveNames[id])));
        var picked = await PickerMenu.ShowAsync(host, $"Relearn slot {slotChoice.Id + 1}", moves, details.RelearnMoves[slotChoice.Id]);
        if (picked is null || picked.Id == details.RelearnMoves[slotChoice.Id]) return false;
        var relearn = details.RelearnMoves.ToArray();
        relearn[slotChoice.Id] = picked.Id;
        session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(RelearnMoves: relearn));
        return true;
    }
}
