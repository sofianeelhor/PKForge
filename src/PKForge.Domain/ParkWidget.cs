namespace PKForge.Domain;

/// <summary>The three home-screen widget shapes: 2x2, 4x2 and 4x4 cells (and anything between).</summary>
public enum ParkWidgetSize { Small, Wide, Large }

/// <summary>
/// One resident as the widget remembers it; <see cref="Sprite"/> is a file name next to the snapshot.
/// The living park also reads <see cref="Live"/>, a sheet of the resident's PMD frames next to it,
/// cut into <see cref="Clips"/> (see <see cref="LivePark"/>), drawn at <see cref="Scale"/>.
/// Older snapshots have neither: the resident then stands still.
/// </summary>
public sealed record ParkWidgetResident(string Id, string Name, int Species, int Form, bool Shiny, string? Sprite,
    string? Live = null, ParkLiveClip[]? Clips = null, float Scale = 1);

/// <summary>What the app last wrote for the widget: the residents and the newest park moments.</summary>
public sealed record ParkWidgetSnapshot(ParkWidgetResident[] Residents, string[] Moments, DateTimeOffset WrittenAt)
{
    public static ParkWidgetSnapshot Empty { get; } = new([], [], DateTimeOffset.MinValue);
}

/// <summary>A resident placed on the widget's grass, in low-resolution scene pixels (feet position).</summary>
public sealed record ParkWidgetPlacement(ParkWidgetResident Resident, int FeetX, int FeetY, bool FacingLeft);

/// <summary>Everything the widget draws, worked out from the snapshot and the clock alone.</summary>
public sealed record ParkWidgetView(
    ParkWidgetSize Size, int Width, int Height, ParkDayPhase Phase, ParkWeather Weather,
    int HorizonY, IReadOnlyList<ParkWidgetPlacement> Placements, IReadOnlyList<string> Lines, string Title);

/// <summary>
/// The pure half of the Poképark widget: which shape a launcher cell is, who stands on the grass
/// (a few residents taking turns every half hour, so the widget is alive without the app open),
/// where they stand, and which lines of the park's journal fit under them.
/// </summary>
public static class ParkWidget
{
    /// <summary>The widget refreshes itself on this period; residents take turns on the same beat.</summary>
    public static readonly TimeSpan TurnLength = TimeSpan.FromMinutes(30);

    public const string EmptyLine = "The park is empty. Tap to invite Pokémon!";

    /// <summary>A launcher cell is about 70-100 dp; 2 cells wide is under 200 dp, 4 cells tall is over 220 dp.</summary>
    public static ParkWidgetSize SizeFor(int widthDp, int heightDp) =>
        widthDp < 200 ? ParkWidgetSize.Small : heightDp >= 220 ? ParkWidgetSize.Large : ParkWidgetSize.Wide;

    /// <summary>The mark a widget wears before it has read anything.</summary>
    public const string Mark = "PKForge";

    /// <summary>
    /// The park before the widget has read anything: the grass under a clear day sky, the title and
    /// the PKForge mark, nobody and no journal line. A host that photographs the widget once (Cocoon)
    /// keeps its first picture, so that picture never claims the park is empty.
    /// </summary>
    public static ParkWidgetView Neutral(ParkWidgetSize size, int width, int height) =>
        Compose(ParkWidgetSnapshot.Empty, size, width, height, new DateTime(2000, 1, 1, 12, 0, 0)) with
        {
            Phase = ParkDayPhase.Day, Weather = ParkWeather.Sunny, Lines = [Mark],
        };

    /// <summary>How many residents stand on the grass for a widget shape.</summary>
    public static int Capacity(ParkWidgetSize size) => size switch
    {
        ParkWidgetSize.Small => 2,
        ParkWidgetSize.Wide => 4,
        _ => 6
    };

    /// <summary>How many journal lines fit under the scene.</summary>
    public static int LineCount(ParkWidgetSize size) => size switch
    {
        ParkWidgetSize.Small => 1,
        ParkWidgetSize.Wide => 1,
        _ => 3
    };

    /// <summary>Roughly how many characters of pixel font fit on one line at that scene width.</summary>
    public static int LineCharacters(int sceneWidth) => Math.Max(8, (sceneWidth - 8) / 4);

    /// <summary>Who is shown this half hour: a window over the roster that moves on each turn.</summary>
    public static IReadOnlyList<ParkWidgetResident> Shown(IReadOnlyList<ParkWidgetResident> residents, ParkWidgetSize size, DateTime local)
    {
        var drawable = residents.Where(r => !string.IsNullOrEmpty(r.Sprite)).ToArray();
        var capacity = Capacity(size);
        if (drawable.Length <= capacity) return drawable;
        var turn = (int)((local.Ticks / TurnLength.Ticks) % drawable.Length);
        return Enumerable.Range(0, capacity).Select(i => drawable[(turn + i) % drawable.Length]).ToArray();
    }

    /// <summary>Shortens a line to the characters that fit, ending on an ellipsis.</summary>
    public static string Fit(string text, int characters)
    {
        text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (characters < 2 || text.Length <= characters) return text;
        var cut = text[..(characters - 1)].TrimEnd();
        var space = cut.LastIndexOf(' ');
        if (space > characters / 2) cut = cut[..space].TrimEnd(',', ';', ':', ' ');
        return cut + "…";
    }

    /// <summary>
    /// Lays out the widget for a scene of <paramref name="width"/> x <paramref name="height"/> low-resolution
    /// pixels. Residents stand evenly across the grass, alternating facing; a large widget uses two rows.
    /// </summary>
    public static ParkWidgetView Compose(ParkWidgetSnapshot snapshot, ParkWidgetSize size, int width, int height, DateTime local)
    {
        width = Math.Max(32, width); height = Math.Max(32, height);
        var lineCount = LineCount(size);
        var panel = lineCount * 9 + 4;
        var horizon = Math.Max(10, (height - panel) * 11 / 20);
        var shown = Shown(snapshot.Residents, size, local);
        var placements = new List<ParkWidgetPlacement>(shown.Count);
        var grassBottom = height - panel - 2;
        if (size == ParkWidgetSize.Large && shown.Count > 3)
        {
            var back = (shown.Count + 1) / 2;
            Row(shown.Take(back).ToArray(), horizon + (grassBottom - horizon) / 2, 0);
            Row(shown.Skip(back).ToArray(), grassBottom, back);
        }
        else Row(shown, grassBottom, 0);

        var characters = LineCharacters(width);
        var lines = shown.Count == 0 && snapshot.Residents.Length == 0
            ? [Fit(EmptyLine, characters)]
            : snapshot.Moments.Where(m => !string.IsNullOrWhiteSpace(m)).Take(lineCount).Select(m => Fit(m, characters)).ToList();
        if (lines.Count == 0 && snapshot.Residents.Length > 0)
            lines.Add(Fit(snapshot.Residents.Length == 1 ? $"{snapshot.Residents[0].Name} lives in the park" : $"{snapshot.Residents.Length} Pokémon live in the park", characters));
        var title = size == ParkWidgetSize.Small ? "Park" : "Poképark";
        return new(size, width, height, ParkCare.PhaseAt(local), ParkCare.WeatherAt(local), horizon, placements, lines, title);

        void Row(IReadOnlyList<ParkWidgetResident> row, int feetY, int offset)
        {
            for (var i = 0; i < row.Count; i++)
            {
                // Even spacing: each resident in the middle of its own share of the width.
                var x = (2 * i + 1) * width / (2 * row.Count);
                placements.Add(new(row[i], x, feetY, (i + offset) % 2 == 1));
            }
        }
    }
}
