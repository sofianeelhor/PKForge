namespace PKForge.Domain;

/// <summary>
/// An item the Item Bank can make: its bank key (the same key a deposit from that game would
/// give it, so a machine stays tied to its game family), the name to show, the game family it
/// is made for and its generation, and the most one stack of it holds in that game's bag.
/// </summary>
public sealed record ItemBankRecipe(string Key, string Name, string Game, int Generation, int StackCap)
{
    /// <summary>The game recorded as the item's origin: the family, marked as made here, so the
    /// bank always tells made items from ones that came out of a real save.</summary>
    public string Origin => ItemBankGenerator.OriginOf(Game);
}

/// <summary>
/// The Item Bank's generator, as pure rules: one generation adds at most a full stack of the
/// item in its game (never past the bank's own cap per item), records where it was made for,
/// and only ever changes the bank. No save is touched here; moving made items into a game goes
/// through the Item Bank's transfer, its checks and its restore point.
/// </summary>
public static class ItemBankGenerator
{
    public static string OriginOf(string game) => $"{game} (made)";

    /// <summary>How many one generation can add: the game's stack cap, within the bank's room.
    /// 0 when the bank already holds the most it keeps of this item.</summary>
    public static int Most(ItemBank bank, ItemBankRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(recipe);
        return Math.Max(0, Math.Min(Math.Max(1, recipe.StackCap), bank.Room(recipe.Key)));
    }

    /// <summary>Why the recipe cannot be made at all, or null when it can.</summary>
    public static string? Refusal(ItemBank bank, ItemBankRecipe recipe) =>
        Most(bank, recipe) <= 0 ? $"The Item Bank already holds the most it keeps of {recipe.Name} ({ItemBank.MaxPerEntry})." : null;

    /// <summary>
    /// Adds <paramref name="amount"/> of the item to the bank, under its family's origin.
    /// Throws when the amount is below 1 or above <see cref="Most"/>: the caller's arrows never
    /// offer more, and a stale amount is refused rather than trimmed in silence.
    /// </summary>
    public static ItemBank Make(ItemBank bank, ItemBankRecipe recipe, int amount)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Key);
        if (Refusal(bank, recipe) is { } refused) throw new InvalidOperationException(refused);
        var most = Most(bank, recipe);
        if (amount < 1 || amount > most)
            throw new InvalidOperationException($"{recipe.Name}: between 1 and {most} can be made at once.");
        return bank.Deposit(recipe.Key, recipe.Name, amount, recipe.Origin, recipe.Generation);
    }

    /// <summary>The recipes a search and a pouch show, by name. A null pouch is every pouch;
    /// the search folds case, accents and spacing the way the bank's own search does.</summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> recipes, Func<T, string> name, Func<T, string> pouchOf, string? pouch, string? query)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        var needle = ItemBankKeys.NameKey(query ?? "");
        return [.. recipes
            .Where(r => pouch is null || pouchOf(r) == pouch)
            .Where(r => needle.Length == 0 || ItemBankKeys.NameKey(name(r)).Contains(needle, StringComparison.Ordinal))
            .OrderBy(name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
