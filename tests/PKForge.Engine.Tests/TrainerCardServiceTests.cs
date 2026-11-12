using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class TrainerCardServiceTests
{
    // Gen 1: an international Red/Blue (the generic blank is a international Red layout tagged English,
    // which a clone reads back with the international offsets). Gen 3: a full-size Emerald, since
    // the sector-only blank cannot be written and has no Hall of Fame sectors.
    private static ISaveEngineSession Blank(int generation) => generation switch
    {
        1 => new SaveEngineSession(new SAV1(LanguageID.English, GameVersion.RB), null),
        3 => new SaveEngineSession(HallOfFameServiceTests.Emerald(), null),
        _ => new SaveEngine().OpenBlankSession(generation),
    };

    // Blank saves lack the footers a full write needs, so reopen from a clone of the save bytes.
    private static ISaveEngineSession Reopen(ISaveEngineSession session) =>
        new SaveEngineSession(((SaveEngineSession)session).SaveFile.Clone(), null);

    [Theory]
    [InlineData(1, 8, "Boulder")]
    [InlineData(2, 16, "Zephyr")]
    [InlineData(3, 8, "Stone")]
    [InlineData(4, 8, "Coal")]
    [InlineData(5, 8, "Basic")]
    [InlineData(6, 8, "Stone")]
    [InlineData(8, 8, "Grass")]
    public void BadgesRoundTripThroughTheSaveBytes(int generation, int count, string first)
    {
        using var session = Blank(generation);
        var before = TrainerCardService.Get(session);
        Assert.True(before.SupportsBadges);
        Assert.Equal(count, before.Badges.Count);
        Assert.Equal(first, before.Badges[0].Name);

        var wanted = TrainerBadgeBits.FirstN(3, count).ToArray();
        wanted[count - 1] = true;
        TrainerCardService.Apply(session, new TrainerCardEdit(Badges: wanted));

        using var reopened = Reopen(session);
        var after = TrainerCardService.Get(reopened);
        Assert.Equal(wanted, after.Badges.Select(b => b.Obtained));
        Assert.Equal(4, after.BadgeCount);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void GamesWithoutABadgeCaseRefuseBadges(int generation)
    {
        using var session = Blank(generation);
        var card = TrainerCardService.Get(session);
        Assert.False(card.SupportsBadges);
        Assert.Throws<NotSupportedException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(Badges: [true])));
    }

    [Fact]
    public void WrongBadgeCountIsRefusedBeforeAnythingIsWritten()
    {
        using var session = Blank(4);
        Assert.Throws<ArgumentException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(Badges: [true], RivalName: "BARRY")));
        Assert.NotEqual("BARRY", TrainerCardService.Get(session).RivalName);
    }

    [Theory]
    [InlineData(1, "GARY")]
    [InlineData(2, "SILVER")]
    [InlineData(4, "Barry")]
    [InlineData(5, "Hugh")]
    public void RivalNameRoundTrips(int generation, string name)
    {
        using var session = Blank(generation);
        var card = TrainerCardService.Get(session);
        Assert.True(card.SupportsRivalName);
        Assert.True(card.RivalNameMaxLength >= name.Length);

        TrainerCardService.Apply(session, new TrainerCardEdit(RivalName: name));
        using var reopened = Reopen(session);
        Assert.Equal(name, TrainerCardService.Get(reopened).RivalName);
    }

    [Fact]
    public void RivalNameLongerThanTheGameAllowsIsRefused()
    {
        using var session = Blank(1);
        var max = TrainerCardService.Get(session).RivalNameMaxLength;
        Assert.Throws<ArgumentException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(RivalName: new string('A', max + 1))));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void GamesWithoutAStoredRivalReportItUnsupported(int generation)
    {
        using var session = Blank(generation);
        Assert.False(TrainerCardService.Get(session).SupportsRivalName);
        Assert.Throws<NotSupportedException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(RivalName: "X")));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void ThreeDsGeoRoundTripsWithNames(int generation)
    {
        using var session = Blank(generation);
        Assert.True(TrainerCardService.Get(session).Geo.SupportsConsoleRegion);

        // 49 = United States, 12 = a subregion PKHeX names; console region 1 = Americas.
        TrainerCardService.Apply(session, new TrainerCardEdit(Country: 49, Region: 12, ConsoleRegion: 1));
        using var reopened = Reopen(session);
        var geo = TrainerCardService.Get(reopened).Geo;
        Assert.Equal((49, 12, 1), (geo.Country, geo.Region, geo.ConsoleRegion));
        Assert.Equal("United States", geo.CountryName);
        Assert.NotNull(geo.RegionName);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void DsGeonetBytesRoundTrip(int generation)
    {
        using var session = Blank(generation);
        var before = TrainerCardService.Get(session).Geo;
        Assert.True(before.Supported);
        Assert.False(before.SupportsConsoleRegion);

        TrainerCardService.Apply(session, new TrainerCardEdit(Country: 8, Region: 3));
        using var reopened = Reopen(session);
        var geo = TrainerCardService.Get(reopened).Geo;
        Assert.Equal((8, 3), (geo.Country, geo.Region));
    }

    [Fact]
    public void InvalidConsoleRegionIsRefused()
    {
        using var session = Blank(7);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(ConsoleRegion: 3)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    public void GeoIsOnlyOfferedWhereTheSaveStoresIt(int generation)
    {
        using var session = Blank(generation);
        Assert.False(TrainerCardService.Get(session).Geo.Supported);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void StartAndFameDatesRoundTrip(int generation)
    {
        using var session = Blank(generation);
        var start = new DateTime(2013, 10, 12, 9, 30, 15);
        var fame = new DateTime(2014, 1, 2, 18, 5, 0);
        TrainerCardService.Apply(session, new TrainerCardEdit(StartDate: start, HallOfFameDate: fame));

        using var reopened = Reopen(session);
        var card = TrainerCardService.Get(reopened);
        Assert.Equal(start, card.StartDate.Value);
        Assert.Equal(fame, card.HallOfFameDate.Value);
    }

    [Fact]
    public void SwordShieldStartDateKeepsTheDayOnly()
    {
        using var session = Blank(8);
        var card = TrainerCardService.Get(session);
        Assert.True(card.StartDate.Supported);
        Assert.False(card.StartDate.HasTime);
        Assert.False(card.HallOfFameDate.Supported);

        TrainerCardService.Apply(session, new TrainerCardEdit(StartDate: new DateTime(2019, 11, 15, 8, 0, 0)));
        using var reopened = Reopen(session);
        Assert.Equal(new DateTime(2019, 11, 15), TrainerCardService.Get(reopened).StartDate.Value);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void LastSavedRoundTripsToTheMinute(int generation)
    {
        using var session = Blank(generation);
        TrainerCardService.Apply(session, new TrainerCardEdit(LastSaved: new DateTime(2020, 5, 6, 7, 8, 59)));
        using var reopened = Reopen(session);
        Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 0), TrainerCardService.Get(reopened).LastSaved.Value);
    }

    [Fact]
    public void ScarletVioletLastSavedRoundTrips()
    {
        using var session = Blank(9);
        var value = new DateTime(2023, 2, 3, 4, 5, 6);
        TrainerCardService.Apply(session, new TrainerCardEdit(LastSaved: value));
        using var reopened = Reopen(session);
        Assert.Equal(value, TrainerCardService.Get(reopened).LastSaved.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void GameBoyEraSavesHaveNoDates(int generation)
    {
        using var session = Blank(generation);
        var card = TrainerCardService.Get(session);
        Assert.False(card.StartDate.Supported);
        Assert.False(card.LastSaved.Supported);
        Assert.Throws<NotSupportedException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(StartDate: new DateTime(2005, 1, 1))));
    }

    [Fact]
    public void DatesOutsideTheStoredRangeAreRefused()
    {
        using var session = Blank(6);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrainerCardService.Apply(session, new TrainerCardEdit(StartDate: new DateTime(1999, 12, 31))));
    }

    [Fact]
    public void UltraSunUltraMoonFlyDestinationsRoundTrip()
    {
        using var session = Blank(7);
        var card = TrainerCardService.Get(session);
        Assert.True(card.SupportsFlyDestinations);
        Assert.Equal(46, card.FlyDestinations.Count);
        Assert.Equal("My House", card.FlyDestinations[0].Name);
        Assert.DoesNotContain(card.FlyDestinations, d => d.Name.StartsWith('#'));

        var wanted = card.FlyDestinations.Select(d => d.Index % 3 == 0).ToArray();
        TrainerCardService.Apply(session, new TrainerCardEdit(FlyDestinations: wanted));
        using var reopened = Reopen(session);
        Assert.Equal(wanted, TrainerCardService.Get(reopened).FlyDestinations.Select(d => d.Unlocked));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(8)]
    public void FlyDestinationsAreAlolaOnly(int generation)
    {
        using var session = Blank(generation);
        Assert.False(TrainerCardService.Get(session).SupportsFlyDestinations);
    }
}
