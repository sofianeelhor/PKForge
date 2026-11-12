namespace PKForge.Domain;

/// <summary>The three home-screen widgets beside the Poképark: the Pocket Pokéwalker, the partner, the egg.</summary>
public enum HomeWidgetKind { Walker, Partner, Egg }

/// <summary>A widget layout: 2x2, 4x2 or 4x4 cells.</summary>
public enum HomeWidgetSizeHint { Small, Wide, Large }

/// <summary>Every home-screen widget PKForge offers, each with its own size choice.</summary>
public enum WidgetSlot { Walker, Partner, Egg, Park, LivePark }

/// <summary>
/// What the player chose in the Widgets menu, kept in the app's files (never in a save). Every
/// field is optional: a widget with nothing chosen shows a friendly empty state.
/// </summary>
/// <param name="Partner">The partner Pokémon, or null.</param>
/// <param name="EggSave">The save whose eggs the egg widget follows; null follows the save last opened.</param>
/// <param name="WalkerBigNumber">The Pokéwalker's big number: steps today (true) or steps this walk.</param>
/// <param name="WalkerAlert">Whether the Pokéwalker shows "!" when a Radar or Dowsing can be afforded.</param>
/// <remarks>
/// The layouts are null for "Auto": the widget follows the launcher's tile, as it always did. They
/// replace the old WalkerSize/PartnerSize/EggSize fields, which only ever sized the menu's preview:
/// those are ignored when read, so a widget nobody sized keeps fitting its launcher.
/// </remarks>
public sealed record HomeWidgetSettings(
    PartnerChoice? Partner = null,
    string? EggSave = null,
    bool WalkerBigNumber = true,
    bool WalkerAlert = true,
    HomeWidgetSizeHint? WalkerLayout = null,
    HomeWidgetSizeHint? PartnerLayout = null,
    HomeWidgetSizeHint? EggLayout = null,
    HomeWidgetSizeHint? ParkLayout = null,
    HomeWidgetSizeHint? LiveParkLayout = null)
{
    /// <summary>The layout chosen for a widget, or null for Auto (fit the launcher).</summary>
    public HomeWidgetSizeHint? LayoutOf(WidgetSlot slot) => slot switch
    {
        WidgetSlot.Walker => WalkerLayout,
        WidgetSlot.Partner => PartnerLayout,
        WidgetSlot.Egg => EggLayout,
        WidgetSlot.Park => ParkLayout,
        _ => LiveParkLayout,
    };

    public HomeWidgetSettings WithLayout(WidgetSlot slot, HomeWidgetSizeHint? layout) => slot switch
    {
        WidgetSlot.Walker => this with { WalkerLayout = layout },
        WidgetSlot.Partner => this with { PartnerLayout = layout },
        WidgetSlot.Egg => this with { EggLayout = layout },
        WidgetSlot.Park => this with { ParkLayout = layout },
        _ => this with { LiveParkLayout = layout },
    };

    public static WidgetSlot SlotOf(HomeWidgetKind kind) => kind switch
    {
        HomeWidgetKind.Walker => WidgetSlot.Walker,
        HomeWidgetKind.Partner => WidgetSlot.Partner,
        _ => WidgetSlot.Egg,
    };

    /// <summary>The settings as kept in the app's files.</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

    /// <summary>Reads the kept settings; anything unreadable gives the defaults (never throws).</summary>
    public static HomeWidgetSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new(); }
        catch (NotSupportedException) { return new(); }
    }
}

/// <summary>Launcher cells for each size hint, and the dp a launcher gives them (about 70 dp a cell, less 30).</summary>
public static class HomeWidgetSizes
{
    public static (int Columns, int Rows) Cells(HomeWidgetSizeHint size) => size switch
    {
        HomeWidgetSizeHint.Small => (2, 2),
        HomeWidgetSizeHint.Wide => (4, 2),
        _ => (4, 4),
    };

    public static (int WidthDp, int HeightDp) Dp(HomeWidgetSizeHint size)
    {
        var (c, r) = Cells(size);
        return (c * 70 - 30, r * 70 - 30);
    }

