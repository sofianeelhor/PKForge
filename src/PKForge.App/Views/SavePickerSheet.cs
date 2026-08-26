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

        var options = candidates.Select(s => new PadOption(s.GameLabel, IconPath: IconFor(s.Emulator))).ToArray();
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
        _ => "storage",
    };
}
