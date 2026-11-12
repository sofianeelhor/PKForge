using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.RechargedYellow;

/// <summary>
/// The Gen 3 flash layout of Pokémon Recharged Yellow (Jaizu Productions), a
/// pokeemerald rebuild of Kanto whose save blocks are repacked.
///
/// Ground truth: the hack's own disassembly as recovered by the
/// switchglitch/recharged-yellow-save-editor project and a byte probe of the user's
/// v1.9 save. The flash envelope is retail: 32 sectors of 0x1000, 14 per rotating
/// slot, footer at 0xFF4 (id u16 @0xFF4, checksum u16 @0xFF6, 0x08012025 @0xFF8,
/// counter u32 @0xFFC). The Pokémon records are stock Gen 3 (80-byte encrypted box
/// records, 100-byte party records).
///
/// What moves:
/// <list type="bullet">
/// <item>SaveBlock1: party count at 0x3B, party at 0x44, money at 0x29C, coins
/// 0x2A0, bag pockets from 0x374/0x2380/0x464/0x52C/0x5AC/0x6AC/0x2AC, gameStats
/// at 0xB50. The v1.9 build has flags at 0xF6F, vars at 0x109C and badges at
/// 0x107F; the published 1.9.7 build has 0xEFB/0x1028/0x100B.</item>
/// <item>Block chunking: v1.9 writes SaveBlock1 as 0xFF4 x3 + 0xF14 (0x3EF0) and
/// the PC as 0xFF4 x8 + 0x7D0 (0x8770). v1.9.7 uses 0xF80 x3 + 0xF14 (0x3D94)
/// and 0xF80 x8 + 0x7D0 (0x83D0). Box names/wallpapers sit at 0x86E4/0x8762 in
/// v1.9 and 0x8344/0x83C2 in v1.9.7.</item>
/// </list>
///
/// The session builds a retail-Emerald-shaped image for PKHeX to edit, then maps
/// the edited fields back. That is why the reported "PC shows, team does not"
/// bug is fixed: PKHeX's party offsets are populated in the virtual image.
/// </summary>
internal static class RechargedYellowFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectionCount = 14;
    public const int SlotCount = 2;

    public const int BoxCount = 14;
    public const int BoxSlotCount = 30;
    public const int PcMonSize = 80;
    public const int PartyMonSize = 100;
    public const int BoxMonBytes = BoxCount * BoxSlotCount * PcMonSize; // 0x8340

    public const uint SectorSignature = 0x0801_2025;

    // SaveBlock1 field offsets shared by both supported builds.
    /// <summary>Minimum structural score for a save whose party does not prove the hack.</summary>
    internal const int WeakEvidenceScore = 12;

    public const int PartyCountOffset = 0x3B;
    public const int PartyOffset = 0x44;
    public const int MoneyOffset = 0x29C;
    public const int CoinsOffset = 0x2A0;
    public const int RegisteredItemOffset = 0x2A2;
    public const int GameStatsOffset = 0xB50;
    public const int PartySize = 6 * PartyMonSize;

    // Retail Emerald SaveBlock1 offsets used by the PKHeX view.
    public const int VanillaPartyCountOffset = 0x234;
    public const int VanillaPartyOffset = 0x238;
    public const int VanillaMoneyOffset = 0x490;
    public const int VanillaCoinsOffset = 0x494;
    public const int VanillaRegisteredItemOffset = 0x496;
    public const int VanillaGameStatsOffset = 0x159C;
    public const int VanillaFlagsOffset = 0x1270;
    public const int VanillaVarsOffset = 0x139C;
    public const int FlagsSize = 0x12C;
    public const int VarsSize = 0x200;
    public const int GameStatsSize = 0x100;

    // Retail SaveBlock2 and PC offsets used by the PKHeX view.
    public const int VanillaSecurityKeyOffset = 0xAC;
    public const int Sb2Chunk = 0xF64;
    public const int Sb2CopySize = 0x90;
    public const int VanillaBoxNamesOffset = 0x8344;
    public const int VanillaBoxWallpapersOffset = 0x83C2;

    // The scratch image must classify as Emerald, not Ruby/Sapphire: PKHeX uses
    // different flag/vars/record offsets for RS. A nonzero key plus a marker byte
    // in the RS-only region selects Emerald. The key is virtual only; money/coins
    // are XORed in the image and XORed back on materialize.
    private const uint VirtualSecurityKey = 0x5A17_3C21;
    private const int VirtualEmeraldMarkerOffset = 0x890;

    private static readonly int[] VanillaSb1Chunks = [0xF80, 0xF80, 0xF80, 0xF80];
    private static readonly int[] VanillaPcChunks = [0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80];
    private static readonly int[] VanillaWindows = [0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80];

    /// <summary>One build's block chunking and relocated regions.</summary>
    internal sealed record Profile(
        string Name,
        int[] Sb1Chunks,
        int[] PcChunks,
        int[] Windows,
        int FlagsOffset,
        int VarsOffset,
        int BadgeOffset,
        int BoxNamesOffset,
        int BoxWallpapersOffset,
        int PcBlockSize,
        int ObjectEventTemplatesOffset,
        int UnionRoomOffset);

    /// <summary>The user's v1.9 build: 0xFF4 block chunks, flags at 0xF6F.</summary>
    public static readonly Profile V19 = new(
        "Recharged Yellow 1.9",
        [0xFF4, 0xFF4, 0xFF4, 0xF14],
        [0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0x7D0],
        [0xF64, 0xFF4, 0xFF4, 0xFF4, 0xF14, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0xFF4, 0x7D0],
        FlagsOffset: 0xF6F,
        VarsOffset: 0x109C,
        BadgeOffset: 0x107F,
        BoxNamesOffset: 0x86E4,
        BoxWallpapersOffset: 0x8762,
        PcBlockSize: 0x8770,
        ObjectEventTemplatesOffset: 0x140C,
        UnionRoomOffset: 0x22A0);

    /// <summary>The published v1.9.7 build: 0xF80 block chunks, flags at 0xEFB.</summary>
    public static readonly Profile V197 = new(
        "Recharged Yellow 1.9.7",
        [0xF80, 0xF80, 0xF80, 0xF14],
        [0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0],
        [0xF64, 0xF80, 0xF80, 0xF80, 0xF14, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0],
        FlagsOffset: 0xEFB,
        VarsOffset: 0x1028,
        BadgeOffset: 0x100B,
        BoxNamesOffset: 0x8344,
        BoxWallpapersOffset: 0x83C2,
        PcBlockSize: 0x83D0,
        ObjectEventTemplatesOffset: 0x1398,
        UnionRoomOffset: 0x21B8);

    /// <summary>One bag pocket: name, block offset, capacity and per-entry maximum.</summary>
    public readonly record struct Pocket(string Name, int Offset, int Capacity, int MaxCount);

    public static readonly Pocket[] Pockets =
    [
        new("Items", 0x374, 60, 99),
        new("Medicine", 0x2380, 100, 99),
        new("KeyItems", 0x464, 50, 99),
        new("Balls", 0x52C, 32, 99),
        new("TMHMs", 0x5AC, 64, 99),
        new("Berries", 0x6AC, 46, 999),
        new("PCItems", 0x2AC, 50, 999),
    ];

    /// <summary>
    /// True when the bytes carry a supported Recharged Yellow layout: a complete
    /// live slot whose section checksums match one of the builds, plus either a
    /// readable party at SB1+0x44 (and none at retail 0x234) or, on a fresh save,
    /// the build's SaveBlock2 tail markers.
    /// </summary>
    public static bool LooksLike(ReadOnlySpan<byte> data)
    {
        if (data.Length != FileSize)
            return false;
        if (!TryGetLayout(data, out _, out var sections, out var profile))
            return false;

        var sb1 = ReadBlock(data, sections, 1, profile.Sb1Chunks);
        var sb2 = data.Slice(sections[0], Sb2Chunk);

        var count = sb1[PartyCountOffset];
        if (count > 6)
            return false;
        var validHack = 0;
        for (var slot = 0; slot < count; slot++)
        {
            if (IsValidStoredMon(sb1.AsSpan(PartyOffset + (slot * PartyMonSize), PartyMonSize)))
                validHack++;
        }

        // A full party is decisive. The hack's slot 5 starts at the retail 0x238,
        // so the retail-party rejection below must not run first: on a six-mon party
        // that is the hack's own sixth record, not a retail party.
        if (count > 0 && validHack == count)
            return true;

        var vanillaCount = sb1[VanillaPartyCountOffset];
        if (vanillaCount is >= 1 and <= 6)
        {
            for (var slot = 0; slot < vanillaCount; slot++)
            {
                if (IsValidStoredMon(sb1.AsSpan(VanillaPartyOffset + (slot * PartyMonSize), PartyMonSize)))
                    return false;
            }
        }

        // Past this point the evidence is weak (a partial party, or only SaveBlock2 tail
        // markers on a fresh save). A pokeemerald-expansion save with an empty party passes
        // the v1.9.7 windows too (its unused tails are zero) and carries a nonzero clock
        // word at SB2+0xF5C, so this branch claimed a real Lazarus save ahead of the
        // expansion engine. A save the expansion engine can prove is never Recharged Yellow.
        // The weak branches also need the build's own SaveBlock1 shape: ROM script pointers in
        // the relocated object-event templates (or the Union Room phrase block). The real save
        // scores 31; vanilla Emerald, FireRed and every expansion sample score 0 or 5, and the
        // templates are filled as soon as the player stands on a map, so a fresh save has them.
        if (Expansion.ExpansionFormat.LooksLike(data) is not null || StructuralScore(data, sections, profile) < WeakEvidenceScore)
            return false;

        if (validHack > 0)
            return true;

        var clock = BinaryPrimitives.ReadUInt32LittleEndian(sb2[0xF5C..]);
        var language = sb2[0x91];
        return clock != 0 && language != 0 && (language & 0x1F) <= 8;
    }

    /// <summary>
    /// Picks the build and the newer of the two 14-sector slots. A slot qualifies
    /// under a build when every id 0-13 is present, carries the retail signature and
    /// validates over that build's own block chunk. When both builds validate (their
    /// distinguishing bytes are zero on very early saves), a structural score based
    /// on ROM script pointers in the object-event templates and the Union Room text
    /// block chooses the real one.
    /// </summary>
    public static bool TryGetLayout(ReadOnlySpan<byte> data, out int slot, out int[] sections, out Profile profile)
    {
        slot = -1;
        sections = [];
        profile = V19;
        if (data.Length != FileSize)
            return false;

        var maps = new int[SlotCount][];
        var hasMap = new bool[SlotCount];
        for (var candidate = 0; candidate < SlotCount; candidate++)
        {
            var map = new int[SectionCount];
            Array.Fill(map, -1);
            var ok = true;
            for (var index = 0; index < SectionCount && ok; index++)
            {
                var offset = (candidate * SectionCount + index) * SectorSize;
                var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF4)..]);
                if (id >= SectionCount || map[id] >= 0)
                    ok = false;
                else
                    map[id] = offset;
            }
            maps[candidate] = map;
            hasMap[candidate] = ok;
        }

        var bestScore = int.MinValue;
        var found = false;
        foreach (var candidateProfile in new[] { V19, V197 })
        {
            var valid0 = hasMap[0] && SlotValid(data, maps[0], candidateProfile.Windows);
            var valid1 = hasMap[1] && SlotValid(data, maps[1], candidateProfile.Windows);
            if (!valid0 && !valid1)
                continue;
            // The newer slot is always the live one; the structural score only
            // chooses which build's chunking explains the save.
            var candidateSlot = valid0 && valid1
                ? SAV3BlockDetection.CompareFooters(data, maps[0][0], maps[1][0])
                : valid1 ? 1 : 0;
            var score = StructuralScore(data, maps[candidateSlot], candidateProfile)
                + FlagsPlausibility(data, maps[candidateSlot], candidateProfile);
            if (score > bestScore)
            {
                bestScore = score;
                slot = candidateSlot;
                sections = maps[candidateSlot];
                profile = candidateProfile;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Builds the retail-Emerald-shaped image PKHeX edits: the hack's mapped fields
    /// go to Emerald SaveBlock1 offsets, and the PC block is repacked from the
    /// build's chunking into the retail 0xF80-per-sector layout.
    /// </summary>
    public static byte[] BuildVirtual(byte[] payload, int[] sections, Profile profile)
    {
        var image = (byte[])payload.Clone();
        var hackSb1 = ReadBlock(payload, sections, 1, profile.Sb1Chunks);
        var hackPc = ReadBlock(payload, sections, 5, profile.PcChunks);

        var virtualSb1 = new byte[4 * 0xF80];
        Copy(hackSb1, PartyCountOffset, virtualSb1, VanillaPartyCountOffset, 1);
        Copy(hackSb1, PartyOffset, virtualSb1, VanillaPartyOffset, PartySize);
        BinaryPrimitives.WriteUInt32LittleEndian(virtualSb1.AsSpan(VanillaMoneyOffset),
            BinaryPrimitives.ReadUInt32LittleEndian(hackSb1.AsSpan(MoneyOffset)) ^ VirtualSecurityKey);
        BinaryPrimitives.WriteUInt16LittleEndian(virtualSb1.AsSpan(VanillaCoinsOffset),
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(hackSb1.AsSpan(CoinsOffset)) ^ (ushort)(VirtualSecurityKey & 0xFFFF)));
        Copy(hackSb1, RegisteredItemOffset, virtualSb1, VanillaRegisteredItemOffset, 2);
        Copy(hackSb1, profile.FlagsOffset, virtualSb1, VanillaFlagsOffset, FlagsSize);
        Copy(hackSb1, profile.VarsOffset, virtualSb1, VanillaVarsOffset, VarsSize);
        Copy(hackSb1, GameStatsOffset, virtualSb1, VanillaGameStatsOffset, GameStatsSize);
        WriteBlock(image, sections, 1, VanillaSb1Chunks, virtualSb1);

        var virtualPc = new byte[9 * 0xF80];
        Copy(hackPc, 0, virtualPc, 0, 4);
        Copy(hackPc, 4, virtualPc, 4, BoxMonBytes);
        Copy(hackPc, profile.BoxNamesOffset, virtualPc, VanillaBoxNamesOffset, BoxCount * 9);
        Copy(hackPc, profile.BoxWallpapersOffset, virtualPc, VanillaBoxWallpapersOffset, BoxCount);
        WriteBlock(image, sections, 5, VanillaPcChunks, virtualPc);

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sections[0] + VanillaSecurityKeyOffset), VirtualSecurityKey);
        image[sections[0] + VirtualEmeraldMarkerOffset] = 1;
        // PKHeX checksums section 0 over 0xF80. Zeroing the unused tail makes that
        // window hash equal the hack's own 0xF64 window.
        image.AsSpan(sections[0] + Sb2Chunk, 0xF80 - Sb2Chunk).Clear();

        // PKHeX picks the live slot by footer counter, while TryGetLayout picked
        // the slot whose checksums actually validate. If the newest slot is torn,
        // hide it in this scratch image so PKHeX reads the validated half. Footers
        // are never copied back, so the real backup slot is unaffected.
        var activeHalf = sections[0] / (SectionCount * SectorSize);
        var otherHalf = (activeHalf + 1) % SlotCount;
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan((otherHalf * SectionCount * SectorSize) + 0xFF4, 2), ushort.MaxValue);

        WriteChecksums(image, sections, VanillaWindows);
        return image;
    }

    /// <summary>
    /// Writes an edited PKHeX image back into the hack's layout. The result starts
    /// from <paramref name="payload"/> (which carries any bag edits made directly),
    /// overlays the mapped fields and the repacked PC block, then recomputes every
    /// live sector checksum over the build's own chunk sizes.
    /// </summary>
    public static byte[] Materialize(byte[] payload, ReadOnlySpan<byte> image, int[] sections, Profile profile, uint? money, ushort? coins)
    {
        var result = (byte[])payload.Clone();
        var editedSb1 = ReadBlock(image, sections, 1, VanillaSb1Chunks);
        var targetSb1 = ReadBlock(result, sections, 1, profile.Sb1Chunks);

        Copy(editedSb1, VanillaPartyCountOffset, targetSb1, PartyCountOffset, 1);
        Copy(editedSb1, VanillaPartyOffset, targetSb1, PartyOffset, PartySize);
        BinaryPrimitives.WriteUInt32LittleEndian(targetSb1.AsSpan(MoneyOffset),
            BinaryPrimitives.ReadUInt32LittleEndian(editedSb1.AsSpan(VanillaMoneyOffset)) ^ VirtualSecurityKey);
        BinaryPrimitives.WriteUInt16LittleEndian(targetSb1.AsSpan(CoinsOffset),
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(editedSb1.AsSpan(VanillaCoinsOffset)) ^ (ushort)(VirtualSecurityKey & 0xFFFF)));
        Copy(editedSb1, VanillaRegisteredItemOffset, targetSb1, RegisteredItemOffset, 2);
        Copy(editedSb1, VanillaFlagsOffset, targetSb1, profile.FlagsOffset, FlagsSize);
        Copy(editedSb1, VanillaVarsOffset, targetSb1, profile.VarsOffset, VarsSize);
        Copy(editedSb1, VanillaGameStatsOffset, targetSb1, GameStatsOffset, GameStatsSize);

        if (money is { } moneyValue)
            BinaryPrimitives.WriteUInt32LittleEndian(targetSb1.AsSpan(MoneyOffset), moneyValue);
        if (coins is { } coinValue)
            BinaryPrimitives.WriteUInt16LittleEndian(targetSb1.AsSpan(CoinsOffset), coinValue);
        WriteBlock(result, sections, 1, profile.Sb1Chunks, targetSb1);

        var editedPc = ReadBlock(image, sections, 5, VanillaPcChunks);
        var targetPc = ReadBlock(result, sections, 5, profile.PcChunks);
        Copy(editedPc, 0, targetPc, 0, 4);
        Copy(editedPc, 4, targetPc, 4, BoxMonBytes);
        Copy(editedPc, VanillaBoxNamesOffset, targetPc, profile.BoxNamesOffset, BoxCount * 9);
        Copy(editedPc, VanillaBoxWallpapersOffset, targetPc, profile.BoxWallpapersOffset, BoxCount);
        WriteBlock(result, sections, 5, profile.PcChunks, targetPc);

        // Identity, options and Pokédex share the retail SaveBlock2 prefix.
        image.Slice(sections[0], Sb2CopySize).CopyTo(result.AsSpan(sections[0]));

        WriteChecksums(result, sections, profile.Windows);
        return result;
    }

    /// <summary>Reads one block, concatenating each section's own chunk length.</summary>
    public static byte[] ReadBlock(ReadOnlySpan<byte> data, int[] sections, int first, int[] chunks)
    {
        var length = 0;
        foreach (var chunk in chunks)
            length += chunk;
        var block = new byte[length];
        var cursor = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            data.Slice(sections[first + index], chunks[index]).CopyTo(block.AsSpan(cursor));
            cursor += chunks[index];
        }
        return block;
    }

    public static void WriteBlock(byte[] data, int[] sections, int first, int[] chunks, ReadOnlySpan<byte> block)
    {
        var cursor = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            block.Slice(cursor, chunks[index]).CopyTo(data.AsSpan(sections[first + index]));
            cursor += chunks[index];
        }
    }

    public static void WriteChecksums(byte[] data, int[] sections, int[] windows)
    {
        for (var id = 0; id < SectionCount; id++)
        {
            var offset = sections[id];
            var checksum = Checksums.CheckSum32(data.AsSpan(offset, windows[id]));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 0xFF6), checksum);
        }
    }

    /// <summary>True when an 80-byte (box) or 100-byte (party) record decrypts to a known species.</summary>
    public static bool IsValidStoredMon(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < PcMonSize)
            return false;
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var otid = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (pid == 0 && otid == 0)
            return false;

        Span<byte> decrypted = stackalloc byte[48];
        var key = pid ^ otid;
        for (var i = 0; i < 12; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(raw[(0x20 + (i * 4))..]) ^ key;
            BinaryPrimitives.WriteUInt32LittleEndian(decrypted[(i * 4)..], value);
        }
        var sum = 0;
        for (var i = 0; i < 24; i++)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(decrypted[(i * 2)..]);
        if ((ushort)sum != BinaryPrimitives.ReadUInt16LittleEndian(raw[0x1C..]))
            return false;

        var order = SubstructOrder[pid % 24];
        var growth = order.IndexOf('G');
        var species = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(decrypted[(growth * 12)..]) & 0x7FF);
        return species is >= 1 and <= 412;
    }

    private static readonly string[] SubstructOrder =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    private static bool SlotValid(ReadOnlySpan<byte> data, int[] sections, int[] windows)
    {
        for (var id = 0; id < SectionCount; id++)
        {
            var offset = sections[id];
            if (offset < 0)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFF8)..]) != SectorSignature)
                return false;
            if (Checksums.CheckSum32(data.Slice(offset, windows[id])) !=
                BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF6)..]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The flags array is the one relocated region whose physical bytes differ
    /// between the builds, so it is the real tie-breaker when both chunkings
    /// validate. A started save has the starter/intro flags set and game-clear
    /// clear; the other build reads unrelated bytes there.
    /// </summary>
    private static int FlagsPlausibility(ReadOnlySpan<byte> data, int[] sections, Profile profile)
    {
        var sb1 = ReadBlock(data, sections, 1, profile.Sb1Chunks);
        if (profile.FlagsOffset + (0x89E >> 3) + 1 > sb1.Length)
            return 0;

        static bool Get(ReadOnlySpan<byte> block, int flags, int flag) =>
            (block[flags + (flag >> 3)] & (1 << (flag & 7))) != 0;

        var started = Get(sb1, profile.FlagsOffset, 0x861) || Get(sb1, profile.FlagsOffset, 0x89E);
        var cleared = Get(sb1, profile.FlagsOffset, 0x864);
        return started && !cleared ? 20 : 0;
    }

    internal static int StructuralScore(ReadOnlySpan<byte> data, int[] sections, Profile profile)
    {
        var sb1 = ReadBlock(data, sections, 1, profile.Sb1Chunks);
        var score = 0;
        // Object event templates carry ROM script pointers; the wrong chunking
        // reads zeros or unrelated data here.
        for (var index = 0; index < 16; index++)
        {
            var offset = profile.ObjectEventTemplatesOffset + (index * 0x18) + 0x10;
            if (offset + 4 > sb1.Length)
                break;
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(sb1.AsSpan(offset));
            if (pointer is >= 0x0800_0000 and < 0x0A00_0000)
                score += 2;
        }
        // The Union Room default phrase block spells HELLO in the Gen 3 charset.
        ReadOnlySpan<byte> hello = [0xC2, 0xBF, 0xC6, 0xC6, 0xC9];
        if (profile.UnionRoomOffset + hello.Length <= sb1.Length &&
            sb1.AsSpan(profile.UnionRoomOffset).IndexOf(hello) >= 0)
            score += 5;
        return score;
    }

    private static void Copy(ReadOnlySpan<byte> source, int sourceOffset, Span<byte> destination, int destinationOffset, int length) =>
        source.Slice(sourceOffset, length).CopyTo(destination.Slice(destinationOffset, length));
}
