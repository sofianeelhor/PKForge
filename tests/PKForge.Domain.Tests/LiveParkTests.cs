using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The living Poképark widget's film: who is on the grass, what they do, that every loop is seamless,
/// and that the film fits the launcher's bitmap allowance.
/// </summary>
public sealed class LiveParkTests
{
    private static readonly ParkLiveClip[] Clips =
    [
        new(LivePark.WalkEast, [new(0, 0, 10, 20, 5, 19, 133), new(11, 0, 10, 20, 5, 19, 166)]),
        new(LivePark.WalkWest, [new(0, 21, 10, 20, 5, 19, 133), new(11, 21, 10, 20, 5, 19, 166)]),
        new(LivePark.IdleSouth, [new(0, 42, 10, 20, 5, 19, 500), new(11, 42, 10, 20, 5, 19, 500)]),
    ];

    private static ParkWidgetResident Mon(int i, bool live = true) =>
        new($"id{i}", $"Mon{i}", i + 1, 0, false, $"{i}.png", live ? $"{i}-live.png" : null, live ? Clips : null);

    private static ParkWidgetSnapshot Park(int residents, bool live = true) =>
        new([.. Enumerable.Range(0, residents).Select(i => Mon(i, live))], ["Mon0 ate a berry."], DateTimeOffset.UnixEpoch);

    [Fact]
    public void EveryoneSleepsAtNight()
    {
        var view = LivePark.Compose(Park(4), ParkWidgetSize.Wide, 180, 88, new DateTime(2026, 11, 12, 23, 30, 0));
        Assert.Equal(4, view.Actors.Count);
        Assert.All(view.Actors, a => Assert.Equal(LiveActivity.Sleep, a.Activity));
        Assert.All(view.Actors, a => Assert.True(LivePark.Pose(a, 3, LivePark.Frames).Asleep));
    }

    [Fact]
    public void AResidentWithoutItsAnimationsOnlyStands()
    {
        var day = Enumerable.Range(0, 48).Select(h => new DateTime(2026, 11, 12, 9, 0, 0).AddMinutes(30 * h))
            .Where(t => ParkCare.PhaseAt(t) == ParkDayPhase.Day);
        foreach (var time in day)
            Assert.Equal(LiveActivity.Idle, LivePark.ActivityFor(Mon(0, live: false), time));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    [InlineData(6)]
    public void AWalkerComesBackWhereItStarted(int frames)
    {
        var walker = new LiveActor(Mon(0), LiveActivity.Walk, 60, 70, Rightward: true);
        var poses = Enumerable.Range(0, frames + 1).Select(f => LivePark.Pose(walker, f, frames)).ToList();
        Assert.Equal(poses[0].X, poses[frames].X);
        // It moves at most one stride a frame: no jump at the loop's seam.
        for (var f = 0; f < frames; f++) Assert.InRange(Math.Abs(poses[f + 1].X - poses[f].X), 0, LivePark.Stride);
        Assert.Contains(poses, p => p.Clip == LivePark.WalkEast);
        Assert.Contains(poses, p => p.Clip == LivePark.WalkWest);
    }

    [Fact]
    public void AClipRunsAWholeNumberOfTimesPerLoop()
    {
        // 299 ms a cycle, 2160 ms a loop: 7 cycles, each frame once per cycle, the loop starting on frame 0.
        var clip = Clips[0];
        Assert.Equal(clip.Frames[0], LivePark.FrameAt(clip, 0, 2160));
        Assert.Equal(clip.Frames[1], LivePark.FrameAt(clip, 140.0 / (7 * 299), 2160));
        Assert.Equal(clip.Frames[0], LivePark.FrameAt(clip, 310.0 / (7 * 299), 2160));
    }

    [Fact]
    public void AMissingSitOrSleepStandsIdle() =>
        Assert.Equal(LivePark.IdleSouth, LivePark.Resolve(Clips, LivePark.Sleep)!.Name);

    [Fact]
    public void AnEmptyParkSaysSoAtEverySize()
    {
        foreach (var size in Enum.GetValues<ParkWidgetSize>())
        {
            var view = LivePark.Compose(ParkWidgetSnapshot.Empty, size, 160, 88, new DateTime(2026, 11, 12, 12, 0, 0));
            Assert.Empty(view.Actors);
            Assert.Single(view.Lines);
        }
    }

    [Fact]
    public void TheLargeWidgetShowsMoreResidents()
    {
        var time = new DateTime(2026, 11, 12, 12, 0, 0);
        Assert.Equal(2, LivePark.Compose(Park(10), ParkWidgetSize.Small, 84, 84, time).Actors.Count);
        Assert.Equal(7, LivePark.Compose(Park(10), ParkWidgetSize.Large, 158, 150, time).Actors.Count);
    }

    [Fact]
    public void TheFilmFitsTheAllowance()
    {
        const long budget = 6_000_000;
        var (frames, scale) = LivePark.Budget(309, 88, 4, budget);
        Assert.True((long)frames * 309 * scale * 88 * scale * 4 <= budget);
        Assert.Equal(LivePark.Frames, frames);
        Assert.Equal(2, scale);
        var (few, small) = LivePark.Budget(158, 150, 4, 1_000_000);
        Assert.True((long)few * 158 * small * 150 * small * 4 <= 1_000_000);
        Assert.InRange(few, 1, LivePark.Frames);
    }
}
