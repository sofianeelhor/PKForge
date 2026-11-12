namespace PKForge.Domain;

/// <summary>One frame of a clip in a resident's live sheet: where it sits in the sheet, where its feet are (from the frame's top-left corner), how long it shows.</summary>
public sealed record ParkLiveFrame(int X, int Y, int Width, int Height, int FeetX, int FeetY, int Ms);

/// <summary>One of a resident's PMD animations, as the living park widget keeps it.</summary>
public sealed record ParkLiveClip(string Name, ParkLiveFrame[] Frames)
{
    public int TotalMs => Math.Max(1, Frames.Sum(f => Math.Max(1, f.Ms)));
}

/// <summary>What a resident is up to on the living park widget for half an hour.</summary>
public enum LiveActivity { Walk, Idle, LookAround, Sit, Sleep }

/// <summary>A resident on the living park's grass: what it does, where it is (feet, in scene pixels), which way it starts.</summary>
public sealed record LiveActor(ParkWidgetResident Resident, LiveActivity Activity, int X, int FeetY, bool Rightward);

/// <summary>One resident in one frame: the clip it plays, how far through the loop (0 to 1), and where its feet are.</summary>
public sealed record LivePose(string Clip, double Phase, int X, int FeetY, bool Asleep);

/// <summary>Everything the living park widget draws for half an hour: the sky of the hour, the residents and what they do.</summary>
public sealed record LiveParkView(
    ParkWidgetSize Size, int Width, int Height, ParkDayPhase Phase, ParkWeather Weather, int HorizonY,
    IReadOnlyList<LiveActor> Actors, IReadOnlyList<string> Lines, string Title, int Seed);

/// <summary>
/// The pure half of the living Poképark widget. A launcher cannot run PKForge's animation loop, so the
/// widget is a short film the launcher's ViewFlipper plays on its own: <see cref="Frames"/> pictures,
/// <see cref="IntervalMs"/> apart, that loop seamlessly (a walker goes there and comes back). Every
/// half hour (and whenever the park changes) the film is shot again: other residents, other places,
/// other activities, the sky of the hour, and everyone asleep at night.
/// </summary>
public static class LivePark
{
    /// <summary>Frames in the film, at best; fewer when the launcher's bitmap memory is short.</summary>
    public const int Frames = 12;
    public const int MinFrames = 6;
    public const int IntervalMs = 180;

    /// <summary>Scene pixels a walker covers per frame.</summary>
    public const int Stride = 2;

    public const string WalkEast = "walk-e", WalkWest = "walk-w", IdleSouth = "idle-s", IdleSouthEast = "idle-se",
        IdleSouthWest = "idle-sw", Sit = "sit", Sleep = "sleep";

    /// <summary>The clips a live sheet holds: the park's pose and PMD direction (0 south, 1 SE, 2 E … 6 W, 7 SW) for each.</summary>
    public static readonly IReadOnlyList<(string Clip, ParkPose Pose, int Direction)> Clips =
    [
        (WalkEast, ParkPose.Walk, 2), (WalkWest, ParkPose.Walk, 6),
        (IdleSouth, ParkPose.Idle, 0), (IdleSouthEast, ParkPose.Idle, 1), (IdleSouthWest, ParkPose.Idle, 7),
        (Sit, ParkPose.Sit, 0), (Sleep, ParkPose.Sleep, 0),
    ];

    public const string EmptyLine = ParkWidget.EmptyLine;

    /// <summary>How many residents live on the grass for a widget shape.</summary>
    public static int Capacity(ParkWidgetSize size) => size switch
    {
        ParkWidgetSize.Small => 2,
        ParkWidgetSize.Wide => 4,
        _ => 7,
    };

    /// <summary>Journal lines under the grass: only the 4x4 has room for them.</summary>
    public static int LineCount(ParkWidgetSize size) => size == ParkWidgetSize.Large ? 2 : 0;

    public static int PanelHeight(int lines) => lines == 0 ? 0 : lines * 9 + 4;

