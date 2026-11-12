using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// An item's lore (description, first game, wild holders) from PokéAPI, keyed like
/// <see cref="ItemArt"/> by the item's English name. Fetched once and kept on disk, so the
/// bag reads it offline afterwards; a miss is retried after a day.
/// </summary>
public static class ItemLoreSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(20);

    /// <summary>The item's lore, or null (unknown to PokéAPI, or offline the first time).</summary>
    public static async Task<ItemLore?> GetAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var slug = ItemArt.Slug(itemName);
        var directory = Path.Combine(FileSystem.AppDataDirectory, "items", "lore");
        var cache = Path.Combine(directory, slug + ".json");
        var miss = cache + ".miss";
        if (File.Exists(cache)) return ItemLores.Parse(await File.ReadAllTextAsync(cache).ConfigureAwait(false));
        if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissTtl) return null;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(cache)) return ItemLores.Parse(await File.ReadAllTextAsync(cache).ConfigureAwait(false));
            Directory.CreateDirectory(directory);
            var json = await Http.GetStringAsync($"https://pokeapi.co/api/v2/item/{slug}").ConfigureAwait(false);
            var lore = ItemLores.Parse(json);
            if (lore is null) throw new InvalidDataException("unreadable item document");
            await File.WriteAllTextAsync(cache, json).ConfigureAwait(false);
            return lore;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            try { await File.WriteAllTextAsync(miss, "").ConfigureAwait(false); }
            catch (IOException) { }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
