using PKHeX.Core;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Legends: Arceus research tasks on a blank save: forced counts, research level 10, perfect,
/// and every species at once. Each write is checked after a reload from a deep copy of the
/// block list (what SwishCrypto writes out; PKHeX's Switch blank can't decrypt its own image).
/// </summary>
public sealed class LegendsResearchTests
{
    private const int Bidoof = 399;
    private const int Starly = 396;

    private static (SAV8LA Save, SaveEngineSession Session) Blank()
    {
        var save = (SAV8LA)BlankSaveFile.Get(GameVersion.PLA, "PKForge", LanguageID.English);
        // A real save types the expedition rank as a u32; PKHeX's blank leaves it untyped,
        // and the report reads it (as TrainerStatsTests does for its blocks).
        var rank = save.Accessor.GetBlock(SaveBlockAccessor8LA.KExpeditionTeamRank);
        if (rank.Type == SCTypeCode.None) rank.ChangeStoredType(SCTypeCode.UInt32);
        // Same for the story counters the "Arceus legend" tasks read.
        foreach (var task in PokedexConstants8a.ResearchTasks.SelectMany(x => x))
        {
            if (task.Task != PokedexResearchTaskType8a.PartOfArceus || task.Hash_08 == 0xCBF29CE484222645) continue;
            var block = save.Accessor.GetBlock((uint)task.Hash_08);
            if (block.Type == SCTypeCode.None && block.Data.Length == 4) block.ChangeStoredType(SCTypeCode.UInt32);
        }
        return (save, new SaveEngineSession(save, null));
    }

    private static SaveEngineSession Reload(SAV8LA save) => new((SAV8LA)save.Clone(), null);

    [Fact]
    public void OnlyLegendsArceusHasResearch()
    {
        using var sw = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SW, "PKForge", LanguageID.English), null);
        Assert.False(LegendsResearchService.Supports(sw));
        Assert.Throws<InvalidOperationException>(() => LegendsResearchService.Get(sw, Bidoof));

        var (_, la) = Blank();
        using (la)
        {
            Assert.True(LegendsResearchService.Supports(la));
            var species = LegendsResearchService.GetSpecies(la);
            Assert.Equal(242, species.Count);
            Assert.Equal((int)Species.Rowlet, species[0]);
        }
    }

    [Fact]
    public void BlankPageListsTasksWithThresholds()
    {
        var (_, session) = Blank();
        using var _s = session;
        var page = LegendsResearchService.Get(session, Bidoof);
        Assert.Equal(0, page.Rate);
        Assert.False(page.Complete);
        Assert.NotEmpty(page.Tasks);
        Assert.All(page.Tasks, t => Assert.Equal(0, t.ReportedLevel));
        Assert.Contains(page.Tasks, t => t.Label == "Number caught" && t.CanSet && t.Thresholds.Count > 0);
    }

    [Fact]
    public void ForcedTaskValueSurvivesReload()
    {
        var (save, session) = Blank();
        using var _s = session;
        var task = LegendsResearchService.Get(session, Bidoof).Tasks.First(t => t.CanSet);
        LegendsResearchService.SetTaskValue(session, Bidoof, task.Index, task.Thresholds[0]);

        using var reloaded = Reload(save);
        var after = LegendsResearchService.Get(reloaded, Bidoof).Tasks[task.Index];
        Assert.Equal(task.Thresholds[0], after.Value);
        Assert.Equal(0, after.ReportedLevel); // not reported until the player (or Report) does
    }

    [Fact]
    public void CompleteReachesLevelTenAndSurvivesReload()
    {
        var (save, session) = Blank();
        using var _s = session;
        var page = LegendsResearchService.Complete(session, Bidoof);
        Assert.True(page.Complete);
        Assert.Equal(10, page.Level);
        Assert.Equal(0, page.Unreported);

        using var reloaded = Reload(save);
        var again = LegendsResearchService.Get(reloaded, Bidoof);
        Assert.True(again.Complete);
        Assert.True(again.Rate >= 100);
    }

    [Fact]
    public void PerfectMaxesEveryTask()
    {
        var (save, session) = Blank();
        using var _s = session;
        // Bidoof's request is a quest-state task the editor leaves to the story, so its page
        // reaches every settable task maxed but not the perfect mark.
        var bidoof = LegendsResearchService.Perfect(session, Bidoof);
        Assert.True(bidoof.Complete);
        Assert.All(bidoof.Tasks.Where(t => t.CanSet), t => Assert.True(t.IsMaxed, t.Label));
        Assert.Contains(bidoof.Tasks, t => !t.CanSet && !t.IsMaxed);
        Assert.False(bidoof.Perfect);

        var page = LegendsResearchService.Perfect(session, Starly);
        Assert.True(page.Perfect);
        Assert.All(page.Tasks, t => Assert.True(t.IsMaxed, t.Label));
        Assert.All(page.Tasks, t => Assert.Equal(t.Thresholds.Count, t.ReportedLevel));
        Assert.True(save.GetCaught(Starly)); // the forms task marks it obtained

        using var reloaded = Reload(save);
        Assert.True(LegendsResearchService.Get(reloaded, Starly).Perfect);
    }

    [Fact]
    public void PerfectAllCompletesTheDexFlags()
    {
        var (save, session) = Blank();
        using var _s = session;
        var perfect = LegendsResearchService.PerfectAll(session);
        var all = LegendsResearchService.GetSpecies(session);
        Assert.True(perfect > all.Count / 2, $"{perfect}/{all.Count} perfect");
        // Every page reaches level 10 except Arceus, whose one task is the story's.
        var pages = all.Select(s => LegendsResearchService.Get(session, s)).ToList();
        Assert.All(pages.Where(p => p.Species != (int)Species.Arceus), p => Assert.True(p.Complete, $"#{p.Species}"));
        Assert.True(pages.Single(p => p.Species == (int)Species.Unown).Perfect); // every form obtained

        using var reloaded = Reload(save);
        var dex = ((SAV8LA)reloaded.SaveFile).PokedexSave;
        // The area dexes get their complete mark; the Hisui dex also needs Arceus, which the story gives.
        Assert.True(dex.IsPokedexCompleted(PokedexType8a.Local1));
        Assert.True(dex.IsPokedexCompleted(PokedexType8a.Local5));
        Assert.False(dex.IsPokedexCompleted(PokedexType8a.Hisui));
        Assert.True(dex.GetTotalResearchPoint() > 0);
    }
}
