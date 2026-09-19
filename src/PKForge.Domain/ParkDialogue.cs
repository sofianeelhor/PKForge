namespace PKForge.Domain;

/// <summary>Stable personality hints used to make park dialogue feel individual without owning save data.</summary>
/// <summary>Everything the dialogue picker can know about a resident at the moment a line is requested.</summary>
public sealed record ParkDialogueContext(
    int Species,
    string Name,
    IReadOnlyList<int> Types,
    ParkEnvironmentId Environment,
    string Activity,
    string Mood,
    ParkPersonality Personality = ParkPersonality.Balanced,
    string Action = "Talk",
    string? NearbyPokemon = null);

/// <summary>A selected line and its stable identity. Store <see cref="Id"/> in recent history.</summary>
public sealed record ParkDialogue(string Id, string Text, string Source);

/// <summary>
/// Context-aware, data-driven dialogue picker. Callers retain a short list of returned IDs per resident;
/// the picker avoids them whenever another coherent line exists.
/// </summary>
public static class ParkDialogueGenerator
{
    private sealed record Line(string Id, string Text, int Species = 0, int Type = 0,
        ParkEnvironmentId? Environment = null, string? Activity = null, string? Mood = null,
        ParkPersonality Personality = ParkPersonality.Balanced, string? Action = null, int Weight = 1,
        string Source = "general");

