namespace PKForge.Domain;

/// <summary>Wild Pokémon that come in from the wild lands around the park, spend a few minutes
/// and leave the way they came. Each place has its own: Slugma and Numel up the volcano's slope,
/// Snorunt and Swinub in the snow, Wingull and Krabby on the beach, Seedot and Teddiursa out of
/// the autumn woods; Pidgey and Bidoof by the home park by day, Hoothoot and Gastly at night,
/// Lotad and Wooper at the water in the rain. Flyers come in over the trees and leave by air.</summary>
public sealed partial class ParkLife
{
    private sealed record Wild(string Name, int Species, int[] Types, ParkPersonality Personality);

    private static readonly Wild[] DayWild =
    [
        new("Pidgey", 16, [ParkType.Normal, ParkType.Flying], ParkPersonality.Shy), new("Rattata", 19, [ParkType.Normal], ParkPersonality.Energetic),
        new("Caterpie", 10, [ParkType.Bug], ParkPersonality.Curious), new("Sentret", 161, [ParkType.Normal], ParkPersonality.Curious),
        new("Zigzagoon", 263, [ParkType.Normal], ParkPersonality.Playful), new("Bidoof", 399, [ParkType.Normal], ParkPersonality.Calm),
        new("Starly", 396, [ParkType.Normal, ParkType.Flying], ParkPersonality.Bold), new("Lillipup", 506, [ParkType.Normal], ParkPersonality.Gentle),
        new("Skwovet", 819, [ParkType.Normal], ParkPersonality.Curious), new("Lechonk", 915, [ParkType.Normal], ParkPersonality.Calm),
        new("Butterfree", 12, [ParkType.Bug, ParkType.Flying], ParkPersonality.Gentle), new("Pikipek", 731, [ParkType.Normal, ParkType.Flying], ParkPersonality.Energetic),
    ];

    private static readonly Wild[] NightWild =
    [
        new("Hoothoot", 163, [ParkType.Normal, ParkType.Flying], ParkPersonality.Calm), new("Zubat", 41, [ParkType.Poison, ParkType.Flying], ParkPersonality.Energetic),
        new("Gastly", 92, [ParkType.Ghost, ParkType.Poison], ParkPersonality.Playful), new("Murkrow", 198, [ParkType.Dark, ParkType.Flying], ParkPersonality.Proud),
        new("Oddish", 43, [ParkType.Grass, ParkType.Poison], ParkPersonality.Shy), new("Poochyena", 261, [ParkType.Dark], ParkPersonality.Bold),
        new("Noibat", 714, [ParkType.Flying, ParkType.Dragon], ParkPersonality.Curious),
    ];

    private static readonly Wild[] RainWild =
    [
        new("Lotad", 270, [ParkType.Water, ParkType.Grass], ParkPersonality.Calm), new("Wooper", 194, [ParkType.Water, ParkType.Ground], ParkPersonality.Calm),
        new("Psyduck", 54, [ParkType.Water], ParkPersonality.Curious), new("Shellos", 422, [ParkType.Water], ParkPersonality.Shy),
    ];

