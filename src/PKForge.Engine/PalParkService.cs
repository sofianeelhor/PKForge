using PKForge.Domain;
using PKHeX.Core;
using static System.Buffers.Binary.BinaryPrimitives;

namespace PKForge.Engine;

/// <summary>One of the six Pal Park places: the Pokémon migrated from a GBA game and waiting for
/// the Catching Show, or nobody.</summary>
public sealed record PalParkSlot(int Index, bool Occupied, ushort Species, byte Form, bool Shiny, string Nickname,
    string SpeciesName, int Level, string Origin, bool Legal, string Verdict);

/// <summary>The once-a-day migration lock. <see cref="Armed"/> is false when the stored console
/// MAC is all zeros: the game then skips the 24 h check entirely.</summary>
public sealed record PalParkLock(bool Armed, string Mac, int TrainersRecorded);

public sealed record PalParkState(IReadOnlyList<PalParkSlot> Slots, PalParkLock Lock)
{
    public int Occupied => Slots.Count(slot => slot.Occupied);
}

/// <summary>Where a Pokémon put into the Pal Park comes from: a slot of the open save, or the
/// bytes of a Bank entry with their recorded format.</summary>
public sealed record PalParkSource(int Box = 0, int Slot = 0, byte[]? Data = null, string? Format = null)
{
    public static PalParkSource FromSave(int box, int slot) => new(box, slot);
    public static PalParkSource FromBytes(byte[] data, string? format) => new(Data: data, Format: format);
}

/// <summary>What putting a Pokémon in would do. <see cref="Refusal"/> is set when it cannot go
/// in at all; <see cref="FromGba"/> is false for a Pokémon that never came from a GBA game
/// (allowed only in HaX mode, never legal).</summary>
public sealed record PalParkPreview(string? Refusal, bool FromGba, string SpeciesName, int Level, bool Legal, string Verdict,
    IReadOnlyList<string> Warnings)
{
    public bool Allowed => Refusal is null;
}

/// <summary>
/// The Pal Park of Diamond/Pearl, Platinum and HeartGold/SoulSilver: the six Pokémon a GBA
/// migration leaves for the Catching Show, and the transfer lock. The block is the games'
/// <c>MigratedPokemon</c> (0x688 bytes) inside the general block, so the general block's own
/// CRC (PKHeX's normal SAV4 save path) covers it; there is no count, the game counts the slots
/// whose species is not 0.
/// Layout (pret/pokeplatinum include/pal_park_transfers.h, src/pal_park_transfers.c;
/// pret/pokeheartgold palPark_migration; pret/pokediamond unk_0202BE98.s, save_arrays.c):
/// <c>mons[6]</c> party-format PK4 encrypted like party slots (an empty slot is an encrypted
/// blank PK4, not zeros), <c>gbaTrainerIds[20]</c> u32 at 0x588, <c>timeStamps[20]</c> s64 at
/// 0x5D8, <c>rtcOffset</c> s64 at 0x678, <c>macAddress[6]</c> at 0x680.
/// The lock (pokeplatinum src/main_menu/gba_migrator.c:1693-1742) is skipped when the MAC is all
/// zeros; otherwise MAC and rtcOffset must match the console and a day must have passed since
/// that GBA trainer's last migration. The game also refuses while any slot is still full.
/// rtcOffset and the MAC are never written here, except to clear the MAC.
/// </summary>
public static class PalParkService
{
    /// <summary>
    /// Offset of MigratedPokemon in the Diamond/Pearl general block, proven from pret/pokediamond
    /// against two PKHeX offsets that read real DP saves:
    /// <list type="bullet">
    /// <item>save_arrays.c:67-104: the general block's entries 31-34 are Mystery Gift
    /// (sub_0202AC20, 0x1354 bytes), Pal Park (sub_0202BE98, 0x688), sub_0202C0E0 (0x28) and
    /// Easy Chat (8); save.c:556 pads every entry by <c>4 - size % 4</c> (4 when already aligned),
    /// and the block ends with its 0x14-byte footer.</item>
    /// <item>Forward: SAV4DP's Mystery Gift offset 0xA6D0 + 0x1358 = 0xBA28.</item>
    /// <item>Backward: SAV4DP's general size 0xC100 − 0x14 − 0xC − 0x2C − 0x68C = 0xBA28.</item>
    /// <item>The same method gives Platinum's real-save offset (0xB4C0 + 0x1330 = 0xC7F0, with
    /// Platinum's extra 4 bytes of padding per entry), and DP's asm uses the same struct layout
    /// (0xEC stride, ids at 0x588, times at 0x5D8, rtcOffset at 0x678, MAC at 0x680).</item>
    /// </list>
    /// No real Diamond/Pearl save has been tried.
    /// </summary>
    public const int OffsetDP = 0xBA28;

    /// <summary>Platinum (pret/pokeplatinum save_table.c order), verified on a real save.</summary>
    public const int OffsetPt = 0xC7F0;