    public static string Label(HomeWidgetSizeHint size)
    {
        var (c, r) = Cells(size);
        return $"{c}x{r}";
    }

    /// <summary>The size menu's cycle: Auto, then 2x2, 4x2, 4x4, and back to Auto.</summary>
    public static HomeWidgetSizeHint? Next(HomeWidgetSizeHint? size) => size switch
    {
        null => HomeWidgetSizeHint.Small,
        HomeWidgetSizeHint.Small => HomeWidgetSizeHint.Wide,
        HomeWidgetSizeHint.Wide => HomeWidgetSizeHint.Large,
        _ => null,
    };

    /// <summary>"Auto" or the cells, for the menu.</summary>
    public static string Label(HomeWidgetSizeHint? size) => size is { } s ? Label(s) : "Auto";

    /// <summary>The same shapes the Poképark widget uses: under 200 dp wide is small, over 220 dp tall is large.</summary>
    public static HomeWidgetSizeHint For(int widthDp, int heightDp) => ParkWidget.SizeFor(widthDp, heightDp) switch
    {
        ParkWidgetSize.Small => HomeWidgetSizeHint.Small,
        ParkWidgetSize.Wide => HomeWidgetSizeHint.Wide,
        _ => HomeWidgetSizeHint.Large,
    };
}

/// <summary>
/// Where a widget's chosen layout stands in the launcher's tile. <see cref="Fills"/>: the layout is
/// drawn at the tile's own size (Auto, or a tile already of that shape). Otherwise the layout is
/// drawn at its natural size times <see cref="Step"/>, a whole number, centred in the tile with the
/// widget's own background around it: never stretched.
/// </summary>
public readonly record struct WidgetFit(HomeWidgetSizeHint Size, bool Fills, int Step);

/// <summary>The size a widget's layout takes in a launcher tile, from the player's choice.</summary>
public static class WidgetLayout
{
    /// <summary>The Poképark widgets' name for the same layout.</summary>
    public static ParkWidgetSize ParkSize(HomeWidgetSizeHint size) => size switch
    {
        HomeWidgetSizeHint.Small => ParkWidgetSize.Small,
        HomeWidgetSizeHint.Wide => ParkWidgetSize.Wide,
        _ => ParkWidgetSize.Large,
    };

    /// <summary>The layout drawn: the one chosen, or with Auto the tile's own shape.</summary>
    public static HomeWidgetSizeHint Pick(HomeWidgetSizeHint? chosen, int tileWidthDp, int tileHeightDp) =>
        chosen ?? HomeWidgetSizes.For(tileWidthDp, tileHeightDp);

    /// <summary>
    /// How the layout goes in a tile of <paramref name="tileWidth"/> x <paramref name="tileHeight"/>
    /// (dp or pixels, the same unit as the natural size): <paramref name="naturalWidth"/> x
    /// <paramref name="naturalHeight"/> is the chosen layout's size at a step of one.
    /// </summary>
    public static WidgetFit Fit(HomeWidgetSizeHint? chosen, int tileWidthDp, int tileHeightDp,
        int tileWidth, int tileHeight, int naturalWidth, int naturalHeight)
    {
        var size = Pick(chosen, tileWidthDp, tileHeightDp);
        if (chosen is null || chosen == HomeWidgetSizes.For(tileWidthDp, tileHeightDp)) return new(size, true, 1);
        naturalWidth = Math.Max(1, naturalWidth); naturalHeight = Math.Max(1, naturalHeight);
        var step = Math.Max(1, Math.Min(tileWidth / naturalWidth, tileHeight / naturalHeight));
        return new(size, false, step);
    }

