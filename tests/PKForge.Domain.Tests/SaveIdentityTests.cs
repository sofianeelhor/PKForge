using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The shelf identity is what the engine read from the bytes. File names are user-controlled
/// and only prefill Rename. Regression: "Pokémon version Or HeartGold" (a French dump) sat
/// beside "Pokémon HeartGold" on Home as a logo-less "ROM hack".
/// </summary>
public sealed class SaveIdentityRulesTests
{
    private static SaveIdentityGuess Guess(string? game, int gen, string file, string? rom = null) =>
        SaveIdentityRules.Guess(game, gen, file, rom, file);

    [Theory]
    [InlineData("HeartGold", 4, "Pokémon version Or HeartGold.sav")]
    [InlineData("HeartGold", 4, "Pokémon Goldene Edition HeartGold (Germany).sav")]
    [InlineData("HeartGold", 4, "Pocket Monsters Heart Gold (Japan).sav")]
    [InlineData("Emerald", 3, "Pokémon Version Émeraude.srm")]
    [InlineData("Emerald", 3, "Pokemon Emerald Rogue.srm")]
    [InlineData("Emerald", 3, "Heart&Soul.sav")]
    [InlineData("FireRed", 3, "Pokémon Edición Rojo Fuego.sav")]
    [InlineData("Platinum", 4, "Pokemon Versione Platino [ita].sav")]
    [InlineData("Black 2", 5, "Pokémon Version Noire 2.sav")]
    [InlineData("Emerald", 3, "main.sav")]
    public void TheLabelAndArtAreTheEnginesGame_WhateverTheFileIsCalled(string game, int gen, string file)
    {
        var guess = Guess(game, gen, file);
        Assert.Equal($"Pokémon {game}", guess.Label);
        Assert.Equal($"Pokémon {game}", guess.ArtLabel);
        Assert.Equal(SaveFormat.Standard, guess.Format);
    }

    [Fact]
    public void TheRomBesideTheSaveCannotRelabelIt()
    {
        Assert.Equal("Pokémon FireRed", Guess("FireRed", 3, "slot1.sav", "Pokemon Radical Red 4.1.gba").Label);
    }

    [Fact]
    public void CfruGamesComeFromTheEnginesByteDetection()
    {
        var rr = Guess("Radical Red", 3, "game.srm");
        Assert.Equal("Pokémon Radical Red", rr.Label);
        Assert.Equal(SaveFormat.RadicalRed, rr.Format);
        Assert.Equal(SaveLayoutFamily.Cfru, rr.Family);
        var unbound = Guess("Unbound", 3, "Some Other Hack.srm");
        Assert.Equal("Pokémon Unbound", unbound.Label);
        Assert.Equal(SaveFormat.Unbound, unbound.Format);
    }

    [Fact]
    public void AnUnnamedGenerationUsesThePlatformLabel()
    {
        var guess = SaveIdentityRules.Guess("Generation 3", 3, "x.sav", null, "GBA save");
        Assert.Equal("GBA save", guess.Label);
        Assert.Equal(SaveFormat.Auto, guess.Format);
    }

    [Theory]
    [InlineData("Pokemon - Emerald Rogue (v1.3) [!].srm", "Emerald Rogue")]
    [InlineData("Heart&Soul.sav", "Heart&Soul")]
    [InlineData("pokemon_inflamed-red.sav", "inflamed red")]
    [InlineData("(USA).sav", null)]
    public void TheFileNameIsOnlyARenamePrefill(string file, string? expected)
    {
        Assert.Equal(expected, SaveIdentityRules.SuggestedNameOf(file));
        Assert.Equal(expected, Guess("Emerald", 3, file).SuggestedName);
    }

    [Fact]
    public void CfruSavesCanOnlyBeRelabelledAsCfruGames()
    {
        var choices = SaveIdentityRules.ChoicesFor(SaveLayoutFamily.Cfru, "x");
        Assert.All(choices, c => Assert.Equal(SaveFormat.RadicalRed, c.Format));
        Assert.DoesNotContain(choices, c => c.Id == "firered");
    }

    [Fact]
    public void ChoiceIdsRoundTripToTheirFormat()
    {
        foreach (var family in Enum.GetValues<SaveLayoutFamily>())
            foreach (var choice in SaveIdentityRules.ChoicesFor(family, "Pokémon Platinum"))
                Assert.Equal(choice.Format, SaveIdentityRules.FormatOfChoice(choice.Id));
    }
}

public sealed class SaveIdentityResolverTests
{
    private static readonly SaveIdentityGuess Emerald = SaveIdentityRules.Guess("Emerald", 3, "Pokemon Emerald Rogue.srm", null, "x");

    [Fact]
    public void WithoutUserInputTheShelfShowsTheDetectedGame()
    {
        var resolved = SaveIdentityResolver.Resolve(Emerald, null);
        Assert.Equal("Pokémon Emerald", resolved.DisplayName);
        Assert.Equal("Pokémon Emerald", resolved.ArtLabel);
        Assert.False(resolved.IsCustomized);
    }

