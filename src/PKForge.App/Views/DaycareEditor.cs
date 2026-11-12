using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Safe Day Care / Nursery view, as one painted screen (<see cref="DaycarePaint"/>): both slots
/// side by side with the Pokémon large, its level and EXP, the egg between them. Left/right pick
/// a slot, A (or a second tap) withdraws it to the first empty PC slot, L/R turn to the other
/// facility, B leaves. Deposits, eggs, and RNG state remain game-owned.
/// </summary>
public sealed class DaycareEditor : IPadHandler
{
    private readonly Grid _host;
    private readonly ISaveEngineSession _session;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService? _sprites;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly Dictionary<(int, int, bool), SKImage?> _art = [];
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private Grid _overlay = null!;
    private DaycareInfo _info = new(false, []);
    private DaycareHits? _hits;
    private int _facility;
    private int _cursor;
    private string _status = "";
    private bool _busy;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var info = session.GetDaycare();
        if (!info.Supported || info.Facilities.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "Day care", "This game has no supported Day Care or Nursery storage.", "OK");
            return;
        }
        await new DaycareEditor(host, session, viewModel, info).RunAsync();
    }

    private DaycareEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, DaycareInfo info)
    {
        _host = host;
        _session = session;
        _viewModel = viewModel;
        _info = info;
        _sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();
        _cursor = Math.Max(0, Facility.Slots.ToList().FindIndex(slot => slot.Occupied));
        _canvas.PaintSurface += PaintGuard.Wrap("DaycareEditor.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("A", "Withdraw", () => OnPadButton(PadButton.A)),
            ("<>", "Slot", () => OnPadButton(PadButton.Right)),
            ("LR", "Facility", () => OnPadButton(PadButton.R)),
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

    private DaycareFacility Facility => _info.Facilities[Math.Clamp(_facility, 0, _info.Facilities.Count - 1)];

    // The egg rocks now and then while it waits: a rest, then left, middle, right, middle.
    private static readonly string[] Rock = ["mid", "mid", "mid", "mid", "mid", "mid", "mid", "mid", "left", "mid", "right", "mid", "left", "mid"];
    private int _tick;

    private SKImage? EggFrame(bool ready) =>
        EggSprite.Get("wobble-" + (ready && MotionSetting.Decorative ? Rock[_tick % Rock.Length] : "mid"));

    private async Task RunAsync()
    {
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        var timer = _canvas.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(170);
        timer.Tick += (_, _) =>
        {
            if (!MotionSetting.Decorative || !Facility.EggAvailable) return;
            _tick++;
            _canvas.InvalidateSurface();
        };
        timer.Start();
        try { await _closed.Task; }
        finally
        {
            timer.Stop();
            router?.Remove(this);
            _host.Remove(_overlay);
        }
    }

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var facility = Facility;
        var slots = facility.Slots.Select(slot => new DaycareSceneSlot(slot.Index, slot.Occupied, DisplayName(slot), slot.SpeciesName,
            slot.Level, slot.Experience is { } exp ? $"EXP {exp}" : null, slot.Occupied ? Art(slot) : null)).ToList();
        var name = facility.Name == "Day Care" ? "Day Care" : facility.Name;
        var scene = new DaycareScene(_info.Facilities.Count > 1 ? $"{name} ({_facility + 1}/{_info.Facilities.Count})" : name,
            _facility > 0, _facility < _info.Facilities.Count - 1, facility.EggAvailable, slots, _cursor,
            HardcoreMode.Guard.Allows(SaveAction.Move), _status, EggFrame(facility.EggAvailable));
        _hits = DaycarePaint.Paint(c, args.Info, scene, _face);
    }

    private SKImage? Art(DaycareSlot slot)
    {
        if (_sprites is null || slot.Species <= 0) return null;
        var key = (slot.Species, slot.Form, slot.Shiny);
        if (_art.TryGetValue(key, out var known)) return known;
        if (_sprites.GetSprite(slot.Species, slot.Form, slot.Shiny) is not { } bitmap)
        {
            _sprites.Warm(slot.Species, slot.Form, slot.Shiny, Invalidate);
            return null;
        }
        // Cropped to the Pokémon itself, so it stands on the floor line.
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var whole = SKImage.FromBitmap(bitmap);
        var image = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
        _art[key] = image;
        return image;
    }

    private void MoveCursor(int delta)
    {
        var count = Math.Min(2, Facility.Slots.Count);
        if (count == 0) return;
        _cursor = Math.Clamp(_cursor + delta, 0, count - 1);
        _status = "";
        Invalidate();
    }

    private void TurnFacility(int delta)
    {
        var next = Math.Clamp(_facility + delta, 0, _info.Facilities.Count - 1);
        if (next == _facility) return;
        _facility = next;
        _cursor = Math.Max(0, Facility.Slots.ToList().FindIndex(slot => slot.Occupied));
        _status = "";
        Invalidate();
    }

    private async Task WithdrawAsync()
    {
        if (_busy || _cursor >= Facility.Slots.Count) return;
        var slot = Facility.Slots[_cursor];
        if (!slot.Occupied)
        {
            _status = "This slot is empty.";
            Invalidate();
            return;
        }
        _busy = true;
        try
        {
            var confirmed = await PadMenu.ConfirmAsync(_host, "WITHDRAW POKéMON?",
                $"{FullName(slot)} moves to the first empty PC box slot. Restore points can undo it.", "Withdraw");
            if (!confirmed) return;
            var facilityIndex = _facility;
            var message = "";
            var saved = await _viewModel.RunMutationAsync(s =>
            {
                var result = s.WithdrawDaycareToFirstEmptyBox(facilityIndex, slot.Index);
                message = $"{result.SpeciesName} moved to Box {result.Box + 1}, Slot {result.Slot + 1}.";
                return new GenerationOutcome(true, message);
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!saved)
            {
                // As before: a refused write leaves the Day Care screen.
                _closed.TrySetResult();
                return;
            }
            _info = _session.GetDaycare();
            if (_info.Facilities.Count == 0) { _closed.TrySetResult(); return; }
            _status = message;
            Invalidate();
        }
        finally { _busy = false; }
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var at = args.Location;
        if (hits.Previous.Contains(at)) { TurnFacility(-1); return; }
        if (hits.Next.Contains(at)) { TurnFacility(1); return; }
        for (var i = 0; i < hits.Withdraw.Count; i++)
            if (hits.Withdraw[i].Contains(at)) { _cursor = i; Invalidate(); _ = WithdrawAsync(); return; }
        for (var i = 0; i < hits.Slots.Count; i++)
        {
            if (!hits.Slots[i].Contains(at)) continue;
            // A tap picks the slot; a second tap withdraws it.
            if (i == _cursor) _ = WithdrawAsync();
            else { _cursor = i; _status = ""; Invalidate(); }
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left: MoveCursor(-1); return true;
            case PadButton.Right: MoveCursor(1); return true;
            case PadButton.L: TurnFacility(-1); return true;
            case PadButton.R: TurnFacility(1); return true;
            case PadButton.A: _ = WithdrawAsync(); return true;
            case PadButton.B: _closed.TrySetResult(); return true;
            default: return true;
        }
    }

    private static string FullName(DaycareSlot slot) => DisplayName(slot) == slot.SpeciesName
        ? slot.SpeciesName
        : $"{slot.Nickname} ({slot.SpeciesName})";

    private static string DisplayName(DaycareSlot slot) => string.IsNullOrWhiteSpace(slot.Nickname) || slot.Nickname == slot.SpeciesName
        ? slot.SpeciesName
        : slot.Nickname;
}
