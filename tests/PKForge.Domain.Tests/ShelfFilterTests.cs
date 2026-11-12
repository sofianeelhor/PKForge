using System.Text.Json;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ShelfFilterTests
{
    [Theory]
    [InlineData(null, "all")]
    [InlineData("", "all")]
    [InlineData("gen10", "all")]
    [InlineData("something-new", "all")]
    [InlineData("gb", "gb")]
    [InlineData("gbc", "gbc")]
    [InlineData("hacks", "hacks")]
    [InlineData("release", "release")]
    public void AStoredKeyIsKeptOnlyWhenKnown(string? stored, string expected) =>
        Assert.Equal(expected, ShelfFilter.Normalize(stored));

    [Theory]
    [InlineData("all", "All")]
    [InlineData("az", "A-Z")]
    [InlineData("gen4", "Gen IV")]
    [InlineData("gen9", "Gen IX")]
    [InlineData("gb", "Game Boy")]
    [InlineData("gbmono", "GB")]
    [InlineData("gbc", "GBC")]
    [InlineData("vanilla", "Vanilla")]
    [InlineData("hacks", "ROM hacks")]
    public void EveryKeyHasTheChipCaption(string key, string caption) =>
        Assert.Equal(caption, ShelfFilter.CaptionOf(key));

    [Theory]
    [InlineData(1, "Pokémon Red", "gbmono", true)]
    [InlineData(1, "Pokémon Yellow", "gbc", false)]
    [InlineData(2, "Pokémon Crystal", "gbc", true)]
    [InlineData(2, "Pokémon Gold", "gbmono", false)]
    [InlineData(1, "Pokémon Red", "gb", true)]
    [InlineData(2, "Pokémon Crystal", "gb", true)]
    [InlineData(3, "Pokémon Emerald", "gb", false)]
    [InlineData(7, "Pokémon Let's Go, Pikachu!", "switch", true)]
    [InlineData(7, "Pokémon Let's Go, Pikachu!", "3ds", false)]
    [InlineData(7, "Pokémon Sun", "3ds", true)]
    [InlineData(4, "Pokémon Platinum", "gen4", true)]
    [InlineData(4, "Pokémon Platinum", "gen5", false)]
    public void ConsolesAndGenerationsSplitTheShelf(int generation, string label, string key, bool shown) =>
        Assert.Equal(shown, ShelfFilter.Matches(key, generation, label, isRomHack: false));

    [Theory]
    [InlineData("vanilla", false, true)]
    [InlineData("vanilla", true, false)]
    [InlineData("hacks", true, true)]
    [InlineData("hacks", false, false)]
    [InlineData("all", true, true)]
    [InlineData("gen3", true, true)]
    public void VanillaAndHackFiltersFollowTheHackFlag(string key, bool isHack, bool shown) =>
        Assert.Equal(shown, ShelfFilter.Matches(key, 3, "Pokémon Emerald", isHack));

    [Fact]
    public void AHackIsWhatThePlayerMarkedOrAHackOnlyLayout()
    {
        var emerald = SaveIdentityRules.Guess("Emerald", 3, "rogue.sav", null, "rogue.sav");
        Assert.False(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(emerald, null), emerald));
        Assert.True(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(emerald, new SaveIdentity("doc", GameChoiceId: "hack-emerald")), emerald));
        Assert.False(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(emerald, new SaveIdentity("doc", GameChoiceId: "emerald")), emerald));

        var unbound = SaveIdentityRules.Guess("Unbound", 3, "unbound.sav", null, "unbound.sav");
        Assert.True(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(unbound, null), unbound));
        var radical = SaveIdentityRules.Guess("Radical Red", 3, "rr.sav", null, "rr.sav");
        Assert.True(ShelfFilter.IsRomHack(null, radical));
        Assert.False(ShelfFilter.IsRomHack(null, null));
    }

    [Theory]
    [InlineData("Recharged Yellow", SaveFormat.RechargedYellow, SaveLayoutFamily.RechargedYellow, "recharged-yellow")]
    [InlineData("Quetzal", SaveFormat.Quetzal, SaveLayoutFamily.Quetzal, "quetzal")]
    [InlineData("Soul Gold", SaveFormat.SoulGold, SaveLayoutFamily.SoulGold, "soulgold")]
    [InlineData("Elite Redux", SaveFormat.EliteRedux, SaveLayoutFamily.EliteRedux, "eliteredux")]
    [InlineData("Heart and Soul 2.0", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("pokeemerald-expansion", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Perfect Emerald", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Team Rocket Edition", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Lazarus", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Lucid", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Emerald Enhanced", SaveFormat.Expansion, SaveLayoutFamily.Expansion, "expansion")]
    [InlineData("Origin HeartGold", SaveFormat.OriginHeartGold, SaveLayoutFamily.OriginHeartGold, "originheartgold")]
    public void HackOnlyEnginesAreHacksWithTheirOwnSingleChoice(string game, SaveFormat format, SaveLayoutFamily family, string choiceId)
    {
        var guess = SaveIdentityRules.Guess(game, 3, "hack.sav", null, "hack.sav");
        Assert.Equal(format, guess.Format);
        Assert.Equal(family, guess.Family);
        Assert.True(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(guess, null), guess));
        var choice = Assert.Single(SaveIdentityRules.ChoicesFor(guess.Family, guess.Label));
        Assert.Equal(choiceId, choice.Id);
        Assert.True(choice.IsHack);
        Assert.Equal(format, SaveIdentityRules.FormatOfChoice(choiceId));
        Assert.True(ShelfFilter.IsRomHack(SaveIdentityResolver.Resolve(guess, new SaveIdentity("doc", GameChoiceId: choiceId)), guess));
    }

    [Fact]
    public void WonderCardFiltersRoundTripWithoutGameSpecificChoices()
    {
        var defaults = new WonderCardQuery { Grouping = WonderCardGrouping.Year };
        var chosen = defaults with
        {
            Kind = WonderCardKindFilter.Pokemon, ShinyOnly = true, CompatibleOnly = true,
            Received = WonderCardReceivedFilter.NotReceived, OnePerEvent = false, Sort = WonderCardSort.DexNumber,
            Grouping = WonderCardGrouping.Species, Language = "FR",
            Search = "pikachu", Year = 2016, Species = 25, Series = "Movie",
        };

        var restored = WonderCardQueryMemory.Restore(WonderCardQueryMemory.Save(chosen, defaults), defaults);

        Assert.Equal(chosen with { Search = "", Year = null, Species = null, Series = null }, restored);
    }

    [Fact]
    public void AnUntouchedGroupingFollowsTheNextGallerysDefault()
    {
        var stored = WonderCardQueryMemory.Save(new WonderCardQuery { Grouping = WonderCardGrouping.Year }, new WonderCardQuery { Grouping = WonderCardGrouping.Year });
        var other = new WonderCardQuery { Grouping = WonderCardGrouping.Games };
        Assert.Equal(WonderCardGrouping.Games, WonderCardQueryMemory.Restore(stored, other).Grouping);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("kind=99;sort=-1;group=x;=;shiny")]
    public void BrokenStoredFiltersFallBackToTheDefaults(string? stored)
    {
        var defaults = new WonderCardQuery { Grouping = WonderCardGrouping.Series };
        Assert.Equal(defaults, WonderCardQueryMemory.Restore(stored, defaults));
    }

    [Fact]
    public void ALinkedFileNeverWaitsToBeRelinkedOnAnotherDevice()
    {
        List<WatchedRoot> roots =
        [
            new(EmulatorKind.RetroArch, "content://tree/a", "RetroArch"),
            new(EmulatorKind.LinkedFile, "content://doc/b", "Emerald.sav"),
        ];
        var archived = new[] { new PreferenceValue(DataTransfer.WatchedRootsKey, "string", JsonSerializer.Serialize(roots)) };

        var relink = DataTransfer.ToRelink([], archived);

        Assert.Equal([EmulatorKind.RetroArch], relink.Select(r => r.Kind));
    }
}