    /// <summary>
    /// The picture that holds a painted layout of <paramref name="pictureWidth"/> x <paramref name="pictureHeight"/>
    /// in a tile of that proportion, at the layout's own pixel scale: the layout centred at
    /// (<c>X</c>, <c>Y</c>), the rest for the background.
    /// </summary>
    public static (int Width, int Height, int X, int Y) Canvas(int pictureWidth, int pictureHeight, int tileWidth, int tileHeight)
    {
        tileWidth = Math.Max(1, tileWidth); tileHeight = Math.Max(1, tileHeight);
        var width = Math.Max(pictureWidth, (int)Math.Round(pictureHeight * (double)tileWidth / tileHeight));
        var height = Math.Max(pictureHeight, (int)Math.Round(pictureWidth * (double)tileHeight / tileWidth));
        return (width, height, (width - pictureWidth) / 2, (height - pictureHeight) / 2);
    }
}

// ── The Pocket Pokéwalker ────────────────────────────────────────────────────────────────

/// <summary>Where today's pedometer steps are counted from: the step counter's first reading of the day.</summary>
public sealed record WalkerDayCount(DateOnly Day, long Anchor, long Last, long Carried = 0);

/// <summary>Everything the Pokéwalker widget shows, worked out from the walk file without writing it.</summary>
public sealed record WalkerWidgetView(
    bool Walking, string Name, int Species, int Form, bool Shiny, int Course, string CourseName,
    int WalkSteps, long TodaySteps, int Watts, int Catches, int Items, bool CanRadar, bool CanDowse, bool Pedometer)
{
    public bool Alert => CanRadar || CanDowse;

    public static WalkerWidgetView Idle(int watts = 0) => new(false, "", 0, 0, false, -1, "", 0, 0, watts, 0, 0, false, false, false);
}

/// <summary>
/// The pure half of the Pokéwalker widget. The widget reads the walk file and, when the player
/// allowed it, the step counter once per update; it counts the new steps in memory the same way
/// the Pokéwalker does (<see cref="WalkerRules"/>) and never writes the file: the next time the
/// Pokéwalker opens it counts them for real from the same anchors.
/// </summary>
public static class WalkerWidget
{
    /// <summary>The walk as it stands now: the pedometer's new steps or stroll mode's, counted but not saved.</summary>
    public static WalkerState Project(WalkerState state, long? sensorNow, DateTimeOffset now)
    {
        if (state.Walk is null) return state;
        if (state.SensorLast is not null)
            return sensorNow is { } reading
                ? WalkerRules.AddSteps(state, (int)Math.Min(int.MaxValue, WalkerRules.SensorDelta(state.SensorLast, reading)))
                : state;
        return WalkerRules.Stroll(state, now);
    }

    /// <summary>
    /// Today's steps from the step counter, which counts from boot: the first reading of a day is
    /// its anchor; a lower reading means the phone restarted, so what was counted before is carried.
    /// </summary>
    public static (WalkerDayCount Count, long Today) Today(WalkerDayCount? previous, long reading, DateOnly day)
    {
        if (previous is null || previous.Day != day) return (new WalkerDayCount(day, reading, reading), 0);
        var carried = previous.Carried;
        var anchor = previous.Anchor;
        if (reading < previous.Last)
        {
            carried += previous.Last - anchor;
            anchor = 0;
        }
        var count = previous with { Anchor = anchor, Last = reading, Carried = carried };
        return (count, carried + reading - anchor);
    }

    /// <summary>Whether a Radar search could start now (the Pokéwalker would accept it).</summary>
    public static bool CanRadar(WalkerState state)
    {
        if (state.Walk is not { } walk || state.Watts < WalkerRules.RadarCost || walk.Catches.Count >= WalkerRules.MaxHeld) return false;
        if (WalkerCourses.Find(walk.Course) is not { } course) return false;
        return walk.Slots.Any(s => walk.Steps >= WalkerRules.StepsFor(course, s, walk.Advantaged));
    }

    /// <summary>Whether Dowsing could start now.</summary>
    public static bool CanDowse(WalkerState state)
    {
        if (state.Walk is not { } walk || state.Watts < WalkerRules.DowsingCost || walk.Items.Count >= WalkerRules.MaxHeld) return false;
        return WalkerCourses.Find(walk.Course) is { } course && course.Items.Any(i => walk.Steps >= i.Steps);
    }

