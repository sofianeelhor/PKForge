using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>BDSP's Grand Underground inventory is a distinct fixed list, not a Bag pocket.</summary>
public static class GrandUndergroundEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var items = session.GetGrandUndergroundItems();
            if (items.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "GRAND UNDERGROUND", "This game has no Grand Underground inventory.", "OK");
                return;
            }

            var options = items.Select(item => new PadOption($"{item.Name} · {item.Count}/{item.MaxCount} · {item.Type}"))
                .Append(new PadOption("Fill every stack", Accent: UiTokens.Green))
                .Append(new PadOption("Clear every stack", Accent: UiTokens.GiftRed))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "GRAND UNDERGROUND", "Spheres, treasures, statues, and pedestals", options);
            if (choice is null) return;

            if (choice is "Fill every stack" or "Clear every stack")
            {
                var filling = choice == "Fill every stack";
                var verb = filling ? "FILL" : "CLEAR";
                var confirmed = await PadMenu.ConfirmAsync(host, $"{verb} UNDERGROUND ITEMS?",
                    filling ? "Every Grand Underground stack becomes full. A restore point is created first." : "Every Grand Underground stack becomes 0. A restore point is created first.",
                    filling ? "Fill" : "Clear");
                if (!confirmed) continue;
                var saved = await viewModel.RunMutationAsync(s =>
                {
                    foreach (var item in s.GetGrandUndergroundItems())
                        s.SetGrandUndergroundItemCount(item.Id, filling ? item.MaxCount : 0);
                    return new GenerationOutcome(true, filling ? "Every Grand Underground stack is full." : "Every Grand Underground stack was cleared.");
                }, slot, refreshSlot: false);
                if (!saved) return;
                continue;
            }

            var selected = items.FirstOrDefault(item => choice.StartsWith($"{item.Name} ·", StringComparison.Ordinal));
            if (selected is null) continue;
            var next = await StatsPopup.ShowSingleAsync(host, selected.Name.ToUpperInvariant(), selected.Count, selected.MaxCount);
            if (next is not { } count || count == selected.Count) continue;
            var savedCount = await viewModel.RunMutationAsync(s =>
            {
                var stored = s.SetGrandUndergroundItemCount(selected.Id, count);
                return new GenerationOutcome(true, $"{selected.Name} ×{stored}");
            }, slot, refreshSlot: false);
            if (!savedCount) return;
        }
    }
}
