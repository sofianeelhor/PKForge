using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Diamond/Pearl/Platinum honey trees, after PKHeX's SAV_HoneyTree. Which four trees can
/// hold Munchlax is fixed by the trainer ID, so that list is always shown - Hardcore
/// included. Slathering and encounter edits are save-data writes and go through the
/// safe mutation pipeline, which Hardcore refuses.
/// </summary>
public static class HoneyTreeEditor
{
    private const string MunchlaxReady = "Munchlax waits here";
    private const string Slather = "Slather honey (catchable now)";
    private const string SetEncounter = "Set encounter…";
    private const string SetShakes = "Set shakes…";

    // Hex-editor-grade data edit: no dedicated SaveAction exists for world state yet.
    private const SaveAction Action = SaveAction.EditWorld;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!HoneyTreeService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "HONEY TREES", "Honey trees exist only in Diamond, Pearl and Platinum.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var trees = HoneyTreeService.GetTrees(session);
            var munchlax = HoneyTreeService.GetMunchlaxTrees(session);
            var summary = "Munchlax trees for this trainer: " +
                string.Join(", ", munchlax.Select(i => HoneyTreeService.Locations[i])) + ".";
            var options = trees.Select(tree => new PadOption(TreeLabel(tree),
                Accent: tree.IsMunchlaxTree ? UiTokens.Green : null)).ToArray();
            var choice = await EditorMenu.ShowAsync(host, "HONEY TREES", summary, options);
            if (choice is null) return;
            var index = Array.FindIndex(options, o => o.Label == choice);
            if (index < 0) continue;
            if (!await ShowTreeAsync(host, session, viewModel, trees[index], slot)) return;
        }
    }

    /// <summary>One tree's detail and actions; false when a write failed and the editor should close.</summary>
    private static async Task<bool> ShowTreeAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel,
        HoneyTree tree, int slot)
    {
        var detail = $"{HoneyTreeService.GroupNames[Math.Min(tree.Group, HoneyTreeService.GroupMunchlax)]} · slot {tree.Slot + 1} · " +
            $"{tree.SpeciesName} · {tree.Shakes} shake(s) · timer {tree.Time} min" +
            (tree.IsMunchlaxTree ? Environment.NewLine + "This is one of your Munchlax trees." : "");

        if (HardcoreMode.Blocks(Action, out var status))
        {
            await EditorMenu.ShowAsync(host, tree.Location.ToUpperInvariant(), detail + Environment.NewLine + status, "OK");
            return true;
        }

        var actions = new List<PadOption>();
        if (tree.IsMunchlaxTree)
            actions.Add(new PadOption(MunchlaxReady, Accent: UiTokens.Green));
        actions.Add(new PadOption(Slather));
        actions.Add(new PadOption(SetEncounter));
        actions.Add(new PadOption(SetShakes));
        var choice = await EditorMenu.ShowAsync(host, tree.Location.ToUpperInvariant(), detail, actions.ToArray());
        switch (choice)
        {
            case MunchlaxReady:
                return await WriteAsync(viewModel, slot, s => HoneyTreeService.SetMunchlaxReady(s, tree.Index),
                    t => $"Munchlax is waiting in the {t.Location} tree.");
            case Slather:
                return await WriteAsync(viewModel, slot, s => HoneyTreeService.Slather(s, tree.Index),
                    t => $"{t.Location}: honey slathered, {t.SpeciesName} catchable.");
            case SetEncounter:
            {
                var group = await PickGroupAsync(host, tree);
                if (group is not { } g) return true;
                var species = HoneyTreeService.GetGroupSpecies(session, g);
                var slotChoice = g == HoneyTreeService.GroupNone ? 0 : await PickSlotAsync(host, species);
                if (slotChoice is not { } encounterSlot) return true;
                return await WriteAsync(viewModel, slot,
                    s => HoneyTreeService.SetTree(s, tree.Index, tree.Time, g, encounterSlot, tree.Shakes),
                    t => $"{t.Location}: {t.SpeciesName} (slot {t.Slot + 1}).");
            }
            case SetShakes:
            {
                var shakes = await StatsPopup.ShowSingleAsync(host, "SHAKES", tree.Shakes, HoneyTreeService.MaxShakes);
                if (shakes is not { } next || next == tree.Shakes) return true;
                return await WriteAsync(viewModel, slot,
                    s => HoneyTreeService.SetTree(s, tree.Index, tree.Time, tree.Group, tree.Slot, next),
                    t => $"{t.Location}: {t.Shakes} shake(s).");
            }
            default:
                return true;
        }
    }

    /// <summary>Munchlax is only offered on this trainer's Munchlax trees: PKHeX warns a
    /// Munchlax caught anywhere else is illegal for the TID16/SID16.</summary>
    private static async Task<int?> PickGroupAsync(Grid host, HoneyTree tree)
    {
        var groups = Enumerable.Range(0, HoneyTreeService.GroupMunchlax + 1)
            .Where(g => g != HoneyTreeService.GroupMunchlax || tree.IsMunchlaxTree)
            .ToArray();
        var labels = groups.Select(g => HoneyTreeService.GroupNames[g]).ToArray();
        var choice = await EditorMenu.ShowAsync(host, "ENCOUNTER GROUP", null, labels);
        var index = Array.IndexOf(labels, choice);
        return index < 0 ? null : groups[index];
    }

    private static async Task<int?> PickSlotAsync(Grid host, IReadOnlyList<string> species)
    {
        var labels = species.Select((name, i) => $"Slot {i + 1} · {name}").ToArray();
        var choice = await EditorMenu.ShowAsync(host, "ENCOUNTER SLOT", null, labels);
        var index = Array.IndexOf(labels, choice);
        return index < 0 ? null : index;
    }

    private static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, int slot,
        Func<ISaveEngineSession, HoneyTree> edit, Func<HoneyTree, string> message) =>
        viewModel.RunMutationAsync(s => new GenerationOutcome(true, message(edit(s))),
            slot, refreshSlot: false, action: Action);

    private static string TreeLabel(HoneyTree tree) =>
        $"{(tree.IsMunchlaxTree ? "★ " : "")}{tree.Location} · {tree.SpeciesName}{(tree.IsReady ? " · ready" : "")}";
}
