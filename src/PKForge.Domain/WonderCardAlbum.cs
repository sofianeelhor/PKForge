using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PKForge.Domain;

/// <summary>
/// Where a card came from, read off its EventsGallery file path, e.g.
/// <c>Wondercards/ENG/007 DP - Movie Darkrai (NA) (ENG).wc4</c>: gallery number, games,
/// the English event name, language, region, variant tags and sub-collection.
/// </summary>
public sealed record WonderCardSource(
    string Number, string Games, string Name, string? Language, string? Region,
    IReadOnlyList<string> Tags, string Collection)
{
    private static readonly Regex Numbered = new(@"^(?<num>[\d-]{2,})\s+(?<games>\S+?)\s*-\s+(?<rest>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Trailing = new(@"\s*\((?<tag>[^()]*)\)\s*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    { "NA", "EU", "UK", "AU", "US", "JP", "KR", "TW", "HK", "PAL", "SEA", "LATAM" };
    private static readonly HashSet<string> Structural = new(StringComparer.OrdinalIgnoreCase)
    { "Wondercards", "3DS", "SwSh", "SV", "BDSP", "PLA", "LGPE", "Switch", "hex extracted cards" };

    /// <summary>Null for an empty path; never throws on odd names (the name is the whole file).</summary>
    public static WonderCardSource? Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var file = Path.GetFileNameWithoutExtension(segments[^1]).Trim();

        string? language = null;
        var collection = new List<string>();
        foreach (var folder in segments[..^1])
        {
            if (GiftLanguages.IsTag(folder)) language ??= folder.ToUpperInvariant();
            else if (!Structural.Contains(folder)) collection.Add(folder);
        }

        string number = "", games = "", rest = file;
        var numbered = Numbered.Match(file);
        if (numbered.Success)
        {
            number = numbered.Groups["num"].Value.Trim('-');
            games = numbered.Groups["games"].Value.Trim();
            rest = numbered.Groups["rest"].Value;
        }
        else if (file.IndexOf(" - ", StringComparison.Ordinal) is var dash and > 0)
        {
            games = file[..dash].Trim();
            rest = file[(dash + 3)..];
        }

        string? region = null;
        var tags = new List<string>();
        rest = rest.Trim();
        while (Trailing.Match(rest) is { Success: true } tail)
        {
            var tag = tail.Groups["tag"].Value.Trim();
            rest = rest[..tail.Index].TrimEnd();
            if (GiftLanguages.IsTag(tag)) language ??= tag.ToUpperInvariant();
            else if (Regions.Contains(tag)) region ??= tag.ToUpperInvariant();
            else if (tag.Length > 0) tags.Insert(0, tag);
            if (rest.Length == 0) break;
        }
        if (rest.Length == 0) rest = file;
        return new WonderCardSource(number, games, rest.Trim(), language, region, tags, string.Join(" / ", collection));
    }
}

/// <summary>Language tags the way the gallery writes them, plus script detection for titles.</summary>
public static class GiftLanguages
{
    private static readonly string[] Tags = ["JPN", "ENG", "FRE", "ITA", "GER", "SPA", "KOR", "CHS", "CHT"];

    public static bool IsTag(string? text) => text is not null && Tags.Contains(text.ToUpperInvariant());

    /// <summary>PKHeX language id (1 JPN, 2 ENG, 3 FRE, 4 ITA, 5 GER, 7 SPA, 8 KOR, 9 CHS, 10 CHT) to its tag.</summary>
    public static string? TagFor(int languageId) => languageId switch
    {
        1 => "JPN", 2 => "ENG", 3 => "FRE", 4 => "ITA", 5 => "GER", 7 => "SPA", 8 => "KOR", 9 => "CHS", 10 => "CHT",
        _ => null,
    };

    public static string DisplayName(string tag) => tag.ToUpperInvariant() switch
    {
        "JPN" => "Japanese", "ENG" => "English", "FRE" => "French", "ITA" => "Italian", "GER" => "German",
        "SPA" => "Spanish", "KOR" => "Korean", "CHS" => "Chinese (Simpl.)", "CHT" => "Chinese (Trad.)",
        _ => tag,
    };

