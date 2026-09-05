using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Loads a platform document into an isolated engine snapshot.</summary>
public sealed class SaveSessionService(ISaveFileAccess access, ISaveEngine engine) : ISaveSessionService
{
    public SaveSession? Current { get; private set; }

    /// <summary>The live engine session backing <see cref="Current"/>; used by editor and legality flows.</summary>
    public ISaveEngineSession? CurrentSession { get; private set; }

    public async ValueTask<SaveSession> OpenAsync(PickedDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bytes = await access.ReadAsync(document.DocumentId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var engineSession = engine.OpenSession(bytes, document.DisplayName);
        CurrentSession?.Dispose();
        var session = new SaveSession(document, engineSession.Snapshot);
        CurrentSession = engineSession;
        Current = session;
        return session;
    }

    public void MarkWritten(string documentId, ReadOnlyMemory<byte> written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (Current is not { } current || current.Document.DocumentId != documentId) return;
        // The baseline is a private copy: later engine mutations can never bleed into it.
        Current = current with { Snapshot = current.Snapshot with { OriginalBytes = written.ToArray() } };
    }
}
