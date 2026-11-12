using System.Globalization;
using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Hall of Fame museum (<see cref="MuseumPaint"/>), opened from the Saves section: every
/// playthrough on the Home shelf, one hall each along a timeline, with its champion teams,
/// badges, time, Pokédex and records. Left/Right walk the halls, Up/Down the hall's teams,
/// A opens the save, B closes. Every save is only read.
/// </summary>
internal sealed class MuseumScreen : IPadHandler
{
    private readonly Grid _host;
    private readonly ISpriteService _sprites;
    private readonly IReadOnlyList<(SaveLibraryRow Row, MuseumHall Hall)> _halls;
    private readonly MuseumTotals _totals;
    private readonly Dictionary<string, SKImage?> _art;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly TaskCompletionSource<SaveLibraryRow?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _overlay;
    private MuseumHits? _hits;
    private int _cursor;
    private int _team;

    /// <summary>Shows the museum over <paramref name="host"/>; the row to open when the player pressed A, else null.</summary>
    public static async Task<SaveLibraryRow?> ShowAsync(Grid host, SaveLibraryViewModel viewModel, ISpriteService sprites)
    {
        // The loading overlay holds the pad (B cancels) until the museum pushes itself: nothing
        // reaches the Saves page beneath in between, so it cannot be closed under the museum.
        var overlay = LoadingOverlay.Show(host, "Opening the museum…", "Every save is only read.");
        IReadOnlyList<(SaveLibraryRow, MuseumHall)> halls;
        Dictionary<string, SKImage?> art;
        try
        {
            halls = await viewModel.MuseumAsync();
            art = await LoadArtAsync(halls);
        }
        finally { overlay.Close(); }
        if (overlay.Cancellation.IsCancellationRequested || host.Window is null)
        {
            // Cancelled, or the page left meanwhile: no museum over a page that is gone.
            foreach (var image in art.Values) image?.Dispose();
            return null;
        }
        var screen = new MuseumScreen(host, sprites, halls, art);
        return await screen.RunAsync();
    }

