using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Bank bytes are stored raw, and same-size formats (PK8/PB8 0x148, PK6/PK7 0xE8/0x104,
/// PK9/PA9 0x148) cannot be told apart by content alone. These tests prove the context-free
/// read misreads them, and that the recorded <see cref="BankEntryInfo.Format"/> makes every
/// bank path (deposit, reopen, facts, PKSM, transfer, migration) read the exact type.
/// </summary>
public sealed class BankEntityFormatTests : IDisposable
{
    private const ushort Pikachu = 25;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-bankfmt-" + Guid.NewGuid().ToString("N"));
    private readonly SaveEngine _engine = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A plain mon in a blank save of its own game (the format that game stores).</summary>
    private static SaveEngineSession Native(GameVersion version, ushort species = Pikachu)
    {
        var save = BlankSaveFile.Get(version, "Sof", LanguageID.English);
        var mon = save.BlankPKM;
        mon.Species = species;
        mon.CurrentLevel = 30;
        mon.Version = version;
        mon.OriginalTrainerName = "Sof";
        mon.Language = (int)LanguageID.English;
        mon.PID = 0x12345678;
        mon.EncryptionConstant = 0x9ABCDEF0;
        mon.Move1 = 33; // Tackle
        mon.MetLocation = 30; // a captured mon (PKHeX's Gen 8/9 detection keys on met data)
        mon.MetLevel = 30;
        if (mon is IObedienceLevel obedience) obedience.ObedienceLevel = 30;
        mon.Nickname = SpeciesName.GetSpeciesNameGeneration(species, mon.Language, mon.Format);
        mon.RefreshChecksum();
        save.SetBoxSlotAtIndex(mon, 0, 0, EntityImportSettings.None);
        var session = new SaveEngineSession(save, null);
        Assert.Equal(mon.GetType(), session.GetEntity(0, 0).GetType());
        return session;
    }

    public static TheoryData<GameVersion, string> Formats => new()
    {
        { GameVersion.BD, nameof(PB8) },
        { GameVersion.SW, nameof(PK8) },
        { GameVersion.PLA, nameof(PA8) },
        { GameVersion.AS, nameof(PK6) },
        { GameVersion.UM, nameof(PK7) },
        { GameVersion.VL, nameof(PK9) },
        { GameVersion.ZA, nameof(PA9) },
    };

    [Fact]
    public void ContextFreeReadMisreadsTheSameSizeSiblings()
    {
        // The bug, reproduced: this is how bank bytes used to be reopened.
        var pb8 = Native(GameVersion.BD).ExportSlot(0, 0);
        Assert.IsType<PK8>(EntityFormat.GetFromBytes(pb8.Data));
        var pk6 = Native(GameVersion.AS).ExportSlot(0, 0);
        Assert.IsType<PK7>(EntityFormat.GetFromBytes(pk6.Data));

        // And the old deposit recorded the misread's generation: a PK6 banked as Gen 7.
        var legacy = EntityFormat.GetFromBytes(pk6.Data)!;
        Assert.Equal(7, legacy.Format);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void DepositAndReopenKeepTheExactTypeAndData(GameVersion version, string format)
    {
        var source = Native(version);
        var original = source.GetEntity(0, 0);
        var export = source.ExportSlot(0, 0);
        Assert.Equal(format, export.Format);

        var bank = new FileBankService(Path.Combine(_root, format));
        var info = _engine.TryDescribeEntity(export.Data, "test", export.Format)!;
        Assert.Equal(format, info.Format);
        Assert.Equal(original.Format, info.Generation);
        var entry = bank.Add(export.Data, info);

        // Reload the index from disk: the format survives.
        var reloaded = new FileBankService(Path.Combine(_root, format)).GetAll().Single();
        Assert.Equal(format, reloaded.Info.Format);

        var bytes = bank.GetData(entry.Id);
        var parsed = EntityBytes.Parse(reloaded, bytes)!;
        Assert.Equal(original.GetType(), parsed.GetType());
        Assert.True(original.Data.SequenceEqual(parsed.Data));

        using var reopened = (SaveEngineSession)_engine.OpenEntitySession(bytes, "test", reloaded.Info.Format)!;
        var edited = reopened.GetEntity(0, 0);
        Assert.Equal(original.GetType(), edited.GetType());
        Assert.Equal(original.Species, edited.Species);
        Assert.Equal(original.PID, edited.PID);
        Assert.Equal(original.EncryptionConstant, edited.EncryptionConstant);
        Assert.Equal(original.Move1, edited.Move1);

        // An edit exported from the entity session is re-described as the same format.
        var roundTrip = reopened.ExportSlot(0, 0);
        Assert.Equal(format, _engine.TryDescribeEntity(roundTrip.Data, "test", roundTrip.Format)!.Format);
    }

    [Fact]
    public void RecordedFormatWinsOverTheHeuristicsAndFileExtensionsNormalize()
    {
        var pb8 = Native(GameVersion.BD).ExportSlot(0, 0).Data;
        Assert.IsType<PB8>(EntityBytes.Parse(pb8, "PB8"));
        Assert.IsType<PB8>(EntityBytes.Parse(pb8, "Pikachu.pb8"));
        Assert.IsType<PK8>(EntityBytes.Parse(pb8, "Pikachu.pk8"));
        Assert.IsType<PK8>(EntityBytes.Parse(pb8, null)); // no evidence: PKHeX's default
        Assert.IsType<PB8>(EntityBytes.Parse(pb8, null, EntityContext.Gen8b));
        Assert.Equal("PB8", EntityBytes.Normalize(".PB8"));
        Assert.Null(EntityBytes.Normalize("mon.pk"));
        // A recorded format that cannot hold the bytes' length is not forced on them.
        var pk3 = Native(GameVersion.E).ExportSlot(0, 0).Data;
        Assert.IsType<PK3>(EntityBytes.Parse(pk3, "PB8"));
    }

    // ── Migration of an index written before BankEntryInfo.Format existed ──

    private string WriteLegacyBank(params (Guid Id, byte[] Bytes, int Species, int Generation, string Source)[] entries)
    {
        var dir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(dir);
        var rows = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var (id, bytes, species, generation, source) = entries[i];
            File.WriteAllBytes(Path.Combine(dir, id.ToString("N") + ".bin"), bytes);
            // The exact shape of a pre-Format index row: no "Format" key at all.
            rows.Add($$"""{"Id":"{{id}}","Box":0,"Slot":{{i}},"Info":{"Species":{{species}},"Form":0,"Shiny":false,"Nickname":"n","Level":30,"Generation":{{generation}},"SourceName":"{{source}}"},"AddedUtc":"2026-01-01T00:00:00+00:00"}""");
        }
        File.WriteAllText(Path.Combine(dir, "index.json"), $$"""{"BoxCount":3,"Entries":[{{string.Join(",", rows)}}]}""");
        return dir;
    }

    [Fact]
    public void LegacyIndexLoadsAndMigratesOnlyWhatItCanProve()
    {
        var pk6 = Native(GameVersion.AS).ExportSlot(0, 0).Data;
        var pk6Legacy = Native(GameVersion.Y, 26).ExportSlot(0, 0).Data; // banked as "Gen 7" by the old misread
        var pk7 = Native(GameVersion.UM, 803).ExportSlot(0, 0).Data;     // Poipole: provably PK7
        var pb8Named = Native(GameVersion.BD).ExportSlot(0, 0).Data;
        var pb8Unnamed = Native(GameVersion.SP, 26).ExportSlot(0, 0).Data;
        var pk8 = Native(GameVersion.SW, 810).ExportSlot(0, 0).Data;     // Grookey: provably PK8
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        // A PK7 of Gen 6 origin (moved up by Poké Transporter): the one PK7 PKHeX cannot tell from a PK6.
        var moved = Native(GameVersion.UM).GetEntity(0, 0);
        moved.Version = GameVersion.AS;
        moved.RefreshChecksum();
        var pk7Ambiguous = new byte[moved.SIZE_PARTY];
        moved.WriteDecryptedDataParty(pk7Ambiguous);
        Assert.Equal(2, EntityBytes.Candidates(pk7Ambiguous).Count);
        var dir = WriteLegacyBank(
            (ids[0], pk6, Pikachu, 6, "PKSM · Gen 6"),
            (ids[1], pk6Legacy, 26, 7, "Pokémon Y"),
            (ids[2], pk7, 803, 7, "whatever"),
            (ids[3], pb8Named, Pikachu, 8, "Pokémon Brilliant Diamond"),
            (ids[4], pb8Unnamed, 26, 8, "my save"),
            (ids[5], pk8, 810, 8, "my save"),
            (ids[6], pk7Ambiguous, Pikachu, 7, "my save"));

        var bank = new FileBankService(dir);
        Assert.All(bank.GetAll(), e => Assert.Null(e.Info.Format)); // old index loads, field optional

        var (migrated, unresolved) = EntityBytes.MigrateFormats(bank);
        Assert.Equal(5, migrated);
        Assert.Equal(2, unresolved);

        var byId = new FileBankService(dir).GetAll().ToDictionary(e => e.Id); // persisted
        Assert.Equal("PK6", byId[ids[0]].Info.Format);           // recorded Gen 6 proves PK6
        Assert.Equal("PK6", byId[ids[1]].Info.Format);           // source names Y
        Assert.Equal(6, byId[ids[1]].Info.Generation);           // generation corrected from the misread 7
        Assert.Equal("PK7", byId[ids[2]].Info.Format);           // only one reading possible
        Assert.Equal("PB8", byId[ids[3]].Info.Format);           // source names Brilliant Diamond
        Assert.Null(byId[ids[4]].Info.Format);                   // PB8 or PK8, no evidence: no guess
        Assert.Equal("PK8", byId[ids[5]].Info.Format);           // Grookey cannot be PB8
        Assert.Null(byId[ids[6]].Info.Format);                   // PK6 or PK7, recorded 7 was only the default

        Assert.IsType<PB8>(EntityBytes.Parse(byId[ids[3]], pb8Named));
        Assert.IsType<PK6>(EntityBytes.Parse(byId[ids[1]], pk6Legacy));

        // Idempotent: a second run touches nothing.
        Assert.Equal((0, 2), EntityBytes.MigrateFormats(new FileBankService(dir)));
    }

    [Fact]
    public void MigrateBankRunsOnceAndMarksUnresolvedEntriesChecked()
    {
        var pk6 = Native(GameVersion.AS).ExportSlot(0, 0).Data;
        var pb8Unnamed = Native(GameVersion.SP, 26).ExportSlot(0, 0).Data;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dir = WriteLegacyBank((ids[0], pk6, Pikachu, 6, "PKSM · Gen 6"), (ids[1], pb8Unnamed, 26, 8, "my save"));

        Assert.True(EntityBytes.MigrateBank(new FileBankService(dir)) > 0);

        var bank = new FileBankService(dir);
        Assert.Equal(EntityBytes.BankMigrationVersion, bank.MigrationVersion);
        var byId = bank.GetAll().ToDictionary(e => e.Id);
        Assert.Equal("PK6", byId[ids[0]].Info.Format);
        Assert.NotNull(byId[ids[0]].Info.Traits);
        Assert.Null(byId[ids[1]].Info.Format); // no proof: stays null, but is not retried
        File.Delete(Path.Combine(dir, ids[1].ToString("N") + ".bin"));
        Assert.Equal(0, EntityBytes.MigrateBank(bank)); // nothing is read again on the next launch
    }

    // ── PKSM ──

    private static byte[] Stored(PKM pk)
    {
        var data = new byte[pk.SIZE_STORED];
        pk.WriteDecryptedDataStored(data);
        return data;
    }

    [Fact]
    public void PksmImportRecordsTheFormatItsTagNames()
    {
        var pk6 = PksmEntityConversion.Decode(6, false, Stored(Native(GameVersion.AS).GetEntity(0, 0)), "PKSM bank");
        Assert.Null(pk6.Reason); // formerly refused: the PK6 would have been read back as PK7
        Assert.Equal("PK6", pk6.Info!.Format);
        Assert.IsType<PK6>(EntityBytes.Parse(pk6.Bytes!, pk6.Info.Format));

        var pk7 = PksmEntityConversion.Decode(7, false, Stored(Native(GameVersion.UM).GetEntity(0, 0)), "PKSM bank");
        Assert.Equal("PK7", pk7.Info!.Format);

        // PKSM's EIGHT tag is Sword/Shield only (PKSM-Core has no BDSP class).
        var pk8 = PksmEntityConversion.Decode(8, false, Stored(Native(GameVersion.SW).GetEntity(0, 0)), "PKSM bank");
        Assert.Equal("PK8", pk8.Info!.Format);

        // A loose dump: the extension names the format.
        var loose = PksmEntityConversion.Decode(null, false, Native(GameVersion.BD).ExportSlot(0, 0).Data, "PKSM dump Pikachu.pb8");
        Assert.Equal("PB8", loose.Info!.Format);
    }

    [Fact]
    public void PksmExportRefusesABankPb8InsteadOfFilingItAsSwordShield()
    {
        var pb8 = Native(GameVersion.BD).ExportSlot(0, 0).Data;
        Assert.Equal(8, PksmEntityConversion.Encode(pb8).Generation); // the bug: context-free → PK8
        var refused = PksmEntityConversion.Encode(pb8, "PB8");
        Assert.Null(refused.Data);
        Assert.Contains("PB8", refused.Reason);

        var pk6 = Native(GameVersion.AS).ExportSlot(0, 0).Data;
        Assert.Equal(6, PksmEntityConversion.Encode(pk6, "PK6").Generation);
        Assert.Equal(7, PksmEntityConversion.Encode(pk6).Generation); // the bug again
    }

    // ── Transfer from the bank ──

    [Fact]
    public void BankPb8TransfersIntoABdspSaveAsPb8()
    {
        var export = Native(GameVersion.BD).ExportSlot(0, 0);
        var bank = new FileBankService(Path.Combine(_root, "transfer"));
        var entry = bank.Add(export.Data, _engine.TryDescribeEntity(export.Data, "BD", export.Format)!);
        var bytes = bank.GetData(entry.Id);

        using var target = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SP, "Lyra", LanguageID.English), null);
        var preview = new TransferPreviewService().Preview(target, 0, 1, bytes, entry.Info.Format);
        Assert.NotNull(preview);
        Assert.False(preview!.Backwards);
        Assert.DoesNotContain(preview.Changes, c => c.Contains("PK8", StringComparison.Ordinal));

        using var landing = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SP, "Lyra", LanguageID.English), null);
        var conversion = landing.ImportSlotWithReport(0, 1, bytes, out var refusal, entry.Info.Format);
        Assert.True(conversion is not null, refusal);
        var landed = Assert.IsType<PB8>(landing.GetEntity(0, 1));
        Assert.Equal(Pikachu, landed.Species);
        Assert.Equal(0x12345678u, landed.PID);
        Assert.True(EntityBytes.Parse(bytes, "PB8")!.Data.SequenceEqual(landed.Data));
    }

    [Fact]
    public void BankPb8IntoSwordShieldIsConvertedFromPb8NotReadAsPk8()
    {
        var export = Native(GameVersion.BD).ExportSlot(0, 0);
        using var swsh = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SW, "Hop", LanguageID.English), null);
        var conversion = swsh.ImportSlotWithReport(0, 1, export.Data, out var refusal, export.Format);
        Assert.True(conversion is not null, refusal);
        var landed = Assert.IsType<PK8>(swsh.GetEntity(0, 1));
        Assert.Equal(Pikachu, landed.Species);
        Assert.Equal(0x12345678u, landed.PID);
    }
}
