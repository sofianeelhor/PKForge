using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// The poffin case (DPPt, BDSP) and the Pokéblock case (RSE, ORAS), after PKHeX's case editors:
/// list what is in the case, fill it with the best treats, delete one or all. Writes go through
/// the mutation pipeline, which makes a restore point and refuses in Hardcore mode.
/// </summary>
public static class ContestTreatsEditor
{
    private const string FillAll = "Fill the case";
    private const string DeleteAll = "Empty the case";
    private const SaveAction Action = SaveAction.EditInventory;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var kind = TreatCaseService.GetKind(session);
        var slot = Math.Max(0, viewModel.SelectedSlot);
        var poffins = kind is TreatCaseKind.Poffins4 or TreatCaseKind.Poffins8b;
        var title = poffins ? "Poffin case" : "Pokéblock case";
        if (kind == TreatCaseKind.None)
        {
            await EditorMenu.ShowAsync(host, title, "This game has no poffin or Pokéblock case.", "OK");
            return;
        }
        if (kind == TreatCaseKind.PokeBlocks6)
        {
            await ShowOrasAsync(host, session, viewModel, slot);
            return;
        }

        while (true)
        {
            var treats = TreatCaseService.GetTreats(session);
            var options = treats.Select(t => new PadOption(Label(t), Detail: Flavors(t, poffins)))
                .Append(new PadOption(FillAll, IconPath: "fill",
                    Detail: poffins ? "Every slot becomes a level-max poffin." : "Every slot becomes a Gold Pokéblock at full strength."))
                .Append(new PadOption(DeleteAll, IconPath: "clear", Detail: "Every treat is thrown away."))
                .ToArray();
            var message = $"{treats.Count} of {TreatCaseService.Capacity(session)} slots used. Pick one to throw it away.";
            var choice = await EditorMenu.ShowAsync(host, title, message, options);
            if (choice is null) return;

            if (choice is FillAll or DeleteAll)
            {
                var filling = choice == FillAll;
                if (!await PadMenu.ConfirmAsync(host, filling ? "Fill the case?" : "Empty the case?",
                        filling ? "Every slot is overwritten. Restore points can undo it." : "Every treat is deleted. Restore points can undo it.",
                        filling ? "Fill" : "Empty"))
                    continue;
                var saved = await viewModel.RunMutationAsync(s =>
                {
                    if (filling) TreatCaseService.Fill(s);
                    else TreatCaseService.DeleteAll(s);
                    return new GenerationOutcome(true, filling ? $"The {title.ToLowerInvariant()} is full." : $"The {title.ToLowerInvariant()} is empty.");
                }, slot, refreshSlot: false, action: Action);
                if (!saved) return;
                continue;
            }

            var picked = treats.FirstOrDefault(t => Label(t) == choice);
            if (picked is null) continue;
            if (!await PadMenu.ConfirmAsync(host, $"Throw away {picked.Name}?", Flavors(picked, poffins), "Throw away"))
                continue;
            var deleted = await viewModel.RunMutationAsync(s =>
            {
                TreatCaseService.Delete(s, picked.Slot);
                return new GenerationOutcome(true, $"{picked.Name} (slot {picked.Slot + 1}) thrown away.");
            }, slot, refreshSlot: false, action: Action);
            if (!deleted) return;
        }
    }

    /// <summary>ORAS keeps counters, not a case: one stack per block colour, 0-999.</summary>
    private static async Task ShowOrasAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int slot)
    {
        while (true)
        {
            var counts = TreatCaseService.GetBlockCounts(session);
            var options = counts.Select(c => new PadOption($"{c.Name} · {c.Count}/{c.MaxCount}"))
                .Append(new PadOption(FillAll, IconPath: "fill", Detail: "Every Pokéblock goes to 999."))
                .Append(new PadOption(DeleteAll, IconPath: "clear", Detail: "Every Pokéblock goes to 0."))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Pokéblock case", "Omega Ruby and Alpha Sapphire count each kind of Pokéblock.", options);
            if (choice is null) return;

            if (choice is FillAll or DeleteAll)
            {
                var filling = choice == FillAll;
                var saved = await viewModel.RunMutationAsync(s =>
                {
                    if (filling) TreatCaseService.Fill(s);
                    else TreatCaseService.DeleteAll(s);
                    return new GenerationOutcome(true, filling ? "Every Pokéblock is at 999." : "Every Pokéblock is at 0.");
                }, slot, refreshSlot: false, action: Action);
                if (!saved) return;
                continue;
            }

            var picked = counts.FirstOrDefault(c => choice.StartsWith($"{c.Name} ·", StringComparison.Ordinal));
            if (picked is null) continue;
            var next = await StatsPopup.ShowSingleAsync(host, picked.Name, picked.Count, picked.MaxCount);
            if (next is not { } count || count == picked.Count) continue;
            var savedCount = await viewModel.RunMutationAsync(s =>
                new GenerationOutcome(true, $"{picked.Name} ×{TreatCaseService.SetBlockCount(s, picked.Id, count)}"),
                slot, refreshSlot: false, action: Action);
            if (!savedCount) return;
        }
    }

    private static string Label(ContestTreat treat) => $"{treat.Slot + 1:000} · {treat.Name} · Lv. {treat.Level}";

    private static string Flavors(ContestTreat t, bool poffins) =>
        $"Spicy {t.Spicy} · Dry {t.Dry} · Sweet {t.Sweet} · Bitter {t.Bitter} · Sour {t.Sour} · {(poffins ? "Smoothness" : "Feel")} {t.Smoothness}";
}