    private static readonly Line[] Lines =
    [
        // Species signatures: deliberately behavioural rather than generic Pokédex exposition.
        S(25, "cheeks", "{name}'s cheeks crackle softly as it leans closer. It seems pleased you came.", action: "talk"),
        S(25, "race", "{name} zigzags around you, then looks back with an unmistakable challenge to keep up.", activity: "stroll"),
        S(25, "charge", "{name} presses its tail to the grass. Tiny sparks run between the blades like fireflies.", env: ParkEnvironmentId.Commons),
        S(1, "sunpatch", "{name} settles where the sunlight reaches its bulb and gives a slow, contented stretch.", mood: "content"),
        S(1, "scent", "The bulb on {name}'s back releases a fresh garden scent. Nearby leaves seem to perk up too."),
        S(4, "flame", "{name} checks its tail flame, then proudly angles it away from the dry grass.", env: ParkEnvironmentId.EmberGrove),
        S(4, "toast", "{name} carefully warms a berry, turning it one claw at a time before offering it to you.", action: "snack"),
        S(7, "shell", "{name} ducks into its shell for a moment, then peeks out with a mischievous grin.", personality: ParkPersonality.Playful),
        S(7, "spray", "{name} draws a shimmering arc over the lake and watches the droplets fall.", env: ParkEnvironmentId.WaterfallLake),
        S(39, "song", "{name} hums a tiny melody and waits expectantly for you to supply the next note.", action: "talk"),
        S(39, "nap", "{name}'s eyes droop halfway through its own song. The final note becomes a yawn.", mood: "sleepy"),
        S(52, "treasure", "{name} proudly presents a bottle cap it found. This is apparently a priceless treasure."),
        S(54, "headache", "{name} holds its head, forgets why, then brightens when it notices you.", mood: "confused"),
        S(54, "float", "{name} sits at the water's edge, watching its reflection with profound concentration.", env: ParkEnvironmentId.WaterfallLake),
        S(94, "shadow", "{name}'s grin appears from the shadow a heartbeat before the rest of it does.", personality: ParkPersonality.Playful),
        S(94, "joke", "{name} makes a ridiculous face behind {nearby}, then vanishes before it can be blamed.", action: "play"),
        S(133, "choice", "{name} studies every path with bright eyes, as if each one might lead to a different adventure.", personality: ParkPersonality.Curious),
        S(133, "fur", "A breeze ruffles {name}'s collar. It gives itself a dignified shake and sits beside you.", env: ParkEnvironmentId.SkySummit),
        S(143, "dream", "{name} smiles in its sleep. Whatever it is dreaming about must smell delicious.", mood: "sleepy"),
        S(143, "snack", "{name} accepts the snack with solemn gratitude, then immediately checks whether there is another.", action: "snack"),
        S(151, "dance", "{name} loops through the air in a carefree little dance, inviting you to forget the time.", action: "play"),
        S(152, "leaf", "{name} tilts the leaf on its head toward the sun, then offers you its patch of shade.", env: ParkEnvironmentId.Commons),
        S(155, "warmth", "{name} curls into a warm little circle. Its back gives off just enough heat to be comforting.", action: "relax"),
        S(158, "chomp", "{name} snaps playfully at a splash, then looks delighted when the water splashes back.", env: ParkEnvironmentId.WaterfallLake),
        S(172, "spark", "{name} sneezes a tiny spark, freezes in surprise, then acts as though it was intentional."),
        S(196, "sun", "{name} sits perfectly still in a beam of sunlight, its forked tail swaying like a pendulum.", action: "relax"),
        S(197, "moon", "{name} chooses the deepest patch of shade and watches the park with calm, gleaming eyes.", action: "relax"),
        S(282, "courtesy", "{name} gives you a graceful bow, then quietly checks that {nearby} is comfortable too.", personality: ParkPersonality.Gentle),
        S(448, "aura", "{name} closes its eyes. For a moment, it seems to be listening to every heartbeat in the park.", action: "talk"),
        S(448, "training", "{name} practices one precise step, then another, careful not to disturb anyone nearby.", activity: "stroll"),
        S(700, "ribbons", "{name}'s feelers curl gently around your wrist; a warm, reassuring feeling follows.", action: "talk"),
        S(778, "costume", "{name} adjusts its crooked disguise and waits very still for your approval.", action: "talk"),
        S(778, "lonely", "{name} lingers near {nearby}, pretending it only happened to choose the same spot.", mood: "lonely"),
        S(906, "perfume", "{name} washes its face with one paw, leaving the air faintly scented with flowers."),
        S(909, "hotstep", "{name} stamps out a cheerful rhythm; each step leaves a momentary glow.", action: "play"),
        S(912, "dance", "{name} turns a puddle into a stage, finishing its dance with an extravagant bow.", action: "play"),

        // Habitat and elemental observations.
        T(ParkType.Water, "water-ripple", "{name} follows the ripples with patient fascination, as comfortable as if the lake were home.", ParkEnvironmentId.WaterfallLake),
        T(ParkType.Fire, "fire-stones", "{name} tests each sun-warmed stone before choosing the coziest one.", ParkEnvironmentId.EmberGrove),
        T(ParkType.Grass, "grass-whisper", "Leaves turn toward {name} as the breeze moves through the commons.", ParkEnvironmentId.Commons),
        T(ParkType.Flying, "flying-updraft", "{name} opens wide to the summit wind and rises without a single hurried movement.", ParkEnvironmentId.SkySummit),
        T(ParkType.Ice, "ice-breath", "{name}'s breath becomes silver mist in the crisp summit air.", ParkEnvironmentId.SkySummit),
        T(ParkType.Electric, "electric-hum", "The grass around {name} hums with harmless static.", ParkEnvironmentId.Commons),
        T(ParkType.Ghost, "ghost-shadow", "{name}'s shadow wanders half a step out of rhythm, apparently enjoying itself."),
        T(ParkType.Psychic, "psychic-pebbles", "A few pebbles orbit {name} while it thinks, then settle neatly back where they began."),
        T(ParkType.Fairy, "fairy-glimmer", "A soft glimmer follows {name}; it fades whenever you look straight at it."),
        T(ParkType.Dragon, "dragon-watch", "{name} surveys the park with the grave responsibility of a tiny guardian."),
        T(ParkType.Dark, "dark-hide", "{name} finds a patch of shade with an excellent view of everyone else's mischief."),
        T(ParkType.Bug, "bug-flowers", "{name} inspects every flower with the seriousness of an expert gardener.", ParkEnvironmentId.Commons),
        T(ParkType.Rock, "rock-summit", "{name} leans into the mountain wind, utterly steady.", ParkEnvironmentId.SkySummit),

        // Actions, activities, moods and personalities combine with every species.
        C("action-talk-secret", "{name} leans closer as if sharing an important secret. The meaning is unclear, but the trust is not.", action: "talk"),
        C("action-play-chase", "{name} darts away, pauses until you notice, then starts an enthusiastic game of chase.", action: "play"),
        C("action-snack-share", "{name} saves the last bite, considers it carefully, and offers half to {nearby}.", action: "snack"),
        C("action-relax-breathe", "{name} settles beside you. For a quiet moment, you breathe at the same unhurried pace.", action: "relax"),
        C("activity-stroll-trail", "{name} pauses mid-stroll to investigate a trail of tiny footprints.", activity: "stroll"),
        C("activity-water-listen", "{name} goes still, listening to the many voices hidden in the moving water.", activity: "water"),
        C("activity-glide-cloud", "{name} glides through a cloud wisp and emerges sparkling with mist.", activity: "glid"),
        C("mood-happy", "{name} can barely contain its happiness; even standing still turns into a little dance.", mood: "happy"),
        C("mood-curious", "{name} tilts its head. Whatever you do next has its complete attention.", mood: "curious"),
        C("mood-content", "{name} looks around the park and gives a small, deeply satisfied sigh.", mood: "content"),
        C("mood-tired", "{name} tries to hide a yawn, then decides there is no reason to pretend.", mood: "tired"),
        C("personality-shy", "{name} watches from a comfortable distance. When you sit quietly, it edges a little closer.", personality: ParkPersonality.Shy),
        C("personality-proud", "{name} strikes its finest pose and waits, not impatiently, but definitely, for admiration.", personality: ParkPersonality.Proud),
        C("personality-playful", "{name} circles behind you and taps your shoulder from the other side.", personality: ParkPersonality.Playful),
        C("personality-calm", "The bustle of the park seems to soften around {name}'s calm presence.", personality: ParkPersonality.Calm),
        C("fallback-observe", "{name} pauses to watch the park's small comings and goings."),
        C("fallback-greeting", "{name} notices you and offers a greeting in its own unmistakable way."),
        C("fallback-together", "{name} seems happy simply sharing this part of the day with you."),
        C("fallback-nearby", "{name} and {nearby} exchange a glance, then quietly return to their respective adventures.")
    ];

