using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class HallOfFameServiceTests
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

    /// <summary>A ROM hack's own session (Soul Gold, Unbound…) has no PKHeX save behind it.</summary>
    public class ForeignSession : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    [Fact]
    public void AForeignSessionIsNotSupportedRatherThanACastThatThrows()
    {
        // Save data asks this of every save it opens: a throw left Y doing nothing on a ROM hack.
        var session = System.Reflection.DispatchProxy.Create<ISaveEngineSession, ForeignSession>();
        Assert.False(HallOfFameService.IsSupported(session));
    }

    /// <summary>A full international Emerald image with valid sector ids, as WorldEventsTests builds one.</summary>
    internal static SAV3E Emerald()
    {
        var raw = new byte[SaveUtil.SIZE_G3RAW];
        for (var id = 0; id < 14; id++)
        {
            var sector = raw.AsSpan(id * 0x1000);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[0xFF4..], (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFF8..], 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFFC..], 1);
        }
        raw[6] = raw[7] = 0xFF; // international OT name area, so SAV3.Japanese reads false
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xAC), 0x1234_5678);
        raw[0x900] = 1;
        return new SAV3E(raw);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(3, 50)]
    [InlineData(6, 16)]
    [InlineData(7, 2)]
    public void BlankSavesHaveAnEmptyHallOfFame(int generation, int slots)
    {
        using var session = Blank(generation);
        Assert.True(HallOfFameService.IsSupported(session));
        var info = HallOfFameService.Get(session);
        Assert.Equal(slots, info.Capabilities.TeamSlots);
        Assert.Empty(info.Teams);
    }

    [Fact]
    public void Gen3SavesWithoutTheHallOfFameSectorsAreNotOffered()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.False(HallOfFameService.IsSupported(session));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    public void GenerationsWithoutACoreModelAreNotOffered(int generation)
    {
        using var session = Blank(generation);
        Assert.False(HallOfFameService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => HallOfFameService.Get(session));
    }

    [Fact]
    public void Gen1MembersAndClearCountRoundTrip()
    {
        using var session = Blank(1);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(6, Level: 62, Nickname: "BLAZE"));
        HallOfFameService.SetMember(session, 0, 1, new HallOfFameMemberEdit(9, Level: 60));
        HallOfFameService.SetMember(session, 1, 4, new HallOfFameMemberEdit(150)); // lands on slot 0
        HallOfFameService.SetClearCount(session, 2);

        using var reopened = Reopen(session);
        var info = HallOfFameService.Get(reopened);
        Assert.Equal(2, info.ClearCount);
        Assert.Equal(2, info.Teams.Count);
        var first = info.Teams[0].Members;
        Assert.Equal((6, 62, "BLAZE"), (first[0].Species, first[0].Level!.Value, first[0].Nickname!));
        Assert.Equal((9, 60, "BLASTOISE"), (first[1].Species, first[1].Level!.Value, first[1].Nickname!));
        Assert.Equal("Mewtwo", info.Teams[1].Members[0].SpeciesName);
        Assert.Equal(0, info.Teams[1].Members[0].Slot);
        Assert.Null(first[0].Pid);
    }

    [Fact]
    public void Gen1DeleteTeamShiftsLaterTeamsUp()
    {
        using var session = Blank(1);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(1));
        HallOfFameService.SetMember(session, 1, 0, new HallOfFameMemberEdit(4));
        var info = HallOfFameService.DeleteTeam(session, 0);
        Assert.Single(info.Teams);
        Assert.Equal((0, (ushort)4), (info.Teams[0].Index, info.Teams[0].Members[0].Species));
    }

    [Fact]
    public void Gen3MembersCarryTrainerIdsAndRoundTrip()
    {
        using var session = Blank(3);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(257, Level: 55, Nickname: "TORCH", Pid: 0x12345678));
        HallOfFameService.SetMember(session, 0, 1, new HallOfFameMemberEdit(384, Level: 70));

        using var reopened = Reopen(session);
        var team = Assert.Single(HallOfFameService.Get(reopened).Teams);
        var m = team.Members[0];
        Assert.Equal((257, 55, "TORCH", 0x12345678u), (m.Species, m.Level!.Value, m.Nickname!, m.Pid!.Value));
        Assert.NotNull(m.TrainerId);
        Assert.Equal("RAYQUAZA", team.Members[1].Nickname);
    }

    [Fact]
    public void Gen3ClearMemberKeepsTheTeamContiguous()
    {
        using var session = Blank(3);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(252));
        HallOfFameService.SetMember(session, 0, 1, new HallOfFameMemberEdit(255));
        HallOfFameService.SetMember(session, 0, 2, new HallOfFameMemberEdit(258));
        var info = HallOfFameService.ClearMember(session, 0, 0);
        Assert.Equal([255, 258], info.Teams[0].Members.Select(x => (int)x.Species));
        Assert.Empty(HallOfFameService.ClearAll(session).Teams);
    }

    [Fact]
    public void Gen6FullCardsDateAndClearNumberRoundTrip()
    {
        using var session = Blank(6);
        HallOfFameService.SetMember(session, 1, 0, new HallOfFameMemberEdit(384, Level: 70, Nickname: "Ray",
            OriginalTrainer: "Brendan", HeldItem: 1, IsShiny: true, Moves: [245, 200]));
        HallOfFameService.SetMember(session, 1, 1, new HallOfFameMemberEdit(658, Level: 66));
        HallOfFameService.SetTeamRecord(session, 1, new DateOnly(2015, 3, 4), 7);

        using var reopened = Reopen(session);
        var team = Assert.Single(HallOfFameService.Get(reopened).Teams);
        Assert.Equal((1, new DateOnly(2015, 3, 4), 7), (team.Index, team.Date!.Value, team.ClearNumber!.Value));
        var m = team.Members[0];
        Assert.Equal((384, 70, "Ray", true, "Brendan", 1, true), (m.Species, m.Level!.Value, m.Nickname!,
            m.IsNicknamed!.Value, m.OriginalTrainer!, m.HeldItem!.Value, m.IsShiny!.Value));
        Assert.Equal([245, 200, 0, 0], m.Moves.Select(x => (int)x));
        Assert.Equal(("Greninja", false), (team.Members[1].Nickname!, team.Members[1].IsNicknamed!.Value));
    }

    [Fact]
    public void Gen6DeleteTeamAndClearAll()
    {
        using var session = Blank(6);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(25));
        HallOfFameService.SetMember(session, 1, 0, new HallOfFameMemberEdit(26));
        Assert.Equal(2, HallOfFameService.Get(session).Teams.Count);
        Assert.Single(HallOfFameService.DeleteTeam(session, 0).Teams);
        Assert.Empty(HallOfFameService.ClearAll(session).Teams);
    }

    [Fact]
    public void Gen7StoresSpeciesForFirstAndLatestTeams()
    {
        using var session = Blank(7);
        Assert.False(HallOfFameService.Get(session).Capabilities.StoresLevel);
        HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(722));
        HallOfFameService.SetMember(session, 1, 2, new HallOfFameMemberEdit(800));

        using var reopened = Reopen(session);
        var info = HallOfFameService.Get(reopened);
        Assert.Equal(["First clear", "Latest"], info.Teams.Select(t => t.Label));
        Assert.Equal((722, (int?)null), (info.Teams[0].Members[0].Species, info.Teams[0].Members[0].Level));
        Assert.Equal((2, 800), (info.Teams[1].Members[0].Slot, (int)info.Teams[1].Members[0].Species));

        Assert.Single(HallOfFameService.DeleteTeam(reopened, 0).Teams);
    }

    [Fact]
    public void OutOfRangeEditsAreRefused()
    {
        using var session = Blank(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => HallOfFameService.SetMember(session, 50, 0, new HallOfFameMemberEdit(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HallOfFameService.SetMember(session, 0, 6, new HallOfFameMemberEdit(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(152)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(1, Level: 101)));
        Assert.Throws<ArgumentException>(() => HallOfFameService.SetMember(session, 0, 0, new HallOfFameMemberEdit(1, Nickname: "ABCDEFGHIJK")));
        Assert.Throws<NotSupportedException>(() => HallOfFameService.SetTeamRecord(session, 0, new DateOnly(2000, 1, 1), 1));
    }
}