    /// <summary>The language a title is written in, judged by its script: kana means Japanese,
    /// Hangul Korean, Han alone Chinese; null for Latin script (any European language).</summary>
    public static string? DetectScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        bool kana = false, hangul = false, han = false;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ') kana = true;
            else if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul = true;
            else if (c is >= '一' and <= '鿿') han = true;
        }
        return kana ? "JPN" : hangul ? "KOR" : han ? "CHS" : null;
    }

    /// <summary>True when the text reads in Latin script: no kana, Hangul, Han, CJK
    /// punctuation or fullwidth forms (which an English UI and its pixel face cannot show).</summary>
    public static bool IsReadable(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !text.Any(c => c is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF'
            or >= '\u1100' and <= '\u11FF' or >= '\uFF00' and <= '\uFFEF');
}

/// <summary>
/// Card titles for an English UI. Gen 4-7 cards print one title in their distribution
/// language, so a Japanese card shows kana; the album prefers, in order: the English sibling
/// card's title (same event, another language), the card's own title when it is the English
/// card, the gallery's English event name (with the common Japanese venue words
/// translated), the card's own Latin title, and finally "<species> gift".
/// </summary>
public static class GiftTitles
{
    private static readonly (string Japanese, string English)[] Venues =
    [
        ("ポケモンセンター", "Pokémon Center"), ("ポケセン", "Pokémon Center"), ("ポケモンストア", "Pokémon Store"),
        ("コロコロ", "CoroCoro"), ("サトシ", "Ash's"), ("えいが", "Movie"), ("映画", "Movie"), ("ゲームフリーク", "Game Freak"),
        ("ヨコハマ", "Yokohama"), ("ナゴヤ", "Nagoya"), ("トウキョー", "Tokyo"), ("オーサカ", "Osaka"), ("フクオカ", "Fukuoka"),
        ("サッポロ", "Sapporo"), ("トウホク", "Tohoku"), ("ヒロシマ", "Hiroshima"), ("カナザワ", "Kanazawa"), ("キョウト", "Kyoto"),
    ];

    public static string Resolve(string cardTitle, string? cardLanguage, WonderCardSource? source,
        string? englishSiblingTitle, string fallbackSubject)
    {
        var own = Clean(cardTitle);
        if (GiftLanguages.IsReadable(englishSiblingTitle)) return Complete(Clean(englishSiblingTitle!), source);
        if (GiftLanguages.IsReadable(own) && (cardLanguage is null or "ENG")) return Complete(own, source);
        if (source is not null && TranslateEventName(source.Name) is { Length: > 0 } eventName)
            return eventName.StartsWith("Item ", StringComparison.Ordinal) && eventName.Length > 5 ? eventName[5..] : eventName;
        if (GiftLanguages.IsReadable(own)) return own;
        return $"{fallbackSubject} gift";
    }

    /// <summary>Titles cut at the card's title width end on a colon ("Trusted Pokémon
    /// Trainer:"); the gallery's event name finishes them.</summary>
    private static string Complete(string title, WonderCardSource? source) =>
        title.EndsWith(':') && source is not null && TranslateEventName(source.Name) is { Length: > 0 } name
            ? $"{title} {name}"
            : title.TrimEnd(':').TrimEnd();

