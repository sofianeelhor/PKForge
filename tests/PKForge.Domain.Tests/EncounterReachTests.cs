using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>"Where can I catch it now": an estimate from the badges, never a promise.</summary>
public sealed class EncounterReachTests
{
    private static EncounterCard Card(string kind, int min, int max = 0) =>
        new(kind, "Route", min, max == 0 ? min : max, "Fire Red", "", false, false);

    private static SaveBadges Badges(int earned) => SaveBadges.Counted(earned, BadgeSet.Kanto);

    [Fact]
    public void ALowLevelWildEncounter_IsReachableFromTheStart()
    {
        var verdict = EncounterReach.Judge(Card("Wild", 3, 5), Badges(0));
        Assert.Equal(EncounterReachKind.Now, verdict.Reach);
        Assert.Contains("estimate", verdict.Note);
    }

    [Fact]
    public void AHighLevelOne_IsLater_UntilMoreBadges()
    {
        var card = Card("Wild", 40);
        var early = EncounterReach.Judge(card, Badges(2));
        Assert.Equal(EncounterReachKind.Later, early.Reach);
        Assert.Contains("estimate", early.Note);
        Assert.Equal(EncounterReachKind.Now, EncounterReach.Judge(card, Badges(4)).Reach);
    }

    [Fact]
    public void EveryBadge_OpensEverything()
    {
        Assert.Equal(EncounterReachKind.Now, EncounterReach.Judge(Card("Static", 70), Badges(8)).Reach);
        // HeartGold/SoulSilver: the eighth of sixteen is Johto's League.
        Assert.Equal(100, EncounterReach.LevelBand(SaveBadges.Counted(8, BadgeSet.JohtoKanto)));
    }

    [Fact]
    public void NoBadgesRead_JudgesNothing()
    {
        Assert.Null(EncounterReach.LevelBand(null));
        Assert.Equal(EncounterReachKind.Unknown, EncounterReach.Judge(Card("Wild", 50), null).Reach);
    }

    [Fact]
    public void EggsAreNow_EventsAreNotTheStorys()
    {
        Assert.Equal(EncounterReachKind.Now, EncounterReach.Judge(Card("Egg", 1), Badges(0)).Reach);
        Assert.Equal(EncounterReachKind.Unknown, EncounterReach.Judge(Card("Event", 10), Badges(3)).Reach);
    }

    [Fact]
    public void JudgeAll_KeepsTheCardsOrder()
    {
        var verdicts = EncounterReach.JudgeAll([Card("Wild", 50), Card("Wild", 2)], Badges(1));
        Assert.Equal([EncounterReachKind.Later, EncounterReachKind.Now], verdicts.Select(verdict => verdict.Reach));
    }
}
