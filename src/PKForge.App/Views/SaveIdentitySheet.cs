using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The player's say over a save: its name, its cartridge color, which game it really is,
/// and whether it shows at all. The engine only reports what the bytes say; everything
/// here persists per document id and wins.
/// </summary>
public static class SaveIdentitySheet
{
    public const string OpenOption = "Open save";
    private const string RenameOption = "Rename";
    private const string ColorOption = "Cartridge color";
    private const string GameOption = "Set game…";
    private const string HideOption = "Hide save";
    private const string ResetOption = "Reset to detected";
    private const string ReadOnlyOption = "Make read-only";

    /// <summary>
    /// Shows the save menu. Returns true when the player picked "Open save" (the caller
    /// opens it); every other action edits the identity in place and returns false.
    /// </summary>
    public static async Task<bool> ShowAsync(Grid host, SavePickerViewModel picker, DetectedSave save, bool offerOpen = true)
    {
        var store = picker.Identities;
        var guess = (picker.DetectedFor(save.DocumentId) ?? save).Guess;
        var resolved = save.Identity;

        var options = new List<PadOption>();
        if (offerOpen) options.Add(new PadOption(OpenOption, IconPath: "play"));
        options.Add(new PadOption(RenameOption, IconPath: "rename"));
        options.Add(new PadOption(ColorOption, Glyph: "●", Accent: SaveColors.For(resolved?.ColorKey, save.Generation)));
        if (guess is not null) options.Add(new PadOption(GameOption, IconPath: "game"));
        // Only saves the box already warned about as a suspected ROM hack carry the choice.
        var flaggedHack = RomHackNotice.IsFlagged(save.DocumentId);
        var acceptedHack = store.Get(save.DocumentId)?.AcceptedHackRisk == true;
        if (flaggedHack && acceptedHack)
            options.Add(new PadOption(ReadOnlyOption, IconPath: "box", Detail: $"{RomHackNotice.Marker} · editing allowed at your own risk"));
        else if (flaggedHack)
            options.Add(new PadOption(RomHackNotice.EditOption, Glyph: "!", Accent: UiTokens.Bad, Detail: $"{RomHackNotice.Marker} · read-only now"));
        options.Add(new PadOption(HideOption, IconPath: "hide", Detail: "Remove from Home and pickers. Settings can show it again."));
        if (resolved is { IsCustomized: true }) options.Add(new PadOption(ResetOption, IconPath: "restore"));

        var choice = await PadMenu.ShowAsync(host, save.GameLabel, Describe(save), [.. options]);
        var current = store.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
        switch (choice)
        {
            case OpenOption:
                return true;
            case RenameOption:
                await RenameAsync(host, store, current, current.DisplayName ?? guess?.SuggestedName ?? save.GameLabel);
                return false;
            case ColorOption:
                await PickColorAsync(host, store, current, save.Generation);
                return false;
            case GameOption when guess is not null:
                await PickGameAsync(host, store, current, guess);
                return false;
            case RomHackNotice.EditOption when flaggedHack:
                if (await PadMenu.ConfirmAsync(host, RomHackNotice.Title, RomHackNotice.Warning, RomHackNotice.EditOption))
                    Writer?.ConfirmLayoutRisk(save.DocumentId);
                return false;
            case ReadOnlyOption:
                Writer?.RevokeLayoutRisk(save.DocumentId);
                return false;
            case HideOption:
                store.Set(current with { Hidden = true });
                return false;
            case ResetOption:
                if (await PadMenu.ConfirmAsync(host, "Reset to detected",
                        "Forget this save's name, color, game choice and ROM-hack editing choice, and show it again if hidden? The save file itself is not touched.", "Reset"))
                    store.Reset(save.DocumentId);
                return false;
            default:
                return false;
        }
    }

    private static ISafeSaveWriter? Writer => IPlatformApplication.Current?.Services.GetService<ISafeSaveWriter>();