    /// <summary>The gallery's event name in English: known venue words translated, any other
    /// CJK word dropped. Empty when nothing readable is left.</summary>
    public static string TranslateEventName(string name)
    {
        var text = name;
        foreach (var (japanese, english) in Venues)
            text = text.Replace(japanese, english, StringComparison.Ordinal);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(GiftLanguages.IsReadable).ToList();
        // Dropping a word can strand its separator ("ポケモン - Pikachu" → "- Pikachu").
        while (words.Count > 0 && !words[0].Any(char.IsLetterOrDigit)) words.RemoveAt(0);
        while (words.Count > 0 && !words[^1].Any(char.IsLetterOrDigit)) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).Trim();
    }

    /// <summary>The event series a card belongs to, for grouping: the event name without the
    /// species, "Shiny", "Egg" and variant brackets ("Movie Darkrai (NA)" → "Movie",
    /// "Doubles S4 Item Gold Bottle Cap" → "Doubles S4"). Item cards with no series are "Items".</summary>
    public static string Series(WonderCardSource? source, string speciesName, EventGiftKind kind)
    {
        if (source is null) return kind == EventGiftKind.Item ? "Items" : "Other events";
        var name = Regex.Replace(TranslateEventName(source.Name), @"\([^()]*\)", " ");
        var itemAt = Regex.Match(name, @"(^|\s)Item(\s|$)");
        if (kind == EventGiftKind.Item || itemAt.Success)
        {
            var before = itemAt.Success ? name[..itemAt.Index].Trim() : "";
            return before.Length > 0 ? before : "Items";
        }
        if (speciesName.Length > 0)
            name = Regex.Replace(name, $@"(^|\s){Regex.Escape(speciesName)}(\s|$).*$", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"(^|\s)(Shiny|Egg)(?=\s|$)", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length > 0 ? name : "Other events";
    }

    private static string Clean(string text) => text.Replace('　', ' ').Trim();
}

/// <summary>One card as the album shows it; <see cref="Variants"/> holds its other-language
/// copies when the album collapses languages (the entry itself is the preferred one).</summary>
public sealed record WonderCardEntry(
    EventGift Gift,
    string Title,
    string OriginalTitle,
    string? LanguageTag,
    WonderCardSource? Source,
    string SpeciesName,
    string Series,
    int? Year,
    bool Compatible,
    bool Received,
    string VariantKey)
{
    public IReadOnlyList<WonderCardEntry> Variants { get; init; } = [];

    /// <summary>True when the shown title is not the one printed on the card.</summary>
    public bool TitleTranslated => !string.Equals(Title, OriginalTitle.Replace('　', ' ').Trim(), StringComparison.Ordinal);

    public DateOnly? Date => Gift.Details?.Date;
}

public enum WonderCardKindFilter { All, Pokemon, Items }
public enum WonderCardReceivedFilter { Any, NotReceived, Received }
public enum WonderCardSort { Newest, Oldest, CardNumber, DexNumber, Title, Level }
public enum WonderCardGrouping { Year, Series, Games, Species, None }

/// <summary>Every knob of the album at once; the default browses everything, one card per
/// event, newest first, grouped by year.</summary>
public sealed record WonderCardQuery
{
    public string Search { get; init; } = "";
    public WonderCardKindFilter Kind { get; init; }
    public bool ShinyOnly { get; init; }
    public bool CompatibleOnly { get; init; }
    public WonderCardReceivedFilter Received { get; init; }
    public int? Year { get; init; }
    public int? Species { get; init; }
    public string? Language { get; init; }
    public string? Series { get; init; }
    /// <summary>Collapse language copies of one event into a single card (preferred language first).</summary>
    public bool OnePerEvent { get; init; } = true;
    public WonderCardSort Sort { get; init; } = WonderCardSort.Newest;
    public WonderCardGrouping Grouping { get; init; } = WonderCardGrouping.Year;

    /// <summary>How many narrowing filters are on (search, sort, grouping and collapsing are not filters).</summary>
    public int ActiveFilterCount =>
        (Kind != WonderCardKindFilter.All ? 1 : 0) + (ShinyOnly ? 1 : 0) + (CompatibleOnly ? 1 : 0)
        + (Received != WonderCardReceivedFilter.Any ? 1 : 0) + (Year is null ? 0 : 1) + (Species is null ? 0 : 1)
        + (Language is null ? 0 : 1) + (Series is null ? 0 : 1);
}

public sealed record WonderCardGroup(string Title, int Start, int Count);

