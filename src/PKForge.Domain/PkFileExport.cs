namespace PKForge.Domain;

/// <summary>One Pokémon ready to be written as a .pk file: its PKHeX-style file name and bytes.</summary>
public sealed record PkFile(string FileName, ReadOnlyMemory<byte> Data);

/// <summary>
/// "Save .pk files to a folder" (from the Bank and from a save's boxes): each Pokémon becomes
/// one file named the way PKHeX names them, with the extension of its exact format (.pk3,
/// .pb8, .pa9...) so PKHeX and PKForge read the same type back. Nothing in the folder is ever
/// overwritten: a name already taken there, or earlier in the same batch, gets " (2)", " (3)"...
/// </summary>
public static class PkFileExport
{
    // Android's SAF and FAT-formatted SD cards reject these even where .NET's own list
    // (just '/' and NUL on Unix) would let them through.
    private static readonly char[] Hostile = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>The PKHeX extension for a format ("PK8" → ".pk8", "PB7" → ".pb7"); ".pk{generation}"
    /// when the format is unknown.</summary>
    public static string ExtensionFor(string? format, int generation) =>
        format is { Length: > 0 } ? "." + format.Trim().ToLowerInvariant() : $".pk{generation}";

    /// <summary>"025 - Sparky.pk7", or "025.pk7" for a mon without a usable nickname.</summary>
    public static string FileNameFor(int species, string? nickname, string extension)
    {
        var name = Clean(nickname ?? "");
        return name.Length == 0 ? $"{species:000}{extension}" : $"{species:000} - {name}{extension}";
    }

    /// <summary>A name every file system the app writes to accepts: hostile and control
    /// characters dropped, the stem trimmed (no leading dot, no trailing dot or space) and
    /// capped, the extension kept. An empty stem becomes "pokemon".</summary>
    public static string SafeFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Length > 8 || "." + Clean(extension) != extension) extension = "";
        var stem = Clean(fileName[..^extension.Length]);
        if (stem.Length > 60) stem = stem[..60].TrimEnd(' ', '.');
        return (stem.Length == 0 ? "pokemon" : stem) + extension;
    }

    /// <summary>
    /// <paramref name="fileName"/> itself while it is free, otherwise "name (2).ext", "name (3).ext"
    /// and so on - the first one not in <paramref name="taken"/>, compared case-insensitively
    /// since most Android storage is. The chosen name is added to <paramref name="taken"/>.
    /// </summary>
    public static string Unique(string fileName, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        var candidate = fileName;
        for (var n = 2; Contains(taken, candidate); n++) candidate = $"{stem} ({n}){extension}";
        taken.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Writes every file into the picked folder under a safe, unique name and returns how many
    /// were written. The folder is listed once first, so an existing file is never replaced.
    /// </summary>
    public static async Task<int> WriteAsync(
        IFolderFileAccess folder, string treeId, IReadOnlyList<PkFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return 0;
        var existing = await folder.ListFilesAsync(treeId, cancellationToken).ConfigureAwait(false);
        var taken = new HashSet<string>(existing.Select(f => f.DisplayName), StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Unique(SafeFileName(file.FileName), taken);
            await folder.WriteFileAsync(treeId, name, file.Data, cancellationToken).ConfigureAwait(false);
            written++;
        }
        return written;
    }

    private static bool Contains(ISet<string> taken, string name) =>
        taken.Contains(name) || taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string raw) =>
        new string([.. raw.Where(c => !char.IsControl(c) && Array.IndexOf(Hostile, c) < 0)]).Trim().Trim('.').Trim();
}