    /// <summary>The widget's view of a projected walk; <paramref name="pedometerToday"/> is null in stroll mode.</summary>
    public static WalkerWidgetView View(WalkerState projected, long? pedometerToday, DateOnly today)
    {
        if (projected.Walk is not { } walk) return WalkerWidgetView.Idle(projected.Watts);
        var stroll = projected.StrollDay == today ? projected.StrollToday : 0;
        var course = WalkerCourses.Find(walk.Course);
        return new WalkerWidgetView(true, walk.Name, walk.Species, walk.Form, walk.Shiny, walk.Course, course?.Name ?? "",
            walk.Steps, pedometerToday ?? stroll, projected.Watts, walk.Catches.Count, walk.Items.Count,
            CanRadar(projected), CanDowse(projected), projected.SensorLast is not null);
    }
}

// ── The partner ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The partner the player picked, as it was when picked: enough to draw it and open its summary
/// without its save open. <see cref="Data"/> is a copy of its bytes (base64), read only.
/// </summary>
public sealed record PartnerChoice(
    string Name, int Species, int Form, bool Shiny, int Level, string Origin,
    Guid? BankId = null, string? DocumentId = null, string? Format = null, string? Data = null);

/// <summary>How the partner feels, from the clock and the battery.</summary>
public enum PartnerMood { Awake, Happy, Sleepy, Asleep, Tired }

public static class PartnerWidget
{
    /// <summary>At or under this battery level (and not charging) the partner is tired.</summary>
    public const int TiredBattery = 15;

    /// <summary>Night (22:00-6:00) puts it to sleep; a low battery tires it; mornings and late evenings
    /// it is drowsy; around midday it is at its liveliest.</summary>
    public static PartnerMood MoodAt(DateTime local, int? battery, bool charging)
    {
        var hour = local.Hour;
        if (hour >= 22 || hour < 6) return PartnerMood.Asleep;
        if (battery is { } level && level <= TiredBattery && !charging) return PartnerMood.Tired;
        if (hour < 8 || hour >= 20) return PartnerMood.Sleepy;
        if (hour is >= 11 and < 15) return PartnerMood.Happy;
        return PartnerMood.Awake;
    }

    public static string Line(PartnerChoice partner, PartnerMood mood, int? battery) => mood switch
    {
        PartnerMood.Asleep => $"{partner.Name} is fast asleep.",
        PartnerMood.Tired => battery is { } level ? $"{partner.Name} is tired. Battery {level}%." : $"{partner.Name} is tired.",
        PartnerMood.Sleepy => $"{partner.Name} is a little drowsy.",
        PartnerMood.Happy => $"{partner.Name} is full of energy!",
        _ => $"{partner.Name} is by your side.",
    };

    public const string EmptyLine = "No partner yet. Pick one in PKForge.";
}

// ── The egg ─────────────────────────────────────────────────────────────────────────────────

/// <summary>An egg in a save's party: its cycles left, the range a real egg of it can hold, and about how many steps.</summary>
public sealed record EggWidgetEgg(string Species, int SpeciesId, int Cycles, int MinCycles, int MaxCycles, int StepsLeft);

/// <summary>What the app last read of one save's eggs and Day Care.</summary>
public sealed record EggWidgetSnapshot(
    string DocumentId, string SaveName, bool HasDaycare, bool EggWaiting, string[] Parents, EggWidgetEgg[] Eggs, DateTimeOffset WrittenAt);

/// <summary>How close an egg is, as the games' summary words it, and the "ready to hatch" state.</summary>
public enum EggStage { None, Waiting, Long, Slow, Moving, Soon, Ready }

/// <summary>Everything the egg widget shows.</summary>
public sealed record EggWidgetView(EggStage Stage, string Title, string Line, string Detail, float Progress, string SaveName);

