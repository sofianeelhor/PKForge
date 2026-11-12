namespace PKForge.Domain;

/// <summary>A resident as the park's life sees it.</summary>
/// <param name="Nature">Its nature when the save or the Bank tells it; it nudges how docile it is.</param>
/// <param name="BaseSpeed">The species' base Speed stat, for races.</param>
/// <param name="Shiny">A shiny: it glints now and then, as shinies do in the games.</param>
/// <param name="Form">Its form (a regional form, Unown's letter…), so its kin can tell it apart.</param>
public sealed record ParkLifeResident(string Id, string Name, int Species, IReadOnlyList<int> Types, ParkPersonality Personality, int? Nature = null,
    int BaseSpeed = 60, bool Shiny = false, int Form = 0)
{
    public ParkTemperament Temperament => ParkLore.TemperamentOf(Species, Personality);
    public ParkQuirk Quirks => ParkLore.QuirksOf(Species, Types);
    public bool Has(ParkQuirk quirk) => (Quirks & quirk) != 0;
    public bool Swims => Types.Contains(ParkType.Water);
    /// <summary>Lives in the water only (see <see cref="ParkSpecies.WaterBound"/>).</summary>
    public bool WaterBound => ParkSpecies.WaterBound.Contains(Species);
    /// <summary>A giant of the sea (see <see cref="ParkSpecies.Leviathans"/>).</summary>
    public bool Leviathan => ParkSpecies.Leviathans.Contains(Species);
    public ParkTraversal Traversal => WaterBound ? ParkTraversal.Water
        : Swims ? ParkTraversal.Land | ParkTraversal.Water : ParkTraversal.Land;
}

/// <summary>A little icon over a Pokémon's head.</summary>
public enum ParkEmote { None, Heart, Note, Sleep, Exclaim, Question, Sweat, Anger, Sparkle, Ellipsis }

/// <summary>Something a Pokémon holds or plays with while it acts.</summary>
public enum ParkProp { None, Berry, Ball, Notes, Egg, Boulder }

/// <summary>The ball the player threw: flying from one point to another, then lying there
/// until its Pokémon picks it up and carries it back.</summary>
public sealed class ParkBall
{
    public const int FlightMilliseconds = 700;
    public ParkPoint From { get; internal set; }
    public ParkPoint To { get; internal set; }
    public long ThrownAt { get; internal set; }
    /// <summary>The Pokémon it was thrown for.</summary>
    public string OwnerId { get; internal set; } = "";
    /// <summary>The Pokémon carrying it, once picked up.</summary>
    public string? HolderId { get; internal set; }
}

/// <summary>A moment two residents shared in front of the player, for the journal.</summary>
/// <param name="Text">A line for the journal when the moment is special (a lullaby, a prank…).</param>
public sealed record ParkMoment(ParkSocialEventKind Kind, string FirstId, string SecondId, string? Text = null);

/// <summary>How a resident took what the player did: whether it went along, a line of its own
/// for the dialogue (null to let the usual dialogue speak), the portrait emotion, and any accident.</summary>
/// <param name="Taste">For a berry: +1 a favorite, −1 disliked, 0 neither.</param>
/// <param name="Gift">Something it gave the player, for the treasures.</param>
public sealed record ParkReply(bool Accepted, string? Line, string Emotion, ParkAccident Accident = ParkAccident.None, int Taste = 0,
    ParkTreasure? Gift = null)
{
    public static readonly ParkReply None = new(false, null, "Normal");
}

public enum ParkEffectKind { Zap, Burn, Frost, Poison, Ghost, Splash, Pollen, Sting, Bite, Lick, Psychic, Hearts, Coin, Scare, Egg, Rustle, Dirt, Sparkle, Miss, Gust, Shine,
    Teleport, Smoke, Wave, Barrier, Nightmare, Aura, Crackle, Spray }

/// <summary>A short burst drawn over the park (sparks, a flame puff, hearts…), at a point in art pixels.</summary>
public sealed record ParkEffect(ParkEffectKind Kind, float X, float Y, long At);

/// <summary>What the player asks of a resident from its card, or of everyone (the picnic).</summary>
public enum ParkCommand { Talk, Snack, Play, Relax }

/// <summary>One Pokémon living in the park. The scene reads it every frame.</summary>
public sealed class ParkActor
{
    internal ParkActor(ParkLifeResident resident, ParkNavigator navigator, Random random, float x, float y)
    {
        Resident = resident; Navigator = navigator; Random = random; X = x; Y = y;
        Care = new ParkCareState(resident.Id);
        // Its own pace: the quick ones trot, the slow ones amble.
        Speed = (6 + Math.Clamp(resident.BaseSpeed, 20, 160) * .07f) * (.92f + (float)random.NextDouble() * .16f) * (resident.Has(ParkQuirk.Slow) ? .5f : 1);
    }

    public ParkLifeResident Resident { get; internal set; }
    public float X { get; internal set; }
    public float Y { get; internal set; }
    /// <summary>PMD order: 0 south, then SE, E, NE, N, NW, W, SW.</summary>
    public int Facing { get; internal set; }
    public ParkPose Pose { get; private set; } = ParkPose.Idle;
    /// <summary>When the pose started, so its animation plays from its first frame.</summary>
    public long PoseSince { get; private set; }
    public ParkEmote Emote { get; private set; }
    public long EmoteSince { get; private set; }
    public ParkProp Prop { get; internal set; }
    /// <summary>The flavor of the berry it holds (0 Spicy … 4 Bitter), or −1 for the park's own berries.</summary>
    public int BerryFlavor { get; internal set; } = -1;
    public string Activity { get; internal set; } = "Looking around";
    /// <summary>What it just did that is worth a word on screen (a reaction, a scene, a meeting, a
    /// prank, standing up for someone…), never its routine; with when it started (park time).</summary>
    public string? Caption { get; internal set; }
    public long CaptionSince { get; internal set; }
    // The plan under way came from its own routine (a stroll, a nap, a habit): nothing to explain.
    internal bool RoutinePlan { get; set; }
    /// <summary>A need running out shows first; otherwise the mood of what it is doing.</summary>
    public string Mood => ParkLife.IsUnwell(Care) ? "Unwell" : Care.Mood() is ("Sleepy" or "Hungry" or "Lonely" or "Bored") and var need ? need : ActivityMood;
    internal string ActivityMood { get; set; } = "Content";
    /// <summary>In high spirits (joyful, delighted, playful…): its flame burns brighter.</summary>
    public bool ActivityMoodIsBright => ActivityMood is "Joyful" or "Delighted" or "Happy" or "Playful" or "Excited" or "Proud";
    /// <summary>Its needs and its bond with the player.</summary>
    public ParkCareState Care { get; internal set; }
    /// <summary>Asleep inside the cabin: not drawn until it comes out.</summary>
    public bool Indoors { get; internal set; }
    public bool IsWalking => Pose == ParkPose.Walk;
    /// <summary>A species it is copying (Ditto), until <see cref="DisguiseUntil"/>.</summary>
    public int? Disguise { get; internal set; }
    /// <summary>Ditto's copies are tinted its purple; Zorua's illusions are not.</summary>
    public bool DisguiseTinted { get; internal set; }
    /// <summary>Until when it will not run from what scares it again.</summary>
    internal long CalmUntil { get; set; }
    public long DisguiseUntil { get; internal set; }
    /// <summary>The partner of a shared moment (a greeting, a play fight), if any.</summary>
    public string? PartnerId { get; internal set; }
    public bool IsAsleep => Pose is ParkPose.Sleep or ParkPose.Lie || Indoors;
    public bool Busy => PartnerId is not null || Troupe is not null || RidingOn is not null || InGame || Settled || InFlight || Submerged || ParkLife.IsKeeper(Resident.Id) ||
        Activity == ParkLife.Evolving;
    /// <summary>The group it is acting a scene with (a formation, a picnic, a sparring match…).</summary>
    internal ParkLife.Troupe? Troupe { get; set; }
    /// <summary>Deep under the water (a giant of the sea): only its great shadow shows.</summary>
    public bool Submerged { get; internal set; }
    /// <summary>In the air: drawn this high above its shadow, over everything.</summary>
    public float Altitude { get; internal set; }
    /// <summary>Flying: from take-off to touch-down. Its X and Y are the shadow's, on any ground.</summary>
    public bool InFlight { get; internal set; }
    internal ParkLife.Flight? Flight { get; set; }

    /// <summary>Blissey, on its way to the egg it found: the two parents.</summary>
    internal (string First, string Second)? EggParents { get; set; }

