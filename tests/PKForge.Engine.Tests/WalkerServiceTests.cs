using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The PKForge Pokéwalker against PKHeX: every Radar catch is a legal Pokéwalker
/// encounter, every Dowsing item is a real HeartGold/SoulSilver item, coming home keeps a legal
/// Pokémon legal (one level at most), and a HGSS sync writes steps, watts and one course.</summary>
public sealed class WalkerServiceTests
{
    private static readonly ITrainerInfo Trainer = WalkerService.TrainerFor(null, null, null);

    [Fact]
    public void Every_slot_of_every_course_is_caught_legal()
    {
        foreach (var course in WalkerCourses.All)
            for (var slot = 0; slot < 6; slot++)
            {
                var caught = WalkerService.Catch(course.Index, slot, Trainer, out var why);
                Assert.True(caught is not null, $"{course.Name} slot {slot}: {why}");
                var pk = EntityBytes.Parse(caught!.Data, caught.Format)!;
                Assert.IsType<PK4>(pk);
                Assert.Equal(Locations.PokeWalker4, pk.MetLocation);
                Assert.Equal(WalkerService.SlotLevel(course.Index, slot), pk.CurrentLevel);
                Assert.True(new LegalityAnalysis(pk).Valid, new LegalityAnalysis(pk).Report());
            }
    }

    [Fact]
    public void A_save_trainer_owns_the_catch()
    {
        using var session = new SaveEngineSession(new SAV4HGSS(new byte[0x80000]) { Language = (int)LanguageID.English, OT = "LYRA", TID16 = 1234, SID16 = 4321 }, null);
        var caught = WalkerService.Catch(18, 0, WalkerService.TrainerFor(session, null, null), out _)!;
        var pk = EntityBytes.Parse(caught.Data, caught.Format)!;
        Assert.Equal("LYRA", pk.OriginalTrainerName);
        Assert.Equal(1234, pk.TID16);
        Assert.True(new LegalityAnalysis(pk).Valid);
    }

    [Fact]
    public void Every_dowsing_item_is_a_heartgold_item()
    {
        foreach (var item in WalkerCourses.All.SelectMany(c => c.Items).Select(i => i.Name).Distinct())
        {
            Assert.True(WalkerService.ItemId(item) is > 0, item);
            Assert.NotNull(WalkerService.BankKey(item));
        }
        Assert.StartsWith("machine:", WalkerService.BankKey("TM18")!.Value.Key);
        Assert.Equal(ItemBankKeys.Item((int)WalkerService.ItemId("Potion")!), WalkerService.BankKey("Potion")!.Value.Key);
    }

    [Fact]
    public void Coming_home_gives_at_most_one_level_and_stays_legal()
    {
        var walker = EntityBytes.Parse(WalkerService.Catch(0, 4, Trainer, out _)!.Data, "pk4")!;
        var level = walker.CurrentLevel;
        var friendship = walker.CurrentFriendship;
        var done = WalkerService.ComeHome(walker, new WalkerHomecoming(50_000, 50_000, 195, [], []), out var why);
        Assert.True(done is not null, why);
        var (pk, growth) = done!.Value;
        Assert.Equal(level + 1, pk.CurrentLevel);
        Assert.Equal(level + 1, growth.LevelAfter);
        Assert.Equal(Experience.GetEXP((byte)(level + 1), pk.PersonalInfo.EXPGrowth), pk.EXP);
        Assert.Equal(Math.Min(255, friendship + 195), pk.CurrentFriendship);
        Assert.True(new LegalityAnalysis(pk).Valid, new LegalityAnalysis(pk).Report());
    }

    [Fact]
    public void A_short_walk_adds_its_steps_as_exp()
    {
        var walker = EntityBytes.Parse(WalkerService.Catch(0, 4, Trainer, out _)!.Data, "pk4")!;
        var before = walker.EXP;
        var (pk, growth) = WalkerService.ComeHome(walker, new WalkerHomecoming(3, 3, 0, [], []), out _)!.Value;
        Assert.Equal(3u, growth.ExpGained);
        Assert.Equal(before + 3, pk.EXP);
    }

    [Fact]
    public void Bank_bytes_come_home_too()
    {
        var caught = WalkerService.Catch(1, 5, Trainer, out _)!;
        var done = WalkerService.ComeHome(caught.Data, caught.Format, new WalkerHomecoming(500, 500, 1, [], []), out var why);
        Assert.True(done is not null, why);
        Assert.True(done!.Value.Growth.ExpGained > 0);
        Assert.True(new LegalityAnalysis(EntityBytes.Parse(done.Value.Data, caught.Format)!).Valid);
    }

    [Fact]
    public void A_sync_adds_steps_writes_watts_and_opens_one_course()
    {
        using var session = new SaveEngineSession(new SAV4HGSS(new byte[0x80000]) { Language = (int)LanguageID.English }, null);
        PokewalkerService.SetCounters(session, 100, 5);
        var state = new WalkerState(Watts: 321, LifetimeWatts: 1500, UnsyncedSteps: 2000);
        var sync = WalkerService.Sync(session, state);
        var after = PokewalkerService.GetState(session);
        Assert.Equal(2100u, after.Steps);
        Assert.Equal(321u, after.Watts);
        Assert.Equal(0, sync.CourseOpened);
        Assert.Equal([0], sync.SaveCourses);
        var again = WalkerService.Sync(session, state with { UnsyncedSteps = 0 });
        Assert.Equal(1, again.CourseOpened);
        Assert.Equal(2100u, PokewalkerService.GetState(session).Steps);
    }
}
