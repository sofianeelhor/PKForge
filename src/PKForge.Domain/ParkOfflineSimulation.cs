namespace PKForge.Domain;

public sealed record ParkOfflineResident(
    string Id,
    string Name,
    int Species,
    ParkEnvironmentId Habitat,
    string Activity = "wandering",
    IReadOnlyList<string>? Traits = null);

public enum ParkJournalEventKind
{
    Activity,
    Discovery,
    Friendship,
    Conflict,
    Reconciliation,
    Affection,
}

public sealed record ParkJournalEvent(
    DateTimeOffset Timestamp,
    ParkJournalEventKind Kind,
    string Text,
    string Habitat,
    IReadOnlyList<string> ResidentIds);

public sealed record ParkOfflineSimulationResult(
    TimeSpan SimulatedDuration,
    bool WasCapped,
    IReadOnlyList<ParkJournalEvent> Events);

/// <summary>
/// Produces a small, chronological account of park life while the application was closed.
/// It owns no persistence and is deterministic when supplied a seeded <see cref="Random"/>.
/// </summary>
public sealed class ParkOfflineSimulator
{
    public static readonly TimeSpan MaximumElapsed = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(20);
    public const int MaximumEventsPerRun = 8;

    public ParkOfflineSimulationResult Simulate(
        DateTimeOffset previous,
        DateTimeOffset now,
        IReadOnlyList<ParkOfflineResident> residents,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(residents);
        ArgumentNullException.ThrowIfNull(random);

        var actual = now > previous ? now - previous : TimeSpan.Zero;
        var elapsed = actual > MaximumElapsed ? MaximumElapsed : actual;
        if (elapsed < MinimumElapsed || residents.Count == 0)
            return new(elapsed, actual > MaximumElapsed, []);

        var eventBudget = Math.Min(MaximumEventsPerRun,
            Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes / 70)));
        var start = now - elapsed;
        var events = new List<ParkJournalEvent>(eventBudget);
        string? previousSignature = null;

        for (var i = 0; i < eventBudget; i++)
        {
            var segment = (i + 1d) / (eventBudget + 1d);
            var jitter = (random.NextDouble() - .5d) / (eventBudget + 1d);
            var timestamp = start + TimeSpan.FromTicks((long)(elapsed.Ticks * Math.Clamp(segment + jitter, .04, .96)));
            var first = residents[random.Next(residents.Count)];
            var sameHabitat = residents.Where(r => r.Id != first.Id && r.Habitat == first.Habitat).ToArray();
            var signature = "";
            ParkJournalEvent generated;

            // Social scenes only involve residents who can plausibly meet in the same habitat.
            if (sameHabitat.Length > 0 && random.Next(100) < 58)
            {
                var second = sameHabitat[random.Next(sameHabitat.Length)];
                var others = sameHabitat.Where(r => r.Id != second.Id).ToArray();
                generated = CreateSocial(timestamp, first, second, others, random, out signature);
            }
            else
            {
                generated = CreateSolo(timestamp, first, random, out signature);
            }

            // A few bounded rerolls prevent adjacent repetitions without risking an
            // unbounded generation loop when the park has only one resident.
            for (var retry = 0; signature == previousSignature && retry < 3; retry++)
                generated = CreateSolo(timestamp, first, random, out signature);
            if (signature == previousSignature) continue;
            previousSignature = signature;
            events.Add(generated);
        }

        return new(elapsed, actual > MaximumElapsed, events.OrderBy(e => e.Timestamp).ToArray());
    }

    private static ParkJournalEvent CreateSolo(
        DateTimeOffset time, ParkOfflineResident resident, Random random, out string signature)
    {
        var habitat = ParkEnvironment.Get(resident.Habitat);
        var activity = CleanActivity(resident.Activity);
        string[] lines = resident.Habitat switch
        {
            ParkEnvironmentId.WaterfallLake =>
            [
                $"{resident.Name} followed the glittering ripples near {habitat.Landmark} while {activity}.",
                $"{resident.Name} found a smooth blue pebble beside the lake and guarded it like treasure.",
                $"{resident.Name} paused to watch mist turn into a tiny rainbow above the water."
            ],
            ParkEnvironmentId.EmberGrove =>
            [
                $"{resident.Name} settled beside a comfortably warm stone while {activity}.",
                $"{resident.Name} discovered a berry warmed by the grove and waited patiently for it to cool.",
                $"{resident.Name} watched sparks rise near {habitat.Landmark}, safely out of their reach."
            ],
            ParkEnvironmentId.SkySummit =>
            [
                $"{resident.Name} listened to the wind around {habitat.Landmark} while {activity}.",
                $"{resident.Name} watched a cloud shadow travel across the summit.",
                $"{resident.Name} found a sheltered ledge and quietly admired the view."
            ],
            _ =>
            [
                $"{resident.Name} explored around {habitat.Landmark} while {activity}.",
                $"{resident.Name} found an especially pleasant patch of grass and rested there.",
                $"{resident.Name} investigated a trail of leaves, then proudly returned to the commons."
            ]
        };
        var index = random.Next(lines.Length);
        signature = $"solo:{resident.Habitat}:{index}";
        var kind = index == 1 ? ParkJournalEventKind.Discovery : ParkJournalEventKind.Activity;
        return new(time, kind, lines[index], habitat.Name, [resident.Id]);
    }

    private static ParkJournalEvent CreateSocial(
        DateTimeOffset time,
        ParkOfflineResident first,
        ParkOfflineResident second,
        IReadOnlyList<ParkOfflineResident> possibleMediators,
        Random random,
        out string signature)
    {
        var habitat = ParkEnvironment.Get(first.Habitat);
        var roll = random.Next(100);
        if (roll < 17)
        {
            if (possibleMediators.Count > 0)
            {
                var mediator = possibleMediators[random.Next(possibleMediators.Count)];
                signature = "social:reconcile";
                return new(time, ParkJournalEventKind.Reconciliation,
                    $"{first.Name} and {second.Name} argued over the best spot near {habitat.Landmark}. " +
                    $"{mediator.Name} distracted them with a game, and the tension soon disappeared.",
                    habitat.Name, [first.Id, second.Id, mediator.Id]);
            }

            signature = "social:conflict";
            return new(time, ParkJournalEventKind.Conflict,
                $"{first.Name} and {second.Name} had a brief disagreement near {habitat.Landmark}, " +
                "but both chose some space and calmed down before it became a fight.",
                habitat.Name, [first.Id, second.Id]);
        }

        if (roll < 34)
        {
            signature = "social:affection";
            return new(time, ParkJournalEventKind.Affection,
                $"{first.Name} stayed close to {second.Name} all afternoon. " +
                $"The two seem to have grown especially fond of each other.",
                habitat.Name, [first.Id, second.Id]);
        }

        var lines = new[]
        {
            $"{first.Name} and {second.Name} explored {habitat.Landmark} together and returned in excellent spirits.",
            $"{first.Name} invited {second.Name} to share a quiet break. Neither seemed in a hurry to leave.",
            $"{first.Name} started a playful chase with {second.Name}; it ended with both resting side by side.",
            $"{first.Name} showed {second.Name} something interesting nearby. They inspected it with great seriousness."
        };
        var index = random.Next(lines.Length);
        signature = $"social:friend:{index}";
        return new(time, ParkJournalEventKind.Friendship, lines[index], habitat.Name, [first.Id, second.Id]);
    }

    private static string CleanActivity(string activity)
    {
        if (string.IsNullOrWhiteSpace(activity)) return "wandering";
        var clean = activity.Trim().TrimEnd('.');
        return char.ToLowerInvariant(clean[0]) + clean[1..];
    }
}
