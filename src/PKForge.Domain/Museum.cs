using System.Globalization;

namespace PKForge.Domain;

// The Hall of Fame museum: every playthrough on the shelf side by side, each with what its
// save says about the run (champion teams, badges, time, Pokédex, the facility records the
// game keeps). Every value is read from the save as PKHeX exposes it; nothing is guessed.

/// <summary>One record a save keeps, as the museum shows it ("Battle Tower", "Best streak", 49).</summary>
public sealed record MuseumRecord(string Facility, string Label, int Value);

/// <summary>What the museum reads from one save, beyond what its insight already says.</summary>
/// <param name="Fame">The Hall of Fame teams, where PKHeX models them (Gen 1, 3, 6, 7).</param>
/// <param name="Clears">How many times the game counts the player as champion; null when it keeps no count.</param>
/// <param name="FirstClearDate">The date of the first Hall of Fame entry (Gen 4-7 trainer card, Gen 6 Hall of Fame).</param>
/// <param name="FirstClearPlayTime">The play time at the first Hall of Fame entry (Gen 3 keeps it): "12:34".</param>
public sealed record MuseumReading(
    int Generation,
    HallOfFameInfo? Fame,
    int? Clears,
    DateOnly? FirstClearDate,
    string? FirstClearPlayTime,
    IReadOnlyList<TrainerBadge> Badges,
    int PlayHours, int PlayMinutes,
    IReadOnlyList<MuseumRecord> Records);

/// <summary>One playthrough's hall in the museum.</summary>
/// <param name="Key">The save's document id: what the hall stands for.</param>
/// <param name="Game">The game's name as the shelf shows it.</param>
/// <param name="LastPlayed">When the file last changed, to place a hall without a clear date on the timeline.</param>
public sealed record MuseumHall(
    string Key, string Game, int Generation,
    string Trainer, int TrainerId, int Gender,
    MuseumReading Reading,
    int Pokemon, int DexCaught, int DexSeen, int DexTotal,
    DateTimeOffset? LastPlayed)
{
    public IReadOnlyList<HallOfFameTeam> Teams => Reading.Fame?.Teams ?? [];

    /// <summary>Times crowned: the game's own count when it keeps one, else the teams it keeps
    /// (a Gen 7 save keeps the first and the latest: two teams mean at least two clears).</summary>
    public int Champions => Reading.Clears ?? (Reading.Fame is null && Reading.FirstClearDate is not null ? 1 : Teams.Count);

    public bool IsChampion => Champions > 0 || Reading.FirstClearDate is not null || Reading.FirstClearPlayTime is not null;

    public int BadgesEarned => Reading.Badges.Count(b => b.Obtained);

    public string PlayTime => $"{Reading.PlayHours}:{Reading.PlayMinutes:00}";

    /// <summary>"First clear 2026-03-02", "First clear at 12:34 played", or "Not champion yet".</summary>
    public string FirstClearLine =>
        Reading.FirstClearDate is { } date ? $"First clear {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        : Reading.FirstClearPlayTime is { } time ? $"First clear at {time} played"
        : IsChampion ? "Champion"
        : "Not champion yet";
}

/// <summary>The museum's totals across every hall.</summary>
public sealed record MuseumTotals(
    int Halls, int ChampionHalls, int Champions, int Teams,
    int Badges, int BadgesTotal, int Pokemon, int DexCaught, int PlayHours, int PlayMinutes)
{
    public string PlayTime => $"{PlayHours}:{PlayMinutes:00}";
}

public static class Museum
{
    /// <summary>
    /// The halls in timeline order: dated first clears first, oldest to newest; then the
    /// playthroughs without a date, by when their file last changed; then by generation.
    /// </summary>
    public static IReadOnlyList<MuseumHall> Timeline(IEnumerable<MuseumHall> halls)
    {
        ArgumentNullException.ThrowIfNull(halls);
        return
        [
            .. halls
                .OrderBy(h => h.Reading.FirstClearDate is null)
                .ThenBy(h => h.Reading.FirstClearDate ?? DateOnly.MaxValue)
                .ThenBy(h => h.LastPlayed ?? DateTimeOffset.MaxValue)
                .ThenBy(h => h.Generation)
                .ThenBy(h => h.Game, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public static MuseumTotals Totals(IEnumerable<MuseumHall> halls)
    {
        ArgumentNullException.ThrowIfNull(halls);
        var list = halls.ToList();
        var minutes = list.Sum(h => (long)h.Reading.PlayHours * 60 + h.Reading.PlayMinutes);
        return new MuseumTotals(
            list.Count,
            list.Count(h => h.IsChampion),
            list.Sum(h => h.Champions),
            list.Sum(h => h.Teams.Count),
            list.Sum(h => h.BadgesEarned),
            list.Sum(h => h.Reading.Badges.Count),
            list.Sum(h => h.Pokemon),
            list.Sum(h => h.DexCaught),
            (int)Math.Min(int.MaxValue, minutes / 60),
            (int)(minutes % 60));
    }

    /// <summary>Gen 3's FIRST_HOF_PLAY_TIME: hours in the high half, minutes and seconds in the low bytes.</summary>
    public static string? Gen3PlayTime(uint packed)
    {
        if (packed == 0) return null;
        var hours = packed >> 16;
        var minutes = (packed >> 8) & 0xFF;
        return minutes > 59 || hours > 999 ? null : $"{hours}:{minutes:00}";
    }

    /// <summary>"12,345": the app speaks English whatever the device's language.</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
