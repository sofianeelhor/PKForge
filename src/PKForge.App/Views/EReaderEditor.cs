using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Gen 3 e-Reader data (Ruby, Sapphire, Emerald, FireRed, LeafGreen): the e-Reader berry the
/// save's Enigma Berries stand for, and the Battle e-Reader trainer. Each can be imported from
/// a file (only a block the game itself would accept), kept as a file, or cleared. Every write
/// goes through <see cref="BoxBrowserViewModel.RunMutationAsync"/> (Hardcore guard, restore point).
/// </summary>
public static class EReaderEditor
{
    private const string Title = "e-Reader";
    private const string ImportBerry = "Import a berry";
    private const string ExportBerry = "Keep a copy of the berry";
    private const string ClearBerry = "Clear the berry";
    private const string GiveBerry = "Add an Enigma Berry to the bag";
    private const string ImportTrainer = "Import an e-Reader trainer";
    private const string ExportTrainer = "Keep a copy of the trainer";
    private const string ClearTrainer = "Clear the trainer";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!EReaderService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, Title, "e-Reader data is kept by Ruby, Sapphire, Emerald, FireRed and LeafGreen.", "OK");
            return;
        }
        while (true)
        {
            var info = EReaderService.Get(session);
            var berry = info.Berry;
            var trainer = info.Trainer;
            var options = new List<PadOption>
            {
                new(ImportBerry, IconPath: "import", Detail: berry.Present
                    ? "Replaces it: a berry file, or a save holding one."
                    : "A berry file of this game, or a save holding one."),
            };
            if (berry.Present)
            {
                options.Add(new(ExportBerry, IconPath: "export", Detail: $"{berry.Name} as a .bin file, to bring to another save."));
                if (berry.EnigmaBerriesInBag == 0)
                    options.Add(new(GiveBerry, IconPath: "item", Detail: $"One berry to plant or hold: it is the {berry.Name}."));
                options.Add(new(ClearBerry, IconPath: "clear", Accent: UiTokens.GiftRed, Detail: "Enigma Berries become plain Enigma Berries again."));
            }
            options.Add(new(ImportTrainer, IconPath: "trainer", Detail: "A Battle e-Reader card trainer file (188 bytes)."));
            if (trainer.Present)
            {
                options.Add(new(ExportTrainer, IconPath: "export", Detail: $"{trainer.Name} as a .bin file."));
                options.Add(new(ClearTrainer, IconPath: "clear", Accent: UiTokens.GiftRed, Detail: "The game's own reset: the card trainer leaves."));
            }

            var choice = await EditorMenu.ShowAsync(host, $"{Title} · {info.Game}", Summary(info), options.ToArray());
            switch (choice)
            {
                case null: return;
                case ImportBerry: await ImportAsync(host, viewModel, "Pick an e-Reader berry or a Gen 3 save", EReaderService.ImportBerry, true); break;
                case ImportTrainer: await ImportAsync(host, viewModel, "Pick an e-Reader trainer file", EReaderService.ImportTrainer, false); break;
                case ExportBerry: await ShareAsync(host, EReaderService.ExportBerry(session), $"e-reader-berry-{Slug(berry.Name)}.bin"); break;
                case ExportTrainer: await ShareAsync(host, EReaderService.ExportTrainer(session), $"e-reader-trainer-{Slug(trainer.Name)}.bin"); break;
                case GiveBerry: await WriteAsync(host, viewModel, EReaderService.GiveEnigmaBerry, SaveAction.EditInventory); break;
                case ClearBerry:
                    var held = berry.EnigmaBerriesInBag > 0 ? $"\nThe {berry.EnigmaBerriesInBag} Enigma Berr{(berry.EnigmaBerriesInBag == 1 ? "y" : "ies")} in the bag stay, as plain Enigma Berries." : "";
                    if (await PadMenu.ConfirmAsync(host, "Clear the e-Reader berry?", $"{berry.Name} leaves this save.{held}\nRestore points can undo it.", "Clear"))
                        await WriteAsync(host, viewModel, s => Refreshed(s, EReaderService.ClearBerry(s)), SaveAction.InjectEvent);
                    break;
                case ClearTrainer:
                    if (await PadMenu.ConfirmAsync(host, "Clear the e-Reader trainer?", $"{trainer.Name} leaves this save.\nRestore points can undo it.", "Clear"))
                        await WriteAsync(host, viewModel, EReaderService.ClearTrainer, SaveAction.InjectEvent);
                    break;
            }
        }
    }

    private static string Summary(EReaderInfo info)
    {
        var berry = info.Berry.Present
            ? $"Berry: {info.Berry.Name}{(info.Berry.Valid ? "" : " (fails the game's check: ignored in game)")}. {info.Berry.Distribution}"
            : "Berry: none. Enigma Berries stay plain.";
        var trainer = info.Trainer.Present
            ? $"Trainer: {info.Trainer.Name}{(info.Trainer.Valid ? "" : " (fails the game's check: the game erases it)")}."
            : "Trainer: none.";
        return $"{berry}\n{trainer}";
    }

    private static async Task ImportAsync(Grid host, BoxBrowserViewModel viewModel, string pickerTitle,
        Func<ISaveEngineSession, ReadOnlySpan<byte>, GenerationOutcome> import, bool berry)
    {
        var pick = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = pickerTitle });
        if (pick is null) return;
        byte[] bytes;
        try
        {
            await using var stream = await pick.OpenReadAsync();
            if (stream.CanSeek && stream.Length > 4 * 1024 * 1024)
            {
                await EditorMenu.ShowAsync(host, Title, "That file is far too large to be e-Reader data.", "OK");
                return;
            }
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            bytes = copy.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await EditorMenu.ShowAsync(host, Title, "That file could not be read.", "OK");
            return;
        }
        var data = bytes;
        await WriteAsync(host, viewModel, s => berry ? Refreshed(s, import(s, data)) : import(s, data), SaveAction.InjectEvent);
    }

    /// <summary>The Enigma Berry check follows the berry the open save now holds.</summary>
    private static GenerationOutcome Refreshed(ISaveEngineSession session, GenerationOutcome outcome)
    {
        if (outcome.Success) EReaderService.RefreshLegality(session);
        return outcome;
    }

    private static async Task ShareAsync(Grid host, byte[]? bytes, string name)
    {
        if (bytes is null) return;
        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory, "export", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(path) });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("ereader", "Exporting e-Reader data failed", error);
            await EditorMenu.ShowAsync(host, Title, $"The file could not be written. {Domain.ErrorText.Plain(error)}", "OK");
        }
    }

    /// <summary>One write: Hardcore guard, restore point and atomic save write; the outcome is shown.</summary>
    private static async Task WriteAsync(Grid host, BoxBrowserViewModel viewModel, Func<ISaveEngineSession, GenerationOutcome> edit, SaveAction action)
    {
        if (await WorldEventsMenu.BlockedAsync(host, Title, "e-Reader data is read-only here.", action))
            return;
        GenerationOutcome? outcome = null;
        var ok = await viewModel.RunMutationAsync(s => outcome = edit(s), Math.Max(0, viewModel.SelectedSlot),
            refreshSlot: false, action: action);
        await EditorMenu.ShowAsync(host, Title, outcome?.Message ?? viewModel.Status, "OK");
        _ = ok;
    }

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        return slug.Length == 0 ? "data" : slug;
    }
}
