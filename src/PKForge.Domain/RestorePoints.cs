using System.Globalization;

namespace PKForge.Domain;

/// <summary>What the write a restore point precedes was doing, for the list's filters.</summary>
public enum RestorePointKind { Other, Edit, Move, New, Release, Transfer, Items, World, Restore, Snapshot }

/// <summary>
/// Reads a restore point for the player: the kind of change it undoes (from the sentence
/// each write records) and when it was made, in their own time. The list used to show the
/// game's name on every row and a UTC timestamp, so finding "the one before I released
/// that box" meant reading them all.
/// </summary>
public static class RestorePoints
{
    // First match wins: a restore's safety copy mentions "restore", a Bank arrival "moved".
    private static readonly (RestorePointKind Kind, string[] Marks)[] Rules =
    [
        (RestorePointKind.Snapshot, ["snapshot: "]),
        (RestorePointKind.Restore, ["safety copy", "restore"]),
        (RestorePointKind.Transfer, ["bank", "arrived", "joined the game", "another game", "sent to", "transfer", "deposit", "withdr"]),
        (RestorePointKind.Release, ["released", "bye-bye", "emptied", "deleted", "cleared"]),
        (RestorePointKind.New, ["generated", "random team", "egg", "gift", "cloned", "duplicat", "living dex", "imported", "showdown", "hatched"]),
        (RestorePointKind.Move, ["move ", "moved ", "swap", "sorted", "sort ", "box layout", "boxes"]),
        (RestorePointKind.Items, ["bag", "item", "apricorn", "bean", "preset", "pouch", "money"]),
        (RestorePointKind.World, ["clock", "roam", "flag", "honey", "pokédex", "dex ", "trainer", "entralink", "pokéwalker", "event", "underground", "fashion", "record"]),
        (RestorePointKind.Edit, ["edit", "updated", "legal", "mine", "batch", "evolv", "nickname", "level", "shiny", "ability", "nature"]),
    ];

    public static RestorePointKind KindOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return RestorePointKind.Other;
        foreach (var (kind, marks) in Rules)
            foreach (var mark in marks)
                if (description.Contains(mark, StringComparison.OrdinalIgnoreCase)) return kind;
        return RestorePointKind.Other;
    }

    /// <summary>The filter's name for a kind ("Moves").</summary>
    public static string Label(RestorePointKind kind) => kind switch
    {
        RestorePointKind.Edit => "Edits",
        RestorePointKind.Move => "Moves",
        RestorePointKind.New => "New Pokémon",
        RestorePointKind.Release => "Releases",
        RestorePointKind.Transfer => "Transfers",
        RestorePointKind.Items => "Items",
        RestorePointKind.World => "World",
        RestorePointKind.Restore => "Restores",
        RestorePointKind.Snapshot => "Snapshots",
        _ => "Other",
    };

    /// <summary>
    /// When a point was made, as the player would say it: "just now", "12 min ago",
    /// "today 14:02", "yesterday 09:30", then the date. <paramref name="now"/> carries the
    /// player's time zone.
    /// </summary>
    public static string When(DateTimeOffset createdUtc, DateTimeOffset now)
    {
        var local = createdUtc.ToOffset(now.Offset);
        var age = now - local;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        var days = (now.Date - local.Date).Days;
        return days switch
        {
            0 => $"today {time}",
            1 => $"yesterday {time}",
            < 7 => $"{local.ToString("dddd", CultureInfo.InvariantCulture)} {time}",
            _ => local.Year == now.Year
                ? $"{local.ToString("MMM d", CultureInfo.InvariantCulture)}, {time}"
                : $"{local.ToString("MMM d yyyy", CultureInfo.InvariantCulture)}, {time}",
        };
    }
}
