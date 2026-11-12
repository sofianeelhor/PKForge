using System.Diagnostics;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>What the overview closed on: the box to open, and the group the player was
/// looking at (null for all boxes), which L/R then stay within.</summary>
public sealed record BankOverviewResult(int Box, string? Group);

/// <summary>
/// Every Bank box at a glance, painted by <see cref="BankOverviewPaint"/>: mini boxes of
/// icons under their names, gathered under their groups. The d-pad walks the cards, L/R jump
/// between groups, SELECT (or the banner's chevrons) shows one group alone. Pick a box up and
/// carry it (Y, or a long press); X switches to swap. Dropping a box among another group's
/// boxes puts it in that group. Rename, wallpaper, lock, group, insert and delete from the box
/// menu (X). Every whole-box change is one Bank write through <see cref="BankPage.ApplyBoxRemap"/>,
/// so names, wallpapers, locks, groups, marks and the living dex region follow their box.
/// </summary>
public sealed class BankBoxOverview : IPadPagingHandler
{
    private const int Slots = IBankService.SlotsPerBox;

    private enum Carry { None, Move, Swap }

    private readonly TaskCompletionSource<BankOverviewResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BankPage _page;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly SKImage? _padlock;

    private BankEntry?[][] _boxes = [];
    private IReadOnlyList<BankBoxGroup> _groups = [];
    private BankOverviewCard?[] _cards = [];
    private int _cursor;
    private int? _held;
    private Carry _carry;
    private bool _busy;
    private string? _view; // null: all boxes; else one group alone
    private string _status = "";

    // The scroll, in design pixels. With full motion it eases to its target in a few frames
    // (no spring, no overshoot); otherwise it jumps there.
    private float _scroll;
    private float _scrollFrom;
    private float _scrollTo;
    private readonly Stopwatch _scrollClock = new();
    private const float ScrollMs = 110f;

    private BankOverviewLayout? _layout;
    private BankOverviewHits? _hits;
    private IReadOnlyList<(string Key, string Label, PadButton Button)> _prompts = [];

    // Touch: a long press lifts the card under the finger; dragging then moves the cursor.
    private SKPoint? _pressAt;
    private bool _dragging;
    private bool _scrolling;
    private float _scrollStart;
    private IDispatcherTimer? _longPress;

    public static Task<BankOverviewResult?> ShowAsync(BankPage page, Grid host, IBankService bank, ISpriteService sprites, int currentBox, string? group) =>
        new BankBoxOverview(page, host, bank, sprites, currentBox, group)._result.Task;

    private BankBoxOverview(BankPage page, Grid host, IBankService bank, ISpriteService sprites, int currentBox, string? group)
    {
        _page = page;
        _host = host;
        _bank = bank;
        _sprites = sprites;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += PaintGuard.Wrap("BankBoxOverview.Paint", Paint);
        _canvas.Touch += Touch;
        _frame = new FrameInvalidator(_canvas);
        using (var padlock = SKBitmap.Decode(PksmIcons.GetPng("padlock", PksmIcons.White)))
            if (padlock is not null) _padlock = SKImage.FromBitmap(padlock);

        var window = new Border
        {
            Content = _canvas,
            Padding = 0,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Margin = new Thickness(10, 8),
        };
        // Tapping outside does what B does: cancel a carry, else close.
        _overlay = Kit.AttachOverlay(host, window, () =>
        {
            if (_busy) return;
            if (_held is not null) CancelCarry();
            else Close(null);
        });

        Reload();
        _view = group is not null && BankBoxGroups.Exists(_groups, group) ? group : null;
        _cursor = Math.Clamp(currentBox, 0, _boxes.Length - 1);
        if (_view is not null && !Shown().Contains(_cursor)) _cursor = Shown().FirstOrDefault(_cursor);
        _snapNext = true;
        _router?.Push(this);
    }

    // ── Model ──────────────────────────────────────────────────────────────