    [Fact]
    public void CustomNameColorAndGameWin()
    {
        var identity = new SaveIdentity("doc", DisplayName: "My Rogue run", ColorKey: "coral", GameChoiceId: "hack-emerald");
        var resolved = SaveIdentityResolver.Resolve(Emerald, identity);
        Assert.Equal("My Rogue run", resolved.DisplayName);
        Assert.Equal("Emerald-based ROM hack", resolved.GameLabel);
        Assert.Null(resolved.ArtLabel);
        Assert.Equal("coral", resolved.ColorKey);
        Assert.True(resolved.IsCustomized);
        Assert.True(resolved.IsRenamed);
    }

    [Fact]
    public void HiddenIsCarriedThrough()
    {
        Assert.True(SaveIdentityResolver.Resolve(Emerald, new SaveIdentity("doc", Hidden: true)).IsHidden);
    }

    [Fact]
    public void UnknownColorKeysAreIgnored()
    {
        Assert.Null(SaveIdentityResolver.Resolve(Emerald, new SaveIdentity("doc", ColorKey: "neon")).ColorKey);
    }

    [Fact]
    public void ChoiceFromAnotherFamilyCannotChangeTheRoute()
    {
        // A persisted "radicalred" choice on an Emerald layout must not route the CFRU engine.
        var resolved = SaveIdentityResolver.Resolve(Emerald, new SaveIdentity("doc", GameChoiceId: "radicalred"));
        Assert.Equal(SaveFormat.Standard, resolved.Format);
    }
}

public sealed class SaveShelfGroupingTests
{
    private static DetectedSave Scan(string id, string game, int gen, string file, SaveIdentity? identity = null, int day = 1)
    {
        var guess = SaveIdentityRules.Guess(game, gen, file, null, file);
        var resolved = SaveIdentityResolver.Resolve(guess, identity);
        return new DetectedSave(id, file, resolved.DisplayName, EmulatorKind.MelonDS, false,
            new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero), gen, Guess: guess, Identity: resolved);
    }

    [Theory]
    [InlineData("HeartGold", 4, "Pokemon HeartGold.sav", "Pokémon version Or HeartGold.sav", "Pocket Monsters Heart Gold.sav")]
    [InlineData("Emerald", 3, "Pokemon Emerald (U).sav", "Pokémon Version Émeraude.sav", "Pokemon Smaragd-Edition.sav")]
    [InlineData("FireRed", 3, "Pokemon FireRed.sav", "Pokémon Version Rouge Feu.sav", "Pokemon Versione Rosso Fuoco.sav")]
    [InlineData("Platinum", 4, "Pokemon Platinum.sav", "Pokémon Edición Platino.sav", "game.sav")]
    [InlineData("Black 2", 5, "Pokemon Black 2.sav", "Pokémon Version Noire 2.sav", "Pokemon Versione Nera 2.sav")]
    public void OneGameInEveryLanguageSharesOneTileWithItsLogo(string game, int gen, string a, string b, string c)
    {
        var tile = Assert.Single(SaveShelf.Group([Scan("1", game, gen, a), Scan("2", game, gen, b), Scan("3", game, gen, c)]));
        Assert.Equal(3, tile.Count);
        Assert.All(tile, s => Assert.Equal($"Pokémon {game}", s.ArtLabel));
    }

    [Fact]
    public void GroupsPutTheNewestSaveFirst()
    {
        var tile = Assert.Single(SaveShelf.Group([Scan("old", "Emerald", 3, "emerald.sav", day: 1), Scan("new", "Emerald", 3, "rogue.sav", day: 9)]));
        Assert.Equal("new", tile[0].DocumentId);
    }

    [Fact]
    public void CustomizedSavesGetTheirOwnTile()
    {
        var plain = SaveShelf.GroupKey(Scan("1", "Emerald", 3, "Pokemon Emerald.sav"));
        Assert.NotEqual(plain, SaveShelf.GroupKey(Scan("2", "Emerald", 3, "e.sav", new SaveIdentity("2", ColorKey: "coral"))));
        Assert.NotEqual(plain, SaveShelf.GroupKey(Scan("3", "Emerald", 3, "e.sav", new SaveIdentity("3", DisplayName: "Pokémon Emerald"))));
        Assert.NotEqual(plain, SaveShelf.GroupKey(Scan("4", "Emerald", 3, "e.sav", new SaveIdentity("4", GameChoiceId: "emerald"))));
        Assert.NotEqual(plain, SaveShelf.GroupKey(Scan("5", "Emerald", 3, "e.sav", new SaveIdentity("5", DisplayName: "Rogue", GameChoiceId: "hack-emerald"))));
        // The file name alone never splits a tile.
        Assert.Equal(plain, SaveShelf.GroupKey(Scan("6", "Emerald", 3, "Pokemon Emerald Rogue.sav")));
    }

    [Fact]
    public void DifferentGamesNeverShareATile()
    {
        Assert.Equal(2, SaveShelf.Group([Scan("1", "HeartGold", 4, "a.sav"), Scan("2", "SoulSilver", 4, "b.sav")]).Count);
    }
}

