using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Hardcore mode, PKForge edition: the achievement-hunter setting. While it is on the
/// open save may be browsed, organised, moved, transferred, backed up and exported,
/// but nothing that edits, fabricates or duplicates Pokémon data can be written - a
/// Pokémon may be MOVED, never COPIED. Off by default. Hardcore wins over
/// <see cref="HaXMode"/>: HaX mode only widens which options are offered, and every
/// one of its writes still has to pass this guard.
/// </summary>
public static class HardcoreMode
{
    private const string Key = "hardcore_mode";

    /// <summary>The one line every blocked surface shows instead of writing.</summary>
    public const string StatusLine = "HARDCORE MODE - data editing is off";

    /// <summary>Short marker for footers and headers while the mode is on.</summary>
    public const string Marker = "HARDCORE";

    /// <summary>The settings-screen explanation; the HaX-mode relationship is stated, not implied.</summary>
    public const string SettingExplanation =
        "For RetroAchievements-style play. Browsing, organising, moving, sending to another game, " +
        "bank deposit and withdrawal as a MOVE, restore points and exports stay available; " +
        "anything that edits, generates or duplicates Pokémon data is blocked, and a Pokémon can be moved but never copied. " +
        "Hardcore wins over HaX mode: while it is on, HaX mode options cannot be written either.";

    public static bool IsOn => Preferences.Default.Get(Key, false);

    public static void Set(bool on) => Preferences.Default.Set(Key, on);

    /// <summary>The decision table for the current setting.</summary>
    public static SaveGuard Guard => SaveGuard.For(IsOn);

    /// <summary>The status a blocked action shows, e.g. "HARDCORE MODE - data editing is off (duplication blocked)".</summary>
    public static string StatusFor(SaveAction action) => $"{StatusLine} ({Describe(action)} blocked)";

    /// <summary>
    /// True when Hardcore mode forbids <paramref name="action"/>; <paramref name="status"/>
    /// then carries the line to show instead of writing.
    /// </summary>
    public static bool Blocks(SaveAction action, out string status)
    {
        if (!Guard.Blocks(action))
        {
            status = string.Empty;
            return false;
        }
        status = StatusFor(action);
        return true;
    }

    private static string Describe(SaveAction action) => action switch
    {
        SaveAction.EditMon => "Pokémon edits",
        SaveAction.CreateMon => "creating Pokémon",
        SaveAction.BatchEdit => "batch edits",
        SaveAction.Duplicate => "duplication",
        SaveAction.InjectEvent => "event injection",
        SaveAction.EditInventory => "inventory edits",
        SaveAction.EditTrainer => "trainer edits",
        SaveAction.EditDex => "dex edits",
        SaveAction.EditWorld => "world edits",
        SaveAction.WriteRawBytes => "byte writes",
        SaveAction.RepairRtc => "clock repair",
        SaveAction.Move => "moving Pokémon",
        SaveAction.Release => "releasing Pokémon",
        SaveAction.ExportFile => "exports",
        SaveAction.Backup => "restore points",
        _ => "data edits",
    };
}