    // The wild ones of each place of the park, by day and by night (the home park and the meadow
    // have the everyday ones above).
    private static readonly Dictionary<string, (Wild[] Day, Wild[] Night)> PlaceWild = new()
    {
        ["volcano"] = ([new("Slugma", 218, [ParkType.Fire], ParkPersonality.Calm), new("Numel", 322, [ParkType.Fire, ParkType.Ground], ParkPersonality.Calm),
            new("Torkoal", 324, [ParkType.Fire], ParkPersonality.Proud), new("Geodude", 74, [ParkType.Rock, ParkType.Ground], ParkPersonality.Bold),
            new("Sizzlipede", 850, [ParkType.Fire, ParkType.Bug], ParkPersonality.Energetic)],
            [new("Litwick", 607, [ParkType.Ghost, ParkType.Fire], ParkPersonality.Shy), new("Slugma", 218, [ParkType.Fire], ParkPersonality.Calm)]),
        ["summit"] = ([new("Swablu", 333, [ParkType.Normal, ParkType.Flying], ParkPersonality.Gentle), new("Rufflet", 627, [ParkType.Normal, ParkType.Flying], ParkPersonality.Bold),
            new("Delibird", 225, [ParkType.Ice, ParkType.Flying], ParkPersonality.Playful), new("Skarmory", 227, [ParkType.Steel, ParkType.Flying], ParkPersonality.Proud)],
            [new("Noctowl", 164, [ParkType.Normal, ParkType.Flying], ParkPersonality.Calm), new("Sneasel", 215, [ParkType.Dark, ParkType.Ice], ParkPersonality.Bold)]),
        ["frost"] = ([new("Snorunt", 361, [ParkType.Ice], ParkPersonality.Shy), new("Swinub", 220, [ParkType.Ice, ParkType.Ground], ParkPersonality.Curious),
            new("Snover", 459, [ParkType.Grass, ParkType.Ice], ParkPersonality.Calm), new("Cubchoo", 613, [ParkType.Ice], ParkPersonality.Playful),
            new("Bergmite", 712, [ParkType.Ice], ParkPersonality.Calm)],
            [new("Sneasel", 215, [ParkType.Dark, ParkType.Ice], ParkPersonality.Bold), new("Snorunt", 361, [ParkType.Ice], ParkPersonality.Shy)]),
        ["amber"] = ([new("Seedot", 273, [ParkType.Grass], ParkPersonality.Curious), new("Teddiursa", 216, [ParkType.Normal], ParkPersonality.Playful),
            new("Deerling", 585, [ParkType.Normal, ParkType.Grass], ParkPersonality.Gentle), new("Shroomish", 285, [ParkType.Grass], ParkPersonality.Shy),
            new("Skwovet", 819, [ParkType.Normal], ParkPersonality.Curious)],
            [new("Hoothoot", 163, [ParkType.Normal, ParkType.Flying], ParkPersonality.Calm), new("Spinarak", 167, [ParkType.Bug, ParkType.Poison], ParkPersonality.Shy),
             new("Phantump", 708, [ParkType.Ghost, ParkType.Grass], ParkPersonality.Shy)]),
        ["shrine"] = ([new("Ralts", 280, [ParkType.Psychic, ParkType.Fairy], ParkPersonality.Shy), new("Flabébé", 669, [ParkType.Fairy], ParkPersonality.Gentle),
            new("Natu", 177, [ParkType.Psychic, ParkType.Flying], ParkPersonality.Calm)],
            [new("Misdreavus", 200, [ParkType.Ghost], ParkPersonality.Playful), new("Gastly", 92, [ParkType.Ghost, ParkType.Poison], ParkPersonality.Playful)]),
        ["daycare"] = ([new("Happiny", 440, [ParkType.Normal], ParkPersonality.Gentle), new("Pichu", 172, [ParkType.Electric], ParkPersonality.Playful),
            new("Cleffa", 173, [ParkType.Fairy], ParkPersonality.Playful), new("Igglybuff", 174, [ParkType.Normal, ParkType.Fairy], ParkPersonality.Gentle)],
            [new("Munna", 517, [ParkType.Psychic], ParkPersonality.Calm)]),
        ["pool"] = ([new("Lotad", 270, [ParkType.Water, ParkType.Grass], ParkPersonality.Calm), new("Marill", 183, [ParkType.Water, ParkType.Fairy], ParkPersonality.Playful),
            new("Psyduck", 54, [ParkType.Water], ParkPersonality.Curious), new("Surskit", 283, [ParkType.Bug, ParkType.Water], ParkPersonality.Energetic)],
            [new("Chinchou", 170, [ParkType.Water, ParkType.Electric], ParkPersonality.Calm), new("Lotad", 270, [ParkType.Water, ParkType.Grass], ParkPersonality.Calm)]),
        ["coast"] = ([new("Wingull", 278, [ParkType.Water, ParkType.Flying], ParkPersonality.Energetic), new("Krabby", 98, [ParkType.Water], ParkPersonality.Bold),
            new("Corphish", 341, [ParkType.Water], ParkPersonality.Bold), new("Staryu", 120, [ParkType.Water], ParkPersonality.Calm)],
            [new("Staryu", 120, [ParkType.Water], ParkPersonality.Calm), new("Chinchou", 170, [ParkType.Water, ParkType.Electric], ParkPersonality.Calm)]),
    };

    internal sealed record Leave : Step;

    public const string VisitorPrefix = "wild:";
    public static bool IsVisitor(string id) => id.StartsWith(VisitorPrefix, StringComparison.Ordinal);

    // A released Pokémon back for a visit: "wild:friend:{key}:{time}".
    private const string FriendPrefix = VisitorPrefix + "friend:";

    /// <summary>Pokémon the player let go into the Wilds; one may come back through the arch now and then.</summary>
    public IReadOnlyList<ParkWildFriend> OldFriends { get; set; } = [];

    /// <summary>The released Pokémon behind a visitor id, when it is one coming back.</summary>
    public ParkWildFriend? OldFriendOf(string id)
    {
        if (!id.StartsWith(FriendPrefix, StringComparison.Ordinal)) return null;
        var key = id[FriendPrefix.Length..id.LastIndexOf(':')];
        return OldFriends.FirstOrDefault(f => f.Key == key);
    }

