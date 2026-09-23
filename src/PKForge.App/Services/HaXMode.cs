using PKForge.App.Theme;

namespace PKForge.App.Services;

/// <summary>
/// PKHeX's HaX mode, PKForge edition: when on, pickers offer every option instead of the
/// legal subset (any ability on any mon, and more later). Writes still go through the
/// same validate, backup, atomic pipeline; legality verdicts just stop being a gate.
/// Off by default: the default experience keeps people from bricking mons by accident.
/// <para>
/// HaX mode and <see cref="HardcoreMode"/> never contradict each other: HaX mode only
/// widens which options the pickers offer, and every write they build still has to pass
/// the Hardcore guard, so Hardcore wins for edits.
/// </para>
/// </summary>
public static class HaXMode
{
    private const string Key = "hax_mode";

    public static bool IsOn => Preferences.Default.Get(Key, false);

    public static void Set(bool on) => Preferences.Default.Set(Key, on);
}