    /// <summary>Settled by the campfire: the others leave it be until it gets up.</summary>
    internal bool Settled { get; set; }
    /// <summary>Playing a game with the player (hide and seek, a race…): nobody else bothers it.</summary>
    public bool InGame { get; internal set; }
    /// <summary>Hidden for hide and seek: not drawn, not found by tapping it.</summary>
    public bool Hidden { get; internal set; }
    /// <summary>Running faster until then (cheered on in a race).</summary>
    internal long BoostUntil { get; set; }
    /// <summary>Turns through its own lines so the same one does not come back twice in a row.</summary>
    internal int LineTurn { get; set; }
    /// <summary>Its recent lines, so it does not repeat itself.</summary>
    internal Queue<string> RecentLines { get; } = new();
    /// <summary>Holding a grudge against the player until then (woken roughly, a disliked berry…).</summary>
    internal long GrudgeUntil { get; set; }
    /// <summary>A troublemaker's next trick, not before then.</summary>
    internal long NextMischief { get; set; }
    /// <summary>Asleep right across a narrow way: nobody on foot gets past.</summary>
    internal bool Blocking { get; set; }
    internal bool QueueTold { get; set; }
    internal bool SongSent { get; set; }
    internal string? PushedBy { get; set; }
    /// <summary>Stuck behind a sleeper across the way since then (0: not stuck).</summary>
    internal long WaitingSince { get; set; }
    /// <summary>Riding on another resident (Lapras, Lotad's leaf) or hiding in its shadow (Gengar).</summary>
    public bool Riding => RidingOn is not null;
    internal string? RidingOn { get; set; }
    internal (float Dx, float Dy) RideOffset { get; set; }
    /// <summary>Hiding in another's shadow (Gengar): not drawn, not tapped, until it pops out.</summary>
    public bool InShadow { get; internal set; }
    /// <summary>The one riding on it.</summary>
    internal string? Carrying { get; set; }
    /// <summary>Jigglypuff drew on its face while it slept: it shows until then (park clock).</summary>
    public long DoodledUntil { get; internal set; }
    // Fading out of sight or back: from, to, since when, over how long.
    internal float FadeFrom { get; set; } = 1;
    internal float FadeTo { get; set; } = 1;
    internal long FadeSince { get; set; }
    internal int FadeMs { get; set; }

    /// <summary>How visible it is now (0 gone, 1 as usual): a Ghost fading out, a teleport, a glitch.</summary>
    public float OpacityAt(long now) => FadeMs <= 0 || now - FadeSince >= FadeMs ? FadeTo
        : FadeFrom + (FadeTo - FadeFrom) * (now - FadeSince) / FadeMs;

    /// <summary>Blended into its surroundings (Kecleon): barely there.</summary>
    public bool Camouflaged { get; internal set; }

    /// <summary>Lucario's next look round at whoever comes near, not before then.</summary>
    internal long NextSense { get; set; }

    /// <summary>When it found a berry bush stripped bare (a berry from the player then means a lot).</summary>
    internal long FoundBareAt { get; set; } = long.MinValue / 2;
    /// <summary>The troublemaker it is going to get its own back on.</summary>
    internal string? Revenge { get; set; }
    /// <summary>Where a Psyduck in trouble in the water can be pulled back to.</summary>
    internal ParkPoint? RescueShore { get; set; }
    /// <summary>Asleep on top of that resident (Snorlax's belly).</summary>
    internal string? PerchedOn { get; set; }
    /// <summary>Its last trick and on whom, so the next one is something else.</summary>
    internal string? LastTrick { get; set; }
    internal string? LastVictim { get; set; }
    /// <summary>A wild visitor heads back out after this.</summary>
    internal long LeaveAt { get; set; }
    /// <summary>The gate a wild visitor came in by, and leaves by.</summary>
    internal ParkPoint? Gateway { get; set; }
    /// <summary>The place a wild visitor came into (its kind fits that place).</summary>
    internal string? Place { get; set; }

    internal ParkNavigator Navigator { get; }
    internal Random Random { get; }
    internal float Speed { get; }
    internal Queue<ParkLife.Step> Plan { get; } = new();
    internal ParkLife.Step? Current { get; set; }
    internal long StepUntil { get; set; }
    internal IReadOnlyList<ParkPoint>? Route { get; set; }
    internal int RouteLeg { get; set; }
    internal ParkLife.Activity LastActivity { get; set; }
    internal ParkPoint? Destination { get; set; }

    internal void SetPose(ParkPose pose, long now)
    {
        if (Pose == pose) return;
        Pose = pose; PoseSince = now;
    }

    internal void Say(ParkEmote emote, long now)
    {
        Emote = emote; EmoteSince = now;
    }
}

/// <summary>The park's life: each Pokémon picks what to do next (a stroll, a nap, berries, a
/// visit to the statue, a swim, saying hello or play-fighting with a friend) and acts it out
/// step by step. The scene advances it with the clock and draws what it says.</summary>
public sealed partial class ParkLife
{
    internal enum Activity { None, Stroll, Nap, Snack, Sit, Statue, Flag, Door, Gate, Greet, PlayFight, Swim, Dance, Train, Bedtime, Quirk, Campfire, Fireside, Fly, Dive, Scene, Habit, Haunt, Meet }

    internal abstract record Step;
    /// <param name="Moving">How it looks on the way (rolling, for one that rolls); walking otherwise.</param>
    internal sealed record GoTo(ParkPoint Target, float SpeedFactor = 1, ParkPose? Moving = null) : Step;
    /// <summary>A flight to a point where it can stand: up, across whatever lies below, and down.</summary>
    internal sealed record Fly(ParkPoint Target, bool Away = false) : Step;
    internal sealed record Flight(ParkPoint From, ParkPoint Bend, ParkPoint To, long Start, long Duration, bool Away = false);
    internal sealed record Act(ParkPose Pose, int Milliseconds, int? Facing = null, ParkEmote Emote = ParkEmote.None, ParkProp Prop = ParkProp.None) : Step;
    internal sealed record Face(string ActorId) : Step;
    internal sealed record Label(string Activity, string Mood) : Step;
    internal sealed record Engage(string ActorId, Step[] Steps) : Step;
    internal sealed record Release : Step;
    internal sealed record Enter : Step;
    /// <summary>Down under the water, or back up (a giant of the sea).</summary>
    internal sealed record Submerge(bool Under) : Step;
    internal sealed record Exit : Step;
    internal sealed record Gain(ParkNeed Need, float Amount, float Bond = 0) : Step;
    internal sealed record Share(ParkSocialEventKind Kind, string OtherId) : Step;
    internal sealed record Throw(ParkPoint To) : Step;
    internal sealed record Pickup : Step;
    internal sealed record Drop : Step;

    public const int TickMilliseconds = 50;
    /// <summary>How many residents fit in the cabin for the night.</summary>
    public const int CabinBeds = 4;
    private readonly List<ParkMoment> _moments = new();

    /// <summary>The ball in play, if the player threw one.</summary>
    public ParkBall? Ball { get; private set; }

    /// <summary>The sky, set from the real clock by the page.</summary>
    public ParkWeather Weather { get; set; }

    /// <summary>The part of the day, set from the real clock by the page.</summary>
    public ParkDayPhase Phase { get; set; } = ParkDayPhase.Day;
    /// <summary>Today's date, for the daily cap on the bond.</summary>
    public DateOnly Today { get; set; }
    /// <summary>How two residents get on (friendship −100…100, rivalry 0…100), from the saved relationships.</summary>
    public Func<string, string, (int Friendship, int Rivalry)>? Relation { get; set; }

    /// <summary>The moments shared since the last call, for the journal and the relationships.</summary>
    public IReadOnlyList<ParkMoment> TakeMoments() { lock (_gate) return TakeMomentsCore(); }

    private IReadOnlyList<ParkMoment> TakeMomentsCore()
    {
        var moments = _moments.ToArray();
        _moments.Clear();
        return moments;
    }

    /// <summary>Every resident's needs and bond, stamped with the time, to save.</summary>
    public IReadOnlyList<ParkCareState> Snapshot(DateTimeOffset now) { lock (_gate) return SnapshotCore(now); }

    private IReadOnlyList<ParkCareState> SnapshotCore(DateTimeOffset now) => _actors.Values.Where(a => !IsVisitor(a.Resident.Id)).Select(a => a.Care with { Id = a.Resident.Id, LastSeen = now }).ToArray();

    /// <summary>Puts saved needs back, aged by the time the park was closed.</summary>
    public void Restore(IEnumerable<ParkCareState> saved, DateTimeOffset now) { lock (_gate) RestoreCore(saved, now); }

    private void RestoreCore(IEnumerable<ParkCareState> saved, DateTimeOffset now)
    {
        foreach (var care in saved)
            if (_actors.TryGetValue(care.Id, out var actor))
                actor.Care = care.LastSeen == default || care.LastSeen > now ? care : care.Away(now - care.LastSeen, _actors.Count - 1);
    }
    private const float PersonalSpace = 16;
    private const long ShineEvery = 18_000;
    private readonly ParkMap _map;
    private readonly Random _random;
    private readonly Dictionary<string, ParkActor> _actors = new();
    private long? _time;

