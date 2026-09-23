using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Game-to-game transfers as TransferService drives them, in both directions: forward
/// through PKHeX's official routes, backwards through <see cref="CrossFormatConverter"/>'s
/// sanitized downgrade (with warnings). Only physically impossible transfers refuse, and
/// they say why. Every fixture is a PKHeX corpus file or built in memory.
/// </summary>
public sealed class CrossGenerationTransferTests
{
    private static string Corpus(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "Legality", relative);
    }

    private static byte[] Fixture(string relative) => File.ReadAllBytes(Corpus(relative));

    /// <summary>A Gen 6 Torchic caught in X, carrying a move, ball and item Emerald never had.</summary>
    private static byte[] Gen6Torchic()
    {
        var pk = new PK6
        {
            Species = (ushort)Species.Torchic,
            CurrentLevel = 12,
            Version = GameVersion.X,
            Language = (int)LanguageID.English,
            OriginalTrainerName = "Serena",
            TID16 = 12345,
            SID16 = 54321,
            Ball = (byte)Ball.Dusk,
            HeldItem = 538, // Eviolite: Gen 5+
            MetLocation = 8,
            MetLevel = 5,
            PID = 0x1234_5678,
            EncryptionConstant = 0x8765_4321,
            Nature = Nature.Adamant,
            Gender = 1,
            CurrentFriendship = 70,
        };
        pk.ClearNickname();
        pk.SetMoves([(ushort)Move.Scratch, (ushort)Move.Growl, (ushort)Move.FlameCharge, (ushort)Move.QuickAttack]);
        pk.RefreshAbility(2); // Speed Boost: Hidden Ability, does not exist in Gen 3
        pk.SetIVs([31, 31, 31, 31, 31, 31]);
        pk.EV_SPE = 252;
        pk.ResetPartyStats();
        pk.HealPP();
        pk.RefreshChecksum();
        return pk.Data.ToArray();
    }

    private static PKM Landed(ISaveEngineSession session, int box, int slot) => ((SaveEngineSession)session).GetEntity(box, slot);

    private static void AssertStorable(PKM pk, ISaveEngineSession target, ushort species)
    {
        Assert.Equal(species, pk.Species);
        Assert.True(pk.ChecksumValid, "checksum must be valid");
        Assert.True(pk.Valid, "entity must decode as valid");
        Assert.True(pk.Species <= pk.MaxSpeciesID);
        Assert.All(new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }, m => Assert.True(m <= pk.MaxMoveID, $"move {m} beyond Gen {pk.Format}"));
        Assert.NotEqual(0, pk.Move1);
        Assert.True(pk.HeldItem <= pk.MaxItemID);
        Assert.Equal(species, target.Snapshot.Slots.Single(s => s.Box == 0 && s.Slot == 0).Species);
    }

    /// <summary>The field report: Torchic from X/Y into Emerald lands, sanitized, with warnings.</summary>
    [Fact]
    public void Gen6TorchicDowngradesIntoEmerald()
    {
        var bytes = Gen6Torchic();
        using var target = new SaveEngine().OpenBlankSession(3);

        var preview = new TransferPreviewService(new LegalityService()).Preview(target, 0, 0, bytes);
        Assert.NotNull(preview);
        Assert.True(preview.Backwards);
        Assert.Contains(preview.Warnings, w => w.Contains("Backwards transfer"));
        Assert.Contains(preview.Warnings, w => w.Contains("Flame Charge"));
        Assert.Contains(preview.Warnings, w => w.Contains("Hidden Ability"));
        Assert.Contains(preview.Warnings, w => w.Contains("Held item"));
        Assert.Contains(preview.Warnings, w => w.Contains("Dusk Ball"));
        Assert.NotEmpty(preview.Changes);

        var pk = Assert.IsType<PK3>(Landed(target, 0, 0));
        AssertStorable(pk, target, (ushort)Species.Torchic);
        Assert.False(pk.HasMove((ushort)Move.FlameCharge));
        Assert.True(pk.HasMove((ushort)Move.Scratch));
        Assert.Equal(0, pk.HeldItem);
        Assert.Equal((byte)Ball.Poke, pk.Ball);
        Assert.Equal((int)Ability.Blaze, pk.Ability);
        Assert.Equal(Nature.Adamant, pk.Nature);   // re-rolled PID keeps the nature...
        Assert.Equal(1, pk.Gender);                // ...and the gender
        Assert.False(pk.IsShiny);
        Assert.Equal(GameVersion.E, pk.Version);
        Assert.Equal("Serena", pk.OriginalTrainerName);
        Assert.Equal("TORCHIC", pk.Nickname);
        Assert.Equal(31, pk.IV_SPE);
    }

    /// <summary>Only the previewed transfer flow may downgrade: a plain import (.pk file,
    /// box copy) has no way to show the compromises, so it keeps refusing.</summary>
    [Fact]
    public void PlainImportRefusesADowngrade()
    {
        using var target = new SaveEngine().OpenBlankSession(3);

        Assert.False(target.ImportSlot(0, 0, Gen6Torchic()));
        Assert.Null(target.Snapshot.Slots.Single(s => s.Box == 0 && s.Slot == 0).Species);
    }

    /// <summary>Newer formats (7/8/9) into Gen 3/4 whenever the species exists there.</summary>
    [Theory]
    [InlineData("Legal/Generation 7 Transfer/151 - ミュウ - 6B9DADB23EB0.pk7", 3, 151, 0)]
    [InlineData("Legal/Generation 7 Transfer/466 - Electivire - FF102D8B375B.pk7", 4, 466, 0)]
    [InlineData("Illegal/Misc/146-01 - Moltres - F9E5ADA9EFD1 no overworld, bad genloc.pk8", 3, 146, 0)]
    [InlineData("Illegal/Misc/146-01 - Moltres - F9E5ADA9EFD1 no overworld, bad genloc.pk8", 4, 146, 0)]
    [InlineData("Legal/General/0089-01_-_Muk_-_07FBB3839D09 move bleed HOME minimize initial GO.pk9", 4, 89, 0)]
    [InlineData("Legal/General/0089-01_-_Muk_-_07FBB3839D09 move bleed HOME minimize initial GO.pk9", 3, 89, 0)]
    [InlineData("Legal/General/645-01 - Landorus - 57BA472C4EB8.pk6", 5, 645, 1)]
    public void NewerFormatsDowngradeWhenTheSpeciesExists(string fixture, int generation, ushort species, byte form)
    {
        var bytes = Fixture(fixture);
        using var target = new SaveEngine().OpenBlankSession(generation);
        Assert.NotNull(((SaveEngineSession)target).ImportSlotWithReport(0, 0, bytes, out _));
        var pk = Landed(target, 0, 0);
        Assert.Equal(generation, pk.Format);
        AssertStorable(pk, target, species);
        Assert.Equal(form, pk.Form); // Galarian Moltres / Alolan Muk fall back to the base form; B2W2 has Therian Landorus
    }

    /// <summary>A species newer than the target generation is the one physical refusal, named exactly.</summary>
    [Fact]
    public void Gen9OnlySpeciesIsRefusedWithTheReason()
    {
        var bytes = Fixture("Legal/Generation 9/0904 - Overqwil - 2F49E4F1319B inherit barb 2.pk9");
        using var target = new SaveEngine().OpenBlankSession(3);

        Assert.False(target.ImportSlot(0, 0, bytes));
        Assert.Null(((SaveEngineSession)target).ImportSlotWithReport(0, 0, bytes, out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("Overqwil", refusal);
        Assert.Contains("Generation 3", refusal);
        Assert.Contains("386", refusal);

        var explained = TransferCompatibility.ExplainRefusal(bytes, "Overqwil", target.Snapshot.Format, target.Snapshot.Generation, "Pokémon Emerald");
        Assert.NotNull(explained);
        Assert.Contains("Pokémon Emerald", explained);
        Assert.Contains("No. 904", explained);
        Assert.DoesNotContain("backwards", explained);
    }

    /// <summary>Downgrades are no longer refused, so the compatibility check has nothing to say.</summary>
    [Fact]
    public void DowngradeIsNoLongerARefusal()
    {
        var bytes = Fixture("Legal/General/645-01 - Landorus - 57BA472C4EB8.pk6");
        using var target = new SaveEngine().OpenBlankSession(5);
        Assert.Null(TransferCompatibility.ExplainRefusal(bytes, "Landorus", target.Snapshot.Format, target.Snapshot.Generation, "Pokémon Black 2"));
        Assert.Null(TransferCompatibility.ExplainRefusal(Gen6Torchic(), "Torchic", "Gen3", 3, "Pokémon Emerald"));
    }

    [Fact]
    public void Gen3EntityIsNotReportedAsARefusal()
    {
        var bytes = Fixture("Illegal/Moves/292 - SHEDINJA - 4E10A0E852EE (two Ninjask Moves).pk3");
        using var target = new SaveEngine().OpenBlankSession(6);
        Assert.True(target.ImportSlot(0, 0, bytes));
        Assert.Null(TransferCompatibility.ExplainRefusal(bytes, "Shedinja", target.Snapshot.Format, target.Snapshot.Generation, "Pokémon X"));
    }

    /// <summary>Virtual Console both ways: Gen 2 up to Gen 7 officially, Gen 7 VC back down to Gen 2.</summary>
    [Fact]
    public void Gen2AndGen7VirtualConsoleGoBothWays()
    {
        var gen2 = Fixture("Illegal/Gen2/107 - HITMONCHAN - 26B0.pk2");
        using (var sun = new SaveEngine().OpenBlankSession(7))
        {
            var conversion = ((SaveEngineSession)sun).ImportSlotWithReport(0, 0, gen2, out _);
            Assert.NotNull(conversion);
            Assert.False(conversion.Backwards);
            AssertStorable(Landed(sun, 0, 0), sun, 107);
        }

        var vc = Fixture("Legal/Generation 7 Transfer/251 ★ - Celebi - 6695A818ECEA badPP.pk7");
        using var crystal = new SaveEngine().OpenBlankSession(2);
        var down = ((SaveEngineSession)crystal).ImportSlotWithReport(0, 0, vc, out _);
        Assert.NotNull(down);
        Assert.True(down.Backwards);
        var pk = Assert.IsType<PK2>(Landed(crystal, 0, 0));
        AssertStorable(pk, crystal, 251);
        Assert.True(pk.IsShiny); // the star survives as shiny DVs
        Assert.Contains(down.Warnings, w => w.Contains("DVs"));
    }

    /// <summary>Gen 3 → Gen 6 (official) → Gen 3 (downgrade) keeps the identity: species,
    /// PID-linked traits, trainer, and a valid, storable entity.</summary>
    [Fact]
    public void RoundTripGen3ThroughGen6KeepsTheIdentity()
    {
        var original = Fixture("Legal/Generation 3/Misc/054 - PSYDUCK - 1FE1F661FBE0.pk3");
        var pk3 = (PK3)EntityFormat.GetFromBytes(original, EntityContext.Gen3)!;

        using var x = new SaveEngine().OpenBlankSession(6);
        Assert.True(x.ImportSlot(0, 0, original));
        var up = x.ExportSlot(0, 0).Data;

        using var emerald = new SaveEngine().OpenBlankSession(3);
        Assert.NotNull(((SaveEngineSession)emerald).ImportSlotWithReport(0, 0, up, out _));
        var back = Assert.IsType<PK3>(Landed(emerald, 0, 0));
        AssertStorable(back, emerald, pk3.Species);
        Assert.Equal(pk3.PID, back.PID);
        Assert.Equal(pk3.Nature, back.Nature);
        Assert.Equal(pk3.Gender, back.Gender);
        Assert.Equal(pk3.IsShiny, back.IsShiny);
        Assert.Equal(pk3.Ability, back.Ability);
        Assert.Equal(pk3.Version, back.Version);
        Assert.Equal(pk3.OriginalTrainerName, back.OriginalTrainerName);
        Assert.Equal(pk3.TID16, back.TID16);
        Assert.Equal(pk3.IV_ATK, back.IV_ATK);
    }

    /// <summary>The downgrade is analyzed like any transfer: a concrete verdict, worded as
    /// "likely flagged" when the checker objects - never a refusal.</summary>
    [Fact]
    public void DowngradeVerdictIsLikelyFlaggedButTransferable()
    {
        var bytes = Gen6Torchic();
        using var target = new SaveEngine().OpenBlankSession(3);
        var preview = new TransferPreviewService(new LegalityService()).Preview(target, 0, 0, bytes);
        Assert.NotNull(preview);
        Assert.NotEqual(TransferLegality.Unknown, preview.Legality);
        if (preview.Legality == TransferLegality.Illegal)
            Assert.Contains("Likely flagged", preview.Verdict);
    }
}
