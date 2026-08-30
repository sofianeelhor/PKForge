using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Edits the battle metadata stored alongside the four selected moves.</summary>
public static class MoveDetailsEditor
{
    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var details = session.GetMoveDetails(box, slot);
            var options = new List<PadOption>();
            for (var i = 0; i < details.Moves.Count; i++)
            {
                var move = details.Moves[i];
                options.Add(new PadOption($"Move {i + 1} · PP {move.PP}/{move.MaxPP} · Ups {move.PPUps}/3"));
            }
            if (details.SupportsRelearn)
                options.Add(new PadOption($"Relearn moves · {details.RelearnMoves.Count(move => move != 0)}/4"));

            var choice = await EditorMenu.ShowAsync(host, "MOVE DETAILS", null, options.ToArray());
            if (choice is null) return dirty;
            if (choice.StartsWith("Move ", StringComparison.Ordinal))
            {
                var index = choice[5] - '1';
                if ((uint)index < 4 && await EditMoveAsync(host, session, box, slot, index)) dirty = true;
            }
            else if (choice.StartsWith("Relearn moves", StringComparison.Ordinal) && await EditRelearnAsync(host, session, box, slot)) dirty = true;
        }
    }

    private static async Task<bool> EditMoveAsync(Grid host, ISaveEngineSession session, int box, int slot, int index)
    {
        var detail = session.GetMoveDetails(box, slot).Moves[index];
        if (detail.Move == 0)
        {
            await EditorMenu.ShowAsync(host, "MOVE DETAILS", "Choose a move first, then set its PP or PP Ups here.", "OK");
            return false;
        }

        var choice = await EditorMenu.ShowAsync(host, $"MOVE {index + 1}", null,
            new PadOption($"PP · {detail.PP}/{detail.MaxPP}"),
            new PadOption($"PP Ups · {detail.PPUps}/3"),
            new PadOption("Restore PP"));
        if (choice is null) return false;
        if (choice == "Restore PP")
        {
            if (detail.PP == detail.MaxPP) return false;
            var pp = session.GetMoveDetails(box, slot).Moves.Select(move => move.PP).ToArray();
            pp[index] = detail.MaxPP;
            session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PP: pp));
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
        var choices = details.RelearnMoves.Select((move, i) => new PickItem(i, $"Slot {i + 1} · {Name(move)}")).ToList();
        var slotChoice = await PickerMenu.ShowAsync(host, "RELEARN MOVES", choices);
        if (slotChoice is null) return false;

        var moves = new List<PickItem> { new(0, "(none)") };
        moves.AddRange(Enumerable.Range(1, data.MoveNames.Count - 1)
            .Where(id => data.MoveNames[id].Length != 0).Select(id => new PickItem(id, data.MoveNames[id])));
        var picked = await PickerMenu.ShowAsync(host, $"RELEARN SLOT {slotChoice.Id + 1}", moves, details.RelearnMoves[slotChoice.Id]);
        if (picked is null || picked.Id == details.RelearnMoves[slotChoice.Id]) return false;
        var relearn = details.RelearnMoves.ToArray();
        relearn[slotChoice.Id] = picked.Id;
        session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(RelearnMoves: relearn));
        return true;
    }
}
