namespace PKForge.Domain;

/// <summary>
/// Hosts the app's second-screen surface (the box grid mirror) on a secondary display
/// when one exists. On single-screen devices <see cref="IsAvailable"/> is false and the
/// content stays in the main UI.
/// </summary>
public interface ISecondaryDisplayHost
{
    bool IsAvailable { get; }
    ValueTask ShowAsync(CancellationToken cancellationToken = default);
    /// <summary>Shows the secondary surface for a live Poképark journal (or the normal mirror when no resident is selected).</summary>
    ValueTask ShowPokeparkJournalAsync(CancellationToken cancellationToken = default) => ShowAsync(cancellationToken);
    ValueTask DismissAsync(CancellationToken cancellationToken = default);
}
