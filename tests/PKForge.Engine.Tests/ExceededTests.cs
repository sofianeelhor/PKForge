using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.Exceeded;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Emerald Exceeded engine against the two local samples (gitignored player saves: every
/// test skips when its file is absent). They prove the record layout on real bytes (species,
/// nickname, EXP and level, moves and PP, held item, party stats), the byte-identical no-op
/// round trip, that each proven edit survives a reopen with every live sector valid and the
/// backup slot untouched, and that what is not proven is refused before anything is written.
/// </summary>
public sealed class ExceededTests
{
    private const string EmeraldExceeded = "batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav";
    private const string OlderExceeded = "batch-2026-11-12/saves/saves/Pokemon - Exceeded.sav";

    private static byte[]? Sample(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        if (directory is null) return null;
        var path = Path.Combine(directory.FullName, ".local-testdata", "romhacks", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static ISaveEngineSession Open(byte[] bytes)
    {
        var session = new SaveEngine().OpenSession(bytes, "test");
        Assert.IsType<ExceededEngineSession>(session);
        return session;
    }

    /// <summary>Every stamped sector of both slots still checksums over the full 4084-byte window.</summary>
    private static void AssertSealed(byte[] data)
    {
        for (var sector = 0; sector < 28; sector++)
        {
            var offset = sector * 0x1000;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0xFF8)) != 0x0801_2025) continue;
            Assert.True(ExceededFormat.Valid(data, offset, 0xFF4), $"sector {sector} checksum");
        }
    }

    /// <summary>Physical sectors of the slot that is NOT live (the backup): they must never change.</summary>
    private static IEnumerable<int> BackupSectors(byte[] data)
    {
        var live = ExceededFormat.SectionOffsets(data)!;
        for (var sector = 0; sector < 28; sector++)
            if (!live.Contains(sector * 0x1000))
                yield return sector;
    }

    [Fact]
    public void BothBuildsAreProvenAndRoundTripUntouched()
    {
        foreach (var file in new[] { EmeraldExceeded, OlderExceeded })
        {
            if (Sample(file) is not { } bytes) continue;
            Assert.True(ExceededFormat.Matches(bytes));
            Assert.Equal(SaveFormat.Expansion, SaveParser.HackRoute(bytes));
            Assert.Null(Expansion.ExpansionFormat.LooksLike(bytes));
            using var session = Open(bytes);
            Assert.Equal(["Emerald Exceeded"], session.GameNames);
            Assert.Equal(bytes, session.Serialize().ToArray());
            Assert.Null(new SaveEngine().AssessLayoutRisk(bytes));
            for (var box = 0; box < 13; box++)
                Assert.False(string.IsNullOrWhiteSpace(session.GetBoxName(box)));
        }
    }

    [Fact]
    public void EmeraldExceededRecordsDecodeOnRealBytes()
    {
        if (Sample(EmeraldExceeded) is not { } bytes) return;
        using var session = Open(bytes);
        Assert.Equal("Mia", session.GetTrainer().Name);

        // Party: species, level (stored, and equal to the EXP's level), moves with in-range PP.
        var rapidash = session.ReadEntity(-1, 0);
        Assert.Equal((78, "Rapidash", 50), (rapidash.Species, rapidash.SpeciesName, rapidash.Level)); // Galarian (raw 976)
        // PKHeX ids: the record's raw 639 is the expansion's Pollen Puff, PKHeX's 676 (QA 3.2 #5).
        Assert.Equal([533, 427, 523, 676], new[] { rapidash.Move1, rapidash.Move2, rapidash.Move3, rapidash.Move4 });
        Assert.Equal(218, rapidash.Stats![0]);
        Assert.Equal(0, rapidash.CurrentHp);
        var zacian = session.ReadEntity(-1, 2);
        Assert.Equal((888, "Zacian", 50), (zacian.Species, zacian.SpeciesName, zacian.Level));
        var kyurem = session.ReadEntity(-1, 3);
        Assert.Equal((646, 28), (kyurem.Species, kyurem.Level)); // Kyurem form, raw 1104
        var pichu = session.ReadEntity(-1, 4);
        Assert.Equal((172, 421, 24), (pichu.Species, pichu.HeldItem, pichu.Level)); // Power Belt
        Assert.Equal("Power Belt", session.GetItemNames()[421]);

        // Box: the level comes from EXP and the species' growth rate.
        var jirachi = session.ReadEntity(0, 0);
        Assert.Equal((385, "Jirachi", 3), (jirachi.Species, jirachi.SpeciesName, jirachi.Level));
        Assert.Equal("Mia", jirachi.OriginalTrainer);
        Assert.True(session.ReadEntity(0, 29).IsEmpty);
    }

    [Fact]
    public void ProvenEditsSurviveAReopenAndTouchNothingElse()
    {
        if (Sample(EmeraldExceeded) is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.ApplyEdit(-1, 1, new EntityEdit(Nickname: "KING", OriginalTrainer: "SOF", HeldItem: 421, Move4: 1));
            session.ApplyEdit(0, 0, new EntityEdit(Level: 40, Move4: 85));
            session.ApplyMoveDetails(0, 0, new MoveDetailsEdit(PP: [1, 2, 3, 4]));
            written = session.Serialize().ToArray();
        }
        AssertSealed(written);
        foreach (var sector in BackupSectors(bytes))
            Assert.True(bytes.AsSpan(sector * 0x1000, 0x1000).SequenceEqual(written.AsSpan(sector * 0x1000, 0x1000)), $"backup sector {sector}");

        using var reopened = Open(written);
        var nidoking = reopened.ReadEntity(-1, 1);
        Assert.Equal(("KING", "SOF", 421, 1), (nidoking.Nickname, nidoking.OriginalTrainer, nidoking.HeldItem, nidoking.Move4));
        Assert.Equal((34, 51), (nidoking.Species, nidoking.Level));
        Assert.Equal(35, reopened.GetMoveDetails(-1, 1).Moves[3].PP); // Pound's PP
        var jirachi = reopened.ReadEntity(0, 0);
        Assert.Equal((385, 40, 85), (jirachi.Species, jirachi.Level, jirachi.Move4));
        Assert.Equal([1, 2, 3, 4], reopened.GetMoveDetails(0, 0).Moves.Select(m => m.PP));
        // The other Pokémon are byte-identical.
        var rapidash = reopened.ReadEntity(-1, 0);
        Assert.Equal((78, 50, "Rapidash"), (rapidash.Species, rapidash.Level, rapidash.Nickname));
        using var original = Open(bytes);
        var before = original.ReadEntity(0, 1);
        var after = reopened.ReadEntity(0, 1);
        Assert.Equal((before.Species, before.Nickname, before.Level, before.Move1), (after.Species, after.Nickname, after.Level, after.Move1));
    }

    [Fact]
    public void UnprovenChangesAreRefusedBeforeAnyWrite()
    {
        if (Sample(EmeraldExceeded) is not { } bytes) return;
        using var session = Open(bytes);
        var rapidash = session.ReadEntity(-1, 0);
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "X", Level: 60)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "X", Species: 1)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "X", IVs: [31, 31, 31, 31, 31, 31])));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Move1: 622))); // a Z-move (PKHeX id)
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Move1: 0, Move2: 0, Move3: 0, Move4: 0)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(Species: 1)));
        Assert.Throws<NotSupportedException>(() => session.MoveSlot(0, 0, -1, 5));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 29, new EntityEdit(Species: 25)));
        Assert.False(session.DuplicateSlot(0, 0, -1, 0));
        Assert.Equal((rapidash.Nickname, rapidash.Level), (session.ReadEntity(-1, 0).Nickname, session.ReadEntity(-1, 0).Level));
        Assert.Equal(bytes, session.Serialize().ToArray());
        Assert.False(session.StoresMove(622));   // Breakneck Blitz: the expansion's raw ids stop short of its Z-moves
        Assert.True(session.StoresMove(659));    // Shore Up, raw 622
        Assert.False(session.StoresMove((int)PKHeX.Core.Move.MaxStrike)); // past the raw 850 both builds share
    }

    [Fact]
    public void PartyToBoxAndBoxMovesKeepTheRecords()
    {
        if (Sample(EmeraldExceeded) is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            var frogadier = session.ReadEntity(-1, 5);
            session.MoveSlot(-1, 5, 12, 29);       // deposit: the 88-byte record, the party closes up
            session.MoveSlot(0, 0, 12, 28);        // box to box
            Assert.True(session.DuplicateSlot(12, 28, 12, 27));
            session.MoveSlot(-1, 0, -1, 1);        // party swap
            written = session.Serialize().ToArray();
            Assert.Equal(frogadier.Nickname, session.ReadEntity(12, 29).Nickname);
        }
        AssertSealed(written);
        using var reopened = Open(written);
        Assert.True(reopened.ReadEntity(-1, 5).IsEmpty);
        var deposited = reopened.ReadEntity(12, 29);
        Assert.Equal((657, 28, "Frogadier"), (deposited.Species, deposited.Level, deposited.Nickname));
        Assert.Null(deposited.Stats);
        Assert.Equal(385, reopened.ReadEntity(12, 28).Species);
        Assert.Equal(385, reopened.ReadEntity(12, 27).Species);
        Assert.True(reopened.ReadEntity(0, 0).IsEmpty);
        Assert.Equal(("Nidoking", "Rapidash"), (reopened.ReadEntity(-1, 0).Nickname, reopened.ReadEntity(-1, 1).Nickname));
    }

    [Fact]
    public void TheOlderBuildKeepsItsOwnSpeciesNumbersUnnamed()
    {
        if (Sample(OlderExceeded) is not { } bytes) return;
        using var session = Open(bytes);
        Assert.Equal("Leaf", session.GetTrainer().Name);
        var latias = session.ReadEntity(-1, 0);
        Assert.Equal((380, "Latias", 55), (latias.Species, latias.SpeciesName, latias.Level));

        // Box 1 slot 3 is this build's Iron Hands (raw 1001, the pack's Hisuian Lilligant).
        var ironHands = session.ReadEntity(0, 2);
        Assert.Equal(("Iron Hands", 0, "Species 1001"), (ironHands.Nickname, ironHands.Species, ironHands.SpeciesName));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 2, new EntityEdit(Level: 50)));

        session.ApplyEdit(0, 2, new EntityEdit(Nickname: "HANDS"));
        var written = session.Serialize().ToArray();
        AssertSealed(written);
        using var reopened = Open(written);
        Assert.Equal("HANDS", reopened.ReadEntity(0, 2).Nickname);
    }
}
