using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.EliteRedux;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon Elite Redux ground truth: the batch sample (kept out of the repository, every case
/// skips without it), save version 1049, trainer Mia, one 20-sector slot rotated by 9 sectors.
/// Party and box expectations were decoded independently from the bytes with the struct of
/// eliteredux-source <c>include/pokemon.h</c>; five of the six party members' stored stats are
/// reproduced exactly by the game's formula with the pinned tables (Lucineon's are not: its
/// base stats changed since the build that wrote the save, which the stat gate must catch).
/// </summary>
public sealed class EliteReduxTests
{
    private static byte[]? Sample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null
            : Path.Combine(directory.FullName, ".local-testdata", "romhacks", "batch-2026-11-12", "saves", "saves", "Pokemon - Elite Redux.sav");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static EliteReduxEngineSession Reopen(ReadOnlyMemory<byte> bytes) => new(bytes);

    private static string Move(int national) => GameInfo.GetStrings("en").movelist[national];

    [Fact]
    public void TheTrainerAndPlayTimeAreRead()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        var trainer = session.GetTrainer();
        Assert.Equal(("Mia", 36900, 10504, 999_900u, 1), (trainer.Name, trainer.TID, trainer.SID, trainer.Money, trainer.Gender));
        Assert.Equal("1:44:05", session.PlayTimeText());
        Assert.Equal(1049, session.SaveVersion);
        Assert.Null(session.ReadOnlyReason);
        Assert.Equal(26, session.BoxCount);
        Assert.Equal(Enumerable.Range(1, 26).Select(i => $"Box{i}"), Enumerable.Range(0, 26).Select(session.GetBoxName));
    }

    [Fact]
    public void ThePartyIsDecodedFieldForField()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);

        var zekrom = session.ReadEntity(-1, 0);
        Assert.Equal((644, "Zekrom", "Zekrom", 20, 13), (zekrom.Species, zekrom.SpeciesName, zekrom.Nickname, zekrom.Level, zekrom.Nature));
        Assert.Equal(["Razor Leaf", "Wing Attack", "Breaking Swipe"], new[] { zekrom.Move1, zekrom.Move2, zekrom.Move3 }.Select(Move));
        Assert.Equal(0, zekrom.Move4); // Triple Tremor is Elite Redux's own move: no PKHeX id
        Assert.Equal("Beast Boost", GameInfo.GetStrings("en").abilitylist[zekrom.Ability]);
        Assert.Equal([76, 71, 59, 53, 51, 51], zekrom.Stats);
        Assert.Equal(76, zekrom.CurrentHp);
        Assert.Equal((int)Ball.Master, zekrom.Ball);
        Assert.Equal("Mia", zekrom.OriginalTrainer);
        Assert.Equal([31, 31, 31, 31, 31, 31], zekrom.IVs);
        Assert.Equal(2, zekrom.Gender);
        Assert.Contains("- Triple Tremor", session.GetShowdownText(-1, 0));
        Assert.Contains("Innates: Teravolt / Transistor / Overwhelm", session.GetShowdownText(-1, 0));

        var latias = session.ReadEntity(-1, 1);
        Assert.Equal((380, 17, 1), (latias.Species, latias.Nature, latias.Gender));
        Assert.Equal([68, 43, 47, 60, 63, 49], latias.Stats);
        Assert.Equal(["Healing Wish", "Dig", "Tearful Look"], new[] { latias.Move1, latias.Move2, latias.Move3 }.Select(Move));

        // Elite Redux's own Eeveelution: no national twin, named from the hack's table.
        var lucineon = session.ReadEntity(-1, 2);
        Assert.Equal((0, "Lucineon", "Lucineon"), (lucineon.Species, lucineon.SpeciesName, lucineon.Nickname));

        Assert.Equal((133, "Eevee-Partner"), (session.ReadEntity(-1, 3).Species, session.ReadEntity(-1, 3).SpeciesName));
        Assert.Equal((608, "Lampent"), (session.ReadEntity(-1, 4).Species, session.ReadEntity(-1, 4).SpeciesName));
        Assert.Equal((281, "Kirlia-Redux"), (session.ReadEntity(-1, 5).Species, session.ReadEntity(-1, 5).SpeciesName));
        Assert.True(session.ReadEntity(-1, 6).IsEmpty);
    }

    [Fact]
    public void BoxesKeepFormsAndHackSpecies()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        var golem = session.ReadEntity(0, 0);
        Assert.Equal((76, 1, "Golem-Alola", 15), (golem.Species, golem.Form, golem.SpeciesName, golem.Level));
        Assert.Equal([5, 12], golem.Types); // Rock / Electric
        var landorus = session.ReadEntity(0, 20);
        Assert.Equal((645, 1, "Landorus-Therian", 20), (landorus.Species, landorus.Form, landorus.SpeciesName, landorus.Level));
        Assert.Equal((0, "Tentagrewl"), (session.ReadEntity(0, 11).Species, session.ReadEntity(0, 11).SpeciesName));
        Assert.Equal(200, session.ReadEntity(0, 18).Friendship); // Minccino

        var snapshot = session.Snapshot;
        Assert.Equal(26 + 6, snapshot.Slots.Count(slot => slot.Species is not null));
        Assert.Equal(26 * 30 + 6, snapshot.Slots.Count);
        Assert.True(session.ReadEntity(1, 0).IsEmpty);
    }

    [Fact]
    public void TheBagIsReadFromItsOwnershipBits()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        var names = session.GetItemNames();
        var bag = session.GetBag().ToDictionary(pouch => pouch.Name, pouch => pouch.Items.Select(item => names[item.Id]).ToList());
        Assert.Equal(27, bag["Balls"].Count);
        Assert.Equal(108, bag["TMHMs"].Count);
        Assert.Contains("Leftovers", bag["BattleItems"]);
        Assert.Contains("Latiasite", bag["MegaStones"]);
        Assert.Contains("Master Ball", bag["Balls"]);
    }

    [Fact]
    public void AnUntouchedSaveRoundTripsByteForByte()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        var engine = new SaveEngine();
        Assert.Null(engine.AssessLayoutRisk(bytes));
        Assert.Null(engine.CheckWriteSafety(bytes, bytes, null));
        // A whole edit/write cycle that ends where it started is byte-identical too.
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Rocky"));
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Golem"));
        Assert.Equal(EliteReduxFormat.Sections(bytes), EliteReduxFormat.Sections(session.Serialize().ToArray()));
    }

    [Fact]
    public void BoxEditsSurviveAReopenWithValidChecksums()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        var leafStorm = (int)PKHeX.Core.Move.LeafStorm;
        session.ApplyEdit(0, 1, new EntityEdit(
            Nickname: "Starry", Level: 42, Nature: 3, HeldItem: ItemId(session, "Leftovers"),
            Move1: leafStorm, EVs: [4, 252, 0, 0, 0, 252], IsShiny: true, Ball: (int)Ball.Ultra, Friendship: 255));
        session.ApplyPotentialEdit(0, 2, new PotentialEdit(AbilitySlot: 2));
        var written = session.Serialize().ToArray();

        Assert.NotNull(EliteReduxFormat.Sections(written)); // every sector still checks out
        Assert.Equal([EliteReduxFormat.StorageFirst], ChangedSections(bytes, written));
        Assert.Null(new SaveEngine().CheckWriteSafety(bytes, written, WriteScope.Only(new SlotRef(0, 1), new SlotRef(0, 2))));

        using var reopened = Reopen(written);
        var star = reopened.ReadEntity(0, 1);
        Assert.Equal(("Starry", 42, 3, "Leftovers", "Leaf Storm", true, (int)Ball.Ultra, 255),
            (star.Nickname, star.Level, star.Nature, reopened.GetItemNames()[star.HeldItem], Move(star.Move1), star.IsShiny, star.Ball, star.Friendship));
        Assert.Equal([4, 252, 0, 0, 0, 252], star.EVs);
        Assert.Equal(398, star.Species);
        Assert.Equal(2, reopened.GetPotential(0, 2).AbilitySlot);
        // Nothing else moved: the other 31 Pokémon read exactly as before.
        using var original = Reopen(bytes);
        foreach (var (box, slot) in Slots().Where(s => s != (0, 1) && s != (0, 2)))
            Assert.Equal(original.ReadEntity(box, slot), reopened.ReadEntity(box, slot), EntityComparer.Instance);
    }

    [Fact]
    public void APartyEditRecomputesStatsTheGameWay()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        session.ApplyEdit(-1, 0, new EntityEdit(Level: 50, EVs: [0, 252, 0, 0, 0, 252]));
        var written = session.Serialize().ToArray();
        Assert.Equal([EliteReduxFormat.SaveBlock1First], ChangedSections(bytes, written));

        using var reopened = Reopen(written);
        var zekrom = reopened.ReadEntity(-1, 0);
        Assert.Equal(50, zekrom.Level);
        // The same formula that reproduced the five stored party stat lines, at level 50, Jolly.
        var expected = reopened.ComputeStats(EliteReduxData.Instance.SpeciesFromNational(644), 50, 13, [0, 252, 0, 0, 0, 252], false);
        Assert.Equal(expected, zekrom.Stats);
        Assert.True(expected[0] > 76 && expected[5] > 51);
        Assert.Equal(expected[0], zekrom.CurrentHp); // full HP stays full (game: hp += newMax - oldMax)
    }

    [Fact]
    public void APartyMemberWhoseStatsTheTablesCannotReproduceIsNotRecomputed()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(-1, 2, new EntityEdit(Level: 30)));
        // Fields that do not touch stats still edit.
        session.ApplyEdit(-1, 2, new EntityEdit(Nickname: "Lumi"));
        using var reopened = Reopen(session.Serialize());
        Assert.Equal(("Lumi", 20), (reopened.ReadEntity(-1, 2).Nickname, reopened.ReadEntity(-1, 2).Level));
    }

    [Fact]
    public void UnstorableChangesAreRefusedAndEchoesChangeNothing()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 0, new EntityEdit(IVs: [0, 31, 31, 31, 31, 31])));
        Assert.Throws<NotSupportedException>(() => session.ApplyEdit(0, 0, new EntityEdit(Gender: 0)));
        Assert.Throws<NotSupportedException>(() => session.MoveSlot(0, 0, -1, 6));
        // The editor re-sends what it read (custom moves read as 0): nothing is written.
        foreach (var (box, slot) in Slots())
        {
            var d = session.ReadEntity(box, slot);
            if (d.IsEmpty) continue;
            session.ApplyEdit(box, slot, new EntityEdit(d.Species, d.Nickname, d.Level, d.Nature, d.Ability, d.HeldItem,
                d.Move1, d.Move2, d.Move3, d.Move4, d.IVs, d.EVs, d.IsShiny, d.Ball, d.OriginalTrainer, d.Gender, d.Friendship));
        }
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    [Fact]
    public void SlotsMoveDepositAndReleaseLikeTheGame()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        session.MoveSlot(0, 0, 0, 1);           // swap Golem and Staraptor
        session.MoveSlot(-1, 5, 1, 0);          // deposit Kirlia
        Assert.True(session.DuplicateSlot(0, 20, 1, 1));
        session.ReleaseSlot(0, 25);
        session.MoveSlot(-1, 0, -1, 1);         // party reorder
        using var reopened = Reopen(session.Serialize());
        Assert.Equal((398, 76), (reopened.ReadEntity(0, 0).Species, reopened.ReadEntity(0, 1).Species));
        Assert.Equal("Kirlia-Redux", reopened.ReadEntity(1, 0).SpeciesName);
        Assert.Equal(20, reopened.ReadEntity(1, 0).Level);
        Assert.Equal(5, reopened.Snapshot.Slots.Count(slot => slot.Box == -1 && slot.Species is not null));
        Assert.Equal(645, reopened.ReadEntity(1, 1).Species);
        Assert.True(reopened.ReadEntity(0, 25).IsEmpty);
        Assert.Equal((380, 644), (reopened.ReadEntity(-1, 0).Species, reopened.ReadEntity(-1, 1).Species));
        Assert.NotNull(EliteReduxFormat.Sections(session.Serialize().ToArray()));
    }

    [Fact]
    public void TrainerMoneyPlayTimeAndBagEditsSurviveAReopen()
    {
        if (Sample() is not { } bytes) return;
        using var session = Reopen(bytes);
        session.SetTrainer(new TrainerInfo("Sofia", 1234, 5678, 123_456, 0));
        session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 12, PlayTimeMinutes: 34, Coins: 500));
        var expShare = ItemId(session, "Exp. Share");
        var abilityCapsule = session.GetPouchLegalItems("Items").First(id => !session.GetBag().Single(p => p.Name == "Items").Items.Any(i => i.Id == id));
        session.SetItemCount("Items", expShare, 0);
        session.SetItemCount("Items", abilityCapsule, 1);
        var written = session.Serialize().ToArray();
        Assert.Equal([EliteReduxFormat.SaveBlock2First, EliteReduxFormat.SaveBlock1First], ChangedSections(bytes, written));

        using var reopened = Reopen(written);
        Assert.Equal(new TrainerInfo("Sofia", 1234, 5678, 123_456, 0), reopened.GetTrainer());
        Assert.StartsWith("12:34:", reopened.PlayTimeText());
        Assert.Equal(500, reopened.GetTrainerStats().Coins);
        var items = reopened.GetBag().Single(p => p.Name == "Items").Items.Select(i => i.Id).ToList();
        Assert.DoesNotContain(expShare, items);
        Assert.Contains(abilityCapsule, items);
    }

    [Fact]
    public void ASaveFromAnotherBuildOpensReadOnly()
    {
        if (Sample() is not { } bytes) return;
        var sections = EliteReduxFormat.Sections(bytes)!;
        var other = bytes.ToArray();
        var at = EliteReduxFormat.SaveVersionOffset;
        var sector = sections[EliteReduxFormat.SaveBlock1First + at / EliteReduxFormat.SectorData];
        BinaryPrimitives.WriteUInt16LittleEndian(other.AsSpan(sector + at % EliteReduxFormat.SectorData), 1048);
        BinaryPrimitives.WriteUInt16LittleEndian(other.AsSpan(sector + 0xFF6), EliteReduxFormat.Checksum(other.AsSpan(sector, EliteReduxFormat.SectorData)));

        Assert.Equal(SaveFormat.EliteRedux, SaveParser.HackRoute(other));
        var engine = new SaveEngine();
        Assert.Equal(LayoutRiskKind.CorruptingLayout, engine.AssessLayoutRisk(other)?.Kind);
        using var session = engine.OpenSession(other, "er");
        Assert.Equal("Zekrom", session.ReadEntity(-1, 0).Nickname);
        Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(0, 0, new EntityEdit(Nickname: "X")));
    }

    [Fact]
    public void DetectionNamesTheGameAndATornSaveIsRefusedByName()
    {
        if (Sample() is not { } bytes) return;
        var engine = new SaveEngine();
        Assert.Equal(SaveFormat.EliteRedux, SaveParser.HackRoute(bytes));
        var description = engine.TryDescribe(bytes, "er.sav");
        Assert.Equal(("Elite Redux", "Mia", "1:44:05", 36900), (description?.GameName, description?.TrainerName, description?.PlayTime, description?.TrainerId));
        var guess = SaveIdentityRules.Guess(description!.GameName, 3, "er.sav", null, "er.sav");
        Assert.Equal(("Pokémon Elite Redux", SaveLayoutFamily.EliteRedux, SaveFormat.EliteRedux), (guess.Label, guess.Family, guess.Format));
        Assert.All(SaveIdentityRules.ChoicesFor(guess.Family, guess.Label), choice => Assert.Equal(SaveFormat.EliteRedux, choice.Format));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, "er", SaveFormat.Standard));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, "er", SaveFormat.Expansion));
        Assert.Null(RecognizedHacks.Identify(bytes));

        // One damaged sector: no engine claims it; it is named and refused, never opened.
        var torn = bytes.ToArray();
        torn[EliteReduxFormat.Sections(bytes)![EliteReduxFormat.StorageFirst] + 0x10] ^= 0x5A;
        Assert.Null(SaveParser.HackRoute(torn));
        Assert.Equal("Elite Redux", RecognizedHacks.Identify(torn)?.Name);
        var refusal = Assert.Throws<InvalidDataException>(() => engine.OpenSession(torn, "er"));
        Assert.Contains("Pokémon Elite Redux save", refusal.Message);
    }

    // ── Helpers ──

    private static IEnumerable<(int Box, int Slot)> Slots() =>
        Enumerable.Range(0, 6).Select(slot => (-1, slot)).Concat(Enumerable.Range(0, 30).Select(slot => (0, slot)));

    private static int ItemId(ISaveEngineSession session, string name) => session.GetItemNames().ToList().IndexOf(name);

    /// <summary>The first logical id of each block whose sectors differ (SaveBlock2, SaveBlock1, storage).</summary>
    private static List<int> ChangedSections(byte[] before, byte[] after)
    {
        var sections = EliteReduxFormat.Sections(before)!;
        var changed = new List<int>();
        foreach (var first in new[] { EliteReduxFormat.SaveBlock2First, EliteReduxFormat.SaveBlock1First, EliteReduxFormat.StorageFirst })
        {
            var last = first switch { EliteReduxFormat.SaveBlock2First => 1, EliteReduxFormat.SaveBlock1First => 7, _ => 19 };
            if (Enumerable.Range(first, last - first + 1).Any(id =>
                    !before.AsSpan(sections[id], EliteReduxFormat.SectorSize).SequenceEqual(after.AsSpan(sections[id], EliteReduxFormat.SectorSize))))
                changed.Add(first);
        }
        // Outside the slot nothing moves at all.
        Assert.True(before.AsSpan(EliteReduxFormat.SlotSectors * EliteReduxFormat.SectorSize).SequenceEqual(after.AsSpan(EliteReduxFormat.SlotSectors * EliteReduxFormat.SectorSize)));
        return changed;
    }

    private sealed class EntityComparer : IEqualityComparer<EntityDetail>
    {
        public static readonly EntityComparer Instance = new();
        public bool Equals(EntityDetail? x, EntityDetail? y) => x is not null && y is not null && Key(x) == Key(y);
        public int GetHashCode(EntityDetail obj) => Key(obj).GetHashCode(StringComparison.Ordinal);
        private static string Key(EntityDetail d) =>
            $"{d.Species}|{d.Form}|{d.Nickname}|{d.Level}|{d.Nature}|{d.Ability}|{d.HeldItem}|{d.Move1}|{d.Move2}|{d.Move3}|{d.Move4}|" +
            $"{string.Join(",", d.IVs)}|{string.Join(",", d.EVs)}|{d.IsShiny}|{d.Ball}|{d.OriginalTrainer}|{d.Gender}|{d.Friendship}|{string.Join(",", d.Stats ?? [])}|{d.CurrentHp}";
    }
}
