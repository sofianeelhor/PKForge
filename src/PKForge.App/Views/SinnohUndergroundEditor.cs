using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// The Diamond/Pearl/Platinum Underground, after PKHeX's SAV_Underground: the goods, traps,
/// treasures and spheres pouches, and the Underground records (flags included). Writes go
/// through the mutation pipeline, which makes a restore point and refuses in Hardcore mode.
/// </summary>
public static class SinnohUndergroundEditor
{
    private const string Records = "Underground records";
    private const string Add = "Add…";
    private const string Fill = "Fill the free slots";
    private const string Clear = "Empty this pouch";
    private const SaveAction Action = SaveAction.EditInventory;

    private static readonly (UndergroundPocket Pocket, string Name)[] Pockets =
    [
        (UndergroundPocket.Goods, "Goods"), (UndergroundPocket.Traps, "Traps"),
        (UndergroundPocket.Treasures, "Treasures"), (UndergroundPocket.Spheres, "Spheres"),
    ];

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!SinnohUndergroundService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Underground", "The Underground pouches exist only in Diamond, Pearl and Platinum.", "OK");
            return;
        }
        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var options = Pockets
                .Select(p => new PadOption($"{p.Name} · {SinnohUndergroundService.GetPocket(session, p.Pocket).Count}/{SinnohUndergroundService.PocketSize}"))
                .Append(new PadOption(Records, Detail: "People met, spheres, fossils, traps and flags."))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Underground", "The pouches you carry under Sinnoh.", options);
            if (choice is null) return;
            var keepGoing = choice == Records
                ? await ShowRecordsAsync(host, session, viewModel, slot)
                : await ShowPocketAsync(host, session, viewModel, slot, Pockets.First(p => choice.StartsWith($"{p.Name} ·", StringComparison.Ordinal)));
            if (!keepGoing) return;
        }
    }

    /// <summary>One pouch; false when a write failed and the editor should close.</summary>
    private static async Task<bool> ShowPocketAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot,
        (UndergroundPocket Pocket, string Name) pocket)
    {
        var spheres = pocket.Pocket == UndergroundPocket.Spheres;
        while (true)
        {
            var held = SinnohUndergroundService.GetPocket(session, pocket.Pocket);
            var options = held.Select(item => new PadOption(Label(item, spheres)))
                .Append(new PadOption(Add))
                .Append(new PadOption(Fill, IconPath: "fill",
                    Detail: spheres ? "Free slots get large spheres of size 99." : "Free slots get one of each item you do not have yet."))
                .Append(new PadOption(Clear, IconPath: "clear"))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, pocket.Name, $"{held.Count} of {SinnohUndergroundService.PocketSize} slots used.", options);
            switch (choice)
            {
                case null:
                    return true;
                case Add:
                {
                    if (held.Count >= SinnohUndergroundService.PocketSize)
                    {
                        await EditorMenu.ShowAsync(host, pocket.Name, "This pouch is full.", "OK");
                        continue;
                    }
                    var choices = SinnohUndergroundService.GetChoices(pocket.Pocket);
                    var name = await EditorMenu.ShowAsync(host, $"Add to {pocket.Name.ToLowerInvariant()}", null, [.. choices.Select(c => c.Name)]);
                    if (name is null) continue;
                    var id = choices.First(c => c.Name == name).Id;
                    var size = SinnohUndergroundService.MaxSphereSize;
                    if (spheres && await StatsPopup.ShowSingleAsync(host, "Sphere size", size, SinnohUndergroundService.MaxSphereSize) is { } picked)
                        size = picked;
                    if (!await WriteAsync(viewModel, slot, s => SinnohUndergroundService.Add(s, pocket.Pocket, id, size)
                            ? $"{name} added." : "This pouch is full."))
                        return false;
                    continue;
                }
                case Fill:
                    if (!await WriteAsync(viewModel, slot, s => $"{SinnohUndergroundService.Fill(s, pocket.Pocket)} added to {pocket.Name.ToLowerInvariant()}."))
                        return false;
                    continue;
                case Clear:
                    if (!await PadMenu.ConfirmAsync(host, $"Empty {pocket.Name.ToLowerInvariant()}?", "Restore points can undo it.", "Empty"))
                        continue;
                    if (!await WriteAsync(viewModel, slot, s => { SinnohUndergroundService.Clear(s, pocket.Pocket); return $"{pocket.Name} emptied."; }))
                        return false;
                    continue;
            }

            var item = held.FirstOrDefault(i => Label(i, spheres) == choice);
            if (item is null) continue;
            var actions = spheres ? new[] { "Resize", "Remove" } : ["Remove"];
            var action = await EditorMenu.ShowAsync(host, item.Name, null, actions);
            if (action == "Resize")
            {
                if (await StatsPopup.ShowSingleAsync(host, "Sphere size", item.Count, SinnohUndergroundService.MaxSphereSize) is not { } size || size == item.Count)
                    continue;
                if (!await WriteAsync(viewModel, slot, s => $"{item.Name} is now size {SinnohUndergroundService.SetSphereSize(s, item.Slot, size)}."))
                    return false;
            }
            else if (action == "Remove")
            {
                if (!await WriteAsync(viewModel, slot, s => { SinnohUndergroundService.Remove(s, pocket.Pocket, item.Slot); return $"{item.Name} removed."; }))
                    return false;
            }
        }
    }

    private static async Task<bool> ShowRecordsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot)
    {
        while (true)
        {
            var records = SinnohUndergroundService.GetRecords(session);
            var labels = records.Select(r => $"{r.Name} · {r.Count:N0}").ToArray();
            var choice = await EditorMenu.ShowAsync(host, Records, null, labels);
            var index = Array.IndexOf(labels, choice);
            if (index < 0) return true;
            var record = records[index];
            if (await StatsPopup.ShowSingleAsync(host, record.Name, record.Count, record.MaxCount) is not { } value || value == record.Count)
                continue;
            if (!await WriteAsync(viewModel, slot, s => $"{record.Name}: {SinnohUndergroundService.SetRecord(s, record.Id, value):N0}."))
                return false;
        }
    }

    private static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, int slot, Func<ISaveEngineSession, string> edit) =>
        viewModel.RunMutationAsync(s => new GenerationOutcome(true, edit(s)), slot, refreshSlot: false, action: Action);

    private static string Label(UndergroundSlot item, bool spheres) =>
        spheres ? $"{item.Slot + 1:00} · {item.Name} · size {item.Count}" : $"{item.Slot + 1:00} · {item.Name}";
}
