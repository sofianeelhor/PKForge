using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The egg tools (issue #75) on blank saves of every generation with eggs: a real egg, its hatch
/// counter kept within the species' range, its starting moves checked by PKHeX, hatching with
/// the met data the game writes, and a Pokémon turned back into an egg of its line, each legal.
/// </summary>
public sealed class EggToolsTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    public static TheoryData<GameVersion, ushort> Games => new()
    {
        { GameVersion.E, (ushort)Species.Treecko },
        { GameVersion.FR, (ushort)Species.Bulbasaur },
        { GameVersion.Pt, (ushort)Species.Chimchar },
        { GameVersion.HG, (ushort)Species.Cyndaquil },
        { GameVersion.B2, (ushort)Species.Snivy },
        { GameVersion.OR, (ushort)Species.Mudkip },
        { GameVersion.US, (ushort)Species.Rowlet },
        { GameVersion.SW, (ushort)Species.Scorbunny },
        { GameVersion.BD, (ushort)Species.Piplup },
        { GameVersion.SL, (ushort)Species.Sprigatito },
    };

    private static void LayEgg(SaveEngineSession session, ushort species)
    {
        var egg = EggService.CreateEgg(session.SaveFile, species, 0, EncounterCriteria.Unrestricted, out _);
        Assert.NotNull(egg);
        session.SetEntity(0, 0, egg!);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AnEggIsRealAndItsCounterStaysInRange(GameVersion version, ushort species)
    {
        using var session = Blank(version);
        Assert.True(EggService.IsSupported(session));
        LayEgg(session, species);

        var status = EggService.Get(session, 0, 0);
        Assert.True(status.IsEgg);
        Assert.True(status.Legal, new LegalityAnalysis(session.GetEntity(0, 0)).Report());
        Assert.Equal(status.MaxHatchCounter, status.HatchCounter);
        Assert.Equal(EggStateLegality.GetEggLevel(session.SaveFile.Generation), session.GetEntity(0, 0).CurrentLevel);

        var low = Math.Max(status.MinHatchCounter, 1);
        Assert.True(EggService.SetHatchCounter(session, 0, 0, low).Success);
        Assert.Equal(low, EggService.Get(session, 0, 0).HatchCounter);
        Assert.True(EggService.Get(session, 0, 0).Legal);
        Assert.False(EggService.SetHatchCounter(session, 0, 0, status.MaxHatchCounter + 1).Success);
        Assert.False(EggService.SetHatchCounter(session, 0, 0, status.MinHatchCounter - 1).Success);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void HatchingWritesLegalMetDataAndMakeEggGoesBack(GameVersion version, ushort species)
    {
        using var session = Blank(version);
        LayEgg(session, species);

        var hatched = EggService.Hatch(session, 0, 0);
        Assert.True(hatched.Success, hatched.Message);
        var pk = session.GetEntity(0, 0);
        Assert.False(pk.IsEgg);
        var legality = new LegalityAnalysis(pk);
        Assert.True(legality.Valid, legality.Report());
        Assert.Equal(EggStateLegality.GetEggHatchFriendship(pk.Context), pk.OriginalTrainerFriendship);
        Assert.False(EggService.Hatch(session, 0, 0).Success);

        // Turned back into an egg of its line, with its nature.
        var nature = pk.Nature;
        var made = EggService.MakeEgg(session, 0, 0);
        Assert.True(made.Success, made.Message);
        var egg = session.GetEntity(0, 0);
        Assert.True(egg.IsEgg);
        Assert.Equal(species, egg.Species);
        Assert.Equal(nature, egg.Nature);
        var eggLegality = new LegalityAnalysis(egg);
        Assert.True(eggLegality.Valid, eggLegality.Report());
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void EggMovesAreCheckedByPkhex(GameVersion version, ushort species)
    {
        using var session = Blank(version);
        LayEgg(session, species);

        var choices = EggService.GetEggMoveChoices(session, 0, 0);
        Assert.NotEmpty(choices);
        var eggMove = choices.FirstOrDefault(c => c.Source == "Egg move");
        var current = EggService.Get(session, 0, 0).Moves;
        if (eggMove is not null)
        {
            var set = current.Where(m => m != eggMove.Move).Take(3).Append(eggMove.Move).ToArray();
            var outcome = EggService.SetEggMoves(session, 0, 0, set);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Contains(eggMove.Move, EggService.Get(session, 0, 0).Moves);
            Assert.True(EggService.Get(session, 0, 0).Legal, new LegalityAnalysis(session.GetEntity(0, 0)).Report());
        }

        // A signature move no egg starts with is refused, and nothing changes.
        var before = EggService.Get(session, 0, 0).Moves;
        Assert.False(EggService.SetEggMoves(session, 0, 0, [(int)Move.Vcreate]).Success);
        Assert.Equal(before, EggService.Get(session, 0, 0).Moves);
    }

    [Fact]
    public void ALegendaryNeverBecomesAnEgg()
    {
        using var session = Blank(GameVersion.E);
        LayEgg(session, (ushort)Species.Treecko);
        EggService.Hatch(session, 0, 0);
        var pk = session.GetEntity(0, 0);
        pk.Species = (ushort)Species.Groudon;
        session.SetEntity(0, 0, pk);
        Assert.NotNull(EggService.Get(session, 0, 0).MakeEggRefusal);
        Assert.False(EggService.MakeEgg(session, 0, 0).Success);
    }

    [Fact]
    public void GamesWithoutEggsAreNotOffered()
    {
        using var session = Blank(GameVersion.GP);
        Assert.False(EggService.IsSupported(session));
    }
}
