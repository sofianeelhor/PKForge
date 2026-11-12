using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Zygarde Cells, Festival Plaza and Super Training on blank Gen 6/7 saves: every
/// write must match PKHeX's own editor and survive a full serialize and reopen.</summary>
public sealed class Gen67SaveFeatureTests
{
    // ── Zygarde Cells ──

    [Theory]
    [InlineData(GameVersion.SN, 95)]
    [InlineData(GameVersion.US, 100)]
    public void ZygardeCellsListEverySpot(GameVersion version, int spots)
    {
        using var session = Open(version);
        Assert.True(ZygardeCellService.IsSupported(session));
        var cells = ZygardeCellService.Get(session);
        Assert.Equal(spots, cells.Spots.Count);
        Assert.Equal(0, cells.PickedUp);
        Assert.DoesNotContain(cells.Spots, s => s.Location.StartsWith("Spot ", StringComparison.Ordinal));
    }

    [Fact]
    public void ZygardeCollectAllSunMoonRaisesBothCounters()
    {
        using var session = Open(GameVersion.SN);
        ZygardeCellService.SetState(session, 3, ZygardeCellService.StateReceived);
        ZygardeCellService.SetCounters(session, 1, 1);

        Assert.Equal(94, ZygardeCellService.CollectAll(session));

        using var reloaded = Reopen(session);
        var cells = ZygardeCellService.Get(reloaded);
        Assert.Equal((95, 95, 95), (cells.PickedUp, cells.Collected, cells.Total));
    }

    [Fact]
    public void ZygardeCollectAllUltraKeepsTotalAndSyncsRecord()
    {
        using var session = Open(GameVersion.US);
        ZygardeCellService.CollectAll(session);

        using var reloaded = Reopen(session);
        var cells = ZygardeCellService.Get(reloaded);
        Assert.Equal((100, 100, 0), (cells.PickedUp, cells.Collected, cells.Total));
        Assert.Equal(100, ((SAV7)((SaveEngineSession)reloaded).SaveFile).GetRecord(ZygardeCellService.RecordCellsUsum));
    }

    [Fact]
    public void ZygardeResetPutsCellsBackAsAvailable()
    {
        using var session = Open(GameVersion.MN);
        ZygardeCellService.CollectAll(session);
        Assert.Equal(95, ZygardeCellService.Reset(session));

        using var reloaded = Reopen(session);
        var cells = ZygardeCellService.Get(reloaded);
        Assert.All(cells.Spots, s => Assert.Equal(ZygardeCellService.StateAvailable, s.State));
        Assert.Equal((0, 0), (cells.Collected, cells.Total));
    }

    // ── Festival Plaza ──

    [Theory]
    [InlineData(GameVersion.SN, 125)]
    [InlineData(GameVersion.UM, 128)]
    public void FestivalFacilityTypesMatchPkhexRange(GameVersion version, int count)
    {
        using var session = Open(version);
        var types = FestivalPlazaService.GetFacilityTypes(session);
        Assert.Equal(count, types.Count);
        Assert.Equal("Lottery · Big Dream · Lv. 1", types[0]);
        // SAV_FestivalPlaza.TypeIndexToType boundaries: 0x0F Haunted, 0x2F Food, 0x65 Dye.
        Assert.StartsWith("Haunted", types[0x0F]);
        Assert.StartsWith("Food", types[0x2F]);
        Assert.StartsWith("Dye", types[0x65]);
        Assert.Equal(7, FestivalPlazaService.Get(session).Facilities.Count);
    }

    [Fact]
    public void FestivalCoinsRankRewardsAndPhrasesRoundTrip()
    {
        using var session = Open(GameVersion.US);
        var sav = (SAV7)((SaveEngineSession)session).SaveFile;
        sav.SetRecord(FestivalPlazaService.RecordCoinsUsed, 500);

        FestivalPlazaService.SetCoins(session, 99_999_999);
        FestivalPlazaService.SetRank(session, 5000);
        FestivalPlazaService.SetAllRewards(session, FestivalPlazaService.RewardReceived);
        FestivalPlazaService.UnlockAllPhrases(session);

        using var reloaded = Reopen(session);
        var info = FestivalPlazaService.Get(reloaded);
        Assert.Equal(FestivalPlazaService.MaxCoins, info.Coins);
        Assert.Equal(500, info.CoinsUsed);
        Assert.Equal(FestivalPlazaService.MaxCoins, info.CoinsCollected); // used + current, capped by JoinFesta7
        Assert.Equal(999, info.Rank);
        Assert.Equal("287951 - ", info.RankCoinRange);
        Assert.All(info.Rewards, r => Assert.Equal(FestivalPlazaService.RewardReceived, r.State));
        Assert.Equal(info.PhraseCount, info.PhrasesUnlocked);
    }

