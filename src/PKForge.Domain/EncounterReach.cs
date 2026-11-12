namespace PKForge.Domain;

public enum EncounterReachKind { Now, Later, Unknown }

/// <summary>Whether a way to get a Pokémon looks reachable yet, and why, in a few words.</summary>
public sealed record EncounterReachVerdict(EncounterReachKind Reach, string Note);

/// <summary>
/// "Where can I catch it now?": an estimate, from the badges a save holds, of which encounter
/// cards the player can already reach. The save does not say which routes are open, so the
/// encounter's level is held against the level a team usually has with that many badges:
/// wild Pokémon get stronger as the story goes on. It is a heuristic, and says so.
/// </summary>
public static class EncounterReach
{
    // The highest encounter level the story usually reaches with 0..7 of 8 badges.
    private static readonly int[] Band = [15, 22, 28, 34, 40, 45, 50, 55];

    /// <summary>
    /// The encounter level within reach with these badges; null when the save gives none to
    /// judge by. Every badge of the region's League (eight) opens everything.
    /// </summary>
    public static int? LevelBand(SaveBadges? badges)
    {
        if (badges is not { Total: > 0 }) return null;
        // HeartGold/SoulSilver keep Johto's and Kanto's sixteen: eight is the League.
        var earned = Math.Clamp(badges.Earned, 0, badges.Total);
        return earned >= 8 ? 100 : Band[earned];
    }

    public static EncounterReachVerdict Judge(EncounterCard card, SaveBadges? badges)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (LevelBand(badges) is not { } band) return new(EncounterReachKind.Unknown, "");
        if (card.Kind == "Event") return new(EncounterReachKind.Unknown, "an event, not part of the story");
        if (card.Kind == "Egg") return new(EncounterReachKind.Now, "bred from a parent");
        if (band >= 100) return new(EncounterReachKind.Now, "every badge earned");
        return card.LevelMin <= band
            ? new(EncounterReachKind.Now, $"Lv. {card.LevelMin}, within reach with {badges!.Earned} {Badges(badges.Earned)} (estimate)")
            : new(EncounterReachKind.Later, $"Lv. {card.LevelMin}, usually after more than {badges!.Earned} {Badges(badges.Earned)} (estimate)");
    }

    /// <summary>Each card's verdict, in the cards' own order (indices stay those of the list).</summary>
    public static IReadOnlyList<EncounterReachVerdict> JudgeAll(IReadOnlyList<EncounterCard> cards, SaveBadges? badges)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return [.. cards.Select(card => Judge(card, badges))];
    }

    private static string Badges(int count) => count == 1 ? "badge" : "badges";
}