    private void Reload()
    {
        var count = _bank.BoxCount;
        var boxes = new BankEntry?[count][];
        for (var box = 0; box < count; box++) boxes[box] = new BankEntry?[Slots];
        foreach (var entry in _bank.GetAll())
            if ((uint)entry.Box < (uint)count && (uint)entry.Slot < Slots) boxes[entry.Box][entry.Slot] = entry;
        _boxes = boxes;
        _groups = _bank.BoxGroups;
        if (_view is not null && !BankBoxGroups.Exists(_groups, _view)) _view = null;
        _cards = new BankOverviewCard?[count];
        _layout = null;
        _cursor = Math.Clamp(_cursor, 0, count - 1);
    }

    private int Occupied(int box) => _boxes[box].Count(e => e is not null);

    /// <summary>The runs on screen: one group alone, or every group then the rest.</summary>
    private List<BankOverviewRun> Runs()
    {
        if (_view is not null) return [new BankOverviewRun(_view, BankBoxGroups.Members(_groups, _view), Heading: false)];
        if (_groups.Count == 0) return [new BankOverviewRun(null, [.. Enumerable.Range(0, _boxes.Length)], Heading: false)];
        return [.. BankBoxGroups.Sections(_groups, _boxes.Length).Select(s => new BankOverviewRun(s.Name, s.Boxes))];
    }

    /// <summary>The boxes on screen, in reading order.</summary>
    private List<int> Shown() => [.. Runs().SelectMany(r => r.Boxes)];

    /// <summary>The group of the run <paramref name="box"/> shows in (null: no group).</summary>
    private string? GroupOf(int box) => BankBoxGroups.GroupOf(_groups, box);

    private BankOverviewCard Card(int box)
    {
        if (_cards[box] is { } cached) return cached;
        var complete = true;
        var icons = new SKImage?[Slots];
        for (var slot = 0; slot < Slots; slot++)
        {
            if (_boxes[box][slot] is not { } entry) continue;
            // The storage screen's box icons (Showdown's sheet, 40×30), so a mini box reads like the real one.
            if (!_sprites.TryGetShowdownIcon(entry.Info.Look, InvalidateCards, out var sheet, out var cell)) complete = false;
            else if (sheet is not null) icons[slot] = IconOf(sheet, cell);
            else if (_sprites.GetSprite(entry.Info.Look) is { } sprite) icons[slot] = SpriteImages.Of(sprite);
            else
            {
                complete = false;
                _sprites.Warm(entry.Info.Look, InvalidateCards);
            }
        }
        var art = BankBoxArt.Art(box, InvalidateCards);
        complete &= BankBoxArt.IsReady(box);
        var card = new BankOverviewCard(box, BankBoxDecor.Label(box), Occupied(box), _bank.LockedBoxes.Contains(box),
            LivingDexAutopilot.BankStartBox == box, icons, art is { } a ? SpriteImages.Of(a.Art) : null,
            art?.Average ?? default, BankBoxArt.Tone(box));
        if (complete) _cards[box] = card;
        return card;
    }

    private void InvalidateCards() => _frame.Request();

    // One image per icon cell of the sheet, cut once for the app's lifetime (a few hundred at most).
    private static readonly Dictionary<(SKBitmap, SKRectI), SKImage> Icons = [];

    private static SKImage? IconOf(SKBitmap sheet, SKRectI cell)
    {
        lock (Icons)
        {
            if (Icons.TryGetValue((sheet, cell), out var icon)) return icon;
            icon = SpriteImages.Of(sheet).Subset(cell);
            if (icon is not null) Icons[(sheet, cell)] = icon;
            return icon;
        }
    }