    public ParkLife(ParkMap map, IEnumerable<ParkLifeResident> residents, int seed = 1)
    {
        _map = map;
        _random = new Random(seed);
        foreach (var resident in residents) Add(resident);
    }

    /// <summary>One lock for the whole park: the page calls in from the UI thread while the park is
    /// drawn (and lived) on the GPU's render thread.</summary>
    private readonly object _gate = new();

    /// <summary>The residents and visitors right now (a copy, safe to go through on any thread).</summary>
    public IReadOnlyCollection<ParkActor> Actors { get { lock (_gate) return _actors.Values.ToArray(); } }
    public ParkActor? this[string id] { get { lock (_gate) return _actors.GetValueOrDefault(id); } }
    public long Now => _time ?? 0;

    /// <summary>Keeps the actors whose ids are still listed, adds the new ones.</summary>
    public void SetResidents(IEnumerable<ParkLifeResident> residents) { lock (_gate) SetResidentsCore(residents); }

    private void SetResidentsCore(IEnumerable<ParkLifeResident> residents)
    {
        var list = residents.ToList();
        var keep = list.Select(r => r.Id).ToHashSet();
        foreach (var gone in _actors.Keys.Where(id => !keep.Contains(id) && !IsVisitor(id)).ToArray()) _actors.Remove(gone);
        foreach (var actor in _actors.Values.Where(a => a.PartnerId is { } partner && !_actors.ContainsKey(partner)))
        {
            actor.PartnerId = null; ClearPlan(actor);
        }
        foreach (var resident in list.Where(r => !_actors.ContainsKey(r.Id))) Add(resident);
        // A game whose players left is over.
        if (Game is { } game && game.Players.Any(id => !_actors.ContainsKey(id))) EndGame();
        // A ball thrown for (or carried by) someone who left goes with them.
        if (Ball is { } ball && (!_actors.ContainsKey(ball.OwnerId) || ball.HolderId is { } holder && !_actors.ContainsKey(holder))) Ball = null;
    }

    private void Add(ParkLifeResident resident)
    {
        var navigator = ParkNavigator.For(_map, resident.Traversal);
        // A lake Pokémon has nowhere to be in a dry area: it stays where it is (its card says so).
        if (navigator.OpenCells.Count == 0) return;
        var random = new Random(_random.Next() ^ Hash(resident.Id));
        var start = FreePoint(navigator, random, null, place: HomePlace(resident)) ?? navigator.Center(navigator.OpenCells[0]);
        var actor = new ParkActor(resident, navigator, random, start.X, start.Y) { Facing = random.Next(8) };
        // Everyone starts mid-afternoon: a short pause, then their own plans.
        actor.Plan.Enqueue(new Act(ParkPose.Idle, random.Next(500, 4000)));
        _actors[resident.Id] = actor;
    }

    /// <summary>Runs the park up to a moment (ms on the caller's clock), in small steps.</summary>
    public void Advance(long now) { lock (_gate) AdvanceCore(now); }

    private void AdvanceCore(long now)
    {
        _time ??= now;
        // A long pause (the page was closed) does not replay every missed step.
        if (now - _time > 5000) _time = now - 5000;
        while (_time < now)
        {
            var step = Math.Min(TickMilliseconds, now - _time.Value);
            _time += step;
            foreach (var actor in _actors.Values) Update(actor, _time.Value, step);
            UpdateGame(_time.Value);
            UpdateVisitors(_time.Value);
            UpdateDefenses(_time.Value);
            UpdateEvening(_time.Value);
            UpdateDaycare(_time.Value);
            UpdateRoadblocks(_time.Value);
            PruneHighlights(_time.Value);
            StormDam();
            UpdateRhythms(_time.Value);
            UpdatePowers(_time.Value);
            UpdateEvolution(_time.Value);
        }
        _effects.RemoveAll(effect => now - effect.At > 3000);
    }

    /// <summary>Drops whatever it was doing and every state that came with it (a scene, lying across
    /// a way, sleeping on someone, riding or carrying someone), but not a partner's link: every place
    /// that empties a plan goes through here, so nothing is left hanging.</summary>
    // The walk to something says less than the thing itself: "Going to say hello to Pikachu" is
    // followed by "Saying hello to Pikachu", and only that one gets a caption.
    private static readonly string[] OnTheWayStarts = ["Going to ", "Going over to ", "Off to ", "Heading ", "Looking for ", "Wandering", "Walking up to ", "Keeping a respectful"];

    private static bool OnTheWay(string label) => OnTheWayStarts.Any(label.StartsWith);

    // The everyday things, which need no caption; meetings, scenes, quirks, greetings and quarrels do.
    private static bool Routine(Activity activity) => activity is not (Activity.Greet or Activity.PlayFight or Activity.Quirk or Activity.Scene or Activity.Meet);

    private void ClearPlan(ParkActor actor)
    {
        // Whatever comes next was put there by something happening: it is worth a caption.
        actor.RoutinePlan = false;
        // An evolution cut short does not leave it busy forever.
        if (actor.Activity == Evolving) actor.Activity = "Looking around";
        actor.Plan.Clear(); actor.Current = null; actor.Prop = ParkProp.None; actor.Destination = null; actor.Route = null;
        actor.Settled = false;
        actor.Troupe = null;
        if (actor.Blocking)
        {
            actor.Blocking = false;
            // "Asleep across the bridge" is not true any more.
            _highlights.RemoveAll(h => h.ActorId == actor.Resident.Id);
        }
        actor.WaitingSince = 0; actor.PerchedOn = null;
        if (actor.Carrying is { } rider && _actors.TryGetValue(rider, out var passenger) && passenger.RidingOn == actor.Resident.Id && !passenger.InShadow)
            Interrupt(passenger);
        actor.Carrying = null;
        if (actor.RidingOn is { } mountId)
        {
            if (_actors.TryGetValue(mountId, out var mount) && mount.Carrying == actor.Resident.Id) mount.Carrying = null;
            Dismount(actor);
        }
        if (!actor.InFlight) actor.Altitude = 0;
        // Whatever it had made of itself (invisible, blended in) it is itself again.
        if (actor.FadeTo < 1) { actor.FadeFrom = actor.FadeTo = 1; actor.FadeMs = 0; }
        actor.Camouflaged = false;
    }

    private void Interrupt(ParkActor actor)
    {
        if (actor.PartnerId is { } partnerId && _actors.TryGetValue(partnerId, out var partner) && partner.PartnerId == actor.Resident.Id)
        {
            partner.PartnerId = null; ClearPlan(partner);
        }
        actor.PartnerId = null;
        ClearPlan(actor);
        actor.BerryFlavor = -1;
        if (Ball?.OwnerId == actor.Resident.Id) Ball = null;
        // Caught in the air: it comes down first, on the nearest ground it can stand on. Already
        // down, it skips the end of its landing hop.
        if (actor.InFlight) actor.Plan.Enqueue(new Fly(Landing(actor, new(actor.X, actor.Y))));
        else actor.Flight = null;
        // Called from the deep: it comes up first.
        if (actor.Submerged) { actor.Submerged = false; _effects.Add(new(ParkEffectKind.Splash, actor.X, actor.Y - 6, Now)); }
    }

    private void PruneHighlights(long now)
    {
        if (now % 1000 < TickMilliseconds) _highlights.RemoveAll(h => h.Until <= now || !_actors.ContainsKey(h.ActorId));
    }

    private void Update(ParkActor actor, long now, long elapsed)
    {
        actor.Care = actor.Care.Live(actor.Indoors ? ParkPose.Sleep : actor.Pose, InWater(actor), elapsed / 1000f);
        // Snorlax and Munchlax are always a little hungrier.
        if (actor.Resident.Has(ParkQuirk.Glutton)) actor.Care = actor.Care.Add(ParkNeed.Hunger, -ParkCare.HungerPerHour * elapsed / 3_600_000f);
        if (actor.Disguise is not null && now > actor.DisguiseUntil) actor.Disguise = null;
        // A shiny glints now and then, the stars of the games around it.
        if (actor.Resident.Shiny && (now + (Hash(actor.Resident.Id) & 0x3fff)) % ShineEvery < TickMilliseconds && !actor.IsAsleep &&
            !actor.Submerged && !actor.Hidden && !actor.InFlight)
            _effects.Add(new(ParkEffectKind.Shine, actor.X, actor.Y - 12, now));
        // An unwell resident breaks into a sweat now and then.
        if (IsUnwell(actor.Care) && !actor.IsAsleep && now % 6000 < TickMilliseconds) actor.Say(ParkEmote.Sweat, now);
        Ride(actor);
        FleeIfScared(actor, now);
        GiveRoom(actor, now);
        // Instant steps chain within one tick; a bounded loop keeps a bad plan from hanging.
        for (var guard = 0; guard < 16; guard++)
        {
            if (actor.Current is null)
            {
                if (actor.Plan.Count == 0) { actor.Settled = false; Choose(actor, now); }
                actor.Current = actor.Plan.Dequeue();
                Start(actor, actor.Current, now);
            }
            if (!Run(actor, actor.Current, now, elapsed)) return;
            actor.Current = null;
            elapsed = 0;
        }
    }

