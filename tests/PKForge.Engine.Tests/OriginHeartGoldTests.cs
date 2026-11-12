using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.OriginHeartGold;
using PKHeX.Core;
using Xunit;
using static PKForge.Engine.OriginHeartGold.OriginHeartGoldFormat;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon Origin HeartGold ground truth: the real sample in the 2026-11-12 batch (kept out of
/// the repository; every case skips without it). Trainer "Rob", 6 badges, 16:19:51; partition A
/// holds the current copy of both blocks (save count 0x12 against 0x11). Expectations were read
/// with an independent Python PK4 decoder before the engine existed.
/// </summary>
public sealed class OriginHeartGoldTests
{
    private const string SampleFile = "romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav";

    private static string? Local(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", relative);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static byte[]? Sample() => Local(SampleFile) is { } path ? File.ReadAllBytes(path) : null;

    private static OriginHeartGoldEngineSession Open(byte[] bytes) => new(bytes, "Origin HeartGold");

    private static bool BothCopiesValid(byte[] bytes, int partition) =>
        BlockValid(bytes, partition, GeneralSize, GeneralBlockId) && BlockValid(bytes, partition + StorageOffset, StorageSize, StorageBlockId);

    [Fact]
    public void TheEnvelopeIsHeartGoldsWithTheHacksBlockSizes()
    {
        if (Sample() is not { } bytes) return;
        Assert.Equal(FileSize, bytes.Length);
        Assert.True(BothCopiesValid(bytes, 0));
        Assert.True(BothCopiesValid(bytes, PartitionSize));
        var layout = Parse(bytes)!;
        Assert.Equal(0, layout.General);
        Assert.Equal(StorageOffset, layout.Storage);
        Assert.Equal(0x12u, layout.GeneralCount);
        Assert.Equal(0x12u, layout.StorageCount);
        Assert.Null(new SaveEngine().AssessLayoutRisk(bytes));
    }

    [Fact]
    public void TrainerPlayTimeAndBoxesDecode()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        var trainer = session.GetTrainer();
        Assert.Equal("Rob", trainer.Name);
        Assert.Equal(51903, trainer.TID);
        Assert.Equal(46129, trainer.SID);
        Assert.Equal(48054u, trainer.Money);
        Assert.Equal(0, trainer.Gender);
        Assert.Equal("16:19:51", session.PlayTimeText());
        Assert.Equal(4, session.Generation);
        Assert.Equal(BoxCount * SlotsPerBox + 6, session.Snapshot.Slots.Count);
        for (var box = 0; box < BoxCount; box++)
            Assert.Equal($"BOX {box + 1}", session.GetBoxName(box));
    }

    [Fact]
    public void PartyMembersReadWithModernIdsAndTheHacksAbilityField()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);

        var meowth = session.ReadEntity(-1, 0);
        Assert.Equal(52, meowth.Species);
        Assert.Equal("Meowth", meowth.Nickname);
        Assert.Equal(20, meowth.Level);
        Assert.Equal((int)Ability.Technician, meowth.Ability);
        Assert.Equal([(int)Move.FakeOut, (int)Move.Taunt, (int)Move.Feint, (int)Move.Bite], new[] { meowth.Move1, meowth.Move2, meowth.Move3, meowth.Move4 });
        Assert.Equal("Rob", meowth.OriginalTrainer);

        var charmeleon = session.ReadEntity(-1, 1);
        Assert.Equal(5, charmeleon.Species);
        Assert.Equal(22, charmeleon.Level);
        Assert.Equal((int)Move.FireFang, charmeleon.Move1);

        var umbreon = session.ReadEntity(-1, 2);
        Assert.Equal(197, umbreon.Species);
        Assert.Equal(21, umbreon.Level);
        Assert.Equal(608, umbreon.Move2); // Baby-Doll Eyes, a Gen 6 move
        Assert.Equal((int)Ability.Synchronize, umbreon.Ability);

        var steenee = session.ReadEntity(-1, 3);
        Assert.Equal(762, steenee.Species); // a Gen 7 species, nicknamed with its own name
        Assert.Equal("Steenee", steenee.SpeciesName);
        Assert.Equal("Steenee", steenee.Nickname);
        Assert.Equal(22, steenee.Level);
        Assert.Equal((int)Ability.Oblivious, steenee.Ability);
        Assert.Equal(1, steenee.Gender);
        Assert.Equal([(int)Move.SweetScent, (int)Move.MagicalLeaf, (int)Move.RapidSpin, (int)Move.RazorLeaf], new[] { steenee.Move1, steenee.Move2, steenee.Move3, steenee.Move4 });

        var butterfree = session.ReadEntity(-1, 4);
        Assert.Equal(12, butterfree.Species);
        Assert.Equal(21, butterfree.Level);
        Assert.Equal((int)Ability.CompoundEyes, butterfree.Ability);

        var fearow = session.ReadEntity(-1, 5);
        Assert.Equal(22, fearow.Species);
        Assert.Equal(22, fearow.Level);
        Assert.Equal((int)Ability.Sniper, fearow.Ability);

        // Party Pokémon show the stats the game stored, in the app's order.
        Assert.NotNull(meowth.Stats);
        Assert.Equal(6, meowth.Stats!.Count);
        Assert.All(meowth.Stats, stat => Assert.InRange(stat, 1, 999));
    }

    [Fact]
    public void BoxedPokemonReadAndTheAbilitySlotIsNotTheAbility()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        var vullaby = session.ReadEntity(0, 2);
        Assert.Equal(629, vullaby.Species);
        Assert.Equal((int)Ability.Overcoat, vullaby.Ability);
        var cottonee = session.ReadEntity(0, 3);
        Assert.Equal(546, cottonee.Species);
        Assert.Equal((int)Ability.Infiltrator, cottonee.Ability);
        Assert.Equal((int)Move.FairyWind, cottonee.Move3);
        var jigglypuff = session.ReadEntity(0, 5);
        Assert.Equal((int)Ability.Competitive, jigglypuff.Ability);
        var onix = session.ReadEntity(0, 9);
        Assert.Equal(95, onix.Species);
        Assert.Equal("Orochimaru", onix.Nickname);
        Assert.Equal((int)Ability.Sturdy, onix.Ability);
        Assert.Null(onix.Stats); // no base stats are invented for a boxed Pokémon
        var magikarp = session.ReadEntity(0, 0);
        Assert.Equal(129, magikarp.Species);
        Assert.Equal(10, magikarp.Level);

        Assert.True(session.ReadEntity(0, 16).IsEmpty);
        Assert.True(session.ReadEntity(1, 0).IsEmpty);
        Assert.Equal(18, session.Snapshot.Slots.Count(s => s.Box >= 0 && s.Species is not null));
        Assert.Equal(6, session.Snapshot.Slots.Count(s => s.Box == -1 && s.Species is not null));
        Assert.All(session.Snapshot.Slots, s => Assert.True(s.IsLegal));
    }

    [Fact]
    public void EveryRecordMatchesTheEvidenceTheDecoderRestsOn()
    {
        if (Sample() is not { } bytes) return;
        var layout = Parse(bytes)!;
        var records = new List<OriginHeartGoldMon>();
        for (var slot = 0; slot < 6; slot++)
            records.Add(new OriginHeartGoldMon(bytes.AsSpan(layout.General + PartyOffset + slot * PartySize, PartySize)));
        var empty = OriginHeartGoldMon.EmptyBoxRecord();
        for (var box = 0; box < BoxCount; box++)
        for (var slot = 0; slot < SlotsPerBox; slot++)
        {
            var raw = bytes.AsSpan(layout.Storage + box * BoxStride + slot * StoredSize, StoredSize);
            var mon = new OriginHeartGoldMon(raw);
            Assert.True(mon.IsEmpty || mon.LooksValid, $"box {box + 1} slot {slot + 1}");
            if (mon.IsEmpty) Assert.True(raw.SequenceEqual(empty), "an empty slot is the encrypted blank record");
            else records.Add(mon);
        }
        Assert.Equal(24, records.Count);
        foreach (var mon in records)
        {
            // The ability slot byte is 0/1 and the ability id at 0x42 is one of the species'
            // modern abilities: a retail PK4 would hold the ability id at 0x15 and zero at 0x42.
            Assert.InRange(mon.AbilitySlot, 0, 1);
            Assert.Contains(mon.AbilityId, OriginHeartGoldData.Abilities(mon.Species, 0));
            // Species are national: every unnicknamed Pokémon carries its English species name.
            if (!mon.IsNicknamed)
                Assert.Equal(OriginHeartGoldData.SpeciesName(mon.Species).ToUpperInvariant(), mon.Nickname.ToUpperInvariant());
        }
        // The growth rates the boxed levels come from give each party Pokémon its stored level.
        foreach (var mon in records.Where(m => m.Party))
            Assert.Equal(mon.StoredLevel, OriginHeartGoldData.LevelFor(mon.Species, mon.Experience));
    }

    [Fact]
    public void BagReadsWithModernItemIds()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        var bag = session.GetBag().ToDictionary(p => p.Name, p => p.Items);
        Assert.Equal(new BagItem(91, 5), bag["Items"][0]); // Star Piece x5
        Assert.Equal(13, bag["Items"].Count);
        Assert.Contains(new BagItem(744, 1), bag["KeyItems"]); // Mega Bracelet, a Gen 6 key item
        Assert.Equal(7, bag["TMHMs"].Count);
        Assert.Contains(new BagItem(50, 4), bag["Medicine"]); // Rare Candy x4
        Assert.Contains(new BagItem(158, 2), bag["Berries"]); // Sitrus Berry x2
        Assert.Contains(new BagItem(4, 7), bag["Balls"]); // Poké Ball x7
        Assert.Throws<NotSupportedException>(() => session.SetItemCount("Items", 91, 6));
    }

    [Fact]
    public void AnUntouchedSaveRoundTripsByteForByte()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        using var routed = new SaveEngine().OpenSession(bytes, "x");
        Assert.IsType<OriginHeartGoldEngineSession>(routed);
        Assert.True(routed.Serialize().Span.SequenceEqual(bytes));
    }

    [Fact]
    public void BoxEditsSurviveAReopenWithValidCrcsAndKeepTheAbilityBytes()
    {
        if (Sample() is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.ApplyEdit(0, 9, new EntityEdit(Nickname: "Rocky", Friendship: 200, IVs: [31, 30, 29, 28, 27, 26], EVs: [4, 252, 0, 0, 0, 252]));
            written = session.Serialize().ToArray();
        }

        Assert.True(BothCopiesValid(written, 0));
        // Only the current storage copy changed; the general block and the backup partition did not.
        Assert.True(written.AsSpan(0, GeneralSize).SequenceEqual(bytes.AsSpan(0, GeneralSize)));
        Assert.True(written.AsSpan(PartitionSize).SequenceEqual(bytes.AsSpan(PartitionSize)));
        Assert.Equal(AllBoxesChanged, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(StorageOffset + BoxChangedFlagsOffset)));
        var changed = Enumerable.Range(0, FileSize).Where(i => written[i] != bytes[i]).ToList();
        Assert.All(changed, i => Assert.True(
            i is >= StorageOffset + 9 * StoredSize and < StorageOffset + 10 * StoredSize
                || i is >= StorageOffset + BoxChangedFlagsOffset and < StorageOffset + BoxChangedFlagsOffset + 4
                || i >= StorageOffset + StorageSize - 2 && i < StorageOffset + StorageSize, $"byte 0x{i:X} changed"));

        var before = new OriginHeartGoldMon(bytes.AsSpan(StorageOffset + 9 * StoredSize, StoredSize));
        var after = new OriginHeartGoldMon(written.AsSpan(StorageOffset + 9 * StoredSize, StoredSize));
        Assert.Equal(before.AbilitySlot, after.AbilitySlot);
        Assert.Equal(before.AbilityId, after.AbilityId);
        Assert.Equal(before.Moves, after.Moves);
        Assert.Equal(before.Experience, after.Experience);
        Assert.Equal(before.Pid, after.Pid);

        Assert.Equal(SaveFormat.OriginHeartGold, SaveParser.HackRoute(written));
        Assert.Null(new SaveEngine().CheckWriteSafety(bytes, written, new WriteScope([new SlotRef(0, 9)])));
        using var reopened = Open(written);
        var onix = reopened.ReadEntity(0, 9);
        Assert.Equal("Rocky", onix.Nickname);
        Assert.Equal(200, onix.Friendship);
        Assert.Equal([31, 30, 29, 28, 27, 26], onix.IVs);
        Assert.Equal([4, 252, 0, 0, 0, 252], onix.EVs);
        Assert.Equal((int)Ability.Sturdy, onix.Ability);
        Assert.Equal(95, onix.Species);
        Assert.True(reopened.Serialize().Span.SequenceEqual(written));
    }

    [Fact]
    public void PartyNicknameAndTrainerEditsRewriteOnlyTheGeneralBlock()
    {
        if (Sample() is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.ApplyEdit(-1, 3, new EntityEdit(Nickname: "Bounce", Friendship: 120));
            session.SetTrainer(session.GetTrainer() with { Name = "Red", Money = 123456 });
            session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 20, PlayTimeMinutes: 5));
            written = session.Serialize().ToArray();
        }
        Assert.True(BothCopiesValid(written, 0));
        Assert.True(written.AsSpan(StorageOffset, StorageSize).SequenceEqual(bytes.AsSpan(StorageOffset, StorageSize)));
        Assert.True(written.AsSpan(PartitionSize).SequenceEqual(bytes.AsSpan(PartitionSize)));

        using var reopened = Open(written);
        var steenee = reopened.ReadEntity(-1, 3);
        Assert.Equal("Bounce", steenee.Nickname);
        Assert.Equal(120, steenee.Friendship);
        Assert.Equal(22, steenee.Level);
        Assert.Equal((int)Ability.Oblivious, steenee.Ability);
        Assert.Equal(session_stats(bytes), steenee.Stats);
        Assert.Equal("Red", reopened.GetTrainer().Name);
        Assert.Equal(123456u, reopened.GetTrainer().Money);
        Assert.Equal("20:05:51", reopened.PlayTimeText());
        Assert.Equal("Origin HeartGold", new SaveEngine().TryDescribe(written)!.GameName);

        static IReadOnlyList<int>? session_stats(byte[] original)
        {
            using var session = new OriginHeartGoldEngineSession(original);
            return session.ReadEntity(-1, 3).Stats;
        }
    }

    [Fact]
    public void BoxSlotsMoveCopyAndReleaseVerbatim()
    {
        if (Sample() is not { } bytes) return;
        byte[] written;
        using (var session = Open(bytes))
        {
            session.MoveSlot(0, 2, 5, 0); // Vullaby into an empty box
            Assert.True(session.DuplicateSlot(0, 3, 5, 1)); // Cottonee copied
            session.ReleaseSlot(0, 0); // Magikarp released
            written = session.Serialize().ToArray();
        }
        Assert.True(BothCopiesValid(written, 0));
        using var reopened = Open(written);
        Assert.True(reopened.ReadEntity(0, 2).IsEmpty);
        Assert.Equal(629, reopened.ReadEntity(5, 0).Species);
        Assert.Equal(546, reopened.ReadEntity(5, 1).Species);
        Assert.Equal(546, reopened.ReadEntity(0, 3).Species);
        Assert.True(reopened.ReadEntity(0, 0).IsEmpty);
        Assert.True(written.AsSpan(StorageOffset + 5 * BoxStride, StoredSize).SequenceEqual(bytes.AsSpan(StorageOffset + 2 * StoredSize, StoredSize)));
        Assert.True(written.AsSpan(StorageOffset, StoredSize).SequenceEqual(OriginHeartGoldMon.EmptyBoxRecord()));
    }

    [Fact]
    public void UnprovenEditsAreRefusedAndWriteNothing()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        var onix = session.ReadEntity(0, 9);
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(Level: onix.Level + 1)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(Move1: (int)Move.Tackle)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(HeldItem: 1)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(Ability: (int)Ability.RockHead)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(Nickname: "Changed", Species: 1)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 9, new EntityEdit(IsShiny: !onix.IsShiny)));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(IVs: [31, 31, 31, 31, 31, 31])));
        Assert.Throws<NotSupportedException>(() => session.ApplyPotentialEdit(0, 9, new PotentialEdit(AbilitySlot: 0)));
        Assert.Throws<NotSupportedException>(() => session.MoveSlot(0, 9, -1, 0));
        Assert.Throws<NotSupportedException>(() => session.MoveSlot(-1, 0, 1, 0));
        Assert.Throws<NotSupportedException>(() => session.ReleaseSlot(-1, 0));
        Assert.Throws<NotSupportedException>(() => session.SetTrainer(session.GetTrainer() with { TID = 1 }));
        Assert.False(session.RerollNatureKeepShiny(0, 9, 3));
        // An editor echo of every current value is not a change.
        session.ApplyEdit(0, 9, new EntityEdit(onix.Species, onix.Nickname, onix.Level, onix.Nature, onix.Ability, onix.HeldItem,
            onix.Move1, onix.Move2, onix.Move3, onix.Move4, onix.IVs, onix.EVs, onix.IsShiny, onix.Ball, onix.OriginalTrainer, onix.Gender, onix.Friendship));
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    [Fact]
    public void ExportedRecordsReimportIntoAnEmptyBoxSlot()
    {
        if (Sample() is not { } bytes) return;
        using var session = Open(bytes);
        var export = session.ExportSlot(-1, 3);
        Assert.Equal("pk4ohg", export.Format);
        Assert.Equal(StoredSize, export.Data.Length);
        Assert.False(session.ImportSlot(3, 0, export.Data, "pk4"));
        Assert.True(session.ImportSlot(3, 0, export.Data, export.Format));
        using var reopened = Open(session.Serialize().ToArray());
        var steenee = reopened.ReadEntity(3, 0);
        Assert.Equal(762, steenee.Species);
        Assert.Equal(22, steenee.Level);
        Assert.Equal((int)Ability.Oblivious, steenee.Ability);
    }

    [Fact]
    public void ATornCopyFallsBackToTheOtherAndNoValidCopyIsRefusedByName()
    {
        if (Sample() is not { } bytes) return;
        var torn = bytes.ToArray();
        torn[0x100] ^= 0xFF; // partition A's general block no longer matches its CRC
        var layout = Parse(torn)!;
        Assert.Equal(PartitionSize, layout.General);
        Assert.Equal(StorageOffset, layout.Storage);
        using (var session = Open(torn))
            Assert.Equal("Rob", session.GetTrainer().Name);

        torn[PartitionSize + 0x100] ^= 0xFF; // and partition B's
        Assert.Null(SaveParser.HackRoute(torn));
        Assert.Equal("Origin HeartGold", RecognizedHacks.Identify(torn)?.Name);
        var refusal = Assert.Throws<InvalidDataException>(() => new SaveEngine().OpenSession(torn, "x"));
        Assert.Contains("Pokémon Origin HeartGold save", refusal.Message);
    }

    [Fact]
    public void AnEmulatorTrailerKeepsTheRouteAndATruncatedFileIsRefused()
    {
        if (Sample() is not { } bytes) return;
        // DeSmuME appends a 122-byte trailer to a .dsv; the 512 KB image in front of it is untouched.
        var trailed = bytes.Concat(System.Text.Encoding.ASCII.GetBytes("|<--Snip above here to create a raw sav by excluding this DeSmuME savedata footer:"))
            .Concat(new byte[40]).ToArray();
        Assert.Equal(SaveFormat.OriginHeartGold, SaveParser.HackRoute(trailed));
        using (var session = new SaveEngine().OpenSession(trailed, "x"))
        {
            Assert.True(session.Serialize().Span.SequenceEqual(trailed));
            session.ApplyEdit(0, 9, new EntityEdit(Nickname: "Rocky"));
            var written = session.Serialize().ToArray();
            Assert.Equal(trailed.Length, written.Length);
            Assert.True(written.AsSpan(FileSize).SequenceEqual(trailed.AsSpan(FileSize)));
            using var reopened = Open(written);
            Assert.Equal("Rocky", reopened.ReadEntity(0, 9).Nickname);
        }
        foreach (var truncated in new[] { bytes[..^1], bytes[..PartitionSize] })
        {
            Assert.Null(SaveParser.HackRoute(truncated));
            Assert.Throws<InvalidDataException>(() => new SaveEngine().OpenSession(truncated, "x"));
        }
    }

    [Fact]
    public void RetailHeartGoldAndOtherSavesAreNeverClaimed()
    {
        foreach (var file in new[]
                 {
                     "thor/nds/Pokemon - HeartGold Version (axekin.com).sav",
                     "thor/nds/Pokemon - Version Or HeartGold (France).sav",
                     "thor/nds/Pokemon - Platinum Version (Europe).sav",
                     "thor/nds/Pokemon - Version Noire (France) (NDSi Enhanced).sav",
                     "thor/melonds-internal/Pokemon - Version Or HeartGold (France).sav",
                 })
        {
            if (Local(file) is not { } path) continue;
            var bytes = File.ReadAllBytes(path);
            Assert.False(LooksLike(bytes), file);
            Assert.NotEqual(SaveFormat.OriginHeartGold, SaveParser.HackRoute(bytes));
        }

        // Retail HeartGold keeps opening as retail HeartGold.
        if (Local("thor/nds/Pokemon - HeartGold Version (axekin.com).sav") is { } retail)
        {
            var bytes = File.ReadAllBytes(retail);
            Assert.False(LooksLike(bytes));
            Assert.IsType<SAV4HGSS>(SaveUtil.GetSaveFile(bytes));
        }
    }

    [Fact]
    public void TheShelfNamesItAndOffersOnlyItsOwnEngine()
    {
        if (Sample() is not { } bytes) return;
        var engine = new SaveEngine();
        var description = engine.TryDescribe(bytes, "Pokemon - Origin HeartGold Version.sav")!;
        Assert.Equal("Origin HeartGold", description.GameName);
        Assert.Equal(4, description.Generation);
        Assert.Equal("Rob", description.TrainerName);
        Assert.Equal("16:19:51", description.PlayTime);
        Assert.Equal(51903, description.TrainerId);
        Assert.True(engine.Validate(bytes));

        var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, "Pokemon - Origin HeartGold Version.sav", null, "x");
        Assert.Equal("Pokémon Origin HeartGold", guess.Label);
        Assert.Equal(SaveFormat.OriginHeartGold, guess.Format);
        Assert.Equal(SaveLayoutFamily.OriginHeartGold, guess.Family);
        var choice = Assert.Single(SaveIdentityRules.ChoicesFor(guess.Family, guess.Label));
        Assert.Equal(SaveFormat.OriginHeartGold, SaveIdentityRules.FormatOfChoice(choice.Id));
        Assert.True(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(guess, null), guess));

        using (var session = engine.OpenSession(bytes, "x", SaveFormat.OriginHeartGold))
            Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, "x", SaveFormat.Standard));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, "x", SaveFormat.SoulGold));
        Assert.Null(engine.CheckWriteSafety(bytes, bytes, null));
    }
}
