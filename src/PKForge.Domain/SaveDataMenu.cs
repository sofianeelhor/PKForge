namespace PKForge.Domain;

/// <summary>Every place Save data leads to.</summary>
public enum SaveDataEntry
{
    TrainerCard, HallOfFame, Pokedex, TrainerRecords, Fashion, Compass,
    Bag, PokeBeans, PoffinCase, PokeblockCase, Underground, GrandUnderground,
    EventFlags, World, HoneyTrees, Raids, EventGallery, GiftInbox, KeyItemEvents,
    RestorePoints, Export, Close,
    Bytes, SaveBlocks,
    ZygardeCells, FestivalPlaza, SuperTraining,
    Pokewalker,
    EReader,
    PalPark,
}

/// <param name="Short">The name on the app tile; <paramref name="Title"/> is the full one, shown with the detail.</param>
public sealed record SaveDataRow(SaveDataEntry Entry, string Title, string Detail, string Icon, string Short);
// Icon names the bundled item sprite the tool shows (ui/items/{Icon}.png), as the games draw their key items.

public sealed record SaveDataGroup(string Title, string Icon, IReadOnlyList<SaveDataRow> Rows);

/// <summary>What the open save and the current mode allow; an entry it rules out is not shown.</summary>
public sealed record SaveDataCapabilities(
    bool Fashion = false, bool Compass = false,
    bool PokeBeans = false, bool GrandUnderground = false,
    bool EventFlags = false, bool World = false, bool EventGallery = false, bool KeyItemEvents = false,
    bool Bytes = false, bool SaveBlocks = false,
    bool HoneyTrees = false, bool HallOfFame = false, bool Raids = false,
    bool PoffinCase = false, bool PokeblockCase = false, bool Underground = false,
    bool ZygardeCells = false, bool FestivalPlaza = false, bool SuperTraining = false,
    bool Pokewalker = false, bool EReader = false, bool PalPark = false,
    // Every retail save has these; a ROM hack engine may not (no dex cells, no records, no bag).
    bool TrainerCard = true, bool Pokedex = true, bool TrainerRecords = true, bool Bag = true, bool GiftInbox = true);

