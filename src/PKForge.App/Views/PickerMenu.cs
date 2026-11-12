using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>An entry in a <see cref="PickerMenu"/>: id, display name, optional icon file,
/// optional detail under the name (a nature's "+Atk −SpA", a move's numbers, an item's
/// effect). The init-only extras draw the shared <see cref="InfoKit"/> pieces on the row:
/// a type badge, the move category icon, a right-hand tag ("Lv 32", "HIDDEN") and a muted
/// name for entries outside the legal set.</summary>
public sealed record PickItem(int Id, string Name, string? IconPath = null, string? Detail = null)
{
    public int? TypeId { get; init; }
    public MoveCategory? Category { get; init; }
    public string? Tag { get; init; }
    public Color? TagColor { get; init; }
    public bool Muted { get; init; }
    /// <summary>Search also matches this text (a move's type, an ability's slot).</summary>
    public string? Keywords { get; init; }
}

/// <summary>
/// A two-state list filter with its own chip and the Y button ("Legal only" / "Show all").
/// <paramref name="Keep"/> decides which items the filtered state shows.
/// </summary>
public sealed record PickerFilter(string OnLabel, string OffLabel, Func<PickItem, bool> Keep, bool StartOn = true);

/// <summary>
/// A live panel under a picker's list that follows the highlighted row (the nature
/// picker's stat preview). With a panel, a tap highlights instead of picking so touch
/// users see the preview too (every picker now works that way: a tap aims, a second tap picks).
/// </summary>
public sealed record PickerPreview(View Panel, Action<PickItem?> OnHighlight);

/// <summary>
/// The searchable choice window for big lists (held items, moves, abilities, balls…), painted
/// in the DA by <see cref="PickerPaint"/>. Touch: tap the search plate to type (the list
/// follows each key), tap a row to aim it and tap it again to pick, drag to scroll. Pad: the
/// d-pad moves the chip, L/R page, A picks, Y flips the filter, X cuts to a type, Select
/// searches, B cancels. Owns the gamepad while open.
/// </summary>
public sealed class PickerMenu : IPadHandler
{
    // Decoded row art by file path, shared by every picker: a list of 2000 items decodes each
    // sprite once, and only when its row first scrolls into view.
    private static readonly Dictionary<string, SKImage?> Icons = [];

    // Icons are tinted with the theme's colours: a new colour scheme starts a fresh cache.
    // The old images are not disposed here: a menu still on screen may be drawing them; once
    // nothing holds them, their finalizer frees them.
    static PickerMenu() => ColorTheme.Changed += () => { lock (Icons) Icons.Clear(); };

    private readonly TaskCompletionSource<PickItem?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<PickItem> _all;
    private readonly string _title;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly PickerPreview? _livePreview;
    private readonly PickerFilter? _filter;
    private readonly bool _detailRows;
    private readonly Dictionary<PickItem, PickerLine> _lines = [];
    private bool _filterOn;
    // Lists of moves can be cut to one type (X): every type present in the list is offered.
    private readonly bool _hasTypes;
    private int? _typeFilter;
    private string _query = "";
    private List<PickItem> _filtered;
    private int _index;
    private float _offset; // the list's scroll, design pixels
    private bool _centerPending = true; // the first paint centres the current value
    private bool _keepPending; // the next paint brings the chip into view
    private bool _armed; // the highlighted row was aimed by a tap: a second tap picks it
    private bool _busy; // a search prompt or the type menu is open over the picker
    private bool _closed;
    private PickerHits? _hits;
    private SKPoint? _pressedAt;
    private float _pressedOffset;
    private bool _dragged;
    private float _velocity; // design pixels per millisecond, for the fling
    private long _lastMove;
    private float _lastY;
    private VsyncLoop? _fling;

    public static Task<PickItem?> ShowAsync(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId = null,
        PickerPreview? preview = null, PickerFilter? filter = null) =>
        new PickerMenu(host, title, items, currentId, preview, filter)._result.Task;

