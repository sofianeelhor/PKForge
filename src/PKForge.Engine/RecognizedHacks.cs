using System.Buffers.Binary;

namespace PKForge.Engine;

/// <summary>
/// ROM hacks PKForge recognizes from their bytes but has no editor for. Each one is named
/// for what it is, never shown as the retail game its envelope copies ("Emerald",
/// "FireRed") and never as an anonymous "unsupported layout": a save the stock parser can
/// read opens read-only under the hack's name (see <see cref="WriteSafety"/>), and one it
/// cannot read is refused with that name. Every test here is structural and cheap, runs
/// only after <see cref="SaveParser.HackRoute"/> found no engine for the bytes, and is
/// proven on real saves by the detection matrix (no other save matches it).
/// </summary>
internal static class RecognizedHacks
{
    /// <summary>A recognized hack: its display name and why PKForge cannot edit it yet.</summary>
    internal sealed record Hack(string Name, string Why);

    private const int GbaSize = 0x20_000;
    private const int SectorSize = 0x1000;
    private const int Sections = 14;
    private const uint RetailSignature = 0x0801_2025;
    private const int FullChunk = 0xFF4;
    private const int RetailChunk = 0xF80;

    private static readonly int[] CfruWindows = [0xF24, 0xFF0, 0xFF0, 0xFF0, 0xD98, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0x450];
    private static readonly int[] FrlgWindows = [0xF24, 0xF80, 0xF80, 0xF80, 0xEE8, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0];

    /// <summary>The hack these bytes (a decoded save file) belong to, or null.</summary>
    public static Hack? Identify(ReadOnlySpan<byte> decoded)
    {
        // A save the Origin HeartGold engine reads never gets here (HackRoute claims it first):
        // this is the hack's footers with no block copy whose CRC holds.
        if (OriginHeartGold(decoded) && !SaveParser.IsPokemonOriginHeartGold(decoded))
            return new("Origin HeartGold", "neither copy of its save blocks passes its checksum");

        if (decoded.Length < GbaSize)
            return null;
        var payload = SramPadding.Trim(decoded.ToArray());
        if (payload.Length < GbaSize)
            return null;
        var data = payload.AsSpan(0, GbaSize);

        // An intact Elite Redux save has its own engine (SaveParser.IsPokemonEliteRedux); only
        // one it cannot prove (a torn sector, an unknown record) is named and refused here.
        if (SingleSlot(data, 20) && !EliteRedux.EliteReduxFormat.LooksLike(data))
            return new("Elite Redux", "its save slot does not check out (a damaged sector, or Pokémon records PKForge's Elite Redux tables do not know)");
        if (BlackPearl2(data))
            return new("Black Pearl Emerald", "it uses Black Pearl's own save format (version 2.1 and later), whose Pokémon PKForge cannot read yet");

        if (TwoSlot(data) is not { } live)
            return null;
        if (Expansion.ExpansionFormat.LooksLike(data) is null && Expansion.ExpansionFormat.CarriesHeartAndSoulMagic(data))
            return new("Heart and Soul", "this build lays its save out differently from the 14-box Heart and Soul saves PKForge reads");
        // Fire Red Extended and Crystal Advance Redux are no longer here: their own engine reads
        // them (FireRedHacks.FireRedHackFormat), and SaveParser.HackRoute claims them first.
        if (Rowe(data, live))
            return new("R.O.W.E.", "it keeps 21 boxes and stores its Pokémon in a format of its own");
        if (Exceeded(data, live) && !PKForge.Engine.Exceeded.ExceededFormat.Matches(data))
            return new("Emerald Exceeded", "it keeps 13 boxes and stores its Pokémon in a format of its own");
        if (Expansion.ExpansionFormat.LooksLike(data) is null && FireEmerald(data, live))
            return new("Fire Emerald", "its PC storage is laid out differently from every pokeemerald-expansion save PKForge reads");
        if (Expansion.ExpansionFormat.LooksLikeGrownSaveBlock1(data))
            return new("Black Pearl Emerald", "its SaveBlock1 is larger than standard pokeemerald-expansion's, so PKForge does not know where its fields moved");
        return null;
    }

    /// <summary>The message shown when the stock parser cannot open a recognized hack.</summary>
    internal static string RefusalMessage(Hack hack) =>
        $"This is a Pokémon {hack.Name} save. PKForge recognizes {hack.Name} but cannot open or edit it yet: {hack.Why}. The file was not touched.";