    /// <summary>The scene's size in scene pixels for a widget of that shape and aspect.</summary>
    public static (int Width, int Height) SceneSize(ParkWidgetSize size, double aspect)
    {
        var height = size switch { ParkWidgetSize.Small => 84, ParkWidgetSize.Wide => 88, _ => 150 };
        aspect = double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.5, 4) : 1;
        return (Math.Max(48, (int)Math.Round(height * aspect)), height);
    }

    /// <summary>The half hour a film belongs to: residents, places and activities change with it.</summary>
    public static long Turn(DateTime local) => local.Ticks / ParkWidget.TurnLength.Ticks;

    /// <summary>A stable hash (string.GetHashCode changes with every process).</summary>
    public static uint Hash(string text, long salt)
    {
        var hash = 2166136261u ^ (uint)salt ^ (uint)(salt >> 32);
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        hash ^= hash >> 15; hash *= 0x2c1b3c6dU; hash ^= hash >> 12;
        return hash;
    }

    /// <summary>Who is on the grass this half hour: a window over the roster that moves on each turn.</summary>
    public static IReadOnlyList<ParkWidgetResident> Shown(IReadOnlyList<ParkWidgetResident> residents, ParkWidgetSize size, DateTime local)
    {
        var drawable = residents.Where(r => !string.IsNullOrEmpty(r.Live) || !string.IsNullOrEmpty(r.Sprite)).ToArray();
        var capacity = Capacity(size);
        if (drawable.Length <= capacity) return drawable;
        var turn = (int)(Turn(local) % drawable.Length);
        return Enumerable.Range(0, capacity).Select(i => drawable[(turn + i) % drawable.Length]).ToArray();
    }

    /// <summary>What a resident does this half hour: asleep at night, a nap or a sit in the evening, sheltering in the rain.</summary>
    public static LiveActivity ActivityFor(ParkWidgetResident resident, DateTime local)
    {
        var phase = ParkCare.PhaseAt(local);
        var weather = ParkCare.WeatherAt(local);
        if (phase == ParkDayPhase.Night) return LiveActivity.Sleep;
        // Without its animations a resident can only stand: it never slides across the grass.
        if (resident.Clips is not { Length: > 0 }) return LiveActivity.Idle;
        var roll = (int)(Hash(resident.Id, Turn(local)) % 100);
        if (weather is ParkWeather.Rain or ParkWeather.Storm)
            return roll switch { < 35 => LiveActivity.Sit, < 70 => LiveActivity.LookAround, < 85 => LiveActivity.Idle, _ => LiveActivity.Walk };
        if (phase == ParkDayPhase.Evening)
            return roll switch { < 15 => LiveActivity.Sleep, < 45 => LiveActivity.Sit, < 70 => LiveActivity.Walk, _ => LiveActivity.LookAround };
        return roll switch { < 45 => LiveActivity.Walk, < 65 => LiveActivity.LookAround, < 85 => LiveActivity.Idle, _ => LiveActivity.Sit };
    }

    /// <summary>Lays the film out on a scene of <paramref name="width"/> x <paramref name="height"/> scene pixels.</summary>
    /// <summary>The living park before the widget has read anything (see <see cref="ParkWidget.Neutral"/>).</summary>
    public static LiveParkView Neutral(ParkWidgetSize size, int width, int height) =>
        Compose(ParkWidgetSnapshot.Empty, size, width, height, new DateTime(2000, 1, 1, 12, 0, 0)) with
        {
            Phase = ParkDayPhase.Day, Weather = ParkWeather.Sunny, Lines = [ParkWidget.Mark],
        };

    public static LiveParkView Compose(ParkWidgetSnapshot snapshot, ParkWidgetSize size, int width, int height, DateTime local)
    {
        width = Math.Max(48, width); height = Math.Max(48, height);
        // An empty park says so, at every size: the friendly empty state.
        var lineCount = snapshot.Residents.Length == 0 ? Math.Max(1, LineCount(size)) : LineCount(size);
        var bottom = height - PanelHeight(lineCount) - 3;
        var horizon = size == ParkWidgetSize.Large ? (bottom * 2) / 5 : (height * 2) / 5;
        var turn = Turn(local);
        var shown = Shown(snapshot.Residents, size, local);
        var actors = new List<LiveActor>(shown.Count);
        if (size == ParkWidgetSize.Large && shown.Count > 3)
        {
            var back = shown.Count / 2;
            Row(shown.Take(back).ToArray(), horizon + (bottom - horizon) * 11 / 20);
            Row(shown.Skip(back).ToArray(), bottom);
        }
        else Row(shown, bottom);

        var characters = ParkWidget.LineCharacters(width);
        var lines = new List<string>();
        if (lineCount > 0)
        {
            if (snapshot.Residents.Length == 0) lines.Add(ParkWidget.Fit(size == ParkWidgetSize.Small ? "Empty. Tap to invite!" : EmptyLine, characters));
            else lines.AddRange(snapshot.Moments.Where(m => !string.IsNullOrWhiteSpace(m)).Take(lineCount).Select(m => ParkWidget.Fit(m, characters)));
            if (lines.Count == 0)
                lines.Add(ParkWidget.Fit(snapshot.Residents.Length == 1 ? $"{snapshot.Residents[0].Name} lives in the park" : $"{snapshot.Residents.Length} Pokémon live in the park", characters));
        }
        var title = size == ParkWidgetSize.Small ? "Park" : "Poképark";
        return new(size, width, height, ParkCare.PhaseAt(local), ParkCare.WeatherAt(local), horizon, actors, lines, title, width * 31 + height);

        void Row(IReadOnlyList<ParkWidgetResident> row, int feetY)
        {
            for (var i = 0; i < row.Count; i++)
            {
                var hash = Hash(row[i].Id, turn);
                // Each resident in the middle of its own share of the width, nudged a little each turn;
                // a walker's whole path stays inside its share.
                var share = width / Math.Max(1, row.Count);
                var nudge = share / 6 == 0 ? 0 : (int)(hash % (uint)(share / 3 + 1)) - share / 6;
                var x = Math.Clamp((2 * i + 1) * width / (2 * row.Count) + nudge, 10, width - 10);
                actors.Add(new(row[i], ActivityFor(row[i], local), x, feetY - (int)((hash >> 8) % 3), (hash >> 4 & 1) == 0));
            }
        }
    }

    /// <summary>
    /// Where a resident is and what it plays in frame <paramref name="frame"/> of <paramref name="frames"/>.
    /// The loop is seamless: a walker walks a third of the film one way, looks about, walks back, looks about.
    /// </summary>
    public static LivePose Pose(LiveActor actor, int frame, int frames)
    {
        frames = Math.Max(1, frames);
        frame = ((frame % frames) + frames) % frames;
        var phase = (double)frame / frames;
        var dir = actor.Rightward ? 1 : -1;
        var idleAhead = actor.Rightward ? IdleSouthEast : IdleSouthWest;
        var idleBack = actor.Rightward ? IdleSouthWest : IdleSouthEast;
        switch (actor.Activity)
        {
            case LiveActivity.Walk:
            {
                var leg = Math.Max(1, frames / 3);
                var half = frames / 2;
                var start = actor.X - leg * Stride * dir / 2;
                if (frame < leg) return new(actor.Rightward ? WalkEast : WalkWest, phase, start + frame * Stride * dir, actor.FeetY, false);
                if (frame < half) return new(idleAhead, phase, start + leg * Stride * dir, actor.FeetY, false);
                if (frame < half + leg) return new(actor.Rightward ? WalkWest : WalkEast, phase, start + (leg - (frame - half)) * Stride * dir, actor.FeetY, false);
                return new(idleBack, phase, start, actor.FeetY, false);
            }
            case LiveActivity.LookAround:
            {
                var clip = (frame * 3 / frames) switch { 0 => idleBack, 1 => IdleSouth, _ => idleAhead };
                return new(clip, phase, actor.X, actor.FeetY, false);
            }
            case LiveActivity.Sit: return new(Sit, phase, actor.X, actor.FeetY, false);
            case LiveActivity.Sleep: return new(Sleep, phase, actor.X, actor.FeetY, true);
            default: return new(IdleSouth, phase, actor.X, actor.FeetY, false);
        }
    }

    /// <summary>A clip a sheet holds, or its nearest stand-in (a sit or a sleep the species has no art for stands idle).</summary>
    public static ParkLiveClip? Resolve(ParkLiveClip[]? clips, string name)
    {
        if (clips is not { Length: > 0 }) return null;
        ParkLiveClip? Find(string n) => Array.Find(clips, c => c.Name == n && c.Frames.Length > 0);
        return Find(name)
            ?? (name is IdleSouthEast or IdleSouthWest or Sit or Sleep ? Find(IdleSouth) : null)
            ?? (name == WalkWest ? Find(WalkEast) : name == WalkEast ? Find(WalkWest) : null)
            ?? Find(IdleSouth) ?? Array.Find(clips, c => c.Frames.Length > 0);
    }

    /// <summary>
    /// The clip's frame at a point of the loop. The clip runs a whole number of times per loop (its own
    /// pace, stretched a little), so the film's last frame flows into its first.
    /// </summary>
    public static ParkLiveFrame FrameAt(ParkLiveClip clip, double phase, int loopMs)
    {
        var total = clip.TotalMs;
        var cycles = Math.Max(1, (int)Math.Round((double)Math.Max(1, loopMs) / total));
        var time = (int)(phase * cycles * total) % total;
        foreach (var frame in clip.Frames)
        {
            var ms = Math.Max(1, frame.Ms);
            if (time < ms) return frame;
            time -= ms;
        }
        return clip.Frames[^1];
    }

    /// <summary>
    /// How many frames and how large a scale fit the launcher's bitmap budget: the most frames
    /// (down to <see cref="MinFrames"/>) at the largest whole scale of at least 2, else at scale 1.
    /// The scale never goes past what the widget shows (<paramref name="maxScale"/>).
    /// </summary>
    public static (int Frames, int Scale) Budget(int sceneWidth, int sceneHeight, int maxScale, long budgetBytes)
    {
        maxScale = Math.Max(1, maxScale);
        long Bytes(int frames, int scale) => (long)frames * sceneWidth * scale * sceneHeight * scale * 4;
        for (var scale = maxScale; scale >= 2; scale--)
            if (Bytes(Frames, scale) <= budgetBytes) return (Frames, scale);
        for (var frames = Frames; frames >= MinFrames; frames -= 2)
            if (Bytes(frames, 2) <= budgetBytes && maxScale >= 2) return (frames, 2);
        for (var frames = Frames; frames >= MinFrames; frames -= 2)
            if (Bytes(frames, 1) <= budgetBytes) return (frames, 1);
        return (1, 1);
    }
}
