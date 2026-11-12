using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Expansion;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The generic pokeemerald-expansion engine against the local saves. The samples are
/// gitignored player saves, so every fact test skips when the file is absent (CI).
/// These tests prove the envelope/profile detection, the repacked mon codec, the
/// byte-identical no-op round trip, party/box edits surviving a reopen, and that the
/// rotating backup slot is never touched.
/// </summary>
public sealed class ExpansionTests
{
    private static string? RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        return directory?.FullName;
    }

    private static byte[]? Sample(string file)
    {
        var root = RepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root, ".local-testdata", "romhacks", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static ExpansionEngineSession Open(byte[] bytes) => new(bytes, "test");

    [Fact]
    public void HeartAndSoul20DetectsReadsAndPersistsEdits()
    {
        if (Sample("heart-and-soul.sav") is not { } bytes) return;

        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        Assert.Equal(ExpansionGame.HeartAndSoul20, profile!.Game);

        using var session = Open(bytes);
        var shiinotic = session.ReadEntity(-1, 0);
        Assert.False(shiinotic.IsEmpty);
        Assert.Equal(756, shiinotic.Species);
        Assert.Equal("Shiinotic", shiinotic.SpeciesName);
        Assert.Equal(5, shiinotic.Level);
        Assert.Equal("SHIINOTIC", shiinotic.Nickname);
        for (var box = 0; box < 14; box++)
            Assert.Equal($"BOX{box + 1}", session.GetBoxName(box));

        var noOp = session.Serialize().ToArray();
        Assert.Equal(bytes, noOp);

        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "MUSHI", Level: 7));
        session.ApplyEdit(0, 0, new EntityEdit(Species: 1, Level: 5, Nickname: "BULBASAUR"));
        var changed = session.Serialize().ToArray();
        Assert.NotEqual(bytes, changed);

        using var reopened = Open(changed);
        var party = reopened.ReadEntity(-1, 0);
        Assert.Equal("MUSHI", party.Nickname);
        Assert.Equal(7, party.Level);
        var boxed = reopened.ReadEntity(0, 0);
        Assert.Equal(1, boxed.Species);
        Assert.Equal(5, boxed.Level);
        Assert.Equal("BULBASAUR", boxed.Nickname);

        // Only the live slot's sectors may differ: the rotating backup copy is untouched.
        var live = ExpansionFormat.SectionOffsets(bytes).ToHashSet();
        foreach (var (_, offset, _) in ExpansionFormat.PhysicalSections(bytes))
        {
            if (live.Contains(offset)) continue;
            Assert.Equal(bytes.AsSpan(offset, 0x1000).ToArray(), changed.AsSpan(offset, 0x1000).ToArray());
        }
    }

    [Fact]
    public void TheBatchHeartAndSoulSaveIsReadAndEditedLikeTheOthers()
    {
        // This save's player stands at X = 2, which the old "save version" test read as
        // SaveBlock1+0 and refused. Same layout as the other samples: 14 boxes, 3968 bytes.
        if (Sample("batch-2026-11-12/romhack/romhack saves/Pokemon - Heart and Soul.sav") is not { } bytes) return;
        Assert.Equal(ExpansionGame.HeartAndSoul20, ExpansionFormat.LooksLike(bytes)?.Game);

        using var session = Open(bytes);
        var trainer = session.GetTrainer();
        Assert.Equal("Yuki", trainer.Name);
        var larvesta = session.ReadEntity(-1, 0);
        Assert.Equal(636, larvesta.Species);
        Assert.Equal("Larvesta", larvesta.SpeciesName);
        Assert.Equal("LARVESTA", larvesta.Nickname);
        Assert.Equal(6, larvesta.Level);
        Assert.Equal("Yuki", larvesta.OriginalTrainer);
        // Dragon Breath (225, 20 PP) and Spikes (191, 20 PP): the PP the record stores agree.
        Assert.Equal([225, 191, 0, 0], new[] { larvesta.Move1, larvesta.Move2, larvesta.Move3, larvesta.Move4 });
        Assert.True(session.ReadEntity(-1, 1).IsEmpty);
        for (var box = 0; box < 14; box++)
            Assert.Equal($"BOX{box + 1}", session.GetBoxName(box));
        Assert.All(session.Snapshot.Slots.Where(slot => slot.Box >= 0), slot => Assert.Null(slot.Species)); // the PC is empty

        Assert.Equal(bytes, session.Serialize().ToArray());

        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "BLAZE", Level: 9));
        session.ApplyEdit(3, 7, new EntityEdit(Species: 4, Level: 5, Nickname: "CHARMANDER"));
        var changed = session.Serialize().ToArray();
        using var reopened = Open(changed);
        Assert.Equal(ExpansionGame.HeartAndSoul20, ExpansionFormat.LooksLike(changed)?.Game);
        Assert.Equal("BLAZE", reopened.ReadEntity(-1, 0).Nickname);
        Assert.Equal(9, reopened.ReadEntity(-1, 0).Level);
        Assert.Equal(4, reopened.ReadEntity(3, 7).Species);
        Assert.Equal("CHARMANDER", reopened.ReadEntity(3, 7).Nickname);

        var live = ExpansionFormat.SectionOffsets(bytes).ToHashSet();
        foreach (var (_, offset, _) in ExpansionFormat.PhysicalSections(bytes))
        {
            if (live.Contains(offset)) continue;
            Assert.Equal(bytes.AsSpan(offset, 0x1000).ToArray(), changed.AsSpan(offset, 0x1000).ToArray());
        }
    }

    [Fact]
    public void LazarusDetectsDecodesAndRoundTrips()
    {
        if (Sample("lazarus.sav") is not { } bytes) return;

        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        Assert.Equal(ExpansionGame.Lazarus, profile!.Game);

        using var session = Open(bytes);
        (int Species, string Name)[] expected =
        [
            (153, "Bayleef"),
            (670, "Floette"),
            (653, "Fennekin"),
            (570, "Zorua"),
            (906, "Sprigatito"),
        ];
        for (var slot = 0; slot < expected.Length; slot++)
        {
            var detail = session.ReadEntity(-1, slot);
            Assert.False(detail.IsEmpty);
            Assert.Equal(expected[slot].Species, detail.Species);
            Assert.Equal(expected[slot].Name, detail.SpeciesName);
        }

        var occupiedBox = session.Snapshot.Slots.Where(slot => slot.Box >= 0 && slot.Species is not null).ToList();
        Assert.NotEmpty(occupiedBox);
        Assert.All(occupiedBox, slot => Assert.True(slot.Species is > 0));

        var noOp = session.Serialize().ToArray();
        Assert.Equal(bytes, noOp);

        var first = occupiedBox[0];
        session.ApplyEdit(first.Box, first.Slot, new EntityEdit(Nickname: "PKFORGE"));
        var changed = session.Serialize().ToArray();
        using var reopened = Open(changed);
        Assert.Equal("PKFORGE", reopened.ReadEntity(first.Box, first.Slot).Nickname);
    }

    [Fact]
    public void SeaglassDetectsAndReadsTheParty()
    {
        if (Sample("emerald-seaglass.sav") is not { } bytes) return;

        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        Assert.Equal(ExpansionGame.StandardExpansion, profile!.Game);

        using var session = Open(bytes);
        int[] species = [5, 153, 253, 156];
        for (var slot = 0; slot < species.Length; slot++)
        {
            var detail = session.ReadEntity(-1, slot);
            Assert.False(detail.IsEmpty);
            Assert.Equal(species[slot], detail.Species);
        }
        Assert.Equal(bytes, session.Serialize().ToArray());
    }

    [Fact]
    public void EmeraldEnhancedDetectsAndReadsTheParty()
    {
        if (Sample("emerald-enhanced.sav") is not { } bytes) return;

        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        Assert.Equal(ExpansionGame.EmeraldEnhanced, profile!.Game);

        using var session = Open(bytes);
        int[] species = [625, 58, 44, 203, 35, 177];
        for (var slot = 0; slot < species.Length; slot++)
        {
            var detail = session.ReadEntity(-1, slot);
            Assert.False(detail.IsEmpty);
            Assert.Equal(species[slot], detail.Species);
        }
        Assert.Equal(bytes, session.Serialize().ToArray());
    }

    [Fact]
    public void PerfectEmeraldIsDetectedWithFifteenBoxes()
    {
        if (Sample("perfect-emerald.sav") is not { } bytes) return;

        var profile = ExpansionFormat.LooksLike(bytes);
        Assert.NotNull(profile);
        Assert.Equal(ExpansionGame.PerfectEmerald, profile!.Game);

        using var session = Open(bytes);
        for (var box = 0; box < 15; box++)
            Assert.Equal($"Box{box + 1}", session.GetBoxName(box));
        Assert.Equal(bytes, session.Serialize().ToArray());
    }

    [Fact]
    public void RoutingDescribesEveryExpansionSample()
    {
        (string File, string Game)[] expected =
        [
            ("heart-and-soul.sav", "Heart and Soul 2.0"),
            ("lazarus.sav", "Lazarus"),
            ("emerald-seaglass.sav", "pokeemerald-expansion"),
            ("emerald-enhanced.sav", "Emerald Enhanced"),
            ("perfect-emerald.sav", "Perfect Emerald"),
        ];
        foreach (var (file, game) in expected)
        {
            if (Sample(file) is not { } bytes) continue;
            Assert.True(SaveParser.IsPokemonExpansion(bytes), file);
            var description = new SaveEngine().TryDescribe(bytes);
            Assert.NotNull(description);
            Assert.Equal(game, description.GameName);
            var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, file, null, "Save");
            Assert.Equal(SaveFormat.Expansion, guess.Format);
            Assert.Equal(SaveLayoutFamily.Expansion, guess.Family);
            using var session = new SaveEngine().OpenSession(bytes);
            Assert.IsType<ExpansionEngineSession>(session);
        }
    }

    [Fact]
    public void VanillaEmeraldAndFireRedAreNotDetected()
    {
        var party = EncryptedVanillaBulbasaurParty();
        Assert.Null(ExpansionFormat.LooksLike(VanillaEnvelope(party, countOffset: 0x234, partyOffset: 0x238)));
        Assert.Null(ExpansionFormat.LooksLike(VanillaEnvelope(party, countOffset: 0x34, partyOffset: 0x38)));
    }

    private static byte[] EncryptedVanillaBulbasaurParty()
    {
        var pk = new PK3
        {
            Species = 1,
            PID = 0x1234_5678,
            ID32 = 0x9ABC_DEF0,
            Nickname = "Bulbasaur",
            OriginalTrainerName = "TEST",
            Language = 2,
            EXP = 125,
            Ball = 4,
        };
        pk.RefreshChecksum();
        var record = new byte[100];
        pk.WriteEncryptedDataParty(record);
        return record;
    }

    /// <summary>A minimal retail Emerald envelope: two rotating slots, 3968-byte
    /// chunks and the vanilla per-chunk windows (id 4 = 0xF08, id 13 = 0x7D0).</summary>
    private static byte[] VanillaEnvelope(byte[] partyRecord, int countOffset, int partyOffset)
    {
        var data = new byte[0x20000];
        for (var slot = 0; slot < 2; slot++)
        for (var id = 0; id < 14; id++)
        {
            var offset = (slot * 14 + id) * 0x1000;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 0xFF4), (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 0xFF8), 0x0801_2025);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 0xFFC), slot == 0 ? 1u : 0u);
        }
        data[0x1000 + countOffset] = 1;
        partyRecord.CopyTo(data.AsSpan(0x1000 + partyOffset, partyRecord.Length));
        for (var slot = 0; slot < 2; slot++)
        for (var id = 0; id < 14; id++)
        {
            var offset = (slot * 14 + id) * 0x1000;
            var length = id switch
            {
                0 => 0xF2C,
                1 or 2 or 3 => 0xF80,
                4 => 0xF08,
                >= 5 and <= 12 => 0xF80,
                _ => 0x7D0,
            };
            var checksum = ExpansionFormat.Checksum(data.AsSpan(offset, length), length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 0xFF6), checksum);
        }
        return data;
    }
}
