namespace PKForge.Domain;

/// <summary>How the two sides of one compared value relate.</summary>
public enum CompareEdge
{
    /// <summary>At least one side does not store it: nothing to compare.</summary>
    None,
    /// <summary>Both sides hold the same value.</summary>
    Same,
    /// <summary>Different, with no "higher" (a nature, a ball, a move).</summary>
    Differs,
    /// <summary>The left side is higher.</summary>
    Left,
    /// <summary>The right side is higher.</summary>
    Right,
}

/// <summary>One text row: the label, each side's value ("—" when its format does not store it).</summary>
public sealed record CompareRow(string Label, string Left, string Right, CompareEdge Edge)
{
    public bool Differs => Edge is CompareEdge.Differs or CompareEdge.Left or CompareEdge.Right;
}

/// <summary>One numeric pair drawn as bars: each side's value (null = not stored) and the
/// value its bar is measured against (a format's own IV/EV cap, or a shared scale).</summary>
public sealed record CompareBar(string Label, int? Left, int? Right, int LeftMax, int RightMax, CompareEdge Edge,
    int LeftNature = 0, int RightNature = 0)
{
    public float LeftFraction => Fraction(Left, LeftMax);
    public float RightFraction => Fraction(Right, RightMax);

    private static float Fraction(int? value, int max) =>
        value is { } v && max > 0 ? Math.Clamp(v / (float)max, 0f, 1f) : 0f;
}

/// <summary>A titled block of the comparison: text rows or paired bars.</summary>
public sealed record CompareSection(string Title, IReadOnlyList<CompareRow> Rows, IReadOnlyList<CompareBar> Bars)
{
    public int Differences => Rows.Count(r => r.Differs) + Bars.Count(b => b.Edge is CompareEdge.Left or CompareEdge.Right);
}

/// <summary>Who one side is: the header above its column.</summary>
public sealed record CompareHead(string Name, string Species, string Form, bool Shiny, int Gender, int Level,
    string Where, string Format, int Generation, SpriteLook Look, bool? Legal);

/// <summary>Two Pokémon side by side, from any two formats (a save slot and a Bank entry,
/// Gen 3 against Gen 8): every value read from each one's own summary, nothing invented.</summary>
public sealed record MonComparison(CompareHead Left, CompareHead Right, IReadOnlyList<CompareSection> Sections)
{
    /// <summary>What a side shows for a value its format does not keep.</summary>
    public const string Missing = "—";

    public static readonly IReadOnlyList<string> StatLabels = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    public int Differences => Sections.Sum(s => s.Differences);

