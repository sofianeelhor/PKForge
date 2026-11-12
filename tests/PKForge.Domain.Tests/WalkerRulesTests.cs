using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The Pokéwalker's rules: steps to watts, course unlocks, the stroll pace, the Poké
/// Radar and Dowsing, and what a walk is worth on return, all reproducible from the seed.</summary>
public sealed class WalkerRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static WalkerState Walking(WalkerState? state = null, int course = 0, uint seed = 42, int[]? types = null) =>
        WalkerRules.Start(state ?? new WalkerState(), WalkerCourses.All[course], "bank:x", "Pikachu", 25, 0, false, "fp",
            types ?? [ParkType.Electric], seed, Noon);

    [Fact]
    public void Twenty_steps_make_a_watt_and_the_rest_carries_over()
    {
        var state = WalkerRules.AddSteps(new WalkerState(), 39);
        Assert.Equal(1, state.Watts);
        Assert.Equal(19, state.StepRemainder);
        state = WalkerRules.AddSteps(state, 1);
        Assert.Equal(2, state.Watts);
        Assert.Equal(2, state.LifetimeWatts);
        Assert.Equal(40, state.UnsyncedSteps);
    }

    [Fact]
    public void Courses_open_by_lifetime_watts_and_the_national_dex()
    {
        var fresh = new WalkerState();
        Assert.Equal(["Refreshing Field", "Noisy Forest"], WalkerRules.Unlocked(fresh).Select(c => c.Name));
        var rich = fresh with { LifetimeWatts = 100_000 };
        Assert.Equal(8, WalkerRules.Unlocked(rich).Count);
        Assert.Equal(20, WalkerRules.Unlocked(rich with { NationalDex = true }).Count);
        Assert.Equal("Rugged Road", WalkerRules.NextLocked(fresh)!.Name);
        // A course the save has open (an event one too) is open here.
        Assert.Contains(WalkerRules.Unlocked(fresh with { SyncedCourses = [5] }), c => c.Index == 5);
    }

    [Fact]
    public void Every_course_matches_the_device()
    {
        Assert.Equal(20, WalkerCourses.All.Count);
        Assert.All(WalkerCourses.All, c =>
        {
            Assert.Equal(6, c.SlotSteps.Count);
            Assert.Equal(3, c.Advantage.Count);
            Assert.InRange(c.Items.Count, 9, 10);
            Assert.Contains(c.Items, i => i.Steps == 0);
            Assert.Equal(0, c.SlotSteps[4]);
            Assert.Equal(0, c.SlotSteps[5]);
        });
        Assert.Equal([0, 0, 50, 200, 500, 1000, 2000, 3000, 5000, 7500, 10000, 15000, 20000, 25000, 30000, 40000, 50000, 65000, 80000, 100000],
            WalkerCourses.All.Select(c => c.Watts));
    }

    [Fact]
    public void A_walk_draws_one_pokemon_of_each_pair_and_replays_from_its_seed()
    {
        var a = Walking(seed: 7).Walk!;
        var b = Walking(seed: 7).Walk!;
        Assert.Equal(a.Slots, b.Slots);
        Assert.InRange(a.Slots[0], 0, 1);
        Assert.InRange(a.Slots[1], 2, 3);
        Assert.InRange(a.Slots[2], 4, 5);
        Assert.Throws<InvalidOperationException>(() => Walking(Walking()));
        Assert.Throws<InvalidOperationException>(() => Walking(course: 2));
    }

    [Fact]
    public void The_advantage_cuts_steps_by_a_quarter()
    {
        var field = WalkerCourses.All[0];
        Assert.True(WalkerRules.HasAdvantage(field, [ParkType.Fire]));
        Assert.False(WalkerRules.HasAdvantage(field, [ParkType.Water]));
        Assert.Equal(2250, WalkerRules.StepsFor(field, 0, true));
        Assert.Equal(3000, WalkerRules.StepsFor(field, 0, false));
    }

    [Fact]
    public void The_sensor_counts_from_the_last_reading_and_survives_a_reboot()
    {
        Assert.Equal(0, WalkerRules.SensorDelta(null, 5000));
        Assert.Equal(120, WalkerRules.SensorDelta(1000, 1120));
        Assert.Equal(300, WalkerRules.SensorDelta(1000, 300));
    }

    [Fact]
    public void Strolling_is_slow_and_capped_per_day()
    {
        Assert.Equal(600, WalkerRules.StrollSteps(Noon, Noon.AddHours(1)));
        Assert.Equal(0, WalkerRules.StrollSteps(Noon, Noon.AddHours(-1)));
        Assert.Equal(WalkerRules.StrollDailyCap, WalkerRules.StrollSteps(Noon.AddHours(-12), Noon.AddHours(11)));
        // Noon to midnight is capped at 3,000; the next 6 hours give 3,000 more.
        Assert.Equal(6000, WalkerRules.StrollSteps(Noon, Noon.AddHours(18)));
        Assert.Equal(600, WalkerRules.StrollSteps(Noon, Noon.AddHours(2), alreadyToday: 2400));
        Assert.True(WalkerRules.StrollSteps(Noon, Noon.AddYears(1)) <= 31 * WalkerRules.StrollDailyCap);
    }

    [Fact]
    public void Stroll_mode_keeps_the_daily_cap_across_many_counts()
    {
        var state = Walking() with { StrollLast = Noon };
        for (var i = 1; i <= 11; i++) state = WalkerRules.Stroll(state, Noon.AddHours(i));
        Assert.Equal(WalkerRules.StrollDailyCap, state.Walk!.Steps);
        Assert.Equal(WalkerRules.StrollDailyCap, state.StrollToday);
        state = WalkerRules.Stroll(state, Noon.AddHours(13));
        Assert.Equal(WalkerRules.StrollDailyCap + 600, state.Walk!.Steps);
        Assert.Equal(600, state.StrollToday);
        // Without a walk, nothing is counted.
        var idle = WalkerRules.Stroll(new WalkerState(StrollLast: Noon), Noon.AddHours(5));
        Assert.Equal(0, idle.LifetimeSteps);
        Assert.Equal(Noon.AddHours(5), idle.StrollLast);
    }

    [Fact]
    public void The_radar_costs_ten_watts_and_meets_only_slots_the_steps_reached()
    {
        var state = WalkerRules.AddSteps(Walking(), 200);
        Assert.Equal(10, state.Watts);
        var (after, round) = WalkerRules.Radar(state)!.Value;
        Assert.Equal(0, after.Watts);
        Assert.Null(WalkerRules.Radar(after));
        // 200 steps on Refreshing Field reach only group C.
        Assert.Equal(2, WalkerCourse.GroupOf(round.Slot));
        Assert.Equal(1, round.Marks);
        Assert.InRange(round.Bush, 0, WalkerRules.Bushes - 1);
    }

    [Fact]
    public void Picking_the_wrong_bush_loses_it_and_the_right_one_may_catch_it()
    {
        var state = WalkerRules.AddSteps(Walking(), 20_000);
        var caught = 0;
        for (var i = 0; i < 40; i++)
        {
            var (spent, round) = WalkerRules.Radar(state)!.Value;
            var (_, wrong) = WalkerRules.Search(spent, round, (round.Bush + 1) % WalkerRules.Bushes);
            Assert.Equal(WalkerRadarOutcome.WrongBush, wrong);
            var (next, outcome) = WalkerRules.Search(spent, round, round.Bush);
            Assert.True(outcome is WalkerRadarOutcome.Caught or WalkerRadarOutcome.Fled);
            if (outcome == WalkerRadarOutcome.Caught)
            {
                caught++;
                Assert.Contains(round.Slot, next.Walk!.Slots);
                if (next.Walk.Catches.Count == WalkerRules.MaxHeld) { Assert.Null(WalkerRules.Radar(next)); break; }
            }
            state = next;
        }
        Assert.True(caught > 0);
    }

    [Fact]
    public void Dowsing_hints_close_or_far_and_finds_items_the_steps_reached()
    {
        var state = WalkerRules.AddSteps(Walking(), 60);
        var (spent, round) = WalkerRules.Dowse(state)!.Value;
        Assert.Equal(0, spent.Watts);
        // 60 steps on Refreshing Field reach only the Potion.
        Assert.Equal("Potion", round.Item);
        var miss = round.Patch == 0 ? 2 : 0;
        var (_, hinted, outcome) = WalkerRules.Dig(spent, round, miss);
        Assert.Equal(Math.Abs(miss - round.Patch) == 1 ? WalkerDowsingOutcome.Close : WalkerDowsingOutcome.Far, outcome);
        Assert.Equal(1, hinted.TriesLeft);
        var (found, _, last) = WalkerRules.Dig(spent, hinted, round.Patch);
        Assert.Equal(WalkerDowsingOutcome.Found, last);
        Assert.Equal(["Potion"], found.Walk!.Items);
        var (_, _, missedTwice) = WalkerRules.Dig(spent, hinted, miss);
        Assert.Equal(WalkerDowsingOutcome.Missed, missedTwice);
    }

    [Fact]
    public void Returning_gives_a_step_of_exp_each_and_friendship_by_256_steps()
    {
        var state = WalkerRules.AddSteps(Walking(), 1000);
        var (home, back) = WalkerRules.Return(state);
        Assert.Null(home.Walk);
        Assert.Equal(50, home.Watts);
        Assert.Equal(1000u, back.Exp);
        Assert.Equal(3, back.Friendship);
        Assert.Throws<InvalidOperationException>(() => WalkerRules.Return(home));
    }

    [Fact]
    public void A_sync_sends_one_course_at_a_time()
    {
        var state = new WalkerState(LifetimeWatts: 600);
        var open = new HashSet<int> { 0, 1 };
        Assert.Equal(2, WalkerRules.CourseToSend(state, open.Contains, _ => true));
        open.Add(2);
        Assert.Equal(3, WalkerRules.CourseToSend(state, open.Contains, _ => true));
        Assert.Null(WalkerRules.CourseToSend(state, _ => true, _ => true));
        Assert.Null(WalkerRules.CourseToSend(state, open.Contains, _ => false));
    }
}
