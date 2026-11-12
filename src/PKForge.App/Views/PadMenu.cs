using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>A menu entry: label plus optional colored glyph badge or image icon - never bare text.
/// <paramref name="Detail"/> is an optional quiet line under the label ("PP 35 → 56");
/// the choice result is still the label.</summary>
public sealed record PadOption(string Label, string? Glyph = null, Color? Accent = null, string? IconPath = null, string? Detail = null);

/// <summary>
/// Every small menu and question, painted in the DA by <see cref="PadMenuPaint"/>: a panel with
/// its title on a banner tab, the choices on bands, the slanted chip as the cursor. The d-pad
/// moves, A chooses, B leaves; a tap selects and a second tap on the same choice takes it.
/// </summary>
public sealed class PadMenu : IPadHandler
{
    private static readonly Dictionary<string, SKImage?> Icons = [];

    // Icons are tinted with the theme's colours: a new colour scheme starts a fresh cache.
    // The old images are not disposed here: a menu still on screen may be drawing them; once
    // nothing holds them, their finalizer frees them.
    static PadMenu() => ColorTheme.Changed += () => { lock (Icons) Icons.Clear(); };

    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PadOption[] _options;
    private readonly PadMenuLine[] _lines;
    private readonly string _title;
    private readonly string? _message;
    private readonly string _leave;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly int _columns;
    private PadMenuHits? _hits;
    private int _index;
    private int _scroll;
    private int _messagePage;
    private bool _closed;

    /// <summary>Shows the menu inside <paramref name="host"/> (a page's root grid) and returns the chosen option or null.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params string[] options) =>
        new PadMenu(host, title, message, options.Select(o => new PadOption(o)).ToArray())._result.Task;

    /// <summary>Rich overload: options with glyph badges, accent colors, or image icons.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params PadOption[] options) =>
        new PadMenu(host, title, message, options)._result.Task;

    /// <summary>The rich menu with the cursor already on option <paramref name="start"/>: a menu shown
    /// again after a toggle keeps the player's place.</summary>
    public static Task<string?> ShowAtAsync(Grid host, string title, string? message, int start, params PadOption[] options) =>
        new PadMenu(host, title, message, options, start)._result.Task;

    /// <summary>Two-option confirm box; true when the user picks <paramref name="confirmLabel"/>.</summary>
    public static async Task<bool> ConfirmAsync(Grid host, string title, string message, string confirmLabel = "OK")
    {
        var choice = await ShowAsync(host, title, message, confirmLabel, "Cancel");
        return choice == confirmLabel;
    }

    private PadMenu(Grid host, string title, string? message, PadOption[] options, int start = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _host = host;
        _title = title;
        _message = message;
        _options = options;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        // Columns by what the labels need, as before: short labels in a long list share rows.
        var longest = options.Length == 0 ? 0 : options.Max(o => o.Label.Length);
        _columns = options.Length switch
        {
            <= 4 => 1,
            <= 12 => longest > 24 ? 1 : 2,
            _ => longest > 16 ? 2 : 3,
        };
        _lines = [.. options.Select(o => new PadMenuLine(o.Label, o.Detail, IconOf(o.IconPath), o.IconPath is null ? o.Glyph : null,
            o.Accent is { } accent ? accent.ToSKColor() : null))];
        // B cancels a question, closes a message, and goes back from a menu: the hint says which.
        _leave = options.Any(option => option.Label == "Cancel") ? "Cancel" : options.Length == 1 ? "Close" : "Back";
        _index = Math.Clamp(start, 0, Math.Max(0, options.Length - 1));
        _canvas.PaintSurface += PaintGuard.Wrap("PadMenu.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _overlay = Kit.AttachOverlay(host, _canvas);
        _router?.Push(this);
        PerfTrace.Log($"menu.open[{options.Length}]", watch);
        PerfTrace.UntilIdle($"menu.open[{options.Length}]", host.Dispatcher);
    }

    /// <summary>A PKSM icon by its semantic name, tinted as the menus always drew it; null for paths or unknown names.</summary>
    private static SKImage? IconOf(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/')) return null;
        lock (Icons)
        {
            if (Icons.TryGetValue(name, out var cached)) return cached;
            SKImage? image = null;
            try
            {
                var png = PksmIcons.GetPng(name, PksmIcons.IsNative(name) ? PksmIcons.Native : PksmIcons.Cyan);
                using var bitmap = SKBitmap.Decode(png);
                if (bitmap is not null)
                {
                    var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
                    using var whole = SKImage.FromBitmap(bitmap);
                    image = Kit.SubsetOf(whole, bounds.IsEmpty ? new SKRectI(0, 0, bitmap.Width, bitmap.Height) : bounds);
                }
            }
            catch (Exception) { image = null; }
            Icons[name] = image;
            return image;
        }
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        var scene = new PadMenuScene(_title, _message, _lines, _columns, _index, _scroll, _leave, _messagePage);
        _hits = PadMenuPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
        // The first paint learns how many rows fit; keep the cursor's row in view.
        if (KeepInView()) _canvas.InvalidateSurface();
    }

    private bool KeepInView()
    {
        if (_hits is not { } hits) return false;
        var row = _index / _columns;
        var scroll = _scroll;
        if (row < scroll) scroll = row;
        else if (row >= scroll + hits.VisibleRows) scroll = row - hits.VisibleRows + 1;
        if (scroll == _scroll) return false;
        _scroll = scroll;
        return true;
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var at = args.Location;
        if (hits.Choose.Contains(at)) { Choose(); return; }
        if (hits.Leave.Contains(at) || !hits.Panel.Contains(at)) { Close(null); return; }
        // A message read in pages: a tap on it turns to the next page, round to the first.
        if (hits.MessagePages > 1 && hits.Message.Contains(at)) { TurnPage((_messagePage + 1) % hits.MessagePages); return; }
        foreach (var (index, rect) in hits.Lines)
        {
            if (!rect.Contains(at)) continue;
            if (index == _index) Choose();
            else Highlight(index);
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Highlight(_index - _columns); return true;
            case PadButton.Down: Highlight(_index + _columns); return true;
            case PadButton.Left: Highlight(_index - 1); return true;
            case PadButton.Right: Highlight(_index + 1); return true;
            case PadButton.L when _hits is { MessagePages: > 1 }: TurnPage(_messagePage - 1); return true;
            case PadButton.R when _hits is { MessagePages: > 1 }: TurnPage(_messagePage + 1); return true;
            case PadButton.L: Highlight(_index - Math.Max(1, (_hits?.VisibleRows ?? 1) * _columns)); return true;
            case PadButton.R: Highlight(_index + Math.Max(1, (_hits?.VisibleRows ?? 1) * _columns)); return true;
            case PadButton.A: Choose(); return true;
            case PadButton.B: Close(null); return true;
            default: return true; // the menu owns the pad while open
        }
    }

    private void Highlight(int index)
    {
        if (_options.Length == 0) return;
        _index = Math.Clamp(index, 0, _options.Length - 1);
        KeepInView();
        _canvas.InvalidateSurface();
    }

    private void TurnPage(int page)
    {
        var pages = _hits?.MessagePages ?? 1;
        _messagePage = Math.Clamp(page, 0, pages - 1);
        _canvas.InvalidateSurface();
    }

    private void Choose()
    {
        if (_options.Length > 0) Close(_options[_index].Label);
    }

    private void Close(string? result)
    {
        if (_closed) return;
        _closed = true;
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
