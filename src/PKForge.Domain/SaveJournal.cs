using System.Globalization;

namespace PKForge.Domain;

/// <summary>
/// One Pokémon as the journal follows it from one reading of a save to the next.
/// <paramref name="Key"/> stays the same through level-ups and evolutions (it is built
/// from what the game never changes: PID, encryption constant, IVs, original trainer).
/// </summary>
public sealed record JournalMon(string Key, int Species, int Form, int Level, string? Nickname, string? MetPlace);

/// <summary>What the journal reads of a save at one moment. PlayMinutes is null when the game keeps no clock.</summary>
public sealed record JournalState(
    uint Money, int? PlayMinutes, int DexSeen, int DexCaught, SaveBadges? Badges, IReadOnlyList<JournalMon> Roster);

public enum JournalFactKind { Badge, Caught, Evolved, LevelUp, Pokedex, Money, PlayTime, Left }

/// <summary>One thing that happened between two readings, as a sentence.</summary>
public sealed record JournalFact(JournalFactKind Kind, string Text);

/// <summary>
/// The game journal: "what changed since last time". Two readings of a save in, a list of
/// facts out: Pokémon caught and where, level-ups, evolutions, badges, Pokédex, money, play time.
/// </summary>
public static class SaveJournal
{
    /// <summary>Reads the journal's view of an open save. Parts a game does not keep read as zero or null.</summary>
    public static JournalState Capture(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var trainer = session.GetTrainer();
        int? minutes = null;
        try
        {
            var stats = session.GetTrainerStats();
            if (stats.SupportsPlayTime) minutes = stats.PlayTimeHours * 60 + stats.PlayTimeMinutes;
        }
        catch (NotSupportedException) { }
        var dex = session.GetDexProgress();
        return new JournalState(trainer.Money, minutes, dex.Seen, dex.Caught, session.GetBadges(), session.GetJournalRoster());
    }

    /// <summary>What happened between <paramref name="before"/> and <paramref name="after"/>, most notable first.</summary>
    public static IReadOnlyList<JournalFact> Diff(JournalState before, JournalState after, Func<int, string> speciesName)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(speciesName);
        var facts = new List<JournalFact>();

        Badges(before.Badges, after.Badges, facts);

        // Pair the Pokémon by key; two identical clones pair off one for one.
        var earlier = before.Roster.GroupBy(mon => mon.Key).ToDictionary(group => group.Key, group => new Queue<JournalMon>(group));
        var caught = new List<JournalFact>();
        var evolved = new List<JournalFact>();
        var levelled = new List<JournalFact>();
        foreach (var mon in after.Roster)
        {
            if (!earlier.TryGetValue(mon.Key, out var twins) || twins.Count == 0)
            {
                var where = string.IsNullOrWhiteSpace(mon.MetPlace) ? "" : $" ({mon.MetPlace})";
                caught.Add(new JournalFact(JournalFactKind.Caught, $"New: {Name(mon, speciesName)}, Lv. {mon.Level}{where}"));
                continue;
            }
            var old = twins.Dequeue();
            if (old.Species != mon.Species)
                evolved.Add(new JournalFact(JournalFactKind.Evolved,
                    $"{Name(old, speciesName)} evolved into {speciesName(mon.Species)}" + (mon.Level != old.Level ? $" (Lv. {mon.Level})" : "")));
            else if (mon.Level > old.Level)
                levelled.Add(new JournalFact(JournalFactKind.LevelUp, $"{Name(mon, speciesName)}: Lv. {old.Level} → {mon.Level}"));
        }
        facts.AddRange(caught);
        facts.AddRange(evolved);
        facts.AddRange(levelled);

        var dexSeen = after.DexSeen - before.DexSeen;
        var dexCaught = after.DexCaught - before.DexCaught;
        if (dexSeen != 0 || dexCaught != 0)
            facts.Add(new JournalFact(JournalFactKind.Pokedex,
                $"Pokédex: {Signed(dexCaught)} caught, {Signed(dexSeen)} seen (now {after.DexCaught} caught, {after.DexSeen} seen)"));

        if (after.Money != before.Money)
            facts.Add(new JournalFact(JournalFactKind.Money,
                $"Money: {Signed((long)after.Money - before.Money)} (now {Number(after.Money)})"));

        if (before.PlayMinutes is { } then && after.PlayMinutes is { } now && now > then)
            facts.Add(new JournalFact(JournalFactKind.PlayTime,
                $"Played {Duration(now - then)} more (now {now / 60}:{now % 60:00})"));

        var gone = earlier.Values.SelectMany(queue => queue).ToList();
        if (gone.Count > 0)
            facts.Add(new JournalFact(JournalFactKind.Left,
                $"No longer in the save (released, traded or moved out): {string.Join(", ", gone.Select(mon => Name(mon, speciesName)))}"));
        return facts;
    }

    /// <summary>
    /// The restore point "last time" means for the file <paramref name="documentId"/>: its newest
    /// one that is not the very bytes the file holds now (a snapshot just taken tells nothing).
    /// Null when there is none.
    /// </summary>
    public static BackupInfo? Baseline(IEnumerable<BackupInfo> points, string documentId, string? currentSha256)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points
            .Where(point => string.Equals(point.DocumentId, documentId, StringComparison.Ordinal)
                && !string.Equals(point.Sha256, currentSha256, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(point => point.CreatedUtc)
            .FirstOrDefault();
    }

    /// <summary>The facts as the lines of a message, at most <paramref name="maxLines"/>; the rest is counted.</summary>
    public static string Report(IReadOnlyList<JournalFact> facts, int maxLines = 24)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0) return "Nothing changed.";
        var shown = facts.Take(maxLines).Select(fact => "• " + fact.Text).ToList();
        if (facts.Count > maxLines) shown.Add($"…and {facts.Count - maxLines} more.");
        return string.Join('\n', shown);
    }

    private static void Badges(SaveBadges? before, SaveBadges? after, List<JournalFact> facts)
    {
        if (before is null || after is null || after.Earned == before.Earned && after.Names.SequenceEqual(before.Names)) return;
        var won = after.Names.Except(before.Names, StringComparer.Ordinal).ToList();
        if (won.Count > 0)
        {
            foreach (var name in won)
                facts.Add(new JournalFact(JournalFactKind.Badge, $"Earned the {name} Badge ({after.Earned} of {after.Total})"));
            return;
        }
        facts.Add(new JournalFact(JournalFactKind.Badge, after.Earned > before.Earned
            ? $"Earned {after.Earned - before.Earned} {(after.Earned - before.Earned == 1 ? "badge" : "badges")} ({after.Earned} of {after.Total})"
            : $"Badges: {before.Earned} → {after.Earned} of {after.Total}"));
    }

    private static string Name(JournalMon mon, Func<int, string> speciesName) =>
        string.IsNullOrWhiteSpace(mon.Nickname) ? speciesName(mon.Species) : $"{mon.Nickname} ({speciesName(mon.Species)})";

    // The app speaks English whatever the device's language: 3,000, never 3 000.
    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Signed(long value) => value > 0 ? "+" + Number(value) : value < 0 ? "−" + Number(-value) : "0";

    private static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
}
