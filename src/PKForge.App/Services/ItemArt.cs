namespace PKForge.App.Services;

/// <summary>
/// Item sprites from the PokeAPI sprite database, keyed by the item's English name
/// ("Master Ball" → items/master-ball.png). Fetched once, cached forever; misses are
/// remembered so lists never wait twice.
/// </summary>
public static class ItemArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(6);

    public static string Slug(string itemName) =>
        string.Concat(itemName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'))
            .Replace("--", "-").Trim('-');

    /// <summary>Local path of the item's sprite, or null (unknown item / offline first time).</summary>
    public static async Task<string?> GetAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var slug = Slug(itemName);
        var directory = Path.Combine(FileSystem.AppDataDirectory, "items");
        var cache = Path.Combine(directory, slug + ".png");
        var miss = cache + ".miss";
        if (File.Exists(cache)) return cache;
        if (File.Exists(miss)) return null;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(cache)) return cache;
            Directory.CreateDirectory(directory);
            var bytes = await Http.GetByteArrayAsync(
                $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/items/{slug}.png").ConfigureAwait(false);
            await File.WriteAllBytesAsync(cache, bytes).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            try { await File.WriteAllTextAsync(miss, "").ConfigureAwait(false); }
            catch { }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