    public static ParkDialogue Generate(ParkDialogueContext context, IReadOnlyCollection<string>? recentIds = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var matches = Lines.Select(line => (line, score: Score(line, context)))
            .Where(x => x.score >= 0).ToArray();
        var unseen = recentIds is { Count: > 0 }
            ? matches.Where(x => !recentIds.Contains(x.line.Id)).ToArray()
            : matches;
        var pool = unseen.Length > 0 ? unseen : matches;
        var best = pool.Max(x => x.score);
        // Keep variety while preventing a generic line from beating strongly contextual material.
        var finalists = pool.Where(x => x.score >= best - 2).ToArray();
        var chosen = WeightedPick(finalists, random ?? Random.Shared).line;
        return new(chosen.Id, Render(chosen.Text, context), chosen.Source);
    }

    private static int Score(Line line, ParkDialogueContext c)
    {
        var score = 0;
        if (line.Species != 0) { if (line.Species != c.Species) return -1; score += 12; }
        if (line.Type != 0) { if (!c.Types.Contains(line.Type)) return -1; score += 5; }
        if (line.Environment is { } env) { if (env != c.Environment) return -1; score += 5; }
        if (line.Personality != ParkPersonality.Balanced) { if (line.Personality != c.Personality) return -1; score += 4; }
        if (line.Action is { } action) { if (!Contains(c.Action, action)) return -1; score += 4; }
        if (line.Activity is { } activity) { if (!Contains(c.Activity, activity)) return -1; score += 3; }
        if (line.Mood is { } mood) { if (!Contains(c.Mood, mood)) return -1; score += 3; }
        return score;
    }

    private static bool Contains(string value, string expected) => value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    private static (Line line, int score) WeightedPick((Line line, int score)[] choices, Random random)
    {
        var total = choices.Sum(x => x.line.Weight);
        var roll = random.Next(total);
        foreach (var choice in choices)
            if ((roll -= choice.line.Weight) < 0) return choice;
        return choices[^1];
    }

    private static string Render(string template, ParkDialogueContext c)
    {
        var nearby = string.IsNullOrWhiteSpace(c.NearbyPokemon) ? "another Pokémon" : c.NearbyPokemon;
        return template.Replace("{name}", c.Name, StringComparison.Ordinal)
            .Replace("{nearby}", nearby, StringComparison.Ordinal);
    }

    private static Line S(int species, string id, string text, ParkEnvironmentId? env = null,
        string? activity = null, string? mood = null, ParkPersonality personality = ParkPersonality.Balanced, string? action = null)
        => new($"species-{species}-{id}", text, Species: species, Environment: env, Activity: activity,
            Mood: mood, Personality: personality, Action: action, Source: "species");
    private static Line T(int type, string id, string text, ParkEnvironmentId? env = null)
        => new($"type-{type}-{id}", text, Type: type, Environment: env, Source: "type/habitat");
    private static Line C(string id, string text, string? activity = null, string? mood = null,
        ParkPersonality personality = ParkPersonality.Balanced, string? action = null)
        => new(id, text, Activity: activity, Mood: mood, Personality: personality, Action: action, Source: "context");
}

/// <summary>Small bounded history helper suitable for persisting one instance per resident.</summary>
public sealed class ParkDialogueHistory(int capacity = 12)
{
    private readonly Queue<string> _ids = new();
    public IReadOnlyCollection<string> Recent => _ids;
    public void Remember(ParkDialogue dialogue)
    {
        if (capacity <= 0) return;
        _ids.Enqueue(dialogue.Id);
        while (_ids.Count > capacity) _ids.Dequeue();
    }
    public void Clear() => _ids.Clear();
}
