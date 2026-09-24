using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class WonderCardAlbumTests
{
    private static readonly EventGiftSaveProfile EnglishSave = new(4, 2, "HeartGold");

    private static readonly Dictionary<int, string> Names = new()
    {
        [25] = "Pikachu", [151] = "Mew", [385] = "Jirachi", [491] = "Darkrai", [490] = "Manaphy", [492] = "Shaymin",
    };

    private static string Name(int species) => Names.GetValueOrDefault(species, $"#{species}");

    private static EventGift Gift(int id, string title, int species = 25, string? source = null, int cardId = 1,
        int? year = null, bool shiny = false, int level = 50, EventGiftKind kind = EventGiftKind.Pokemon,
        DateOnly? date = null, int language = 0, string ot = "", IReadOnlyList<string>? moves = null, string item = "") =>
        new(id, title, $"Card #: {cardId:0000} - {title}", kind == EventGiftKind.Item ? 0 : species, level, shiny, cardId, 4, language, year,
            kind, new EventGiftDetails
            {
                SourceFile = source, Date = date, OriginalTrainer = ot, Moves = moves ?? [],
                Items = kind == EventGiftKind.Item ? [new EventGiftItem(50, item, 5)] : [],
            });

    private static IReadOnlyList<WonderCardEntry> Entries(params EventGift[] gifts) =>
        WonderCardAlbum.CreateEntries(gifts, Name, EnglishSave, _ => false);

    // ── Source file names ──────────────────────────────────────────────────

    [Fact]
    public void SourceParsesNumberGamesNameLanguageAndRegion()
    {
        var source = WonderCardSource.Parse("Wondercards/ENG/007 DP - Movie Darkrai (NA) (ENG).wc4")!;

        Assert.Equal("007", source.Number);
        Assert.Equal("DP", source.Games);
        Assert.Equal("Movie Darkrai", source.Name);
        Assert.Equal("ENG", source.Language);
        Assert.Equal("NA", source.Region);
        Assert.Empty(source.Tags);
    }

    [Fact]
    public void SourceKeepsVariantTagsAndCollection()
    {
        var source = WonderCardSource.Parse("Wondercards/SPA/0538 ORAS - JUN2015 Dragonite (SPA) (F) (C).wc6")!;
        Assert.Equal("SPA", source.Language);
        Assert.Equal(["F", "C"], source.Tags);

        var ranked = WonderCardSource.Parse("SwSh/Wondercards/Ranked Battles/Doubles S4/0007 SWSH - Doubles S4 Item Gold Bottle Cap.wc8")!;
        Assert.Null(ranked.Language);
        Assert.Equal("Ranked Battles / Doubles S4", ranked.Collection);
        Assert.Equal("Doubles S4 Item Gold Bottle Cap", ranked.Name);
    }

    [Fact]
    public void SourceToleratesUnnumberedAndOddNames()
    {
        var egg = WonderCardSource.Parse("Pokemon Ranger Manaphy Egg/DPPtHGSS - Manaphy Egg.pgt")!;
        Assert.Equal("DPPtHGSS", egg.Games);
        Assert.Equal("Manaphy Egg", egg.Name);
        Assert.Equal("", egg.Number);

        var tight = WonderCardSource.Parse("Wondercards/FRE/1509 ORAS- PGL Amaura (FRE).wc6")!;
        Assert.Equal(("1509", "ORAS", "PGL Amaura", "FRE"), (tight.Number, tight.Games, tight.Name, tight.Language));
        Assert.Equal("Item Master Ball", WonderCardSource.Parse("SV/Wondercards/1531 SV  - Item Master Ball.wc9")!.Name);

        Assert.Equal("weird", WonderCardSource.Parse("weird.wc4")!.Name);
        Assert.Null(WonderCardSource.Parse(null));
        Assert.Null(WonderCardSource.Parse("  "));
    }

    // ── Titles ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScriptDetectionTellsJapaneseKoreanAndLatinApart()
    {
        Assert.Equal("JPN", GiftLanguages.DetectScript("おたんじょうび きねん"));
        Assert.Equal("KOR", GiftLanguages.DetectScript("이클립스"));
        Assert.Equal("CHS", GiftLanguages.DetectScript("配信"));
        Assert.Null(GiftLanguages.DetectScript("Cadeau d'anniversaire"));
        Assert.True(GiftLanguages.IsReadable("Pokémon Center"));
        Assert.False(GiftLanguages.IsReadable("ポケセン"));
        Assert.False(GiftLanguages.IsReadable("Ｃａｆｅ"));   // fullwidth Latin
        Assert.False(GiftLanguages.IsReadable("「Present」"));  // CJK brackets
        Assert.Equal("Pikachu", GiftTitles.TranslateEventName("ポケモン - Ｃａｆｅ Pikachu"));
    }

    [Fact]
    public void JapaneseCardBorrowsItsEnglishSiblingsTitle()
    {
        var entries = Entries(
            Gift(0, "おたんじょうびきねん プレゼント", source: "Wondercards/JPN/014 DP - Birthday Pikachu (JPN).wc4", cardId: 14),
            Gift(1, "Birthday Present", source: "Wondercards/ENG/014 DP - Birthday Pikachu (ENG).wc4", cardId: 14));

        var japanese = entries.Single(e => e.LanguageTag == "JPN");
        Assert.Equal("Birthday Present", japanese.Title);
        Assert.Equal("おたんじょうびきねん プレゼント", japanese.OriginalTitle);
        Assert.True(japanese.TitleTranslated);
        Assert.False(entries.Single(e => e.LanguageTag == "ENG").TitleTranslated);
    }

    [Fact]
    public void WithoutASiblingTheEnglishEventNameIsUsedWithVenuesTranslated()
    {
        var entries = Entries(Gift(0, "ポケセン ピカチュウ", source: "Wondercards/JPN/0136 ORAS - ポケセン Pikachu (JPN).wc6"));

        Assert.Equal("Pokémon Center Pikachu", entries[0].Title);
        Assert.Equal("Pokémon Center", entries[0].Series);
    }

    [Fact]
    public void NonEnglishLatinCardPrefersTheEnglishEventName()
    {
        var entries = Entries(Gift(0, "Darkrai du film", 491, source: "Wondercards/FRE/010 DP - Movie Darkrai (FRE).wc4"));
        Assert.Equal("Movie Darkrai", entries[0].Title);
        Assert.Equal("FRE", entries[0].LanguageTag);
    }

    [Fact]
    public void ItemEventNamesDropTheItemPrefix()
    {
        var entries = Entries(Gift(0, "ポケモンスクラップ ふしぎなアメ", kind: EventGiftKind.Item, item: "Rare Candy",
            source: "Wondercards/JPN/0056 ORAS - Item Rare Candy (JPN).wc6"));
        Assert.Equal("Rare Candy", entries[0].Title);
        Assert.Equal("Items", entries[0].Series);
    }

    [Fact]
    public void UnsourcedUnreadableCardFallsBackToTheSpecies()
    {
        var entries = Entries(Gift(0, "まぼろしのポケモン", 151));
        Assert.Equal("Mew gift", entries[0].Title);
        Assert.Equal("JPN", entries[0].LanguageTag); // told by the script alone
    }

    [Fact]
    public void RestrictionLanguageTagsAnUnsourcedCard()
    {
        var entries = Entries(Gift(0, "Wishing Star", 385, language: 2));
        Assert.Equal("ENG", entries[0].LanguageTag);
        Assert.Equal("Wishing Star", entries[0].Title);
    }

    [Theory]
    [InlineData("Movie Darkrai", "Darkrai", "Movie")]
    [InlineData("VGC 2010 Shiny Pikachu", "Pikachu", "VGC 2010")]
    [InlineData("(Trainer) Pikachu Egg", "Pikachu", "Other events")]
    [InlineData("Darkrai", "Darkrai", "Other events")]
    public void SeriesStripsSpeciesShinyAndBrackets(string name, string species, string expected) =>
        Assert.Equal(expected, GiftTitles.Series(new WonderCardSource("1", "DP", name, "ENG", null, [], ""), species, EventGiftKind.Pokemon));

    [Fact]
    public void ItemSeriesIsTheTextBeforeItemOrItems()
    {
        var ranked = new WonderCardSource("7", "SWSH", "Doubles S4 Item Gold Bottle Cap", null, null, [], "");
        var plain = new WonderCardSource("68", "BW", "Item Master Ball", "JPN", null, [], "");
        Assert.Equal("Doubles S4", GiftTitles.Series(ranked, "", EventGiftKind.Item));
        Assert.Equal("Items", GiftTitles.Series(plain, "", EventGiftKind.Item));
        Assert.Equal("Items", GiftTitles.Series(null, "", EventGiftKind.Item));
    }

    // ── Variants ────────────────────────────────────────────────────────────

    [Fact]
    public void LanguageCopiesCollapseToTheSavesLanguage()
    {
        var entries = Entries(
            Gift(0, "Darkrai du film", 491, source: "Wondercards/FRE/010 DP - Movie Darkrai (FRE).wc4", cardId: 10),
            Gift(1, "Movie Darkrai", 491, source: "Wondercards/ENG/010 DP - Movie Darkrai (ENG).wc4", cardId: 10),
            Gift(2, "Film-Darkrai", 491, source: "Wondercards/GER/010 DP - Movie Darkrai (GER).wc4", cardId: 10));

        var collapsed = WonderCardAlbum.CollapseVariants(entries, "ENG");
        var card = Assert.Single(collapsed);
        Assert.Equal(1, card.Gift.Id);
        Assert.Equal(3, card.Variants.Count);
        Assert.Same(card.Variants[0].Gift, card.Gift); // the chosen copy leads its variants

        Assert.Equal(0, WonderCardAlbum.CollapseVariants(entries, "FRE").Single().Gift.Id);
        Assert.Equal(1, WonderCardAlbum.CollapseVariants(entries, "KOR").Single().Gift.Id); // English next
    }

    [Fact]
    public void RegionalReleasesStaySeparateAndReceivedSpreadsAcrossCopies()
    {
        var entries = WonderCardAlbum.CreateEntries([
            Gift(0, "Movie Darkrai", 491, source: "Wondercards/ENG/009 DP - Movie Darkrai (UK) (ENG).wc4", cardId: 9),
            Gift(1, "Movie Darkrai", 491, source: "Wondercards/ENG/007 DP - Movie Darkrai (NA) (ENG).wc4", cardId: 7),
            Gift(2, "Darkrai du film", 491, source: "Wondercards/FRE/007 DP - Movie Darkrai (NA) (FRE).wc4", cardId: 7),
        ], Name, EnglishSave, g => g.Id == 2);

        var collapsed = WonderCardAlbum.CollapseVariants(entries, "ENG");
        Assert.Equal(2, collapsed.Count);
        var na = collapsed.Single(e => e.Source!.Region == "NA");
        Assert.Equal(1, na.Gift.Id);
        Assert.True(na.Received); // the French copy was received
        Assert.False(collapsed.Single(e => e.Source!.Region == "UK").Received);
    }

    // ── Query: filters, search, sort, group ─────────────────────────────────

    private static IReadOnlyList<WonderCardEntry> Shelf() => Entries(
        Gift(0, "Birthday Present", 25, "Wondercards/ENG/014 DP - Birthday Pikachu (ENG).wc4", 14, year: 2008, level: 20),
        Gift(1, "Movie Darkrai", 491, "Wondercards/ENG/010 DP - Movie Darkrai (ENG).wc4", 10, year: 2008, level: 50, ot: "MOVIE", moves: ["Dark Void"]),
        Gift(2, "Shiny Jirachi", 385, "Wondercards/ENG/120 HGSS - Shiny Jirachi (ENG).wc4", 120, date: new DateOnly(2010, 3, 5), shiny: true, level: 5),
        Gift(3, "Rare Candy Gift", kind: EventGiftKind.Item, source: "Wondercards/ENG/200 HGSS - Item Rare Candy (ENG).wc4", cardId: 200, item: "Rare Candy"),
        Gift(4, "Manaphy Egg", 490, "Wondercards/JPN/001 DP - TRU Manaphy (JPN).wc4", 1, level: 1));

    [Fact]
    public void DefaultQueryShowsEverythingNewestFirstGroupedByYear()
    {
        var page = WonderCardAlbum.Query(Shelf(), new WonderCardQuery(), EnglishSave);

        Assert.Equal(5, page.Total);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(["2010", "2008", "Undated"], page.Groups.Select(g => g.Title));
        Assert.Equal(2, page.Items[0].Gift.Id);
        Assert.Equal(1, page.Groups[1].Start);
        Assert.Equal(2, page.Groups[1].Count);
        Assert.Equal(3, page.Groups[2].Start);
    }

    [Fact]
    public void KindShinyAndReceivedFiltersNarrow()
    {
        var shelf = Shelf();
        Assert.Equal([3], WonderCardAlbum.Query(shelf, new WonderCardQuery { Kind = WonderCardKindFilter.Items }, EnglishSave).Items.Select(e => e.Gift.Id));
        Assert.Equal(4, WonderCardAlbum.Query(shelf, new WonderCardQuery { Kind = WonderCardKindFilter.Pokemon }, EnglishSave).Items.Count);
        Assert.Equal([2], WonderCardAlbum.Query(shelf, new WonderCardQuery { ShinyOnly = true }, EnglishSave).Items.Select(e => e.Gift.Id));

        var received = WonderCardAlbum.CreateEntries(shelf.Select(e => e.Gift).ToArray(), Name, EnglishSave, g => g.Id == 1);
        Assert.Equal([1], WonderCardAlbum.Query(received, new WonderCardQuery { Received = WonderCardReceivedFilter.Received }, EnglishSave).Items.Select(e => e.Gift.Id));
        var fresh = WonderCardAlbum.Query(received, new WonderCardQuery { Received = WonderCardReceivedFilter.NotReceived }, EnglishSave);
        Assert.Equal(4, fresh.Items.Count);
        Assert.Equal(0, fresh.ReceivedCount);
    }

    [Fact]
    public void YearSpeciesLanguageAndSeriesFiltersNarrow()
    {
        var shelf = Shelf();
        Assert.Equal(2, WonderCardAlbum.Query(shelf, new WonderCardQuery { Year = 2008 }, EnglishSave).Items.Count);
        Assert.Equal([4], WonderCardAlbum.Query(shelf, new WonderCardQuery { Species = 490 }, EnglishSave).Items.Select(e => e.Gift.Id));
        Assert.Equal([4], WonderCardAlbum.Query(shelf, new WonderCardQuery { Language = "JPN" }, EnglishSave).Items.Select(e => e.Gift.Id));
        Assert.Equal([1], WonderCardAlbum.Query(shelf, new WonderCardQuery { Series = "movie" }, EnglishSave).Items.Select(e => e.Gift.Id));
        Assert.Equal(1, new WonderCardQuery { Year = 2008 }.ActiveFilterCount);
        Assert.Equal(0, new WonderCardQuery { Search = "x", Sort = WonderCardSort.Title }.ActiveFilterCount);
    }

    [Theory]
    [InlineData("darkrai", 1)]
    [InlineData("DARK VOID", 1)]      // moves are searchable
    [InlineData("movie", 1)]          // OT and event name
    [InlineData("rare candy", 3)]     // item gifts by item name
    [InlineData("pokemon", -1)]       // accent-free, nothing matches
    [InlineData("#120", 2)]           // card number
    [InlineData("shiny jirachi", 2)]  // every word must match
    [InlineData("manaphy tru", 4)]
    public void SearchMatchesEveryWordAcrossTheCardsFacts(string search, int expectedId)
    {
        var items = WonderCardAlbum.Query(Shelf(), new WonderCardQuery { Search = search }, EnglishSave).Items;
        if (expectedId < 0) Assert.Empty(items);
        else Assert.Equal(expectedId, Assert.Single(items).Gift.Id);
    }

    [Fact]
    public void SearchIgnoresAccentsAndCase() =>
        Assert.Equal("pokemon center", WonderCardAlbum.Normalize("Pokémon CENTER"));

    [Fact]
    public void SortsOrderWithinUngroupedPages()
    {
        var shelf = Shelf();
        WonderCardPage Sorted(WonderCardSort sort) =>
            WonderCardAlbum.Query(shelf, new WonderCardQuery { Sort = sort, Grouping = WonderCardGrouping.None }, EnglishSave);

        Assert.Empty(Sorted(WonderCardSort.Newest).Groups);
        Assert.Equal(2, Sorted(WonderCardSort.Newest).Items[0].Gift.Id);
        Assert.Equal([4, 1, 0, 2, 3], Sorted(WonderCardSort.CardNumber).Items.Select(e => e.Gift.Id));
        Assert.Equal([25, 385, 490, 491, 0], Sorted(WonderCardSort.DexNumber).Items.Select(e => e.Gift.Species));
        Assert.Equal(25, Sorted(WonderCardSort.DexNumber).Items[0].Gift.Species);
        Assert.Equal(EventGiftKind.Item, Sorted(WonderCardSort.DexNumber).Items[^1].Gift.Kind);
        Assert.Equal(["Birthday Present", "Movie Darkrai"], Sorted(WonderCardSort.Title).Items.Take(2).Select(e => e.Title));
        Assert.Equal(1, Sorted(WonderCardSort.Level).Items[0].Gift.Id);
        Assert.Equal(1, Sorted(WonderCardSort.Oldest).Items[0].Gift.Id); // 2008, card 10 before card 14
    }

    [Fact]
    public void GroupingBySeriesGameAndSpecies()
    {
        var shelf = Shelf();
        var bySeries = WonderCardAlbum.Query(shelf, new WonderCardQuery { Grouping = WonderCardGrouping.Series }, EnglishSave);
        // Every series here holds one card, so they all fold into "Other events".
        Assert.Equal([WonderCardAlbum.OtherEvents], bySeries.Groups.Select(g => g.Title));
        Assert.Equal(bySeries.Items.Count, bySeries.Groups.Sum(g => g.Count));

        var movies = Entries(
            Gift(10, "Movie Darkrai", 491, "Wondercards/ENG/010 DP - Movie Darkrai (ENG).wc4", 10),
            Gift(11, "Movie Shaymin", 492, "Wondercards/ENG/014 DP - Movie Shaymin (ENG).wc4", 14),
            Gift(12, "TRU Manaphy", 490, "Wondercards/ENG/001 DP - TRU Manaphy (ENG).wc4", 1));
        var grouped = WonderCardAlbum.Query(movies, new WonderCardQuery { Grouping = WonderCardGrouping.Series }, EnglishSave);
        Assert.Equal(["Movie", WonderCardAlbum.OtherEvents], grouped.Groups.Select(g => g.Title));
        Assert.Equal(2, grouped.Groups[0].Count);

        var byGame = WonderCardAlbum.Query(shelf, new WonderCardQuery { Grouping = WonderCardGrouping.Games }, EnglishSave);
        Assert.Equal(["DP", "HGSS"], byGame.Groups.Select(g => g.Title));
        Assert.Equal(3, byGame.Groups[0].Count);

        var bySpecies = WonderCardAlbum.Query(shelf, new WonderCardQuery { Grouping = WonderCardGrouping.Species }, EnglishSave);
        Assert.Equal(["Pikachu", "Jirachi", "Manaphy", "Darkrai", "Items"], bySpecies.Groups.Select(g => g.Title));
    }

    [Fact]
    public void GroupOfFindsTheSectionOfAnyIndex()
    {
        var page = WonderCardAlbum.Query(Shelf(), new WonderCardQuery(), EnglishSave);
        Assert.Equal(0, page.GroupOf(0));
        Assert.Equal(1, page.GroupOf(2));
        Assert.Equal(2, page.GroupOf(4));
    }

    [Fact]
    public void OnePerEventCollapsesByDefaultAndCanBeTurnedOff()
    {
        var entries = Entries(
            Gift(0, "Darkrai du film", 491, source: "Wondercards/FRE/010 DP - Movie Darkrai (FRE).wc4", cardId: 10),
            Gift(1, "Movie Darkrai", 491, source: "Wondercards/ENG/010 DP - Movie Darkrai (ENG).wc4", cardId: 10));

        var collapsed = WonderCardAlbum.Query(entries, new WonderCardQuery(), EnglishSave);
        Assert.Equal(1, collapsed.Total);
        Assert.Equal(1, Assert.Single(collapsed.Items).Gift.Id);

        var all = WonderCardAlbum.Query(entries, new WonderCardQuery { OnePerEvent = false }, EnglishSave);
        Assert.Equal(2, all.Items.Count);

        // Filtering by language picks that language's copy as the representative.
        var french = WonderCardAlbum.Query(entries, new WonderCardQuery { Language = "FRE" }, EnglishSave);
        Assert.Equal(0, Assert.Single(french.Items).Gift.Id);
    }

    [Fact]
    public void CompatibilityFollowsTheExistingRules()
    {
        var jpnOnly = Gift(0, "Test", language: 1);
        var entry = WonderCardAlbum.CreateEntries([jpnOnly, Gift(1, "Test 2", cardId: 2)], Name, EnglishSave, _ => false);
        Assert.False(entry[0].Compatible);
        Assert.True(entry[1].Compatible);
        Assert.Single(WonderCardAlbum.Query(entry, new WonderCardQuery { CompatibleOnly = true }, EnglishSave).Items);
        Assert.True(WonderCardAlbum.CreateEntries([jpnOnly], Name, null, _ => false)[0].Compatible); // no save, never narrows
    }
}

