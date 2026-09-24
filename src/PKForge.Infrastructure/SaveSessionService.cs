using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Loads a platform document into an isolated engine snapshot. When the user told PKForge
/// which game a save is (<see cref="ISaveIdentityStore"/>), that choice picks the engine route.
/// </summary>
public sealed class SaveSessionService(ISaveFileAccess access, ISaveEngine engine, ISaveIdentityStore? identities = null) : ISaveSessionService
{
    public SaveSession? Current { get; private set; }

    /// <summary>The live engine session backing <see cref="Current"/>; used by editor and legality flows.</summary>
    public ISaveEngineSession? CurrentSession { get; private set; }

    public async ValueTask<SaveSession> OpenAsync(PickedDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bytes = await access.ReadAsync(document.DocumentId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var format = SaveIdentityRules.FormatOfChoice(identities?.Get(document.DocumentId)?.GameChoiceId);
        var engineSession = engine.OpenSession(bytes, document.DisplayName, format);
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

    public void RevertToBaseline()
    {
        if (Current is not { } current) return;
        var format = SaveIdentityRules.FormatOfChoice(identities?.Get(current.Document.DocumentId)?.GameChoiceId);
        ISaveEngineSession reopened;
        try { reopened = engine.OpenSession(current.Snapshot.OriginalBytes, current.Document.DisplayName, format); }
        catch
        {
            // The baseline no longer opens: a session that cannot be trusted is not kept.
            Close();
            throw;
        }
        CurrentSession?.Dispose();
        CurrentSession = reopened;
        Current = current with { Snapshot = reopened.Snapshot with { OriginalBytes = current.Snapshot.OriginalBytes } };
    }

    public void Close()
    {
        CurrentSession?.Dispose();
        CurrentSession = null;
        Current = null;
    }
}
