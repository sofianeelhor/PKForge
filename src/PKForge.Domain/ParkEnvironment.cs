namespace PKForge.Domain;

/// <summary>The four handcrafted areas of Poképark. Values are stable so a saved park can be restored.</summary>
public enum ParkEnvironmentId { Commons = 0, WaterfallLake = 1, EmberGrove = 2, SkySummit = 3, QuietRefuge = SkySummit }

public sealed record ParkEnvironment(ParkEnvironmentId Id, string Name, string Subtitle, string Landmark,
    IReadOnlySet<int> FavouredTypes, bool FavoursShy, string[] AmbientLines)
{
    public static readonly ParkEnvironment Commons = new(ParkEnvironmentId.Commons, "Sunlit Commons", "A little of everything", "Meadow plaza", new HashSet<int>(), false,
        ["Wind combs the tall grass.", "A distant bell marks snack time.", "The commons hums with friendly footsteps."]);
    public static readonly ParkEnvironment WaterfallLake = new(ParkEnvironmentId.WaterfallLake, "Cascade Lake", "Waterfall play zone", "Moonfall cascade", new HashSet<int> { ParkType.Water }, false,
        ["The waterfall throws rainbows across the lake.", "Pebbles skip over the shallows.", "Mist cools every warm nose nearby."]);
    public static readonly ParkEnvironment EmberGrove = new(ParkEnvironmentId.EmberGrove, "Ember Grove", "Warm stones and brave games", "Cinder ring", new HashSet<int> { ParkType.Fire, ParkType.Ground, ParkType.Rock }, false,
        ["Warm stones tick softly as they cool.", "A safe ember glows beneath the trees.", "The grove smells of toasted berries."]);
    public static readonly ParkEnvironment SkySummit = new(ParkEnvironmentId.SkySummit, "Sky Summit", "Breezes above the clouds", "Snowcap overlook", new HashSet<int> { ParkType.Flying, ParkType.Ice, ParkType.Rock }, true,
        ["Cloud shadows drift below the plateau.", "A mountain breeze whistles between the rocks.", "Snow sparkles on the distant peaks."]);
    public static ParkEnvironment QuietRefuge => SkySummit;

    public static IReadOnlyList<ParkEnvironment> All { get; } = [Commons, WaterfallLake, EmberGrove, SkySummit];
    public static ParkEnvironment Get(ParkEnvironmentId id) => All[(int)id];
    public static ParkEnvironment Step(ParkEnvironmentId current, int delta)
        => All[(int)((((int)current + delta) % All.Count) + All.Count) % All.Count];

    /// 0..2 mood bonus: matching habitats feel immediately more welcoming.
    public int MoodBonus(IReadOnlyList<int> types, bool shy = false)
        => (FavoursShy && shy) ? 2 : (types.Any(FavouredTypes.Contains) ? 1 : 0);

    public string Ambient(Random? random = null) => AmbientLines[(random ?? Random.Shared).Next(AmbientLines.Length)];
}

/// <summary>Small, deterministic environment-specific hooks used by the dialogue picker.</summary>
public static class ParkEnvironmentScenes
{
    public static IReadOnlyList<string> For(ParkEnvironmentId id, string action, IReadOnlyList<int> types, bool shy = false)
    {
        var key = action.ToLowerInvariant();
        var scenes = new List<string>();
        if (id == ParkEnvironmentId.WaterfallLake && (key == "play" || key == "talk"))
        {
            if (types.Contains(ParkType.Water))
                scenes.AddRange(["{name} dives into the shallows, then surfaces with a splash that seems suspiciously aimed at you.", "{name} traces lazy circles in the water. The lake has become its favourite playground.", "{name} watches the waterfall foam, then challenges a drifting leaf to a race."]);
            else
                scenes.AddRange(["{name} watches ripples from the grassy bank and carefully tests the water with one foot.", "A splash reaches the shore. {name} steps back, then looks at you as if this was your idea."]);
        }
        if (id == ParkEnvironmentId.EmberGrove)
        {
            if (types.Contains(ParkType.Fire))
                scenes.AddRange(["{name} settles on a warm basalt shelf with a deeply satisfied sigh.", "{name} makes a tiny spark dance above a stone, then proudly waits for applause.", "{name} brings you a toasted berry. It blows on it very carefully before sharing."]);
            else
                scenes.AddRange(["{name} finds a pleasantly warm patch of ground, a comfortable distance from the lava.", "{name} watches a glowing bubble burst and takes one very sensible step back."]);
        }
        if (id == ParkEnvironmentId.SkySummit)
        {
            if (types.Contains(ParkType.Flying))
                scenes.AddRange(["{name} catches an updraft, circles the plateau, and lands beside you with a flourish.", "{name} tilts into the mountain breeze as though listening to a song only it can hear."]);
            else if (types.Contains(ParkType.Ice))
                scenes.AddRange(["{name} nestles into the cool mountain air. At last, a place with the right temperature.", "{name} carefully shapes a little snowball and looks far too innocent."]);
            else
                scenes.Add("{name} stays on the broad plateau and watches the clouds drifting far below.");
            if (shy) scenes.Add("{name} finds a sheltered nook behind a rock. Sitting quietly nearby seems to be exactly the invitation it wanted.");
        }
        if (id == ParkEnvironmentId.Commons && key == "talk")
            scenes.AddRange(["A comic little Pokémon nearby imitates {name}'s pose. {name} pretends to be offended, then bows.", "{name} stops to inspect a flower, then makes room beside it for you.", "{name} stretches out in the grass. There is clearly no hurry today."]);
        return scenes;
    }
}
