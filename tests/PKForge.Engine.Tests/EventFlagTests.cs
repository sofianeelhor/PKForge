using PKForge.Engine;
using System.Reflection;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Event flag / work editor on blank retail saves: PKHeX label loading per game, set/clear
/// round-trips through the game's own accessor, work ranges, unlabeled filtering and the
/// changed-since-open diff.
/// </summary>
public sealed class EventFlagTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    /// <summary>Gen 5+ blanks serialize, so they open from bytes like a real file (with a baseline).</summary>
    private static SaveEngineSession FromBytes(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English).Write().ToArray(), null);

    [Theory]
    // Index + label straight from the PKHeX label file named in the comment.
    [InlineData(GameVersion.C, 832, "Received GS Ball")]                  // gen2/flags_c_en 0832
    [InlineData(GameVersion.E, 137, "Received HM01 Cut")]                 // gen3/flags_e_en 0137
    [InlineData(GameVersion.R, 0x853, "Received Eon Ticket")]             // gen3/flags_rs_en
    [InlineData(GameVersion.Pt, 291, null)]                               // gen4/flags_pt_en (Shaymin)
    [InlineData(GameVersion.HG, 781, null)]                               // gen4/flags_hgss_en
    [InlineData(GameVersion.OR, 3010, "Received Eon Ticket")]             // gen6/flags_oras_en
    public void LoadsPkhexFlagLabels(GameVersion version, int index, string? label)
    {
        using var session = Blank(version);
        Assert.True(EventFlagService.IsSupported(session));
        var catalog = EventFlagService.Load(session);
        var entry = Assert.Single(catalog.Flags, f => f.Index == index);
        Assert.True(entry.Labeled);
        if (label is not null) Assert.Equal(label, entry.Label);
        Assert.StartsWith("PKHeX", catalog.LabelSource);
    }

    [Fact]
    public void StoryFlagsAreRiskyAndSectioned()
    {
        using var session = Blank(GameVersion.E);
        var cut = EventFlagService.Load(session).Flags.Single(f => f.Index == 137);
        Assert.Equal("Story", cut.Category); // 's' in flags_e_en
        Assert.True(cut.Risky);
        var gift = EventFlagService.Load(session).Flags.Single(f => f.Index == 151); // "g Received Castform"
        Assert.Equal("Gifts", gift.Category);
        Assert.False(gift.Risky);
    }

    [Fact]
    public void WorkLabelsCarryPresets()
    {
        using var session = Blank(GameVersion.Pt);
        // const_pt_en: "0067 e Member Card 0:Not Activated,4617:Activated"
        var member = EventFlagService.Load(session).Work.Single(w => w.Index == 67);
        Assert.Equal("Member Card", member.Label);
        Assert.Contains(member.Presets, p => p.Value == 4617 && p.Name == "Activated");
        Assert.DoesNotContain(member.Presets, p => p.Value == NamedEventConst.CustomMagicValue);
        Assert.Equal("Not Activated", member.ValueName);
        Assert.Equal((0, 65535), (member.Min, member.Max));
    }

    [Theory]
    [InlineData(GameVersion.RD, 0x8C1)]
    [InlineData(GameVersion.C, 832)]
    [InlineData(GameVersion.E, 137)]
    [InlineData(GameVersion.FR, 740)]
    [InlineData(GameVersion.Pt, 291)]
    [InlineData(GameVersion.HG, 781)]
    [InlineData(GameVersion.B2, 100)]
    [InlineData(GameVersion.X, 100)]
    [InlineData(GameVersion.SN, 100)]
    [InlineData(GameVersion.GP, 100)]
    [InlineData(GameVersion.BD, 100)]
    public void SetAndClearRoundTripThroughTheGamesAccessor(GameVersion version, int index)
    {
        using var session = Blank(version);
        Assert.False(Flag(session, index).IsOn);

        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.Event, index, true).Success);
        Assert.True(Flag(session, index).IsOn);
        Assert.True(Raw(session.SaveFile, index));
        Assert.True(Flag(session, index).Changed);

        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.Event, index, false).Success);
        Assert.False(Flag(session, index).IsOn);
        Assert.False(Raw(session.SaveFile, index));
        Assert.False(Flag(session, index).Changed);
    }

    [Fact]
    public void BdspSystemFlagsAreASeparateRiskyBank()
    {
        using var session = Blank(GameVersion.BD);
        var catalog = EventFlagService.Load(session);
        var clear = catalog.Flags.Single(f => f.Bank == EventFlagBank.System && f.Index == 5); // system_bdsp "SYS_FLAG_GAME_CLEAR"
        Assert.Equal("SYS_FLAG_GAME_CLEAR", clear.Label);
        Assert.True(clear.Risky);
        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.System, 5, true).Success);
        Assert.True(((SAV8BS)session.SaveFile).FlagWork.GetSystemFlag(5));
        Assert.False(((SAV8BS)session.SaveFile).FlagWork.GetFlag(5)); // the event bank is untouched
    }

    [Fact]
    public void FlagSurvivesSerializeAndReopen()
    {
        using var session = FromBytes(GameVersion.B2);
        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.Event, 123, true).Success);
        Assert.True(EventFlagService.SetWork(session, 45, 777).Success);
        using var reopened = new SaveEngineSession(session.Serialize(), null);
        var catalog = EventFlagService.Load(reopened);
        Assert.True(catalog.Flags.Single(f => f.Index == 123).IsOn);
        Assert.Equal(777, catalog.Work.Single(w => w.Index == 45).Value);
    }

    [Fact]
    public void ChangedViewDiffsAgainstTheOpenedFile()
    {
        using var session = FromBytes(GameVersion.W2);
        EventFlagService.SetFlag(session, EventFlagBank.Event, 200, true);
        EventFlagService.SetWork(session, 10, 3);
        var catalog = EventFlagService.Load(session);
        Assert.True(catalog.HasBaseline);
        var changed = EventFlagService.Filter(catalog.Flags, new EventFilter(ChangedOnly: true));
        Assert.Equal(200, Assert.Single(changed).Index); // unlabeled, but changed-only still shows it
        var work = Assert.Single(EventFlagService.Filter(catalog.Work, new EventFilter(ChangedOnly: true)));
        Assert.Equal((10, 3L, 0L), (work.Index, work.Value, work.Original));
    }

    [Fact]
    public void WorkRangesAreEnforcedNotWrapped()
    {
        using (var gen2 = Blank(GameVersion.C))
        {
            Assert.True(EventFlagService.SetWork(gen2, 3, 255).Success);
            Assert.False(EventFlagService.SetWork(gen2, 3, 256).Success);
            Assert.False(EventFlagService.SetWork(gen2, 3, -1).Success);
            Assert.Equal(255, ((SAV2)gen2.SaveFile).GetWork(3));
        }
        using (var gen3 = Blank(GameVersion.E))
        {
            Assert.True(EventFlagService.SetWork(gen3, 0x7E, 65535).Success);
            Assert.False(EventFlagService.SetWork(gen3, 0x7E, 65536).Success);
            Assert.Equal(65535, ((SAV3)gen3.SaveFile).GetWork(0x7E));
            Assert.False(EventFlagService.SetWork(gen3, ((SAV3)gen3.SaveFile).EventWorkCount, 1).Success);
        }
        using (var gg = Blank(GameVersion.GE))
        {
            Assert.True(EventFlagService.SetWork(gg, 40, -5).Success); // Let's Go work is a signed int
            Assert.Equal(-5, ((SAV7b)gg.SaveFile).EventWork.GetWork(40));
        }
    }

    [Fact]
    public void UnlabeledFlagsHideUntilAskedOrSearchedByNumber()
    {
        using var session = Blank(GameVersion.E);
        var flags = EventFlagService.Load(session).Flags;
        var shown = EventFlagService.Filter(flags, new EventFilter());
        Assert.All(shown, f => Assert.True(f.Labeled));
        Assert.True(shown.Count < flags.Count);
        Assert.Equal(flags.Count, EventFlagService.Filter(flags, new EventFilter(ShowUnlabeled: true)).Count);

        var unlabeled = flags.First(f => !f.Labeled);
        Assert.Single(EventFlagService.Filter(flags, new EventFilter(Query: unlabeled.Index.ToString())));
        Assert.Single(EventFlagService.Filter(flags, new EventFilter(Query: $"0x{unlabeled.Index:X}")));

        var cut = EventFlagService.Filter(flags, new EventFilter(Query: "hm01"));
        Assert.Contains(cut, f => f.Index == 137);
        Assert.All(EventFlagService.Filter(flags, new EventFilter(Category: "Story")), f => Assert.Equal("Story", f.Category));
        Assert.DoesNotContain(EventFlagService.Categories(flags, showUnlabeled: false), c => c == "Unlabeled");
    }

    [Fact]
    public void LetsGoAndBdspSectionUnlabeledFlagsByTheGamesOwnRanges()
    {
        using var gg = Blank(GameVersion.GP);
        var ggFlags = EventFlagService.Load(gg).Flags;
        Assert.Equal("Zone", ggFlags[0].Category);
        Assert.Contains(ggFlags, f => f.Category == "Visibility" && f.Labeled); // flags_gg "v0277 ... Snorlax"

        using var bd = Blank(GameVersion.BD);
        var bdFlags = EventFlagService.Load(bd).Flags;
        Assert.Equal("Hidden items", bdFlags.Single(f => f.Bank == EventFlagBank.Event && f.Index == 0).Category);
        Assert.Equal("Trainers", bdFlags.Single(f => f.Bank == EventFlagBank.Event && f.Index == 700).Category);
    }

    [Fact]
    public void SwitchFlagsAreBooleanSaveBlocks()
    {
        var save = BlankSaveFile.Get(GameVersion.SL, "PKForge", LanguageID.English);
        var blocks = ((ISCBlockArray)save).AllBlocks;
        // Blank Switch saves carry every block untyped; type one as a real Bool1 (see SaveBlockEditorTests).
        var key = blocks[0].Key;
        ((IList<SCBlock>)blocks)[0] = (SCBlock)Activator.CreateInstance(typeof(SCBlock),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [key, SCTypeCode.Bool1], null)!;
        using var session = new SaveEngineSession(save, null);

        var flag = Assert.Single(EventFlagService.Load(session).Flags);
        Assert.Equal(EventFlagBank.Block, flag.Bank);
        Assert.Equal(key.ToString("X8"), flag.Id);
        Assert.Empty(EventFlagService.Load(session).Work);

        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.Block, flag.Index, true).Success);
        Assert.Equal(SCTypeCode.Bool2, ((ISCBlockArray)save).AllBlocks[0].Type);
        Assert.True(EventFlagService.Load(session).Flags[0].Changed);
    }

    [Fact]
    public void UnsupportedSavesSayWhy()
    {
        using var xd = new SaveEngineSession(new SAV3XD(), null);
        Assert.False(EventFlagService.IsSupported(xd));
        Assert.Contains("Colosseum/XD", EventFlagService.UnsupportedReason(xd));
        Assert.Throws<NotSupportedException>(() => EventFlagService.Load(xd));
        Assert.False(EventFlagService.SetFlag(xd, EventFlagBank.Event, 0, true).Success);
    }

    private static EventEntry Flag(SaveEngineSession session, int index) =>
        EventFlagService.Load(session).Flags.Single(f => f.Bank == EventFlagBank.Event && f.Index == index);

    private static bool Raw(SaveFile save, int index) => save switch
    {
        SAV7b gg => gg.EventWork.GetFlag(index),
        SAV8BS bd => bd.FlagWork.GetFlag(index),
        IEventFlagProvider37 p => p.EventWork.GetEventFlag(index),
        IEventFlagArray a => a.GetEventFlag(index),
        _ => throw new NotSupportedException(),
    };
}