/// <summary>What the album shows for a query: the ordered cards, the section headers over
/// them, and the counts for the header strip.</summary>
public sealed record WonderCardPage(IReadOnlyList<WonderCardEntry> Items, IReadOnlyList<WonderCardGroup> Groups, int Total, int ReceivedCount)
{
    /// <summary>The group holding item <paramref name="index"/> (0 when there are no groups).</summary>
    public int GroupOf(int index)
    {
        for (var g = Groups.Count - 1; g >= 0; g--)
            if (index >= Groups[g].Start) return g;
        return 0;
    }
}

/// <summary>
/// The pure logic of the Mystery Gift album: turning raw gifts into readable entries
/// (titles, languages, series, variants, compatibility, received marks) and answering a
/// <see cref="WonderCardQuery"/> with sorted, grouped pages. No UI, no engine.
/// </summary>
public static class WonderCardAlbum
{
    /// <summary>
    /// Builds album entries. <paramref name="speciesName"/> names a species id;
    /// <paramref name="isReceived"/> reads the injected-history ledger.
    /// </summary>
    public static IReadOnlyList<WonderCardEntry> CreateEntries(
        IReadOnlyList<EventGift> gifts, Func<int, string> speciesName, EventGiftSaveProfile? profile, Func<EventGift, bool> isReceived)
    {
        var parsed = gifts.Select(g =>
        {
            var source = WonderCardSource.Parse(g.Details?.SourceFile);
            var language = source?.Language ?? GiftLanguages.TagFor(g.Language) ?? GiftLanguages.DetectScript(g.Title);
            return (Gift: g, Source: source, Language: language, Key: VariantKey(g, source));
        }).ToArray();

        // The English copy of each event lends its title to the other languages.
        var english = parsed
            .Where(p => GiftLanguages.IsReadable(p.Gift.Title) && (p.Language == "ENG" || (p.Language is null && p.Source is null)))
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.First().Gift.Title);

