using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.Expansion;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The three Emerald hacks read through their own pokeemerald-expansion profiles: Team Rocket
/// Edition (its own record header), Black Pearl Emerald before 2.1 (grown SaveBlock1, its own
/// item enum) and Fire Emerald (a longer PokemonStorage). Every fact is checked on the real
/// gitignored saves and skips when they are absent: the party reads true, an untouched save
/// round-trips byte for byte, the proven edits survive a reopen with valid checksums, the
/// unproven fields refuse, and the backup slot is never touched.
/// </summary>
public sealed class ExpansionHackProfilesTests
{
    private const string Batch = "batch-2026-11-12/";
    private const string TeamRocketFile = Batch + "mgba/mGBA Romhack Saves/Pokemon - Team Rocket Edition.srm";
    private const string BlackPearlFile = Batch + "romhack/romhack saves/Pokemon - Black Pearl Emerald.sav";
    private const string FireEmeraldFile = Batch + "saves/saves/Pokemon - Fire Emerald.sav";

    private static byte[]? Sample(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        if (directory is null) return null;
        var path = Path.Combine(directory.FullName, ".local-testdata", "romhacks", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static ExpansionEngineSession Open(byte[] bytes) => new(bytes, "test");

    /// <summary>Every physical sector that is not in the live slot is byte-identical.</summary>
    private static void AssertBackupUntouched(byte[] before, byte[] after)
    {
        var live = ExpansionFormat.SectionOffsets(before).ToHashSet();
        for (var offset = 0; offset < ExpansionFormat.FileSize; offset += ExpansionFormat.SectorSize)
            if (!live.Contains(offset))
                Assert.True(before.AsSpan(offset, 0x1000).SequenceEqual(after.AsSpan(offset, 0x1000)), $"sector 0x{offset:X} changed");
    }

    /// <summary>The decrypted 100-byte form of a party record of the live slot.</summary>
    private static byte[] DecryptedParty(byte[] save, ExpansionProfile profile, int slot)
    {
        var sections = ExpansionFormat.SectionOffsets(save);
        var block = ExpansionFormat.ReadBlock(save, sections, profile, ExpansionFormat.SaveBlock1FirstSection,
            ExpansionFormat.SaveBlock1Sections, profile.SaveBlock1Size);
        return new PK3(block.AsSpan(profile.PartyOffset + slot * 100, 100).ToArray()).Data.ToArray();
    }

    // ── Team Rocket Edition ──

    [Fact]
    public void TeamRocketEditionReadsTrueAndRoundTrips()
    {
        if (Sample(TeamRocketFile) is not { } bytes) return;
        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.Equal(ExpansionGame.TeamRocketEdition, profile?.Game);
        Assert.Equal(ExpansionMonCodec.TeamRocketEdition, profile!.Codec);
        Assert.Equal(0xFF4, profile.SectorDataSize);

        using var session = Open(bytes);
        Assert.Null(session.ReadOnlyReason);
        Assert.Equal("Jason", session.GetTrainer().Name);
        (int Species, int Level, int[] Moves, string Item, int Friendship)[] party =
        [
            (169, 79, [174, 366, 305, 19], "Air Balloon", 70),       // Crobat: Curse, Tailwind, Poison Fang, Fly
            (229, 77, [68, 126, 53, 517], "Silver Powder", 35),      // Houndoom: Counter, Fire Blast, Flamethrower, Inferno
            (105, 77, [195, 89, 707, 514], "Sea Incense", 70),       // Marowak: Perish Song, Earthquake, Stomping Tantrum (raw 661), Retaliate
            (142, 77, [355, 19, 157, 416], "Choice Scarf", 70),      // Aerodactyl: Roost, Fly, Rock Slide, Giga Impact
            (3, 77, [311, 338, 76, 38], "Dragon Fang", 70),          // Venusaur: Weather Ball, Frenzy Plant, Solar Beam, Double-Edge
            (137, 77, [278, 94, 161, 160], "Electirizer", 70),       // Porygon: Recycle, Psychic, Tri Attack, Conversion
        ];
        for (var slot = 0; slot < party.Length; slot++)
        {
            var mon = session.ReadEntity(-1, slot);
            Assert.Equal(party[slot].Species, mon.Species);
            Assert.Equal(party[slot].Level, mon.Level);
            Assert.Equal(party[slot].Moves, new[] { mon.Move1, mon.Move2, mon.Move3, mon.Move4 });
            Assert.Equal(party[slot].Item, ExpansionData.SharedSince18.ItemName(mon.HeldItem));
            Assert.Equal(party[slot].Friendship, mon.Friendship); // base friendship, from byte 0x08
            Assert.Equal("Jason", mon.OriginalTrainer);           // 7 bytes from 0x13
        }
        // Gift legendaries carry ability slot 2 (bits 24-25 of the ribbon word): Articuno's hidden ability.
        Assert.Equal(144, session.ReadEntity(0, 10).Species);
        Assert.Equal(2, session.GetPotential(0, 10).AbilitySlot);
        Assert.False(session.GetPotential(0, 10).SupportsAbilitySlot);

        for (var box = 0; box < 14; box++)
            Assert.Equal($"BOX{box + 1}", session.GetBoxName(box));
        var boxed = session.Snapshot.Slots.Where(slot => slot.Box >= 0 && slot.Species is not null).ToList();
        Assert.Equal(23, boxed.Count);
        Assert.All(boxed, slot => Assert.False(slot.IsEgg));
        Assert.Equal(bytes, session.Serialize().ToArray());
        Assert.Null(new SaveEngine().AssessLayoutRisk(bytes));

        // Pickers: only the ids every expansion release since 1.8 shares.
        // StoresMove takes PKHeX ids: the shared raw range ends at 847, before the Z-moves.
        Assert.True(session.StoresMove(ExpansionData.Instance.MoveToNational(847)));
        Assert.False(session.StoresMove(622)); // Breakneck Blitz, a Z-move
        Assert.False(session.StoresMove((int)PKHeX.Core.Move.MaxStrike));
        Assert.Equal(828, session.GetItemNames().Count);
    }

    [Fact]
    public void TeamRocketEditionPersistsProvenEditsAndRefusesTheRest()
    {
        if (Sample(TeamRocketFile) is not { } bytes) return;
        var profile = ExpansionProfile.TeamRocketEdition;
        var crobatBefore = DecryptedParty(bytes, profile, 0);
        byte[] changed;
        using (var session = Open(bytes))
        {
            // Box: Omastar.
            session.ApplyEdit(0, 0, new EntityEdit(Nickname: "SHELLY", Level: 80, Move1: 89, HeldItem: 472));
            // Party: Crobat, whose stored stats the expansion tables reproduce.
            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "NOCTURNE", Level: 85, Move4: 94, HeldItem: 472));
            // Party: Houndoom, whose stats this hack changed. Nickname and moves are fine, level is not.
            session.ApplyEdit(-1, 1, new EntityEdit(Nickname: "CERBERUS", Move1: 337));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 1, new EntityEdit(Level: 90)));
            // Unproven fields refuse and leave the record as it was.
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(Ability: 4)));      // Kabutops: Battle Armor
            Assert.Throws<NotSupportedException>(() => session.ApplyPotentialEdit(0, 1, new PotentialEdit(AbilitySlot: 1)));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(Ball: 4)));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(IVs: [0, 0, 0, 0, 0, 0])));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(Nickname: "ELEVENCHARS")));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 1, new EntityEdit(Species: 25)));
            Assert.Throws<NotSupportedException>(() => session.ApplyMoveDetails(0, 1, new MoveDetailsEdit(PPUps: [3, 0, 0, 0])));
            Assert.Throws<NotSupportedException>(() => session.MoveSlot(0, 2, -1, 6));
            Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 29, new EntityEdit(Species: 25)));
            // A party member stored into a box needs no stats.
            session.MoveSlot(-1, 5, 1, 0);
            changed = session.Serialize().ToArray();
        }

        AssertBackupUntouched(bytes, changed);
        using (var reopened = Open(changed))
        {
            Assert.Equal(ExpansionGame.TeamRocketEdition, ExpansionFormat.LooksLike(changed)?.Game); // checksums hold
            var omastar = reopened.ReadEntity(0, 0);
            Assert.Equal(("SHELLY", 80, 89, 472, "Jason"), (omastar.Nickname, omastar.Level, omastar.Move1, omastar.HeldItem, omastar.OriginalTrainer));
            Assert.Equal(10, reopened.GetMoveDetails(0, 0).Moves[0].PP); // Earthquake's PP

            var crobat = reopened.ReadEntity(-1, 0);
            Assert.Equal(("NOCTURNE", 85, 94, 472), (crobat.Nickname, crobat.Level, crobat.Move4, crobat.HeldItem));
            var houndoom = reopened.ReadEntity(-1, 1);
            Assert.Equal(("CERBERUS", 77, 337), (houndoom.Nickname, houndoom.Level, houndoom.Move1));
            Assert.Equal("Kabutops", reopened.ReadEntity(0, 1).Nickname); // the refused edits left it as it was
            Assert.Equal(137, reopened.ReadEntity(1, 0).Species); // Porygon, now boxed
            Assert.Equal(5, reopened.Snapshot.Slots.Count(slot => slot.Box == -1 && slot.Species is not null));
        }

        // Only the proven fields of Crobat moved: the header (0x12-0x1F), friendship and the
        // unknown bytes of substruct 0 and the ribbon word are as the game wrote them.
        var crobatAfter = DecryptedParty(changed, profile, 0);
        Assert.Equal(crobatBefore.AsSpan(0x12, 0x0A).ToArray(), crobatAfter.AsSpan(0x12, 0x0A).ToArray());
        Assert.Equal(crobatBefore.AsSpan(0x1E, 2).ToArray(), crobatAfter.AsSpan(0x1E, 2).ToArray());
        Assert.Equal(crobatBefore.AsSpan(0x28, 4).ToArray(), crobatAfter.AsSpan(0x28, 4).ToArray());
        Assert.Equal(crobatBefore.AsSpan(0x48, 8).ToArray(), crobatAfter.AsSpan(0x48, 8).ToArray());
    }

    // ── Black Pearl Emerald (before 2.1) ──

    [Fact]
    public void BlackPearlLegacyReadsTrueAndPersistsEdits()
    {
        if (Sample(BlackPearlFile) is not { } bytes) return;
        Assert.Equal(ExpansionGame.BlackPearlLegacy, ExpansionFormat.LooksLike(bytes)?.Game);
        byte[] changed;
        using (var session = Open(bytes))
        {
            Assert.Equal("Black Pearl Emerald", session.GameNames[0]);
            Assert.Equal(("Mia", 999_999u), (session.GetTrainer().Name, session.GetTrainer().Money));
            var ralts = session.ReadEntity(-1, 0);
            Assert.Equal((280, 6, "Ralts", 36), (ralts.Species, ralts.Level, ralts.Nickname, ralts.Ability)); // Trace (slot 1)
            Assert.Equal([20, 9, 8, 9, 12, 11], ralts.Stats); // HP, Atk, Def, SpA, SpD, Spe: the stored stats
            // The hack's own item enum: TM01-TM219 sit at 582-800, then the expansion's items resume.
            var items = session.GetItemNames();
            Assert.Equal(975, items.Count);
            Assert.Equal(("TM01", "TM101", "Leftovers"), (items[582], items[682], items[472]));
            Assert.Equal(bytes, session.Serialize().ToArray());
            Assert.Null(new SaveEngine().AssessLayoutRisk(bytes));

            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "KIRLIA", Level: 12, Move2: 94, HeldItem: 682, Ability: 140));
            Assert.True(session.DuplicateSlot(-1, 0, 0, 0));
            session.ApplyEdit(0, 0, new EntityEdit(Nickname: "COPY", Move3: 585, Level: 20));
            changed = session.Serialize().ToArray();
        }

        AssertBackupUntouched(bytes, changed);
        Assert.Equal(ExpansionGame.BlackPearlLegacy, ExpansionFormat.LooksLike(changed)?.Game);
        using var reopened = Open(changed);
        var edited = reopened.ReadEntity(-1, 0);
        Assert.Equal(("KIRLIA", 12, 94, 682, 140), (edited.Nickname, edited.Level, edited.Move2, edited.HeldItem, edited.Ability));
        Assert.Equal("TM101", ExpansionData.BlackPearlLegacy.ItemName(edited.HeldItem));
        var boxed = reopened.ReadEntity(0, 0);
        Assert.Equal(("COPY", 20, 585), (boxed.Nickname, boxed.Level, boxed.Move3));
    }

    // ── Fire Emerald ──

    [Fact]
    public void FireEmeraldReadsTrueAndPersistsPartyAndBoxEdits()
    {
        if (Sample(FireEmeraldFile) is not { } bytes) return;
        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.Equal(ExpansionGame.FireEmerald, profile?.Game);
        Assert.Equal(0xF6C, profile!.SectorDataSize);
        Assert.Equal(0x8344, profile.BoxNamesOffset); // the standard offset, in 0xF6C chunks
        byte[] changed;
        using (var session = Open(bytes))
        {
            Assert.Equal(("Fire Emerald", "Mia", 3000u), (session.GameNames[0], session.GetTrainer().Name, session.GetTrainer().Money));
            for (var box = 0; box < 14; box++)
                Assert.Equal($"BOX{box + 1}", session.GetBoxName(box));
            var charmander = session.ReadEntity(-1, 0);
            Assert.Equal((4, 5, 66), (charmander.Species, charmander.Level, charmander.Ability)); // Blaze
            Assert.Equal([19, 10, 9, 11, 11, 12], charmander.Stats);
            Assert.Equal(bytes, session.Serialize().ToArray());
            Assert.Null(new SaveEngine().AssessLayoutRisk(bytes));
            Assert.Equal(828, session.GetItemNames().Count);

            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "BLAZE", Level: 16, Move4: 53, HeldItem: 472, Ability: 94));
            Assert.True(session.DuplicateSlot(-1, 0, 13, 29)); // the last slot, in the last storage sector (id 13)
            session.ApplyEdit(13, 29, new EntityEdit(Nickname: "BOXED", Move2: 337, Level: 30));
            changed = session.Serialize().ToArray();
        }
        AssertBackupUntouched(bytes, changed);
        Assert.Equal(ExpansionGame.FireEmerald, ExpansionFormat.LooksLike(changed)?.Game);
        using var reopened = Open(changed);
        var edited = reopened.ReadEntity(-1, 0);
        Assert.Equal(("BLAZE", 16, 53, 472, 94), (edited.Nickname, edited.Level, edited.Move4, edited.HeldItem, edited.Ability));
        var boxed = reopened.ReadEntity(13, 29);
        Assert.Equal(("BOXED", 30, 337, 53), (boxed.Nickname, boxed.Level, boxed.Move2, boxed.Move4));
        Assert.Equal("BOX14", reopened.GetBoxName(13));
    }

    // ── Standard expansion hacks named by their SaveBlock2 key ──

    [Theory]
    [InlineData("lazarus.sav", "Lazarus", 0xB0, 2000u)]
    [InlineData("lazarus-pokedex.srm", "Lazarus", 0xB0, 174_506u)]
    [InlineData(Batch + "mgba/mGBA Romhack Saves/Pokemon - Lucid.srm", "Lucid", 0xA4, 997_999u)]
    [InlineData("emerald-enhanced.sav", "Emerald Enhanced", 0x34, 21_874u)]
    [InlineData(Batch + "romhack/romhack saves/Pokemon - Emerald Enhanced.sav", "Emerald Enhanced", 0x34, 3000u)]
    public void NamedStandardHacksReadMoneyWithTheirOwnKey(string file, string name, int keyOffset, uint money)
    {
        if (Sample(file) is not { } bytes) return;
        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        var game = profile.Game;
        Assert.NotEqual(ExpansionGame.StandardExpansion, game);
        Assert.Equal((name, keyOffset), (profile.GameName, profile.EncryptionKeyOffset));
        byte[] changed;
        using (var session = Open(bytes))
        {
            Assert.Equal(name, session.GameNames[0]);
            Assert.Equal(money, session.GetTrainer().Money);
            Assert.Equal(0, session.GetTrainerStats().Coins);
            Assert.Equal(bytes, session.Serialize().ToArray());
            session.SetTrainer(session.GetTrainer() with { Money = 123_456 });
            session.SetTrainerStats(new TrainerStatsEdit(Coins: 321));
            changed = session.Serialize().ToArray();
        }
        AssertBackupUntouched(bytes, changed);
        Assert.Equal(game, ExpansionFormat.LooksLike(changed)?.Game);
        using var reopened = Open(changed);
        Assert.Equal((123_456u, 321), (reopened.GetTrainer().Money, reopened.GetTrainerStats().Coins));
    }

    [Theory]
    [InlineData("emerald-seaglass.sav", 3000u)]
    [InlineData(Batch + "romhack/romhack saves/Pokemon - Emerald Ex.sav", 339_483u)]
    public void UpstreamKeyedSavesKeepTheGenericName(string file, uint money)
    {
        // Seaglass and Emerald Ex keep upstream's key at 0xAC: nothing in their layout tells
        // them from any other expansion hack, so the generic profile reads them.
        if (Sample(file) is not { } bytes) return;
        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.Equal((ExpansionGame.StandardExpansion, "pokeemerald-expansion", 0xAC), (profile?.Game, profile?.GameName, profile?.EncryptionKeyOffset));
        using var session = Open(bytes);
        Assert.Equal(money, session.GetTrainer().Money);
    }

    [Fact]
    public void AnAmbiguousKeyKeepsTheGenericName()
    {
        // A Lazarus save whose upstream key slot (0xAC) is made to decode the money too: two
        // layouts are proven, so the engine does not guess which hack it is.
        if (Sample("lazarus.sav") is not { } bytes) return;
        var sections = ExpansionFormat.SectionOffsets(bytes);
        var key = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sections[0] + 0xB0));
        var copy = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(sections[0] + 0xAC), key);
        ExpansionFormat.WriteChecksum(copy, sections[0], 0xF34);
        var profile = ExpansionFormat.LooksLike(copy);
        Assert.Equal(ExpansionGame.StandardExpansion, profile?.Game);
        Assert.Equal("pokeemerald-expansion", profile?.GameName);
    }

    // ── The shared expansion codec ──

    [Fact]
    public void IvsLiveInTheIvWordAndNaturesUseGen3StatOrder()
    {
        // Lazarus: four of its five party records carry stats the 1.17 tables reproduce once IVs
        // are read from substruct 3's IV word (not pokérus / met data) and the nature boosts the
        // Gen 3 stat it names (Atk, Def, Spe, SpA, SpD). Bayleef (slot 0) is a stat the hack changed.
        if (Sample("lazarus.sav") is not { } bytes) return;
        var profile = ExpansionFormat.LooksLike(bytes)!;
        var sections = ExpansionFormat.SectionOffsets(bytes);
        var saveBlock1 = ExpansionFormat.ReadBlock(bytes, sections, profile, 1, 4, profile.SaveBlock1Size);
        for (var slot = 1; slot < 5; slot++)
        {
            var mon = new ExpansionMon(saveBlock1, profile.PartyOffset + slot * 100, true, ExpansionData.Instance);
            Assert.True(mon.StoredStatsMatchTables, $"party slot {slot}");
        }

        // An IV edit changes only the IV word: met location and level stay as the game wrote them.
        byte[] changed;
        var before = DecryptedParty(bytes, profile, 1);
        using (var session = Open(bytes))
        {
            session.ApplyEdit(-1, 1, new EntityEdit(IVs: [31, 31, 31, 31, 31, 31]));
            Assert.Equal([31, 31, 31, 31, 31, 31], session.ReadEntity(-1, 1).IVs);
            changed = session.Serialize().ToArray();
        }
        var after = DecryptedParty(changed, profile, 1);
        Assert.Equal(before.AsSpan(0x44, 4).ToArray(), after.AsSpan(0x44, 4).ToArray());
        Assert.Equal(0x3FFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(0x48)) & 0x3FFF_FFFFu);
    }
}
