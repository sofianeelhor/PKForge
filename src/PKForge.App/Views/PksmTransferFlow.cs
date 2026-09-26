using System.Text;
using PKForge.App.Services;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;

namespace PKForge.App.Views;

/// <summary>
/// The Bank's PKSM bridge: bring a 3DS PKSM bank (.bnk + .json), a PKSM dumps zip or loose
/// .pk files into the bank with a preview first, and write bank boxes back out as a PKSM
/// bank the 3DS can open. Format rules live in <see cref="PksmBankFile"/>; this is the UI.
/// </summary>
public static class PksmTransferFlow
{
    /// <summary>Box names imported from PKSM (or set later); shown beside the box number.</summary>
    public static BankBoxNames BoxNames => BankBoxDecor.Names;

    /// <summary>Engine decode, mapped from PKSM tags to chronological generations.</summary>
    public static PksmDecoded Decode(PksmGeneration? generation, byte[] bytes, string source)
    {
        var number = generation is { } g ? PksmBankFile.GenerationNumber(g) : (int?)null;
        if (number == 0) return PksmDecoded.Skip($"unknown generation tag 0x{(uint)generation!.Value:X}");
        var result = PksmEntityConversion.Decode(number, generation == PksmGeneration.LetsGo, bytes, source);
        return new PksmDecoded(result.Bytes, result.Info, result.Reason);
    }

    public static PksmEncoded Encode(byte[] bankBytes, string? format)
    {
        var result = PksmEntityConversion.Encode(bankBytes, format);
        return result.Data is not null && PksmBankFile.FromGenerationNumber(result.Generation, result.LetsGo) is { } tag
            ? new PksmEncoded(tag, result.Data, null)
            : PksmEncoded.Skip(result.Reason ?? "PKSM has no slot for this format");
    }

