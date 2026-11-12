namespace PKForge.Domain;

/// <summary>
/// Hosts the app's second-screen surface (the box grid mirror) on a secondary display
/// when one exists. On single-screen devices <see cref="IsAvailable"/> is false and the
/// content stays in the main UI.
/// </summary>
public interface ISecondaryDisplayHost
{
    bool IsAvailable { get; }
    /// <summary>Where the second screen sits against the one PKForge runs on, for hints that name a screen.</summary>
    ScreenArrangement Arrangement => ScreenArrangement.MainOnTop;
    ValueTask ShowAsync(CancellationToken cancellationToken = default);
    /// <summary>Shows the secondary surface for a live Poképark journal (or the normal mirror when no resident is selected).</summary>
    ValueTask ShowPokeparkJournalAsync(CancellationToken cancellationToken = default) => ShowAsync(cancellationToken);
    /// <summary>Refreshes an already-visible Poképark journal without rebuilding the presentation.</summary>
    ValueTask RefreshPokeparkJournalAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
    ValueTask DismissAsync(CancellationToken cancellationToken = default);
}

/// <summary>How the two panels of a dual-screen handheld relate, as far as Android lets PKForge tell.</summary>
public enum ScreenArrangement
{
    /// <summary>PKForge runs on the larger panel, the top one of every known clamshell (AYN Thor): the second screen is below.</summary>
    MainOnTop,
    /// <summary>Panels of the same size (Anbernic RG DS), or PKForge on the smaller one: Android does not say which is on top.</summary>
    Unknown,
}

/// <summary>One built-in panel as Android reports it, in physical pixels.</summary>
public readonly record struct ScreenPanel(int Id, int Width, int Height)
{
    public long Area => (long)Width * Height;
}

/// <summary>
/// Names the screens in hints. PKForge always runs on Android's default display (only the
/// other panels accept the second screen), but firmwares put that display in different
/// places: the Thor's stock OS makes its large top panel the default, Gamma OS on the
/// Anbernic RG DS its lower panel (issue #61). Android never says which panel is on top,
/// so "top" and "lower" are only said when the panel sizes give it away.
/// </summary>
public static class DualScreenLayout
{
    /// <summary>The larger panel sits on top on every known clamshell; with equal panels nobody can tell.</summary>
    public static ScreenArrangement Arrange(ScreenPanel main, ScreenPanel second) =>
        main.Area > second.Area ? ScreenArrangement.MainOnTop : ScreenArrangement.Unknown;

    /// <summary>The main screen as the second screen's hints name it, in capitals.</summary>
    public static string MainScreenCaps(ScreenArrangement arrangement) =>
        arrangement == ScreenArrangement.MainOnTop ? "THE TOP SCREEN" : "THE MAIN SCREEN";

    /// <summary>The second screen as the main screen's text names it.</summary>
    public static string SecondScreenName(ScreenArrangement arrangement) =>
        arrangement == ScreenArrangement.MainOnTop ? "the lower screen" : "the second screen";
}
