namespace PKForge.Domain;

// Trainer card details beyond name, IDs, money and play time: badges, rival name, the
// country/region a DS or 3DS save carries, adventure start / first Hall of Fame / last saved
// dates and Alola fly destinations. Every field has a Supports flag: a field the game does
// not store is reported as unsupported and ignored on write.

/// <summary>One badge (or Gen 2 / HeartGold-SoulSilver Kanto badge) and whether the save has it.</summary>
/// <param name="Index">Bit order as the game stores it; the order <see cref="TrainerCardEdit.Badges"/> takes.</param>
/// <param name="Region">"Kanto", "Johto", "Hoenn"... so a 16-badge game can be drawn as two rows.</param>
public sealed record TrainerBadge(int Index, string Name, string Region, bool Obtained);

/// <summary>A fly destination the save gates with a flag.</summary>
public sealed record TrainerFlyDestination(int Index, string Name, bool Unlocked);

/// <summary>A dated field. <paramref name="HasTime"/> is false when the save keeps the day only.</summary>
/// <param name="Value">Null when supported but empty or not a valid date in the save.</param>
public sealed record TrainerDateField(bool Supported, DateTime? Value, bool HasTime, DateTime Min, DateTime Max)
{
    public static readonly DateTime DefaultMin = new(2000, 1, 1);
    public static readonly DateTime DefaultMax = new(2099, 12, 31, 23, 59, 59);
    public static TrainerDateField None { get; } = new(false, null, false, DefaultMin, DefaultMax);

    /// <summary>True when <paramref name="value"/> fits this field.</summary>
    public bool Accepts(DateTime value) => Supported && value >= Min && value <= Max;
}

/// <summary>Country and subregion as stored by Gen 4/5 (Wi-Fi/Geonet) and Gen 6/7 (3DS profile).</summary>
/// <param name="CountryName">Resolved for the 3DS list only; Gen 4/5 lists are not part of the engine.</param>
/// <param name="SupportsConsoleRegion">3DS console region (0 Japan, 1 Americas, 2 Europe, 4 China, 5 Korea, 6 Taiwan).</param>
public sealed record TrainerGeoField(
    bool Supported, int Country, int Region, string? CountryName, string? RegionName, int Max,
    bool SupportsConsoleRegion, int ConsoleRegion)
{
    public static TrainerGeoField None { get; } = new(false, 0, 0, null, null, 0, false, 0);
}

/// <summary>Everything the trainer card editor shows beyond the core fields.</summary>
/// <param name="RivalNameMaxLength">Characters the game allows (PKHeX <c>MaxStringLengthTrainer</c>); 0 when unsupported.</param>
/// <param name="HallOfFameDate">The first Hall of Fame entry's date stored on the card (Gen 4-7).</param>
public sealed record TrainerCardDetails(
    int Generation,
    bool SupportsBadges, IReadOnlyList<TrainerBadge> Badges,
    bool SupportsRivalName, string? RivalName, int RivalNameMaxLength,
    TrainerGeoField Geo,
    TrainerDateField StartDate,
    TrainerDateField HallOfFameDate,
    TrainerDateField LastSaved,
    bool SupportsFlyDestinations, IReadOnlyList<TrainerFlyDestination> FlyDestinations)
{
    public int BadgeCount => Badges.Count(b => b.Obtained);
}

/// <summary>A trainer card write. Null leaves the field as it is; a value for an unsupported field is refused.</summary>
/// <param name="Badges">One entry per badge, in <see cref="TrainerBadge.Index"/> order.</param>
/// <param name="FlyDestinations">One entry per destination, in <see cref="TrainerFlyDestination.Index"/> order.</param>
public sealed record TrainerCardEdit(
    IReadOnlyList<bool>? Badges = null,
    string? RivalName = null,
    int? Country = null,
    int? Region = null,
    int? ConsoleRegion = null,
    DateTime? StartDate = null,
    DateTime? HallOfFameDate = null,
    DateTime? LastSaved = null,
    IReadOnlyList<bool>? FlyDestinations = null);

/// <summary>Badge bit helpers shared by every generation that packs badges into an integer.</summary>
public static class TrainerBadgeBits
{
    public static IReadOnlyList<bool> FromMask(int mask, int count) =>
        Enumerable.Range(0, count).Select(i => (mask >> i & 1) != 0).ToArray();

    public static int ToMask(IReadOnlyList<bool> badges)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(badges.Count, 31);
        var mask = 0;
        for (var i = 0; i < badges.Count; i++)
            if (badges[i])
                mask |= 1 << i;
        return mask;
    }

    /// <summary>"First n badges obtained", the quick fill the editor offers.</summary>
    public static IReadOnlyList<bool> FirstN(int obtained, int count) =>
        Enumerable.Range(0, count).Select(i => i < obtained).ToArray();
}
