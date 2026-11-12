using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Wild visitors: who may come in where, catching one fairly, the real weather and the
/// screensaver's timing.</summary>
public sealed class ParkCatchTests
{
    private static readonly ParkMap Home = ParkMap.Park;

    [Fact]
    public void Wild_kinds_fit_the_place_and_the_time()
    {
        var volcano = ParkLife.WildKinds("volcano", ParkDayPhase.Day, ParkWeather.Sunny).Select(k => k.Species).ToArray();
        Assert.Contains(218, volcano); // Slugma
        Assert.DoesNotContain(361, volcano); // no Snorunt on the volcano
        var frost = ParkLife.WildKinds("frost", ParkDayPhase.Day, ParkWeather.Sunny).Select(k => k.Species).ToArray();
        Assert.Contains(361, frost);
        var homeNight = ParkLife.WildKinds("home", ParkDayPhase.Night, ParkWeather.Sunny).Select(k => k.Species).ToArray();
        Assert.Contains(163, homeNight); // Hoothoot at night
        Assert.DoesNotContain(16, homeNight); // no Pidgey at night
        Assert.Contains(16, ParkLife.WildKinds("home", ParkDayPhase.Day, ParkWeather.Sunny).Select(k => k.Species));
        Assert.Contains(194, ParkLife.WildKinds("coast", ParkDayPhase.Day, ParkWeather.Rain).Select(k => k.Species)); // Wooper in the rain
        Assert.DoesNotContain(194, ParkLife.WildKinds("coast", ParkDayPhase.Day, ParkWeather.Sunny).Select(k => k.Species));
        // An unknown place has the everyday ones.
        Assert.Equal(ParkLife.WildKinds("home", ParkDayPhase.Day, ParkWeather.Sunny), ParkLife.WildKinds("nowhere", ParkDayPhase.Day, ParkWeather.Sunny));
    }

    [Fact]
    public void Visitors_that_come_in_are_of_the_kinds_their_place_allows()
    {
        var life = new ParkLife(Home, [new("eevee", "Eevee", 133, [ParkType.Normal], ParkPersonality.Calm)], seed: 4);
        var checkedAny = false;
        for (long t = 0; t < 3 * 60 * 60 * 1000; t += 500)
        {
            life.Advance(t);
            foreach (var actor in life.Actors.Where(a => ParkLife.IsVisitor(a.Resident.Id) && !ParkLife.IsKeeper(a.Resident.Id) && life.OldFriendOf(a.Resident.Id) is null))
            {
                if (life.CatchTarget(actor.Resident.Id) is not { } target) continue;
                var all = new[] { ParkDayPhase.Day, ParkDayPhase.Night }.SelectMany(p => new[] { ParkWeather.Sunny, ParkWeather.Rain }
                    .SelectMany(w => ParkLife.WildKinds(target.Place, p, w).Concat(ParkLife.WildKinds("home", p, w)))).Select(k => k.Species);
                Assert.Contains(target.Species, all);
                checkedAny = true;
            }
        }
        Assert.True(checkedAny);
    }

    [Fact]
    public void The_hinted_approach_is_the_best_one_and_patience_helps()
    {
        foreach (var personality in Enum.GetValues<ParkPersonality>())
        {
            var liked = ParkCatch.Likes(personality);
            Assert.NotEqual(liked, ParkCatch.Dislikes(personality));
            foreach (var approach in Enum.GetValues<ParkApproach>())
                Assert.True(ParkCatch.CatchOdds(personality, liked, 0) >= ParkCatch.CatchOdds(personality, approach, 0));
            Assert.True(ParkCatch.CatchOdds(personality, liked, 1) > ParkCatch.CatchOdds(personality, liked, 0));
            Assert.NotEmpty(ParkCatch.Hint("Pidgey", personality));
        }
        Assert.Equal(ParkCatchOutcome.Caught, ParkCatch.Resolve(ParkPersonality.Shy, ParkApproach.Wait, 0, 69, 0));
        Assert.Equal(ParkCatchOutcome.Wary, ParkCatch.Resolve(ParkPersonality.Shy, ParkApproach.Wait, 0, 70, 99));
        Assert.Equal(ParkCatchOutcome.Fled, ParkCatch.Resolve(ParkPersonality.Shy, ParkApproach.Ball, 0, 99, 10));
        // The last try either works or it leaves.
        Assert.Equal(ParkCatchOutcome.Fled, ParkCatch.Resolve(ParkPersonality.Shy, ParkApproach.Wait, ParkCatch.Tries - 1, 99, 99));
    }

    [Fact]
    public void Levels_are_low_at_home_and_higher_in_the_wilder_places()
    {
        for (var roll = 0; roll < 50; roll++)
        {
            Assert.InRange(ParkCatch.Level("home", roll), 2, 6);
            Assert.InRange(ParkCatch.Level("volcano", roll), 8, 20);
        }
    }

