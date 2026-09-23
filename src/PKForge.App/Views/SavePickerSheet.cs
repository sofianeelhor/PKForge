using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The transfer destination sheet: every save by its display name and cartridge color.
/// Returns the chosen save, or null on cancel.
/// </summary>
public static class SavePickerSheet
{
    public static async Task<DetectedSave?> PickAsync(
        Grid host, IReadOnlyList<DetectedSave> saves, string title, string? message, string? excludeDocumentId = null)
    {
        var candidates = saves.Where(s => s.DocumentId != excludeDocumentId).ToArray();
        if (candidates.Length == 0) return null;

        // Each row is the player's own name for the save, in its cartridge color. Twins
        // (same name) are told apart by trainer, then file and date, then a number.
        var labels = candidates.Select(s => s.GameLabel).ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            if (candidates.Count(x => x.GameLabel == candidates[i].GameLabel) <= 1) continue;
            var save = candidates[i];
            if (!string.IsNullOrEmpty(save.TrainerName)) labels[i] += $" · {save.TrainerName}";
            labels[i] += $" · {save.FileName}";
            if (save.LastModified is { } modified) labels[i] += $" · {modified:yyyy-MM-dd}";
        }
        for (var i = 0; i < labels.Length; i++)
        {
            // Number identical labels against the pristine list; suffixing in place
            // would shift the comparisons as we go.
            if (labels.Count(label => label == labels[i]) <= 1) continue;
            var ordinal = Enumerable.Range(0, i + 1).Count(j => labels[j] == labels[i]);
            labels[i] += $" (#{ordinal})";
        }
        var options = candidates.Select((s, i) => new PadOption(labels[i], Glyph: "●",
            Accent: SaveColors.For(s.Identity?.ColorKey, s.Generation))).ToArray();
        var choice = await PadMenu.ShowAsync(host, title, message, options);
        if (choice is null) return null;
        var index = Array.FindIndex(options, o => o.Label == choice);
        return index < 0 ? null : candidates[index];
    }

    /// <summary>
    /// The chooser behind a shared cartridge: one row per save of that game, newest first.
    /// A single save is returned without asking unless <paramref name="allowSingle"/>.
    /// The row leads with who is playing (trainer, playtime, language), the quiet line
    /// says where it lives (file, emulator, folder, date). Returns null on cancel.
    /// </summary>
    public static async Task<DetectedSave?> ChooseFromTileAsync(
        Grid host, IReadOnlyList<DetectedSave> saves, string title, string? message, bool allowSingle = false)
    {
        if (saves.Count == 0) return null;
        if (saves.Count == 1 && !allowSingle) return saves[0];
        var labels = saves.Select(RowLabel).ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            // The menu answers with the label, so twins need a number.
            if (labels.Count(label => label == labels[i]) <= 1) continue;
            var ordinal = Enumerable.Range(0, i + 1).Count(j => labels[j] == labels[i]);
            labels[i] += $" (#{ordinal})";
        }
        var options = saves.Select((s, i) => new PadOption(labels[i], Glyph: "●",
            Accent: SaveColors.For(s.Identity?.ColorKey, s.Generation), Detail: RowDetail(s))).ToArray();
        var choice = await PadMenu.ShowAsync(host, title, message, options);
        var index = choice is null ? -1 : Array.FindIndex(options, o => o.Label == choice);
        return index < 0 ? null : saves[index];
    }

    private static string RowLabel(DetectedSave save)
    {
        var who = string.IsNullOrEmpty(save.TrainerName) ? save.FileName : save.TrainerName!;
        if (!string.IsNullOrEmpty(save.TrainerName) && !string.IsNullOrEmpty(save.PlayTime)) who += $" · {save.PlayTime}";
        if (save.Language is { } language) who += $" · {language}";
        return who;
    }

    private static string RowDetail(DetectedSave save)
    {
        var detail = ViewModels.SaveDescriptions.Detail(save);
        return string.IsNullOrEmpty(save.TrainerName) ? detail : $"{save.FileName} · {detail}";
    }
}
