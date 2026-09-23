using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PKForge.Infrastructure;

/// <summary>
/// PKSM's generation tag, stored as a little-endian u32 at the head of every bank entry.
/// The numbering is PKSM-Core's <c>Generation::GenerationEnum</c> (include/enums/Generation.hpp),
/// which is NOT chronological: Gen IV was the first format PKSM supported, so it is 0.
/// </summary>
public enum PksmGeneration : uint
{
    Four = 0,
    Five = 1,
    Six = 2,
    Seven = 3,
    LetsGo = 4,
    Eight = 5,
    Three = 6,
    One = 7,
    Two = 8,
    Nine = 9,
    /// <summary>An empty slot: PKSM fills the whole entry (tag included) with 0xFF.</summary>
    Unused = 0xFFFFFFFF,
}

/// <summary>Why a byte buffer is not a PKSM bank - PKSM's own <c>BankFile::Error</c> set.</summary>
public enum PksmBankError
{
    /// <summary>Fewer bytes than the version's header needs.</summary>
    TooSmall,
    /// <summary>No "PKSMBANK" magic and not a pre-2019 <c>bank.bin</c> either.</summary>
    BadMagic,
    /// <summary>Written by a newer PKSM than this reader knows; refused rather than mangled.</summary>
    NewerVersion,
    /// <summary>Version 0 or another value PKSM never shipped.</summary>
    UnknownVersion,
    /// <summary>Box count of zero, or beyond PKSM's 500-box ceiling.</summary>
    BadBoxCount,
}

/// <summary>One occupied or empty bank slot, exactly as the file held it.</summary>
/// <param name="Data">The 0x148-byte payload (decrypted PKSM-Core <c>rawData</c>, 0xFF padded).</param>
public sealed record PksmBankSlot(int Box, int Slot, PksmGeneration Generation, byte[] Data)
{
    public bool IsEmpty => Generation == PksmGeneration.Unused;
}

/// <summary>A parsed bank, always widened to the current (v3) entry layout.</summary>
/// <param name="SourceVersion">The version found in the file: 1, 2 or 3; 0 for a legacy <c>bank.bin</c>.</param>
/// <param name="Truncated">The header claimed more boxes than the file held; missing slots read as empty.</param>
public sealed record PksmBankContents(int SourceVersion, int Boxes, IReadOnlyList<PksmBankSlot> Slots, bool Truncated)
{
    public bool IsLegacyBankBin => SourceVersion == 0;
}

/// <summary>Parse outcome: contents or a typed error, never an exception for bad input.</summary>
public sealed record PksmBankParseResult(PksmBankContents? Contents, PksmBankError? Error)
{
    public bool Success => Contents is not null;
}

/// <summary>
/// The PKSM bank file format (<c>/3ds/PKSM/banks/&lt;name&gt;.bnk</c>) and its box-name sidecar
/// (<c>&lt;name&gt;.json</c>), byte for byte as FlagBrew/PKSM writes them. Pure: bytes in, bytes out.
/// <para>
/// Source of truth: PKSM <c>common/include/BankFile.hpp</c> + <c>common/source/BankFile.cpp</c>
/// (parse and migration), <c>3ds/source/Bank.cpp</c> (entry encode/decode, JSON names, the
/// pre-bank <c>bank.bin</c> conversion), PKSM-Core <c>include/enums/Generation.hpp</c> (tags).
/// </para>
/// <list type="bullet">
/// <item>v3 (current): 16-byte header <c>"PKSMBANK" | u32 version=3 | u32 boxes</c>, then
/// <c>boxes × 30</c> entries of 0x150 bytes: <c>u32 generation | u8[0x148] data | u8[4] padding</c>.</item>
/// <item>v2: same header, 264-byte entries (<c>u32 generation | u8[260] data</c>).</item>
/// <item>v1: 12-byte header (no box count), 264-byte entries; boxes = body / 264 / 30.</item>
/// <item>Legacy <c>/3ds/PKSM/bank/bank.bin</c>: no header, a flat run of 232-byte Gen VI/VII
/// box structures, 30 per box (PKSM guesses PK6 vs PK7 per mon on conversion).</item>
/// </list>
/// All integers are little-endian (the 3DS is). Empty slots are 0xFF throughout.
/// </summary>
public static class PksmBankFile
{
    public const string Magic = "PKSMBANK";
    public const int CurrentVersion = 3;
    public const int SlotsPerBox = 30;
    public const int MaxBoxes = 500;
    /// <summary>PKSM's <c>BANK_DEFAULT_SIZE</c> - the size a bank gets when banks.json names none.</summary>
    public const int DefaultBoxes = 50;
    public const int HeaderSize = 16;
    public const int V1HeaderSize = 12;
    public const int EntrySize = 0x150;
    public const int EntryDataSize = 0x148;
    public const int OldEntrySize = 264;
    /// <summary>One Gen VI/VII box structure - the unit of the legacy <c>bank.bin</c>.</summary>
    public const int LegacyEntrySize = 232;