public static class EggWidget
{
    /// <summary>A count with comma thousands, whatever the phone's language: the pixel font has no narrow space.</summary>
    public static string Num(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The egg nearest hatching in the party, or null.</summary>
    public static EggWidgetEgg? Nearest(EggWidgetSnapshot? snapshot) =>
        snapshot?.Eggs.OrderBy(e => e.Cycles - e.MinCycles).ThenBy(e => e.StepsLeft).FirstOrDefault();

    /// <summary>The games' thresholds: over 40 cycles a long time, 11-40 not close, 6-10 moving, 5 or fewer
    /// sounds inside; at the lowest counter a real egg can hold, the next cycle hatches it.</summary>
    public static EggStage StageOf(EggWidgetEgg egg)
    {
        var left = egg.Cycles - egg.MinCycles;
        return left <= 0 ? EggStage.Ready : egg.Cycles <= 5 ? EggStage.Soon : egg.Cycles <= 10 ? EggStage.Moving : egg.Cycles <= 40 ? EggStage.Slow : EggStage.Long;
    }

    /// <summary>0 for a fresh egg, 1 for one about to hatch.</summary>
    public static float Progress(EggWidgetEgg egg)
    {
        var span = egg.MaxCycles - egg.MinCycles;
        if (span <= 0) return egg.Cycles <= egg.MinCycles ? 1f : 0f;
        return Math.Clamp(1f - (egg.Cycles - egg.MinCycles) / (float)span, 0f, 1f);
    }

    /// <summary>The egg's wobble frames (the artist's egg frame names) and how long each stays, in ms.</summary>
    public static (string[] Frames, int IntervalMs) Motion(EggStage stage) => stage switch
    {
        EggStage.Ready => (["crack-mid", "crack-left", "crack-mid", "crack-right"], 450),
        EggStage.Soon => (["wobble-mid", "wobble-left", "wobble-mid", "wobble-right"], 350),
        EggStage.Moving => (["wobble-mid", "wobble-left", "wobble-mid", "wobble-right"], 700),
        EggStage.Slow => (["wobble-mid", "wobble-mid", "wobble-left", "wobble-mid"], 1100),
        EggStage.Waiting => (["wobble-mid", "wobble-mid", "wobble-right", "wobble-mid"], 1400),
        _ => (["wobble-mid", "wobble-mid", "wobble-mid", "wobble-mid"], 4000),
    };

    public const string NoSaveLine = "Open a save in PKForge.";

    public static EggWidgetView View(EggWidgetSnapshot? snapshot)
    {
        if (snapshot is null)
            return new(EggStage.None, "Egg", "No egg yet", NoSaveLine, 0f, "");
        if (Nearest(snapshot) is { } egg)
        {
            var stage = StageOf(egg);
            var steps = egg.StepsLeft > 0 ? $"~{Num(egg.StepsLeft)} steps" : "any step now";
            var line = stage switch
            {
                EggStage.Ready => "Ready to hatch!",
                EggStage.Soon => "Sounds come from inside!",
                EggStage.Moving => "It moves now and then.",
                EggStage.Slow => "It's not close yet.",
                _ => "It will take a long time.",
            };
            var cycles = $"{egg.Cycles} {(egg.Cycles == 1 ? "cycle" : "cycles")}";
            var detail = stage == EggStage.Ready ? "Walk a little in your game." : $"{cycles} · {steps}";
            var more = snapshot.Eggs.Length > 1 ? $" ({snapshot.Eggs.Length} eggs)" : "";
            return new(stage, $"{egg.Species} egg{more}", line, detail, Progress(egg), snapshot.SaveName);
        }
        if (snapshot.EggWaiting)
            return new(EggStage.Waiting, "Day Care", "An egg is waiting!",
                snapshot.Parents.Length == 2 ? $"From {snapshot.Parents[0]} and {snapshot.Parents[1]}." : "Pick it up in your game.", 0f, snapshot.SaveName);
        if (snapshot.Parents.Length > 0)
            return new(EggStage.None, "Day Care", snapshot.Parents.Length == 2 ? $"{snapshot.Parents[0]} and {snapshot.Parents[1]}" : $"{snapshot.Parents[0]} is staying",
                "No egg yet. Keep walking!", 0f, snapshot.SaveName);
        return new(EggStage.None, "Egg", "No egg right now", snapshot.HasDaycare ? "Leave two Pokémon at the Day Care." : "No egg in this save's party.", 0f, snapshot.SaveName);
    }
}
