using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Web;

namespace PKForge.Sync;

/// <summary>
/// What a person types as a sync server address, made safe: https anywhere, plain http only for a server
/// on the home network (spec §11). Typing no scheme picks http for home hosts and https otherwise.
/// </summary>
public static class ServerAddress
{
    public static bool TryParse(string? input, [NotNullWhen(true)] out Uri? address, out string error)
    {
        address = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Enter the server's address.";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var host = text.Split('/', ':')[0];
            text = (IsHomeNetworkHost(host) ? "http://" : "https://") + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            error = "That is not a web address (http:// or https://).";
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Leave out anything after the address (? or #).";
            return false;
        }
        if (uri.Scheme == "http" && !IsHomeNetworkHost(uri.Host))
        {
            error = "Plain http:// only works for servers on your home network. Use https:// for anything else.";
            return false;
        }
        address = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        error = "";
        return true;
    }

    /// <summary>Private and link-local addresses, Tailscale's range, localhost, and names without a public domain.</summary>
    public static bool IsHomeNetworkHost(string host)
    {
        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The QR payload a signed-in device shows to add another: pkforge-sync://pair?server=…&amp;code=….</summary>
public static class PairingLink
{
    public static bool TryParse(string? payload, [NotNullWhen(true)] out Uri? server, [NotNullWhen(true)] out string? code)
    {
        server = null;
        code = null;
        if (!Uri.TryCreate(payload?.Trim(), UriKind.Absolute, out var link) || link.Scheme != "pkforge-sync" || link.Host != "pair")
            return false;
        var query = HttpUtility.ParseQueryString(link.Query);
        if (string.IsNullOrWhiteSpace(query["code"]) || !ServerAddress.TryParse(query["server"], out var address, out _))
            return false;
        server = address;
        code = query["code"]!.Trim();
        return true;
    }
}
