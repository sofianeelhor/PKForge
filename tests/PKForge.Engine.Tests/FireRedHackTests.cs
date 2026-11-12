using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.FireRedHacks;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Fire Red Extended and Crystal Advance Redux, proven on the real saves (gitignored; every test
/// skips when its sample is absent): names, an untouched byte-identical round trip, edits that
/// survive a reopen, the hack's own checksum scheme on every rewritten sector, nothing but the
/// edited records changing, and every id the hack is not proven to have refused.
/// </summary>
public sealed class FireRedHackTests
{
    private const string Fre = "romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav";
    private const string Car = "romhacks/batch-2026-11-12/romhack/romhack saves/Pokemon - Crystal Advance - Redux.sav";

    private static byte[]? Sample(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", relative);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static ISaveEngineSession Open(byte[] bytes) => new SaveEngine().OpenSession(bytes, "test");

    /// <summary>Every live sector validates over the hack's own windows (and only those).</summary>
    private static void AssertSealed(byte[] bytes, FireRedHackGame game)
    {
        var data = bytes.AsSpan(0, FireRedHackFormat.FileSize);
        var live = FireRedHackFormat.LiveSectors(data);
        var windows = FireRedHackFormat.Windows(game);
        for (var id = 0; id < 14; id++)
            Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(data[(live[id] + 0xFF6)..]),
                Expansion.ExpansionFormat.Checksum(data.Slice(live[id], windows[id]), windows[id]));
    }

    /// <summary>Physical sectors whose bytes differ.</summary>
    private static List<int> ChangedSectors(byte[] a, byte[] b) =>
        Enumerable.Range(0, 32).Where(s => !a.AsSpan(s * 0x1000, 0x1000).SequenceEqual(b.AsSpan(s * 0x1000, 0x1000))).ToList();

    // ── Fire Red Extended ──

    [Fact]
    public void FireRedExtendedRoutesToItsEngineAndReadsItsPokemon()
    {
        if (Sample(Fre) is not { } bytes) return;
        Assert.Equal(SaveFormat.FireRedExtended, SaveParser.HackRoute(bytes));
        Assert.Equal(FireRedHackGame.FireRedExtended, FireRedHackFormat.Identify(bytes));
        Assert.False(SaveParser.IsPokemonRadicalRed(bytes));
        var engine = new SaveEngine();
        Assert.Null(engine.AssessLayoutRisk(bytes));
        Assert.Equal("Fire Red Extended", engine.TryDescribe(bytes)?.GameName);

        using var session = Open(bytes);
        Assert.Equal(["Fire Red Extended"], session.GameNames);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));

        var mew = session.ReadEntity(-1, 0);
        Assert.Equal("Mew", mew.SpeciesName);
        Assert.Equal(151, mew.Species);
        Assert.Equal(10, mew.Level);
        Assert.Equal(141, mew.HeldItem);
        Assert.Equal("Lum Berry", session.GetItemNames()[mew.HeldItem]);
        Assert.Equal([144, 219, 277, 5], new[] { mew.Move1, mew.Move2, mew.Move3, mew.Move4 }); // Transform, Safeguard, Magic Coat, Mega Punch

        var suicune = session.ReadEntity(0, 0);
        Assert.Equal(("Suicune", 245, 10), (suicune.SpeciesName, suicune.Species, suicune.Level));
        var genesect = session.ReadEntity(0, 18); // stored as 702 = national 649 + 53
        Assert.Equal(("Genesect", 649), (genesect.SpeciesName, genesect.Species));
        Assert.Equal([0, 0, 0, 210], new[] { genesect.Move1, genesect.Move2, genesect.Move3, genesect.Move4 }); // 401/449/409 unnamed, Fury Cutter
        var turtwig = session.ReadEntity(4, 9);
        Assert.Equal(("Turtwig", 387), (turtwig.SpeciesName, turtwig.Species));
        var deoxysForm = session.ReadEntity(2, 16); // 1140: a form id past Enamorus
        Assert.Equal(("Species #1140", 0, "Deoxys"), (deoxysForm.SpeciesName, deoxysForm.Species, deoxysForm.Nickname));
    }

    [Fact]
    public void FireRedExtendedSpeciesPastDeoxysAreNationalPlus53()
    {
        // Every boxed Pokémon stored between 440 and 958 still wears its default nickname: each
        // one is the species national + 53 names, which is the whole evidence for that rule.
        if (Sample(Fre) is not { } bytes) return;
        using var session = Open(bytes);
        var tables = FireRedHackTables.FireRedExtended;
        var storage = FireRedHackFormat.ReadBlock(bytes, FireRedHackFormat.LiveSectors(bytes), 5, FireRedHackFormat.StorageSize);
        var anchors = 0;
        var retail = 0;
        for (var box = 0; box < 14; box++)
        for (var slot = 0; slot < 30; slot++)
        {
            var mon = new FireRedHackMon(storage.AsSpan(4 + (box * 30 + slot) * 80, 80), encrypted: true);
            if (mon.IsEmpty) continue;
            Assert.True(mon.LooksValid);
            if (mon.Species is >= 440 and <= 958)
            {
                var name = tables.SpeciesName(mon.Species);
                // Nicknames hold 10 characters: the ROM spells Blacephalon "Blacphalon" to fit.
                Assert.Equal(mon.Nickname, name == "Blacephalon" ? "Blacphalon" : name[..Math.Min(10, name.Length)]);
                anchors++;
            }
            else if (FireRedHackTables.IsRetailSpecies(mon.Species))
            {
                // Retail ids keep FireRed's growth: each was generated at level 10.
                Assert.Equal(10, FireRedHackTables.LevelOf(mon.Species, mon.Experience));
                if (mon.Nickname.Length > 0 && mon.Species != 283) // the Mudkip carries a garbled nickname
                    Assert.Equal(mon.Nickname, tables.SpeciesName(mon.Species));
                retail++;
            }
        }
        Assert.Equal(87, anchors);
        Assert.Equal(30, retail);
    }

    [Fact]
    public void FireRedExtendedEditsSurviveAReopenUnderItsOwnChecksums()
    {
        if (Sample(Fre) is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "Mewtle", HeldItem: 200, Move1: 94)); // Leftovers, Psychic
            session.ApplyEdit(-1, 0, new EntityEdit(Level: 25)); // party stats proven: they follow FireRed's formula
            session.ApplyEdit(4, 0, new EntityEdit(Level: 30, Move4: 53)); // boxed Charmander: Flamethrower
            session.ApplyEdit(0, 10, new EntityEdit(Nickname: "Lati")); // Latias keeps its unnamed moves 409/482
            session.MoveSlot(0, 0, 6, 0); // Suicune to box 7
            session.MoveSlot(1, 0, 0, 0); // Manaphy onto the freed slot
            written = session.Serialize().ToArray();
        }
        AssertSealed(written, FireRedHackGame.FireRedExtended);
        Assert.Equal(SaveFormat.FireRedExtended, SaveParser.HackRoute(written));
        Assert.Null(new SaveEngine().CheckWriteSafety(bytes, written, null));
        // Only the live slot's SaveBlock1 sector and the touched PC sectors changed.
        var live = FireRedHackFormat.LiveSectors(bytes);
        Assert.All(ChangedSectors(bytes, written), sector => Assert.Contains(sector * 0x1000, live));

        using var reopened = Open(written);
        var mew = reopened.ReadEntity(-1, 0);
        Assert.Equal(("Mewtle", 200, 94, 25), (mew.Nickname, mew.HeldItem, mew.Move1, mew.Level));
        Assert.Equal(FireRedHackTables.RetailStats(151, 25, mew.IVs, mew.EVs, mew.Nature), mew.Stats);
        Assert.Equal(53, reopened.ReadEntity(4, 0).Move4);
        Assert.Equal(30, reopened.ReadEntity(4, 0).Level);
        Assert.Equal(15, reopened.GetMoveDetails(4, 0).Moves[3].PP);
        var latias = reopened.ReadEntity(0, 10);
        Assert.Equal("Lati", latias.Nickname);
        var latiasRecord = new FireRedHackMon(FireRedHackFormat.ReadBlock(written, FireRedHackFormat.LiveSectors(written), 5, FireRedHackFormat.StorageSize).AsSpan(4 + 10 * 80, 80), true);
        Assert.Equal([409, 346, 204, 482], Enumerable.Range(0, 4).Select(latiasRecord.Move).ToArray());
        Assert.Equal("Suicune", reopened.ReadEntity(6, 0).SpeciesName);
        Assert.Equal("Manaphy", reopened.ReadEntity(0, 0).SpeciesName);
        Assert.True(reopened.ReadEntity(1, 0).IsEmpty);
        // Untouched Pokémon keep their exact bytes.
        Assert.Equal(Record(bytes, 2, 0), Record(written, 2, 0));
    }

    private static byte[] Record(byte[] save, int box, int slot) =>
        FireRedHackFormat.ReadBlock(save, FireRedHackFormat.LiveSectors(save), 5, FireRedHackFormat.StorageSize)
            .AsSpan(4 + (box * 30 + slot) * 80, 80).ToArray();

    [Fact]
    public void FireRedExtendedNeverWritesWhatItCannotProve()
    {
        if (Sample(Fre) is not { } bytes) return;
        using var session = Open(bytes);
        Assert.False(session.StoresMove(355));
        Assert.True(session.StoresMove(354));
        Assert.Equal("", session.GetItemNames()[91]); // FireRed's "???", reused by the hack (99x in its bag)
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Move2: 400)));
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(HeldItem: 91)));
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Species: 150)));
        // Genesect's growth rate is the ROM's: its level is never written.
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(0, 18, new EntityEdit(Level: 50)));
        // A boxed Pokémon's party stats come from base stats only the ROM has.
        Assert.Throws<InvalidOperationException>(() => session.MoveSlot(4, 0, -1, 1));
        // A refused edit changed nothing.
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        // An echo of every shown field (the editor's save button) is a no-op, unknown moves included.
        var genesect = session.ReadEntity(0, 18);
        session.ApplyEdit(0, 18, new EntityEdit(genesect.Species, genesect.Nickname, genesect.Level, genesect.Nature, genesect.Ability,
            genesect.HeldItem, genesect.Move1, genesect.Move2, genesect.Move3, genesect.Move4, genesect.IVs, genesect.EVs,
            genesect.IsShiny, genesect.Ball, genesect.OriginalTrainer, genesect.Gender, genesect.Friendship));
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    // ── Crystal Advance Redux ──

    [Fact]
    public void CrystalAdvanceReduxRoutesToItsEngineAndReadsItsParty()
    {
        if (Sample(Car) is not { } bytes) return;
        Assert.Equal(SaveFormat.CrystalAdvanceRedux, SaveParser.HackRoute(bytes));
        Assert.Equal(FireRedHackGame.CrystalAdvanceRedux, FireRedHackFormat.Identify(bytes));
        var engine = new SaveEngine();
        Assert.Null(engine.AssessLayoutRisk(bytes));
        Assert.Equal("Crystal Advance Redux", engine.TryDescribe(bytes)?.GameName);

        using var session = Open(bytes);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        var chikorita = session.ReadEntity(-1, 0);
        Assert.Equal(("Chikorita", 152, 5, "Mia"), (chikorita.SpeciesName, chikorita.Species, chikorita.Level, chikorita.OriginalTrainer));
        Assert.Equal("Oran Berry", session.GetItemNames()[chikorita.HeldItem]);
        Assert.Equal([33, 45, 22, 0], new[] { chikorita.Move1, chikorita.Move2, chikorita.Move3, chikorita.Move4 }); // Tackle, Growl, Vine Whip
        Assert.Equal([21, 10, 12, 10, 13, 10], chikorita.Stats); // FireRed's formula, exactly
        Assert.Equal("", session.GetItemNames()[121]); // Orange Mail's id sits in the hack's Key Items pocket
        Assert.Equal("BOX1", session.GetBoxName(0));
    }

    [Fact]
    public void CrystalAdvanceReduxEditsStayPlainAndSurviveAReopen()
    {
        if (Sample(Car) is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "Leafy", Level: 12, HeldItem: 200, Move4: 75)); // Leftovers, Razor Leaf
            Assert.True(session.DuplicateSlot(-1, 0, 0, 0)); // a copy into box 1
            session.MoveSlot(0, 0, 3, 7); // box to box
            Assert.True(session.DuplicateSlot(3, 7, -1, 1)); // withdrawn: FireRed's data, level from EXP
            written = session.Serialize().ToArray();
        }
        AssertSealed(written, FireRedHackGame.CrystalAdvanceRedux);
        Assert.Equal(SaveFormat.CrystalAdvanceRedux, SaveParser.HackRoute(written));
        Assert.Null(new SaveEngine().CheckWriteSafety(bytes, written, null));
        var live = FireRedHackFormat.LiveSectors(bytes);
        Assert.All(ChangedSectors(bytes, written), sector => Assert.Contains(sector * 0x1000, live));

        using var reopened = Open(written);
        var leafy = reopened.ReadEntity(-1, 0);
        Assert.Equal(("Leafy", 12, 200, 75), (leafy.Nickname, leafy.Level, leafy.HeldItem, leafy.Move4));
        Assert.Equal(FireRedHackTables.RetailStats(152, 12, leafy.IVs, leafy.EVs, leafy.Nature), leafy.Stats);
        Assert.Equal("Leafy", reopened.ReadEntity(3, 7).Nickname);
        Assert.Equal(12, reopened.ReadEntity(3, 7).Level);
        Assert.Equal(leafy.Stats, reopened.ReadEntity(-1, 1).Stats);
        Assert.True(reopened.ReadEntity(0, 0).IsEmpty);

        // The records stay in the hack's own form: plain G-A-E-M data, checksum field zero.
        var sb1 = FireRedHackFormat.ReadBlock(written, FireRedHackFormat.LiveSectors(written), 1, FireRedHackFormat.SaveBlock1Size);
        var party = sb1.AsSpan(0x38, 100);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(party[0x1C..]));
        Assert.Equal(152, BinaryPrimitives.ReadUInt16LittleEndian(party[0x20..]));
        Assert.Equal(75, BinaryPrimitives.ReadUInt16LittleEndian(party[0x32..]));
        Assert.Equal(party[..80].ToArray(), Record(written, 3, 7));
    }

    [Fact]
    public void CrystalAdvanceReduxRefusesIdsItIsNotProvenToHave()
    {
        if (Sample(Car) is not { } bytes) return;
        using var session = Open(bytes);
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Move1: 355)));
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(HeldItem: 121)));
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Species: 155)));
        Assert.Throws<InvalidOperationException>(() => session.ReleaseSlot(-1, 0)); // never an empty party
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }
}