/// <summary>
/// Save data, grouped by what the player wants to do: the trainer, the items, the world,
/// the file itself, and the raw tools. Each entry appears once, in one group; a group with
/// nothing in it for this save is left out.
/// </summary>
public static class SaveDataMenu
{
    public static IReadOnlyList<SaveDataGroup> Build(SaveDataCapabilities can)
    {
        ArgumentNullException.ThrowIfNull(can);
        var groups = new List<SaveDataGroup>
        {
            Group("Trainer", "trainer",
                Row(can.TrainerCard, SaveDataEntry.TrainerCard, "Trainer card", "Name, ID, money, play time and the rest of the card.", "member-card", "Card"),
                Row(can.HallOfFame, SaveDataEntry.HallOfFame, "Hall of Fame", "Champion teams: see, add or remove.", "hall-of-fame", "Hall of Fame"),
                Row(can.Pokedex, SaveDataEntry.Pokedex, "Pokédex", "Seen and caught, one species or all of them.", "pokedex"),
                Row(can.TrainerRecords, SaveDataEntry.TrainerRecords, "Trainer records", "The game's own counters, to read.", "vs-recorder", "Records"),
                Row(can.Fashion, SaveDataEntry.Fashion, "Fashion", "Unlock every outfit this save can legally own.", "fashion-case"),
                Row(can.Compass, SaveDataEntry.Compass, "Compass settings", "Exp share, level cap and the hack's other options.", "poke-radar", "Compass"),
                Row(can.SuperTraining, SaveDataEntry.SuperTraining, "Super Training", "Training bags, unlocked regimens and best times.", "hp-up", "Training"),
                Row(can.FestivalPlaza, SaveDataEntry.FestivalPlaza, "Festival Plaza", "Festival Coins, plaza rank, rewards and facilities.", "pass", "Plaza")),
            Group("Items", "bag",
                Row(can.Bag, SaveDataEntry.Bag, "Bag", "Every pouch, with counts, search and presets.", "prop-case"),
                Row(can.PokeBeans, SaveDataEntry.PokeBeans, "Poké Beans", "Kept for Poké Pelago.", "oran-berry"),
                Row(can.PoffinCase, SaveDataEntry.PoffinCase, "Poffin case", "Flavors and smoothness; fill or empty it.", "poffin-case", "Poffins"),
                Row(can.PokeblockCase, SaveDataEntry.PokeblockCase, "Pokéblock case", "Each kind with its count; fill or empty it.", "pokeblock-case", "Pokéblocks"),
                Row(can.Underground, SaveDataEntry.Underground, "Underground", "Goods, traps, treasures, spheres and records.", "explorer-kit"),
                Row(can.GrandUnderground, SaveDataEntry.GrandUnderground, "Grand Underground", "Spheres, statues and the rest kept apart from the bag.", "explorer-kit", "Underground")),
            Group("World", "map",
                Row(can.World, SaveDataEntry.World, "World & encounters", "Clocks, roaming Pokémon, one-time encounters.", "town-map", "Encounters"),
                Row(can.HoneyTrees, SaveDataEntry.HoneyTrees, "Honey trees", "See and change them, Munchlax ones included.", "honey", "Honey"),
                Row(can.Pokewalker, SaveDataEntry.Pokewalker, "Pokéwalker", "Steps, watts, courses and the Pokémon on a stroll.", "power-anklet", "Pokéwalker"),
                Row(can.PalPark, SaveDataEntry.PalPark, "Pal Park", "The six Pokémon migrated from a GBA game, and the once-a-day lock.", "park-ball", "Pal Park"),
                Row(can.Raids, SaveDataEntry.Raids, "Raids", "Max Raid dens and Tera raids: activate, clear, catch 7-stars again.", "poke-ball", "Raids"),
                Row(can.ZygardeCells, SaveDataEntry.ZygardeCells, "Zygarde Cells", "Which are picked up; collect all or put them back.", "dowsing-machine", "Cells"),
                Row(can.EventFlags, SaveDataEntry.EventFlags, "Story flags", "Turn story and event switches on or off.", "journal", "Flags"),
                Row(can.EventGallery, SaveDataEntry.EventGallery, "Mystery Gift gallery", "Everything this save can receive.", "gracidea", "Gift gallery"),
                Row(can.GiftInbox, SaveDataEntry.GiftInbox, "Gifts in this save", "Cards waiting to be collected: add, delete, received flags.", "letter", "Stored gifts"),
                Row(can.KeyItemEvents, SaveDataEntry.KeyItemEvents, "Ticket events", "Eon Ticket, Old Sea Map, Member Card and the like.", "eon-ticket", "Tickets"),
                Row(can.EReader, SaveDataEntry.EReader, "e-Reader", "Its berry and Battle trainer: import, keep a copy, clear.", "enigma-berry", "e-Reader")),
            Group("File", "sdcard",
                Row(true, SaveDataEntry.RestorePoints, "Restore points", "Go back to the save as it was before a change.", "timer-ball", "Restore"),
                Row(true, SaveDataEntry.Export, "Export a copy", "Share the edited save file.", "pal-pad", "Export"),
                Row(true, SaveDataEntry.Close, "Close save", "Back to the games.", "door", "Close")),
            Group("Advanced", "hex",
                Row(can.SaveBlocks, SaveDataEntry.SaveBlocks, "Save blocks", "Browse them and change simple values.", "basement-key", "Blocks"),
                Row(can.Bytes, SaveDataEntry.Bytes, "Raw bytes", "Edit the file byte by byte; written when you close.", "card-key", "Bytes")),
        };
        return [.. groups.Where(group => group.Rows.Count > 0)];
    }

    private static SaveDataGroup Group(string title, string icon, params SaveDataRow?[] rows) =>
        new(title, icon, [.. rows.OfType<SaveDataRow>()]);

    private static SaveDataRow? Row(bool shown, SaveDataEntry entry, string title, string detail, string icon, string? shortName = null) =>
        shown ? new SaveDataRow(entry, title, detail, icon, shortName ?? title) : null;
}
