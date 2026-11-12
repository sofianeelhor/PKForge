using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>The Poképark's resident card and dialogue box (<see cref="ParkCardPaint"/>): a panel
/// along the bottom of the screen over the live park, with no scrim, so the Pokémon can be
/// watched reacting. Touch and the pad both drive it.</summary>
public sealed class PokeparkCardOverlay : IPadHandler
{
    /// <summary>The panel's height on screen, so the page keeps the Pokémon above it.</summary>
    public static float CoveredHeight(float width) => (ParkCardPaint.DesignHeight + 80) * width / 1920f + ReservedPixels;

    /// <summary>The park's footer height (device-independent units), set by the page: the panel sits
    /// above it instead of covering its button hints (QA 2026-11-12).</summary>
    public static double ReservedBottom { get; set; }

    private static float ReservedPixels => (float)(ReservedBottom * DeviceDisplay.Current.MainDisplayInfo.Density);

    private static readonly string[] Keys = ["talk", "snack", "play", "relax", "leave", "find"];
    private const float CharactersPerSecond = 45;

    private readonly Grid _host;
    private readonly SKCanvasView _view = new() { EnableTouchEvents = true, IgnorePixelScaling = false };
    private readonly GamepadRouter? _router;
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly Func<ParkCardPaint.Card>? _card;
    private readonly Func<SKBitmap?>? _portrait;
    private readonly string _speaker = "", _text = "";
    private readonly IDispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private SKImage? _portraitImage;
    private SKBitmap? _portraitSource;
    private SKRect _panel;
    private float _unit = 1;
    private int _selected;
    private bool _revealed, _closed;
    // A dialogue longer than the box is read a page at a time.
    private IReadOnlyList<string> _dialoguePages = [""];
    private (float Width, bool Portrait) _dialogueLayout;
    private int _dialoguePage;
    private long _dialoguePageStart;
    private ParkCardPaint.Menu _menu = ParkCardPaint.Menu.Actions;
    private ParkCardPaint.Page _page = ParkCardPaint.Page.Care;

    public static Task<string?> ShowCardAsync(Grid host, Func<ParkCardPaint.Card> card, Func<SKBitmap?> portrait) =>
        new PokeparkCardOverlay(host, card, portrait, "", "")._result.Task;

    public static Task ShowDialogueAsync(Grid host, string speaker, string text, Func<SKBitmap?>? portrait = null) =>
        new PokeparkCardOverlay(host, null, portrait, speaker, text)._result.Task;

    private PokeparkCardOverlay(Grid host, Func<ParkCardPaint.Card>? card, Func<SKBitmap?>? portrait, string speaker, string text)
    {
        _host = host; _card = card; _portrait = portrait; _speaker = speaker; _text = text;
        _selected = card is null ? 0 : 1;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view.BackgroundColor = Colors.Transparent;
        _view.PaintSurface += PaintGuard.Wrap("PokeparkCardOverlay.Paint", Paint);
        _view.Touch += Touch;
        host.Add(_view);
        Grid.SetRowSpan(_view, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_view, Math.Max(1, host.ColumnDefinitions.Count));
        _timer = host.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        // Redrawn while it rises and while its words appear, at once on any input, and otherwise a
        // few times a second for the live needs and mood: not 30 full-screen frames a second.
        _timer.Tick += (_, _) =>
        {
            var now = _clock.ElapsedMilliseconds;
            if (now < 300 || _card is null && !_revealed || now - _lastPaint >= 250) Redraw();
        };
        _timer.Start();
        App.Suspended += Suspended;
        _view.Unloaded += Unloaded;
        _router?.Push(this);
    }

    private long _lastPaint;

