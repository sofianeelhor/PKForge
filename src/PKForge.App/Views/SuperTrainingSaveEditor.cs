using System.Globalization;
using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// X/Y and Omega Ruby/Alpha Sapphire Super Training, after PKHeX's SAV_SuperTrain: the
/// training bags, the unlocked regimens and their best times. Each Pokémon's medals live
/// with its ribbons, under Awards. Writes go through the safe mutation pipeline.
/// </summary>
public static class SuperTrainingSaveEditor
{
    private const string Bags = "Training bags";
    private const string Records = "Best times";
    private const string UnlockAll = "Unlock every regimen";
    private const string AddBag = "Add a bag…";
    private const SaveAction Action = SaveAction.EditTrainer;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!SuperTrainingService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Super Training", "Super Training exists only in X, Y, Omega Ruby and Alpha Sapphire.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var info = SuperTrainingService.Get(session);
            var held = info.Bags.Count(b => b.Bag != 0);
            var summary = $"Regimens unlocked {info.StagesUnlocked}/{info.StageCount} · bags {held}/{SuperTrainingService.BagSlots} · " +
                $"records {info.Records.Count(r => r.HasRecord)}";
            var blocked = HardcoreMode.Blocks(Action, out var status);
            if (blocked) summary += Environment.NewLine + status;
            var options = new List<PadOption>
            {
                new(Bags, Detail: "The bags waiting to be used, twelve at most."),
                new(Records, Detail: "Each regimen's two best times and who set them."),
            };
            if (!blocked)
                options.Add(new PadOption(UnlockAll, Detail: "Every regimen released in the game (never the distribution ones)."));

            var choice = await EditorMenu.ShowAsync(host, "Super Training", summary, options.ToArray());
            switch (choice)
            {
                case null: return;
                case Bags:
                    if (!await ShowBagsAsync(host, session, viewModel, slot, blocked)) return;
                    break;
                case Records:
                    if (!await ShowRecordsAsync(host, session, viewModel, slot, blocked)) return;
                    break;
                case UnlockAll:
                    if (!await WriteAsync(viewModel, slot, s => { SuperTrainingService.UnlockAllStages(s); return "Every regimen unlocked."; })) return;
                    break;
            }
        }
    }

    private static async Task<bool> ShowBagsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot, bool blocked)
    {
        while (true)
        {
            var bags = SuperTrainingService.Get(session).Bags.Where(b => b.Bag != 0).ToList();
            var items = bags.Select(b => new PickItem(b.Slot, $"{b.Slot + 1:00} · {b.Name}", Detail: blocked ? null : "Tap to remove")).ToList();
            if (!blocked && bags.Count < SuperTrainingService.BagSlots)
                items.Insert(0, new PickItem(-1, AddBag));
            if (items.Count == 0)
            {
                await EditorMenu.ShowAsync(host, Bags, "No training bags.", "OK");
                return true;
            }
            var picked = await PickerMenu.ShowAsync(host, Bags, items);
            if (picked is null || blocked) return true;
            if (picked.Id < 0)
            {
                var names = SuperTrainingService.BagNames;
                var bag = await PickerMenu.ShowAsync(host, "Add a bag", [.. names.Select((n, i) => new PickItem(i, n)).Skip(1)]);
                if (bag is null) continue;
                if (!await WriteAsync(viewModel, slot, s => SuperTrainingService.AddBag(s, bag.Id) ? $"{bag.Name} added." : "The bag slots are full."))
                    return false;
            }
            else if (!await WriteAsync(viewModel, slot, s => { SuperTrainingService.RemoveBag(s, picked.Id); return "Bag removed."; }))
                return false;
        }
    }

    private static async Task<bool> ShowRecordsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot, bool blocked)
    {
        while (true)
        {
            var records = SuperTrainingService.Get(session).Records;
            var picked = await PickerMenu.ShowAsync(host, Records, [.. records.Select(r => new PickItem(r.Index,
                $"{r.Index + 1:00} · {r.Stage}{(r.Unlocked ? "" : " · locked")}",
                Detail: r.HasRecord ? $"{Time(r.Time1)} {r.Holder1} · {Time(r.Time2)} {r.Holder2}" : "No record"))]);
            if (picked is null) return true;
            var record = records[picked.Id];
            if (blocked || !record.HasRecord) continue;
            const string clear = "Clear these records";
            var choice = await EditorMenu.ShowAsync(host, record.Stage,
                $"Best: {Time(record.Time1)} by {record.Holder1}" + Environment.NewLine + $"Second: {Time(record.Time2)} by {record.Holder2}",
                new PadOption(clear, Detail: "Both times and holders are wiped."));
            if (choice != clear) continue;
            if (!await WriteAsync(viewModel, slot, s => { SuperTrainingService.ClearRecord(s, record.Index); return $"{record.Stage}: records cleared."; }))
                return false;
        }
    }

    private static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, int slot, Func<ISaveEngineSession, string> edit) =>
        viewModel.RunMutationAsync(s => new GenerationOutcome(true, edit(s)), slot, refreshSlot: false, action: Action);

    private static string Time(float seconds) =>
        seconds > 0 ? seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "—";
}