    private long _nextVisit;
    private readonly List<string> _leaving = new();

    /// <summary>The player approaches a wild visitor: it either bolts back out through the arch or
    /// warms up and leaves a little something behind.</summary>
    public ParkReply Befriend(string id) { lock (_gate) return BefriendCore(id); }

    private ParkReply BefriendCore(string id)
    {
        if (!IsVisitor(id) || IsKeeper(id) || !_actors.TryGetValue(id, out var visitor)) return ParkReply.None;
        var name = visitor.Resident.Name;
        Interrupt(visitor);
        // An old friend never runs from you.
        if (OldFriendOf(id) is { } friend)
        {
            Burst(ParkEffectKind.Hearts, visitor);
            Add(visitor, new Label("Happy to see you again", "Joyous"), new Act(ParkPose.Hop, 1400, 0, ParkEmote.Heart), new Act(ParkPose.Dance, 2500, Emote: ParkEmote.Note));
            visitor.LeaveAt = Math.Max(visitor.LeaveAt, Now + 120_000);
            return new(true, ParkWilds.Greeting(friend, Today, visitor.Random), "Joyous", Gift: ParkWilds.Gift(friend, visitor.Random));
        }
        if (visitor.Random.Next(100) < (visitor.Resident.Personality == ParkPersonality.Shy ? 70 : 35))
        {
            visitor.LeaveAt = 0;
            Add(visitor, new Label("Running off", "Scared"), new Act(ParkPose.Cringe, 500, 0, ParkEmote.Exclaim));
            Depart(visitor, hurry: true);
            return new(false, $"The wild {name} startles and darts off the way it came. Maybe next time.", "Surprised");
        }
        Burst(ParkEffectKind.Coin, visitor);
        Add(visitor, new Label("Making friends", "Friendly"), new Act(ParkPose.Nod, 1400, 0, ParkEmote.Heart), new Act(ParkPose.Hop, 1000));
        var gift = visitor.Random.Next(3) switch { 0 => "an Oran Berry", 1 => "a Pecha Berry", _ => "a smooth pebble" };
        return new(true, $"The wild {name} sniffs your hand, decides you are all right, and drops {gift} at your feet as thanks.", "Happy",
            Gift: new ParkTreasure($"{gift} (a gift from a wild {name})", 1));
    }

    /// <summary>The anniversary of the day the player met a resident: everyone awake gathers round
    /// and dances, and it glows with pride.</summary>
    public ParkReply Celebrate(string id, int years) { lock (_gate) return CelebrateCore(id, years); }

    private ParkReply CelebrateCore(string id, int years)
    {
        if (!_actors.TryGetValue(id, out var hero) || hero.Indoors) return ParkReply.None;
        var name = hero.Resident.Name;
        if (hero.IsAsleep) { Interrupt(hero); Wake(hero); hero.GrudgeUntil = 0; }
        else Interrupt(hero);
        Add(hero, new Label("Celebrating with everyone", "Joyful"), new Spark(ParkEffectKind.Sparkle), new Act(ParkPose.Pose, 2000, 0, ParkEmote.Sparkle),
            new Spark(ParkEffectKind.Hearts), new Act(ParkPose.Dance, 5000, 0, ParkEmote.Note), new Gain(ParkNeed.Fun, 25, 6), new Gain(ParkNeed.Social, 25));
        // The guests stand round the hero where there is room, the nearest first: two rings of
        // places, so a party by a hedge or a wall still finds space.
        var places = Enumerable.Range(0, 16).Select(i => (Angle: i * MathF.PI * 2 / 8 + (i >= 8 ? MathF.PI / 8 : 0), Far: i >= 8))
            .Select(r => new ParkPoint(hero.X + MathF.Cos(r.Angle) * (r.Far ? 40 : 24), hero.Y + MathF.Sin(r.Angle) * (r.Far ? 22 : 14)))
            .Where(p => hero.Navigator.CanStand(p.X, p.Y) && hero.Navigator.Connected(new(hero.X, hero.Y), p)).ToList();
        foreach (var guest in _actors.Values.Where(a => a != hero && !a.IsAsleep && !a.Indoors && !a.Busy && !IsVisitor(a.Resident.Id))
                     .OrderBy(a => Distance(a.X, a.Y, hero.X, hero.Y)).Take(8))
        {
            var index = places.FindIndex(p => guest.Navigator.CanStand(p.X, p.Y) && guest.Navigator.Connected(new(guest.X, guest.Y), p));
            if (index < 0) continue;
            var spot = places[index];
            places.RemoveAt(index);
            Interrupt(guest);
            Add(guest, new Label($"At {name}'s party", "Joyful"), new GoTo(spot, 1.5f), new Face(id), new Act(ParkPose.Hop, 1500, Emote: ParkEmote.Note),
                new Act(ParkPose.Dance, 4000), new Gain(ParkNeed.Fun, 15), new Gain(ParkNeed.Social, 15));
        }
        var when = years <= 0 ? "today" : years == 1 ? "one year ago today" : $"{years} years ago today";
        _moments.Add(new ParkMoment(ParkSocialEventKind.Bond, id, "", $"The park threw a party for {name}: you met it {when}."));
        return new(true, $"You met {name} {when}! The whole park gathers round to celebrate, and {name} cannot stop dancing.", "Joyous");
    }

