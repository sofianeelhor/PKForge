using System.Buffers.Binary;
using System.Security.Cryptography;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Read-only My Pokémon Ranch boundary. The pinned SAV4Ranch constructor trusts
/// file offsets, and BulkStorage.ChecksumsValid does not check the Ranch SHA-1.
/// Layout and record sizes come from that pinned reader and its Ranch substructures.
/// Original-revision object-array framing is cross-checked with ignored USA DP
/// fixtures; RanchTests also exercises synthetic arrays and hostile offsets.
/// </summary>
internal static class RanchFormat
{
    // RK4 stored/party size in the pinned PKHeX reader; includes ownership metadata.
    private const int RecordSize = 164;
    internal const string ReadOnlyReason = "My Pokémon Ranch is read-only. Copy Pokémon to the Bank or export them; deposits, edits and withdrawals are not supported.";

    internal static bool IsCandidate(int length) => length is SaveUtil.SIZE_G4RANCH or SaveUtil.SIZE_G4RANCH_PLAT;

    internal static string? WriteRestriction(ReadOnlySpan<byte> data) =>
        IsCandidate(data.Length) || RetroArchSaveContainer.IsCompressed(data) && IsCandidate(RetroArchSaveContainer.Decode(data).Length)
            ? ReadOnlyReason : null;

    internal static void Validate(ReadOnlySpan<byte> data)
    {
        if (data.Length != SaveUtil.SIZE_G4RANCH)
            throw new InvalidDataException("This My Pokémon Ranch revision is not supported. Only the original Diamond/Pearl Ranch format has been verified.");
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(data[20..], digest);
        if (!CryptographicOperations.FixedTimeEquals(data[..20], digest))
            throw new InvalidDataException("My Pokémon Ranch save checksum failed. Choose an intact save or its backup.");

        if (U32(data, 0x14) != 0x00030005)
            throw new InvalidDataException("Unsupported My Pokémon Ranch header.");
        Span<int> offsets = stackalloc int[5];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (U32(data, 0x18 + i * 8) != (uint)i)
                throw new InvalidDataException("Invalid My Pokémon Ranch block table.");
            uint offset = U32(data, 0x1C + i * 8);
            if (offset > (uint)(data.Length - 8) || offset < 0x40 || (i > 0 && offset <= offsets[i - 1]))
                throw new InvalidDataException("My Pokémon Ranch block offset is outside its data.");
            offsets[i] = (int)offset;
        }
        if (offsets[0] != 0x40 || U32(data, offsets[0]) != 0x2260 || U32(data, offsets[0] + 4) != 1 ||
            offsets[1] != offsets[0] + 8 + 0x2260)
            throw new InvalidDataException("Unsupported My Pokémon Ranch metadata layout.");

        int level = data[0x5A] + 1, planned = data[0x5B] + 1;
        if (level is < 1 or > 25 || planned is < 1 or > 25)
            throw new InvalidDataException("Invalid My Pokémon Ranch level.");
        int capacity = RanchLevel.GetSlotCount(level);
        CheckArray(data, offsets[1], offsets[2], RanchMii.SIZE, RanchLevel.GetMaxMiiCount(level));
        CheckArray(data, offsets[2], offsets[3], RanchTrainerMii.SIZE, RanchLevel.GetMaxMiiCount(level));
        CheckArray(data, offsets[3], offsets[4], RecordSize, capacity);
        if (U32(data, offsets[4]) != RanchMii.SIZE || U32(data, offsets[4] + 4) != 0 ||
            (long)offsets[3] + 8 + (long)capacity * RecordSize > data.Length)
            throw new InvalidDataException("Invalid My Pokémon Ranch storage extent or end marker.");

        int records = (int)U32(data, offsets[3] + 4);
        for (int i = 0; i < records; i++)
        {
            byte owner = data[offsets[3] + 8 + i * RecordSize + 0x88];
            if (owner is not (0 or 1 or 4 or 5))
                throw new InvalidDataException("Unrecognized My Pokémon Ranch ownership type.");
        }
    }

    private static void CheckArray(ReadOnlySpan<byte> data, int start, int end, int size, int maximum)
    {
        uint count = U32(data, start + 4);
        if (U32(data, start) != size || count > maximum || (long)start + 8 + (long)count * size != end)
            throw new InvalidDataException("Invalid My Pokémon Ranch array size or count.");
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
