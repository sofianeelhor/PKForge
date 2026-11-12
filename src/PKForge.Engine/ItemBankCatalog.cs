using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One game family the Item Bank can make items for: its label, the game PKHeX reads its
/// bag rules from, and its generation. <see cref="Id"/> is that game's PKHeX name; <see cref="Games"/>
/// spells out every game the label shortens.</summary>
public sealed record ItemBankFamily(string Id, string Label, int Generation, string Games);

/// <summary>An item the generator offers: the recipe the bank takes, the pouch it sits in in that
/// game (for the tabs) and its English name (for its art).</summary>
public sealed record ItemBankCatalogItem(ItemBankRecipe Recipe, string Pouch, string EnglishName);

/// <summary>
/// The Item Bank generator's catalogue, read from PKHeX's own item tables: for each game family,
/// every item its bag can hold that may also leave it for the bank (the same rules as a deposit:
/// no key items, no HMs, nothing the bank cannot key), keyed exactly as a deposit from that game
/// would be. So a made item is withdrawn, refused or tied to its family by the rules every banked
/// item already follows. Built once per family from a blank save, then kept.
/// </summary>
public static class ItemBankCatalog
{
    private static readonly (GameVersion Version, string Label, string Games)[] Games =
    [
        (GameVersion.RD, "Red / Blue / Yellow", "Red, Blue, Yellow"),
        (GameVersion.C, "Gold / Silver / Crystal", "Gold, Silver, Crystal"),
        (GameVersion.E, "Ruby / Sapphire / Emerald", "Ruby, Sapphire, Emerald"),
        (GameVersion.FR, "FireRed / LeafGreen", "FireRed, LeafGreen"),
        (GameVersion.Pt, "Diamond / Pearl / Platinum", "Diamond, Pearl, Platinum"),
        (GameVersion.HG, "HeartGold / SoulSilver", "HeartGold, SoulSilver"),
        (GameVersion.B2, "Black / White / B2W2", "Black, White, Black 2, White 2"),
        (GameVersion.AS, "X / Y / ORAS", "X, Y, Omega Ruby, Alpha Sapphire"),
        (GameVersion.UM, "Sun / Moon / USUM", "Sun, Moon, Ultra Sun, Ultra Moon"),
        (GameVersion.GP, "Let's Go Pikachu / Eevee", "Let's Go, Pikachu! and Let's Go, Eevee!"),
        (GameVersion.SW, "Sword / Shield", "Sword, Shield"),
        (GameVersion.BD, "BDSP", "Brilliant Diamond, Shining Pearl"),
        // Legends: Arceus is left out: a blank save of it has no bag blocks to read the rules from.
        (GameVersion.VL, "Scarlet / Violet", "Scarlet, Violet"),
        (GameVersion.ZA, "Legends: Z-A", "Legends: Z-A"),
    ];

    /// <summary>Every family, oldest first.</summary>
    public static IReadOnlyList<ItemBankFamily> Families { get; } =
        [.. Games.Select(g => new ItemBankFamily(g.Version.ToString(), g.Label, g.Version.Generation, g.Games))];

    private static readonly Dictionary<string, IReadOnlyList<ItemBankCatalogItem>> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>The family a game belongs to: the one sharing its bag rules (context and item
    /// table), so a Diamond save makes Platinum's items and a Yellow save Red/Blue's.</summary>
    public static ItemBankFamily? FamilyOf(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession { SaveFile: { } save }) return null;
        var version = save.Version;
        var game = Games.FirstOrDefault(g => SameGroup(g.Version, version));
        if (game.Label is null) game = Games.LastOrDefault(g => g.Version.Generation == save.Generation);
        return game.Label is null ? null : Families.First(f => f.Id == game.Version.ToString());
    }

