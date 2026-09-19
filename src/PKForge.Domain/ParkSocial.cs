namespace PKForge.Domain;

public enum ParkPersonality { Balanced, Playful, Gentle, Bold, Shy, Curious, Energetic, Calm, Helpful, Proud }
public enum ParkSocialEventKind { Play, Rest, Help, Conflict, Deescalation, Crush, Bond }

public sealed record ParkSocialPokemon(
    string Id, string Name, int Species, IReadOnlyList<int> Types, ParkPersonality Personality,
    string Family = "", ParkEnvironmentId Habitat = ParkEnvironmentId.Commons);

public sealed record ParkRelationship(
    string FirstId, string SecondId, int Friendship = 0, int Affection = 0, int Rivalry = 0,
    int SharedMoments = 0, DateTimeOffset? LastEventAt = null)
{
    public string Key => KeyFor(FirstId, SecondId);
    public static string KeyFor(string first, string second) => string.CompareOrdinal(first, second) <= 0
        ? $"{first}\u001f{second}" : $"{second}\u001f{first}";

    public ParkRelationship Normalize() => this with
    {
        Friendship = Math.Clamp(Friendship, -100, 100),
        Affection = Math.Clamp(Affection, 0, 100),
        Rivalry = Math.Clamp(Rivalry, 0, 100),
        SharedMoments = Math.Max(0, SharedMoments)
    };
}

public sealed record ParkSocialEvent(
    string Id, DateTimeOffset OccurredAt, ParkSocialEventKind Kind, string Text,
    string[] PokemonIds, int FriendshipDelta = 0, int AffectionDelta = 0, int RivalryDelta = 0);

public sealed record ParkSocialState(
    ParkRelationship[] Relationships, ParkSocialEvent[] Journal, DateTimeOffset LastSimulatedAt)
{
    public static ParkSocialState Empty(DateTimeOffset now) => new([], [], now);
}

/// <summary>Pure relationship and event simulation. Persistence and clocks live outside the domain.</summary>
public static class ParkSocialSimulation
{
    public static (ParkSocialState State, IReadOnlyList<ParkSocialEvent> Events) Simulate(
        ParkSocialState state, IReadOnlyList<ParkSocialPokemon> residents, DateTimeOffset now,
        Random? random = null, int maximumEvents = 8)
    {
        random ??= Random.Shared;
        if (residents.Count < 2 || now <= state.LastSimulatedAt)
            return (state with { LastSimulatedAt = now > state.LastSimulatedAt ? now : state.LastSimulatedAt }, []);

        var elapsed = now - state.LastSimulatedAt;
        // One possible social beat per two offline hours, capped to avoid a noisy catch-up journal.
        var count = Math.Clamp((int)Math.Floor(elapsed.TotalHours / 2), 0, Math.Max(0, maximumEvents));
        if (count == 0) return (state, []);

        var relationships = state.Relationships.ToDictionary(r => r.Key, r => r.Normalize());
        var generated = new List<ParkSocialEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var pair = PickPair(residents, random);
            var key = ParkRelationship.KeyFor(pair.A.Id, pair.B.Id);
            var relation = relationships.GetValueOrDefault(key) ?? new ParkRelationship(pair.A.Id, pair.B.Id);
            var at = state.LastSimulatedAt + TimeSpan.FromTicks(elapsed.Ticks * (i + 1) / (count + 1));
            var socialEvent = Generate(pair.A, pair.B, residents, relation, at, random);
            generated.Add(socialEvent);
            relationships[key] = Apply(relation, socialEvent, at);

            if (socialEvent.Kind == ParkSocialEventKind.Deescalation && socialEvent.PokemonIds.Length == 3)
            {
                var helper = socialEvent.PokemonIds[2];
                RewardHelper(relationships, helper, pair.A.Id, at);
                RewardHelper(relationships, helper, pair.B.Id, at);
            }
        }

