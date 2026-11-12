using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>The e-Reader berry a Gen 3 save holds, as the game reads it.</summary>
/// <param name="Present">False when the slot is empty: Enigma Berries then stay plain Enigma Berries.</param>
/// <param name="Name">The berry's name ("PUMKIN", "DRASH"…), empty when absent.</param>
/// <param name="Valid">The game's own check passes (growth time, yield and checksum); an invalid berry is ignored in game.</param>
/// <param name="Distribution">Which official distribution the name matches, or why it does not.</param>
/// <param name="BlockSize">The size of the berry in this game: 1328 bytes in Ruby/Sapphire (with its picture and texts), 52 in Emerald and FireRed/LeafGreen.</param>
public sealed record EReaderBerryInfo(bool Present, string Name, bool Valid, string Distribution, int BlockSize, int EnigmaBerriesInBag);

/// <summary>The Battle e-Reader trainer (Battle Tower / Trainer Tower card) a Gen 3 save holds.</summary>
public sealed record EReaderTrainerInfo(bool Present, string Name, bool Valid);

/// <summary>Everything e-Reader the open Gen 3 save keeps.</summary>
public sealed record EReaderInfo(string Game, bool Japanese, EReaderBerryInfo Berry, EReaderTrainerInfo Trainer);

/// <summary>
/// Gen 3 e-Reader data, after what PKHeX exposes for SAV3: the e-Reader berry
/// (<see cref="ISaveBlock3Large.EReaderBerry"/>, which PKHeX's legality reads through
/// <see cref="EReaderBerrySettings"/>) and the Battle e-Reader trainer
/// (<see cref="ISaveBlock3Small.EReaderTrainer"/>). Data moves only as whole blocks the game
/// itself would accept: an import must have this game's exact size and pass the game's own
/// check, so nothing half-written ever lands. Clearing writes zeros, what the games' own
/// ClearEnigmaBerry / ClearEReaderTrainer write.
/// <para>
/// The game checks (pret disassemblies): IsEnigmaBerryValid wants a growth time, a max yield
/// and the byte sum of the block before its checksum (pokeruby sums it with the two description
/// pointers set to null); ValidateEReaderTrainer sums the trainer as 32-bit words.
/// </para>
/// </summary>
public static class EReaderService
{
    /// <summary>Gen 3's item id for the Enigma Berry (what an e-Reader berry is held as).</summary>
    public const int EnigmaBerryItem = 175;

    private const int TrainerSize = 0xBC;
    private const int BerryNameLength = 7;
    private const int MaxYieldOffset = 0x0A;
    private const int DescriptionPointersOffset = 0x0C;
    private const int GrowthDurationOffset = 0x14;
    // pokeemerald BattleTowerEReaderTrainer: unk0, facilityClass, winSpeechId (u16), then name[8].
    private const int TrainerNameOffset = 4;
    private const int TrainerNameLength = 8;

    private static readonly string BerryPouch = nameof(InventoryType.Berries);

    public static bool IsSupported(ISaveEngineSession session) => Save(session) is not null;

    public static EReaderInfo Get(ISaveEngineSession session)
    {
        var save = Save(session) ?? throw new InvalidOperationException("e-Reader data is kept by Ruby, Sapphire, Emerald, FireRed and LeafGreen only.");
        return new EReaderInfo(GameName(save), save.Japanese, ReadBerry(session, save), ReadTrainer(save));
    }

    /// <summary>
    /// Points PKHeX's legality at this save's berry, as PKHeX's own save-load step does: a
    /// Pokémon holding an Enigma Berry is checked against the stored name. Called for the open
    /// save when it opens and after its berry changes; the setting is process-wide.
    /// </summary>
    public static void RefreshLegality(ISaveEngineSession session)
    {
        if (Save(session) is { } save) EReaderBerrySettings.LoadFrom(save);
    }

    // ── Berry ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores an e-Reader berry from <paramref name="file"/>: the berry block itself (a dump of this
    /// game's exact size) or a whole save of the same game family holding one. Refused unless the
    /// game would accept it.
    /// </summary>
    public static GenerationOutcome ImportBerry(ISaveEngineSession session, ReadOnlySpan<byte> file)
    {
        if (Save(session) is not { } save)
            return new GenerationOutcome(false, "e-Reader berries belong to Ruby, Sapphire, Emerald, FireRed and LeafGreen.");
        var target = save.LargeBlock.EReaderBerry;
        byte[] block;
        if (file.Length == target.Length) block = file.ToArray();
        else if (FromSave(file, target.Length, out var fromSave, out var why)) block = fromSave;
        else return new GenerationOutcome(false, why ?? $"This is not an e-Reader berry for {GameName(save)}: one is {target.Length} bytes, this file is {file.Length}.");

        if (IsEmpty(block))
            return new GenerationOutcome(false, "That file holds no e-Reader berry.");
        if (!IsBerryValid(block))
            return new GenerationOutcome(false, "That berry fails the game's own check (checksum, growth time or yield), so the game would ignore it. Nothing was written.");
        block.CopyTo(target);
        return new GenerationOutcome(true, $"e-Reader berry {BerryName(save, block)} stored. Enigma Berries in this save are now {BerryName(save, block)}.");
    }

