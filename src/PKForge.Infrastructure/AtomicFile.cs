using System.Text;

namespace PKForge.Infrastructure;

/// <summary>
/// Replaces a file whole or not at all: the bytes go to a temporary file beside it, are flushed
/// to the storage device, and only then renamed over the target. A crash or a power cut leaves
/// either the old file or the new one, never a truncated one (a rename without the flush can
/// surface as an empty file after a power cut on Android's file systems).
/// </summary>
public static class AtomicFile
{
    /// <param name="temporary">A fixed temporary name, for a store whose own lock already serialises its
    /// writes (the Bank index keeps <c>index.json.tmp</c>); by default a unique hidden one.</param>
    public static void Write(string path, ReadOnlySpan<byte> bytes, string? temporary = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var mode = temporary is null ? FileMode.CreateNew : FileMode.Create;
        temporary ??= Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, mode, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void WriteText(string path, string text, string? temporary = null) => Write(path, Encoding.UTF8.GetBytes(text), temporary);

    /// <summary>As <see cref="Write"/>, without holding the calling thread during the write (restore points).</summary>
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
