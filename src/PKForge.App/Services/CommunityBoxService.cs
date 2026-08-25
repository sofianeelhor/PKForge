using System.Text.Json;

namespace PKForge.App.Services;

/// <summary>One entry in a community collection: a folder to walk into, or a Pokémon file.</summary>
public sealed record CommunityNode(string Name, string Path, bool IsDirectory, long Size, string? DownloadUrl);

/// <summary>
/// Browses community event collections hosted as plain GitHub repositories
/// (RoC's PC layout: nested folders, leaf folders holding .pk*/.pkm files).
/// Listings are cached on disk so the unauthenticated GitHub API limit
/// (60 requests/hour) is spent on new ground, never on re-treading.
/// </summary>
public sealed class CommunityBoxService
{
    public const string RepoTitle = "RoC's PC";
    private const string Repo = "ReignOfComputer/RoCs-PC";
    private static readonly TimeSpan CacheLife = TimeSpan.FromDays(7);

    private static readonly string[] EntityExtensions =
        [".pkm", ".pk1", ".pk2", ".pk3", ".pk4", ".pk5", ".pk6", ".pk7", ".pk8", ".pk9",
         ".pb7", ".pb8", ".pa8", ".ck3", ".xk3", ".bk4"];

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public CommunityBoxService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PKForge");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _cacheRoot = System.IO.Path.Combine(FileSystem.CacheDirectory, "community");
        Directory.CreateDirectory(_cacheRoot);
    }

    public static bool IsEntityFile(string name) =>
        EntityExtensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());

    /// <summary>Lists a folder of the collection. Empty path = repository root.</summary>
    public async Task<IReadOnlyList<CommunityNode>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await GetListingJsonAsync(path, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var nodes = new List<CommunityNode>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? "";
            var type = element.GetProperty("type").GetString();
            var itemPath = element.GetProperty("path").GetString() ?? name;
            if (type == "dir")
            {
                nodes.Add(new CommunityNode(name, itemPath, true, 0, null));
            }
            else if (type == "file" && IsEntityFile(name))
            {
                var url = element.TryGetProperty("download_url", out var u) ? u.GetString() : null;
                nodes.Add(new CommunityNode(name, itemPath, false, element.GetProperty("size").GetInt64(), url));
            }
        }
        return nodes
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<byte[]> DownloadAsync(CommunityNode file, CancellationToken cancellationToken = default)
    {
        if (file.DownloadUrl is null) throw new InvalidOperationException("Not a downloadable file.");
        return await _http.GetByteArrayAsync(file.DownloadUrl, cancellationToken);
    }

    private async Task<string> GetListingJsonAsync(string path, CancellationToken cancellationToken)
    {
        var cacheFile = System.IO.Path.Combine(_cacheRoot, CacheKey(path) + ".json");
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheLife)
            return await File.ReadAllTextAsync(cacheFile, cancellationToken);

        var url = $"https://api.github.com/repos/{Repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && File.Exists(cacheFile))
            return await File.ReadAllTextAsync(cacheFile, cancellationToken); // rate-limited: stale beats nothing
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "GitHub's hourly limit was reached. Already-visited folders still work; try new ones in a while."
                : $"GitHub answered {(int)response.StatusCode}.");
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(cacheFile, json, cancellationToken);
        return json;
    }

    private static string CacheKey(string path)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes);
    }
}
