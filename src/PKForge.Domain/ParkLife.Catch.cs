namespace PKForge.Domain;

/// <summary>How the player tries to win a wild visitor over.</summary>
public enum ParkApproach { Berry, Wait, Ball }

/// <summary>How a catch attempt ended: caught, still unsure (try again), or gone.</summary>
public enum ParkCatchOutcome { Caught, Wary, Fled }

/// <summary>A wild visitor as the catch screen sees it: who it is, how strong, and a fair hint
/// of what it likes.</summary>
public sealed record ParkCatchTarget(string Id, string Name, int Species, bool Shiny, ParkPersonality Personality, int Level, string Place, string Hint, int TriesLeft);

public sealed record ParkCatchResult(ParkCatchOutcome Outcome, string Line, ParkCatchTarget? Target);

/// <summary>A wild kind that can wander into a place.</summary>
public sealed record ParkWildKind(string Name, int Species);

/// <summary>
/// The rules of catching a wild visitor, small and fair: every temperament has one approach it
/// warms to, which the hint names, and one it dislikes. Three tries at most; each one it lets
/// pass makes it a little more used to you. Nothing is hidden: the odds below are all there is.
/// </summary>
public static class ParkCatch
{
    public const int Tries = 3;

    /// <summary>The approach a temperament warms to.</summary>
    public static ParkApproach Likes(ParkPersonality personality) => personality switch
    {
        ParkPersonality.Playful or ParkPersonality.Energetic or ParkPersonality.Bold or ParkPersonality.Proud => ParkApproach.Ball,
        ParkPersonality.Shy or ParkPersonality.Calm or ParkPersonality.Gentle => ParkApproach.Wait,
        _ => ParkApproach.Berry,
    };

    /// <summary>The approach a temperament dislikes.</summary>
    public static ParkApproach Dislikes(ParkPersonality personality) => personality switch
    {
        ParkPersonality.Shy or ParkPersonality.Calm or ParkPersonality.Gentle => ParkApproach.Ball,
        ParkPersonality.Playful or ParkPersonality.Energetic or ParkPersonality.Bold or ParkPersonality.Proud => ParkApproach.Wait,
        _ => ParkApproach.Wait,
    };

    /// <summary>What the player can see of a temperament before trying: it names the liked approach.</summary>
    public static string Hint(string name, ParkPersonality personality) => Likes(personality) switch
    {
        ParkApproach.Ball => $"The {name} keeps bouncing on the spot, as if it wanted to play.",
        ParkApproach.Wait => $"The {name} keeps its distance and watches you quietly.",
        _ => $"The {name} keeps sniffing the air, curious about your bag.",
    };

    /// <summary>The chance, out of 100, that an approach wins it over on a given try (0-based).</summary>
    public static int CatchOdds(ParkPersonality personality, ParkApproach approach, int attempt) =>
        Math.Min(95, (approach == Likes(personality) ? 70 : approach == Dislikes(personality) ? 20 : 40) + 10 * Math.Max(0, attempt));

    /// <summary>The chance, out of 100, that a failed try makes it run off.</summary>
    public static int FleeOdds(ParkPersonality personality, ParkApproach approach, int attempt) =>
        attempt + 1 >= Tries ? 100 : approach == Dislikes(personality) ? 50 : 15;

    /// <summary>One try, from two rolls in 0..99.</summary>
    public static ParkCatchOutcome Resolve(ParkPersonality personality, ParkApproach approach, int attempt, int catchRoll, int fleeRoll) =>
        catchRoll < CatchOdds(personality, approach, attempt) ? ParkCatchOutcome.Caught
        : fleeRoll < FleeOdds(personality, approach, attempt) ? ParkCatchOutcome.Fled : ParkCatchOutcome.Wary;

    /// <summary>The level of a wild one by where it came in: little ones by the home park, stronger
    /// ones out in the wilder places.</summary>
    public static int Level(string place, int roll) => place is "home" or "meadow" ? 2 + Math.Abs(roll) % 5 : 8 + Math.Abs(roll) % 13;
}

public sealed partial class ParkLife
{
    private readonly Dictionary<string, int> _catchTries = new();