public sealed class WonderCardAlbumDefaultsTests
{
    private static WonderCardEntry Entry(int? year) =>
        new(new EventGift(0, "T", "", 25, 5, false, 1, 4, 0, year), "T", "T", "ENG", null, "Pikachu", "Other events", year, true, false, "k");

    [Fact]
    public void DatedArchivesGroupByYearUndatedOnesBySeries()
    {
        Assert.Equal(WonderCardGrouping.Year, WonderCardAlbum.DefaultQuery([Entry(2010), Entry(2011), Entry(null)]).Grouping);
        Assert.Equal(WonderCardGrouping.Series, WonderCardAlbum.DefaultQuery([Entry(null), Entry(null), Entry(2010)]).Grouping);
        var dp = Entry(null) with { Source = new WonderCardSource("1", "DP", "x", null, null, [], "") };
        var hgss = Entry(null) with { Source = new WonderCardSource("2", "HGSS", "y", null, null, [], "") };
        Assert.Equal(WonderCardGrouping.Games, WonderCardAlbum.DefaultQuery([dp, hgss, Entry(null)]).Grouping);
        Assert.True(WonderCardAlbum.DefaultQuery([]).OnePerEvent);
    }

    [Fact]
    public void ColonCutTitlesAreFinishedByTheEventName()
    {
        var source = WonderCardSource.Parse("Wondercards/ENG/184 DPPt - Oblivia Shaymin (ENG).wc4");
        Assert.Equal("Trusted Pokémon Trainer: Oblivia Shaymin", GiftTitles.Resolve("Trusted Pokémon Trainer:", "ENG", source, null, "Shaymin"));
        Assert.Equal("Trusted Pokémon Trainer", GiftTitles.Resolve("Trusted Pokémon Trainer:", "ENG", null, null, "Shaymin"));
    }
}