    private void Redraw()
    {
        _lastPaint = _clock.ElapsedMilliseconds;
        _view.InvalidateSurface();
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        _unit = e.Info.Width / 1920f;
        var height = _card is null ? ParkCardPaint.DesignHeight : ParkCardPaint.HeightOf(_page);
        var reserve = _view.Height > 0 ? (float)(ReservedBottom * e.Info.Height / _view.Height) : 0f;
        _panel = new SKRect(40 * _unit, e.Info.Height - reserve - (40 + height) * _unit, e.Info.Width - 40 * _unit, e.Info.Height - reserve - 40 * _unit);
        // The panel rises into place over its first 150 ms.
        var rise = Services.MotionSetting.Decorative ? Math.Max(0, 1 - _clock.ElapsedMilliseconds / 150f) : 0;
        c.Translate(0, rise * 60 * _unit);
        var portrait = Portrait();
        if (_card is not null)
            ParkCardPaint.PaintCard(c, _panel, _card(), portrait, _selected, PixelFont.Face, _unit, _menu, _page);
        else
        {
            // The pages depend on the room the text has: cut again when it changes (the
            // portrait arriving, the window resizing).
            var layout = (_panel.Width, portrait is not null);
            if (layout != _dialogueLayout)
            {
                _dialogueLayout = layout;
                _dialoguePages = ParkCardPaint.DialoguePages(_panel, _text, layout.Item2, PixelFont.Face, _unit);
                _dialoguePage = Math.Min(_dialoguePage, _dialoguePages.Count - 1);
            }
            var page = _dialoguePages[_dialoguePage];
            // With motion off the text is simply there; the arrow below does not blink
            // unless decorative motion is on.
            if (!Services.MotionSetting.Any) _revealed = true;
            var shown = _revealed ? page.Length
                : Math.Min(page.Length, (int)((_clock.ElapsedMilliseconds - _dialoguePageStart) / 1000f * CharactersPerSecond));
            if (shown >= page.Length) _revealed = true;
            ParkCardPaint.PaintDialogue(c, _panel, _speaker, page[..shown], portrait,
                _revealed && _dialoguePage < _dialoguePages.Count - 1,
                Services.MotionSetting.Decorative ? _clock.ElapsedMilliseconds : 0, PixelFont.Face, _unit);
        }
    }

    // The portrait changes with the mood: keep one image per bitmap.
    private SKImage? Portrait()
    {
        var bitmap = _portrait?.Invoke();
        if (bitmap is null) return null;
        if (!ReferenceEquals(bitmap, _portraitSource))
        {
            _portraitImage?.Dispose();
            _portraitImage = SKImage.FromBitmap(bitmap);
            _portraitSource = bitmap;
        }
        return _portraitImage;
    }

    private void Touch(object? sender, SKTouchEventArgs e)
    {
        Redraw();
        e.Handled = true;
        if (e.ActionType != SKTouchAction.Released) return;
        var point = e.Location;
        if (_card is null) { Advance(); return; }
        for (var i = 0; i < ParkCardPaint.Pages.Count; i++)
            if (ParkCardPaint.PageTabRect(_panel, i, _unit).Contains(point)) { OpenPage(ParkCardPaint.Pages[i].Page); return; }
        if (_page != ParkCardPaint.Page.Care)
        {
            if (!_panel.Contains(point)) Close(null);
            return;
        }
        if (_menu != ParkCardPaint.Menu.Actions)
        {
            for (var i = 0; i < ChoiceCount; i++)
                if (ParkCardPaint.ChoiceRect(_panel, i, _unit, ChoiceCount).Contains(point)) { Pick(i); return; }
            if (!_panel.Contains(point)) Close(null);
            return;
        }
        for (var i = 0; i < ParkCardPaint.Actions.Count; i++)
            if (ParkCardPaint.ActionRect(_panel, i, _unit).Contains(point)) { Choose(i); return; }
        if (ParkCardPaint.LeaveRect(_panel, _unit).Contains(point)) { Close("leave"); return; }
        if (ParkCardPaint.FindRect(_panel, _unit).Contains(point)) { Close("find"); return; }
        // A tap on the park above closes the card.
        if (!_panel.Contains(point)) Close(null);
    }

    // The dialogue's A or tap: show the whole page first, then turn to the next one, and
    // close after the last.
    private void Advance()
    {
        if (!_revealed) _revealed = true;
        else if (_dialoguePage < _dialoguePages.Count - 1)
        {
            _dialoguePage++;
            _dialoguePageStart = _clock.ElapsedMilliseconds;
            _revealed = false;
        }
        else Close(null);
    }