    private MuseumScreen(Grid host, ISpriteService sprites, IReadOnlyList<(SaveLibraryRow, MuseumHall)> halls, Dictionary<string, SKImage?> art)
    {
        _host = host;
        _sprites = sprites;
        _halls = halls;
        _art = art;
        _totals = Museum.Totals(halls.Select(h => h.Item2));
        // Start on the latest champion's hall along the timeline, else the first.
        _cursor = Math.Max(0, halls.ToList().FindLastIndex(h => h.Item2.IsChampion));
        _canvas.PaintSurface += PaintGuard.Wrap("MuseumScreen.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("◀▶", "Halls", null),
            ("▲▼", "Teams", null),
            ("A", "Open this save", () => OnPadButton(PadButton.A)),
            ("B", "Back", () => OnPadButton(PadButton.B)));
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, footer },
        };
        Grid.SetRow(footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
    }

    /// <summary>Each hall's game art, decoded once; the museum disposes it on close.</summary>
    private static async Task<Dictionary<string, SKImage?>> LoadArtAsync(IReadOnlyList<(SaveLibraryRow Row, MuseumHall Hall)> halls)
    {
        var art = new Dictionary<string, SKImage?>();
        foreach (var (row, hall) in halls)
        {
            if (art.ContainsKey(hall.Key)) continue;
            SKImage? image = null;
            try
            {
                if (await GameArt.GetIconAsync(row.Entry.Save) is { } path)
                {
                    using var bitmap = SKBitmap.Decode(path);
                    if (bitmap is not null) image = SKImage.FromBitmap(bitmap);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            art[hall.Key] = image;
        }
        return art;
    }

    private async Task<SaveLibraryRow?> RunAsync()
    {
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        try { return await _closed.Task; }
        finally
        {
            router?.Remove(this);
            _host.Remove(_overlay);
            foreach (var image in _art.Values) image?.Dispose();
            _art.Clear();
        }
    }

    private SKImage? Sprite(HallOfFameMember member)
    {
        var look = new SpriteLook(member.Species, member.Form ?? 0, member.IsShiny ?? false);
        if (_sprites.GetSprite(look) is { } bitmap) return SpriteImages.Of(bitmap);
        _sprites.Warm(look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
        return null;
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private MuseumHallView View(MuseumHall hall)
    {
        var r = hall.Reading;
        var facts = new List<string> { hall.FirstClearLine };
        if (hall.Champions > 1) facts.Add($"Champion {hall.Champions} times");
        if (r.PlayHours > 0 || r.PlayMinutes > 0) facts.Add($"Played {hall.PlayTime}");
        if (hall.DexTotal > 0) facts.Add($"Pokédex {Museum.Count(hall.DexCaught)} caught of {Museum.Count(hall.DexTotal)}");
        facts.Add(hall.Pokemon == 1 ? "1 Pokémon" : $"{Museum.Count(hall.Pokemon)} Pokémon");
        // The timeline is a calendar: a date or a year under every stop, never a play time (that
        // one reads "14:03…" beside "2026", QA 2026-11-12; it stays in the facts, "First clear at…").
        var when = r.FirstClearDate is { } date ? Day(date)
            : hall.LastPlayed is { } played ? played.Year.ToString(CultureInfo.InvariantCulture)
            : "";
        return new MuseumHallView(hall.Game, when, _art.GetValueOrDefault(hall.Key), hall.Trainer, hall.Gender, hall.IsChampion,
            facts,
            [.. r.Badges.Select(b => (TrainerCardPaint.BadgeKey(b.Region, b.Name), b.Obtained))],
            [.. r.Records.Select(x => (x.Facility, x.Label, Museum.Count(x.Value)))]);
    }

    private static SKImage? _medal;
    private static bool _medalTried;

    /// <summary>The Hall of Fame ribbon sprite, trimmed to its pixels.</summary>
    private static SKImage? Medal
    {
        get
        {
            if (_medalTried) return _medal;
            _medalTried = true;
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync("ui/items/hall-of-fame.png").GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream);
                if (bitmap is not null)
                {
                    var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
                    using var whole = SKImage.FromBitmap(bitmap);
                    _medal = bounds.Width > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
                }
            }
            catch (Exception error) when (error is IOException or FileNotFoundException) { _medal = null; }
            return _medal;
        }
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        MuseumTeamView? team = null;
        var teams = _cursor < _halls.Count ? _halls[_cursor].Hall.Teams : [];
        var noTeam = "No champion team yet.";
        if (_cursor < _halls.Count)
        {
            var hall = _halls[_cursor].Hall;
            if (teams.Count > 0)
            {
                var t = teams[Math.Clamp(_team, 0, teams.Count - 1)];
                team = new MuseumTeamView(t.Label,
                    t.Date is { } d ? Day(d) : "",
                    [.. t.Members.Select(m => new MuseumMember(
                        m.Nickname is { Length: > 0 } nick ? nick : m.SpeciesName,
                        m.Level is { } level ? $"{m.SpeciesName} · Lv. {level}" : m.SpeciesName,
                        Sprite(m), m.IsShiny ?? false))]);
            }
            else if (hall.Reading.Fame is null && hall.IsChampion)
                noTeam = "This game's Hall of Fame teams cannot be read.";
            else if (hall.Reading.Fame is null)
                noTeam = "This game keeps no Hall of Fame PKForge can read.";
        }
        var totals = new List<(string, string)>
        {
            ("Champions", Museum.Count(_totals.Champions)),
            ("Badges", $"{_totals.Badges}/{_totals.BadgesTotal}"),
            ("Pokémon", Museum.Count(_totals.Pokemon)),
            ("Played", _totals.PlayTime),
            ("Teams", Museum.Count(_totals.Teams)),
        };
        var scene = new MuseumScene(totals, [.. _halls.Select(h => View(h.Hall))], _cursor, team,
            Math.Clamp(_team, 0, Math.Max(0, teams.Count - 1)), teams.Count, noTeam,
            "Every save on the Home shelf, read as it is: nothing is written.",
            "No save on the Home shelf yet.", Medal);
        _hits = MuseumPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    private void MoveHall(int index)
    {
        if (_halls.Count == 0) return;
        var next = Math.Clamp(index, 0, _halls.Count - 1);
        if (next != _cursor) _team = 0;
        _cursor = next;
        _canvas.InvalidateSurface();
    }

    private void MoveTeam(int delta)
    {
        if (_cursor >= _halls.Count) return;
        var count = _halls[_cursor].Hall.Teams.Count;
        if (count == 0) return;
        _team = Math.Clamp(_team + delta, 0, count - 1);
        _canvas.InvalidateSurface();
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        if (hits.PreviousTeam.Contains(args.Location)) { MoveTeam(-1); return; }
        if (hits.NextTeam.Contains(args.Location)) { MoveTeam(1); return; }
        foreach (var (index, rect) in hits.Halls)
            if (rect.Contains(args.Location)) { MoveHall(index); return; }
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: case PadButton.L: MoveHall(_cursor - 1); return true;
            case PadButton.Right: case PadButton.R: MoveHall(_cursor + 1); return true;
            case PadButton.Up: MoveTeam(-1); return true;
            case PadButton.Down: MoveTeam(1); return true;
            case PadButton.A when _cursor < _halls.Count: _closed.TrySetResult(_halls[_cursor].Row); return true;
            case PadButton.B: _closed.TrySetResult(null); return true;
            default: return true; // modal while open
        }
    }
}
