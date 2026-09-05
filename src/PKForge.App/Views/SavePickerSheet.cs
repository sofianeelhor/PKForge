using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The transfer destination sheet: detected games as a pad menu, with emulator logos.
/// Returns the chosen save, or null on cancel.
/// </summary>
public static class SavePickerSheet
{
    public static async Task<DetectedSave?> PickAsync(
        Grid host, IReadOnlyList<DetectedSave> saves, string title, string? message, string? excludeDocumentId = null)
    {
        var candidates = saves.Where(s => s.DocumentId != excludeDocumentId).ToArray();
        if (candidates.Length == 0) return null;

        // When one game exists as several saves (an emulator reinstall orphans the old
        // console identity), bare game labels are a coin flip: disambiguate with the
        // trainer and the last-modified date, and number any residual twins.
        var labels = candidates.Select(s =>
        {
            var label = s.GameLabel;
            if (candidates.Count(x => x.GameLabel == s.GameLabel) > 1)
            {
                if (!string.IsNullOrEmpty(s.TrainerName)) label += $" · {s.TrainerName}";
                if (s.LastModified is { } modified) label += $" · {modified:yyyy-MM-dd}";
            }
            return label;
        }).ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            // Number identical labels against the pristine list; suffixing in place
            // would shift the comparisons as we go.
            if (labels.Count(label => label == labels[i]) <= 1) continue;
            var ordinal = Enumerable.Range(0, i + 1).Count(j => labels[j] == labels[i]);
            labels[i] += $" (#{ordinal})";
        }
        var options = candidates.Select((s, i) => new PadOption(labels[i], IconPath: IconFor(s.Emulator))).ToArray();
        var choice = await PadMenu.ShowAsync(host, title, message, options);
        if (choice is null) return null;
        var index = Array.FindIndex(options, o => o.Label == choice);
        return index >= 0 ? candidates[index] : null;
    }

    private static string IconFor(EmulatorKind kind) => kind switch
    {
        EmulatorKind.RetroArch => "retroarch",
        EmulatorKind.MelonDS => "melonds",
        EmulatorKind.Azahar => "azahar",
        EmulatorKind.Eden => "eden",
        EmulatorKind.Dolphin => "dolphin",
        EmulatorKind.DraStic => "drastic",
        EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc => "pizzaboy",
        EmulatorKind.Linkboy => "linkboy",
        _ => "storage",
    };
}