    private PickerMenu(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId, PickerPreview? livePreview,
        PickerFilter? filter)
    {
        _host = host;
        _all = items;
        _title = Kit.Tidy(title);
        _livePreview = livePreview;
        _filter = filter;
        // The current value always stays reachable: a filter that would hide it starts off.
        _filterOn = filter is not null && filter.StartOn
            && (currentId is not { } cur || items.FirstOrDefault(x => x.Id == cur) is not { } held || filter.Keep(held));
        _hasTypes = items.Count(x => x.TypeId is not null) > 1;
        _detailRows = items.Any(x => x.Detail is not null);
        _filtered = Filter("");
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        if (currentId is { } id)
        {
            var current = _filtered.FindIndex(x => x.Id == id);
            if (current >= 0) _index = current;
        }

        _canvas.PaintSurface += PaintGuard.Wrap("PickerMenu.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _overlay = Kit.AttachOverlay(host, _canvas);
        if (livePreview is not null)
        {
            // The live card rides over the frame the painter leaves for it (placed on paint).
            livePreview.Panel.HorizontalOptions = LayoutOptions.Start;
            livePreview.Panel.VerticalOptions = LayoutOptions.Start;
            livePreview.Panel.IsVisible = false;
            _overlay.Add(livePreview.Panel);
        }
        UpdatePreview();
        _router?.Push(this);
    }

    private List<PickItem> Filter(string query)
    {
        IEnumerable<PickItem> source = _all;
        if (_filterOn && _filter is not null) source = source.Where(_filter.Keep);
        if (_typeFilter is { } type) source = source.Where(x => x.Id == 0 || x.TypeId == type);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            source = source.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || (x.Keywords?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return source.ToList();
    }

    // ---------- Painting ----------

    private PickerLine LineOf(int index)
    {
        var item = _filtered[index];
        if (_lines.TryGetValue(item, out var line)) return line;
        line = new PickerLine(item.Name, item.Detail, IconOf(item.IconPath),
            item.TypeId is { } t ? TypeFacts.Name(t) : null,
            item.TypeId is { } tc ? InfoKit.TypeColor(tc).ToSKColor() : null,
            item.Category is { } k ? (int)k : null,
            item.Tag, item.TagColor?.ToSKColor(), item.Muted, item.TypeId);
        _lines[item] = line;
        return line;
    }

    private static SKImage? IconOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Icons)
        {
            if (Icons.TryGetValue(path, out var cached)) return cached;
            SKImage? image = null;
            try
            {
                using var bitmap = File.Exists(path) ? SKBitmap.Decode(path) : null;
                if (bitmap is not null)
                {
                    // Cropped to the opaque pixels so the art fills its box in whole steps.
                    var all = new SKRectI(0, 0, bitmap.Width, bitmap.Height);
                    var bounds = StoragePaint.OpaqueBounds(bitmap, all);
                    using var whole = SKImage.FromBitmap(bitmap);
                    image = Kit.SubsetOf(whole, bounds.IsEmpty ? all : bounds);
                }
            }
            catch (Exception) { image = null; }
            Icons[path] = image;
            return image;
        }
    }

    private PickerScene Scene()
    {
        var chips = new List<PickerChip>();
        if (_hasTypes)
            chips.Add(new PickerChip(_typeFilter is { } t ? $"✓ {TypeFacts.Name(t)}" : "Type: All", _typeFilter is not null,
                _typeFilter is { } tc ? InfoKit.TypeColor(tc).ToSKColor() : null));
        if (_filter is not null)
            chips.Add(new PickerChip(_filterOn ? $"✓ {_filter.OnLabel}" : _filter.OffLabel, _filterOn));
        var keys = new List<(string, string)> { ("A", "Pick") };
        if (_hasTypes) keys.Add(("X", "Type"));
        if (_filter is not null) keys.Add(("Y", "Filter"));
        keys.Add(("L/R", "Page"));
        keys.Add(("B", "Cancel"));
        return new PickerScene(_title, _query, _filtered.Count, LineOf, _index, _offset, chips, StatusText(), keys,
            _detailRows, _livePreview is not null, (c, r, k) => InfoKit.CategoryIcon.DrawCategory(c, r, (MoveCategory)k),
            TypePlates.Paint);
    }

    private string StatusText()
    {
        var current = Current;
        if (current is null) return _filtered.Count == 0 ? "A picks: (nothing matches)" : "";
        return _armed ? $"A or tap again picks: {current.Name}" : $"A picks: {current.Name}";
    }

