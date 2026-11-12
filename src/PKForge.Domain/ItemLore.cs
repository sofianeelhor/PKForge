using System.Text.Json;

namespace PKForge.Domain;

/// <summary>
/// What the bag tells about an item beyond its count: the games' own description, where it
/// first appeared, and which wild Pokémon may carry it.
/// </summary>
public sealed record ItemLore(string? About, string? FirstSeen, string? HeldBy);

/// <summary>Reads an item's lore from a PokéAPI item document (/api/v2/item/{slug}).</summary>
public static class ItemLores
{
    // PokéAPI version groups in release order, with the names the games carry.
    private static readonly (string Group, int Generation, string Title)[] Groups =
    [
        ("red-blue", 1, "Red & Blue"), ("yellow", 1, "Yellow"),
        ("gold-silver", 2, "Gold & Silver"), ("crystal", 2, "Crystal"),
        ("ruby-sapphire", 3, "Ruby & Sapphire"), ("emerald", 3, "Emerald"), ("firered-leafgreen", 3, "FireRed & LeafGreen"),
        ("colosseum", 3, "Colosseum"), ("xd", 3, "XD"),
        ("diamond-pearl", 4, "Diamond & Pearl"), ("platinum", 4, "Platinum"), ("heartgold-soulsilver", 4, "HeartGold & SoulSilver"),
        ("black-white", 5, "Black & White"), ("black-2-white-2", 5, "Black 2 & White 2"),
        ("x-y", 6, "X & Y"), ("omega-ruby-alpha-sapphire", 6, "Omega Ruby & Alpha Sapphire"),
        ("sun-moon", 7, "Sun & Moon"), ("ultra-sun-ultra-moon", 7, "Ultra Sun & Ultra Moon"),
        ("lets-go-pikachu-lets-go-eevee", 7, "Let's Go, Pikachu! & Let's Go, Eevee!"),
        ("sword-shield", 8, "Sword & Shield"), ("brilliant-diamond-shining-pearl", 8, "Brilliant Diamond & Shining Pearl"),
        ("legends-arceus", 8, "Legends: Arceus"),
        ("scarlet-violet", 9, "Scarlet & Violet"),
    ];

    private static readonly string[] FirstOfGeneration =
        ["", "Red & Blue", "Gold & Silver", "Ruby & Sapphire", "Diamond & Pearl", "Black & White", "X & Y", "Sun & Moon", "Sword & Shield", "Scarlet & Violet"];

    private static readonly string[] Roman = ["", "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix"];

    /// <summary>The lore in <paramref name="json"/>, or null when the document cannot be read.</summary>
    public static ItemLore? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ItemLore(About(root), FirstSeen(root), HeldBy(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The latest English description; the short effect when the games wrote none.</summary>
    private static string? About(JsonElement root)
    {
        string? latest = null;
        var latestOrder = -1;
        foreach (var entry in Array(root, "flavor_text_entries"))
        {
            if (Name(entry, "language") != "en") continue;
            var order = System.Array.FindIndex(Groups, g => g.Group == Name(entry, "version_group"));
            if (order < latestOrder || !entry.TryGetProperty("text", out var text)) continue;
            latestOrder = order;
            latest = text.GetString();
        }
        if (latest is null)
            foreach (var entry in Array(root, "effect_entries"))
                if (Name(entry, "language") == "en" && entry.TryGetProperty("short_effect", out var effect))
                    latest = effect.GetString();
        return latest is null ? null : Tidy(latest);
    }

    /// <summary>"Gold & Silver (Gen 2)": the first generation that has the item, and its first game there.</summary>
    private static string? FirstSeen(JsonElement root)
    {
        var first = int.MaxValue;
        foreach (var index in Array(root, "game_indices"))
        {
            var generation = System.Array.IndexOf(Roman, (Name(index, "generation") ?? "").Replace("generation-", "", StringComparison.Ordinal));
            if (generation > 0) first = Math.Min(first, generation);
        }
        if (first == int.MaxValue) return null;

        // A description from that generation names the exact game; otherwise its first pair.
        var title = FirstOfGeneration[first];
        var best = int.MaxValue;
        foreach (var entry in Array(root, "flavor_text_entries"))
        {
            var order = System.Array.FindIndex(Groups, g => g.Group == Name(entry, "version_group"));
            if (order >= 0 && Groups[order].Generation == first && order < best) best = order;
        }
        if (best != int.MaxValue) title = Groups[best].Title;
        return $"{title} (Gen {first})";
    }

    /// <summary>"Koffing, Weezing and Salandit": up to three wild Pokémon that may carry it.</summary>
    private static string? HeldBy(JsonElement root)
    {
        var names = Array(root, "held_by_pokemon")
            .Select(entry => Name(entry, "pokemon"))
            .OfType<string>()
            .Where(name => !name.Contains('-', StringComparison.Ordinal)) // forms ("salazzle-totem") repeat a name
            .Select(name => char.ToUpperInvariant(name[0]) + name[1..])
            .Distinct()
            .Take(3)
            .ToList();
        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>The games' text wraps at their own width and shouts POKéMON: one line, written as today.</summary>
    public static string Tidy(string text)
    {
        var one = string.Join(' ', text.Split(['\n', '\f', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));
        return one.Replace("POKéMON", "Pokémon", StringComparison.Ordinal)
                  .Replace("POKé BALL", "Poké Ball", StringComparison.Ordinal)
                  .Replace("POKéBALL", "Poké Ball", StringComparison.Ordinal);
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Name(JsonElement entry, string field) =>
        entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("name", out var name) ? name.GetString() : null;
}