    /// <summary>Compares <paramref name="left"/> with <paramref name="right"/>; the where-strings
    /// say where each lives ("Emerald · Box 03", "Bank · Box 01").</summary>
    public static MonComparison Build(MonSummary left, string leftWhere, MonSummary right, string rightWhere)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new MonComparison(Head(left, leftWhere), Head(right, rightWhere),
        [
            new("Profile", Profile(left, right), []),
            new("Stats", [], Stats(left, right, s => s.Stats, (_, _) => null, nature: true, total: true)),
            new("Base stats", [], Stats(left, right, s => s.BaseStats, (_, _) => 255, nature: false, total: true)),
            new("IVs", [], Stats(left, right, s => s.IVs, (s, _) => s.Caps.IvMax, nature: false, total: false, comparable: left.Caps.IvMax == right.Caps.IvMax)),
            new("EVs", [], Stats(left, right, s => s.EVs, (s, _) => s.Caps.EvMax, nature: false, total: false, comparable: left.Caps.EvMax == right.Caps.EvMax)),
            new("Moves", Moves(left, right), []),
            new("Origin", Origin(left, right), []),
        ]);
    }

    private static CompareHead Head(MonSummary s, string where) =>
        new(s.DisplayName, s.SpeciesName, s.FormName, s.IsShiny, s.Gender, s.Level, where, s.Format, s.Generation, s.Look, s.Legal);

    private static List<CompareRow> Profile(MonSummary l, MonSummary r) =>
    [
        Text("Species", l.SpeciesName, r.SpeciesName),
        Text("Form", Form(l), Form(r)),
        Text("Shiny", l.IsShiny ? "Shiny" : "No", r.IsShiny ? "Shiny" : "No"),
        Number("Level", l.Level, r.Level),
        Text("Nature", l.NatureName, r.NatureName),
        Text("Ability", l.AbilityName, r.AbilityName),
        Text("Held item", HeldItem(l), HeldItem(r)),
        Text("Ball", Ball(l), Ball(r)),
    ];

    private static List<CompareRow> Moves(MonSummary l, MonSummary r)
    {
        var rows = new List<CompareRow>(4);
        var leftSet = l.Moves.Select(m => m.Id).ToHashSet();
        var rightSet = r.Moves.Select(m => m.Id).ToHashSet();
        for (var i = 0; i < 4; i++)
        {
            var a = i < l.Moves.Count ? l.Moves[i] : null;
            var b = i < r.Moves.Count ? r.Moves[i] : null;
            // A move both know counts as shared wherever it sits in the four slots.
            var edge = a is null && b is null ? CompareEdge.Same
                : a is not null && b is not null && rightSet.Contains(a.Id) && leftSet.Contains(b.Id) ? CompareEdge.Same
                : CompareEdge.Differs;
            rows.Add(new($"Move {i + 1}", a?.Name ?? Missing, b?.Name ?? Missing, edge));
        }
        return rows;
    }

    private static List<CompareRow> Origin(MonSummary l, MonSummary r) =>
    [
        Text("OT", Blank(l.OriginalTrainer), Blank(r.OriginalTrainer)),
        Text("TID", l.TrainerId?.ToString(System.Globalization.CultureInfo.InvariantCulture), r.TrainerId?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Text("Origin game", Blank(l.Met?.VersionName), Blank(r.Met?.VersionName)),
        Text("Met at", Blank(l.Met?.MetLocationName), Blank(r.Met?.MetLocationName)),
        Text("Met on", MetDate(l), MetDate(r)),
        Number("Ribbons", Ribbons(l), Ribbons(r)),
        Text("Legality", Verdict(l.Legal), Verdict(r.Legal)),
    ];

    private static List<CompareBar> Stats(MonSummary l, MonSummary r, Func<MonSummary, IReadOnlyList<int>> pick,
        Func<MonSummary, int, int?> cap, bool nature, bool total, bool comparable = true)
    {
        var a = pick(l);
        var b = pick(r);
        var bars = new List<CompareBar>(7);
        for (var i = 0; i < 6; i++)
        {
            int? x = a.Count == 6 ? a[i] : null;
            int? y = b.Count == 6 ? b[i] : null;
            // Final stats share one scale, a little above the larger, so both bars read against each other.
            var shared = (int)MathF.Ceiling(Math.Max(x ?? 0, y ?? 0) * 1.15f);
            bars.Add(new(StatLabels[i], x, y, cap(l, i) ?? shared, cap(r, i) ?? shared,
                comparable ? Edge(x, y) : CompareEdge.None,
                nature ? NatureMark(l, i) : 0, nature ? NatureMark(r, i) : 0));
        }
        if (total)
        {
            int? x = a.Count == 6 ? a.Sum() : null;
            int? y = b.Count == 6 ? b.Sum() : null;
            var shared = (int)MathF.Ceiling(Math.Max(x ?? 0, y ?? 0) * 1.15f);
            bars.Add(new("Total", x, y, shared, shared, Edge(x, y)));
        }
        return bars;
    }

    private static int NatureMark(MonSummary s, int stat) => s.NatureUp == stat && s.NatureDown != stat ? 1 : s.NatureDown == stat && s.NatureUp != stat ? -1 : 0;

    private static CompareRow Text(string label, string? left, string? right)
    {
        var a = Blank(left);
        var b = Blank(right);
        var edge = a == Missing || b == Missing ? CompareEdge.None
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? CompareEdge.Same : CompareEdge.Differs;
        return new(label, a, b, edge);
    }

    private static CompareRow Number(string label, int? left, int? right) =>
        new(label, left?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Missing,
            right?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Missing, Edge(left, right));

    /// <summary>Which of two numbers is higher; None when either is not stored.</summary>
    public static CompareEdge Edge(int? left, int? right) => (left, right) switch
    {
        ({ } a, { } b) when a > b => CompareEdge.Left,
        ({ } a, { } b) when a < b => CompareEdge.Right,
        ({ }, { }) => CompareEdge.Same,
        _ => CompareEdge.None,
    };

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? Missing : value;

    private static string Form(MonSummary s) => s.FormName is { Length: > 0 } f ? f : s.Form == 0 ? "Base" : $"#{s.Form}";

    // Gen 1 keeps no held item (that byte is the catch rate).
    private static string HeldItem(MonSummary s) => s.Generation <= 1 ? Missing : s.HeldItemName ?? "None";

    // Before Gen 3 the ball is not stored (Gen 2 Crystal keeps only some met data, no ball).
    private static string Ball(MonSummary s) => s.Generation < 3 && s.Ball <= 0 ? Missing : Blank(s.BallName);

    private static string MetDate(MonSummary s) => s.Met is { SupportsMetDate: true, MetDate: { Length: > 0 } date } ? date : Missing;

    // Ribbons arrived in Gen 3; a romhack that answers nothing keeps the dash too.
    private static int? Ribbons(MonSummary s) => s.Generation < 3 ? null : s.RibbonCount;

    private static string Verdict(bool? legal) => legal switch { true => "Legal", false => "Illegal", null => Missing };
}

/// <summary>
/// The two-step pick behind "Compare": the first chosen Pokémon waits here (from a save or
/// the Bank, any generation) until a second one is chosen, then both are handed over.
/// </summary>
public sealed class ComparePick<T> where T : class
{
    private (T Item, string Label, string? Place)? _first;

    /// <summary>The waiting first pick's label, or null when none waits.</summary>
    public string? Waiting => _first?.Label;

    /// <summary>Whether the waiting first pick is the Pokémon at <paramref name="place"/>
    /// (a save slot or a Bank entry, as the caller names it).</summary>
    public bool Holds(string place) => _first?.Place is { } held && held == place;

    /// <summary>Holds <paramref name="item"/> as the first pick when none waits, and returns
    /// null; otherwise returns the waiting one (and clears it) to compare with <paramref name="item"/>.</summary>
    public T? Choose(T item, string label, string? place = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_first is { } first)
        {
            _first = null;
            return first.Item;
        }
        _first = (item, label, place);
        return null;
    }

    public void Clear() => _first = null;
}
