using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The pure half of the three new home-screen widgets: the Pokéwalker's steps counted in memory
/// (never saved), today's pedometer steps, the partner's mood, and how close an egg is.
/// </summary>
public sealed class HomeWidgetsTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static WalkerState Walking(int steps = 0, int watts = 0, long? sensor = null, DateTimeOffset? strollLast = null)
    {
        var state = WalkerRules.Start(new WalkerState(Watts: watts), WalkerCourses.All[0], "id", "Pikachu", 25, 0, false, null, [13], 7u, Morning);
        state = state with { SensorLast = sensor, StrollLast = strollLast ?? Morning };
        return steps > 0 ? state with { Walk = state.Walk! with { Steps = steps } } : state;
    }

    [Fact]
    public void Pedometer_steps_are_counted_without_touching_the_anchor_the_walker_keeps()
    {
        var state = Walking(sensor: 1000);
        var projected = WalkerWidget.Project(state, 1400, Morning);
        Assert.Equal(400, projected.Walk!.Steps);
        Assert.Equal(20, projected.Watts);
        // The widget never moves SensorLast: the Pokéwalker counts the same steps for real later.
        Assert.Equal(1000, projected.SensorLast);
        Assert.Equal(1000, state.SensorLast);
    }

    [Fact]
    public void Without_a_reading_the_pedometer_walk_stays_as_saved()
    {
        var state = Walking(sensor: 1000);
        Assert.Same(state, WalkerWidget.Project(state, null, Morning));
    }

    [Fact]
    public void Stroll_mode_counts_the_hours_since_the_last_look()
    {
        var projected = WalkerWidget.Project(Walking(), null, Morning.AddHours(2));
        Assert.Equal(2 * WalkerRules.StrollStepsPerHour, projected.Walk!.Steps);
        var view = WalkerWidget.View(projected, null, DateOnly.FromDateTime(Morning.Date));
        Assert.Equal(1200, view.TodaySteps);
    }

    [Fact]
    public void Today_starts_at_the_first_reading_of_the_day_and_survives_a_restart()
    {
        var day = new DateOnly(2026, 11, 12);
        var (count, today) = WalkerWidget.Today(null, 5000, day);
        Assert.Equal(0, today);
        (count, today) = WalkerWidget.Today(count, 5600, day);
        Assert.Equal(600, today);
        // The phone restarted: the counter starts again from zero, the morning's 600 are kept.
        (count, today) = WalkerWidget.Today(count, 150, day);
        Assert.Equal(750, today);
        (_, today) = WalkerWidget.Today(count, 9000, day.AddDays(1));
        Assert.Equal(0, today);
    }

    [Fact]
    public void The_alert_follows_what_the_Pokewalker_would_accept()
    {
        Assert.False(WalkerWidget.CanRadar(Walking(steps: 5000, watts: 9)));
        Assert.True(WalkerWidget.CanRadar(Walking(steps: 5000, watts: 10)));
        Assert.True(WalkerWidget.CanDowse(Walking(steps: 5000, watts: 3)));
        Assert.False(WalkerWidget.CanDowse(new WalkerState(Watts: 99)));
        Assert.False(WalkerWidget.View(new WalkerState(Watts: 99), null, default).Alert);
    }

    [Theory]
    [InlineData(23, 80, false, PartnerMood.Asleep)]
    [InlineData(3, 5, false, PartnerMood.Asleep)]
    [InlineData(13, 10, false, PartnerMood.Tired)]
    [InlineData(13, 10, true, PartnerMood.Happy)]
    [InlineData(7, 90, false, PartnerMood.Sleepy)]
    [InlineData(16, null, false, PartnerMood.Awake)]
    public void The_partner_sleeps_at_night_and_tires_on_a_low_battery(int hour, int? battery, bool charging, PartnerMood mood) =>
        Assert.Equal(mood, PartnerWidget.MoodAt(new DateTime(2026, 11, 12, hour, 0, 0), battery, charging));

    private static EggWidgetSnapshot Save(params EggWidgetEgg[] eggs) => new("doc", "HeartGold · LYRA", true, false, [], eggs, Morning);

    [Theory]
    [InlineData(0, EggStage.Ready)]
    [InlineData(4, EggStage.Soon)]
    [InlineData(9, EggStage.Moving)]
    [InlineData(20, EggStage.Slow)]
    [InlineData(80, EggStage.Long)]
    public void Eggs_take_the_games_stages(int cycles, EggStage stage) =>
        Assert.Equal(stage, EggWidget.StageOf(new EggWidgetEgg("Togepi", 175, cycles, 0, 80, cycles * 256)));

    [Fact]
    public void The_egg_nearest_hatching_is_shown_and_wobbles_faster()
    {
        var view = EggWidget.View(Save(new("Eevee", 133, 30, 0, 35, 7680), new("Togepi", 175, 3, 0, 10, 1024)));
        Assert.Equal(EggStage.Soon, view.Stage);
        Assert.Equal("Togepi egg (2 eggs)", view.Title);
        Assert.Contains("1,024", view.Detail);
        Assert.True(EggWidget.Motion(EggStage.Soon).IntervalMs < EggWidget.Motion(EggStage.Slow).IntervalMs);
        Assert.Equal("crack-mid", EggWidget.Motion(EggStage.Ready).Frames[0]);
    }

    [Fact]
    public void Gen2_style_eggs_are_ready_at_their_lowest_counter()
    {
        var egg = new EggWidgetEgg("Togepi", 175, 1, 1, 10, 256);
        Assert.Equal(EggStage.Ready, EggWidget.StageOf(egg));
        Assert.Equal(1f, EggWidget.Progress(egg));
    }

    [Fact]
    public void Every_empty_state_has_words()
    {
        Assert.Equal(EggStage.None, EggWidget.View(null).Stage);
        Assert.False(string.IsNullOrWhiteSpace(EggWidget.View(null).Detail));
        var waiting = EggWidget.View(new EggWidgetSnapshot("d", "Emerald", true, true, ["Togekiss", "Ditto"], [], Morning));
        Assert.Equal(EggStage.Waiting, waiting.Stage);
        Assert.Contains("Togekiss", waiting.Detail);
        Assert.Equal(EggStage.None, EggWidget.View(Save()).Stage);
    }

    [Theory]
    [InlineData(110, 110, HomeWidgetSizeHint.Small)]
    [InlineData(250, 110, HomeWidgetSizeHint.Wide)]
    [InlineData(250, 250, HomeWidgetSizeHint.Large)]
    public void Sizes_follow_the_Pokepark_widget(int width, int height, HomeWidgetSizeHint size)
    {
        Assert.Equal(size, HomeWidgetSizes.For(width, height));
        var (w, h) = HomeWidgetSizes.Dp(size);
        Assert.Equal(size, HomeWidgetSizes.For(w, h));
    }
}