    private static string Describe(DetectedSave save)
    {
        var lines = new List<string>();
        var game = save.Identity?.GameLabel ?? save.GameLabel;
        lines.Add(save.Identity?.GameChoiceId is not null ? $"{game} · set by you" : $"{game} · read from the save");
        lines.Add($"{save.FileName} · {SaveDescriptions.Detail(save)}");
        if (!string.IsNullOrEmpty(save.TrainerName))
            lines.Add(string.IsNullOrEmpty(save.PlayTime) ? save.TrainerName! : $"{save.TrainerName} · {save.PlayTime}");
        return string.Join("\n", lines);
    }

    private static async Task RenameAsync(Grid host, ISaveIdentityStore store, SaveIdentity current, string shown)
    {
        var text = await TextPopup.ShowLineAsync(host, "Name this save", "Leave empty to use the game's name", shown);
        if (text is null) return;
        store.Set(current with { DisplayName = text.Length == 0 ? null : text[..Math.Min(text.Length, 32)] });
    }

    private static async Task PickColorAsync(Grid host, ISaveIdentityStore store, SaveIdentity current, int generation)
    {
        const string eraOption = "Console color";
        var options = new List<PadOption> { new(eraOption, Glyph: "●", Accent: Kit.EraColor(generation)) };
        options.AddRange(SaveIdentityPalette.Swatches.Select(s =>
            new PadOption(s.Key == current.ColorKey ? $"{s.Name} ✓" : s.Name, Glyph: "●", Accent: SaveColors.For(s.Key, generation))));
        var choice = await PadMenu.ShowAsync(host, "Cartridge color", null, [.. options]);
        if (choice is null) return;
        var picked = SaveIdentityPalette.Swatches.FirstOrDefault(s => choice.StartsWith(s.Name, StringComparison.Ordinal));
        store.Set(current with { ColorKey = choice == eraOption ? null : picked.Key });
    }

    private static async Task PickGameAsync(Grid host, ISaveIdentityStore store, SaveIdentity current, SaveIdentityGuess guess)
    {
        var choices = SaveIdentityRules.ChoicesFor(guess.Family, guess.Label);
        var options = choices.Select(c => new PadOption(
            c.Id == current.GameChoiceId ? $"{c.Label} ✓" : c.Label,
            IconPath: c.IsHack ? "patch" : "game")).ToArray();
        var message = guess.Family switch
        {
            SaveLayoutFamily.Cfru or SaveLayoutFamily.UnboundCfru or SaveLayoutFamily.GsChroniclesCfru =>
                "This save uses a CFRU layout. Only games that write it are listed, so PKForge never opens it with the wrong engine.",
            SaveLayoutFamily.Other => "Pick the retail game, or mark it as a ROM hack built on it.",
            _ => "Only games that share this save's layout are listed. A ROM hack built on it keeps the retail engine.",
        };
        var choice = await PadMenu.ShowAsync(host, "This game is…", message, options);
        if (choice is null) return;
        var picked = choices.First(c => choice.StartsWith(c.Label, StringComparison.Ordinal));
        var updated = current with { GameChoiceId = picked.Id };
        store.Set(updated);
        // A hack deserves its own name on the shelf: ask for it right away (file name as the prefill).
        if (picked.IsHack && picked.ArtLabel is null)
            await RenameAsync(host, store, store.Get(current.DocumentId) ?? updated, updated.DisplayName ?? guess.SuggestedName ?? "");
    }
}

/// <summary>The curated cartridge palette: saturated enough to read on the grey housing, never neon.</summary>
public static class SaveColors
{
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.Ordinal)
    {
        ["navy"] = Color.FromArgb("#34509A"),
        ["sky"] = Color.FromArgb("#4FB6DB"),
        ["teal"] = Color.FromArgb("#2FA39A"),
        ["leaf"] = Color.FromArgb("#5DAE4A"),
        ["gold"] = Color.FromArgb("#E2B53A"),
        ["amber"] = Color.FromArgb("#EE9A4A"),
        ["coral"] = Color.FromArgb("#E4675A"),
        ["berry"] = Color.FromArgb("#C0457A"),
        ["violet"] = Color.FromArgb("#8B6AD8"),
        ["slate"] = Color.FromArgb("#5E6B7A"),
    };

    /// <summary>The save's chosen color, or its console era color.</summary>
    public static Color For(string? key, int generation) =>
        key is not null && Palette.TryGetValue(key, out var color) ? color : Kit.EraColor(generation);
}