    [Fact]
    public void FestivalFacilityTypeClampsColourAndColourRoundTrips()
    {
        using var session = Open(GameVersion.UM);
        // Dye (8 colours), last colour; then Lottery (4 colours) clamps to its last one.
        var dye = FestivalPlazaService.GetFacilityTypes(session).ToList().FindIndex(t => t.StartsWith("Dye", StringComparison.Ordinal));
        FestivalPlazaService.SetFacilityType(session, 2, dye);
        FestivalPlazaService.SetFacilityColor(session, 2, 7);
        var lottery = FestivalPlazaService.SetFacilityType(session, 2, 1);
        Assert.Equal(3, lottery.Color);
        Assert.Equal("Black", lottery.ColorName);
        Assert.Throws<ArgumentOutOfRangeException>(() => FestivalPlazaService.SetFacilityColor(session, 2, 4));
        FestivalPlazaService.SetFacilityColor(session, 2, 1);

        using var reloaded = Reopen(session);
        var facility = FestivalPlazaService.Get(reloaded).Facilities[2];
        Assert.Equal((1, 0, 2, 1, "Blue"), (facility.Type, facility.Kind, facility.Level, facility.Color, facility.ColorName));
    }

    [Fact]
    public void ExchangeFacilityIsUltraOnly()
    {
        using var session = Open(GameVersion.SN);
        Assert.Throws<ArgumentOutOfRangeException>(() => FestivalPlazaService.SetFacilityType(session, 0, 125));
    }

    // ── Super Training: save ──

    [Theory]
    [InlineData(GameVersion.X)]
    [InlineData(GameVersion.OR)]
    public void SuperTrainingBagsUnlocksAndRecordsRoundTrip(GameVersion version)
    {
        using var session = Open(version);
        var block = ((ISaveBlock6Main)((SaveEngineSession)session).SaveFile).SuperTrain;
        block.SetTime1(4, 12.5f);
        block.GetHolder1(4).Species = (ushort)Species.Pikachu;

        Assert.True(SuperTrainingService.AddBag(session, (int)SuperTrainBag.HPBagL));
        Assert.True(SuperTrainingService.AddBag(session, (int)SuperTrainBag.SoothingBag));
        Assert.True(SuperTrainingService.AddBag(session, (int)SuperTrainBag.Strength));
        SuperTrainingService.RemoveBag(session, 0);
        SuperTrainingService.UnlockAllStages(session);

        using var reloaded = Reopen(session);
        var info = SuperTrainingService.Get(reloaded);
        Assert.Equal(32, info.StagesUnlocked);
        Assert.Equal("Soothing Bag", info.Bags[0].Name);
        Assert.Equal("Strength Bag", info.Bags[1].Name);
        Assert.Equal(0, info.Bags[2].Bag);
        Assert.Equal((12.5f, "Pikachu"), (info.Records[4].Time1, info.Records[4].Holder1));
        var reloadedBlock = ((ISaveBlock6Main)((SaveEngineSession)reloaded).SaveFile).SuperTrain;
        Assert.False(reloadedBlock.GetIsDistributionUnlocked(0));

        SuperTrainingService.ClearRecord(reloaded, 4);
        Assert.False(SuperTrainingService.Get(reloaded).Records[4].HasRecord);
    }

    [Fact]
    public void SuperTrainingBagsStopAtTwelve()
    {
        using var session = Open(GameVersion.Y);
        for (var i = 0; i < SuperTrainingService.BagSlots; i++)
            Assert.True(SuperTrainingService.AddBag(session, 1));
        Assert.False(SuperTrainingService.AddBag(session, 1));
    }

    [Fact]
    public void SuperTrainingIsXyOrasOnly()
    {
        using var sunMoon = Open(GameVersion.SN);
        using var xy = Open(GameVersion.X);
        using var oras = Open(GameVersion.OR);
        Assert.False(SuperTrainingService.IsSupported(sunMoon));
        Assert.False(ZygardeCellService.IsSupported(xy));
        Assert.False(FestivalPlazaService.IsSupported(oras));
    }

