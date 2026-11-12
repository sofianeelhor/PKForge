namespace PKForge.Domain;

/// <summary>
/// Tells the copies other tools keep beside the real saves from the saves themselves.
/// Syncthing versions files in a hidden <c>.stversions</c> folder, emulators and front ends
/// keep "backup" folders and files, and each used to stand on the shelf as one more save of
/// the same game. A copy is still found and never dropped: it starts in Hidden saves, where
/// the player can put it back on the shelf like any save they hid themselves.
/// </summary>
public static class SaveNoise
{
    /// <summary>A hidden folder (dot-prefixed, as sync and trash tools make them) or a backup folder.</summary>
    public static bool IsNoiseFolder(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name[0] == '.'
            || name.Contains("backup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lost+found", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("$RECYCLE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("@Recycle", StringComparison.OrdinalIgnoreCase));

    /// <summary>A file that says it is a copy of a save: a backup, a sync conflict, an editor's leftover.</summary>
    public static bool IsBackupCopy(string? fileName) =>
        !string.IsNullOrEmpty(fileName)
        && (fileName[0] == '.'
            || fileName.Contains("backup", StringComparison.OrdinalIgnoreCase)
            // The name PKForge itself gives a copy it writes beside a file of the same name.
            || fileName.Contains(" (copy", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".sync-conflict-", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith('~'));

    /// <summary>
    /// True when a save found at <paramref name="relativePath"/> (from the linked folder down to
    /// the file, <c>/</c> separated) is a copy by its own name or by a folder it sits in.
    /// <paramref name="savesFolder"/> names a folder that is the emulator's real save folder
    /// despite its name: DraStic keeps its saves in <c>backup</c>.
    /// </summary>
    public static bool IsCopyPath(string? relativePath, string? savesFolder = null)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
            if (IsNoiseFolder(parts[i]) && !string.Equals(parts[i], savesFolder, StringComparison.OrdinalIgnoreCase))
                return true;
        return IsBackupCopy(parts[^1]);
    }
}