    /// <summary>The stored berry block, to keep as a file; null when the slot is empty.</summary>
    public static byte[]? ExportBerry(ISaveEngineSession session)
    {
        if (Save(session) is not { } save) return null;
        var block = save.LargeBlock.EReaderBerry;
        return IsEmpty(block) ? null : block.ToArray();
    }

    /// <summary>Empties the e-Reader berry slot (zeros, as the game's ClearEnigmaBerry): Enigma Berries become plain Enigma Berries again.</summary>
    public static GenerationOutcome ClearBerry(ISaveEngineSession session)
    {
        if (Save(session) is not { } save)
            return new GenerationOutcome(false, "This save keeps no e-Reader berry.");
        var block = save.LargeBlock.EReaderBerry;
        if (IsEmpty(block)) return new GenerationOutcome(false, "No e-Reader berry is stored.");
        block.Clear();
        return new GenerationOutcome(true, "e-Reader berry cleared.");
    }

    /// <summary>Puts one Enigma Berry in the Berries pouch when there is none, to plant or hold the stored berry.</summary>
    public static GenerationOutcome GiveEnigmaBerry(ISaveEngineSession session)
    {
        if (Save(session) is null)
            return new GenerationOutcome(false, "This save keeps no e-Reader berry.");
        if (CountInBag(session) > 0) return new GenerationOutcome(false, "The bag already holds an Enigma Berry.");
        return session.SetItemCount(BerryPouch, EnigmaBerryItem, 1) >= 1
            ? new GenerationOutcome(true, "One Enigma Berry added to the Berries pouch.")
            : new GenerationOutcome(false, "The Berries pouch has no room for an Enigma Berry.");
    }

    /// <summary>The game's IsEnigmaBerryValid: growth time, max yield and checksum.</summary>
    public static bool IsBerryValid(ReadOnlySpan<byte> block)
    {
        if (block.Length < GrowthDurationOffset + 5) return false;
        if (block[GrowthDurationOffset] == 0 || block[MaxYieldOffset] == 0) return false;
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(block[^4..]);
        uint sum = 0, withoutPointers = 0;
        for (var i = 0; i < block.Length - 4; i++)
        {
            sum += block[i];
            if (i is < DescriptionPointersOffset or >= DescriptionPointersOffset + 8) withoutPointers += block[i];
        }
        // Emerald and FireRed/LeafGreen sum every byte; Ruby/Sapphire null the description pointers first.
        return stored == sum || stored == withoutPointers;
    }

    /// <summary>Writes the checksum the game expects (every byte before it, summed); for tests and repairs.</summary>
    public static void SignBerry(Span<byte> block)
    {
        uint sum = 0;
        for (var i = 0; i < block.Length - 4; i++) sum += block[i];
        BinaryPrimitives.WriteUInt32LittleEndian(block[^4..], sum);
    }

    private static EReaderBerryInfo ReadBerry(ISaveEngineSession session, SAV3 save)
    {
        var block = save.LargeBlock.EReaderBerry;
        var inBag = CountInBag(session);
        if (save.IsEBerryEngima)
            return new EReaderBerryInfo(false, "", false, "No e-Reader berry: Enigma Berries stay plain.", block.Length, inBag);
        var name = save.EBerryName;
        var distribution = (EReaderBerrySettings.IsEReaderBerriesNameUSA(name), EReaderBerrySettings.IsEReaderBerriesNameJPN(name), save.Japanese) switch
        {
            (true, _, false) => "An official English e-Reader berry.",
            (_, true, true) => "An official Japanese e-Reader berry.",
            (true, _, true) => "An English berry in a Japanese save: Pokémon holding it read as illegal.",
            (_, true, false) => "A Japanese berry in an English save: Pokémon holding it read as illegal.",
            _ => "Not an official distribution: Pokémon holding it read as illegal.",
        };
        return new EReaderBerryInfo(true, name, IsBerryValid(block), distribution, block.Length, inBag);
    }

    private static string BerryName(SAV3 save, ReadOnlySpan<byte> block) => save.GetString(block[..BerryNameLength]);

    private static int CountInBag(ISaveEngineSession session) => session.GetBag()
        .Where(p => p.Name == BerryPouch).SelectMany(p => p.Items).Where(i => i.Id == EnigmaBerryItem).Sum(i => i.Count);

