using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Radical Red session against the owner's real champion save: party decode, the
/// 58-byte PC stream across sections 5-13, the raw sector-30/31 boxes, dex bitmaps,
/// checksum policies, and byte-identical serialization, all verified on ground truth
/// rather than fixtures.
/// </summary>
public sealed class RadicalRedSessionTests
{
    private static string? GroundTruth()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "radicalred-champ.sav");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static RadicalRedEngineSession? OpenGroundTruth()
    {
        var path = GroundTruth();
        if (path is null) return null; // gitignored ground truth: dev-only, skipped on CI
        return new RadicalRedEngineSession(File.ReadAllBytes(path), "Radical Red");
    }

    [Fact]
    public void PartyDecodesFromGroundTruth()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Equal(3, session.Generation);
        Assert.Equal(22, session.BoxCount);
        Assert.Equal(30, session.BoxSlotCount);
        Assert.Equal(1375, session.MaxSpeciesId);

        var terapagos = session.ReadEntity(-1, 0);
        Assert.False(terapagos.IsEmpty);
        Assert.Equal(1370, terapagos.Species);
        Assert.Equal("Terapagos-Terastal", terapagos.SpeciesName);
        Assert.Equal("Terapagos", terapagos.Nickname);
        Assert.Equal("sri", terapagos.OriginalTrainer);
        Assert.Equal(85, terapagos.Level);
        Assert.Equal(308, terapagos.CurrentHp);
        Assert.NotNull(terapagos.Stats);
        Assert.Equal(308, terapagos.Stats![0]); // max HP from the party tail
        Assert.Equal([31, 31, 31, 31, 31, 31], terapagos.IVs);
        Assert.True(session.ReadEntity(-1, 6).IsEmpty); // reads stop at the stored count
    }

    [Fact]
    public void TrainerCardReadsSectionZero()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var trainer = session.GetTrainer();
        Assert.Equal("sri", trainer.Name);
        Assert.Equal(45930, trainer.TID);
        Assert.Equal(36901, trainer.SID);
        Assert.Equal(1_391_024u, trainer.Money);
    }

    [Fact]
    public void PcStreamHoldsThe151GroundTruthMons()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var mons = 0;
        for (var box = 0; box < session.BoxCount; box++)
        for (var slot = 0; slot < session.BoxSlotCount; slot++)
            if (!session.ReadEntity(box, slot).IsEmpty)
                mons++;
        Assert.Equal(151, mons);

        var kirlia = session.ReadEntity(0, 0);
        Assert.Equal(393, kirlia.Species); // Hoenn-internal id, not the national 280
        Assert.Equal("Kirlia", kirlia.SpeciesName);
        Assert.Equal("Kirlia", kirlia.Nickname);

        // Boxes 0-5 hold the 151 mons; box 6 has the stragglers, box 7+ are empty.
        Assert.False(session.ReadEntity(0, 26).IsEmpty);
        Assert.True(session.ReadEntity(7, 0).IsEmpty);
        Assert.True(session.ReadEntity(21, 29).IsEmpty); // raw sector-30/31 box, empty here
    }

    [Fact]
    public void BoxNamesReadTheReverseRadicalRedTable()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Equal("Box1", session.GetBoxName(0));
        Assert.Equal("Box14", session.GetBoxName(13));
        Assert.Equal("Box15", session.GetBoxName(14)); // names past 14 are stored backwards
        Assert.Equal("Box22", session.GetBoxName(21));
    }

    [Fact]
    public void DexBitmapsReadFromSectionOne()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var progress = session.GetDexProgress();
        Assert.Equal(407, progress.Seen);
        Assert.Equal(107, progress.Caught);
        Assert.Equal(1000, progress.Total);
        Assert.Equal(new DexEntryState(false, false), session.GetDexEntry(0));
        Assert.Equal(new DexEntryState(false, false), session.GetDexEntry(1001)); // past the bitmap
    }

    [Fact]
    public void SerializeRoundTripsByteIdentical()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var original = File.ReadAllBytes(GroundTruth()!);
        var serialized = session.Serialize().ToArray();
        Assert.Equal(original, serialized);
    }

    [Fact]
    public void NicknameEditMirrorsToEverySectionOneCopyAndFixesChecksums()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "TERA"));

        var bytes = session.Serialize().ToArray();
        var copies = 0;
        for (var sector = 0; sector < RadicalRedFormat.SectorCount; sector++)
        {
            var off = sector * RadicalRedFormat.SectorSize;
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF4)) != 1) continue;
            copies++;
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF6));
            var window = RadicalRedFormat.CfruWindows[1];
            var computed = RadicalRedFormat.Checksum(bytes.AsSpan(off, window), window);
            Assert.Equal(computed, stored);
        }
        Assert.True(copies >= 2); // both rotating halves carry section 1
        Assert.True(SaveParser.IsPokemonRadicalRed(bytes));
    }

    [Fact]
    public void PcEditKeepsTheParasiteTailAndReopens()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        var original = File.ReadAllBytes(GroundTruth()!);
        Assert.True(session.DuplicateSlot(0, 0, 0, 3));

        var bytes = session.Serialize().ToArray();
        // Section 13's spare tail (the CFRU parasite past its 0x450 window) is preserved.
        var sections = RadicalRedFormat.SectionOffsets(original);
        var parasite = original.AsSpan(sections[13] + 0x450, 0xFF4 - 0x450).ToArray();
        Assert.Equal(parasite, bytes.AsSpan(sections[13] + 0x450, 0xFF4 - 0x450).ToArray());

        using var reopened = new RadicalRedEngineSession(bytes);
        Assert.Equal(393, reopened.ReadEntity(0, 3).Species); // the duplicated Kirlia
        Assert.Equal(152, CountMons(reopened));
    }

    [Fact]
    public void ReleaseAndMoveRoundTripThroughReopen()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        session.ReleaseSlot(0, 4); // an occupied slot (box 0 has gaps at 3/11/24)
        using var released = new RadicalRedEngineSession(session.Serialize());
        Assert.True(released.ReadEntity(0, 4).IsEmpty);
        Assert.Equal(150, CountMons(released));

        released.MoveSlot(-1, 0, 1, 15); // party Terapagos into an empty PC slot
        using var moved = new RadicalRedEngineSession(released.Serialize());
        Assert.Equal(5, PartyCount(moved)); // the party compacted to five
        Assert.True(moved.ReadEntity(-1, 5).IsEmpty);
        var boxed = moved.ReadEntity(1, 15);
        Assert.False(boxed.IsEmpty);
        Assert.Equal(1370, boxed.Species);
        Assert.Equal("Terapagos", boxed.Nickname);
        Assert.Equal(5, PartyCount(moved));
    }

    [Fact]
    public void DexEditsRoundTripThroughReopen()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        var before = session.GetDexEntry(985);
        Assert.Equal(new DexEntryState(false, false), before); // a species the owner never saw

        session.SetDexEntry(985, seen: true, caught: true);
        using var reopened = new RadicalRedEngineSession(session.Serialize());
        Assert.Equal(new DexEntryState(true, true), reopened.GetDexEntry(985));
        Assert.Equal(408, reopened.GetDexProgress().Seen);
        Assert.Equal(108, reopened.GetDexProgress().Caught);
    }

    [Fact]
    public void ExportAndImportRoundTripThroughPk3()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        var export = session.ExportSlot(0, 0);
        Assert.EndsWith(".pk3", export.FileName);


        Assert.False(session.ImportSlot(2, 29, new byte[8])); // garbage never imports
        Assert.True(session.ImportSlot(2, 29, export.Data));
        var imported = session.ReadEntity(2, 29);
        Assert.Equal(393, imported.Species);
        Assert.Equal("Kirlia", imported.Nickname);
    }

    [Fact]
    public void ShowdownTextAndBoxExportSpeakNames()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var text = session.GetShowdownText(-1, 0);
        Assert.Contains("Terapagos-Terastal", text);
        Assert.Contains("Level: 85", text);
        Assert.Contains("Ice Beam", text); // shared move-id zone 1..354

        var box = session.ExportBoxShowdown(0);
        Assert.Contains("Kirlia", box);
    }

    [Fact]
    public void ShowdownGenerationResolvesRadicalRedSpeciesByName()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var outcome = session.GenerateFromShowdownText(7, 1, "Mewtwo\nLevel: 70\nTimid Nature\n- Ice Beam\n- Psychic");
        Assert.True(outcome.Success, outcome.Message);
        var generated = session.ReadEntity(7, 1);
        Assert.Equal(150, generated.Species); // gen 1/2 ids stay national below 252
        Assert.Equal(70, generated.Level);
        using var reopened = new RadicalRedEngineSession(session.Serialize());
        Assert.Equal(150, reopened.ReadEntity(7, 1).Species);
    }

    [Fact]
    public void GenerationSkipsMovesBeyondTheSharedZone()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var outcome = session.GenerateFromShowdownText(7, 2, "Mewtwo\nLevel: 70\n- Tera Starstorm");
        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("skipped", outcome.Message); // honest about the unresolvable move
        Assert.Equal(0, session.ReadEntity(7, 2).Move1);
    }

    [Fact]
    public void UnsupportedFeaturesFailWithHonestMessages()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Throws<NotSupportedException>(() => session.CompleteDex());
        Assert.Throws<NotSupportedException>(() => session.SetTrainer(new TrainerInfo("X", 1, 2, 0, 0)));
        Assert.Throws<NotSupportedException>(() => session.SortBoxes(SortCriteria.DexNumber));
        Assert.Empty(session.GetEncounterCards(25, 0));
    }

    private static int CountMons(RadicalRedEngineSession session)
    {
        var mons = 0;
        for (var box = 0; box < session.BoxCount; box++)
        for (var slot = 0; slot < session.BoxSlotCount; slot++)
            if (!session.ReadEntity(box, slot).IsEmpty)
                mons++;
        return mons;
    }

    private static int PartyCount(RadicalRedEngineSession session)
    {
        var count = 0;
        for (var slot = 0; slot < 6; slot++)
            if (!session.ReadEntity(-1, slot).IsEmpty)
                count++;
        return count;
    }
}
