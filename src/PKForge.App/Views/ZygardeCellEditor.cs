using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Sun/Moon and Ultra Sun/Ultra Moon Zygarde Cells, after PKHeX's SAV_ZygardeCell: the
/// counters and every spot are always shown, Hardcore included; collecting or putting
/// cells back are world writes and go through the safe mutation pipeline.
/// </summary>
public static class ZygardeCellEditor
{
    private const string CollectAll = "Collect every Cell";
    private const string PutBack = "Put picked-up Cells back";
    private const string Browse = "See every spot";
    private const SaveAction Action = SaveAction.EditWorld;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!ZygardeCellService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Zygarde Cells", "Zygarde Cells exist only in Sun, Moon, Ultra Sun and Ultra Moon.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var cells = ZygardeCellService.Get(session);
            var summary = $"Picked up {cells.PickedUp}/{cells.Spots.Count} · in the Cube {cells.Collected} · collected in all {cells.Total}";
            var options = new List<PadOption> { new(Browse, Detail: "Where each Cell or Core is, and whether it was picked up.") };
            var blocked = HardcoreMode.Blocks(Action, out var status);
            if (blocked)
                summary += Environment.NewLine + status;
            else
            {
                options.Add(new PadOption(CollectAll, Detail: "Every spot picked up; the counters follow, as in the game."));
                options.Add(new PadOption(PutBack, Detail: "Picked-up Cells return to the world, ready to collect again."));
            }

            var choice = await EditorMenu.ShowAsync(host, "Zygarde Cells", summary, options.ToArray());
            switch (choice)
            {
                case null: return;
                case Browse:
                    await PickerMenu.ShowAsync(host, "Cell spots", [.. cells.Spots.Select(s => new PickItem(s.Index,
                        $"{(s.IsCollected ? "✓" : "○")} {s.Index + 1:000} · {s.Location}",
                        Detail: ZygardeCellService.StateNames[Math.Min(s.State, ZygardeCellService.StateReceived)]))]);
                    break;
                case CollectAll:
                    if (!await viewModel.RunMutationAsync(s => new GenerationOutcome(true,
                            $"{ZygardeCellService.CollectAll(s)} Zygarde Cell(s) collected."), slot, refreshSlot: false, action: Action))
                        return;
                    break;
                case PutBack:
                    if (!await viewModel.RunMutationAsync(s => new GenerationOutcome(true,
                            $"{ZygardeCellService.Reset(s)} Zygarde Cell(s) put back."), slot, refreshSlot: false, action: Action))
                        return;
                    break;
            }
        }
    }
}