    /// <summary>The layout-risk reason for a recognized hack the stock parser can show.</summary>
    internal static string ReadOnlyReason(Hack hack) =>
        $"this is a Pokémon {hack.Name} save. PKForge recognizes {hack.Name} but cannot edit it yet: {hack.Why}. " +
        "It opens read-only, and writing it with a retail layout would corrupt it.";

    // ── Envelopes ──

    /// <summary>Live physical offset of logical ids 0-13 in a retail-style two-slot envelope,
    /// or null when an id is missing or a slot sector carries an id past 13 (one-slot hacks).</summary>
    internal static int[]? TwoSlot(ReadOnlySpan<byte> data)
    {
        var live = new int[Sections];
        var counters = new uint[Sections];
        Array.Fill(live, -1);
        for (var sector = 0; sector < 28; sector++)
        {
            var footer = data[(sector * SectorSize + 0xFF4)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != RetailSignature)
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
            if (id >= Sections)
            {
                if (id < 0x100) return null;
                continue;
            }
            var counter = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
            if (live[id] < 0 || counter >= counters[id])
            {
                live[id] = sector * SectorSize;
                counters[id] = counter;
            }
        }
        return live.Any(offset => offset < 0) ? null : live;
    }

    /// <summary>Physical sectors 0..n-1 carry ids 0..n-1 exactly once (rotation allowed)
    /// with one save counter, and the slot sectors after them are unstamped.</summary>
    private static bool SingleSlot(ReadOnlySpan<byte> data, int count)
    {
        var seen = new bool[count];
        uint? counter = null;
        for (var sector = 0; sector < 28; sector++)
        {
            var footer = data[(sector * SectorSize + 0xFF4)..];
            var stamped = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) == RetailSignature;
            if (sector >= count)
            {
                if (stamped) return false;
                continue;
            }
            var id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
            if (!stamped || id >= count || seen[id] || (counter is { } c && c != value))
                return false;
            seen[id] = true;
            counter = value;
        }
        return true;
    }

    /// <summary>Black Pearl Emerald 2.1+: "BPE2" at 0xFF8 and a sector kind (1 progress,
    /// 2 box) at 0xFF4 on at least one full progress copy's worth of sectors.</summary>
    private static bool BlackPearl2(ReadOnlySpan<byte> data)
    {
        var stamps = 0;
        for (var sector = 0; sector < 32; sector++)
        {
            var offset = sector * SectorSize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFF8)..]) == 0x3245_5042 && data[offset + 0xFF4] is 1 or 2)
                stamps++;
        }
        return stamps >= 5;
    }

    /// <summary>Origin HeartGold (a Chinese HGSS binary hack): both HGSS footers in a slot
    /// carry the hack's block sizes, general 0xF7CC and 24-box storage 0x18408.</summary>
    private static bool OriginHeartGold(ReadOnlySpan<byte> data)
    {
        if (data.Length != 0x80000)
            return false;
        foreach (var slot in new[] { 0, 0x40000 })
            if (NdsFooter(data, slot, 0xF7CC) && NdsFooter(data, slot + 0xF800, 0x18408))
                return true;
        return false;

        static bool NdsFooter(ReadOnlySpan<byte> data, int offset, int size) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + size - 8)..]) == 0x2006_0623
            && BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + size - 12)..]) == size;
    }

    // ── Two-slot hacks on the retail envelope ──

    /// <summary>Fire Red Extended 3.5.x: CFRU checksum windows (some retail FRLG window
    /// fails), FireRed's game code in SaveBlock2, and vanilla-ENCRYPTED PK3 records (Radical
    /// Red keeps them plaintext). With no party, the first stored box records decide.</summary>
    internal static bool FireRedExtended(ReadOnlySpan<byte> data, int[] live)
    {
        if (!AllWindows(data, live, CfruWindows) || AllWindows(data, live, FrlgWindows))
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[(live[0] + 0xAC)..]) != 1)
            return false;
        var party = live[1];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(party + 0x34)..]);
        if (count > 6)
            return false;
        if (count == 0)
        {
            // No party: the stored boxes decide, read as the CFRU stream FRE writes.
            var stream = new byte[8 * 0xFF0 + 0x450];
            for (var id = 5; id <= 13; id++)
                data.Slice(live[id], CfruWindows[id]).CopyTo(stream.AsSpan((id - 5) * 0xFF0));
            return StoredRecordsAreEncryptedPk3(stream, 4);
        }
        var checkedRecords = 0;
        for (var i = 0; i < (int)count; i++)
        {
            var span = data.Slice(party + 0x38 + i * 100, 80);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) == BinaryPrimitives.ReadUInt32LittleEndian(span[4..]))
                continue; // PID == OTID: no encryption key, so the two readings cannot differ
            if (!Pk3Valid(span, encrypted: true) || Pk3Valid(span, encrypted: false))
                return false;
            checkedRecords++;
        }
        return checkedRecords > 0;
    }

    /// <summary>Crystal Advance Redux: every live sector checksummed over a flat 0xF80
    /// (retail FireRed uses shorter windows on some), FireRed's game code, and a party of
    /// plaintext records with a zero checksum, a retail species and retail moves.</summary>
    internal static bool CrystalAdvanceRedux(ReadOnlySpan<byte> data, int[] live)
    {
        var flat = Enumerable.Repeat(RetailChunk, Sections).ToArray();
        if (!AllWindows(data, live, flat) || AllWindows(data, live, FrlgWindows))
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[(live[0] + 0xAC)..]) != 1)
            return false;
        var party = live[1];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(party + 0x34)..]);
        if (count is 0 or > 6)
            return false;
        for (var i = 0; i < (int)count; i++)
        {
            var record = data.Slice(party + 0x38 + i * 100, 80);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[0x1C..]) != 0 || Pk3Valid(record, encrypted: true))
                return false;
            var species = BinaryPrimitives.ReadUInt16LittleEndian(record[0x20..]);
            var firstMove = BinaryPrimitives.ReadUInt16LittleEndian(record[0x2C..]);
            if (species is 0 or > 411 || firstMove is 0 or > 354)
                return false;
            for (var move = 1; move < 4; move++)
                if (BinaryPrimitives.ReadUInt16LittleEndian(record[(0x2C + move * 2)..]) > 354)
                    return false;
        }
        return true;
    }

    /// <summary>R.O.W.E.: 4084-byte windows with 0x130 on id 13 (21 boxes of 52-byte
    /// records), a SaveBlock1 sector that needs the longer window, 21 box names at
    /// storage+0x7FFC, and a party that is not PK3.</summary>
    private static bool Rowe(ReadOnlySpan<byte> data, int[] live) =>
        FullChunkHack(data, live, lastWindow: 0x130, namesOffset: 0x7FFC, boxes: 21);

    /// <summary>Emerald Exceeded (both samples share it): 4084-byte windows with 0x6F8 on
    /// id 13, 13 box names at storage+0x8614, and a party that is not PK3.</summary>
    private static bool Exceeded(ReadOnlySpan<byte> data, int[] live) =>
        FullChunkHack(data, live, lastWindow: 0x6F8, namesOffset: 0x8614, boxes: 13);

    /// <summary>The shared shape of the 4084-byte-chunk hacks with a custom mon format. The
    /// SaveBlock2 and last SaveBlock1 sectors (ids 0 and 4) are not checked: their real
    /// block sizes are unknown, and the storage windows and box-name run decide.</summary>
    private static bool FullChunkHack(ReadOnlySpan<byte> data, int[] live, int lastWindow, int namesOffset, int boxes)
    {
        for (var id = 1; id < Sections; id++)
            if (id != 4 && !Window(data, live[id], id == 13 ? lastWindow : FullChunk))
                return false;
        var longer = false;
        for (var id = 1; id <= 3; id++)
            longer |= !Window(data, live[id], RetailChunk);
        if (!longer)
            return false;
        if (!Expansion.ExpansionFormat.HasBoxNames(Storage(data, live, FullChunk), namesOffset, boxes))
            return false;
        var party = live[1];
        var count = data[party + 0x234];
        if (count > 6)
            return false;
        for (var i = 0; i < count; i++)
            if (Pk3Valid(data.Slice(party + 0x238 + i * 100, 80), encrypted: true))
                return false;
        return true;
    }

    /// <summary>Fire Emerald: a 3968-byte envelope whose 14 box names sit 0xA0 past the
    /// standard storage's (and not at the standard offset), with valid PK3 party records.</summary>
    private static bool FireEmerald(ReadOnlySpan<byte> data, int[] live)
    {
        var storage = Storage(data, live, RetailChunk);
        if (!Expansion.ExpansionFormat.HasBoxNames(storage, 0x83E4, 14) || Expansion.ExpansionFormat.HasBoxNames(storage, 0x8344, 14))
            return false;
        var saveBlock1 = Block(data, live, 1, 4, RetailChunk);
        var count = saveBlock1[0x234];
        if (count > 6)
            return false;
        for (var i = 0; i < count; i++)
            if (!Pk3Valid(saveBlock1.AsSpan(0x238 + i * 100, 80), encrypted: true))
                return false;
        return true;
    }

    // ── Helpers ──

    /// <summary>
    /// The first one or two non-empty records of the 14 FireRed boxes in a box stream (80
    /// bytes each after the currentBox word) are vanilla PK3s: checksum-valid once decrypted
    /// and unshuffled, and, when PID ≠ OTID gives them a key, NOT also valid as plaintext.
    /// That is Fire Red Extended's storage, never Radical Red's 58-byte plaintext records
    /// (each misaligned 80-byte read passes the checksum 1 time in 65536). FRE's own boxed
    /// mons carry PID == OTID (no key), so the checksum alone speaks for those. False when
    /// the boxes are empty: a fresh save cannot be told apart from another CFRU hack.
    /// </summary>
    internal static bool StoredRecordsAreEncryptedPk3(ReadOnlySpan<byte> stream, int start)
    {
        const int boxRecords = 14 * 30;
        var checkedRecords = 0;
        for (var index = 0; index < boxRecords && checkedRecords < 2; index++)
        {
            var offset = start + index * 80;
            if (offset + 80 > stream.Length)
                break;
            var record = stream.Slice(offset, 80);
            if (record.IndexOfAnyExcept((byte)0) < 0)
                continue;
            var keyed = BinaryPrimitives.ReadUInt32LittleEndian(record) != BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            if (!Pk3Valid(record, encrypted: true) || (keyed && Pk3Valid(record, encrypted: false)))
                return false;
            checkedRecords++;
        }
        return checkedRecords > 0;
    }

    private static bool AllWindows(ReadOnlySpan<byte> data, int[] live, int[] windows)
    {
        for (var id = 0; id < Sections; id++)
            if (!Window(data, live[id], windows[id]))
                return false;
        return true;
    }

    private static bool Window(ReadOnlySpan<byte> data, int sector, int length) =>
        Expansion.ExpansionFormat.Checksum(data.Slice(sector, length), length) == BinaryPrimitives.ReadUInt16LittleEndian(data[(sector + 0xFF6)..]);

    private static byte[] Block(ReadOnlySpan<byte> data, int[] live, int first, int count, int chunk)
    {
        var block = new byte[count * chunk];
        for (var i = 0; i < count; i++)
            data.Slice(live[first + i], chunk).CopyTo(block.AsSpan(i * chunk));
        return block;
    }

    private static byte[] Storage(ReadOnlySpan<byte> data, int[] live, int chunk) => Block(data, live, 5, 9, chunk);

    private static readonly string[] SubstructOrders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    /// <summary>A Gen 3 80-byte record whose 48-byte data area (decrypted with PID ^ OTID
    /// when <paramref name="encrypted"/>, unshuffled by PID % 24) sums to its checksum,
    /// with a non-zero species. All-zero records never count.</summary>
    internal static bool Pk3Valid(ReadOnlySpan<byte> record, bool encrypted)
    {
        if (record.IndexOfAnyExcept((byte)0) < 0)
            return false;
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var key = encrypted ? pid ^ BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) : 0u;
        Span<byte> area = stackalloc byte[48];
        for (var i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(area[i..], BinaryPrimitives.ReadUInt32LittleEndian(record[(0x20 + i)..]) ^ key);
        var order = SubstructOrders[pid % 24];
        var growth = order.IndexOf('G') * 12;
        if (BinaryPrimitives.ReadUInt16LittleEndian(area[growth..]) == 0)
            return false;
        ushort sum = 0;
        for (var i = 0; i < 48; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(area[i..]);
        return sum == BinaryPrimitives.ReadUInt16LittleEndian(record[0x1C..]);
    }
}
