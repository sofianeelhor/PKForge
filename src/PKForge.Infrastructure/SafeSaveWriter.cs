using System.Security.Cryptography;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Validates candidate bytes, creates a backup, and performs one platform write in that order.</summary>
public sealed class SafeSaveWriter(
    ISaveEngine engine,
    IBackupService backups,
    ISaveFileAccess access,
    ISaveIdentityStore? identities = null) : ISafeSaveWriter
{
    private readonly HashSet<string> _confirmedLayoutRisk = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // The layout verdict depends only on the file's bytes, and analysing it re-parses and
    // round-trips the whole save; it is kept per document for the exact bytes it judged.
    private readonly Dictionary<string, (byte[] Bytes, LayoutRisk? Risk)> _layoutVerdicts = new(StringComparer.Ordinal);

    /// <summary>Accepts the suspected-hack risk for this session and, with an identity
    /// store, for good. A corrupting layout is never lifted.</summary>
    public void ConfirmLayoutRisk(string documentId)
    {
        lock (_gate) _confirmedLayoutRisk.Add(documentId);
        if (identities is not null && identities.Get(documentId) is not { AcceptedHackRisk: true })
            identities.Set((identities.Get(documentId) ?? new SaveIdentity(documentId)) with { AcceptedHackRisk = true });
    }

    public void RevokeLayoutRisk(string documentId)
    {
        lock (_gate) _confirmedLayoutRisk.Remove(documentId);
        if (identities?.Get(documentId) is { AcceptedHackRisk: true } identity)
            identities.Set(identity with { AcceptedHackRisk = false });
    }

    public LayoutRisk? LayoutRiskOf(string documentId, SaveSnapshot original) => LayoutRisk(documentId, original.OriginalBytes);

    public string? WhyWritesAreRefused(string documentId, SaveSnapshot original) => Refusal(documentId, original)?.Message;

    // With an identity store the persisted choice is the only truth, so "Reset to detected"
    // or a revoke on Home takes effect immediately; without one it lasts for this session.
    private bool Accepted(string documentId)
    {
        if (identities is not null) return identities.Get(documentId)?.AcceptedHackRisk == true;
        lock (_gate) return _confirmedLayoutRisk.Contains(documentId);
    }

    /// <summary>The refusal every non-restore write of this document would hit before any
    /// slot is compared, or null when the save accepts writes.</summary>
    private UnsafeSaveWriteException? Refusal(string documentId, SaveSnapshot original)
    {
        // "Other CFRU hack" borrows the Radical Red / Unbound engine to READ the shared
        // CFRU layout, but species, growth-rate, base-stat, item and bag tables are
        // per-hack: a level edit would compute EXP and stats from the wrong game's
        // tables (the level-100 class of corruption). Read-only until an engine exists.
        if (identities?.Get(documentId)?.GameChoiceId is "hack-cfru" or "hack-cfru-unbound")
            return new UnsafeSaveWriteException(
                "Write refused: this save is marked as a CFRU hack PKForge has no engine for. It can be browsed, " +
                "but writing would use another game's Pokémon tables. The original was not touched.");

        return LayoutRisk(documentId, original.OriginalBytes) switch
        {
            // A vanilla write provably breaks the file: no confirmation can make it safe.
            { Kind: LayoutRiskKind.CorruptingLayout } risk => new UnsafeSaveWriteException(
                $"Write refused: {risk.Reason} This save is read-only in PKForge; the original was not touched."),
            { Kind: LayoutRiskKind.SuspectedHack } risk when !Accepted(documentId) => new UnsafeSaveWriteException(
                $"Read-only: {risk.Reason} To edit anyway, long-press this save on Home and choose \"Edit at my own risk\". " +
                "The original was not touched.", requiresConfirmation: true),
            _ => null,
        };
    }

    private LayoutRisk? LayoutRisk(string documentId, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            if (_layoutVerdicts.TryGetValue(documentId, out var cached) && bytes.Span.SequenceEqual(cached.Bytes))
                return cached.Risk;
        }
        var risk = engine.AssessLayoutRisk(bytes);
        lock (_gate) _layoutVerdicts[documentId] = (bytes.ToArray(), risk);
        return risk;
    }

    public ValueTask<SaveWriteReceipt> WriteAsync(
        string documentId,
        SaveSnapshot original,
        ReadOnlyMemory<byte> candidate,
        string? changeDescription = null,
        CancellationToken cancellationToken = default)
        => WriteCoreAsync(documentId, original, candidate, scope: null, changeDescription, cancellationToken);

    public ValueTask<SaveWriteReceipt> WriteScopedAsync(
        string documentId,
        SaveSnapshot original,
        ReadOnlyMemory<byte> candidate,
        WriteScope scope,
        string? changeDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return WriteCoreAsync(documentId, original, candidate, scope, changeDescription, cancellationToken);
    }

    private async ValueTask<SaveWriteReceipt> WriteCoreAsync(
        string documentId,
        SaveSnapshot original,
        ReadOnlyMemory<byte> candidate,
        WriteScope? scope,
        string? changeDescription,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        // An unchanged candidate means the mutation produced identical bytes: writing the
        // same state again would create a meaningless restore point, so nothing happens.
        if (candidate.Span.SequenceEqual(original.OriginalBytes.Span))
            return new SaveWriteReceipt(
                string.Empty,
                Convert.ToHexString(SHA256.HashData(original.OriginalBytes.Span)),
                Convert.ToHexString(SHA256.HashData(candidate.Span)),
                DateTimeOffset.UtcNow,
                Changed: false);

        if (!engine.Validate(candidate))
            throw new InvalidDataException("The candidate save failed engine validation; the original was not touched.");

        // Structural safety net. A whole-file restore (Unrestricted) is the recovery path
        // and must stay open; every other write is refused when the engine is not sure of
        // the layout (a ROM hack parsed as vanilla rewrites checksums and slots in a layout
        // the game does not use) or when the diff touches slots the mutation did not target.
        if (scope is not { Unrestricted: true })
        {
            if (Refusal(documentId, original) is { } refusal)
                throw refusal;
            if (engine.CheckWriteSafety(original.OriginalBytes, candidate, scope) is { } unsafeDiff)
                throw new UnsafeSaveWriteException($"Write refused: {unsafeDiff} The original was not touched.");
        }

        var backup = await backups.CreateAsync(original, changeDescription, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await access.WriteAtomicallyAsync(documentId, candidate, cancellationToken).ConfigureAwait(false);
        // A write that passed the checks keeps the file's layout, so the candidate inherits
        // the verdict and the next write skips the whole-file layout analysis.
        lock (_gate)
        {
            if (_layoutVerdicts.TryGetValue(documentId, out var verdict))
                _layoutVerdicts[documentId] = (candidate.ToArray(), verdict.Risk);
        }

        return new SaveWriteReceipt(
            backup.BackupId,
            Convert.ToHexString(SHA256.HashData(original.OriginalBytes.Span)),
            Convert.ToHexString(SHA256.HashData(candidate.Span)),
            DateTimeOffset.UtcNow);
    }
}