    private void UpdateVisitors(long now)
    {
        foreach (var id in _leaving)
        {
            _actors.Remove(id);
            foreach (var other in _actors.Values.Where(a => a.PartnerId == id)) { other.PartnerId = null; ClearPlan(other); }
            if (Ball?.OwnerId == id || Ball?.HolderId == id) Ball = null;
            if (Game?.Players.Contains(id) == true) EndGame();
        }
        _leaving.Clear();
        if (now < _nextVisit) return;
        _nextVisit = now + 60_000;
        if (_nextVisitArmed is false) { _nextVisitArmed = true; return; } // never in the very first minute
        if (_actors.Keys.Any(id => IsVisitor(id) && !IsKeeper(id)) || _actors.Keys.Count(id => !IsKeeper(id)) >= MostResidents + 2 || Weather == ParkWeather.Storm || _random.Next(100) >= 18) return;
        // One time in four, if any, it is a Pokémon you let go, back from the Wilds, coming in where
        // its kind would.
        if (OldFriends.Count > 0 && _random.Next(4) == 0)
        {
            var friend = OldFriends[_random.Next(OldFriends.Count)];
            var back = new ParkLifeResident($"{FriendPrefix}{friend.Key}:{now}", friend.Name, friend.Species, friend.Types, friend.Personality,
                Shiny: friend.Shiny, Form: friend.Form);
            if (Arrive(back, now + _random.Next(300_000, 480_000), HomePlace(back).Id, now) is not { } returning) return;
            returning.Plan.Enqueue(new Label("Back from the Wilds", "Joyful"));
            Highlight(returning, $"{friend.Name} is back from the Wilds to visit!", 2, 60_000, "A Familiar Face");
            returning.Plan.Enqueue(new Act(ParkPose.LookUp, 1200, 0, ParkEmote.Exclaim));
            returning.Plan.Enqueue(new Act(ParkPose.Hop, 1200, 0, ParkEmote.Heart));
            returning.Plan.Enqueue(new Journal(ParkWilds.Arrival(friend, Today)));
            return;
        }
        // Which place it comes into: the home park most often, else any place with wild ones of its
        // own; in the rain, often the water's edge.
        string place;
        Wild wild;
        if (Weather == ParkWeather.Rain && _random.Next(2) == 0)
        {
            place = _random.Next(2) == 0 ? "pool" : "coast";
            wild = RainWild[_random.Next(RainWild.Length)];
        }
        else if (_random.Next(3) == 0 || PlaceWild.Count == 0)
        {
            place = "home";
            var pool = Phase == ParkDayPhase.Night ? NightWild : DayWild;
            wild = pool[_random.Next(pool.Length)];
        }
        else
        {
            var places = PlaceWild.Keys.Where(id => _map.Regions.Any(r => r.Id == id)).ToArray();
            place = places[_random.Next(places.Length)];
            var (day, night) = PlaceWild[place];
            var pool = Phase == ParkDayPhase.Night ? night : day;
            wild = pool[_random.Next(pool.Length)];
        }
        // Once in a long while, a shiny one.
        var shiny = _random.Next(ShinyOdds) == 0;
        if (Arrive(new ParkLifeResident($"{VisitorPrefix}{wild.Species}:{now}", wild.Name, wild.Species, wild.Types, wild.Personality, Shiny: shiny),
            now + _random.Next(180_000, 360_000), place, now) is not { } visitor) return;
        DogsBark(visitor);
        Highlight(visitor, shiny ? $"A shiny {wild.Name} wandered into {(_map.Regions.FirstOrDefault(r => r.Id == place)?.Name ?? "the park")}!"
            : $"A wild {wild.Name} wandered in", shiny ? 3 : 1, shiny ? 90_000 : 30_000, shiny ? "A Shiny Visitor" : null, secret: shiny);
        var where = _map.Regions.FirstOrDefault(r => r.Id == place)?.Name ?? "the park";
        visitor.Plan.Enqueue(new Label("Wandered in from outside", "Curious"));
        visitor.Plan.Enqueue(new Act(ParkPose.LookUp, 1500, 0, ParkEmote.Question));
        visitor.Plan.Enqueue(new Journal(shiny
            ? $"A shiny {wild.Name} wandered into {where}{(Phase == ParkDayPhase.Night ? " tonight" : "")}. Its colors were all wrong, and everyone stared."
            : $"A wild {wild.Name} wandered into {where}{(Phase == ParkDayPhase.Night ? " tonight" : "")}."));
    }

