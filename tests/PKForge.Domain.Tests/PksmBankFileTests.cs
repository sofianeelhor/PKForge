using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The PKSM bank format against fixtures built by hand from PKSM's own layout
/// (common/include/BankFile.hpp, common/source/BankFile.cpp, 3ds/source/Bank.cpp), plus the
/// import/export plumbing over a real on-disk bank. Entity conversion is faked here: the
/// engine side (PKHeX) is exercised by the engine, not by the byte format.
/// </summary>
public sealed class PksmBankFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture builders: bytes laid out exactly as PKSM writes them ──────────

    private static byte[] Header(uint version, uint? boxes)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("PKSMBANK"));
        bytes.AddRange(BitConverter.GetBytes(version)); // little-endian host, like the 3DS
        if (boxes is { } b) bytes.AddRange(BitConverter.GetBytes(b));
        return bytes.ToArray();
    }

    /// <summary>One entry: u32 tag, payload, then 0xFF to the entry's size (Bank::pkm pads with 0xFF).</summary>
    private static byte[] Entry(int entrySize, PksmGeneration generation, byte[]? payload = null)
    {
        var entry = new byte[entrySize];
        entry.AsSpan().Fill(0xFF);
        if (generation == PksmGeneration.Unused) return entry;
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)generation);
        payload?.CopyTo(entry, 4);
        return entry;
    }

    private static byte[] Payload(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    /// <summary>A Gen I international list record: its Japanese-length end byte is neither 0x50 nor 0.</summary>
    private static byte[] Gen1International()
    {
        var bytes = Payload(PksmBankFile.Pk1InternationalLength, 1);
        bytes[PksmBankFile.Pk1JapaneseLength - 1] = 0x81;
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] EmptyEntries(int count, int entrySize) =>
        Concat(Enumerable.Range(0, count).Select(_ => Entry(entrySize, PksmGeneration.Unused)).ToArray());

    // ── Parse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_a_v3_bank_built_to_PKSMs_layout()
    {
        var gen4 = Payload(136, 10);
        var gen8 = Payload(PksmBankFile.EntryDataSize, 20);
        var bank = Concat(
            Header(3, 2),
            Entry(PksmBankFile.EntrySize, PksmGeneration.Four, gen4),
            Entry(PksmBankFile.EntrySize, PksmGeneration.Unused),
            Entry(PksmBankFile.EntrySize, PksmGeneration.Eight, gen8),
            EmptyEntries(57, PksmBankFile.EntrySize));
        Assert.Equal(16 + 60 * 0x150, bank.Length);

        var parsed = PksmBankFile.Parse(bank);

        var contents = Assert.IsType<PksmBankContents>(parsed.Contents);
        Assert.Equal(3, contents.SourceVersion);
        Assert.Equal(2, contents.Boxes);
        Assert.False(contents.Truncated);
        Assert.Equal(60, contents.Slots.Count);
        Assert.Equal(PksmGeneration.Four, contents.Slots[0].Generation);
        Assert.Equal(gen4, PksmBankFile.EntityBytes(contents.Slots[0].Generation, contents.Slots[0].Data));
        Assert.True(contents.Slots[1].IsEmpty);
        Assert.Equal(gen8, contents.Slots[2].Data);
        Assert.All(contents.Slots.Skip(3), s => Assert.True(s.IsEmpty));
    }

    [Fact]
    public void Generation_tags_follow_PKSM_Core_numbering()
    {
        // include/enums/Generation.hpp: FOUR=0 … EIGHT=5, THREE=6, ONE=7, TWO=8, NINE=9.
        Assert.Equal(0u, (uint)PksmGeneration.Four);
        Assert.Equal(4u, (uint)PksmGeneration.LetsGo);
        Assert.Equal(6u, (uint)PksmGeneration.Three);
        Assert.Equal(7u, (uint)PksmGeneration.One);
        Assert.Equal(8u, (uint)PksmGeneration.Two);
        Assert.Equal(0xFFFFFFFFu, (uint)PksmGeneration.Unused);
        Assert.Equal(PksmGeneration.LetsGo, PksmBankFile.FromGenerationNumber(7, letsGo: true));
        Assert.Null(PksmBankFile.FromGenerationNumber(9));
    }

    [Fact]
    public void Migrates_v2_banks_with_264_byte_entries()
    {
        var gen6 = Payload(232, 3);
        var bank = Concat(Header(2, 1), Entry(PksmBankFile.OldEntrySize, PksmGeneration.Six, gen6), EmptyEntries(29, PksmBankFile.OldEntrySize));

        var contents = PksmBankFile.Parse(bank).Contents!;

        Assert.Equal(2, contents.SourceVersion);
        Assert.Equal(1, contents.Boxes);
        Assert.Equal(gen6, PksmBankFile.EntityBytes(PksmGeneration.Six, contents.Slots[0].Data));
        // The widened tail keeps the empty fill.
        Assert.All(contents.Slots[0].Data.Skip(260), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Migrates_v1_banks_whose_size_is_the_box_count()
    {
        var gen5 = Payload(136, 7);
        var bank = Concat(Header(1, null), Entry(PksmBankFile.OldEntrySize, PksmGeneration.Five, gen5), EmptyEntries(59, PksmBankFile.OldEntrySize));

        var contents = PksmBankFile.Parse(bank).Contents!;

        Assert.Equal(1, contents.SourceVersion);
        Assert.Equal(2, contents.Boxes);
        Assert.Equal(PksmGeneration.Five, contents.Slots[0].Generation);
    }

    [Theory]
    [InlineData(4u, PksmBankError.NewerVersion)]
    [InlineData(0u, PksmBankError.UnknownVersion)]
    public void Refuses_versions_it_has_no_migration_for(uint version, PksmBankError expected)
    {
        var parsed = PksmBankFile.Parse(Concat(Header(version, 1), EmptyEntries(30, PksmBankFile.EntrySize)));
        Assert.False(parsed.Success);
        Assert.Equal(expected, parsed.Error);
    }

    [Fact]
    public void Refuses_bad_magic_short_files_and_bad_box_counts()
    {
        Assert.Equal(PksmBankError.TooSmall, PksmBankFile.Parse(Encoding.ASCII.GetBytes("PKSMBA")).Error);
        Assert.Equal(PksmBankError.TooSmall, PksmBankFile.Parse(Header(3, null)).Error); // v3 needs 16 bytes
        var notBank = Concat(Encoding.ASCII.GetBytes("NOTABANK"), Header(3, 1)[8..], EmptyEntries(30, PksmBankFile.EntrySize));
        Assert.Equal(PksmBankError.BadMagic, PksmBankFile.Parse(notBank).Error);
        Assert.Equal(PksmBankError.BadBoxCount, PksmBankFile.Parse(Header(3, 0)).Error);
        Assert.Equal(PksmBankError.BadBoxCount, PksmBankFile.Parse(Header(3, 501)).Error);
        Assert.Equal(PksmBankError.BadBoxCount, PksmBankFile.Parse(Header(1, null)).Error); // v1 with no body = zero boxes
    }

    [Fact]
    public void A_truncated_bank_keeps_what_it_holds_and_reads_the_rest_as_empty()
    {
        var gen7 = Payload(232, 9);
        // Header claims 3 boxes (90 slots) but the file stops half-way through slot 32.
        var bank = Concat(Header(3, 3), EmptyEntries(31, PksmBankFile.EntrySize),
            Entry(PksmBankFile.EntrySize, PksmGeneration.Seven, gen7), new byte[100]);

        var contents = PksmBankFile.Parse(bank).Contents!;

        Assert.True(contents.Truncated);
        Assert.Equal(3, contents.Boxes);
        Assert.Equal(90, contents.Slots.Count);
        Assert.Equal(PksmGeneration.Seven, contents.Slots[31].Generation);
        Assert.Equal((1, 1), (contents.Slots[31].Box, contents.Slots[31].Slot));
        Assert.All(contents.Slots.Skip(32), s => Assert.True(s.IsEmpty));
    }

    [Fact]
    public void Reads_the_legacy_bank_bin_as_flat_232_byte_boxes()
    {
        var legacy = new byte[232 * 30];
        Payload(232, 5).CopyTo(legacy, 232 * 4);

        var contents = PksmBankFile.Parse(legacy).Contents!;

        Assert.True(contents.IsLegacyBankBin);
        Assert.Equal(1, contents.Boxes);
        Assert.Single(contents.Slots, s => !s.IsEmpty);
        Assert.Equal(4, contents.Slots.Single(s => !s.IsEmpty).Slot);
        Assert.Equal(PksmBankError.BadMagic, PksmBankFile.Parse(new byte[232 * 30 + 1]).Error);
    }

    [Fact]
    public void Gen1_and_Gen2_pick_Japanese_or_international_length_like_PKSM()
    {
        var japanese = Payload(PksmBankFile.EntryDataSize, 1);
        japanese[PksmBankFile.Pk1JapaneseLength - 1] = 0x50;
        Assert.Equal(59, PksmBankFile.EntityBytes(PksmGeneration.One, japanese)!.Length);
        var international = Payload(PksmBankFile.EntryDataSize, 1);
        international[PksmBankFile.Pk1JapaneseLength - 1] = 0x81;
        Assert.Equal(69, PksmBankFile.EntityBytes(PksmGeneration.One, international)!.Length);
        var gen2 = Payload(PksmBankFile.EntryDataSize, 1);
        gen2[PksmBankFile.Pk2JapaneseLength - 1] = 0;
        Assert.Equal(63, PksmBankFile.EntityBytes(PksmGeneration.Two, gen2)!.Length);
        Assert.Null(PksmBankFile.EntityBytes(PksmGeneration.Nine, gen2));
    }

    // ── Write ────────────────────────────────────────────────────────────────

    [Fact]
    public void Writes_byte_identical_banks_and_round_trips_every_generation()
    {
        var gen1 = Gen1International();
        var gen3 = Payload(80, 30);
        var gen4 = Payload(136, 40);
        var gen7 = Payload(232, 50);
        var lgpe = Payload(232, 60);
        var gen8 = Payload(PksmBankFile.EntryDataSize, 70);
        (int, int, PksmGeneration, ReadOnlyMemory<byte>)[] entries =
        [
            (0, 0, PksmGeneration.One, gen1),
            (0, 29, PksmGeneration.Three, gen3),
            (1, 0, PksmGeneration.Four, gen4),
            (1, 5, PksmGeneration.Seven, gen7),
            (1, 6, PksmGeneration.LetsGo, lgpe),
            (1, 29, PksmGeneration.Eight, gen8),
        ];

        var written = PksmBankFile.Write(2, entries);

        // Against a hand-built expectation of PKSM's own layout.
        var expected = new byte[16 + 60 * 0x150];
        expected.AsSpan(16).Fill(0xFF);
        Header(3, 2).CopyTo(expected, 0);
        foreach (var (box, slot, generation, data) in entries)
            Entry(0x150, generation, data.ToArray()).CopyTo(expected, 16 + (box * 30 + slot) * 0x150);
        Assert.Equal(expected, written);

        var contents = PksmBankFile.Parse(written).Contents!;
        foreach (var (box, slot, generation, data) in entries)
        {
            var parsed = contents.Slots[box * 30 + slot];
            Assert.Equal(generation, parsed.Generation);
            Assert.Equal(data.ToArray(), PksmBankFile.EntityBytes(generation, parsed.Data));
        }
        Assert.Equal(written, PksmBankFile.Write(2, contents.Slots.Select(s => (s.Box, s.Slot, s.Generation, (ReadOnlyMemory<byte>)s.Data))));
    }

    [Fact]
    public void Write_refuses_what_PKSM_could_not_read()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PksmBankFile.Write(0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => PksmBankFile.Write(501, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => PksmBankFile.Write(1, [(1, 0, PksmGeneration.Four, new byte[136])]));
        Assert.Throws<ArgumentException>(() => PksmBankFile.Write(1, [(0, 0, PksmGeneration.Nine, new byte[0x158])]));
        Assert.Throws<InvalidOperationException>(() => PksmBankFile.Write(1,
            [(0, 0, PksmGeneration.Four, new byte[136]), (0, 0, PksmGeneration.Five, new byte[136])]));
    }

    [Fact]
    public void Box_names_json_round_trips_with_PKSMs_trailing_nul()
    {
        var pksmWritten = Encoding.UTF8.GetBytes("[\n  \"Storage 1\",\n  \"Shinies ★\"\n]\0");
        Assert.Equal(["Storage 1", "Shinies ★"], PksmBankFile.ParseBoxNames(pksmWritten)!);

        var written = PksmBankFile.WriteBoxNames(["Team", "Shinies ★"]);
        Assert.Equal(0, written[^1]);
        Assert.Equal("[\n  \"Team\",\n  \"Shinies ★\"\n]", Encoding.UTF8.GetString(written, 0, written.Length - 1).Replace("\r\n", "\n"));
        Assert.Equal(["Team", "Shinies ★"], PksmBankFile.ParseBoxNames(written)!);
        Assert.Null(PksmBankFile.ParseBoxNames("{not json"u8));
    }

    // ── Import / export over a real bank ──────────────────────────────────────

    /// <summary>Stand-in for the engine: any payload whose first byte is 0 is "corrupt".</summary>
    private static PksmDecoded FakeDecode(PksmGeneration? generation, byte[] bytes, string source) =>
        bytes[0] == 0
            ? PksmDecoded.Skip("checksum mismatch (corrupt slot)")
            : new PksmDecoded(Concat([(byte)(generation ?? PksmGeneration.Unused)], bytes),
                new BankEntryInfo(bytes[0], 0, false, $"mon{bytes[0]}", 5, PksmBankFile.GenerationNumber(generation ?? PksmGeneration.Four), source), null);

    /// <summary>Inverse of <see cref="FakeDecode"/>: the leading byte carries the PKSM tag.</summary>
    private static PksmEncoded FakeEncode(byte[] bankBytes, string? format) =>
        (PksmGeneration)bankBytes[0] == PksmGeneration.Nine
            ? PksmEncoded.Skip("PKSM banks have no slot for PK9")
            : new PksmEncoded((PksmGeneration)bankBytes[0], bankBytes[1..], null);

    private static byte[] SampleBank()
    {
        var corrupt = Payload(136, 0);
        return PksmBankFile.Write(3,
        [
            (0, 0, PksmGeneration.Four, Payload(136, 11)),
            (0, 1, PksmGeneration.Three, Payload(80, 12)),
            (0, 2, PksmGeneration.Four, corrupt),
            (2, 7, PksmGeneration.Eight, Payload(PksmBankFile.EntryDataSize, 13)),
            (2, 8, PksmGeneration.Nine, Payload(200, 14)),
        ]);
    }

    [Fact]
    public void Plan_counts_generations_and_explains_every_skip()
    {
        var plan = PksmBankTransfer.PlanBank("pksm_1", SampleBank(), PksmBankFile.WriteBoxNames(["Main", "", "Swsh"]), FakeDecode);

        Assert.Equal(3, plan.ReadyCount);
        Assert.Equal(2, plan.SkippedCount);
        Assert.Equal([("Gen 3", 1), ("Gen 4", 1), ("Gen 8", 1)], plan.CountsByGeneration);
        Assert.Contains(plan.SkipReasons, r => r.Reason.Contains("checksum"));
        Assert.Contains(plan.SkipReasons, r => r.Reason.Contains("Gen 9"));
        Assert.Equal(["Main", "Storage 2", "Swsh"], plan.BoxNames);
        Assert.Equal(3, plan.UsedBoxes);
        Assert.Contains("PKSM bank pksm_1 · Swsh slot 8", plan.Items.Single(i => i.Box == 2 && i.Ready).Info!.SourceName);

        var bad = Assert.Throws<InvalidDataException>(() => PksmBankTransfer.PlanBank("x", "PKSMBANK\x04\0\0\0"u8, default, FakeDecode));
        Assert.Contains("newer PKSM", bad.Message);
    }

    [Fact]
    public void New_box_import_keeps_layout_and_names_and_skips_duplicates()
    {
        var bank = new FileBankService(_root);
        var existing = bank.Add([1, 2, 3], new BankEntryInfo(25, 0, false, "Pika", 5, 7, "test"));
        var names = new BankBoxNames(_root);
        var plan = PksmBankTransfer.PlanBank("pksm_1", SampleBank(), PksmBankFile.WriteBoxNames(["Main", "Two", "Swsh"]), FakeDecode);

        var result = PksmBankTransfer.Apply(bank, names, [plan], PksmImportMode.NewBoxes);

        Assert.Equal(3, result.Imported);
        Assert.Equal(1, result.FirstNewBox); // after the bank's last used box
        Assert.Equal(3, result.BoxesUsed);
        var imported = bank.GetAll().Where(e => e.Id != existing.Id).ToArray();
        Assert.Equal([(1, 0), (1, 1), (3, 7)], imported.Select(e => (e.Box, e.Slot)).Order().ToArray());
        Assert.Equal("Main", names.Get(1));
        Assert.Equal("Swsh", names.Get(3));
        Assert.Equal("Swsh", new BankBoxNames(_root).Get(3)); // persisted

        var again = PksmBankTransfer.Apply(bank, names, [plan], PksmImportMode.Merge);
        Assert.Equal(0, again.Imported);
        Assert.Equal(3, again.Duplicates);
    }

    [Fact]
    public void Export_round_trips_through_import_and_reports_what_PKSM_cannot_hold()
    {
        var source = new FileBankService(Path.Combine(_root, "a"));
        var plan = PksmBankTransfer.PlanBank("pksm_1", SampleBank(), default, FakeDecode);
        PksmBankTransfer.Apply(source, null, [plan], PksmImportMode.NewBoxes);
        source.Add([(byte)PksmGeneration.Nine, 99], new BankEntryInfo(906, 0, false, "Sprigatito", 5, 9, "sv"));
        var names = new BankBoxNames(Path.Combine(_root, "a"));
        names.SetMany([(2, "Swsh")]);

        var export = PksmBankTransfer.Export(source, source.GetAll(), FakeEncode, names.Get, PksmExportLayout.KeepPositions);

        Assert.Equal(3, export.Written);
        var skipped = Assert.Single(export.Skipped);
        Assert.Equal(906, skipped.Entry.Info.Species);
        Assert.Contains("PK9", skipped.Reason);
        Assert.Equal(3, export.Boxes);
        Assert.Equal(["Storage 1", "Storage 2", "Swsh"], PksmBankFile.ParseBoxNames(export.BoxNames)!);

        // What PKSM would load: the same mons in the same slots, with the same payloads.
        var original = PksmBankFile.Parse(SampleBank()).Contents!.Slots;
        var exported = PksmBankFile.Parse(export.Bank).Contents!.Slots;
        foreach (var slot in original.Where(s => s.Generation is PksmGeneration.Three or PksmGeneration.Eight
                     || (s.Generation == PksmGeneration.Four && s.Data[0] != 0)))
        {
            var match = exported[slot.Box * 30 + slot.Slot];
            Assert.Equal(slot.Generation, match.Generation);
            Assert.Equal(PksmBankFile.EntityBytes(slot.Generation, slot.Data), PksmBankFile.EntityBytes(match.Generation, match.Data));
        }

        var packed = PksmBankTransfer.Export(source, source.GetAll(), FakeEncode, names.Get, PksmExportLayout.Pack);
        Assert.Equal(1, packed.Boxes);
        Assert.Equal(3, PksmBankFile.Parse(packed.Bank).Contents!.Slots.Take(3).Count(s => !s.IsEmpty));
    }

    [Fact]
    public void Collect_pairs_banks_with_names_and_opens_dump_zips()
    {
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
            Add("dumps/2026-9-23/12-0-0 - 25 - PIKACHU - 1234ABCD.pk7", Payload(232, 1));
            Add("dumps/2026-9-23/12-0-1 - 133 - EEVEE - 0000BEEF.pb7", Payload(232, 2));
            Add("readme.txt", [1]);
        }

        var sources = PksmBankTransfer.Collect(
        [
            ("pksm_1.bnk", SampleBank()),
            ("pksm_1.json", PksmBankFile.WriteBoxNames(["A"])),
            ("dumps.zip", zipBytes.ToArray()),
            ("banks.json", "{\"pksm_1\": 50}"u8.ToArray()),
        ]);

        var bank = Assert.Single(sources.Banks);
        Assert.Equal("pksm_1", bank.Name);
        Assert.NotNull(bank.BoxNames);
        Assert.Equal(2, sources.LooseFiles.Count);
        Assert.Contains("readme.txt", sources.Ignored);
        Assert.Contains("banks.json", sources.Ignored);

        var loose = PksmBankTransfer.PlanLoose("dumps", sources.LooseFiles, FakeDecode);
        Assert.Equal(["Gen 7", "LGPE"], loose.Items.Select(i => i.Label).Order().ToArray());
        Assert.Equal(PksmGeneration.Seven, PksmBankTransfer.GenerationFromFileName("x.PK7"));
        Assert.Null(PksmBankTransfer.GenerationFromFileName("x.pk9"));
    }
}
