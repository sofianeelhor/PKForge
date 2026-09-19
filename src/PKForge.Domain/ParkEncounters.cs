namespace PKForge.Domain;

public sealed record ParkEncounter(string Id, string Text, string Effect);

/// <summary>Offline authored park fiction. The caller persists recent IDs to avoid repeat visits feeling identical.</summary>
public static class ParkEncounters
{
    private sealed record Entry(string Key, string Action, string Text, string Effect, int Species = 0, int Type = -1);
    private static readonly List<Entry> Entries = Build();
    public static int Count => Entries.Count;
    public static ParkEncounter Next(int species, IReadOnlyList<int> types, string name, string action,
        IReadOnlyCollection<string> recent, Random? random = null)
    {
        random ??= Random.Shared;
        var pool = Entries.Where(e => e.Action == action && (e.Species == species || e.Species == 0 && (e.Type < 0 || types.Contains(e.Type)))).ToArray();
        if (pool.Length == 0) pool = Entries.Where(e => e.Action == "talk" && e.Type < 0 && e.Species == 0).ToArray();
        var fresh = pool.Where(e => !recent.Contains(e.Key)).ToArray();
        if (fresh.Length == 0) fresh = pool.Where(e => e.Key != recent.LastOrDefault()).ToArray();
        if (fresh.Length == 0) fresh = pool;
        // Specific scenes are preferred but never defeat recent-history exclusions.
        var weighted = fresh.SelectMany(e => Enumerable.Repeat(e, e.Species > 0 ? 5 : e.Type >= 0 ? 3 : 1)).ToArray();
        var chosen = weighted[random.Next(weighted.Length)];
        return new(chosen.Key, chosen.Text.Replace("{name}", name), chosen.Effect);
    }
    private static List<Entry> Build()
    {
        var result = new List<Entry>();
        void Add(string action, string effect, string[] texts, int species = 0, int type = -1)
        {
            foreach (var text in texts) result.Add(new($"{species}:{type}:{action}:{result.Count}", action, text, effect, species, type));
        }
        Add("talk", "talk", [
            "{name} listens until you finish, then makes one small sound. Somehow, that feels like a very thoughtful answer.",
            "You try copying {name}'s greeting. It pauses, repeats it slowly, and gives you another chance.",
            "{name} notices a leaf on your shoulder before you do. It seems quite pleased with this discovery.",
            "You sit quietly with {name}. After a while, it chooses to move a little closer.",
            "{name} appears busy watching something in the distance. You follow its gaze. A very ordinary pebble is receiving extraordinary attention.",
            "You tell {name} about your day. It interrupts at exactly the dramatic part, as though it already knows the story.",
            "{name} gives you a long, solemn look. Then it sneezes. The moment loses some of its grandeur.",
            "You offer {name} a compliment. It looks away, but keeps sneaking little glances back at you.",
            "{name} is not feeling sociable just now. You give it room, and it settles down without having to ask twice.",
            "A passing cloud changes the light. Both you and {name} look up at the same time.",
            "{name} repeats a familiar little gesture. You remember it from your last visit and return the greeting.",
            "You ask {name} to choose a picnic spot. It considers the question far more carefully than you expected."
        ]);
        Add("play", "play", [
            "You roll the ball toward {name}. It sends it back gently, then waits for your next move.",
            "{name} watches the ball roll past. Apparently, the grass is more interesting today. You put the toy away.",
            "The ball takes an unexpected bounce. {name} stops, studies the ground, and tries to make it happen again.",
            "{name} invents a rule: the ball must go around that pebble. You accept the challenge.",
            "You fake a throw. {name} doesn't fall for it. Now it is watching your hand very closely.",
            "{name} would rather lead a game of follow-the-leader. You spend the next minute taking very unusual steps.",
            "The ball stops between you. Neither of you moves. This may have become a staring contest.",
            "{name} makes a tiny victory sound before the game has even started. Confidence is not a problem.",
            "You aim for a patch of grass and miss. {name} looks at the ball, then at you. Fair criticism.",
            "{name} prefers a slower game today. You roll the ball in short, gentle passes.",
            "A leaf lands on the ball. {name} decides this is an important new feature and refuses to disturb it.",
            "After a few rounds, {name} wanders off for a rest. You take the hint and collect the ball."
        ]);
        Add("snack", "snack", [
            "{name} inspects the berry from every angle before trying a cautious bite. The second bite is much less cautious.",
            "{name} takes one taste, then politely leaves the rest. You offer fresh water instead.",
            "You set down two berries. {name} chooses the smaller one and seems perfectly satisfied.",
            "{name} pushes a berry back toward you. Sharing, apparently, goes both ways.",
            "A little juice escapes. {name} pauses with the dignified expression of someone hoping nobody noticed.",
            "{name} saves the last bite for later. It checks on it twice before settling down.",
            "{name} is more interested in the picnic basket than the food. You let it investigate the empty corner.",
            "You wait until {name} comes closer on its own. It takes the treat at its own pace.",
            "{name} seems to prefer this berry sliced. You prepare a few smaller pieces and try again.",
            "{name} finishes its snack and looks toward the water. A drink is clearly next on the agenda.",
            "A berry rolls away. {name} watches it go, then calmly chooses another from the basket.",
            "{name} declines the snack but stays beside you. The company seems to be enough."
        ]);
        Add("relax", "relax", [
            "You find a quiet patch beside {name}. For a while, the park does all the talking.",
            "{name} changes resting spots three times before finding the perfect one. You decide not to question the process.",
            "A distant splash catches {name}'s attention. It listens, then returns to its daydream.",
            "You stop trying to start an activity. {name} visibly settles. Doing nothing together counts too.",
            "{name} looks almost asleep, but notices immediately when you shift your weight.",
            "The breeze brings a new smell. {name} pauses its rest to investigate, then comes back.",
            "You hum a few quiet notes. {name} answers once, then lets the tune fade into the afternoon.",
            "{name} prefers a little space. You sit nearby instead of crowding its favorite spot.",
            "You and {name} watch the same drifting cloud until it disappears beyond the trees.",
            "{name} stretches, settles, and lets out a contented sound. It has made a strong case for taking breaks.",
            "A leaf tumbles between you. Neither of you feels any need to chase it.",
            "{name} keeps one eye on the picnic basket while resting. Some responsibilities cannot be set aside."
        ]);
        // Each type contributes distinct scenes for every action, rather than swapping a type adjective.
        Add("play", "play", ["A small spark jumps from {name} to the ball. {name} looks horrified, then carefully pats it out.", "{name} demonstrates a perfect measured stance. You try it. Your balance does not impress anyone."], type: ParkType.Electric);
        Add("talk", "talk", ["{name} crackles softly when you mention the weather. It seems to have a very strong opinion about clouds."], type: ParkType.Electric);
        Add("snack", "snack", ["The berry tingles against {name}'s tongue. It looks at you, then at the berry, as if requesting an explanation."], type: ParkType.Electric);
        Add("play", "play", ["{name} ignores the ball and demonstrates a precise little martial-arts bow instead. Lesson one is apparently patience.", "You try a practice stance with {name}. It gently corrects your feet and nods when you improve."], type: ParkType.Fighting);
        Add("relax", "relax", ["{name} curls near the warm stones, perfectly content to let the heat do the work."], type: ParkType.Fire);
        Add("play", "play", ["{name} sends the ball across the pond with a single splash. It looks proud, and a little surprised."], type: ParkType.Water);
        Add("relax", "relax", ["{name} settles beneath the broad leaves. Even the flowers seem to lean closer."], type: ParkType.Grass);
        Add("talk", "talk", ["{name} answers with a tiny flame and immediately looks apologetic. You reassure it that the grass is fine."], type: ParkType.Fire);
        Add("snack", "snack", ["{name} warms the berry just enough to make it fragrant. It offers you the first bite."], type: ParkType.Fire);
        Add("play", "play", ["{name} races the ball to the waterfall and back, then pretends it was never trying."], type: ParkType.Flying);
        Add("relax", "relax", ["{name} finds a cool cave shadow and lets the echo of the park wash over it."], type: ParkType.Ghost);
        Add("talk", "talk", ["{name} studies your reflection in the lake and then looks at you, as if comparing notes."], type: ParkType.Flying);
        return result;
    }
}