    /// <summary>HeartGold/SoulSilver (pret/pokeheartgold save_arrays.c), verified on two real saves.</summary>
    public const int OffsetHGSS = 0xB3C0;

    public const int BlockSize = 0x688;
    public const int SlotCount = 6;
    public const int SlotSize = 0xEC; // PK4 party size
    public const int TrainerIdsOffset = 0x588;
    public const int TimeStampsOffset = 0x5D8;
    public const int RtcOffsetOffset = 0x678;
    public const int MacOffset = 0x680;
    public const int HistoryCount = 20;
    public const int MaxSpecies = 493;

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    /// <summary>The MigratedPokemon block of a SAV4 save (a slice of PKHeX's General span).</summary>
    public static Span<byte> Block(SAV4 save) => save.General.Slice(OffsetOf(save) ?? throw Unsupported(), BlockSize);

    public static int? OffsetOf(SaveFile save) => save switch
    {
        SAV4DP => OffsetDP,
        SAV4Pt => OffsetPt,
        SAV4HGSS => OffsetHGSS,
        _ => null,
    };

    public static PalParkState GetState(ISaveEngineSession session)
    {
        var save = Require(session);
        var block = Block(save);
        var slots = new PalParkSlot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
            slots[i] = Describe(i, ReadSlot(save, i));
        return new PalParkState(slots, ReadLock(block));
    }

    /// <summary>The Pokémon in slot <paramref name="index"/>, decrypted as a party PK4 (species 0 when empty).</summary>
    public static PK4 ReadSlot(SAV4 save, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        var data = Block(save).Slice(index * SlotSize, SlotSize).ToArray();
        PokeCrypto.Decrypt45(data);
        return new PK4(data);
    }

    /// <summary>What putting <paramref name="source"/> in would make of it, without writing.</summary>
    public static PalParkPreview Preview(ISaveEngineSession session, PalParkSource source, bool allowNonGba) =>
        Prepare(session, source, allowNonGba).Preview;

    /// <summary>Holds a Pal Park arrival for Pokémon History until the save is written.</summary>
    private static void Remember(ISaveEngineSession session, HistoryTripPlan? trip, PK4 landed)
    {
        if (session is SaveEngineSession engine) engine.QueueHistory(trip, landed);
    }