    private void Start(ParkActor actor, Step step, long now)
    {
        switch (step)
        {
            case GoTo go when CanFly(actor.Resident) && !actor.InGame && Distance(actor.X, actor.Y, go.Target.X, go.Target.Y) > Hop:
                TakeOff(actor, go.Target, now);
                break;
            case GoTo go:
                actor.Flight = null;
                var routeStarted = SlowChoice is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                actor.Route = actor.Navigator.Route(new(actor.X, actor.Y), go.Target);
                if (SlowChoice is { } trace && System.Diagnostics.Stopwatch.GetElapsedTime(routeStarted).TotalMilliseconds is > 3 and var routeMs)
                    trace($"{routeMs:0.0} ms: route for {actor.Resident.Name} from {actor.X:0},{actor.Y:0} to {go.Target.X:0},{go.Target.Y:0} ({actor.Route?.Count ?? 0} legs)");
                actor.RouteLeg = 0;
                actor.Destination = actor.Route is null ? null : go.Target;
                break;
            case Fly fly:
                TakeOff(actor, fly.Target, now, fly.Away);
                break;
            case Leap leap:
                actor.StepUntil = now + leap.Milliseconds;
                break;
            case Act act:
                actor.StepUntil = now + act.Milliseconds;
                if (act.Facing is { } facing) actor.Facing = facing;
                actor.SetPose(act.Pose, now);
                actor.Prop = act.Prop;
                if (act.Emote != ParkEmote.None) actor.Say(act.Emote, now);
                break;
        }
    }

    /// <returns>True when the step is over.</returns>
    private bool Run(ParkActor actor, Step step, long now, long elapsed)
    {
        switch (step)
        {
            case GoTo when actor.Flight is not null:
                return Glide(actor, now);
            case GoTo go:
                if (actor.Route is null || actor.RouteLeg >= actor.Route.Count) return Arrive(actor, now);
                // Stuck behind Snorlax asleep across the way.
                if (HeldUp(actor, go, now)) return false;
                actor.SetPose(go.Moving ?? ParkPose.Walk, now);
                actor.Prop = ParkProp.None;
                var budget = actor.Speed * go.SpeedFactor * (now < actor.BoostUntil ? 1.35f : 1) * (IsUnwell(actor.Care) ? .55f : 1) * elapsed / 1000f;
                while (budget > 0 && actor.RouteLeg < actor.Route.Count)
                {
                    var target = actor.Route[actor.RouteLeg];
                    float dx = target.X - actor.X, dy = target.Y - actor.Y;
                    var distance = MathF.Sqrt(dx * dx + dy * dy);
                    if (distance > .01f) actor.Facing = ParkMotion.Facing(dx, dy);
                    if (distance <= budget) { actor.X = target.X; actor.Y = target.Y; budget -= distance; actor.RouteLeg++; }
                    else { actor.X += dx / distance * budget; actor.Y += dy / distance * budget; budget = 0; }
                }
                return actor.RouteLeg >= actor.Route.Count && Arrive(actor, now);
            case Fly:
                return Glide(actor, now);
            case Muster muster:
                return RunMuster(actor, muster, now);
            case Leap leap:
                return RunLeap(actor, leap, now);
            case Act:
                if (now < actor.StepUntil) return false;
                actor.Prop = ParkProp.None;
                return true;
            case Face face:
                if (_actors.TryGetValue(face.ActorId, out var other))
                    actor.Facing = ParkMotion.Facing(other.X - actor.X, other.Y - actor.Y);
                return true;
            case Label label:
                if (!actor.RoutinePlan && label.Activity != actor.Activity && !OnTheWay(label.Activity)) { actor.Caption = label.Activity; actor.CaptionSince = now; }
                actor.Activity = label.Activity; actor.ActivityMood = label.Mood;
                return true;
            case Engage engage:
                if (_actors.TryGetValue(engage.ActorId, out var partner) && partner.PartnerId == actor.Resident.Id)
                {
                    ClearPlan(partner);
                    foreach (var s in engage.Steps) partner.Plan.Enqueue(s);
                    partner.Plan.Enqueue(new Release());
                }
                return true;
            case Release:
                actor.PartnerId = null;
                return true;
            case Enter:
                actor.Indoors = true;
                return true;
            case Exit:
                actor.Indoors = false;
                return true;
            case Submerge submerge:
                if (actor.Submerged != submerge.Under)
                    _effects.Add(new(actor.Resident.Leviathan && !submerge.Under ? ParkEffectKind.Wave : ParkEffectKind.Splash, actor.X, actor.Y - 6, now));
                // The red Gyarados of the Lake of Rage, rising out of the sea.
                if (actor.Submerged && !submerge.Under && actor.Resident is { Species: 130, Shiny: true } && actor.Random.Next(3) == 0)
                {
                    _moments.Add(new ParkMoment(ParkSocialEventKind.Rest, actor.Resident.Id, "",
                        $"{actor.Resident.Name}, the red Gyarados, rose out of the sea. Everyone on the shore went quiet."));
                    Highlight(actor, $"The red Gyarados rose out of the sea!", 3, 20_000, "The Red Gyarados", secret: true);
                }
                actor.Submerged = submerge.Under;
                return true;
            case Gain gain:
                actor.Care = actor.Care.Add(gain.Need, gain.Amount);
                if (gain.Bond > 0) actor.Care = actor.Care.AddBond(gain.Bond, Today);
                return true;
            case Share share:
                _moments.Add(new ParkMoment(share.Kind, actor.Resident.Id, share.OtherId));
                return true;
            case Throw toss:
                Ball = new ParkBall { From = new(actor.X, actor.Y), To = toss.To, ThrownAt = now, OwnerId = actor.Resident.Id };
                return true;
            case Pickup:
                if (Ball is not null) Ball.HolderId = actor.Resident.Id;
                return true;
            case Drop:
                Ball = null;
                return true;
            default:
                return RunQuirkStep(actor, step, now);
        }
    }

    private static bool Arrive(ParkActor actor, long now)
    {
        actor.Route = null; actor.Destination = null;
        actor.SetPose(ParkPose.Idle, now);
        return true;
    }

    /// <summary>Diagnostics: told of every choice that took more than 3 ms (who, what, how long).</summary>
    public static Action<string>? SlowChoice { get; set; }