    /// <summary>The wild kinds that can wander into a place at a time of day and in a weather:
    /// the water's edge has the rain ones in the rain, the home park (and any place without its
    /// own) the everyday ones, every other place its own.</summary>
    public static IReadOnlyList<ParkWildKind> WildKinds(string place, ParkDayPhase phase, ParkWeather weather)
    {
        var night = phase == ParkDayPhase.Night;
        var pool = place == "home" || !PlaceWild.TryGetValue(place, out var own) ? night ? NightWild : DayWild : night ? own.Night : own.Day;
        // In the rain the water's edge also gets the rain ones (see UpdateVisitors).
        if (weather == ParkWeather.Rain && place is "pool" or "coast") pool = [.. pool, .. RainWild];
        return pool.DistinctBy(w => w.Species).Select(w => new ParkWildKind(w.Name, w.Species)).ToArray();
    }

    /// <summary>What the player may try to catch: a wild visitor in sight (not Blissey, not a
    /// Pokémon that came back from the Wilds, not one already leaving).</summary>
    public ParkCatchTarget? CatchTarget(string id) { lock (_gate) return CatchTargetCore(id); }

    private ParkCatchTarget? CatchTargetCore(string id)
    {
        if (!IsVisitor(id) || IsKeeper(id) || OldFriendOf(id) is not null || _leaving.Contains(id) || !_actors.TryGetValue(id, out var visitor)) return null;
        var resident = visitor.Resident;
        var place = visitor.Place ?? "home";
        var tries = _catchTries.GetValueOrDefault(id);
        if (tries >= ParkCatch.Tries) return null;
        return new ParkCatchTarget(id, resident.Name, resident.Species, resident.Shiny, resident.Personality,
            ParkCatch.Level(place, StableRoll(id)), place, ParkCatch.Hint(resident.Name, resident.Personality), ParkCatch.Tries - tries);
    }

    // A roll fixed for a visitor, so its level never changes between looks.
    private static int StableRoll(string id)
    {
        var hash = 17;
        foreach (var c in id) hash = unchecked(hash * 31 + c);
        return hash & int.MaxValue;
    }

    /// <summary>One try at winning a wild visitor over. Caught, it leaves the park with the player
    /// (the page sends it on); fled, it runs back out the way it came.</summary>
    public ParkCatchResult TryCatch(string id, ParkApproach approach) { lock (_gate) return TryCatchCore(id, approach); }

    private ParkCatchResult TryCatchCore(string id, ParkApproach approach)
    {
        if (CatchTargetCore(id) is not { } target || !_actors.TryGetValue(id, out var visitor))
            return new(ParkCatchOutcome.Fled, "There is no wild Pokémon there any more.", null);
        var attempt = _catchTries.GetValueOrDefault(id);
        _catchTries[id] = attempt + 1;
        var outcome = ParkCatch.Resolve(target.Personality, approach, attempt, visitor.Random.Next(100), visitor.Random.Next(100));
        var name = target.Name;
        Interrupt(visitor);
        switch (outcome)
        {
            case ParkCatchOutcome.Caught:
                Burst(ParkEffectKind.Hearts, visitor);
                if (!_leaving.Contains(id)) _leaving.Add(id);
                _catchTries.Remove(id);
                _moments.Add(new ParkMoment(ParkSocialEventKind.Bond, id, "", $"A wild {name} chose to come along with you."));
                return new(outcome, approach switch
                {
                    ParkApproach.Ball => $"The {name} bats the ball back, then hops into it on its own. It wants to come with you!",
                    ParkApproach.Wait => $"You wait. Little by little the {name} comes closer, and at last it nuzzles your hand. It wants to come with you!",
                    _ => $"The {name} takes the berry from your palm and stays by your side. It wants to come with you!",
                }, target);
            case ParkCatchOutcome.Fled:
                visitor.LeaveAt = 0;
                Add(visitor, new Label("Running off", "Scared"), new Act(ParkPose.Cringe, 500, 0, ParkEmote.Exclaim));
                Depart(visitor, hurry: true);
                _catchTries.Remove(id);
                return new(outcome, $"The {name} startles and darts off the way it came. Maybe another one will come by.", target);
            default:
                Add(visitor, new Label("Not sure about you", "Curious"), new Act(ParkPose.LookUp, 900, 0, ParkEmote.Question));
                visitor.LeaveAt = Math.Max(visitor.LeaveAt, Now + 60_000);
                return new(outcome, $"The {name} hesitates and backs off a step, but it doesn't run. {ParkCatch.Tries - attempt - 1} {(ParkCatch.Tries - attempt - 1 == 1 ? "try" : "tries")} left.",
                    target with { TriesLeft = ParkCatch.Tries - attempt - 1 });
        }
    }
}