    /// <summary>Writes the Pokémon into slot <paramref name="index"/> (a copy: the source is untouched).</summary>
    public static PalParkPreview Put(ISaveEngineSession session, int index, PalParkSource source, bool allowNonGba)
    {
        var save = Require(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        var (preview, pk, trip) = Prepare(session, source, allowNonGba);
        if (pk is null) return preview;
        WriteSlot(save, index, pk);
        Remember(session, trip, pk);
        return preview;
    }

    /// <summary>Encrypts a party PK4 (party stats refreshed) into slot <paramref name="index"/>.</summary>
    public static void WriteSlot(SAV4 save, int index, PK4 pk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        pk.ResetPartyStats();
        pk.RefreshChecksum();
        pk.WriteEncryptedDataParty(Block(save).Slice(index * SlotSize, SlotSize));
    }

    /// <summary>Empties a slot the way the game does: an encrypted blank PK4.</summary>
    public static void Clear(ISaveEngineSession session, int index)
    {
        var save = Require(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        EmptySlot().CopyTo(Block(save).Slice(index * SlotSize, SlotSize));
    }

    /// <summary>The bytes of an empty slot: a zeroed PK4 (checksum 0, PID 0) encrypted.</summary>
    public static byte[] EmptySlot()
    {
        var data = new byte[SlotSize];
        PokeCrypto.Encrypt45(data);
        return data;
    }

    /// <summary>
    /// Lifts the once-a-day lock by zeroing the stored MAC (the game skips the check then).
    /// With <paramref name="history"/>, the GBA trainer IDs and their timestamps go too.
    /// rtcOffset is left alone. Returns false when there was nothing to clear.
    /// </summary>
    public static bool ClearLock(ISaveEngineSession session, bool history)
    {
        var block = Block(Require(session));
        var mac = block.Slice(MacOffset, 6);
        var changed = mac.ContainsAnyExcept((byte)0);
        mac.Clear();
        if (history)
        {
            var ids = block.Slice(TrainerIdsOffset, RtcOffsetOffset - TrainerIdsOffset); // ids + timestamps
            changed |= ids.ContainsAnyExcept((byte)0);
            ids.Clear();
        }
        return changed;
    }

    /// <summary>Puts the sources, in order, into the empty slots; returns how many went in and why
    /// the others did not.</summary>
    public static (int Placed, IReadOnlyList<string> Skipped) Fill(ISaveEngineSession session, IEnumerable<PalParkSource> sources, bool allowNonGba)
    {
        var save = Require(session);
        var skipped = new List<string>();
        var placed = 0;
        var free = new Queue<int>(Enumerable.Range(0, SlotCount).Where(i => ReadSlot(save, i).Species == 0));
        foreach (var source in sources)
        {
            if (free.Count == 0) break;
            var (preview, pk, trip) = Prepare(session, source, allowNonGba);
            if (pk is null)
            {
                if (preview.Refusal is { } why && preview.SpeciesName.Length > 0) skipped.Add($"{preview.SpeciesName}: {why}");
                continue;
            }
            WriteSlot(save, free.Dequeue(), pk);
            Remember(session, trip, pk);
            placed++;
        }
        return (placed, skipped);
    }

    private static (PalParkPreview Preview, PK4? Entity, HistoryTripPlan? Trip) Prepare(ISaveEngineSession session, PalParkSource source, bool allowNonGba)
    {
        var save = Require(session);
        PKM? pk;
        if (source.Data is { } data) pk = EntityBytes.Parse(data, source.Format);
        else pk = (session as SaveEngineSession)?.GetEntity(source.Box, source.Slot); // Require() vouched for it
        if (pk is null || pk.Species == 0)
            return (Refuse("There is no Pokémon there.", ""), null, null);
        var name = Name(pk.Species);
        if (pk.IsEgg)
            return (Refuse("An egg cannot be migrated: only hatched Pokémon go to the Pal Park.", name), null, null);
        if (pk.Species > MaxSpecies)
            return (Refuse($"{name} is not one of the 493 Pokémon the Pal Park knows.", name), null, null);

        var fromGba = pk.Format == 3 || (pk is PK4 already && already.Generation == 3);
        if (!fromGba && !allowNonGba)
            return (Refuse("A Pal Park Pokémon has to come from a GBA game (Ruby, Sapphire, Emerald, FireRed, LeafGreen, Colosseum or XD). Turn on HaX mode to put it in anyway.", name), null, null);

        var warnings = new List<string>();
        PK4 result;
        HistoryTripPlan? trip = null;
        if (pk is PK4 pk4) result = (PK4)pk4.Clone();
        else
        {
            var converted = CrossFormatConverter.Convert(pk, save, out var refusal);
            // Pokémon History: one coming back to Gen 4 starts from the copy it had here.
            if (converted is not null && (session as SaveEngineSession)?.HistorySource?.Invoke() is { } history)
                (converted, trip) = PokemonHistoryService.Apply(history, pk, converted, save);
            if (converted?.Entity is not PK4 c) return (Refuse(refusal ?? "This Pokémon cannot be stored in a Gen 4 game.", name), null, null);
            result = c;
            warnings.AddRange(converted.Warnings);
        }
        if (!fromGba)
            warnings.Insert(0, "Not from a GBA game: a Pal Park Pokémon must have migrated from Ruby, Sapphire, Emerald, FireRed, LeafGreen, Colosseum or XD to be legal. It will be flagged.");

        result.ResetPartyStats();
        result.RefreshChecksum();
        var (legal, verdict) = Verdict(result);
        return (new PalParkPreview(null, fromGba, name, result.CurrentLevel, legal, verdict, warnings), result, trip);
    }

    private static PalParkPreview Refuse(string why, string name) => new(why, false, name, 0, false, "", []);

    private static (bool Legal, string Verdict) Verdict(PK4 pk)
    {
        var analysis = new LegalityAnalysis(pk);
        if (analysis.Valid) return (true, "Legal");
        var reason = LegalityChecks.Group(analysis)
            .Where(check => check.Judgement == LegalityJudgement.Invalid)
            .SelectMany(check => check.Reasons.Select(r => $"{check.Name}: {r}"))
            .FirstOrDefault();
        return (false, reason is null ? "Not legal" : $"Not legal. {reason}");
    }

    private static PalParkSlot Describe(int index, PK4 pk)
    {
        if (pk.Species == 0 || pk.Species > MaxSpecies)
            return new PalParkSlot(index, false, 0, 0, false, "", "", 0, "", false, "");
        var species = Name(pk.Species);
        var origin = GameInfo.GetStrings("en").gamelist is var games && (int)pk.Version < games.Length ? games[(int)pk.Version] : pk.Version.ToString();
        var (legal, verdict) = Verdict(pk);
        return new PalParkSlot(index, true, pk.Species, pk.Form, pk.IsShiny, pk.IsNicknamed ? pk.Nickname : species, species,
            pk.CurrentLevel, origin, legal, verdict);
    }

    private static PalParkLock ReadLock(ReadOnlySpan<byte> block)
    {
        var mac = block.Slice(MacOffset, 6);
        var count = 0;
        for (var i = 0; i < HistoryCount; i++)
            if (ReadUInt32LittleEndian(block[(TrainerIdsOffset + (i * 4))..]) != 0) count++;
        return new PalParkLock(mac.ContainsAnyExcept((byte)0), Convert.ToHexString(mac), count);
    }

    private static string Name(ushort species) => SpeciesName.GetSpeciesName(species, (int)LanguageID.English);

    private static SAV4? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine && engine.SaveFile is SAV4 save && OffsetOf(save) is not null ? save : null;

    private static SAV4 Require(ISaveEngineSession session) => TryGetSave(session) ?? throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("The Pal Park exists only in Diamond, Pearl, Platinum, HeartGold and SoulSilver.");
}
