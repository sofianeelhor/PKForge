using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Hall of Fame museum's reading of a save (<see cref="MuseumService"/>) and its sums across
/// saves (<see cref="Museum"/>). The device saves in .local-testdata/thor are read from a temporary
/// copy when present; the original files are never opened for writing.
/// </summary>
public sealed class MuseumServiceTests(ITestOutputHelper output)
{
    private static string? Thor()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, ".local-testdata", "thor");
            if (Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Every device save, copied into a fresh temporary folder first.</summary>
    private static List<(string Name, byte[] Bytes)> DeviceSaves()
    {
        var thor = Thor();
        if (thor is null) return [];
        var temp = Directory.CreateTempSubdirectory("pkforge-museum-");
        try
        {
            var list = new List<(string, byte[])>();
            foreach (var file in Directory.EnumerateFiles(thor, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var copy = Path.Combine(temp.FullName, $"{list.Count}-{Path.GetFileName(file)}");
                File.Copy(file, copy);
                list.Add((Path.GetRelativePath(thor, file), File.ReadAllBytes(copy)));
            }
            return list;
        }
        finally { temp.Delete(recursive: true); }
    }

    private static MuseumHall Hall(string key, ISaveEngineSession session, DateTimeOffset? played = null)
    {
        var reading = MuseumService.Read(session);
        var trainer = session.GetTrainer();
        var held = session.Snapshot.Slots.Count(s => s.Species is not null && !s.IsEgg);
        return new MuseumHall(key, key, reading.Generation, trainer.Name, trainer.TID, trainer.Gender, reading, held, 0, 0, 0, played);
    }

    [Fact]
    public void DeviceSavesReadWithoutChangingAByte()
    {
        var saves = DeviceSaves();
        if (saves.Count == 0) return; // the device copies are not on this machine
        var engine = new SaveEngine();
        var halls = new List<MuseumHall>();
        foreach (var (name, bytes) in saves)
        {
            var before = bytes.ToArray();
            ISaveEngineSession session;
            try { session = engine.OpenSession(bytes, Path.GetFileName(name)); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                output.WriteLine($"{name}: not opened ({error.Message})");
                continue;
            }
            using (session)
            {
                // Reading decrypts some record blocks in place: they must be left as they were.
                var inner = (session as SaveEngineSession)?.SaveFile.Data.ToArray();
                var hall = Hall(name, session);
                if (inner is not null) Assert.Equal(inner, ((SaveEngineSession)session).SaveFile.Data.ToArray());
                halls.Add(hall);
                output.WriteLine($"{name}: gen {hall.Generation} {hall.Trainer} · {hall.PlayTime} · teams {hall.Teams.Count} · champions {hall.Champions} · {hall.FirstClearLine} · badges {hall.BadgesEarned}/{hall.Reading.Badges.Count} · "
                    + string.Join(", ", hall.Reading.Records.Select(r => $"{r.Facility} {r.Label} {r.Value}")));
                Assert.InRange(hall.Reading.PlayMinutes, 0, 59);
                Assert.All(hall.Reading.Records, r => Assert.True(r.Value > 0));
                Assert.All(hall.Teams, t => Assert.InRange(t.Members.Count, 1, 6));
            }
            Assert.Equal(before, bytes); // the bytes handed in are untouched
        }
        Assert.NotEmpty(halls);

        var totals = Museum.Totals(halls);
        Assert.Equal(halls.Count, totals.Halls);
        Assert.Equal(halls.Sum(h => h.Champions), totals.Champions);
        Assert.Equal(halls.Sum(h => h.BadgesEarned), totals.Badges);
        Assert.Equal(halls.Sum(h => h.Pokemon), totals.Pokemon);
        Assert.Equal(halls.Count, Museum.Timeline(halls).Count);
    }

    [Fact]
    public void EmeraldHallOfFameAndFirstClearPlayTimeAreRead()
    {
        var sav = HallOfFameServiceTests.Emerald();
        sav.PlayedHours = 20;
        sav.PlayedMinutes = 5;
        sav.SetRecord((int)RecID3Emerald.ENTERED_HOF, 3);
        sav.SetRecord((int)RecID3Emerald.FIRST_HOF_PLAY_TIME, (12u << 16) | (34u << 8) | 56u);
        sav.SetRecord((int)RecID3Emerald.WON_CONTEST, 7);
        using var session = new SaveEngineSession(sav, null);
        var pk = new PK3 { Species = (ushort)Species.Treecko, CurrentLevel = 50 };
        session.SaveFile.SetPartySlotAtIndex(pk, 0);
        HallOfFameService.RecordParty(session);

        var reading = MuseumService.Read(session);
        Assert.Equal(3, reading.Generation);
        Assert.Equal(3, reading.Clears);
        Assert.Equal("12:34", reading.FirstClearPlayTime);
        Assert.Equal(20, reading.PlayHours);
        Assert.Equal(5, reading.PlayMinutes);
        Assert.Single(reading.Fame!.Teams);
        Assert.Equal(8, reading.Badges.Count);
        Assert.Contains(reading.Records, r => r is { Facility: "Contests", Label: "Won", Value: 7 });
        Assert.DoesNotContain(reading.Records, r => r.Value == 0);
    }

    [Fact]
    public void EmeraldFrontierBestStreakIsTheBestOfEveryModeAndLevel()
    {
        var sav = HallOfFameServiceTests.Emerald();
        var frontier = sav.SmallBlock.BattleFrontier;
        frontier.SetStat(BattleFrontierFacility3.Tower, BattleFrontierBattleMode3.Doubles, BattleFrontierRecordType3.OpenLevel, BattleFrontierStatType3.RecordStreak, 42);
        frontier.SetStat(BattleFrontierFacility3.Tower, BattleFrontierBattleMode3.Singles, BattleFrontierRecordType3.Level50, BattleFrontierStatType3.RecordStreak, 21);
        frontier.SetStat(BattleFrontierFacility3.Pike, BattleFrontierBattleMode3.Singles, BattleFrontierRecordType3.Level50, BattleFrontierStatType3.RecordStreak, 9);
        using var session = new SaveEngineSession(sav, null);
        var records = MuseumService.Read(session).Records;
        Assert.Contains(records, r => r is { Facility: "Battle Tower", Label: "Best streak", Value: 42 });
        Assert.Contains(records, r => r is { Facility: "Battle Pike", Value: 9 });
        Assert.DoesNotContain(records, r => r.Facility == "Battle Dome");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void BlankSavesOfEveryDatedGenerationReadEmpty(int generation)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);
        var reading = MuseumService.Read(session);
        Assert.Equal(generation, reading.Generation);
        Assert.Null(reading.FirstClearDate);
        Assert.Empty(reading.Records);
    }

    [Fact]
    public void Gen6ChampionsCountTheLatestClearNumber()
    {
        using var session = new SaveEngine().OpenBlankSession(6);
        var sav = ((SaveEngineSession)session).SaveFile;
        sav.SetPartySlotAtIndex(new PK6 { Species = (ushort)Species.Froakie, CurrentLevel = 50 }, 0);
        HallOfFameService.RecordParty(session, new DateOnly(2026, 3, 2));
        HallOfFameService.RecordParty(session, new DateOnly(2026, 4, 11));
        var reading = MuseumService.Read(session);
        Assert.Equal(2, reading.Clears);
        Assert.Equal(new DateOnly(2026, 3, 2), reading.FirstClearDate);
    }

    private static MuseumHall Made(string key, DateOnly? first, DateTimeOffset? played, int generation, int clears, int badges, int pokemon, int hours, int minutes)
    {
        var badgeList = Enumerable.Range(0, 8).Select(i => new TrainerBadge(i, $"B{i}", "kanto", i < badges)).ToList();
        var reading = new MuseumReading(generation, null, clears, first, null, badgeList, hours, minutes, []);
        return new MuseumHall(key, key, generation, "RED", 1, 0, reading, pokemon, 0, 0, 0, played);
    }

    [Fact]
    public void TimelinePutsDatedClearsFirstThenByLastPlayed()
    {
        var halls = new[]
        {
            Made("undated-new", null, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), 3, 0, 2, 10, 1, 0),
            Made("dated-late", new DateOnly(2024, 6, 1), null, 6, 1, 8, 200, 40, 30),
            Made("undated-old", null, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, 2, 8, 30, 99, 45),
            Made("dated-early", new DateOnly(2010, 3, 6), null, 4, 1, 16, 400, 120, 50),
        };
        Assert.Equal(["dated-early", "dated-late", "undated-old", "undated-new"], Museum.Timeline(halls).Select(h => h.Key));

        var totals = Museum.Totals(halls);
        Assert.Equal(4, totals.Halls);
        Assert.Equal(4, totals.Champions);
        Assert.Equal(3, totals.ChampionHalls); // the undated save with no clear is not a champion
        Assert.Equal(2 + 8 + 8 + 8, totals.Badges);
        Assert.Equal(640, totals.Pokemon);
        Assert.Equal(262, totals.PlayHours); // 1:00 + 40:30 + 99:45 + 120:50 = 262:05
        Assert.Equal(5, totals.PlayMinutes);
    }

    [Theory]
    [InlineData(0u, null)]
    [InlineData((12u << 16) | (34u << 8) | 56u, "12:34")]
    [InlineData((999u << 16) | (59u << 8), "999:59")]
    [InlineData((1u << 16) | (60u << 8), null)]
    public void Gen3FirstClearPlayTimeUnpacks(uint packed, string? expected) =>
        Assert.Equal(expected, Museum.Gen3PlayTime(packed));
}
