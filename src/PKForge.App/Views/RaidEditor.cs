using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Sword/Shield Max Raid dens and Scarlet/Violet Tera raids, after PKHeX's SAV_Raid8,
/// SAV_Raid9 and SAV_RaidSevenStar9. The lists are always readable; lighting dens, putting
/// them out and resetting 7-star raids are save-data writes, so they go through the safe
/// mutation pipeline and Hardcore refuses them.
/// </summary>
public static class RaidEditor
{
    private const string SevenStar = "7-star raids";
    private const string ActivateAll = "Activate every den (new seeds)";
    private const string DeactivateAll = "Clear every den";
    private const string Activate = "Activate with a new seed";
    private const string Deactivate = "Clear this den";
    private const string ResetAll = "Make every 7-star raid catchable again";

    private const SaveAction Action = SaveAction.EditWorld;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!RaidService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Raids", "Raids are edited only in Sword/Shield and Scarlet/Violet.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var areas = RaidService.GetAreas(session);
            var options = areas.Select(a => new PadOption($"{a.Name} · {RaidService.GetDens(session, a.Index).Count(d => d.Active)}/{a.Count} active")).ToList();
            if (RaidService.HasSevenStar(session))
                options.Add(new PadOption(SevenStar, Accent: UiTokens.Green, Detail: "Distribution raids already fought or caught."));
            var choice = await EditorMenu.ShowAsync(host, "Raids", null, options.ToArray());
            if (choice is null) return;
            if (choice == SevenStar)
            {
                if (!await ShowSevenStarAsync(host, session, viewModel, slot)) return;
                continue;
            }
            var index = options.FindIndex(o => o.Label == choice);
            if (index < 0 || index >= areas.Count) continue;
            if (!await ShowAreaAsync(host, session, viewModel, areas[index], slot)) return;
        }
    }

    /// <summary>One area's dens; false when a write failed and the editor should close.</summary>
    private static async Task<bool> ShowAreaAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel,
        RaidArea area, int slot)
    {
        while (true)
        {
            var dens = RaidService.GetDens(session, area.Index);
            var options = new List<PadOption>
            {
                new(ActivateAll, Accent: UiTokens.Green, Detail: "Every den gets a raid, with watts to collect."),
                new(DeactivateAll),
            };
            options.AddRange(dens.Select(d => new PadOption(DenLabel(d), Accent: d.Active ? UiTokens.Green : null)));
            var choice = await EditorMenu.ShowAsync(host, area.Name, $"{dens.Count(d => d.Active)} of {dens.Count} active.", options.ToArray());
            switch (choice)
            {
                case null:
                    return true;
                case ActivateAll:
                    if (await BlockedAsync(host, area.Name)) continue;
                    if (!await WriteAsync(viewModel, slot, s => $"{area.Name}: {RaidService.ActivateAll(s, area.Index)} dens active.")) return false;
                    continue;
                case DeactivateAll:
                    if (await BlockedAsync(host, area.Name)) continue;
                    if (!await WriteAsync(viewModel, slot, s => $"{area.Name}: {RaidService.DeactivateAll(s, area.Index)} dens cleared.")) return false;
                    continue;
            }
            var den = dens.FirstOrDefault(d => DenLabel(d) == choice);
            if (den is null) continue;
            if (!await ShowDenAsync(host, viewModel, area, den, slot)) return false;
        }
    }

    private static async Task<bool> ShowDenAsync(Grid host, BoxBrowserViewModel viewModel, RaidArea area, RaidDen den, int slot)
    {
        var title = $"{area.Name} · {DenName(den)}";
        var detail = $"{den.Detail}{Environment.NewLine}Seed {den.Seed:X}";
        if (den.Special)
        {
            await EditorMenu.ShowAsync(host, title, detail + Environment.NewLine + "The game sets this one itself.", "OK");
            return true;
        }
        if (HardcoreMode.Blocks(Action, out var status))
        {
            await EditorMenu.ShowAsync(host, title, detail + Environment.NewLine + status, "OK");
            return true;
        }
        var choice = await EditorMenu.ShowAsync(host, title, detail,
            new PadOption(Activate, Accent: UiTokens.Green, Detail: "A new raid here, watts or League Points ready again."),
            new PadOption(Deactivate));
        return choice switch
        {
            Activate => await WriteAsync(viewModel, slot, s =>
                $"{title}: {RaidService.Activate(s, area.Index, den.Index).Detail}."),
            Deactivate => await WriteAsync(viewModel, slot, s =>
            {
                RaidService.Deactivate(s, area.Index, den.Index);
                return $"{title}: cleared.";
            }),
            _ => true,
        };
    }

    private static async Task<bool> ShowSevenStarAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot)
    {
        while (true)
        {
            var raids = RaidService.GetSevenStar(session);
            var options = new List<PadOption> { new(ResetAll, Accent: UiTokens.Green) };
            options.AddRange(raids.Select(r => new PadOption(SevenStarLabel(r))));
            var choice = await EditorMenu.ShowAsync(host, SevenStar,
                "Resetting clears captured and defeated, so the raid can be done again.", options.ToArray());
            if (choice is null) return true;
            if (await BlockedAsync(host, SevenStar)) continue;
            if (choice == ResetAll)
            {
                if (!await WriteAsync(viewModel, slot, s => $"{RaidService.ResetAllSevenStar(s)} 7-star raid(s) reset.")) return false;
                continue;
            }
            var raid = raids.FirstOrDefault(r => SevenStarLabel(r) == choice);
            if (raid is null || !(raid.Captured || raid.Defeated)) continue;
            if (!await WriteAsync(viewModel, slot, s =>
            {
                RaidService.ResetSevenStar(s, raid.Index);
                return $"7-star raid {raid.Identifier} can be done again.";
            })) return false;
        }
    }

    private static async Task<bool> BlockedAsync(Grid host, string title)
    {
        if (!HardcoreMode.Blocks(Action, out var status)) return false;
        await EditorMenu.ShowAsync(host, title, status, "OK");
        return true;
    }

    private static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, int slot, Func<ISaveEngineSession, string> edit) =>
        viewModel.RunMutationAsync(s => new GenerationOutcome(true, edit(s)), slot, refreshSlot: false, action: Action);

    private static string DenName(RaidDen den) => $"#{den.Index + 1}";

    private static string DenLabel(RaidDen den) => $"{DenName(den)} · {den.Detail}";

    // The identifier is the first distribution date (yyyymmdd), so it reads as one.
    private static string SevenStarLabel(SevenStarRaid raid) =>
        $"{raid.Identifier} · {(raid.Captured ? "caught" : raid.Defeated ? "defeated" : "not done")}";
}