public sealed class JsonSaveIdentityStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pkforge-identity-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_dir, "save-identities.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void IdentitySurvivesARestart()
    {
        var store = new JsonSaveIdentityStore(PathName);
        store.Set(new SaveIdentity("content://a", DisplayName: "  Rogue  ", ColorKey: "teal", GameChoiceId: "hack-emerald", Hidden: true));

        var reopened = new JsonSaveIdentityStore(PathName).Get("content://a");
        Assert.NotNull(reopened);
        Assert.Equal("Rogue", reopened!.DisplayName);
        Assert.Equal("teal", reopened.ColorKey);
        Assert.Equal("hack-emerald", reopened.GameChoiceId);
        Assert.True(reopened.Hidden);
    }

    [Fact]
    public void RomHackRiskAcceptanceSurvivesARestartAndCanBeRevoked()
    {
        var store = new JsonSaveIdentityStore(PathName);
        store.Set(new SaveIdentity("content://hack", AcceptedHackRisk: true));
        Assert.True(new JsonSaveIdentityStore(PathName).Get("content://hack")?.AcceptedHackRisk);

        // Acceptance alone is a customisation; withdrawing it leaves nothing behind.
        store.Set(store.Get("content://hack")! with { AcceptedHackRisk = false });
        Assert.Null(new JsonSaveIdentityStore(PathName).Get("content://hack"));
    }

    [Fact]
    public void ResetForgetsEverythingAndNotifies()
    {
        var store = new JsonSaveIdentityStore(PathName);
        store.Set(new SaveIdentity("doc", DisplayName: "x", Hidden: true));
        string? changed = null;
        store.Changed += id => changed = id;

        store.Reset("doc");

        Assert.Equal("doc", changed);
        Assert.Null(store.Get("doc"));
    }

    [Fact]
    public void AnEmptyIdentityLeavesNoEntry()
    {
        var store = new JsonSaveIdentityStore(null);
        store.Set(new SaveIdentity("doc", Hidden: true));
        store.Set(new SaveIdentity("doc"));
        Assert.Null(store.Get("doc"));
    }

    [Fact]
    public void EntriesWithOnlyRetiredFlagsAreDroppedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, """[{"DocumentId":"doc","Confirmed":true,"NeedsConfirmation":true}]""");
        Assert.Null(new JsonSaveIdentityStore(PathName).Get("doc"));
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, "{ not json");
        Assert.Null(new JsonSaveIdentityStore(PathName).Get("doc"));
    }
}

public sealed class EmulatorSaveHintTests
{
    [Fact]
    public void SiblingRomWithTheSameStemIsFound()
    {
        Assert.Equal("Heart&Soul.gba",
            EmulatorSaveHeuristics.FindSiblingRom("Heart&Soul.sav", ["Heart&Soul.gba", "other.gba", "Heart&Soul.sav"]));
        Assert.Null(EmulatorSaveHeuristics.FindSiblingRom("a.sav", ["b.gba"]));
    }

    [Theory]
    [InlineData("primary:RetroArch/saves/mGBA/x.srm", "saves/mGBA")]
    [InlineData("primary:x.srm", null)]
    [InlineData("primary:Saves/x.sav", "Saves")]
    public void FolderHintIsTheLastTwoFolders(string documentId, string? expected)
    {
        Assert.Equal(expected, EmulatorSaveHeuristics.FolderHint(documentId));
    }

    [Theory]
    [InlineData("FireRed / LeafGreen", SaveLayoutFamily.FireRedLeafGreen)]
    [InlineData("Ruby / Sapphire", SaveLayoutFamily.RubySapphire)]
    [InlineData("Diamond / Pearl", SaveLayoutFamily.DiamondPearl)]
    public void AnUnplacedPairAsksOnceUntilChosen(string engineGame, SaveLayoutFamily family)
    {
        var guess = SaveIdentityRules.Guess(engineGame, 3, "main.sav", null, "main.sav");
        Assert.Equal(family, guess.Family);
        Assert.True(SaveIdentityRules.NeedsEditionChoice(guess, null));
        var edition = SaveIdentityRules.ChoicesFor(family, guess.Label).First(c => !c.IsHack);
        Assert.False(SaveIdentityRules.NeedsEditionChoice(guess, edition.Id));
        Assert.Equal(SaveFormat.Standard, SaveIdentityRules.FormatOfChoice(edition.Id));
    }

    [Theory]
    [InlineData("LeafGreen")]
    [InlineData("Sapphire")]
    [InlineData("Pearl")]
    [InlineData("Emerald")]
    public void AnIdentifiedEditionNeverAsks(string engineGame)
    {
        var guess = SaveIdentityRules.Guess(engineGame, 3, "main.sav", null, "main.sav");
        Assert.False(SaveIdentityRules.NeedsEditionChoice(guess, null));
    }
}