        var journal = state.Journal.Concat(generated).OrderBy(e => e.OccurredAt).TakeLast(100).ToArray();
        return (new ParkSocialState(relationships.Values.ToArray(), journal, now), generated);
    }

    public static int Compatibility(ParkSocialPokemon a, ParkSocialPokemon b)
    {
        var score = 0;
        if (a.Species == b.Species) score += 22;
        if (!string.IsNullOrWhiteSpace(a.Family) && a.Family.Equals(b.Family, StringComparison.OrdinalIgnoreCase)) score += 16;
        if (a.Types.Intersect(b.Types).Any()) score += 12;
        if (a.Habitat == b.Habitat) score += 8;
        score += PersonalityCompatibility(a.Personality, b.Personality);
        return Math.Clamp(score, -30, 60);
    }

    private static int PersonalityCompatibility(ParkPersonality a, ParkPersonality b)
    {
        if (a == b) return a == ParkPersonality.Proud ? -4 : 10;
        if ((a == ParkPersonality.Playful && b is ParkPersonality.Energetic or ParkPersonality.Curious) ||
            (b == ParkPersonality.Playful && a is ParkPersonality.Energetic or ParkPersonality.Curious)) return 14;
        if ((a == ParkPersonality.Shy && b is ParkPersonality.Gentle or ParkPersonality.Calm or ParkPersonality.Helpful) ||
            (b == ParkPersonality.Shy && a is ParkPersonality.Gentle or ParkPersonality.Calm or ParkPersonality.Helpful)) return 16;
        if ((a == ParkPersonality.Bold && b == ParkPersonality.Proud) ||
            (b == ParkPersonality.Bold && a == ParkPersonality.Proud)) return -16;
        if ((a == ParkPersonality.Calm && b == ParkPersonality.Energetic) ||
            (b == ParkPersonality.Calm && a == ParkPersonality.Energetic)) return -3;
        return 2;
    }

    private static ParkSocialEvent Generate(ParkSocialPokemon a, ParkSocialPokemon b,
        IReadOnlyList<ParkSocialPokemon> residents, ParkRelationship relation, DateTimeOffset at, Random random)
    {
        var compatibility = Compatibility(a, b);
        var conflictChance = Math.Clamp(8 + relation.Rivalry / 3 - compatibility / 3, 2, 38);
        if (random.Next(100) < conflictChance)
        {
            var mediator = residents.Where(p => p.Id != a.Id && p.Id != b.Id)
                .OrderByDescending(p => MediatorScore(p)).ThenBy(_ => random.Next()).FirstOrDefault();
            if (mediator is not null && random.Next(100) < Math.Clamp(45 + MediatorScore(mediator), 45, 92))
                return Event(ParkSocialEventKind.Deescalation, at,
                    $"{a.Name} and {b.Name} became upset during a game, but {mediator.Name} stepped between them and helped everyone cool down.",
                    [a.Id, b.Id, mediator.Id], 1, 0, -5);
            return Event(ParkSocialEventKind.Conflict, at,
                $"{a.Name} and {b.Name} disagreed over the same favourite spot. They traded grumpy looks, then wisely gave each other some space.",
                [a.Id, b.Id], -2, 0, 6);
        }

        var affectionChance = Math.Clamp((relation.Friendship + compatibility + relation.SharedMoments / 2) / 4, 0, 32);
        if (random.Next(100) < affectionChance)
        {
            var kind = relation.Affection >= 35 ? ParkSocialEventKind.Bond : ParkSocialEventKind.Crush;
            var text = kind == ParkSocialEventKind.Bond
                ? $"{a.Name} and {b.Name} spent a quiet moment together, perfectly content just to share the view. Their bond feels stronger."
                : $"{a.Name} brought {b.Name} a carefully chosen berry, then became adorably shy when it was accepted.";
            return Event(kind, at, text, [a.Id, b.Id], 3, kind == ParkSocialEventKind.Bond ? 4 : 3, -1);
        }

        var roll = random.Next(3);
        if (roll == 0)
            return Event(ParkSocialEventKind.Play, at, PlayText(a, b), [a.Id, b.Id], 3, 0, -1);
        if (roll == 1)
            return Event(ParkSocialEventKind.Help, at, HelpText(a, b), [a.Id, b.Id], 4, 1, -2);
        return Event(ParkSocialEventKind.Rest, at, RestText(a, b), [a.Id, b.Id], 2, 1, -1);
    }

    private static string PlayText(ParkSocialPokemon a, ParkSocialPokemon b) => a.Habitat switch
    {
        ParkEnvironmentId.WaterfallLake => $"{a.Name} and {b.Name} raced drifting leaves across the shallows, cheering for both leaves at once.",
        ParkEnvironmentId.EmberGrove => $"{a.Name} and {b.Name} invented a careful game of hopping between comfortably warm stones.",
        ParkEnvironmentId.SkySummit => $"{a.Name} and {b.Name} chased cloud shadows over the plateau until they both needed a rest.",
        _ => $"{a.Name} and {b.Name} turned a fallen berry into the park's most important ball game."
    };

    private static string RestText(ParkSocialPokemon a, ParkSocialPokemon b) => a.Habitat switch
    {
        ParkEnvironmentId.WaterfallLake => $"{a.Name} and {b.Name} dozed beside the waterfall, soothed by its steady rumble.",
        ParkEnvironmentId.EmberGrove => $"{a.Name} and {b.Name} shared a warm stone and watched harmless sparks drift upward.",
        ParkEnvironmentId.SkySummit => $"{a.Name} and {b.Name} rested in a sheltered nook while clouds sailed below them.",
        _ => $"{a.Name} and {b.Name} fell asleep back-to-back in a sunny patch of grass."
    };

    private static string HelpText(ParkSocialPokemon a, ParkSocialPokemon b) =>
        $"{a.Name} noticed {b.Name} struggling with a tangled branch and hurried over to help. Together, it was easy.";

    private static ParkSocialEvent Event(ParkSocialEventKind kind, DateTimeOffset at, string text,
        string[] ids, int friendship, int affection, int rivalry) =>
        new(Guid.NewGuid().ToString("N"), at, kind, text, ids, friendship, affection, rivalry);

    private static ParkRelationship Apply(ParkRelationship relationship, ParkSocialEvent socialEvent, DateTimeOffset at) =>
        (relationship with
        {
            Friendship = relationship.Friendship + socialEvent.FriendshipDelta,
            Affection = relationship.Affection + socialEvent.AffectionDelta,
            Rivalry = relationship.Rivalry + socialEvent.RivalryDelta,
            SharedMoments = relationship.SharedMoments + 1,
            LastEventAt = at
        }).Normalize();

    private static void RewardHelper(Dictionary<string, ParkRelationship> relationships, string helper, string other, DateTimeOffset at)
    {
        var key = ParkRelationship.KeyFor(helper, other);
        var relation = relationships.GetValueOrDefault(key) ?? new ParkRelationship(helper, other);
        relationships[key] = (relation with { Friendship = relation.Friendship + 2, SharedMoments = relation.SharedMoments + 1, LastEventAt = at }).Normalize();
    }

    private static int MediatorScore(ParkSocialPokemon pokemon) => pokemon.Personality switch
    {
        ParkPersonality.Helpful => 35, ParkPersonality.Gentle => 30, ParkPersonality.Calm => 25,
        ParkPersonality.Shy => 8, ParkPersonality.Proud => 0, _ => 12
    };

    private static (ParkSocialPokemon A, ParkSocialPokemon B) PickPair(IReadOnlyList<ParkSocialPokemon> residents, Random random)
    {
        var first = random.Next(residents.Count);
        var second = random.Next(residents.Count - 1);
        if (second >= first) second++;
        return (residents[first], residents[second]);
    }
}
