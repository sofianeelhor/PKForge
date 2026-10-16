using System.Diagnostics.CodeAnalysis;

namespace PKForge.Sync;

/// <summary>
/// What a person types to add a device without a camera: the line another device shows under Add a device
/// ("sync.example.com  K7QM-4XTP"), a pasted pairing link, or just the code. The server part follows the
/// <see cref="ServerAddress"/> rules; the code is the server's 8-character pairing code in any case,
/// with or without spaces and dashes.
/// </summary>
public static class PairingEntry
{
    /// <summary>The server's pairing alphabet (no I, L, O, 0 or 1).</summary>
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;

    public static bool TryParse(string? text, out Uri? server, out string code) =>
        TryParse(text, out server, out code, out _);

    /// <summary>Server is null when only a code was typed. On failure, error says what to fix.</summary>
    public static bool TryParse(string? text, out Uri? server, out string code, out string error)
    {
        server = null;
        code = "";
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "Type the server and code another device shows under Add a device.";
            return false;
        }

        if (PairingLink.TryParse(trimmed, out var linkServer, out var linkCode))
        {
            if (!TryNormalizeCode(linkCode, out var normal))
            {
                error = "That pairing link has no valid code in it.";
                return false;
            }
            server = linkServer;
            code = normal;
            error = "";
            return true;
        }

        // The code is the last word or two ("K7QM-4XTP", "K7QM 4XTP"); at most one word before it is the server.
        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var take = 1; take <= Math.Min(2, words.Length); take++)
        {
            if (words.Length - take > 1) continue;
            if (!TryNormalizeCode(string.Concat(words[^take..]), out var candidate)) continue;
            if (words.Length - take == 1)
            {
                if (!ServerAddress.TryParse(words[0], out var address, out error)) return false;
                server = address;
            }
            code = candidate;
            error = "";
            return true;
        }

        error = words.Length != 1
            ? "Type the server, a space, then the code, like sync.example.com K7QM-4XTP."
            : words[0].IndexOfAny(['.', ':', '/']) >= 0
                ? "Add the code after the server, like sync.example.com K7QM-4XTP."
                : $"That isn't a pairing code. It is {CodeLength} letters and digits, like K7QM-4XTP.";
        return false;
    }

    /// <summary>Uppercases and drops spaces and dashes; true when what is left is a pairing code.</summary>
    public static bool TryNormalizeCode(string? text, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (text is null) return false;
        var chars = new List<char>(CodeLength);
        foreach (var c in text)
        {
            if (c is ' ' or '-' || char.IsWhiteSpace(c)) continue;
            var upper = char.ToUpperInvariant(c);
            if (!CodeAlphabet.Contains(upper, StringComparison.Ordinal)) return false;
            chars.Add(upper);
        }
        if (chars.Count != CodeLength) return false;
        code = new string([.. chars]);
        return true;
    }

    /// <summary>
    /// The line to show beside the QR code so it can be typed instead: "host  XXXX-XXXX". The scheme is left
    /// out for https (unless the host would otherwise be read as a home-network http one); the port only when default.
    /// </summary>
    public static string Format(Uri server, string code)
    {
        var display = TryNormalizeCode(code, out var normal) ? $"{normal[..4]}-{normal[4..]}" : code.Trim();
        return $"{FormatServer(server)}  {display}";
    }

    public static string FormatServer(Uri server)
    {
        var bare = server.Scheme == "https" && !ServerAddress.IsHomeNetworkHost(server.Host);
        return (bare ? "" : server.Scheme + "://") + server.Authority + server.AbsolutePath.TrimEnd('/');
    }
}
