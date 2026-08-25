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
    ValueTask DismissAsync(CancellationToken cancellationToken = default);
}
