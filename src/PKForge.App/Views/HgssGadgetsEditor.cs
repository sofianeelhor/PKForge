using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// HeartGold/SoulSilver gadgets: the Apricorn Box (PKHeX SAV_Apricorn). Apricorns are bag contents
/// (<see cref="SaveAction.EditInventory"/>). The Pokéwalker has its own painted screen, <see cref="PokewalkerEditor"/>.
/// </summary>
public static class HgssGadgetsEditor
{
    private const string AllApricorns = "Fill every Apricorn (99)";
    private const string NoApricorns = "Empty the Apricorn Box";

    public static async Task ShowApricornsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var apricorns = ApricornService.GetApricorns(session);
            var detail = "Give Apricorns to Kurt in Azalea Town and he makes one Poké Ball from each, ready the next day.";
            if (await WorldEventsMenu.BlockedAsync(host, "Apricorn Box",
                    detail + Environment.NewLine + string.Join(Environment.NewLine, apricorns.Select(a => $"{a.Name}: {a.Count}")),
                    SaveAction.EditInventory))
                return;

            var options = apricorns.Select(a => new PadOption($"{a.Name} × {a.Count}", IconPath: "ball", Detail: $"Makes a {a.BallMade}"))
                .Append(new PadOption(AllApricorns, IconPath: "fill", Accent: UiTokens.Green))
                .Append(new PadOption(NoApricorns, IconPath: "clear"))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Apricorn Box", detail, options);
            if (choice is null) return;
            bool ok;
            if (choice == AllApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, ApricornService.MaxCount); return "Every Apricorn set to 99."; }, SaveAction.EditInventory);
            else if (choice == NoApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, 0); return "Apricorn Box emptied."; }, SaveAction.EditInventory);
            else
            {
                var index = Array.FindIndex(options, o => o.Label == choice);
                if (index < 0 || index >= apricorns.Count) continue;
                var apricorn = apricorns[index];
                var count = await StatsPopup.ShowSingleAsync(host, apricorn.Name, apricorn.Count, ApricornService.MaxCount, "0 - 99");
                if (count is not { } value) continue;
                ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{apricorn.Name}: {ApricornService.SetCount(s, apricorn.Index, value)[apricorn.Index].Count}.", SaveAction.EditInventory);
            }
            if (!ok) return;
        }
    }
}