    /// <summary>
    /// Pick → preview → write. Importing your own PKSM bank is a transfer from your 3DS, so
    /// Hardcore mode allows it (every entry is tagged with its PKSM origin); loose files and
    /// dumps are copies from outside the games, which Hardcore refuses like any .pk import.
    /// </summary>
    public static async Task<string?> ImportAsync(Grid host, IBankService bank)
    {
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IDocumentPicker>();
        var access = services?.GetService<ISaveFileAccess>();
        if (picker is null || access is null) return null;

        var go = await PadMenu.ShowAsync(host, "Import from PKSM",
            "Pick the bank's .bnk (and its .json for box names), a zip of /3ds/PKSM/dumps, or .pk files.\n" + PksmBankTransfer.SdCardHelp,
            new PadOption("Choose files…", IconPath: "folder"));
        if (go is null) return null;
        var documents = await picker.PickManyAsync();
        if (documents.Count == 0) return null;

        var overlay = LoadingOverlay.Show(host, "Reading PKSM files…", $"{documents.Count} file(s).");
        List<PksmImportPlan> plans;
        var notes = new List<string>();
        try
        {
            var files = new List<(string, byte[])>();
            foreach (var document in documents)
                files.Add((document.DisplayName, (await access.ReadAsync(document.DocumentId)).ToArray()));
            (plans, notes) = await Task.Run(() => BuildPlans(files));
        }
        catch (Exception error)
        {
            return $"PKSM import failed: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }

        if (HardcoreMode.IsOn && plans.Any(p => !p.IsBank && p.ReadyCount > 0))
        {
            plans.RemoveAll(p => !p.IsBank);
            notes.Add("Loose .pk files and dumps are copies - skipped in Hardcore mode (banks still import).");
        }
        if (plans.Sum(p => p.ReadyCount) == 0)
        {
            var why = new StringBuilder("Nothing importable found.");
            foreach (var plan in plans) AppendPlan(why, plan);
            foreach (var note in notes) why.Append('\n').Append(note);
            await PadMenu.ShowAsync(host, "PKSM import", why.ToString(), "OK");
            return "No importable Pokémon in those files.";
        }

        var preview = new StringBuilder();
        foreach (var plan in plans) AppendPlan(preview, plan);
        foreach (var note in notes) preview.Append('\n').Append(note);
        preview.Append("\nExact copies of mons already in the bank are skipped.");
        var choice = await PadMenu.ShowAsync(host, $"Import {plans.Sum(p => p.ReadyCount)} pokémon?", preview.ToString().Trim(),
            new PadOption("Add as new boxes (keep layout + names)", IconPath: "box"),
            new PadOption("Merge into free slots", IconPath: "compact"));
        if (choice is null) return "PKSM import cancelled.";
        var mode = choice.StartsWith("Add", StringComparison.Ordinal) ? PksmImportMode.NewBoxes : PksmImportMode.Merge;

        var writing = LoadingOverlay.Show(host, "Filling the bank…", "Writing entries.");
        try
        {
            var result = await Task.Run(() => PksmBankTransfer.Apply(bank, BoxNames, plans, mode));
            var where = mode == PksmImportMode.NewBoxes && result.BoxesUsed > 0
                ? $" into boxes {result.FirstNewBox + 1:00}-{result.FirstNewBox + result.BoxesUsed:00}"
                : "";
            var skipped = plans.Sum(p => p.SkippedCount);
            return $"PKSM: {result.Imported} imported{where}"
                   + (result.Duplicates > 0 ? $" · {result.Duplicates} duplicate(s) skipped" : "")
                   + (skipped > 0 ? $" · {skipped} unreadable" : "") + ".";
        }
        catch (Exception error)
        {
            return $"PKSM import failed: {error.Message}";
        }
        finally
        {
            writing.Close();
        }
    }

    private static (List<PksmImportPlan>, List<string>) BuildPlans(IEnumerable<(string, byte[])> files)
    {
        var sources = PksmBankTransfer.Collect(files);
        var plans = new List<PksmImportPlan>();
        var notes = new List<string>();
        foreach (var (name, bnk, json) in sources.Banks)
        {
            try { plans.Add(PksmBankTransfer.PlanBank(name, bnk, json, Decode)); }
            catch (InvalidDataException error) { notes.Add(error.Message); }
        }
        if (sources.LooseFiles.Count > 0)
            plans.Add(PksmBankTransfer.PlanLoose($"{sources.LooseFiles.Count} loose file(s)", sources.LooseFiles, Decode));
        if (sources.Ignored.Count > 0)
            notes.Add($"Ignored: {string.Join(", ", sources.Ignored.Take(4))}{(sources.Ignored.Count > 4 ? "…" : "")}");
        return (plans, notes);
    }

    private static void AppendPlan(StringBuilder text, PksmImportPlan plan)
    {
        text.Append('\n').Append(plan.IsBank
            ? $"{plan.Source}.bnk (v{(plan.SourceVersion == 0 ? "bank.bin" : plan.SourceVersion)}, {plan.UsedBoxes} box(es) used): {plan.ReadyCount} ready"
            : $"{plan.Source}: {plan.ReadyCount} ready");
        if (plan.CountsByGeneration.Count > 0)
            text.Append(" - ").Append(string.Join(", ", plan.CountsByGeneration.Select(c => $"{c.Label} ×{c.Count}")));
        if (plan.Truncated) text.Append("\n  The file is shorter than its header says; the missing slots read as empty.");
        foreach (var (reason, count) in plan.SkipReasons.Take(4))
            text.Append($"\n  skipped {count}: {reason}");
    }

    /// <summary>Writes <paramref name="entries"/> as NAME.bnk + NAME.json into a picked folder.</summary>
    public static async Task<string?> ExportAsync(Grid host, IBankService bank, IReadOnlyList<BankEntry> entries, PksmExportLayout layout, string label)
    {
        if (entries.Count == 0) return "Nothing to export.";
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IFolderPicker>();
        var files = services?.GetService<IFolderFileAccess>();
        if (picker is null || files is null) return null;

        var typed = await TextPopup.ShowAsync(host, "PKSM bank name",
            "Becomes NAME.bnk + NAME.json. Blank = pkforge. Use pksm_1 only to replace PKSM's default bank.");
        if (typed is null) return null;
        var name = PksmBankTransfer.SanitizeBankName(typed);
        var folder = await picker.PickFolderAsync();
        if (folder is null) return null;

        var overlay = LoadingOverlay.Show(host, "Building a PKSM bank…", $"{entries.Count} Pokémon from {label}.");
        PksmExportResult result;
        try
        {
            result = await Task.Run(() => PksmBankTransfer.Export(bank, entries, Encode, BoxNames.Get, layout));
            if (result.Written > 0)
            {
                await files.WriteFileAsync(folder.TreeId, name + ".bnk", result.Bank);
                await files.WriteFileAsync(folder.TreeId, name + ".json", result.BoxNames);
            }
        }
        catch (Exception error)
        {
            return $"PKSM export failed: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }

        var message = new StringBuilder(result.Written == 0
            ? "None of these Pokémon fit a PKSM bank."
            : $"{name}.bnk: {result.Written} Pokémon in {result.Boxes} box(es) → {folder.DisplayName}.");
        foreach (var group in result.Skipped.GroupBy(s => s.Reason).Take(4))
            message.Append($"\nLeft out {group.Count()}: {group.Key}");
        if (result.Written > 0)
            message.Append("\n\n").Append(PksmBankTransfer.ReturnHelp.Replace("NAME", name).Replace("box count", result.Boxes.ToString()));
        await PadMenu.ShowAsync(host, "PKSM export", message.ToString(), "OK");
        return result.Written == 0
            ? "PKSM export: nothing PKSM can hold."
            : $"PKSM bank written: {result.Written} Pokémon ({result.Skipped.Count} left out) → {folder.DisplayName}/{name}.bnk";
    }
}
