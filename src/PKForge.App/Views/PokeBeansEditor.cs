using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// SM/USUM Poké Beans live in Poké Pelago, not the normal Bag. This editor keeps
/// the distinction visible while using the same safe write path as every other edit.
/// </summary>
public static class PokeBeansEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var beans = session.GetPokeBeans();
            if (beans.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "POKé BEANS", "This game has no Poké Pelago Bean storage.", "OK");
                return;
            }

            var options = beans.Select(bean => new PadOption($"{bean.Name} · {bean.Count}/{bean.MaxCount}"))
                .Append(new PadOption("Fill every Bean stack", Accent: UiTokens.Green))
                .Append(new PadOption("Clear every Bean stack", Accent: UiTokens.GiftRed))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "POKé BEANS", "Poké Pelago storage", options);
            if (choice is null) return;

            if (choice == "Fill every Bean stack" || choice == "Clear every Bean stack")
            {
                var filling = choice.StartsWith("Fill", StringComparison.Ordinal);
                var verb = filling ? "FILL" : "CLEAR";
                var confirmed = await PadMenu.ConfirmAsync(host, $"{verb} ALL BEANS?",
                    filling ? "Every Poké Bean stack becomes 255. A restore point is created first." : "Every Poké Bean stack becomes 0. A restore point is created first.",
                    filling ? "Fill" : "Clear");
                if (!confirmed) continue;
                var success = await viewModel.RunMutationAsync(s =>
                {
                    foreach (var bean in s.GetPokeBeans()) s.SetPokeBeanCount(bean.Id, filling ? bean.MaxCount : 0);
                    return new GenerationOutcome(true, filling ? "Every Poké Bean stack is full." : "Every Poké Bean stack was cleared.");
                }, slot, refreshSlot: false);
                if (!success) return;
                continue;
            }

            var index = Array.FindIndex(beans.ToArray(), bean => choice.StartsWith(bean.Name, StringComparison.Ordinal));
            if (index < 0) continue;
            var selected = beans[index];
            var count = await StatsPopup.ShowSingleAsync(host, selected.Name.ToUpperInvariant(), selected.Count, selected.MaxCount);
            if (count is not { } next || next == selected.Count) continue;
            var saved = await viewModel.RunMutationAsync(s =>
            {
                var stored = s.SetPokeBeanCount(selected.Id, next);
                return new GenerationOutcome(true, $"{selected.Name} ×{stored}");
            }, slot, refreshSlot: false);
            if (!saved) return;
        }
    }
}
