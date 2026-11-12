namespace PKForge.Domain;

/// <summary>How a bag pouch is shown: the name the games use and the icon beside it.</summary>
public sealed record BagPouchLook(string Title, string Icon);

/// <summary>
/// Pouches arrive as the engine's type names ("KeyItems", "TMHMs"). The bag shows them as
/// the games write them, each with its own icon, so a pouch is found at a glance.
/// </summary>
public static class BagPouchLooks
{
    public static BagPouchLook Of(string pouch) => pouch switch
    {
        "Items" => new("Items", "bag"),
        "KeyItems" => new("Key Items", "key"),
        "TMHMs" => new("TMs & HMs", "moves"),
        "Medicine" => new("Medicine", "heal"),
        "Berries" => new("Berries", "tree"),
        "Balls" => new("Poké Balls", "ball"),
        "BattleItems" => new("Battle Items", "battle"),
        "MailItems" => new("Mail", "inbox"),
        "PCItems" => new("PC Storage", "storage"),
        "FreeSpace" => new("Free Space", "bag"),
        "ZCrystals" => new("Z-Crystals", "power"),
        "Candy" => new("Candy", "level"),
        "Treasure" => new("Treasures", "rarity"),
        "Ingredients" => new("Ingredients", "fill"),
        "MegaStones" => new("Mega Stones", "evolve"),
        _ => new(Spaced(pouch), "item"),
    };

    /// <summary>"SomeNewPouch" reads "Some New Pouch" until it gets a name of its own.</summary>
    private static string Spaced(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Pouch";
        var text = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }
}
