using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class AwardsEditingTests
{
    [Fact]
    public void PokerusCanBeInfectedCuredAndCleared()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM });

        var initial = session.GetPokerus(0, 0);
        Assert.True(initial.Supported);
        Assert.Equal(PokerusStatus.Susceptible, initial.Status);

        session.SetPokerus(0, 0, PokerusStatus.Infectious);
        var infected = session.GetPokerus(0, 0);
        Assert.Equal(PokerusStatus.Infectious, infected.Status);
        Assert.InRange(infected.Strain, 1, 8);
        Assert.Equal(Pokerus.GetMaxDuration(infected.Strain), infected.Days);

        session.SetPokerus(0, 0, PokerusStatus.Cured);
        var cured = session.GetPokerus(0, 0);
        Assert.Equal(PokerusStatus.Cured, cured.Status);
        Assert.True(cured.Strain > 0);
        Assert.Equal(0, cured.Days);

        session.SetPokerus(0, 0, PokerusStatus.Susceptible);
        var cleared = session.GetPokerus(0, 0);
        Assert.Equal(PokerusStatus.Susceptible, cleared.Status);
        Assert.Equal(0, cleared.Strain);
    }

    [Fact]
    public void Gen1DoesNotOfferPokerus()
    {
        using var session = Seed(1, new PK1 { Version = GameVersion.RD });
        Assert.False(session.GetPokerus(0, 0).Supported);
        Assert.Throws<NotSupportedException>(() => session.SetPokerus(0, 0, PokerusStatus.Infectious));
    }

    [Fact]
    public void BooleanRibbonCanBeToggledAndExported()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM });
        var ribbon = Assert.Single(session.GetRibbons(0, 0), r => r.Id == "RibbonEffort");
        Assert.Equal(1, ribbon.MaxValue);

        session.SetRibbon(0, 0, ribbon.Id, 1);
        Assert.Equal(1, Assert.Single(session.GetRibbons(0, 0), r => r.Id == ribbon.Id).Value);

        var bytes = session.ExportSlot(0, 0).Data;
        var exported = Assert.IsType<PK7>(EntityFormat.GetFromBytes(bytes));
        Assert.True(exported.RibbonEffort);

        session.SetRibbon(0, 0, ribbon.Id, 0);
        Assert.Equal(0, Assert.Single(session.GetRibbons(0, 0), r => r.Id == ribbon.Id).Value);
    }

    [Fact]
    public void CountedRibbonClampsAndMaintainsMemoryFlag()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM });
        var ribbon = Assert.Single(session.GetRibbons(0, 0), r => r.Id == "RibbonCountMemoryContest");
        Assert.Equal(40, ribbon.MaxValue);

        session.SetRibbon(0, 0, ribbon.Id, 99);
        Assert.Equal(40, Assert.Single(session.GetRibbons(0, 0), r => r.Id == ribbon.Id).Value);
        var set = Assert.IsAssignableFrom<IRibbonSetMemory6>(session.GetEntity(0, 0));
        Assert.True(set.HasContestMemoryRibbon);

        session.SetRibbon(0, 0, ribbon.Id, 0);
        var cleared = Assert.IsAssignableFrom<IRibbonSetMemory6>(session.GetEntity(0, 0));
        Assert.False(cleared.HasContestMemoryRibbon);
    }

    [Fact]
    public void AwardsSurviveAFullSaveWriteAndReopen()
    {
        using var session = Seed(5, new PK5 { Version = GameVersion.B });
        session.SetPokerus(0, 0, PokerusStatus.Infectious);
        session.SetRibbon(0, 0, "RibbonEffort", 1);

        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var reopened));
        using var reloaded = new SaveEngineSession(reopened!, null);
        Assert.Equal(PokerusStatus.Infectious, reloaded.GetPokerus(0, 0).Status);
        Assert.Equal(1, Assert.Single(reloaded.GetRibbons(0, 0), r => r.Id == "RibbonEffort").Value);
    }

    [Fact]
    public void ModernPokemonCanAffixOnlyAnOwnedRibbonOrMark()
    {
        using var session = Seed(8, new PK8 { Version = GameVersion.SW });
        session.SetRibbon(0, 0, "RibbonEffort", 1);

        var initial = session.GetAffixedRibbon(0, 0);
        Assert.True(initial.Supported);
        Assert.Equal(AffixedRibbon.None, initial.SelectedIndex);
        var effort = Assert.Single(initial.Choices, choice => choice.Id == (int)RibbonIndex.Effort);
        Assert.Equal("Effort", effort.Name);

        session.SetAffixedRibbon(0, 0, effort.Id);
        var selected = session.GetAffixedRibbon(0, 0);
        Assert.Equal((int)RibbonIndex.Effort, selected.SelectedIndex);
        Assert.Equal("Effort", selected.SelectedName);
        Assert.Equal((sbyte)RibbonIndex.Effort, Assert.IsAssignableFrom<IRibbonSetAffixed>(session.GetEntity(0, 0)).AffixedRibbon);

        Assert.Throws<ArgumentException>(() => session.SetAffixedRibbon(0, 0, (int)RibbonIndex.ChampionKalos));
        session.SetAffixedRibbon(0, 0, AffixedRibbon.None);
        Assert.Equal(AffixedRibbon.None, session.GetAffixedRibbon(0, 0).SelectedIndex);
    }

    [Fact]
    public void OlderFormatsDoNotOfferAnAffixedRibbonTitle()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM });
        Assert.False(session.GetAffixedRibbon(0, 0).Supported);
        Assert.Throws<NotSupportedException>(() => session.SetAffixedRibbon(0, 0, AffixedRibbon.None));
    }

    [Fact]
    public void ModernValidPokerusStrainIsPreservedWhenCured()
    {
        var mon = new PK7 { Version = GameVersion.UM, PokerusStrain = 15, PokerusDays = 1 };
        using var session = Seed(7, mon);
        session.SetPokerus(0, 0, PokerusStatus.Cured);
        var cured = session.GetPokerus(0, 0);
        Assert.Equal(15, cured.Strain);
        Assert.Equal(0, cured.Days);
    }

    private static SaveEngineSession Seed(int generation, PKM mon)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        mon.Species = 25;
        mon.CurrentLevel = 20;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
        return session;
    }
}