    // ── Input ──────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left: Walk(-1); return true;
            case PadButton.Right: Walk(1); return true;
            case PadButton.Up: Climb(-1); return true;
            case PadButton.Down: Climb(1); return true;
            case PadButton.L: Jump(-1); return true;
            case PadButton.R: Jump(1); return true;
            case PadButton.Select:
                if (_held is null) CycleView(1);
                return true;
            case PadButton.A:
                if (_held is not null) Drop();
                else if (Shown().Contains(_cursor)) Close(_cursor);
                return true;
            case PadButton.Y:
                if (_held is null) { if (Shown().Contains(_cursor)) PickUp(_cursor); }
                else Drop();
                return true;
            case PadButton.X:
                if (_held is not null) ToggleCarryMode();
                else if (Shown().Contains(_cursor)) _ = BoxMenuAsync();
                else if (_view is not null) _ = GroupMenuAsync(null);
                return true;
            case PadButton.Start:
                if (_held is null) _ = AddBoxAsync(_boxes.Length, _view);
                return true;
            case PadButton.B:
                if (_held is not null) CancelCarry();
                else Close(null);
                return true;
            default:
                return true;
        }
    }

    /// <summary>Left/right: the previous or next card in reading order, wrapping.</summary>
    private void Walk(int delta)
    {
        var shown = Shown();
        if (shown.Count == 0) return;
        var at = shown.IndexOf(_cursor);
        _cursor = shown[((at < 0 ? 0 : at + delta) % shown.Count + shown.Count) % shown.Count];
        Moved();
    }

    /// <summary>Up/down: the card in the row above or below, the nearest column.</summary>
    private void Climb(int delta)
    {
        if (_layout is not { } layout || layout.Cards.Count == 0) return;
        var here = layout.Cards.FirstOrDefault(c => c.Box == _cursor);
        if (here.Rect.Height <= 0) { _cursor = layout.Cards[0].Box; Moved(); return; }
        var row = layout.Cards.Where(c => c.Row == here.Row + delta).ToList();
        if (row.Count == 0) return;
        _cursor = row.MinBy(c => Math.Abs(c.Column - here.Column)).Box;
        Moved();
    }

    /// <summary>L/R: the first box of the previous or next group. With one run, a page of rows.</summary>
    private void Jump(int delta)
    {
        var runs = Runs().Where(r => r.Boxes.Count > 0).ToList();
        if (runs.Count == 0) return;
        if (runs.Count == 1)
        {
            var shown = runs[0].Boxes;
            var rows = _layout is { } layout ? Math.Max(1, (int)(layout.Viewport.Height / BankOverviewPaint.RowPitch)) : 2;
            var at = Math.Max(0, shown.ToList().IndexOf(_cursor));
            _cursor = shown[Math.Clamp(at + delta * BankOverviewPaint.Columns * rows, 0, shown.Count - 1)];
            Moved();
            return;
        }
        var current = runs.FindIndex(r => r.Boxes.Contains(_cursor));
        var next = current < 0 ? 0 : ((current + delta) % runs.Count + runs.Count) % runs.Count;
        _cursor = runs[next].Boxes[0];
        Moved();
    }

    /// <summary>SELECT and the chevrons: all boxes, then each group alone, then all boxes again.</summary>
    private void CycleView(int delta)
    {
        if (_groups.Count == 0)
        {
            _status = "No groups yet: make one from a box's menu (X).";
            _frame.Request();
            return;
        }
        var views = new List<string?> { null };
        views.AddRange(_groups.Select(g => g.Name));
        var at = views.FindIndex(v => string.Equals(v, _view, StringComparison.OrdinalIgnoreCase));
        _view = views[((at + delta) % views.Count + views.Count) % views.Count];
        _layout = null;
        var shown = Shown();
        if (shown.Count > 0 && !shown.Contains(_cursor)) _cursor = shown[0];
        _status = _view is null ? "" : "L/R in the box stay within this group.";
        _snapNext = true;
        _frame.Request();
    }

    private void Moved()
    {
        _status = "";
        _frame.Request();
    }

    private void PickUp(int box)
    {
        _held = box;
        _carry = Carry.Move;
        _frame.Request();
    }

    private void ToggleCarryMode()
    {
        _carry = _carry == Carry.Move ? Carry.Swap : Carry.Move;
        _frame.Request();
    }

    private void CancelCarry()
    {
        if (_held is { } held) _cursor = held;
        _held = null;
        _carry = Carry.None;
        _dragging = false;
        _frame.Request();
    }

    private void Drop()
    {
        if (_held is not { } held) return;
        var target = _cursor;
        var swap = _carry == Carry.Swap;
        _held = null;
        _carry = Carry.None;
        _dragging = false;
        if (held == target) { _frame.Request(); return; }

        var heldGroup = GroupOf(held);
        var targetGroup = GroupOf(target);
        var remap = swap ? BankBoxRemap.Swap(_boxes.Length, held, target) : BankBoxRemap.Move(_boxes.Length, held, target);
        if (!_page.ApplyBoxRemap(remap)) { Reload(); _frame.Request(); return; }
        var moved = remap.Map(held) ?? held;
        // A box dropped among another group's boxes joins that group; swapped boxes trade groups,
        // so each lands where it was shown.
        if (!string.Equals(heldGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
        {
            var other = remap.Map(target) ?? target;
            _page.ChangeGroups(groups =>
            {
                groups = BankBoxGroups.Assign(groups, moved, targetGroup);
                return swap ? BankBoxGroups.Assign(groups, other, heldGroup) : groups;
            }, null);
            _status = targetGroup is null ? $"{BankBoxDecor.Label(moved)} is in no group now." : $"{BankBoxDecor.Label(moved)} joined {targetGroup}.";
        }
        Reload();
        _cursor = moved;
        _frame.Request();
    }

    private async Task BoxMenuAsync()
    {
        var box = _cursor;
        var count = Occupied(box);
        var locked = _bank.LockedBoxes.Contains(box);
        var group = GroupOf(box);
        var options = new List<PadOption>
        {
            new("Open this box", IconPath: "box"),
            new("Pick it up to move it", IconPath: "move"),
            new("Rename…", IconPath: "rename"),
            new("Wallpaper…", IconPath: "fashion"),
            new(locked ? "Unlock this box" : "Lock this box", IconPath: "padlock",
                Detail: "A locked box is left out of sorts, moves, sends, releases and deposits. It can still be opened."),
            new("Group…", IconPath: "blocks",
                Detail: group is null ? "In no group. Groups gather boxes (Shiny, Living dex) without moving them." : $"In {group}."),
            new("Insert a new box before it", IconPath: "create"),
            new("Insert a new box after it", IconPath: "create"),
        };
        if (_boxes.Length > 1 && !locked) options.Add(new PadOption("Delete this box…", IconPath: "delete"));

        _busy = true;
        string? choice;
        try
        {
            var where = group is null ? "" : $" In {group}.";
            choice = await PadMenu.ShowAsync(_host, BankBoxDecor.Label(box),
                (count == 0 ? "Empty box." : $"{count} / {Slots} Pokémon.") + where, [.. options]);
        }
        finally { _busy = false; }

        switch (choice)
        {
            case "Open this box":
                Close(box);
                return;
            case "Pick it up to move it":
                PickUp(box);
                return;
            case "Rename…":
                await RunAsync(() => _page.RenameBoxAsync(box));
                return;
            case "Wallpaper…":
                await RunAsync(() => _page.PickWallpaperAsync(box));
                return;
            case "Lock this box" or "Unlock this box":
                _page.ToggleBoxLock(box);
                Reload();
                _frame.Request();
                return;
            case "Group…":
                await GroupMenuAsync(box);
                return;
            case "Insert a new box before it":
                await AddBoxAsync(box, group);
                return;
            case "Insert a new box after it":
                await AddBoxAsync(box + 1, group);
                return;
            case "Delete this box…":
                await RunAsync(() => _page.DeleteBoxAsync(box));
                return;
        }
    }

    /// <summary>
    /// The group menu for <paramref name="box"/> (or for the group on screen when null): put
    /// the box in a group, take it out, make a group, rename or delete one. A group is only a
    /// label over boxes: deleting one never moves or deletes a box or a Pokémon.
    /// </summary>
    private async Task GroupMenuAsync(int? box)
    {
        var current = box is { } b ? GroupOf(b) : _view;
        var options = new List<PadOption>();
        var putIn = new Dictionary<string, string>();
        if (box is not null)
        {
            foreach (var group in _groups.Where(g => !string.Equals(g.Name, current, StringComparison.OrdinalIgnoreCase)))
            {
                var label = $"Put it in {group.Name}";
                if (putIn.TryAdd(label, group.Name))
                    options.Add(new PadOption(label, IconPath: "move", Detail: $"{group.Boxes.Count} box{(group.Boxes.Count == 1 ? "" : "es")}."));
            }
            options.Add(new PadOption("New group with this box…", IconPath: "create"));
            if (current is not null) options.Add(new PadOption($"Take it out of {current}", IconPath: "back"));
        }
        else options.Add(new PadOption("New group…", IconPath: "create"));
        if (current is not null)
        {
            options.Add(new PadOption($"Rename {current}…", IconPath: "rename"));
            options.Add(new PadOption($"Delete the group {current}…", IconPath: "delete",
                Detail: "Its boxes and their Pokémon stay where they are, just in no group."));
        }

        _busy = true;
        string? choice;
        try
        {
            choice = await PadMenu.ShowAsync(_host, box is { } shown ? $"Group · {BankBoxDecor.Label(shown)}" : $"Group · {current}",
                current is null ? "In no group yet." : $"In {current}.", [.. options]);
        }
        finally { _busy = false; }
        if (choice is null) return;

        if (putIn.TryGetValue(choice, out var into) && box is { } moving)
        {
            Apply(groups => BankBoxGroups.Assign(groups, moving, into), $"{BankBoxDecor.Label(moving)} is in {into} now.");
            return;
        }
        if (choice.StartsWith("New group", StringComparison.Ordinal))
        {
            var name = await AskNameAsync("New group", "");
            if (name is null) return;
            Apply(groups => box is { } member ? BankBoxGroups.Create(groups, name, member) : BankBoxGroups.Create(groups, name),
                box is null ? $"Group {name} made." : $"Group {name} made with {BankBoxDecor.Label(box.Value)}.");
            return;
        }
        if (current is null) return;
        if (choice.StartsWith("Take it out", StringComparison.Ordinal) && box is { } leaving)
        {
            Apply(groups => BankBoxGroups.Assign(groups, leaving, null), $"{BankBoxDecor.Label(leaving)} left {current}.");
            return;
        }
        if (choice.StartsWith("Rename", StringComparison.Ordinal))
        {
            var name = await AskNameAsync($"Rename {current}", current);
            if (name is null) return;
            var renamed = BankBoxGroups.Clean(name);
            if (Apply(groups => BankBoxGroups.Rename(groups, current, renamed), $"{current} is now {renamed}.")
                && string.Equals(_view, current, StringComparison.OrdinalIgnoreCase))
                _view = renamed;
            return;
        }
        if (choice.StartsWith("Delete the group", StringComparison.Ordinal))
        {
            _busy = true;
            bool sure;
            try
            {
                sure = await PadMenu.ConfirmAsync(_host, $"Delete the group {current}?",
                    "Only the group goes: its boxes and their Pokémon stay where they are, in no group.", "Delete group");
            }
            finally { _busy = false; }
            if (!sure) return;
            if (Apply(groups => BankBoxGroups.Delete(groups, current), $"Group {current} deleted; its boxes stay.")
                && string.Equals(_view, current, StringComparison.OrdinalIgnoreCase))
                _view = null;
        }
    }

    private async Task<string?> AskNameAsync(string title, string current)
    {
        _busy = true;
        try
        {
            var name = await TextPopup.ShowLineAsync(_host, title, $"Up to {BankBoxGroups.MaxNameLength} characters, like Shiny or Living dex", current);
            name = name is null ? null : BankBoxGroups.Clean(name);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        finally { _busy = false; }
    }

    private bool Apply(Func<IReadOnlyList<BankBoxGroup>, IReadOnlyList<BankBoxGroup>> change, string done)
    {
        var ok = _page.ChangeGroups(change, done);
        _status = ok ? done : _page.StatusText;
        Reload();
        _snapNext = true;
        _frame.Request();
        return ok;
    }

    private async Task RunAsync(Func<Task<bool>> action)
    {
        _busy = true;
        try
        {
            if (await action()) Reload();
            _frame.Request();
        }
        finally { _busy = false; }
    }

    /// <summary>A new empty box at <paramref name="at"/>, joining <paramref name="group"/>: the
    /// group on screen for "+", the neighbour's group when inserted beside a box.</summary>
    private Task AddBoxAsync(int at, string? group)
    {
        var remap = BankBoxRemap.Insert(_boxes.Length, at);
        if (_page.ApplyBoxRemap(remap))
        {
            if (group is not null) _page.ChangeGroups(groups => BankBoxGroups.Assign(groups, at, group), null);
            Reload();
            _cursor = at;
            _status = group is null ? $"{BankBoxDecor.Label(at)} added." : $"{BankBoxDecor.Label(at)} added to {group}.";
        }
        _frame.Request();
        return Task.CompletedTask;
    }

    private void Close(int? box)
    {
        _longPress?.Stop();
        _router?.Remove(this);
        _host.Remove(_overlay);
        _padlock?.Dispose();
        _result.TrySetResult(box is { } b ? new BankOverviewResult(b, _view) : null);
    }

    // ── Touch ──────────────────────────────────────────────────────────────

    private int? BoxAt(SKPoint point) =>
        _hits is { } hits && hits.Viewport.Contains(point) ? hits.Cards.FirstOrDefault(c => c.Rect.Contains(point)) is { Rect.Width: > 0 } hit ? hit.Box : null : null;

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_busy || _hits is not { } hits) return;
        var point = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                _pressAt = point;
                _scrolling = false;
                _scrollStart = _scrollTo;
                StartLongPress();
                break;
            case SKTouchAction.Moved when _pressAt is { } start:
                if (_dragging)
                {
                    if (BoxAt(point) is { } over && over != _cursor) { _cursor = over; _frame.Request(); }
                }
                else if (_scrolling || Math.Abs(point.Y - start.Y) > 18 * hits.Unit)
                {
                    _longPress?.Stop();
                    _scrolling = true;
                    SetScroll(_scrollStart - (point.Y - start.Y) / hits.Unit, snap: true);
                }
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                _longPress?.Stop();
                var tapped = args.ActionType == SKTouchAction.Released && !_scrolling && _pressAt is not null;
                _pressAt = null;
                _scrolling = false;
                if (_dragging) { Drop(); break; }
                if (!tapped) break;
                if (hits.Prompts.Select((r, i) => (r, i)).FirstOrDefault(p => p.r.Contains(point)) is { r.Width: > 0 } prompt && prompt.i < _prompts.Count)
                {
                    OnPadButton(_prompts[prompt.i].Button);
                    break;
                }
                if (hits.Previous.Contains(point)) { if (_held is null) CycleView(-1); break; }
                if (hits.Next.Contains(point)) { if (_held is null) CycleView(1); break; }
                if (BoxAt(point) is { } box)
                {
                    if (_held is not null) { _cursor = box; Drop(); }
                    else if (box == _cursor) Close(box); // a second tap opens it
                    else { _cursor = box; Moved(); }
                }
                break;
        }
    }

    private void StartLongPress()
    {
        _longPress ??= Application.Current?.Dispatcher.CreateTimer();
        if (_longPress is null) return;
        _longPress.Stop();
        _longPress.Interval = TimeSpan.FromMilliseconds(380);
        _longPress.IsRepeating = false;
        _longPress.Tick -= OnLongPress;
        _longPress.Tick += OnLongPress;
        _longPress.Start();
    }

    private void OnLongPress(object? sender, EventArgs e)
    {
        if (_pressAt is not { } start || _scrolling || _held is not null || BoxAt(start) is not { } box) return;
        _cursor = box;
        _dragging = true;
        PickUp(box);
    }

    // ── Paint ──────────────────────────────────────────────────────────────

    private bool _snapNext;

    private void SetScroll(float target, bool snap)
    {
        var max = _layout is { } layout ? Math.Max(0, layout.ContentHeight - layout.Viewport.Height) : target;
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(target - _scrollTo) < 0.5f && !snap) return;
        _scrollFrom = _scroll;
        _scrollTo = target;
        if (snap || !MotionSetting.Decorative) _scroll = target;
        else _scrollClock.Restart();
        _frame.Request();
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        canvas.Clear(SKColors.Transparent);
        var runs = Runs();
        var layout = _layout = BankOverviewPaint.Layout(runs, info.Size);
        SetScroll(BankOverviewPaint.ScrollFor(layout, _cursor, _scrollTo), _snapNext);
        _snapNext = false;
        if (_scroll != _scrollTo)
        {
            var t = Math.Clamp(_scrollClock.ElapsedMilliseconds / ScrollMs, 0f, 1f);
            var eased = 1 - (1 - t) * (1 - t);
            _scroll = t >= 1f ? _scrollTo : _scrollFrom + (_scrollTo - _scrollFrom) * eased;
        }

        PksmPaint.LogoGrid(canvas, new SKRect(0, 0, info.Width, info.Height), 32 * info.Width / BankOverviewPaint.DesignWidth);
        var cards = new BankOverviewCard[_boxes.Length];
        var shown = runs.SelectMany(r => r.Boxes).ToHashSet();
        for (var box = 0; box < cards.Length; box++)
            cards[box] = shown.Contains(box) ? Card(box) : new BankOverviewCard(box, "", 0, false, false, [], null, default, default);

        _prompts = _held is null
            ?
            [
                ("A", "Open", PadButton.A), ("Y", "Pick up", PadButton.Y), ("X", "Box menu", PadButton.X),
                ("+", "New box", PadButton.Start), ("-", "View", PadButton.Select),
                ("LR", runs.Count(r => r.Boxes.Count > 0) > 1 ? "Group" : "Page", PadButton.R), ("B", "Close", PadButton.B),
            ]
            : [("A", "Drop here", PadButton.A), ("X", _carry == Carry.Swap ? "Move instead" : "Swap instead", PadButton.X), ("B", "Cancel", PadButton.B)];

        var total = _boxes.Sum(b => b.Count(e => e is not null));
        var status = _status.Length > 0 ? _status
            : _held is { } held ? $"{(_carry == Carry.Swap ? "Swapping" : "Moving")} {BankBoxDecor.Label(held)}"
            : $"{_boxes.Length} boxes · {total} Pokémon";
        var title = _view ?? "All boxes";
        var scene = new BankOverviewScene(cards, runs, title, _groups.Count > 0 && _held is null, _cursor, _scroll, _held,
            _carry == Carry.Swap, status, [.. _prompts.Select(p => (p.Key, p.Label))], BoxBackground.Style, _padlock,
            "Put boxes in it from a box's menu (X), or X here to rename it.");
        _hits = BankOverviewPaint.Paint(canvas, info, scene, _face);
        if (_scroll != _scrollTo) _frame.Request();
    }
}