    private PickItem? Current => _index >= 0 && _index < _filtered.Count ? _filtered[_index] : null;

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        var face = BoxBrowserPage.PixelTypeface();
        _hits = PickerPaint.Paint(args.Surface.Canvas, args.Info, Scene(), face);
        // The first paint learns the list's height: centre the current value, keep the chip in view.
        var before = _offset;
        if (_centerPending)
        {
            _centerPending = false;
            _offset = _index * _hits.RowHeight - (_hits.ListHeight - _hits.RowHeight) / 2f;
        }
        else if (_keepPending) KeepInView();
        _keepPending = false;
        _offset = Math.Clamp(_offset, 0f, _hits.MaxOffset);
        if (MathF.Abs(before - _offset) > 0.01f) _hits = PickerPaint.Paint(ClearAndGet(args), args.Info, Scene(), face);
        PlacePreview(args.Info);
    }

    private static SKCanvas ClearAndGet(SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        return args.Surface.Canvas;
    }

    private void KeepInView()
    {
        if (_hits is not { } hits) return;
        var top = _index * hits.RowHeight;
        if (top < _offset) _offset = top;
        else if (top + hits.RowHeight > _offset + hits.ListHeight) _offset = top + hits.RowHeight - hits.ListHeight;
    }

    /// <summary>Lays the live card over the frame the painter left for it, in device-independent units.</summary>
    private void PlacePreview(SKImageInfo info)
    {
        if (_livePreview is null || _hits is not { } hits || hits.Side.IsEmpty || _canvas.Width <= 0) return;
        var scale = (float)(info.Width / _canvas.Width);
        var side = hits.Side;
        var panel = _livePreview.Panel;
        const double inset = 10;
        var margin = new Thickness(side.Left / scale + inset, side.Top / scale + inset, 0, 0);
        var width = side.Width / scale - 2 * inset;
        var height = side.Height / scale - 2 * inset;
        if (panel.Margin != margin) panel.Margin = margin;
        if (Math.Abs(panel.WidthRequest - width) > 0.5) panel.WidthRequest = width;
        if (Math.Abs(panel.MaximumHeightRequest - height) > 0.5) panel.MaximumHeightRequest = height;
        panel.IsVisible = true;
    }

    private void Invalidate() => _canvas.InvalidateSurface();

    // ---------- Touch ----------

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_hits is not { } hits || _busy) return;
        var at = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                StopFling();
                _pressedAt = at;
                _pressedOffset = _offset;
                _dragged = false;
                _velocity = 0;
                _lastY = at.Y;
                _lastMove = Environment.TickCount64;
                return;
            case SKTouchAction.Moved when _pressedAt is { } start && hits.List.Contains(start):
            {
                var dy = (start.Y - at.Y) / hits.Unit;
                if (!_dragged && MathF.Abs(dy) < hits.RowHeight / 4f) return;
                _dragged = true;
                _offset = Math.Clamp(_pressedOffset + dy, 0f, hits.MaxOffset);
                var now = Environment.TickCount64;
                var dt = Math.Max(1, now - _lastMove);
                _velocity = 0.6f * _velocity + 0.4f * ((_lastY - at.Y) / hits.Unit / dt);
                _lastY = at.Y;
                _lastMove = now;
                Invalidate();
                return;
            }
            case SKTouchAction.Released:
                var tapped = !_dragged;
                _pressedAt = null;
                if (tapped) OnTap(hits, at);
                else if (Environment.TickCount64 - _lastMove < 80 && MathF.Abs(_velocity) > 0.3f) StartFling();
                return;
            case SKTouchAction.Cancelled:
                _pressedAt = null;
                return;
        }
    }

    private void StartFling()
    {
        // Stepped on each screen refresh by the time that really passed (see VsyncLoop).
        _fling ??= new VsyncLoop(OnFling);
        _fling.Start();
    }

    private bool OnFling(float elapsed)
    {
        if (_hits is not { } hits || _closed) return false;
        _offset = Math.Clamp(_offset + _velocity * elapsed, 0f, hits.MaxOffset);
        _velocity *= MathF.Pow(0.94f, elapsed / 16f);
        Invalidate();
        return !(MathF.Abs(_velocity) < 0.05f || _offset <= 0f || _offset >= hits.MaxOffset);
    }

    private void StopFling() => _fling?.Stop();

    private void OnTap(PickerHits hits, SKPoint at)
    {
        foreach (var (key, rect) in hits.Keys)
        {
            if (!rect.Contains(at)) continue;
            switch (key)
            {
                case "A": PickHighlighted(); break;
                case "X": _ = ChooseTypeAsync(); break;
                case "Y": ToggleFilter(); break;
                case "L/R": Page(1); break;
                default: Close(null); break;
            }
            return;
        }
        if (!hits.Panel.Contains(at)) { Close(null); return; }
        if (hits.Search.Contains(at)) { _ = SearchAsync(); return; }
        for (var i = 0; i < hits.Chips.Count; i++)
        {
            if (!hits.Chips[i].Contains(at)) continue;
            // The chips are laid type first, then the filter, as Scene() lists them.
            if (_hasTypes && i == 0) _ = ChooseTypeAsync();
            else ToggleFilter();
            return;
        }
        foreach (var (index, rect) in hits.Rows)
        {
            if (!rect.Contains(at) || !hits.List.Contains(at)) continue;
            // A tap aims the row (the status line and live card follow); a second tap on it picks.
            if (index == _index && _armed) { PickHighlighted(); return; }
            _index = index;
            _armed = true;
            _keepPending = true;
            UpdatePreview();
            return;
        }
    }

    // ---------- Pad ----------

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Up: Move(-1); return true;
            case PadButton.Down: Move(1); return true;
            case PadButton.L: case PadButton.Left: Page(-1); return true;
            case PadButton.R: case PadButton.Right: Page(1); return true;
            case PadButton.A: PickHighlighted(); return true;
            case PadButton.B: Close(null); return true;
            case PadButton.Y: ToggleFilter(); return true;
            case PadButton.X: _ = ChooseTypeAsync(); return true;
            case PadButton.Select: case PadButton.Start: _ = SearchAsync(); return true;
            default: return true; // the picker owns the pad while open
        }
    }

    private int PageRows => _hits is { } hits ? Math.Max(1, (int)(hits.ListHeight / hits.RowHeight) - 1) : 8;

    private void Page(int direction) => Move(direction * PageRows);

    private void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        StopFling();
        _index = Math.Clamp(_index + delta, 0, _filtered.Count - 1);
        _armed = false;
        _keepPending = true;
        UpdatePreview();
    }

    // ---------- Search and filters ----------

    /// <summary>The search prompt: the list filters behind it on every key; Cancel puts the old search back.</summary>
    private async Task SearchAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        StopFling();
        var before = _query;
        var keep = Current?.Id;
        try
        {
            var typed = await TextPopup.ShowLineAsync(_host, "Search", "Part of the name", _query, confirm: "Search",
                changed: text => { _query = text; Refilter(null); });
            _query = typed ?? before;
            Refilter(typed is null ? keep : null);
        }
        finally { _busy = false; }
    }

    private void ToggleFilter()
    {
        if (_filter is null) return;
        var keep = Current?.Id;
        _filterOn = !_filterOn;
        Refilter(keep);
    }

    /// <summary>Cuts the list to one type, picked from the types the list holds.</summary>
    private async Task ChooseTypeAsync()
    {
        if (!_hasTypes || _busy || _closed) return;
        _busy = true;
        try
        {
            const string All = "All types";
            var types = _all.Where(x => x.TypeId is not null).Select(x => x.TypeId!.Value).Distinct().Order().ToList();
            var options = new List<PadOption> { new(All, IconPath: "all") };
            options.AddRange(types.Select(t => new PadOption(TypeFacts.Name(t), Glyph: "●", Accent: InfoKit.TypeColor(t))));
            var choice = await PadMenu.ShowAsync(_host, "Move type", null, [.. options]);
            if (choice is null) return;
            var keep = Current?.Id;
            _typeFilter = choice == All ? null : types.FirstOrDefault(t => TypeFacts.Name(t) == choice, -1) is var found and >= 0 ? found : null;
            Refilter(keep);
        }
        finally { _busy = false; }
    }

    /// <summary>Rebuilds the visible list, keeping the highlight on <paramref name="keepId"/> when it survives.</summary>
    private void Refilter(int? keepId)
    {
        _filtered = Filter(_query);
        _index = keepId is { } id ? Math.Max(0, _filtered.FindIndex(x => x.Id == id)) : 0;
        _armed = false;
        if (keepId is null) _offset = 0;
        else _centerPending = true;
        UpdatePreview();
    }

    private void PickHighlighted()
    {
        if (Current is { } item) Close(item);
    }

    private void UpdatePreview()
    {
        _livePreview?.OnHighlight(Current);
        Invalidate();
    }

    private void Close(PickItem? result)
    {
        if (_closed) return;
        _closed = true;
        StopFling();
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