    public bool OnPadButton(PadButton button)
    {
        Redraw();
        if (_card is null)
        {
            if (button is PadButton.A or PadButton.B) Advance();
            return true;
        }
        // L and R turn the card's pages, as the shoulder buttons do everywhere else.
        if (button is PadButton.L or PadButton.R)
        {
            var count = ParkCardPaint.Pages.Count;
            var at = ParkCardPaint.Pages.ToList().FindIndex(p => p.Page == _page);
            OpenPage(ParkCardPaint.Pages[(at + (button == PadButton.R ? 1 : count - 1)) % count].Page);
            return true;
        }
        if (_page != ParkCardPaint.Page.Care)
        {
            if (button == PadButton.B) Close(null);
            return true;
        }
        if (_menu != ParkCardPaint.Menu.Actions)
        {
            var columns = ParkCardPaint.ChoiceColumns(ChoiceCount);
            _selected = button switch
            {
                PadButton.Left => _selected % columns > 0 ? _selected - 1 : _selected,
                PadButton.Right => _selected % columns < columns - 1 && _selected + 1 < ChoiceCount ? _selected + 1 : _selected,
                PadButton.Up => _selected >= columns ? _selected - columns : _selected,
                PadButton.Down => Math.Min(ChoiceCount - 1, _selected + columns),
                _ => _selected
            };
            if (button == PadButton.A) Pick(_selected);
            if (button == PadButton.B) Back();
            return true;
        }
        _selected = button switch
        {
            // The two links under the buttons: find on the left, leave on the right.
            PadButton.Left => _selected == 4 ? 5 : _selected == 5 ? 5 : _selected % 2 == 1 ? _selected - 1 : _selected,
            PadButton.Right => _selected == 5 ? 4 : _selected == 4 ? 4 : _selected % 2 == 0 ? _selected + 1 : _selected,
            PadButton.Up => _selected == 4 ? 3 : _selected == 5 ? 2 : _selected >= 2 ? _selected - 2 : _selected,
            PadButton.Down => _selected < 2 ? _selected + 2 : 4,
            _ => _selected
        };
        if (button == PadButton.A) Choose(_selected);
        if (button == PadButton.B) Close(null);
        return true;
    }

    private void OpenPage(ParkCardPaint.Page page)
    {
        _page = page;
        _menu = ParkCardPaint.Menu.Actions;
        _selected = 1;
    }

    // Snack opens the berry choice, Play the games; Talk and Relax close the card.
    private void Choose(int index)
    {
        if (index == 1) { _menu = ParkCardPaint.Menu.Berries; _selected = 2; return; }
        if (index == 2) { _menu = ParkCardPaint.Menu.Games; _selected = 0; return; }
        Close(Keys[index]);
    }

    private void Back()
    {
        _selected = _menu == ParkCardPaint.Menu.Berries ? 1 : 2;
        _menu = ParkCardPaint.Menu.Actions;
    }

    // The choices on show, Back included: five berries, or the games this Pokémon offers.
    private int ChoiceCount => (_menu == ParkCardPaint.Menu.Berries ? ParkCardPaint.BerryNames.Count : Games.Count) + 1;
    private IReadOnlyList<(string Key, string Label)> Games => ParkCardPaint.GamesFor(_card?.Invoke().Fighter == true);

    private void Pick(int index)
    {
        if (index == ChoiceCount - 1) { Back(); return; }
        Close(_menu == ParkCardPaint.Menu.Berries ? $"snack:{index}" : $"game:{Games[index].Key}");
    }

    private void Suspended() => Close(null);
    private void Unloaded(object? sender, EventArgs e) => Close(null);

    private void Close(string? result)
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        App.Suspended -= Suspended;
        _view.Unloaded -= Unloaded;
        _router?.Remove(this);
        _host.Remove(_view);
        _portraitImage?.Dispose();
        _result.TrySetResult(result);
    }
}
