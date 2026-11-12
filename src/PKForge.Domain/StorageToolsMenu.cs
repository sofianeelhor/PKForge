namespace PKForge.Domain;

/// <summary>Every tool Storage tools leads to.</summary>
public enum StorageTool
{
    MultiSelect, BoxManager, SortBoxes,
    ImportFiles, ImportShowdown, RandomTeam, EggFactory, LivingDex, DayCare,
    BatchEditor, BattlePrep, Presets, Rename, TrainerProfiles,
    HowToGet, FindItem, IvRanking, CollectionDex,
    LegalityCheck, AuditReport, NuzlockeReport, ExportShowdown,
}

public sealed record StorageToolRow(StorageTool Entry, string Title, string Detail, string Icon, string Short);

public sealed record StorageToolGroup(string Title, IReadOnlyList<StorageToolRow> Rows);

/// <summary>What the open save and the current mode allow; a tool it rules out is not shown.</summary>
/// <param name="BoxTools">The stock engine's bulk box tools (romhack sessions refuse them).</param>
/// <param name="CreateMon">New Pokémon may be made (not in Hardcore mode).</param>
/// <param name="BatchEdit">Many Pokémon may be changed at once (not in Hardcore mode).</param>
/// <param name="LivingDex">A living dex made for this exact game ships with the app.</param>
public sealed record StorageToolsCapabilities(bool BoxTools = false, bool CreateMon = false, bool BatchEdit = false, bool LivingDex = true);

/// <summary>
/// Storage tools, grouped by what the player wants to do with the boxes: organise them, add
/// Pokémon, change many at once, find one, and check or share. Each tool appears once; a
/// group with nothing in it for this save is left out. The icon names a bundled item sprite
/// (ui/items/{Icon}.png), as the games draw their key items.
/// </summary>
public static class StorageToolsMenu
{
    public static IReadOnlyList<StorageToolGroup> Build(StorageToolsCapabilities can)
    {
        ArgumentNullException.ThrowIfNull(can);
        var groups = new List<StorageToolGroup>
        {
            Group("Organise",
                Row(true, StorageTool.MultiSelect, "Multi-select", "Mark several Pokémon to move, copy or edit them together. Also on the minus button.", "vs-seeker", "Select"),
                Row(true, StorageTool.BoxManager, "Box manager", "Reorder, copy, lock or empty whole boxes.", "storage-key", "Boxes"),
                Row(can.BoxTools, StorageTool.SortBoxes, "Sort boxes", "Order this box or all of them by dex number, level, type and more.", "tm-case", "Sort")),
            Group("Add",
                Row(can.CreateMon, StorageTool.ImportFiles, "Import .pk files", "Pick Pokémon files to fill this box's empty slots.", "poke-ball", "Files"),
                Row(can.CreateMon, StorageTool.ImportShowdown, "Import Showdown team", "Paste Showdown sets, see their legality, place them in empty slots.", "great-ball", "Showdown"),
                Row(can.CreateMon && can.BoxTools, StorageTool.RandomTeam, "Random team", "Roll a random legal team into the party or empty slots.", "quick-ball", "Random"),
                Row(can.CreateMon, StorageTool.EggFactory, "Egg factory", "Fill empty slots with eggs: one of each species, or one species.", "mystery-egg", "Eggs"),
                Row(can.CreateMon && can.LivingDex, StorageTool.LivingDex, "Generate Living Dex", "Replace every box with one legal Pokémon of each species.", "master-ball", "Living Dex"),
                Row(true, StorageTool.DayCare, "Day Care", "See who is at the Day Care or Nursery and bring them to the PC.", "oval-charm", "Day Care")),
            Group("Edit many",
                Row(can.BatchEdit && can.BoxTools, StorageTool.BatchEditor, "Batch editor", "Change level, IVs, EVs, healing and more on many Pokémon at once.", "rare-candy", "Batch"),
                Row(can.BatchEdit && can.BoxTools, StorageTool.BattlePrep, "Battle prep", "Heal, max PP or set levels for the party or every box.", "full-restore", "Battle prep"),
                Row(can.BatchEdit, StorageTool.Presets, "Presets", "One change, like Level 100 or perfect IVs, on a box or all boxes.", "hp-up", "Presets"),
                Row(can.BatchEdit && can.BoxTools, StorageTool.Rename, "Rename and original trainer", "Reset nicknames, or make you the original trainer, on many Pokémon.", "pass", "Rename / OT"),
                Row(true, StorageTool.TrainerProfiles, "Trainer profiles", "Save trainer details and give them to Pokémon as their original trainer.", "ss-ticket", "Profiles")),
            Group("Find",
                Row(true, StorageTool.HowToGet, "How to get a Pokémon", "Where and how to get a species in this game.", "town-map", "How to get"),
                Row(true, StorageTool.FindItem, "Find held item", "See which Pokémon hold an item and go to one.", "dowsing-machine", "Held items"),
                Row(true, StorageTool.IvRanking, "IV ranking", "Every Pokémon sorted by its IVs; pick one to go to it.", "silph-scope", "IVs"),
                Row(true, StorageTool.CollectionDex, "Collection dex", "Living dex tracker for the Bank and this save, normal and shiny.", "shiny-charm", "Collection")),
            Group("Check & share",
                Row(true, StorageTool.LegalityCheck, "Legality check", "Check every Pokémon in the save and repair the illegal ones.", "full-heal", "Legality"),
                Row(true, StorageTool.AuditReport, "Audit report", "Find cloned Pokémon and impossible values across the save.", "scope-lens", "Audit"),
                Row(true, StorageTool.NuzlockeReport, "Nuzlocke report", "The first catch on each route, from where each Pokémon was met.", "spell-tag", "Nuzlocke"),
                Row(true, StorageTool.ExportShowdown, "Export box to Showdown", "This box as Showdown text to copy or share.", "air-mail", "Export")),
        };
        return [.. groups.Where(group => group.Rows.Count > 0)];
    }

    private static StorageToolGroup Group(string title, params StorageToolRow?[] rows) =>
        new(title, [.. rows.OfType<StorageToolRow>()]);

    private static StorageToolRow? Row(bool shown, StorageTool entry, string title, string detail, string icon, string shortName) =>
        shown ? new StorageToolRow(entry, title, detail, icon, shortName) : null;
}
