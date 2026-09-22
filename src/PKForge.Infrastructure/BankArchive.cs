using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>One .pk file in an exported archive folder, described for humans and tools.</summary>
public sealed record BankArchiveEntry(
    string File,
    int Species,
    string Nickname,
    bool Shiny,
    int Generation,
    int Box,
    int Slot,
    string Sha256);

/// <summary>
/// The manifest.json written beside the .pk files. An interchange format, not an internal
/// index: camelCase so PKHeX users can read it in any file manager, per-file SHA-256 so a
/// transfer can be verified. (The full bank-as-ZIP model lives in docs/BANK_MODEL.md; this
/// is its loose-folder shipping subset.)
/// </summary>
public sealed record BankArchiveManifest(
    string App,
    int SchemaVersion,
    DateTimeOffset ExportedUtc,
    IReadOnlyList<BankArchiveEntry> Entries);

/// <summary>Import outcome: what joined the bank and what was left behind, and why.</summary>
public sealed record BankArchiveImportResult(int Imported, int SkippedDuplicates, int Rejected);

/// <summary>
/// Bank archive export/import over a picked folder: one .pkN file per mon (species number,
/// nickname, short id — unique even for clones) plus manifest.json. Import merges into the
/// bank, skipping exact byte copies (SHA-256) of mons already stored or already imported
/// this batch. Loose .pk files import with or without a manifest.
/// </summary>
public static class BankArchive
{
    public const string ManifestFileName = "manifest.json";
    public const int CurrentSchemaVersion = 1;

    /// <summary>camelCase on purpose: the manifest is read outside the app too.</summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> ExportAsync(
        IBankService bank,
        IFolderFileAccess files,
        string treeId,
        IReadOnlyList<BankEntry>? entries = null,
        CancellationToken cancellationToken = default)
    {
        var list = entries ?? bank.GetAll();
        var manifestEntries = new BankArchiveEntry[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = list[i];
            var bytes = bank.GetData(entry.Id);
            var name = FileNameFor(entry);
            await files.WriteFileAsync(treeId, name, bytes, cancellationToken).ConfigureAwait(false);
            manifestEntries[i] = new BankArchiveEntry(
                name, entry.Info.Species, entry.Info.Nickname, entry.Info.Shiny,
                entry.Info.Generation, entry.Box, entry.Slot, Sha256Hex(bytes));
        }
        var manifest = new BankArchiveManifest("PKForge", CurrentSchemaVersion, DateTimeOffset.UtcNow, manifestEntries);
        var json = JsonSerializer.Serialize(manifest, Json);
        await files.WriteFileAsync(treeId, ManifestFileName, Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
        return list.Count;
    }

    /// <summary>
    /// Merge-imports every recognized .pk file in the folder. <paramref name="describe"/> is
    /// the engine's loose-entity probe (passed as a delegate so the archive stays engine-free).
    /// </summary>
    public static async Task<BankArchiveImportResult> ImportAsync(
        IBankService bank,
        Func<byte[], string, BankEntryInfo?> describe,
        IFolderFileAccess files,
        string treeId,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = (await files.ListFilesAsync(treeId, cancellationToken).ConfigureAwait(false))
            .Where(f => IsPkFileName(f.DisplayName))
            .ToArray();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in bank.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            known.Add(Sha256Hex(bank.GetData(entry.Id)));
        }

        var imported = 0;
        var skipped = 0;
        var rejected = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(i, candidates.Length);
            var bytes = (await files.ReadFileAsync(candidates[i].DocumentId, cancellationToken).ConfigureAwait(false)).ToArray();
            if (describe(bytes, candidates[i].DisplayName) is not { } info)
            {
                rejected++;
                continue;
            }
            if (!known.Add(Sha256Hex(bytes)))
            {
                skipped++;
                continue;
            }
            bank.Add(bytes, info);
            imported++;
        }
        progress?.Invoke(candidates.Length, candidates.Length);
        return new BankArchiveImportResult(imported, skipped, rejected);
    }

    /// <summary>File name for one entry: "025 - Sparky a1b2c3d4.pk7". The short id keeps
    /// clones and same-named siblings distinct; the generation picks the .pkN extension.</summary>
    public static string FileNameFor(BankEntry entry)
    {
        var id = entry.Id.ToString("N")[..8];
        var nickname = SanitizeFileName(entry.Info.Nickname);
        var extension = $".pk{entry.Info.Generation}";
        return nickname.Length == 0
            ? $"{entry.Info.Species:000} {id}{extension}"
            : $"{entry.Info.Species:000} - {nickname} {id}{extension}";
    }

    /// <summary>True for .pk plus .pk1 through .pk9 (case-insensitive) — the cheap prefilter
    /// that keeps an import scan from parsing a folder of arbitrary files.</summary>
    public static bool IsPkFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".pk" or ".pk1" or ".pk2" or ".pk3" or ".pk4"
            or ".pk5" or ".pk6" or ".pk7" or ".pk8" or ".pk9";
    }

    /// <summary>Strips filesystem-hostile characters (SAF display names reject them too) and caps length.</summary>
    public static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Trim().Length > 20 ? cleaned.Trim()[..20].Trim() : cleaned.Trim();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