    /// <summary>A whole Gen 3 save whose berry block has the size wanted: its berry, if any.</summary>
    private static bool FromSave(ReadOnlySpan<byte> file, int size, out byte[] block, out string? why)
    {
        block = [];
        why = null;
        if (file.Length < SaveUtil.SIZE_G3RAW) return false;
        if (!SaveParser.TryGetSaveFile(file.ToArray(), out var other) || other is not SAV3 source) return false;
        var berry = source.LargeBlock.EReaderBerry;
        if (berry.Length != size)
        {
            why = "That save is from another game family: Ruby/Sapphire berries carry their picture and texts, and do not fit Emerald or FireRed/LeafGreen.";
            return false;
        }
        if (IsEmpty(berry))
        {
            why = "That save holds no e-Reader berry.";
            return false;
        }
        block = berry.ToArray();
        return true;
    }

    // ── Trainer ──────────────────────────────────────────────────────────────

    /// <summary>The stored Battle e-Reader trainer, to keep as a file; null when absent.</summary>
    public static byte[]? ExportTrainer(ISaveEngineSession session)
    {
        if (Save(session) is not { } save) return null;
        var block = save.SmallBlock.EReaderTrainer;
        return IsEmpty(block) ? null : block.ToArray();
    }

    /// <summary>Stores a Battle e-Reader trainer dump (188 bytes) that passes the game's checksum.</summary>
    public static GenerationOutcome ImportTrainer(ISaveEngineSession session, ReadOnlySpan<byte> file)
    {
        if (Save(session) is not { } save)
            return new GenerationOutcome(false, "e-Reader trainers belong to Ruby, Sapphire, Emerald, FireRed and LeafGreen.");
        if (file.Length != TrainerSize)
            return new GenerationOutcome(false, $"This is not an e-Reader trainer: one is {TrainerSize} bytes, this file is {file.Length}.");
        if (IsEmpty(file) || !IsTrainerValid(file))
            return new GenerationOutcome(false, "That trainer fails the game's own checksum, so the game would erase it. Nothing was written.");
        file.CopyTo(save.SmallBlock.EReaderTrainer);
        return new GenerationOutcome(true, $"e-Reader trainer {TrainerName(save, file)} stored.");
    }

    /// <summary>Removes the Battle e-Reader trainer (zeros, as the game's ClearEReaderTrainer).</summary>
    public static GenerationOutcome ClearTrainer(ISaveEngineSession session)
    {
        if (Save(session) is not { } save)
            return new GenerationOutcome(false, "This save keeps no e-Reader trainer.");
        var block = save.SmallBlock.EReaderTrainer;
        if (IsEmpty(block)) return new GenerationOutcome(false, "No e-Reader trainer is stored.");
        block.Clear();
        return new GenerationOutcome(true, "e-Reader trainer cleared.");
    }

    /// <summary>The game's ValidateEReaderTrainer: the 32-bit words before the checksum, summed.</summary>
    public static bool IsTrainerValid(ReadOnlySpan<byte> block)
    {
        if (block.Length != TrainerSize) return false;
        uint sum = 0;
        for (var i = 0; i < TrainerSize - 4; i += 4) sum += BinaryPrimitives.ReadUInt32LittleEndian(block[i..]);
        return sum == BinaryPrimitives.ReadUInt32LittleEndian(block[^4..]);
    }

    /// <summary>Writes the trainer checksum the game expects; for tests and repairs.</summary>
    public static void SignTrainer(Span<byte> block)
    {
        uint sum = 0;
        for (var i = 0; i < TrainerSize - 4; i += 4) sum += BinaryPrimitives.ReadUInt32LittleEndian(block[i..]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[^4..], sum);
    }

    private static EReaderTrainerInfo ReadTrainer(SAV3 save)
    {
        var block = save.SmallBlock.EReaderTrainer;
        return IsEmpty(block)
            ? new EReaderTrainerInfo(false, "", false)
            : new EReaderTrainerInfo(true, TrainerName(save, block), IsTrainerValid(block));
    }

    private static string TrainerName(SAV3 save, ReadOnlySpan<byte> block) =>
        save.GetString(block.Slice(TrainerNameOffset, TrainerNameLength));

    // ── Shared ───────────────────────────────────────────────────────────────

    private static bool IsEmpty(ReadOnlySpan<byte> block) =>
        !block.ContainsAnyExcept((byte)0) || !block.ContainsAnyExcept((byte)0xFF);

    private static string GameName(SAV3 save) => save switch
    {
        SAV3RS => "Ruby/Sapphire",
        SAV3E => "Emerald",
        _ => "FireRed/LeafGreen",
    };

    // ROM hacks (Unbound, Radical Red) have their own session types and their own layouts.
    private static SAV3? Save(ISaveEngineSession session) =>
        session is SaveEngineSession { SaveFile: SAV3 save } ? save : null;
}
