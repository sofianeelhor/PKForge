using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// Encounter cards: the read-only "how do I get this?" surface over the pinned
/// encounter database, and the CATCH placement that materializes a legal Pokémon
/// from one card. Species-first: no mon is needed to ask the question.
/// </summary>
public sealed class EncounterCardTests
{
    private readonly ITestOutputHelper _output;
    public EncounterCardTests(ITestOutputHelper output) => _output = output;

    private static int IndexOf(IReadOnlyList<EncounterCard> cards, Predicate<EncounterCard> match)
    {
        for (var i = 0; i < cards.Count; i++)
            if (match(cards[i]))
                return i;
        return -1;
    }

    private static UnboundEngineSession? OpenUnboundGroundTruth()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "unbound-v2111.srm");
        return path is not null && File.Exists(path) ? new UnboundEngineSession(File.ReadAllBytes(path), "Unbound") : null;
    }

    [Fact]
    public void PikachuInPlatinumListsTrophyGardenWildAndEggs()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);

        var cards = session.GetEncounterCards(25, 0);

        _output.WriteLine($"{cards.Count} cards");
        foreach (var card in cards.Take(12))
            _output.WriteLine($"{card.Kind} | {card.Location} | {card.LevelMin}-{card.LevelMax} | {card.GameName} | {card.Detail}");

        Assert.NotEmpty(cards);
        Assert.Contains(cards, c => c.Kind == "Wild" && c.Location.Contains("Trophy Garden", StringComparison.Ordinal));
        Assert.Contains(cards, c => c.Kind == "Egg");
        Assert.All(cards, c =>
        {
            Assert.True(c.LevelMin is >= 1 and <= 100);
            Assert.True(c.LevelMin <= c.LevelMax && c.LevelMax <= 100);
            Assert.False(string.IsNullOrEmpty(c.GameName));
        });
    }

    [Fact]
    public void EncounterCardsAreDeterministic()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);
        Assert.Equal(session.GetEncounterCards(25, 0), session.GetEncounterCards(25, 0));
    }

    [Fact]
    public void UnobtainableSpeciesAndBadIdsHaveNoCards()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);
        Assert.Empty(session.GetEncounterCards(0, 0)); // invalid id
        Assert.Empty(session.GetEncounterCards(9999, 0)); // beyond MaxSpeciesID
    }

    [Fact]
    public void PlaceEncounterPutsALegalWildCatchInTheEmptySlot()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);
        var cards = session.GetEncounterCards(25, 0);
        var index = IndexOf(cards, c => c.Kind == "Wild" && c.Location.Contains("Trophy Garden", StringComparison.Ordinal));
        Assert.True(index >= 0, "Trophy Garden wild card must exist");

        var outcome = session.PlaceEncounter(25, 0, index, 0, 1);

        Assert.True(outcome.Success, outcome.Message);
        var placed = session.ReadEntity(0, 1);
        Assert.False(placed.IsEmpty);
        Assert.True(placed.Species is 25 or 172, $"placed species {placed.Species}");
    }

    [Fact]
    public void PlaceEncounterSurvivesSerializeRoundTrip()
    {
        var save = BlankSaveFile.Get(GameVersion.B2, "PKForge", LanguageID.English);
        using var session = new SaveEngineSession(save, null);

        var cards = session.GetEncounterCards(504, 0);
        _output.WriteLine($"{cards.Count} cards for Patrat in B2");
        foreach (var card in cards.Take(8))
            _output.WriteLine($"{card.Kind} | {card.Location} | {card.LevelMin}-{card.LevelMax} | {card.GameName} | {card.Detail}");
        Assert.NotEmpty(cards);
        var index = IndexOf(cards, c => c.Kind == "Wild");
        if (index < 0) index = 0; // eggs are the fallback path in games without wild tables

        Assert.True(session.PlaceEncounter(504, 0, index, 0, 0).Success);
        var species = session.ReadEntity(0, 0).Species;

        var serialized = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(serialized, out var reloaded));
        using var reopened = new SaveEngineSession(reloaded!, null);
        Assert.Equal(species, reopened.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void PlaceEncounterRefusesOccupiedSlotsAndStaleIndexes()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);
        var cards = session.GetEncounterCards(25, 0);
        Assert.True(session.PlaceEncounter(25, 0, 0, 0, 0).Success);

        var occupied = session.PlaceEncounter(25, 0, 0, 0, 0);
        Assert.False(occupied.Success);

        var stale = session.PlaceEncounter(25, 0, cards.Count + 7, 0, 1);
        Assert.False(stale.Success);
        Assert.True(session.ReadEntity(0, 1).IsEmpty, "a refused placement must leave the destination empty");
    }

    [Fact]
    public void UnboundHasNoEncounterCards()
    {
        using var unbound = OpenUnboundGroundTruth();
        if (unbound is null) return; // gitignored ground truth: dev-only, skipped on CI
        Assert.Empty(unbound.GetEncounterCards(25, 0));
    }
}