        return parsed.Select(p =>
        {
            var name = p.Gift.Kind == EventGiftKind.Item ? "" : speciesName(p.Gift.Species);
            var subject = p.Gift.Kind == EventGiftKind.Item
                ? p.Gift.Details?.Items.FirstOrDefault()?.Name is { Length: > 0 } item ? item : "Item"
                : name;
            var title = GiftTitles.Resolve(p.Gift.Title, p.Language, p.Source, english.GetValueOrDefault(p.Key), subject);
            return new WonderCardEntry(
                p.Gift, title, p.Gift.Title, p.Language, p.Source, name,
                GiftTitles.Series(p.Source, name, p.Gift.Kind),
                p.Gift.Year ?? p.Gift.Details?.Date?.Year,
                profile is null || EventGiftRules.IsCompatible(p.Gift, profile),
                isReceived(p.Gift),
                p.Key);
        }).ToArray();
    }

    /// <summary>Language copies of one event share this key: the gallery number when the card
    /// came from the gallery (shared across its language folders), else the card number.</summary>
    public static string VariantKey(EventGift gift, WonderCardSource? source)
    {
        var number = source is { Number.Length: > 0 } && source.Number.Any(char.IsAsciiDigit) ? $"n{source.Number.TrimStart('0')}" : $"c{gift.CardId}";
        var item = gift.Details?.Items.FirstOrDefault()?.ItemId ?? 0;
        return $"{gift.Generation}|{number}|{gift.Species}|{(int)gift.Kind}|{item}|{source?.Region}";
    }

    /// <summary>Collapses language copies: one entry per event, the save's language first,
    /// then English, then the first seen. The winner carries every copy in Variants.</summary>
    public static IReadOnlyList<WonderCardEntry> CollapseVariants(IReadOnlyList<WonderCardEntry> entries, string? preferredLanguage)
    {
        var result = new List<WonderCardEntry>();
        foreach (var group in entries.GroupBy(e => e.VariantKey))
        {
            var copies = group.ToArray();
            var best = copies.FirstOrDefault(e => preferredLanguage is not null && e.LanguageTag == preferredLanguage)
                ?? copies.FirstOrDefault(e => e.LanguageTag == "ENG")
                ?? copies[0];
            var ordered = copies.OrderBy(e => ReferenceEquals(e, best) ? 0 : 1).ToArray();
            result.Add(best with { Variants = ordered, Received = copies.Any(c => c.Received) });
        }
        return result;
    }

    /// <summary>The album's opening query for these cards: grouped by year when most cards
    /// carry a date, else by game when the gallery names several (Gen 4 cards print no date),
    /// else by event series.</summary>
    public static WonderCardQuery DefaultQuery(IReadOnlyList<WonderCardEntry> entries) => new()
    {
        Grouping = entries.Count(e => e.Year is not null) * 2 >= entries.Count ? WonderCardGrouping.Year
            : entries.Select(e => e.Source?.Games).Where(g => !string.IsNullOrEmpty(g)).Distinct().Skip(1).Any() ? WonderCardGrouping.Games
            : WonderCardGrouping.Series,
    };

    /// <summary>Answers a query: collapse, filter, sort, group.</summary>
    public static WonderCardPage Query(IReadOnlyList<WonderCardEntry> entries, WonderCardQuery query, EventGiftSaveProfile? profile)
    {
        IReadOnlyList<WonderCardEntry> pool = query.OnePerEvent
            ? CollapseVariants(entries, query.Language ?? (profile is null ? null : GiftLanguages.TagFor(profile.Language)))
            : entries;
        var tokens = Normalize(query.Search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = pool.Where(e => Matches(e, query, tokens)).ToList();
        // Event sections of a single card are noise: they fold into one "Other events" section.
        var seriesCounts = matched.GroupBy(e => e.Series, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        string SeriesKey(WonderCardEntry e) => seriesCounts[e.Series] > 1 ? e.Series : OtherEvents;
        var sorted = Sort(matched, query.Sort, query.Grouping, SeriesKey);
        var groups = Group(sorted, query.Grouping, SeriesKey);
        return new WonderCardPage(sorted, groups, pool.Count, sorted.Count(e => e.Received));
    }

    public static bool Matches(WonderCardEntry e, WonderCardQuery q, IReadOnlyList<string> tokens)
    {
        if (q.Kind == WonderCardKindFilter.Pokemon && e.Gift.Kind != EventGiftKind.Pokemon) return false;
        if (q.Kind == WonderCardKindFilter.Items && e.Gift.Kind != EventGiftKind.Item) return false;
        if (q.ShinyOnly && !e.Gift.Shiny) return false;
        if (q.CompatibleOnly && !e.Compatible) return false;
        if (q.Received == WonderCardReceivedFilter.Received && !e.Received) return false;
        if (q.Received == WonderCardReceivedFilter.NotReceived && e.Received) return false;
        if (q.Year is { } year && e.Year != year) return false;
        if (q.Species is { } species && e.Gift.Species != species) return false;
        if (q.Series is { } series && !string.Equals(e.Series, series, StringComparison.OrdinalIgnoreCase)) return false;
        if (q.Language is { } language && !(e.Variants.Count > 0 ? e.Variants : [e]).Any(v => v.LanguageTag == language)) return false;
        if (tokens.Count == 0) return true;
        var haystack = Haystack(e);
        return tokens.All(t => haystack.Contains(t, StringComparison.Ordinal));
    }

    private static string Haystack(WonderCardEntry e)
    {
        var d = e.Gift.Details;
        var parts = new List<string?>
        {
            e.Title, e.OriginalTitle, e.SpeciesName, e.Series, e.Source?.Name, e.Source?.Games, e.Source?.Region,
            e.LanguageTag, e.Year?.ToString(CultureInfo.InvariantCulture), $"#{e.Gift.CardId}", e.Gift.CardId.ToString(CultureInfo.InvariantCulture),
            $"lv{e.Gift.Level}", e.Gift.Shiny ? "shiny" : null, e.Gift.Kind == EventGiftKind.Item ? "item" : null,
            d?.OriginalTrainer, d?.TrainerId, d?.HeldItemName, d?.BallName, d?.MetLocation,
        };
        if (d is not null)
        {
            parts.AddRange(d.Moves);
            parts.AddRange(d.Items.Select(i => i.Name));
        }
        return Normalize(string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p))));
    }

    /// <summary>Lower-case, accent-free text ("Pokémon" matches "pokemon").</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public const string OtherEvents = "Other events";

    private static List<WonderCardEntry> Sort(List<WonderCardEntry> items, WonderCardSort sort, WonderCardGrouping grouping, Func<WonderCardEntry, string> seriesKey)
    {
        // Within a sort, ties fall back to the card number then the title: a stable album.
        IOrderedEnumerable<WonderCardEntry> ordered = grouping switch
        {
            WonderCardGrouping.Year => items.OrderBy(e => e.Year is null ? 1 : 0).ThenBy(e => sort == WonderCardSort.Oldest ? e.Year ?? 0 : -(e.Year ?? 0)),
            WonderCardGrouping.Series => items.OrderBy(e => seriesKey(e) == OtherEvents ? 1 : 0).ThenBy(seriesKey, StringComparer.OrdinalIgnoreCase),
            WonderCardGrouping.Games => items.OrderBy(e => e.Source?.Games is { Length: > 0 } ? 0 : 1).ThenBy(e => e.Source?.Games ?? "", StringComparer.OrdinalIgnoreCase),
            WonderCardGrouping.Species => items.OrderBy(e => e.Gift.Kind == EventGiftKind.Item ? int.MaxValue : e.Gift.Species),
            _ => items.OrderBy(_ => 0),
        };
        ordered = sort switch
        {
            WonderCardSort.Newest => ordered.ThenBy(e => DateKey(e) is null ? 1 : 0).ThenByDescending(e => DateKey(e)).ThenByDescending(e => e.Gift.CardId),
            WonderCardSort.Oldest => ordered.ThenBy(e => DateKey(e) is null ? 1 : 0).ThenBy(e => DateKey(e)).ThenBy(e => e.Gift.CardId),
            WonderCardSort.CardNumber => ordered.ThenBy(e => e.Gift.CardId),
            WonderCardSort.DexNumber => ordered.ThenBy(e => e.Gift.Kind == EventGiftKind.Item ? int.MaxValue : e.Gift.Species).ThenBy(e => e.Gift.CardId),
            WonderCardSort.Title => ordered.ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase),
            WonderCardSort.Level => ordered.ThenBy(e => e.Gift.Kind == EventGiftKind.Item ? 1 : 0).ThenByDescending(e => e.Gift.Level).ThenBy(e => e.Gift.Species),
            _ => ordered,
        };
        return ordered.ThenBy(e => e.Gift.CardId).ThenBy(e => e.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>Card date when printed, else the year (Jan 1) - so undated-but-yeared cards still sort.</summary>
    private static DateOnly? DateKey(WonderCardEntry e) =>
        e.Date ?? (e.Year is { } y && y is >= 1 and <= 9999 ? new DateOnly(y, 1, 1) : null);

    private static IReadOnlyList<WonderCardGroup> Group(List<WonderCardEntry> items, WonderCardGrouping grouping, Func<WonderCardEntry, string> seriesKey)
    {
        if (grouping == WonderCardGrouping.None || items.Count == 0) return [];
        var groups = new List<WonderCardGroup>();
        string? current = null;
        var start = 0;
        for (var i = 0; i <= items.Count; i++)
        {
            var title = i < items.Count ? grouping == WonderCardGrouping.Series ? seriesKey(items[i]) : GroupTitle(items[i], grouping) : null;
            if (i < items.Count && title == current) continue;
            if (current is not null) groups.Add(new WonderCardGroup(current, start, i - start));
            current = title;
            start = i;
        }
        return groups;
    }

    public static string GroupTitle(WonderCardEntry e, WonderCardGrouping grouping) => grouping switch
    {
        WonderCardGrouping.Year => e.Year?.ToString(CultureInfo.InvariantCulture) ?? "Undated",
        WonderCardGrouping.Series => e.Series,
        WonderCardGrouping.Games => e.Source?.Games is { Length: > 0 } games ? games : "Other",
        WonderCardGrouping.Species => e.Gift.Kind == EventGiftKind.Item ? "Items" : e.SpeciesName,
        _ => "",
    };
}