    [Fact]
    public void Catching_a_visitor_ends_in_it_coming_along_or_leaving_within_three_tries()
    {
        var life = new ParkLife(Home, [new("eevee", "Eevee", 133, [ParkType.Normal], ParkPersonality.Calm)], seed: 4);
        string? id = null;
        for (long t = 0; t < 3 * 60 * 60 * 1000 && id is null; t += 500)
        {
            life.Advance(t);
            id = life.Actors.Select(a => a.Resident.Id).FirstOrDefault(i => life.CatchTarget(i) is not null);
        }
        Assert.NotNull(id);
        var target = life.CatchTarget(id!)!;
        Assert.Equal(ParkCatch.Tries, target.TriesLeft);
        Assert.Equal(target.Level, life.CatchTarget(id!)!.Level); // a stable level
        ParkCatchResult? result = null;
        for (var i = 0; i < ParkCatch.Tries; i++)
        {
            result = life.TryCatch(id!, ParkCatch.Likes(target.Personality));
            if (result.Outcome != ParkCatchOutcome.Wary) break;
        }
        Assert.NotEqual(ParkCatchOutcome.Wary, result!.Outcome);
        Assert.Null(life.CatchTarget(id!));
        // A caught or startled visitor is gone soon after, and is never saved.
        for (long t = 3 * 60 * 60 * 1000; t < 3 * 60 * 60 * 1000 + 120_000; t += 500) life.Advance(t);
        if (result.Outcome == ParkCatchOutcome.Caught) Assert.Null(life[id!]);
        Assert.DoesNotContain(life.Snapshot(DateTimeOffset.Now), c => c.Id == id);
    }

    [Fact]
    public void Old_friends_and_Blissey_cannot_be_caught()
    {
        var life = new ParkLife(Home, [new("eevee", "Eevee", 133, [ParkType.Normal], ParkPersonality.Calm)], seed: 1);
        Assert.Null(life.CatchTarget("eevee"));
        Assert.Null(life.CatchTarget("wild:friend:abc:1"));
        Assert.Equal(ParkCatchOutcome.Fled, life.TryCatch("eevee", ParkApproach.Berry).Outcome);
    }

    [Fact]
    public void Real_weather_maps_codes_parses_answers_and_caches_an_hour()
    {
        Assert.Equal(ParkWeather.Sunny, ParkRealWeather.FromWmo(0));
        Assert.Equal(ParkWeather.Cloudy, ParkRealWeather.FromWmo(3));
        Assert.Equal(ParkWeather.Cloudy, ParkRealWeather.FromWmo(45));
        Assert.Equal(ParkWeather.Rain, ParkRealWeather.FromWmo(61));
        Assert.Equal(ParkWeather.Rain, ParkRealWeather.FromWmo(73));
        Assert.Equal(ParkWeather.Storm, ParkRealWeather.FromWmo(95));
        Assert.Equal(ParkWeather.Rain, ParkRealWeather.ParseCurrent("""{"current":{"time":"2026-11-11T10:00","weather_code":63}}"""));
        Assert.Null(ParkRealWeather.ParseCurrent("not json"));
        Assert.Null(ParkRealWeather.ParseCurrent("{}"));
        var place = ParkRealWeather.ParsePlace("""{"results":[{"name":"Lyon","latitude":45.75,"longitude":4.85}]}""");
        Assert.Equal(("Lyon", 45.75, 4.85), place);
        Assert.Null(ParkRealWeather.ParsePlace("""{"generationtime_ms":0.1}"""));
        // Only the city name, and only coordinates, go out.
        Assert.Equal("name=Saint%20%C3%89tienne", ParkRealWeather.GeocodeUrl(" Saint Étienne ").Query.Split('&').Last());
        Assert.Equal("?latitude=45.75&longitude=4.85&current=weather_code", ParkRealWeather.ForecastUrl(45.75, 4.85).Query);
        var now = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        var reading = new ParkWeatherReading("Lyon", 45.75, 4.85, ParkWeather.Rain, now);
        Assert.True(ParkRealWeather.Fresh(reading, "lyon", now.AddMinutes(59)));
        Assert.False(ParkRealWeather.Fresh(reading, "Lyon", now.AddMinutes(61)));
        Assert.False(ParkRealWeather.Fresh(reading, "Paris", now));
        // Offline: a stale reading falls back to the park's own sky; a picked sky always wins.
        Assert.Equal(ParkWeather.Rain, ParkRealWeather.SkyFor(null, reading, now.AddHours(2), ParkWeather.Sunny));
        Assert.Equal(ParkWeather.Sunny, ParkRealWeather.SkyFor(null, reading, now.AddHours(4), ParkWeather.Sunny));
        Assert.Equal(ParkWeather.Storm, ParkRealWeather.SkyFor(ParkWeather.Storm, reading, now, ParkWeather.Sunny));
        Assert.Equal(ParkWeather.Cloudy, ParkRealWeather.SkyFor(null, null, now, ParkWeather.Cloudy));
    }

    [Fact]
    public void The_screensaver_sleeps_after_its_minutes_and_slows_down()
    {
        Assert.False(ParkScreensaver.ShouldSleep(0, 0, long.MaxValue / 2));
        Assert.False(ParkScreensaver.ShouldSleep(2, 0, 119_999));
        Assert.True(ParkScreensaver.ShouldSleep(2, 0, 120_000));
        Assert.True(ParkScreensaver.FrameStride(true, false, 0) > ParkScreensaver.FrameStride(false, false, 0));
        Assert.True(ParkScreensaver.FrameStride(true, false, 1) > ParkScreensaver.FrameStride(true, false, 0));
        Assert.Equal(1, ParkScreensaver.FrameStride(false, true, 0));
    }
}