    private const int ShinyOdds = 100;

    /// <summary>The most residents a park has (the page holds it to this); a wild visitor still fits.</summary>
    public const int MostResidents = 30;

    // Where a place lets the wild lands in: its gates (the end of the pier, the top of the mountain
    // path, the edge of the woods); the home park's arch when it has none.
    private ParkSpot? Gateway(string place)
    {
        var region = _map.Regions.FirstOrDefault(r => r.Id == place);
        var gates = _map.Spots.Where(s => s.Kind == ParkSpotKind.Gate && region?.Contains(s.X, s.Y) == true).ToArray();
        if (gates.Length > 0) return gates[_random.Next(gates.Length)];
        return HomeArch is { } arch ? _map.Spots.First(s => s.Kind == ParkSpotKind.Gate && s.X == arch.X && s.Y == arch.Y) : null;
    }

    /// <summary>The home park's arch (its gate), for shelter from the rain.</summary>
    private ParkPoint? HomeArch
    {
        get
        {
            var home = _map.RegionAt(_map.AtHome(168, 100).X, _map.AtHome(168, 100).Y);
            return _map.Spots.FirstOrDefault(s => s.Kind == ParkSpotKind.Gate && home.Contains(s.X, s.Y)) is { } gate ? new(gate.X, gate.Y) : null;
        }
    }

    // A visitor comes in at a gate of the place, on foot; a flyer comes over the trees from the
    // nearest edge of the map and lands there.
    private ParkActor? Arrive(ParkLifeResident resident, long leaveAt, string place, long now)
    {
        var navigator = ParkNavigator.For(_map, resident.Traversal);
        if (Gateway(place) is not { } gate || !navigator.CanStand(gate.X, gate.Y)) return null;
        var gateway = new ParkPoint(gate.X, gate.Y);
        var flies = CanFly(resident);
        var start = flies ? Outside(gateway) : gateway;
        var visitor = new ParkActor(resident, navigator, new Random(_random.Next()), start.X, start.Y) { Facing = 0, LeaveAt = leaveAt, Gateway = gateway, Place = place };
        _actors[resident.Id] = visitor;
        if (flies) TakeOff(visitor, gateway, now);
        return visitor;
    }

    // Just beyond the nearest edge of the map, level with a point.
    private ParkPoint Outside(ParkPoint near)
    {
        var edges = new (float Distance, ParkPoint Point)[]
        {
            (near.X, new(-24, near.Y)), (_map.Width - near.X, new(_map.Width + 24, near.Y)),
            (near.Y, new(near.X, -24)), (_map.Height - near.Y, new(near.X, _map.Height + 24))
        };
        return edges.MinBy(e => e.Distance).Point;
    }

    // The way out: back to the gate it came in by and away, or up and off over the edge of the map.
    private void Depart(ParkActor actor, bool hurry = false)
    {
        var gateway = actor.Gateway ?? HomeArch ?? new(actor.X, actor.Y);
        if (CanFly(actor.Resident)) actor.Plan.Enqueue(new Fly(Outside(new(actor.X, actor.Y)), Away: true));
        else actor.Plan.Enqueue(new GoTo(gateway, hurry ? 2 : 1));
        actor.Plan.Enqueue(new Leave());
    }

    private bool _nextVisitArmed;

    // A visitor whose time is up heads back out the way it came.
    private bool PlanDeparture(ParkActor actor, long now)
    {
        if (!IsVisitor(actor.Resident.Id) || now < actor.LeaveAt) return false;
        actor.Plan.Enqueue(new Label("Heading back out", "Content"));
        Depart(actor);
        return true;
    }

    private bool RunVisitorStep(ParkActor actor, Step step)
    {
        if (step is not Leave) return false;
        if (!_leaving.Contains(actor.Resident.Id)) _leaving.Add(actor.Resident.Id);
        actor.Plan.Clear();
        actor.Plan.Enqueue(new Act(ParkPose.Idle, 60_000));
        return true;
    }
}
