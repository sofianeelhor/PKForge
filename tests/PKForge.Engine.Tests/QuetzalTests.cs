using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Quetzal;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon Quetzal: 16-sector slots, plaintext 104-byte party records and a bit-packed PC
/// whose record size (21/31/38 bytes) is identified from the data. The real saves live
/// outside the repository; every test skips when its sample is absent.
/// </summary>
public sealed class QuetzalTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "romhacks", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static byte[]? Sample(string file) =>
        LocalArtifact(file) is { } path ? File.ReadAllBytes(path) : null;

    /// <summary>The bills-pc ground-truth dump (0x20010, mGBA RTC footer, trainer John). The
    /// sample archive shipped a different Quetzal save under this name (0x20000, the
    /// archive's "Pokemon - Quetzal.sav"); its contents do not match these assertions, so
    /// the bills-specific tests skip unless the real dump is present.</summary>
    private static byte[]? BillsSample() =>
        Sample("quetzal-bills.sav") is { Length: 0x20010 } bytes ? bytes : null;

    [Fact]
    public void RealSavesLookLikeQuetzal()
    {
        foreach (var file in new[] { "quetzal-9.1.sav", "quetzal-bills.sav" })
        {
            var bytes = Sample(file);
            if (bytes is null) continue;
            Assert.True(QuetzalFormat.LooksLike(bytes), file);
            Assert.False(QuetzalFormat.LooksLike(new byte[0x20000]), $"{file}: a blank image is not a save");
        }
    }

    [Fact]
    public void PartyReadsTheSamplesKnownPokemon()
    {
        var nine = Sample("quetzal-9.1.sav");
        if (nine is not null)
        {
            using var session = new QuetzalEngineSession(nine);
            Assert.Equal(3, session.Generation);
            Assert.Equal("Quetzal", Assert.Single(session.GameNames));
            Assert.Equal(1528, session.MaxSpeciesId);
            Assert.Equal(6, session.Snapshot.Slots.Count(s => s.Box == -1));

            int[] species = [286, 281, 34, 256, 475, 522];
            string[] nicknames = ["Breloom", "Kirlia", "Nidoking", "Combusken", "Vergil", "Blitzle"];
            int[] levels = [23, 24, 24, 24, 24, 19];
            for (var slot = 0; slot < 6; slot++)
            {
                var mon = session.ReadEntity(-1, slot);
                Assert.False(mon.IsEmpty);
                Assert.Equal(species[slot], mon.Species);
                Assert.Equal(nicknames[slot], mon.Nickname);
                Assert.Equal(levels[slot], mon.Level);
                Assert.Equal("Camron", mon.OriginalTrainer);
            }

            var trainer = session.GetTrainer();
            Assert.Equal("Camron", trainer.Name);
            Assert.Equal(39314, trainer.TID);
            Assert.Equal(3579, trainer.SID);
        }

        var bills = BillsSample();
        if (bills is not null)
        {
            using var session = new QuetzalEngineSession(bills);
            int[] species = [208, 286, 143, 272, 6, 561];
            string[] nicknames = ["Steelix", "Breloom", "Snorlax", "Ludicolo", "Rayquaza", "Sigilyph"];
            int[] levels = [44, 45, 47, 45, 41, 37];
            for (var slot = 0; slot < 6; slot++)
            {
                var mon = session.ReadEntity(-1, slot);
                Assert.Equal(species[slot], mon.Species);
                Assert.Equal(nicknames[slot], mon.Nickname);
                Assert.Equal(levels[slot], mon.Level);
                Assert.Equal("John", mon.OriginalTrainer);
            }
            var trainer = session.GetTrainer();
            Assert.Equal("John", trainer.Name);
            Assert.Equal(8202, trainer.TID);
            Assert.Equal(54380, trainer.SID);
        }
    }

    [Fact]
    public void PcFormatBoxCountAndNamesAreDetected()
    {
        var bills = BillsSample();
        if (bills is not null)
        {
            using var session = new QuetzalEngineSession(bills);
            Assert.True(session.BoxFormatDetected);
            Assert.Equal(31, session.BoxRecordSize);
            Assert.Equal(45, session.BoxCount);
            Assert.Equal(30, session.BoxSlotCount);
            Assert.Equal("BOX1", session.GetBoxName(0));
            Assert.Equal("BOX45", session.GetBoxName(44));

            var surskit = session.ReadEntity(0, 0);
            Assert.Equal(283, surskit.Species);
            Assert.Equal("Surskit", surskit.Nickname);
            Assert.Equal(153, surskit.HeldItem);
            Assert.Equal(98, surskit.Move1);
            Assert.Equal(21, surskit.Level);
            Assert.False(surskit.IsShiny);
            Assert.True(surskit.Gender is 0 or 1);
            Assert.Equal(45 * 30, session.Snapshot.Slots.Count(s => s.Box >= 0));
        }

        var nine = Sample("quetzal-9.1.sav");
        if (nine is not null)
        {
            using var session = new QuetzalEngineSession(nine);
            Assert.True(session.BoxFormatDetected);
            Assert.Equal(31, session.BoxRecordSize);
            Assert.Equal(45, session.BoxCount);
            Assert.Equal("BOX1", session.GetBoxName(0));

            var first = session.ReadEntity(0, 0);
            Assert.Equal(194, first.Species);
            Assert.Equal(3, first.Level);
            Assert.Equal(55, first.Move1);

            var second = session.ReadEntity(0, 1);
            Assert.Equal(406, second.Species);
            Assert.True(second.IsShiny);
        }
    }

    [Fact]
    public void NoOpSerializeIsByteIdentical()
    {
        foreach (var file in new[] { "quetzal-9.1.sav", "quetzal-bills.sav" })
        {
            var bytes = Sample(file);
            if (bytes is null) continue;
            using var session = new QuetzalEngineSession(bytes);
            _ = session.Snapshot;
            _ = session.ReadEntity(-1, 0);
            Assert.True(bytes.SequenceEqual(session.Serialize().ToArray()), file);
        }
    }

    [Fact]
    public void PartyEditsPersistAndTheBackupSlotIsUntouched()
    {
        var bytes = Sample("quetzal-9.1.sav");
        if (bytes is null) return;

        byte[] edited;
        using (var session = new QuetzalEngineSession(bytes))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Level: 50, Nickname: "BREL"));
            edited = session.Serialize().ToArray();
        }

        // The 9.1 save's newest slot is the first half (counter 42 vs 41).
        Assert.True(bytes.AsSpan(0x10000, 0x10000).SequenceEqual(edited.AsSpan(0x10000, 0x10000)));
        using var reopened = new QuetzalEngineSession(edited);
        var mon = reopened.ReadEntity(-1, 0);
        Assert.Equal(50, mon.Level);
        Assert.Equal("BREL", mon.Nickname);
    }

    [Fact]
    public void PartyEditsSurviveTheRtcFooterDumpToo()
    {
        var bytes = BillsSample();
        if (bytes is null) return;
        Assert.Equal(0x20010, bytes.Length);

        byte[] edited;
        using (var session = new QuetzalEngineSession(bytes))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Level: 50, Nickname: "STEEL"));
            edited = session.Serialize().ToArray();
        }
        Assert.Equal(0x20010, edited.Length);
        Assert.True(bytes.AsSpan(^16).SequenceEqual(edited.AsSpan(^16)));

        using var reopened = new QuetzalEngineSession(edited);
        var mon = reopened.ReadEntity(-1, 0);
        Assert.Equal(50, mon.Level);
        Assert.Equal("STEEL", mon.Nickname);
        Assert.Equal(208, mon.Species);
    }

    [Fact]
    public void BoxEditsPersistAndLeaveOtherFieldsAlone()
    {
        var bytes = BillsSample();
        if (bytes is null) return;

        byte[] edited;
        using (var session = new QuetzalEngineSession(bytes))
        {
            session.ApplyEdit(0, 0, new EntityEdit(HeldItem: 200, Move1: 100));
            edited = session.Serialize().ToArray();
        }

        // The bills save's newest slot is the second half (counter 125 vs 124).
        Assert.True(bytes.AsSpan(0, 0x10000).SequenceEqual(edited.AsSpan(0, 0x10000)));
        using var reopened = new QuetzalEngineSession(edited);
        var mon = reopened.ReadEntity(0, 0);
        Assert.Equal(283, mon.Species);
        Assert.Equal("Surskit", mon.Nickname);
        Assert.Equal(200, mon.HeldItem);
        Assert.Equal(100, mon.Move1);
        Assert.Equal(230, mon.Move2);
        Assert.Equal(21, mon.Level);
    }

    [Fact]
    public void RoutingValidatesDescribesAndRejectsTheWrongRoute()
    {
        var bytes = Sample("quetzal-9.1.sav");
        if (bytes is null) return;

        var engine = new SaveEngine();
        Assert.True(SaveParser.IsPokemonQuetzal(bytes));
        Assert.True(engine.Validate(bytes));
        Assert.Null(engine.AssessLayoutRisk(bytes));

        using var session = engine.OpenSession(bytes);
        Assert.IsType<QuetzalEngineSession>(session);

        var description = engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Quetzal", description.GameName);
        var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, "q.sav", null, "Save");
        Assert.Equal(SaveFormat.Quetzal, guess.Format);
        Assert.Equal(SaveLayoutFamily.Quetzal, guess.Family);

        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, null, SaveFormat.Standard));
        using var explicitSession = engine.OpenSession(bytes, null, SaveFormat.Quetzal);
        Assert.IsType<QuetzalEngineSession>(explicitSession);
    }

    [Fact]
    public void OutOfRangeEditsAreRefused()
    {
        var bytes = Sample("quetzal-9.1.sav");
        if (bytes is null) return;
        using var session = new QuetzalEngineSession(bytes);

        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(Species: 1529)));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(Level: 0)));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(Move1: 849)));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(IVs: [32, 0, 0, 0, 0, 0])));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(EVs: [0, 0, 0, 0, 0, 256])));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(0, 0, new EntityEdit(HeldItem: 1024)));
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyEdit(0, 0, new EntityEdit(Ball: 64)));
    }
}
