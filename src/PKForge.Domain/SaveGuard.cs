namespace PKForge.Domain;

/// <summary>
/// What an action does to a save, in Hardcore mode's terms. Management actions
/// (<see cref="Move"/>, <see cref="Release"/>, <see cref="ExportFile"/>,
/// <see cref="Backup"/>) never change a Pokémon's data; every other value either
/// edits data or fabricates it.
/// </summary>
public enum SaveAction
{
    /// <summary>Field edits to an existing Pokémon: nickname, level, moves, IVs, ability, OT, legalizing, RNG rerolls.</summary>
    EditMon,

    /// <summary>Fabricating a Pokémon that did not exist: the generator, Showdown paste, .pk import, encounter placement, egg factory, living dex.</summary>
    CreateMon,

    /// <summary>Wholesale edits across many Pokémon: the batch editor, its presets, batch rename/OT, battle prep, trainer-database sweeps.</summary>
    BatchEdit,

    /// <summary>Copying a Pokémon where the original stays: Duplicate, Copy to Bank, Copy to another game, copy boxes.</summary>
    Duplicate,

    /// <summary>Delivering an event: wonder-card injection, event gifts, event items and flags.</summary>
    InjectEvent,

    /// <summary>Bag, Poké Beans and Grand Underground quantities - adding items or changing counts.</summary>
    EditInventory,

    /// <summary>Trainer card, trainer records, in-save game settings, wardrobe unlocks.</summary>
    EditTrainer,

    /// <summary>Pokédex flags: setting seen/caught, completing or filling the dex.</summary>
    EditDex,

    /// <summary>In-game world state: honey trees and similar world/flag toggles.</summary>
    EditWorld,

    /// <summary>Raw byte writes from the hex editor.</summary>
    WriteRawBytes,

    /// <summary>Cartridge clock repair (Gen 2/3 RTC reset).</summary>
    RepairRtc,

    /// <summary>Changing where a Pokémon lives, never what it is: box moves, sorting, box reordering, bank and game transfers, deposits and withdrawals.</summary>
    Move,

    /// <summary>Deleting a Pokémon or clearing a slot; nothing is fabricated and no data is rewritten.</summary>
    Release,

    /// <summary>Writing a copy out to a file (.pk export, Showdown text, audit report); the save itself is untouched.</summary>
    ExportFile,

    /// <summary>Restore points: reading and writing backup copies of whole saves.</summary>
    Backup,
}

/// <summary>
/// The Hardcore-mode decision table, with no UI or engine dependency so it can be
/// unit tested directly. Hardcore mode exists for RetroAchievements-style play: a
/// save may be browsed, organised, moved, transferred, backed up and exported, but
/// nothing that edits or fabricates Pokémon data may be written - and a Pokémon may
/// be moved but never copied.
/// </summary>
public sealed class SaveGuard
{
    /// <summary>The default guard: everything is allowed.</summary>
    public static SaveGuard Off { get; } = new(false);

    /// <summary>The achievement-hunter guard: management stays, edits and copies are refused.</summary>
    public static SaveGuard Hardcore { get; } = new(true);

    /// <summary>The guard for the given Hardcore-mode setting.</summary>
    public static SaveGuard For(bool hardcore) => hardcore ? Hardcore : Off;

    private SaveGuard(bool hardcore) => IsHardcore = hardcore;

    /// <summary>True while Hardcore mode is on.</summary>
    public bool IsHardcore { get; }

    /// <summary>
    /// The whole policy in one place: everything is allowed normally; Hardcore mode
    /// refuses every data-changing action and keeps the management ones.
    /// </summary>
    public bool Allows(SaveAction action) => !IsHardcore
        || action is SaveAction.Move or SaveAction.Release or SaveAction.ExportFile or SaveAction.Backup;

    /// <summary>True when Hardcore mode refuses <paramref name="action"/> right now.</summary>
    public bool Blocks(SaveAction action) => !Allows(action);

    /// <summary>Mon field edits and legalizing.</summary>
    public bool CanEditMon => Allows(SaveAction.EditMon);

    /// <summary>The generator, Showdown paste, .pk import, encounters, eggs, living dex.</summary>
    public bool CanCreateMon => Allows(SaveAction.CreateMon);

    /// <summary>Batch editor, presets, batch rename/OT, battle prep.</summary>
    public bool CanBatchEdit => Allows(SaveAction.BatchEdit);

    /// <summary>Duplication and copy semantics, save side or bank side.</summary>
    public bool CanDuplicate => Allows(SaveAction.Duplicate);

    /// <summary>Wonder-card injection, event gifts, event items and flags.</summary>
    public bool CanInjectEvent => Allows(SaveAction.InjectEvent);

    /// <summary>Bag, Poké Beans and Grand Underground edits.</summary>
    public bool CanEditInventory => Allows(SaveAction.EditInventory);

    /// <summary>Trainer card, records, game settings and wardrobe unlocks.</summary>
    public bool CanEditTrainer => Allows(SaveAction.EditTrainer);

    /// <summary>Pokédex flags and completion.</summary>
    public bool CanEditDex => Allows(SaveAction.EditDex);

    /// <summary>Honey trees and other in-game world state.</summary>
    public bool CanEditWorld => Allows(SaveAction.EditWorld);

    /// <summary>Hex-editor byte writes.</summary>
    public bool CanWriteRawBytes => Allows(SaveAction.WriteRawBytes);

    /// <summary>Cartridge clock repair.</summary>
    public bool CanRepairRtc => Allows(SaveAction.RepairRtc);

    /// <summary>Box moves, sorting, box reordering, deposits, withdrawals and game transfers - always available.</summary>
    public bool CanMove => Allows(SaveAction.Move);

    /// <summary>Releasing or clearing a slot - always available: data leaves, nothing is fabricated.</summary>
    public bool CanRelease => Allows(SaveAction.Release);

    /// <summary>Exporting a copy to a file - always available: the save is untouched.</summary>
    public bool CanExport => Allows(SaveAction.ExportFile);

    /// <summary>Restore points - always available, they are the safety net.</summary>
    public bool CanBackup => Allows(SaveAction.Backup);

    /// <summary>
    /// True when only move semantics may be written: the save can be reorganised and
    /// Pokémon moved or transferred, but never edited, fabricated or copied.
    /// </summary>
    public bool CanMoveOnly => IsHardcore;
}