    // What to do next: weighted by personality and type, never twice the same thing in a row
    // if anything else is possible.
    private void Choose(ParkActor actor, long now)
    {
        if (SlowChoice is not { } trace) { ChooseCore(actor, now); return; }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ChooseCore(actor, now);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms > 3) trace($"{ms:0.0} ms: {actor.Resident.Name} chose {actor.LastActivity} ({actor.Plan.Count} steps, first: {actor.Plan.FirstOrDefault()?.GetType().Name})");
    }

    private void ChooseCore(ParkActor actor, long now)
    {
        actor.RoutinePlan = true;
        var resident = actor.Resident;
        if (PlanDeparture(actor, now) || PlanKeeper(actor)) return;
        if (actor.Indoors)
        {
            // In the cabin: sleep on until morning, or wait out the rain, then come out and stretch.
            if (Phase == ParkDayPhase.Night || actor.Care.Energy < 60) { actor.Plan.Enqueue(new Act(ParkPose.Sleep, actor.Random.Next(60_000, 180_000))); return; }
            if (Wet && ShelterFromRain(actor.Resident)) { actor.Plan.Enqueue(new Act(ParkPose.Idle, 60_000)); return; }
            if (_map.Spots.FirstOrDefault(spot => spot.Kind == ParkSpotKind.Door) is { } door) { actor.X = door.X; actor.Y = door.Y + 6; }
            actor.Plan.Enqueue(new Exit());
            actor.Plan.Enqueue(new Label("Good morning!", "Refreshed"));
            actor.Plan.Enqueue(new Act(ParkPose.Wake, 1200, 0));
            actor.Plan.Enqueue(new Act(ParkPose.DeepBreath, 1600, 0, ParkEmote.Sparkle));
            return;
        }
        if (Phase != ParkDayPhase.Night && PlanGuest(actor)) return;
        // Getting its own back is a story, not routine: worth a caption.
        if (PlanRevenge(actor)) { actor.RoutinePlan = false; return; }
        var weights = new Dictionary<Activity, float>
        {
            // Its own habits come first; the walks and the park's sights are what it does between them.
            [Activity.Habit] = 28, [Activity.Haunt] = 10, [Activity.Meet] = 0,
            [Activity.Stroll] = 20, [Activity.Nap] = 8, [Activity.Snack] = 12, [Activity.Sit] = 8,
            [Activity.Statue] = 3, [Activity.Flag] = 1.5f, [Activity.Door] = 1.5f, [Activity.Gate] = 1.5f,
            [Activity.Greet] = 12, [Activity.PlayFight] = 6, [Activity.Dance] = 4, [Activity.Train] = 5,
            [Activity.Swim] = resident.Swims ? 28 : 0, [Activity.Bedtime] = 0, [Activity.Quirk] = 0,
            [Activity.Campfire] = 0, [Activity.Fireside] = 0, [Activity.Fly] = 0,
            [Activity.Dive] = resident.Leviathan ? 30 : 0, [Activity.Scene] = 0
        };
        void Scale(Activity activity, float factor) => weights[activity] *= factor;
        switch (resident.Personality)
        {
            case ParkPersonality.Playful: Scale(Activity.PlayFight, 2); Scale(Activity.Dance, 2); break;
            case ParkPersonality.Energetic: Scale(Activity.Stroll, 1.5f); Scale(Activity.Dance, 2); Scale(Activity.Train, 2); Scale(Activity.Nap, .5f); break;
            case ParkPersonality.Calm: Scale(Activity.Sit, 2); Scale(Activity.Nap, 1.5f); Scale(Activity.PlayFight, .5f); break;
            case ParkPersonality.Shy: Scale(Activity.Greet, .4f); Scale(Activity.PlayFight, .3f); Scale(Activity.Sit, 1.5f); break;
            case ParkPersonality.Curious: Scale(Activity.Stroll, 1.5f); Scale(Activity.Statue, 2); Scale(Activity.Flag, 2); Scale(Activity.Door, 2); Scale(Activity.Gate, 2); break;
            case ParkPersonality.Gentle or ParkPersonality.Helpful: Scale(Activity.Greet, 1.6f); Scale(Activity.PlayFight, .6f); break;
            case ParkPersonality.Bold: Scale(Activity.PlayFight, 1.6f); Scale(Activity.Train, 1.6f); break;
            case ParkPersonality.Proud: Scale(Activity.Statue, 2); Scale(Activity.Train, 1.6f); break;
        }
        if (resident.Types.Contains(ParkType.Grass) || resident.Types.Contains(ParkType.Bug)) { Scale(Activity.Snack, 1.5f); Scale(Activity.Nap, 1.2f); }
        // Fish and their kin stay in the lake: nothing ashore is for them.
        if (resident.WaterBound)
            foreach (var shore in new[] { Activity.Stroll, Activity.Nap, Activity.Snack, Activity.Sit, Activity.Statue, Activity.Flag, Activity.Door, Activity.Gate })
                weights[shore] = 0;
        if (resident.Types.Contains(ParkType.Electric) || resident.Types.Contains(ParkType.Fighting) || resident.Types.Contains(ParkType.Fire)) Scale(Activity.Train, 1.6f);
        if (actor.LastActivity != Activity.None) Scale(actor.LastActivity, .2f);

        // Needs: a hungry Pokémon goes for berries, a tired one naps, a bored one plays, a
        // lonely one looks for company.
        var care = actor.Care;
        Scale(Activity.Snack, ParkCare.Urge(care.Hunger));
        Scale(Activity.Nap, ParkCare.Urge(care.Energy));
        foreach (var fun in new[] { Activity.Dance, Activity.PlayFight, Activity.Swim, Activity.Train }) Scale(fun, ParkCare.Urge(care.Fun));
        foreach (var company in new[] { Activity.Greet, Activity.PlayFight }) Scale(company, ParkCare.Urge(care.Social));

        // The day: lazy mornings, quieter evenings, and at night everyone goes to bed.
        switch (Phase)
        {
            case ParkDayPhase.Morning: Scale(Activity.Stroll, 1.3f); Scale(Activity.Snack, 1.5f); break;
            case ParkDayPhase.Evening: Scale(Activity.Sit, 1.8f); Scale(Activity.Nap, 1.5f); Scale(Activity.Train, .5f); break;
            case ParkDayPhase.Night:
                foreach (var key in weights.Keys.ToArray()) weights[key] *= .1f;
                weights[Activity.Bedtime] = 400;
                break;
        }
        if (care.Energy < 12) weights[Activity.Bedtime] = Math.Max(weights[Activity.Bedtime], 200);
        WeatherWeights(actor, weights);
        EveningWeights(actor, weights);
        QuirkWeights(actor, weights);
        FlightWeights(actor, weights);
        SceneWeights(actor, weights);
        MeetWeights(actor, weights);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var total = weights.Values.Sum();
            if (total <= 0) break;
            var roll = (float)actor.Random.NextDouble() * total;
            var choice = Activity.None;
            foreach (var (candidate, weight) in weights)
            {
                if (weight <= 0) continue;
                choice = candidate;
                if ((roll -= weight) < 0) break;
            }
            actor.RoutinePlan = Routine(choice);
            if (Plan(actor, choice, now)) { actor.LastActivity = choice; return; }
            weights[choice] = 0;
        }
        actor.Plan.Enqueue(new Act(ParkPose.Idle, 2000));
    }

    /// <returns>False when the activity is not possible right now (no free spot, no partner).</returns>
    private bool Plan(ParkActor actor, Activity activity, long now)
    {
        var random = actor.Random;
        void Add(params Step[] steps) { foreach (var step in steps) actor.Plan.Enqueue(step); }
        int Pause(int min, int max) => random.Next(min, max);
        switch (activity)
        {
            case Activity.Fly:
                return PlanFlight(actor);
            case Activity.Scene:
                return PlanScene(actor, now);
            case Activity.Habit:
                return PlanHabit(actor);
            case Activity.Meet:
                return PlanMeet(actor, now);
            case Activity.Haunt:
                return PlanHaunt(actor);
            case Activity.Stroll:
            {
                // Homebodies stroll round their own place; roamers now and then set off across the whole park.
                var range = Range(actor.Resident);
                var area = range < 0 && random.Next(3) > 0 ? HomePlace(actor.Resident)
                    : range > 0 && random.Next(4) == 0 ? new ParkRegion("park", "", ParkEnvironmentId.Commons, 0, 0, _map.Width, _map.Height) : null;
                if (FreePoint(actor.Navigator, random, actor, land: !actor.Resident.Swims || random.Next(2) == 0, place: area) is not { } point) return false;
                Add(new Label(area is { Id: "park" } ? "Off exploring the park" : "Taking a gentle stroll", "Curious"), new GoTo(point));
                // Now and then a clumsy one trips over its own feet.
                var clumsy = actor.Resident.Personality is ParkPersonality.Energetic or ParkPersonality.Playful ? 8 : 16;
                if (random.Next(clumsy) == 0)
                    Add(new Label("Tripped over its own feet", "Embarrassed"), new Act(ParkPose.Trip, 1100, Emote: ParkEmote.Sweat), new Act(ParkPose.Idle, 900));
                var linger = Lingering(actor.Resident);
                var tic = TicOf(actor.Resident);
                Add(new Act(ParkPose.Idle, (int)(Pause(1500, 3500) * linger)), new Act(ParkPose.Idle, (int)(Pause(1000, 3000) * linger), random.Next(8)));
                // Its own little gesture, most times it stops.
                if (random.Next(3) > 0) Add(new Act(tic.Pose, tic.Ms, Emote: tic.Emote));
                return true;
            }
            case Activity.Nap:
            {
                if (Spot(actor, ParkSpotKind.Nap) is not { } spot) return false;
                Add(new Label("Looking for a sunny patch", "Sleepy"), new GoTo(new(spot.X, spot.Y)),
                    new Label("Napping in the grass", "Sleepy"),
                    new Act(ParkPose.Lie, 1500, spot.Facing),
                    new Act(ParkPose.Sleep, Pause(15000, 35000), Emote: ParkEmote.Sleep),
                    new Act(ParkPose.Wake, 1200), new Act(ParkPose.DeepBreath, 1500));
                return true;
            }
            case Activity.Snack:
            {
                if (Spot(actor, ParkSpotKind.Berries) is not { } spot) return false;
                Add(new Label("Off to pick berries", "Hungry"), new GoTo(new(spot.X, spot.Y)), new CheckBush(spot),
                    new Label("Munching on berries", "Delighted"),
                    new Act(ParkPose.Eat, Pause(5000, 9000), spot.Facing, Prop: ParkProp.Berry),
                    new Act(ParkPose.Hop, 1200, Emote: ParkEmote.Note));
                // Snorlax and Munchlax leave nothing behind.
                if (actor.Resident.Has(ParkQuirk.Glutton))
                {
                    Add(new Label("Eating every last berry", "Delighted"), new Act(ParkPose.Eat, Pause(8000, 12_000), spot.Facing, Prop: ParkProp.Berry), new Strip(spot));
                    if (random.Next(3) == 0) Add(new Journal($"{actor.Resident.Name} ate every last berry on the bush. Again."));
                }
                return true;
            }
            case Activity.Sit:
            {
                if (Spot(actor, ParkSpotKind.Shore) is not { } spot) return false;
                Add(new Label("Heading to the lake", "Calm"), new GoTo(new(spot.X, spot.Y)),
                    new Label("Watching the lake", "Peaceful"),
                    new Act(ParkPose.Sit, Pause(8000, 16000), spot.Facing, random.Next(3) == 0 ? ParkEmote.Ellipsis : ParkEmote.None));
                return true;
            }
            case Activity.Statue:
            {
                if (Spot(actor, ParkSpotKind.Statue) is not { } spot) return false;
                Add(new Label("Visiting the statue", "Curious"), new GoTo(new(spot.X, spot.Y)),
                    new Label("Admiring the statue", "Inspired"),
                    new Act(ParkPose.LookUp, 2200, spot.Facing, ParkEmote.Exclaim),
                    new Act(ParkPose.Idle, 1200, spot.Facing));
                if (actor.Resident.Personality is ParkPersonality.Proud or ParkPersonality.Bold || random.Next(2) == 0)
                    Add(new Label("Striking the statue's pose", "Proud"), new Act(ParkPose.Pose, 1800, 0, ParkEmote.Sparkle));
                return true;
            }
            case Activity.Flag:
            {
                if (Spot(actor, ParkSpotKind.Flag) is not { } spot) return false;
                Add(new Label("Going to the flag", "Curious"), new GoTo(new(spot.X, spot.Y)),
                    new Label("Watching the flag in the wind", "Curious"),
                    new Act(ParkPose.LookUp, 2500, spot.Facing), new Act(ParkPose.Idle, 2000, spot.Facing, ParkEmote.Question));
                return true;
            }
            case Activity.Door:
            {
                if (Spot(actor, ParkSpotKind.Door) is not { } spot) return false;
                Add(new Label("Visiting the cabin", "Hopeful"), new GoTo(new(spot.X, spot.Y)),
                    new Label("Waiting at the cabin door", "Hopeful"),
                    new Act(ParkPose.Idle, 1500, spot.Facing), new Act(ParkPose.Nod, 1200, spot.Facing, ParkEmote.Question),
                    new Act(ParkPose.Idle, 2500, spot.Facing, ParkEmote.Ellipsis));
                return true;
            }
            case Activity.Gate:
            {
                if (Spot(actor, ParkSpotKind.Gate) is not { } spot) return false;
                Add(new Label("Looking out at the wild lands", "Dreamy"), new GoTo(new(spot.X, spot.Y)),
                    new Act(ParkPose.Idle, Pause(3000, 6000), spot.Facing), new Act(ParkPose.LookUp, 1800, spot.Facing));
                return true;
            }
            case Activity.Dive:
            {
                // A giant of the sea goes down where it is deep, glides along under the surface, a
                // great shadow, and comes up again further on.
                if (FreePoint(actor.Navigator, random, actor, water: true, deep: true) is not { } point) return false;
                Add(new Label("Diving deep", "Calm"), new Act(ParkPose.Float, 1200), new Submerge(true), new Label("Deep under the sea", "Calm"),
                    new GoTo(point, .6f), new Act(ParkPose.Float, Pause(8000, 20000)), new Submerge(false), new Label("Coming up for air", "Refreshed"),
                    new Act(ParkPose.Float, Pause(2500, 5000), Emote: random.Next(3) == 0 ? ParkEmote.Note : ParkEmote.None));
                return true;
            }
            case Activity.Swim:
            {
                if (FreePoint(actor.Navigator, random, actor, water: true, deep: actor.Resident.Leviathan) is not { } point) return false;
                Add(new Label(_map.RegionAt(point.X, point.Y).Id is "sea" or "coast" or "lighthouse" ? "Swimming out at sea" : "Swimming in the lake", "Refreshed"),
                    new GoTo(point, .8f), new Act(ParkPose.Float, Pause(4000, 9000)));
                if (FreePoint(actor.Navigator, random, actor, water: true, deep: actor.Resident.Leviathan) is { } next)
                    Add(new GoTo(next, .8f), new Act(ParkPose.Float, Pause(3000, 7000), Emote: random.Next(2) == 0 ? ParkEmote.Note : ParkEmote.None));
                return true;
            }
            case Activity.Bedtime:
            {
                // Flyers roost at home on the Summit, under the open sky.
                if (CanFly(actor.Resident) && !Wet && PlanRoost(actor)) return true;
                // The little ones like to sleep on Snorlax.
                if (!Wet && PlanBellyBed(actor)) return true;
                // A bed in the cabin if one is free, else a soft patch of grass under the stars.
                var beds = _actors.Values.Count(a => a.Indoors || a.Current is Enter || a.Plan.Any(step => step is Enter));
                // Snorlax does not fit in the cabin: it sleeps out under the stars.
                if (!actor.Resident.WaterBound && actor.Resident.Species != Snorlax && beds < CabinBeds && Spot(actor, ParkSpotKind.Door, crowd: false) is { } door && random.Next(3) > 0)
                {
                    if (Wet && ShelterFromRain(actor.Resident) && Phase != ParkDayPhase.Night)
                    {
                        Add(new Label("Running for cover from the rain", "Grumpy"), new GoTo(new(door.X, door.Y), 1.6f),
                            new Act(ParkPose.Idle, 400, door.Facing, ParkEmote.Sweat), new Enter(),
                            new Label("Sheltering in the cabin", "Grumpy"), new Act(ParkPose.Idle, 60_000));
                        return true;
                    }
                    Add(new Label("Heading to bed in the cabin", "Sleepy"), new GoTo(new(door.X, door.Y)),
                        new Act(ParkPose.Idle, 600, door.Facing, ParkEmote.Ellipsis), new Enter(),
                        new Label("Sleeping in the cabin", "Sleepy"), new Act(ParkPose.Sleep, Pause(60_000, 180_000)));
                    return true;
                }
                if (Wet && ShelterFromRain(actor.Resident) && Phase != ParkDayPhase.Night && HomeArch is { } arch && !Crowded(actor, arch.X, arch.Y, PersonalSpace))
                {
                    // The cabin is full: under the arch's beam is the next best thing.
                    Add(new Label("Sheltering under the arch", "Grumpy"), new GoTo(new(arch.X, arch.Y), 1.6f),
                        new Act(ParkPose.Idle, Pause(20_000, 40_000), 0, ParkEmote.Sweat));
                    return true;
                }
                var bed = Spot(actor, ParkSpotKind.Nap) is { } nap ? new ParkPoint(nap.X, nap.Y)
                    : FreePoint(actor.Navigator, random, actor) ?? new ParkPoint(actor.X, actor.Y);
                Add(new Label("Looking for a place to sleep", "Sleepy"), new GoTo(bed),
                    new Label("Fast asleep under the stars", "Sleepy"),
                    // Snorlax, once down, sleeps the night through.
                    new Act(ParkPose.Lie, 1500), new Act(ParkPose.Sleep, actor.Resident.Species == Snorlax ? Pause(1_800_000, 3_600_000) : Pause(60_000, 180_000),
                        Emote: ParkEmote.Sleep));
                return true;
            }
            case Activity.Dance when Wet && actor.Resident.Swims:
                Add(new Label("Dancing in the rain", "Joyful"),
                    new Act(ParkPose.Dance, Pause(3000, 5000), Emote: ParkEmote.Note), new Act(ParkPose.Spin, 1400), new Act(ParkPose.LookUp, 1500, 4));
                return true;
            case Activity.Train when Weather == ParkWeather.Storm && actor.Resident.Types.Contains(ParkType.Electric):
                Add(new Label("Soaking up the storm", "Joyful"),
                    new Act(ParkPose.LookUp, 1500, 4, ParkEmote.Exclaim), new Act(ParkPose.Charge, Pause(4000, 7000)), new Gain(ParkNeed.Energy, 20),
                    new Act(ParkPose.Hop, 1200, Emote: ParkEmote.Sparkle));
                return true;
            case Activity.Dance:
                Add(new Label("Dancing for no reason at all", "Joyful"),
                    new Act(ParkPose.Dance, Pause(2500, 4000), Emote: ParkEmote.Note), new Act(ParkPose.Spin, 1400), new Act(ParkPose.Hop, 1200, Emote: ParkEmote.Sparkle));
                return true;
            case Activity.Train:
                Add(new Label("Practicing its moves", "Determined"),
                    new Act(ParkPose.Charge, 1800), new Act(ParkPose.Attack, 1000), new Act(ParkPose.Idle, 800),
                    new Act(ParkPose.Attack, 1000, Emote: ParkEmote.Exclaim), new Act(ParkPose.DeepBreath, 1500));
                return true;
            case Activity.Quirk:
                return PlanQuirk(actor);
            case Activity.Campfire:
                return PlanCampfire(actor);
            case Activity.Fireside:
                return PlanFireside(actor);
            case Activity.Greet or Activity.PlayFight:
                return PlanTogether(actor, activity, now);
        }
        return false;
    }

    // Walk up to a free neighbor, then act the moment out together. The partner waits in place
    // while the visitor walks over, so they actually meet.
    private bool PlanTogether(ParkActor actor, Activity activity, long now)
    {
        var random = actor.Random;
        (int Friendship, int Rivalry) Between(ParkActor other) => Relation?.Invoke(actor.Resident.Id, other.Resident.Id) ?? (0, 0);
        // Friends seek each other out; distance and a little chance do the rest.
        var partner = _actors.Values
            .Where(other => other != actor && !other.Busy && !other.IsAsleep && !other.Indoors && other.Current is not GoTo &&
                Feeling(actor, other) != ParkPairKind.Fears && Feeling(other, actor) != ParkPairKind.Fears &&
                // Nobody walks up to say hello to someone imposing, and the imposing do not go round greeting.
                !InAwe(actor, other) && !InAwe(other, actor) &&
                actor.Navigator.Connected(new(actor.X, actor.Y), new(other.X, other.Y)))
            .OrderBy(other => Distance(actor.X, actor.Y, other.X, other.Y) + random.Next(60) - Between(other).Friendship * .8f -
                Feeling(actor, other) switch { ParkPairKind.Love => 150, ParkPairKind.Family => 80, ParkPairKind.Friends => 70, ParkPairKind.Rivals => 40, _ => 0 })
            .FirstOrDefault();
        if (partner is null) return false;
        // Stand beside the partner, on whichever side is free.
        var side = actor.X < partner.X ? -1 : 1;
        ParkPoint? meet = null;
        foreach (var s in new[] { side, -side })
        {
            var point = new ParkPoint(partner.X + s * 18, partner.Y);
            if (actor.Navigator.CanStand(point.X, point.Y) && actor.Navigator.Connected(new(actor.X, actor.Y), point)) { meet = point; break; }
        }
        if (meet is null) return false;

        // Rivals sometimes quarrel instead, Bold and Proud ones more readily.
        var relation = Between(partner);
        var temper = actor.Resident.Personality is ParkPersonality.Bold or ParkPersonality.Proud ? 1.5f : 1;
        var quarrel = relation.Rivalry > 20 && random.NextDouble() < relation.Rivalry / 100f * temper;
        // Two who fell out may make up instead, the gentle ones and those who were friends first more readily.
        var gentle = actor.Resident.Temperament is ParkTemperament.Gentle or ParkTemperament.Friendly;
        var makeUp = relation.Rivalry > 20 && !quarrel && (relation.Friendship > 0 || gentle) && random.NextDouble() < (gentle ? .5 : .3);

        actor.PartnerId = partner.Resident.Id;
        partner.PartnerId = actor.Resident.Id;
        ClearPlan(partner);
        var feeling = Feeling(actor, partner);
        // Sworn rivals fight for real now and then, not every time they cross paths.
        if (feeling == ParkPairKind.Rivals && Now - _foughtAt.GetValueOrDefault(PairKey(actor, partner), long.MinValue / 2) < FightAgain)
            feeling = ParkPairKind.None;
        if (feeling == ParkPairKind.Rivals) _foughtAt[PairKey(actor, partner)] = Now;
        if (feeling is ParkPairKind.Love or ParkPairKind.Rivals)
        {
            partner.Plan.Enqueue(new Face(actor.Resident.Id));
            partner.Plan.Enqueue(new Act(ParkPose.Idle, 20000, Emote: feeling == ParkPairKind.Love ? ParkEmote.Heart : ParkEmote.Anger));
            partner.Plan.Enqueue(new Release());
            PlanPair(actor, partner, feeling, meet.Value);
            return true;
        }
        // The partner notices the visitor and waits (released by the visitor's Engage, or after 20 s).
        partner.Plan.Enqueue(new Face(actor.Resident.Id));
        partner.Plan.Enqueue(new Act(ParkPose.Idle, 20000, Emote: activity == Activity.Greet && !quarrel ? ParkEmote.None : ParkEmote.Exclaim));
        partner.Plan.Enqueue(new Release());

        string them = partner.Resident.Name, me = actor.Resident.Name, myId = actor.Resident.Id, theirId = partner.Resident.Id;
        void Add(params Step[] steps) { foreach (var step in steps) actor.Plan.Enqueue(step); }
        if (makeUp)
        {
            Add(new Label($"Going to make up with {them}", "Nervous"), new GoTo(meet.Value, .8f), new Face(theirId),
                new Label($"Making up with {them}", "Nervous"),
                new Engage(theirId, [new Face(myId), new Label($"Making up with {me}", "Nervous"), new Act(ParkPose.Idle, 1200, Emote: ParkEmote.Ellipsis),
                    new Act(ParkPose.Nod, 1400, Emote: ParkEmote.Heart), new Gain(ParkNeed.Social, 12)]),
                new Act(ParkPose.Cringe, 1200, Emote: ParkEmote.Sweat), new Act(ParkPose.Nod, 1400, Emote: ParkEmote.Heart),
                new Gain(ParkNeed.Social, 12),
                new Journal($"{me} walked up to {them}, looked at its feet for a while, and said sorry. They are friends again.", theirId, ParkSocialEventKind.Deescalation),
                new Release());
        }
        else if (quarrel)
        {
            // A spat: glares, a shove, then both turn their backs. They will make up another day.
            Add(new Label($"Walking up to {them}", "Grumpy"), new GoTo(meet.Value), new Face(theirId),
                new Label($"Arguing with {them}", "Grumpy"),
                new Engage(theirId, [new Face(myId), new Label($"Arguing with {me}", "Grumpy"),
                    new Act(ParkPose.Idle, 900, Emote: ParkEmote.Anger), new Act(ParkPose.Cringe, 900),
                    new Act(ParkPose.Idle, 1200, Emote: ParkEmote.Anger), new Gain(ParkNeed.Social, -4), new Act(ParkPose.Idle, 2500, random.Next(8))]),
                new Act(ParkPose.Idle, 900, Emote: ParkEmote.Anger), new Act(ParkPose.Attack, 900),
                new Act(ParkPose.Idle, 1200, Emote: ParkEmote.Anger), new Gain(ParkNeed.Social, -4),
                new Share(ParkSocialEventKind.Conflict, theirId), new Act(ParkPose.Idle, 2500, (actor.Facing + 4) % 8), new Release());
        }
        else if (activity == Activity.Greet)
        {
            Add([new Label($"Going to say hello to {them}", "Friendly"), new GoTo(meet.Value, 1.2f), new Face(theirId),
                new Label($"Saying hello to {them}", "Friendly"),
                new Engage(theirId, [new Face(myId), new Label($"Saying hello to {me}", "Friendly"),
                    new Act(ParkPose.Idle, 400), new Act(ParkPose.Nod, 1600, Emote: ParkEmote.Heart), new Gain(ParkNeed.Social, 14), new Act(ParkPose.Idle, 1500)]),
                new Act(ParkPose.Nod, 1600, Emote: ParkEmote.Note), new Gain(ParkNeed.Social, 14),
                .. actor.Resident.Has(ParkQuirk.Copycat)
                    ? new Step[] { new Spark(ParkEffectKind.Psychic), new Copy(theirId), new Journal($"{me} said hello to {them}, then turned into a second {them}. {them} did not find it funny.", theirId) }
                    : [],
                new Share(relation.Friendship > 50 ? ParkSocialEventKind.Bond : ParkSocialEventKind.Rest, theirId),
                new Act(ParkPose.Idle, 1900), new Release()]);
        }
        else
        {
            // Two beats of sparring, then both bounce: nobody really gets hurt.
            Add(new Label($"Challenging {them} to a play fight", "Playful"), new GoTo(meet.Value, 1.2f), new Face(theirId),
                new Label($"Play-fighting with {them}", "Playful"),
                new Engage(theirId, [new Face(myId), new Label($"Play-fighting with {me}", "Playful"),
                    new Act(ParkPose.Idle, 500), new Act(ParkPose.Hurt, 900, Emote: ParkEmote.Sweat), new Act(ParkPose.Attack, 800),
                    new Act(ParkPose.Hop, 1600, Emote: ParkEmote.Note), new Gain(ParkNeed.Social, 10)]),
                new Act(ParkPose.Attack, 900, Emote: ParkEmote.Exclaim), new Act(ParkPose.Idle, 500), new Act(ParkPose.Hurt, 800),
                new Act(ParkPose.Hop, 1600, Emote: ParkEmote.Note), new Gain(ParkNeed.Social, 10),
                new Share(ParkSocialEventKind.Play, theirId), new Release());
        }
        return true;
    }

    private readonly Dictionary<(string, string), long> _foughtAt = new();
    private const long FightAgain = 90 * 60_000;

    private bool InWater(ParkActor actor) => _map.TerrainAt(actor.X, actor.Y) == ParkTerrain.Water;

    /// <summary>Whether a resident is swimming right now (its feet are in water).</summary>
    public bool IsSwimming(ParkActor actor) => InWater(actor);

    // A spot of that kind that nobody else stands on or walks to: one in the place the Pokémon is
    // in when there is one, now and then one back in the place it calls home, else the nearest few.
    private ParkSpot? Spot(ParkActor actor, ParkSpotKind kind, bool crowd = true)
    {
        var free = _map.Spots.Where(spot => spot.Kind == kind && actor.Navigator.CanStand(spot.X, spot.Y) &&
            actor.Navigator.Connected(new(actor.X, actor.Y), new(spot.X, spot.Y)) &&
            (!crowd || !Crowded(actor, spot.X, spot.Y, PersonalSpace))).ToArray();
        if (free.Length == 0) return null;
        var home = HomePlace(actor.Resident);
        var homeward = free.Where(s => home.Contains(s.X, s.Y)).ToArray();
        if (homeward.Length > 0 && !home.Contains(actor.X, actor.Y) && actor.Random.Next(5) == 0) return homeward[actor.Random.Next(homeward.Length)];
        var local = free.Where(s => Local(actor, s.X, s.Y)).ToArray();
        if (local.Length > 0) return local[actor.Random.Next(local.Length)];
        // Nothing of the kind here: it goes to look elsewhere one time in three (always for a bed
        // in the cabin or the daycare), and otherwise does something else where it is.
        if (kind is not (ParkSpotKind.Door or ParkSpotKind.Daycare) && actor.Random.Next(3) > 0) return null;
        var nearest = free.OrderBy(s => Distance(s.X, s.Y, actor.X, actor.Y)).Take(3).ToArray();
        return nearest[actor.Random.Next(nearest.Length)];
    }

    // How far a Pokémon goes for an everyday errand: within its place, or this far across the open
    // meadow that ties the places together.
    private const float Errand = 220;

    private bool Local(ParkActor actor, float x, float y)
    {
        var place = _map.RegionAt(actor.X, actor.Y);
        return IsWhole(place) ? Distance(actor.X, actor.Y, x, y) <= Errand : place.Contains(x, y);
    }

    private bool IsWhole(ParkRegion place) => place.Width >= _map.Width && place.Height >= _map.Height;

    /// <summary>The place a Pokémon calls home: the open sea for the giants of the sea, a height
    /// for the true flyers, the water, the volcano or the snow where its types feel at home, else
    /// the home park; among several, one of them for good.</summary>
    public ParkRegion HomePlace(ParkLifeResident resident)
    {
        var types = resident.Types;
        if (resident.Leviathan && _map.Regions.FirstOrDefault(r => r.Id == "sea") is { } sea) return sea;
        // Flyers perch on the height that suits them: the crater's rim for the fiery ones, the
        // lighthouse point for the seabirds, the shrine's plateau for the gentle and the mystic,
        // the snowy summit for the rest.
        if (Winged(resident))
        {
            var perch = types.Contains(ParkType.Fire) ? "crater" : types.Contains(ParkType.Water) ? "lighthouse"
                : types.Contains(ParkType.Grass) || types.Contains(ParkType.Fairy) || types.Contains(ParkType.Psychic) ? "shrine" : "summit";
            if ((_map.Regions.FirstOrDefault(r => r.Id == perch) ?? _map.Regions.FirstOrDefault(r => r.High)) is { } height) return height;
        }
        var habitat = types.Contains(ParkType.Water) ? ParkEnvironmentId.WaterfallLake
            : types.Contains(ParkType.Fire) ? ParkEnvironmentId.EmberGrove
            : types.Contains(ParkType.Ice) ? ParkEnvironmentId.SkySummit
            : types.Contains(ParkType.Ground) || types.Contains(ParkType.Rock) ? ParkEnvironmentId.EmberGrove
            : ParkEnvironmentId.Commons;
        var places = _map.Regions.Where(r => r.Habitat == habitat && !IsWhole(r)).ToArray();
        if (habitat == ParkEnvironmentId.Commons || places.Length == 0)
            return _map.RegionAt(_map.AtHome(168, 100).X, _map.AtHome(168, 100).Y);
        // Kin live together: the place goes by species, so a second Diglett finds the first.
        return places[(int)((uint)Hash($"{resident.Species}") % places.Length)];
    }

    private bool Crowded(ParkActor? self, float x, float y, float space) => _actors.Values.Any(other => other != self &&
        (Distance(other.X, other.Y, x, y) < space ||
         other.Destination is { } d && Distance(d.X, d.Y, x, y) < space));

    // A random open point away from everyone: on land, in water, or either. In a place (by default
    // the one the Pokémon is in, or an errand's walk across the meadow); one time in eight anywhere.
    private ParkPoint? FreePoint(ParkNavigator navigator, Random random, ParkActor? actor, bool? land = null, bool water = false, ParkRegion? place = null,
        bool deep = false)
    {
        var cells = navigator.OpenCells;
        if (cells.Count == 0) return null;
        if (place is null && actor is not null && random.Next(8) > 0)
        {
            var here = _map.RegionAt(actor.X, actor.Y);
            place = IsWhole(here)
                ? new ParkRegion(here.Id, here.Name, here.Habitat, (int)(actor.X - Errand), (int)(actor.Y - Errand), (int)(2 * Errand), (int)(2 * Errand))
                : here;
        }
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var cell = place is null ? cells[random.Next(cells.Count)]
                : navigator.CellNear(place.Left + (float)random.NextDouble() * place.Width, place.Top + (float)random.NextDouble() * place.Height);
            if (cell < 0) return null;
            var point = navigator.Center(cell);
            var terrain = _map.TerrainAt(point.X, point.Y);
            if (water && terrain != ParkTerrain.Water) continue;
            // Deep: open water all round, well away from any shore.
            if (deep && new[] { (-40f, 0f), (40f, 0f), (0f, -28f), (0f, 28f) }.Any(d => _map.TerrainAt(point.X + d.Item1, point.Y + d.Item2) != ParkTerrain.Water)) continue;
            if (land == true && terrain != ParkTerrain.Land) continue;
            if (actor is not null && (!navigator.Connected(new(actor.X, actor.Y), point) || Distance(actor.X, actor.Y, point.X, point.Y) < 40)) continue;
            if (Crowded(actor, point.X, point.Y, PersonalSpace)) continue;
            return point;
        }
        return null;
    }

    /// <summary>The PMD portrait that shows a mood (PMDCollab emotion names).</summary>
    public static string PortraitFor(string mood) => mood switch
    {
        "Delighted" or "Friendly" or "Refreshed" or "Playful" or "Happy" => "Happy",
        "Joyful" => "Joyous",
        "Inspired" or "Curious" => "Inspired",
        "Proud" or "Determined" => "Determined",
        "Embarrassed" or "Sleepy" or "Bored" => "Sigh",
        "Hopeful" or "Hungry" => "Worried",
        "Lonely" => "Sad",
        "Grumpy" => "Angry",
        "Scared" => "Surprised",
        "Unwell" => "Pain",
        "Nervous" => "Worried",
        "In love" => "Happy",
        _ => "Normal"
    };

    /// <summary>A resident's lasting personality, from its id.</summary>
    public static ParkPersonality PersonalityFor(string id) => ((uint)Hash(id) % 9) switch
    {
        0 => ParkPersonality.Curious,
        1 => ParkPersonality.Calm,
        2 => ParkPersonality.Gentle,
        3 => ParkPersonality.Playful,
        4 => ParkPersonality.Helpful,
        5 => ParkPersonality.Shy,
        6 => ParkPersonality.Energetic,
        7 => ParkPersonality.Proud,
        _ => ParkPersonality.Bold
    };

    private static float Distance(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private static int Hash(string text)
    {
        uint hash = 2166136261;
        foreach (var letter in text) hash = (hash ^ letter) * 16777619;
        return unchecked((int)hash);
    }
}