    private static bool SameGroup(GameVersion family, GameVersion version) => (family, version) switch
    {
        (GameVersion.RD, GameVersion.RD or GameVersion.GN or GameVersion.BU or GameVersion.YW or GameVersion.RB or GameVersion.RBY) => true,
        (GameVersion.C, GameVersion.GD or GameVersion.SI or GameVersion.C or GameVersion.GS or GameVersion.GSC) => true,
        (GameVersion.E, GameVersion.R or GameVersion.S or GameVersion.E or GameVersion.RS or GameVersion.RSE) => true,
        (GameVersion.FR, GameVersion.FR or GameVersion.LG or GameVersion.FRLG) => true,
        (GameVersion.Pt, GameVersion.D or GameVersion.P or GameVersion.Pt or GameVersion.DP or GameVersion.DPPt) => true,
        (GameVersion.HG, GameVersion.HG or GameVersion.SS or GameVersion.HGSS) => true,
        (GameVersion.B2, GameVersion.B or GameVersion.W or GameVersion.B2 or GameVersion.W2 or GameVersion.BW or GameVersion.B2W2) => true,
        (GameVersion.AS, GameVersion.X or GameVersion.Y or GameVersion.OR or GameVersion.AS or GameVersion.XY or GameVersion.ORAS) => true,
        (GameVersion.UM, GameVersion.SN or GameVersion.MN or GameVersion.US or GameVersion.UM or GameVersion.SM or GameVersion.USUM) => true,
        (GameVersion.GP, GameVersion.GP or GameVersion.GE or GameVersion.GG) => true,
        (GameVersion.SW, GameVersion.SW or GameVersion.SH or GameVersion.SWSH) => true,
        (GameVersion.BD, GameVersion.BD or GameVersion.SP or GameVersion.BDSP) => true,
        (GameVersion.VL, GameVersion.SL or GameVersion.VL or GameVersion.SV) => true,
        _ => family == version,
    };

    /// <summary>The family's items, by name; empty for a family PKHeX cannot make a blank save of.</summary>
    public static IReadOnlyList<ItemBankCatalogItem> Items(ItemBankFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        lock (Gate)
        {
            if (Cache.TryGetValue(family.Id, out var known)) return known;
            var built = Build(family);
            Cache[family.Id] = built;
            return built;
        }
    }

    private static IReadOnlyList<ItemBankCatalogItem> Build(ItemBankFamily family)
    {
        if (!Enum.TryParse<GameVersion>(family.Id, out var version)) return [];
        SaveFile blank;
        try { blank = BlankSaveFile.Get(version, "PKForge", LanguageID.English); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
        {
            return [];
        }
        using var session = new SaveEngineSession(blank, null);
        var names = session.GetItemNames();
        var english = GameInfo.GetStrings("en").GetItemStrings(blank.Context, blank.Version);
        var bag = blank.Inventory;
        var items = new List<ItemBankCatalogItem>();
        foreach (var (key, place) in ItemBankService.Places(session))
        {
            if (ItemBankService.WhyKept(session, place.Pouch, place.ItemId) is not null) continue;
            var englishName = place.ItemId < english.Length ? english[place.ItemId] ?? "" : "";
            var name = place.ItemId < names.Count && names[place.ItemId].Length > 0 ? names[place.ItemId] : englishName;
            if (!Nameable(name) || !Nameable(englishName)) continue;
            var type = bag.Pouches.First(p => p.Type.ToString() == place.Pouch).Type;
            var cap = bag.GetMaxCount(type, place.ItemId);
            if (cap <= 0) continue;
            items.Add(new ItemBankCatalogItem(new ItemBankRecipe(key, name, family.Label, family.Generation, cap), place.Pouch, englishName));
        }
        return [.. items.OrderBy(i => i.Recipe.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>PKHeX's tables keep placeholders for unused ids ("???", "(unused)"): never offered.</summary>
    private static bool Nameable(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith('?') && !name.StartsWith('(') && !name.Contains("unused", StringComparison.OrdinalIgnoreCase);
}
