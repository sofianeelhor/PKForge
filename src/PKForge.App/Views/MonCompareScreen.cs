using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>One Pokémon chosen for a comparison: its decoded summary and where it lives.</summary>
public sealed record CompareCandidate(MonSummary Summary, string Where);

/// <summary>
/// "Compare" in the boxes' and the Bank's Pokémon menus: the first choice waits here (it
/// survives moving between a save and the Bank), the second opens <see cref="MonCompareScreen"/>.
/// </summary>
public static class CompareFlow
{
    private static readonly ComparePick<CompareCandidate> Pick = new();

    private const string CancelLabel = "Cancel compare";

    /// <summary>The menu entry for the Pokémon at <paramref name="place"/>, worded for whether a
    /// first Pokémon waits, and whether it is this one (then choosing it again cancels).</summary>
    public static PadOption Option(string place) => Pick.Waiting is not { } waiting
        ? new PadOption("Compare with…", IconPath: "info", Detail: "Pick this one, then choose the Pokémon to compare it with, here, in another game or in the Bank.")
        : Pick.Holds(place)
            ? new PadOption(CancelLabel, IconPath: "close", Detail: $"{waiting} stops waiting to be compared.")
            : new PadOption($"Compare with {waiting}", IconPath: "info", Detail: "Opens both side by side.");

    /// <summary>Whether a menu choice is the compare entry.</summary>
    public static bool Is(string? choice) => choice is not null
        && (choice.StartsWith("Compare with", StringComparison.Ordinal) || choice == CancelLabel);

    /// <summary>Drops the waiting first pick (B on the boxes and the Bank). Returns the status
    /// line to show, or null when nothing was waiting.</summary>
    public static string? Cancel()
    {
        if (Pick.Waiting is not { } waiting) return null;
        Pick.Clear();
        return $"Compare cancelled · {waiting} is no longer picked.";
    }

    /// <summary>Takes this Pokémon as the first or second pick; the second opens the comparison,
    /// and the first one chosen again cancels the pick. <paramref name="place"/> names where the
    /// Pokémon lives (save slot or Bank entry). Returns the status line to show.</summary>
    public static async Task<string> ChooseAsync(Grid host, ISpriteService sprites, Func<Task<MonSummary?>> load, string where, string place)
    {
        if (Pick.Holds(place)) return Cancel()!;
        var summary = await load();
        if (summary is null) return "That Pokémon could not be read.";
        var first = Pick.Choose(new CompareCandidate(summary, where), summary.DisplayName, place);
        if (first is null) return $"{summary.DisplayName} picked · choose Compare on another Pokémon, or press B to cancel.";
        await MonCompareScreen.ShowAsync(host, sprites, first, new CompareCandidate(summary, where));
        return $"Compared {first.Summary.DisplayName} with {summary.DisplayName}.";
    }
}

/// <summary>
/// Two Pokémon side by side (<see cref="ComparePaint"/>). L/R (or a tap) focuses a side, A (or a
/// second tap on it) opens that side's full summary, up/down scroll the sections, B closes.
/// On a second screen the inspector shows the focused side in full, like the boxes' and the
/// Bank's cursor. Read-only, so Hardcore mode keeps it.
/// </summary>
public sealed class MonCompareScreen : IPadHandler
{
    private readonly Grid _host;
    private readonly ISpriteService _sprites;
    private readonly CompareCandidate[] _sides;
    private readonly MonComparison _model;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _overlay;
    private readonly SecondScreenState? _secondScreen;
    private readonly SecondScreenClaim? _secondClaim;
    private CompareHits? _hits;
    private int _focus;
    private int _section;
    private bool _busy;

    public static async Task ShowAsync(Grid host, ISpriteService sprites, CompareCandidate left, CompareCandidate right)
    {
        var screen = new MonCompareScreen(host, sprites, left, right);
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(screen);
        try { await screen._closed.Task; }
        finally
        {
            router?.Remove(screen);
            screen._secondClaim?.Release();
            if (screen._secondScreen is not null) screen._secondScreen.Compared = null;
            host.Remove(screen._overlay);
        }
    }

    private MonCompareScreen(Grid host, ISpriteService sprites, CompareCandidate left, CompareCandidate right)
    {
        _host = host;
        _sprites = sprites;
        _sides = [left, right];
        _model = MonComparison.Build(left.Summary, left.Where, right.Summary, right.Where);
        _canvas.PaintSurface += PaintGuard.Wrap("MonCompareScreen.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("LR", "Side", () => OnPadButton(PadButton.R)),
            ("▲▼", "Scroll", () => OnPadButton(PadButton.Down)),
            ("A", "Summary", () => OnPadButton(PadButton.A)),
            ("B", "Close", () => OnPadButton(PadButton.B)));
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, footer },
        };
        Grid.SetRow(footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
        // The lower screen follows the focused side until this closes.
        _secondScreen = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        _secondClaim = _secondScreen?.Routes.OpenOverlay(SecondScreenOwner.Compare);
        PublishFocus();
    }

    /// <summary>The focused side, for the second screen's inspector.</summary>
    private void PublishFocus()
    {
        if (_secondScreen is null) return;
        var side = _sides[_focus];
        _secondScreen.Compared = new InspectorContent(side.Summary, false, $"Compare · {(_focus == 0 ? "left" : "right")} · {side.Where}");
    }

    private void Focus(int side)
    {
        if (side == _focus) return;
        _focus = side;
        PublishFocus();
        _canvas.InvalidateSurface();
    }

    private SKImage? Sprite(MonSummary summary)
    {
        if (_sprites.GetSprite(summary.Look) is { } bitmap) return SpriteImages.Of(bitmap);
        _sprites.Warm(summary.Look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
        return null;
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        var scene = new CompareScene(_model, Sprite(_sides[0].Summary), Sprite(_sides[1].Summary), _focus, _section);
        _hits = ComparePaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    private void Scroll(int direction)
    {
        var next = _section + direction;
        if (next < 0 || next >= _model.Sections.Count) return;
        if (direction > 0 && _hits is { CanScrollDown: false }) return;
        _section = next;
        _canvas.InvalidateSurface();
    }

    /// <summary>The focused side's full summary, on top of this screen.</summary>
    private async Task OpenSummaryAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var side = _sides[_focus];
            var deck = new SummaryDeck(1, _ => true, 0, (_, _) => Task.FromResult<MonSummary?>(side.Summary), side.Where);
            await MonSummaryScreen.ShowAsync(_host, _sprites, deck);
        }
        finally { _busy = false; }
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var side = hits.Left.Contains(args.Location) ? 0 : hits.Right.Contains(args.Location) ? 1 : -1;
        if (side >= 0)
        {
            // A tap focuses a side; a second tap on the focused side opens its summary.
            if (side == _focus) _ = OpenSummaryAsync();
            else Focus(side);
            return;
        }
        if (hits.Body.Contains(args.Location))
            Scroll(args.Location.Y > hits.Body.MidY ? 1 : -1);
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.L or PadButton.R or PadButton.Left or PadButton.Right:
                // L/R swap the focused side; the d-pad's left/right pick that side.
                Focus(button switch { PadButton.Left => 0, PadButton.Right => 1, _ => 1 - _focus });
                return true;
            case PadButton.Up: Scroll(-1); return true;
            case PadButton.Down: Scroll(1); return true;
            case PadButton.A: _ = OpenSummaryAsync(); return true;
            case PadButton.B: _closed.TrySetResult(); return true;
            default: return true; // modal while open
        }
    }
}
