using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class EventGiftFilterTests
{
    private static readonly EventGiftSaveProfile Save = new(7, 2, "Ultra Moon");

    private static EventGift Gift(int generation = 7, int language = 0, int? year = null) =>
        new(0, "Test Card", "Card #: 0001 - Test Card", 25, 50, false, 1, generation, language, year);

    [Theory]
    [InlineData(7, 0, true)]   // same generation, distributed in every language
    [InlineData(7, 2, true)]   // same generation, restricted exactly to the save's language
    [InlineData(7, 1, false)]  // same generation, but a Japanese-only card in an English save
    [InlineData(6, 0, false)]  // another generation's card never fits
    [InlineData(6, 2, false)]
    public void CompatibilityFollowsGenerationThenLanguage(int generation, int language, bool expected) =>
        Assert.Equal(expected, EventGiftRules.IsCompatible(Gift(generation, language), Save));

    [Fact]
    public void ASaveWithoutAKnownLanguageMatchesEveryRestriction()
    {
        var unknownLanguage = Save with { Language = -1 };

        Assert.True(EventGiftRules.IsCompatible(Gift(language: 1), unknownLanguage));
        Assert.True(EventGiftRules.IsCompatible(Gift(language: 3), Save with { Language = 0 }));
    }

    [Fact]
    public void NoSessionNeverNarrows()
    {
        Assert.False(EventGiftRules.IsCompatible(Gift(generation: 4, language: 1), null));
        Assert.True(new EventGiftFilter().Matches(Gift(generation: 4, language: 1), null));
        Assert.True(new EventGiftFilter(CompatibleOnly: true).Matches(Gift(), null));
    }

    [Fact]
    public void DefaultFilterBrowsesEverythingUnchanged()
    {
        var filter = new EventGiftFilter();

        Assert.False(filter.IsActive);
        Assert.True(filter.Matches(Gift(generation: 4, language: 1, year: 2011), Save));
        Assert.True(filter.Matches(Gift(), null));
    }

    [Fact]
    public void CompatibleOnlyHidesForeignGenerationAndLanguage()
    {
        var filter = new EventGiftFilter(CompatibleOnly: true);

        Assert.True(filter.Matches(Gift(), Save));
        Assert.True(filter.Matches(Gift(language: 2), Save));
        Assert.False(filter.Matches(Gift(language: 1), Save));
        Assert.False(filter.Matches(Gift(generation: 8), Save));
    }

    [Fact]
    public void GenerationFilterKeepsOnlyThatGeneration()
    {
        var filter = new EventGiftFilter(Generation: 5);

        Assert.True(filter.IsActive);
        Assert.True(filter.Matches(Gift(generation: 5, year: 2012), Save));
        Assert.False(filter.Matches(Gift(generation: 7), Save));
    }

    [Fact]
    public void YearFilterKeepsOnlyCardsDatedThatYear()
    {
        var filter = new EventGiftFilter(Year: 2018);

        Assert.True(filter.Matches(Gift(year: 2018), Save));
        Assert.False(filter.Matches(Gift(year: 2016), Save));
        Assert.False(filter.Matches(Gift(year: null), Save)); // undated cards cannot claim a year
    }

    [Fact]
    public void FiltersCombineAsAnAnd()
    {
        var filter = new EventGiftFilter(CompatibleOnly: true, Year: 2018);

        Assert.True(filter.Matches(Gift(language: 2, year: 2018), Save));
        Assert.False(filter.Matches(Gift(language: 2, year: 2019), Save)); // right language, wrong year
        Assert.False(filter.Matches(Gift(language: 1, year: 2018), Save)); // right year, wrong language
    }
}