    // PKSM-Core pkx/PK1.hpp and PK2.hpp: the single-mon "list" layouts with names attached
    // (1 count + 2 species/terminator + party struct + OT + nickname). Identical to PKHeX's
    // SIZE_1JLIST / SIZE_1ULIST / SIZE_2JLIST / SIZE_2ULIST.
    public const int Pk1JapaneseLength = 59;
    public const int Pk1InternationalLength = 69;
    public const int Pk2JapaneseLength = 63;
    public const int Pk2InternationalLength = 73;

    public static PksmBankParseResult Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < V1HeaderSize)
            return LegacyOr(bytes, PksmBankError.TooSmall);
        if (!bytes[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            return LegacyOr(bytes, PksmBankError.BadMagic);

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[Magic.Length..]);
        int headerSize, entrySize;
        switch (version)
        {
            case 1: headerSize = V1HeaderSize; entrySize = OldEntrySize; break;
            case 2: headerSize = HeaderSize; entrySize = OldEntrySize; break;
            case CurrentVersion: headerSize = HeaderSize; entrySize = EntrySize; break;
            default:
                return new(null, version > CurrentVersion ? PksmBankError.NewerVersion : PksmBankError.UnknownVersion);
        }
        if (bytes.Length < headerSize)
            return new(null, PksmBankError.TooSmall);

        var body = bytes[headerSize..];
        // Wider than u32 so no header claim can wrap the slot arithmetic (as PKSM does).
        ulong boxes = version == 1
            ? (ulong)(body.Length / entrySize / SlotsPerBox)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(Magic.Length + 4)..]);
        if (boxes is 0 or > MaxBoxes)
            return new(null, PksmBankError.BadBoxCount);

        var slotCount = (int)boxes * SlotsPerBox;
        // The header's box count is a claim; the file's length is the fact.
        var readable = Math.Min(slotCount, body.Length / entrySize);
        var slots = new PksmBankSlot[slotCount];
        for (var i = 0; i < slotCount; i++)
        {
            if (i >= readable)
            {
                slots[i] = EmptySlot(i);
                continue;
            }
            var entry = body.Slice(i * entrySize, entrySize);
            var generation = (PksmGeneration)BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var data = new byte[EntryDataSize];
            data.AsSpan().Fill(0xFF); // a migrated (shorter) entry keeps the empty fill in its tail
            entry.Slice(4, Math.Min(EntryDataSize, entrySize - 4)).CopyTo(data);
            slots[i] = new PksmBankSlot(i / SlotsPerBox, i % SlotsPerBox, generation, data);
        }
        return new(new PksmBankContents((int)version, (int)boxes, slots, readable < slotCount), null);
    }

    /// <summary>
    /// The pre-bank <c>bank.bin</c> (PKSM before 6.0): accepted only when its length is an exact
    /// whole number of 30 × 232-byte boxes, the same test <c>Bank::convertFromBankBin</c> applies.
    /// Its slots carry <see cref="PksmGeneration.Six"/>; PKSM re-tags suspected Gen VII mons on
    /// conversion, which the entity decoder repeats from the bytes themselves.
    /// </summary>
    private static PksmBankParseResult LegacyOr(ReadOnlySpan<byte> bytes, PksmBankError error)
    {
        const int boxBytes = LegacyEntrySize * SlotsPerBox;
        if (bytes.Length == 0 || bytes.Length % boxBytes != 0 || bytes.Length / boxBytes > MaxBoxes)
            return new(null, error);
        var count = bytes.Length / LegacyEntrySize;
        var slots = new PksmBankSlot[count];
        for (var i = 0; i < count; i++)
        {
            var raw = bytes.Slice(i * LegacyEntrySize, LegacyEntrySize);
            // An all-zero or all-0xFF structure is an empty slot (PKSM reads it as species None).
            if (!raw.ContainsAnyExcept((byte)0) || !raw.ContainsAnyExcept((byte)0xFF))
            {
                slots[i] = EmptySlot(i);
                continue;
            }
            var data = new byte[EntryDataSize];
            data.AsSpan().Fill(0xFF);
            raw.CopyTo(data);
            slots[i] = new PksmBankSlot(i / SlotsPerBox, i % SlotsPerBox, PksmGeneration.Six, data);
        }
        return new(new PksmBankContents(0, count / SlotsPerBox, slots, false), null);
    }

    private static PksmBankSlot EmptySlot(int index)
    {
        var data = new byte[EntryDataSize];
        data.AsSpan().Fill(0xFF);
        return new PksmBankSlot(index / SlotsPerBox, index % SlotsPerBox, PksmGeneration.Unused, data);
    }

    /// <summary>
    /// Writes a current-version bank of <paramref name="boxes"/> boxes. Slots not supplied are
    /// empty (all 0xFF, as <c>BankFile::emptyEntry</c>); payloads shorter than 0x148 are 0xFF padded
    /// exactly as <c>Bank::pkm(const PKX&amp;, …)</c> pads them; the 4 padding bytes are 0xFF too.
    /// </summary>
    public static byte[] Write(int boxes, IEnumerable<(int Box, int Slot, PksmGeneration Generation, ReadOnlyMemory<byte> Data)> entries)
    {
        if (boxes is < 1 or > MaxBoxes)
            throw new ArgumentOutOfRangeException(nameof(boxes), $"A PKSM bank holds 1 to {MaxBoxes} boxes.");
        var output = new byte[HeaderSize + boxes * SlotsPerBox * EntrySize];
        Encoding.ASCII.GetBytes(Magic).CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)boxes);
        output.AsSpan(HeaderSize).Fill(0xFF);
        var used = new HashSet<(int, int)>();
        foreach (var (box, slot, generation, data) in entries)
        {
            if (box < 0 || box >= boxes || slot is < 0 or >= SlotsPerBox)
                throw new ArgumentOutOfRangeException(nameof(entries), $"Box {box + 1} slot {slot + 1} is outside a {boxes}-box bank.");
            if (!used.Add((box, slot)))
                throw new InvalidOperationException($"Box {box + 1} slot {slot + 1} was written twice.");
            if (data.Length > EntryDataSize)
                throw new ArgumentException($"A {data.Length}-byte payload does not fit a PKSM bank entry.", nameof(entries));
            var entry = output.AsSpan(HeaderSize + (box * SlotsPerBox + slot) * EntrySize, EntrySize);
            if (generation == PksmGeneration.Unused) continue; // already the empty fill
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)generation);
            data.Span.CopyTo(entry[4..]);
        }
        return output;
    }

    /// <summary>
    /// The mon bytes inside a slot payload, cut to the length PKSM-Core's constructor for that
    /// generation reads (<c>Bank::pkm(int, int)</c>). Gen I/II pick Japanese vs international by
    /// the byte that ends a Japanese record (0x50 terminator or 0x00), exactly as PKSM does.
    /// Null for an empty slot or a tag PKSM's bank never decodes (Gen IX, unknown values).
    /// </summary>
    public static byte[]? EntityBytes(PksmGeneration generation, ReadOnlySpan<byte> data)
    {
        int length;
        switch (generation)
        {
            case PksmGeneration.One:
                length = data[Pk1JapaneseLength - 1] is 0x50 or 0 ? Pk1JapaneseLength : Pk1InternationalLength;
                break;
            case PksmGeneration.Two:
                length = data[Pk2JapaneseLength - 1] is 0x50 or 0 ? Pk2JapaneseLength : Pk2InternationalLength;
                break;
            case PksmGeneration.Three: length = 80; break;
            case PksmGeneration.Four or PksmGeneration.Five: length = 136; break;
            case PksmGeneration.Six or PksmGeneration.Seven or PksmGeneration.LetsGo: length = 232; break;
            case PksmGeneration.Eight: length = EntryDataSize; break;
            default: return null;
        }
        return data[..length].ToArray();
    }

    /// <summary>The chronological generation number for display (LGPE counts as 7).</summary>
    public static int GenerationNumber(PksmGeneration generation) => generation switch
    {
        PksmGeneration.One => 1,
        PksmGeneration.Two => 2,
        PksmGeneration.Three => 3,
        PksmGeneration.Four => 4,
        PksmGeneration.Five => 5,
        PksmGeneration.Six => 6,
        PksmGeneration.Seven or PksmGeneration.LetsGo => 7,
        PksmGeneration.Eight => 8,
        PksmGeneration.Nine => 9,
        _ => 0,
    };

    /// <summary>The tag for a chronological generation (+ Let's Go flag); null where PKSM has none.</summary>
    public static PksmGeneration? FromGenerationNumber(int generation, bool letsGo = false) => generation switch
    {
        7 when letsGo => PksmGeneration.LetsGo,
        1 => PksmGeneration.One,
        2 => PksmGeneration.Two,
        3 => PksmGeneration.Three,
        4 => PksmGeneration.Four,
        5 => PksmGeneration.Five,
        6 => PksmGeneration.Six,
        7 => PksmGeneration.Seven,
        8 => PksmGeneration.Eight,
        _ => null,
    };

    public static string Label(PksmGeneration generation) => generation switch
    {
        PksmGeneration.LetsGo => "LGPE",
        PksmGeneration.Unused => "empty",
        _ when GenerationNumber(generation) > 0 => $"Gen {GenerationNumber(generation)}",
        _ => $"tag 0x{(uint)generation:X}",
    };

    // ── The box-name sidecar ────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>&lt;name&gt;.json</c>: a JSON array of box-name strings (nlohmann <c>dump(2)</c>).
    /// PKSM writes one trailing NUL after the text, which is stripped. Unreadable JSON yields
    /// null (PKSM then regenerates names); non-string items become empty.
    /// </summary>
    public static IReadOnlyList<string>? ParseBoxNames(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            return document.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "")
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the sidecar the way PKSM does: 2-space indented array plus one trailing NUL.</summary>
    public static byte[] WriteBoxNames(IReadOnlyList<string> names)
    {
        var options = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartArray();
            foreach (var name in names) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    /// <summary>PKSM's default box name ("Storage N", from i18n STORAGE in English).</summary>
    public static string DefaultBoxName(int box) => $"Storage {box + 1}";
}
