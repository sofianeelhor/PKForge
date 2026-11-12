using System.Text.RegularExpressions;

namespace PKForge.Domain;

/// <summary>
/// Turns an exception into a sentence a player can act on. PKForge's own refusals are already
/// written for the player and pass through; what the system throws (a file that vanished, a
/// folder Android no longer lets us read, a bug) is said in plain words, never as a type name,
/// a path or a stack line. The exception itself still goes to the log.
/// </summary>
public static partial class ErrorText
{
    public const string Unexpected = "Something went wrong. Try again; if it keeps happening, share the logs from Help and about.";
    public const string NoAccess = "Android no longer lets PKForge open this file. Link its folder again from Home.";
    public const string Missing = "The file is gone: it was moved, renamed or deleted. Rescan from Home.";
    public const string Storage = "The file could not be read or written. Check the storage, then try again.";

    public static string Plain(Exception? error)
    {
        switch (error)
        {
            case null:
                return Unexpected;
            case AggregateException { InnerExceptions.Count: 1 } aggregate:
                return Plain(aggregate.InnerExceptions[0]);
            case OperationCanceledException:
                return "Cancelled.";
            case UnauthorizedAccessException or System.Security.SecurityException:
                return NoAccess;
            case FileNotFoundException or DirectoryNotFoundException:
                return Missing;
            case IOException when error.GetType() == typeof(IOException) || error is EndOfStreamException:
                return Storage;
            case NullReferenceException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidCastException
                or NotImplementedException or KeyNotFoundException or FormatException or OverflowException
                or DivideByZeroException or ObjectDisposedException:
                return Unexpected;
        }
        return Tidy(error.Message);
    }

    /// <summary>A message kept as it is unless it carries what a player cannot use (a path, a type name).</summary>
    public static string Tidy(string? message)
    {
        var text = message?.Trim() ?? "";
        if (text.Length == 0 || TypeName().IsMatch(text)) return Unexpected;
        text = PathLike().Replace(text, "the file");
        if (!text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?') && !text.EndsWith('…')) text += ".";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    // "System.IO.IOException", "Object reference not set to an instance of an object", "Parameter 'x'".
    [GeneratedRegex(@"\b(System|Microsoft|Java|Android)\.[A-Za-z.]+|Exception\b|instance of an object|Parameter '|0x[0-9A-Fa-f]{4,}")]
    private static partial Regex TypeName();

    // content://…, /storage/…, /data/…, C:\… — a location the player never needs to read.
    [GeneratedRegex(@"(content://\S+|(?<![\w.])/(storage|sdcard|data|mnt|tmp|Users)/\S+|[A-Za-z]:\\\S+)")]
    private static partial Regex PathLike();
}