    // ── Super Training: medals ──

    [Fact]
    public void Gen6PokemonCompleteAllIsSupremelyTrainedAndLegal()
    {
        using var session = Open(GameVersion.X);
        PlaceMon(session, GameVersion.X);
        Assert.Equal(0, SuperTrainingService.GetMedals(session, 0, 0)!.Medals);

        var medals = SuperTrainingService.CompleteAll(session, 0, 0);
        Assert.Equal((30, true, true), (medals.Medals, medals.SecretUnlocked, medals.SupremelyTrained));

        using var reloaded = Reopen(session);
        var pk = ((SaveEngineSession)reloaded).SaveFile.GetBoxSlotAtIndex(0, 0);
        Assert.Equal(0, ((PK6)pk).DistTrainBitFlags);
        AssertMedalsLegal(pk);
    }

    [Fact]
    public void Gen6PokemonRegularOnlyIsNotSupremelyTrained()
    {
        using var session = Open(GameVersion.AS);
        PlaceMon(session, GameVersion.AS);
        var medals = SuperTrainingService.CompleteRegular(session, 0, 0);
        Assert.Equal((18, 0, true, false), (medals.Regular, medals.Secret, medals.SecretUnlocked, medals.SupremelyTrained));
        AssertMedalsLegal(((SaveEngineSession)session).SaveFile.GetBoxSlotAtIndex(0, 0));

        var cleared = SuperTrainingService.ClearAll(session, 0, 0);
        Assert.Equal((0, false, false), (cleared.Medals, cleared.SecretUnlocked, cleared.SupremelyTrained));
    }

    [Fact]
    public void Gen7PokemonFromGen6GetsMedalsWithoutSecretFlags()
    {
        using var session = Open(GameVersion.SN);
        PlaceMon(session, GameVersion.X);
        var medals = SuperTrainingService.CompleteAll(session, 0, 0);
        Assert.Equal((30, false, false, false), (medals.Medals, medals.SecretUnlocked, medals.SupremelyTrained, medals.HasSecretFlags));
        AssertMedalsLegal(((SaveEngineSession)session).SaveFile.GetBoxSlotAtIndex(0, 0));
    }

    [Fact]
    public void NativeGen7PokemonCannotTrain()
    {
        using var session = Open(GameVersion.MN);
        PlaceMon(session, GameVersion.MN);
        Assert.False(SuperTrainingService.GetMedals(session, 0, 0)!.CanTrain);
        Assert.Throws<InvalidOperationException>(() => SuperTrainingService.CompleteAll(session, 0, 0));
    }

    [Fact]
    public void OtherFormatsHaveNoMedals()
    {
        using var session = new SaveEngine().OpenBlankSession(8);
        Assert.Null(SuperTrainingService.GetMedals(session, 0, 0));
    }

    // ── Helpers ──

    private static void AssertMedalsLegal(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        Assert.DoesNotContain(la.Results, r => r.Identifier == CheckIdentifier.Training && !r.Valid);
    }

    /// <summary>A plain level 50 Pikachu caught in <paramref name="origin"/>, stored in box 1 slot 1.</summary>
    private static void PlaceMon(SaveEngineSession session, GameVersion origin)
    {
        var save = session.SaveFile;
        var pk = save.BlankPKM;
        pk.Species = (ushort)Species.Pikachu;
        pk.CurrentLevel = 50;
        pk.Version = origin;
        pk.Language = (int)LanguageID.English;
        pk.OriginalTrainerName = "PKForge";
        pk.ID32 = save.ID32;
        pk.MetLevel = 50;
        pk.Ball = (byte)Ball.Poke;
        pk.Nickname = SpeciesName.GetSpeciesName(pk.Species, pk.Language);
        pk.RefreshChecksum();
        save.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
    }

    private static SaveEngineSession Open(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    /// <summary>What the app writes, reopened as the same save type (a PKHeX blank carries no
    /// signature the parser recognises, so the type is named rather than sniffed).</summary>
    private static SaveEngineSession Reopen(SaveEngineSession session)
    {
        var bytes = session.Serialize().ToArray();
        var type = session.SaveFile.GetType();
        var reloaded = (SaveFile)(type.GetConstructor([typeof(Memory<byte>)]) is { } memory
            ? memory.Invoke([new Memory<byte>(bytes)])
            : Activator.CreateInstance(type, bytes)!);
        return new SaveEngineSession(reloaded, null);
    }
}
