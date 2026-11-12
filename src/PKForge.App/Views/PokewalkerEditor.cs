using System.Globalization;
using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The HeartGold/SoulSilver Pokéwalker as one painted screen (<see cref="PokewalkerPaint"/>): the
/// Pokémon paired with the device (read-only, as PKHeX shows it), the steps and watts, and the 27
/// courses. A sets the focused counter, opens or locks the focused course, or opens every course
/// the game's language has; the focused course's catches read in the status line. Writes are world
/// state (<see cref="SaveAction.EditWorld"/>) through the safe mutation pipeline.
/// </summary>
public sealed class PokewalkerEditor : IPadHandler
{
    private const SaveAction Action = SaveAction.EditWorld;

    private readonly Grid _host;
    private readonly ISaveEngineSession _session;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService? _sprites;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private Grid _overlay = null!;
    private PokewalkerState _state;
    private PokewalkerPokemon? _paired;
    private SKImage? _art;
    private bool _artLoaded;
    private PokewalkerHits? _hits;
    private int _cursor = PokewalkerPaint.StepsItem;
    private string _status = "";
    private bool _busy;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!PokewalkerService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Pokéwalker", "The Pokéwalker pairs only with HeartGold and SoulSilver.", "OK");
            return;
        }
        await new PokewalkerEditor(host, session, viewModel).RunAsync();
    }

    private PokewalkerEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        _host = host;
        _session = session;
        _viewModel = viewModel;
        _state = PokewalkerService.GetState(session);
        _paired = PokewalkerService.GetPairedPokemon(session);
        _sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();
        _canvas.PaintSurface += PaintGuard.Wrap("PokewalkerEditor.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("A", "Change", () => OnPadButton(PadButton.A)),
            ("<>", "Move", () => OnPadButton(PadButton.Right)),
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

    private int Rows => (int)Math.Ceiling(_state.Courses.Count / (double)PokewalkerPaint.CourseColumns);

    private async Task RunAsync()
    {
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        try { await _closed.Task; }
        finally
        {
            router?.Remove(this);
            _host.Remove(_overlay);
        }
    }

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var canEdit = !HardcoreMode.Blocks(Action, out var blocked);
        var mon = _paired is { } p
            ? new PokewalkerSceneMon(DisplayName(p), DisplayName(p) == p.SpeciesName ? "Paired with the Pokéwalker" : p.SpeciesName, p.Level, Art(p))
            : null;
        var courses = _state.Courses.Select(course => new PokewalkerSceneCourse(course.Name, course.Unlocked, course.IsSpecial, course.AvailableForLanguage)).ToList();
        var status = _status.Length > 0 ? _status : canEdit ? Hint() : blocked;
        var scene = new PokewalkerScene(mon, Number(_state.Steps), Number(_state.Watts), courses, _cursor, canEdit, status);
        _hits = PokewalkerPaint.Paint(c, args.Info, scene, _face);
    }

    /// <summary>What the focused item is, and what A does to it.</summary>
    private string Hint()
    {
        switch (_cursor)
        {
            case PokewalkerPaint.StepsItem: return "The steps the Pokéwalker has counted. A sets them.";
            case PokewalkerPaint.WattsItem: return "The watts stored on the Pokéwalker. A sets them.";
            case PokewalkerPaint.UnlockAllItem: return "A opens every course your game's language has.";
        }
        var course = _state.Courses[_cursor - PokewalkerPaint.FirstCourseItem];
        var catches = course.Encounters.Count > 0 ? string.Join(", ", course.Encounters) : "No catches listed";
        if (!course.AvailableForLanguage && !course.Unlocked)
            return $"{course.Name}: your game's language never received this course. {catches}.";
        return $"{course.Name}{(course.IsSpecial ? " (event course)" : "")}: {catches}. A {(course.Unlocked ? "locks" : "opens")} it.";
    }

    private SKImage? Art(PokewalkerPokemon mon)
    {
        if (_artLoaded) return _art;
        if (_sprites is null) return null;
        if (_sprites.GetSprite(mon.Species, mon.Form, mon.Shiny) is not { } bitmap)
        {
            _sprites.Warm(mon.Species, mon.Form, mon.Shiny, Invalidate);
            return null;
        }
        // Cropped to the Pokémon itself, so it stands on the floor line.
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var whole = SKImage.FromBitmap(bitmap);
        _art = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
        _artLoaded = true;
        return _art;
    }

    private void Focus(int item)
    {
        _cursor = Math.Clamp(item, 0, PokewalkerPaint.FirstCourseItem + _state.Courses.Count - 1);
        _status = "";
        Invalidate();
    }

    private void Move(PadButton button)
    {
        var rows = Rows;
        var count = _state.Courses.Count;
        if (_cursor is PokewalkerPaint.StepsItem or PokewalkerPaint.WattsItem)
        {
            switch (button)
            {
                case PadButton.Up: Focus(PokewalkerPaint.StepsItem); break;
                case PadButton.Down: Focus(PokewalkerPaint.WattsItem); break;
                case PadButton.Right:
                    Focus(_cursor == PokewalkerPaint.StepsItem ? PokewalkerPaint.UnlockAllItem : PokewalkerPaint.FirstCourseItem + Math.Min(rows, count) - 1);
                    break;
            }
            return;
        }
        if (_cursor == PokewalkerPaint.UnlockAllItem)
        {
            if (button == PadButton.Down) Focus(PokewalkerPaint.FirstCourseItem);
            else if (button == PadButton.Left) Focus(PokewalkerPaint.StepsItem);
            return;
        }
        var index = _cursor - PokewalkerPaint.FirstCourseItem;
        var (column, row) = (index / rows, index % rows);
        switch (button)
        {
            case PadButton.Up:
                Focus(row == 0 ? PokewalkerPaint.UnlockAllItem : _cursor - 1);
                break;
            case PadButton.Down:
                if (row < rows - 1 && index + 1 < count) Focus(_cursor + 1);
                break;
            case PadButton.Left:
                if (column == 0) Focus(row < rows / 2 ? PokewalkerPaint.StepsItem : PokewalkerPaint.WattsItem);
                else Focus(_cursor - rows);
                break;
            case PadButton.Right:
                if (index + rows < count) Focus(_cursor + rows);
                break;
        }
    }

    private async Task ActAsync()
    {
        if (_busy) return;
        if (HardcoreMode.Blocks(Action, out var blocked))
        {
            _status = blocked;
            Invalidate();
            return;
        }
        _busy = true;
        try
        {
            switch (_cursor)
            {
                case PokewalkerPaint.StepsItem:
                case PokewalkerPaint.WattsItem:
                {
                    var isSteps = _cursor == PokewalkerPaint.StepsItem;
                    var (steps, watts) = (_state.Steps, _state.Watts);
                    var value = await StatsPopup.ShowSingleAsync(_host, isSteps ? "Steps" : "Watts",
                        (int)Math.Min(isSteps ? steps : watts, PokewalkerService.MaxCounter), (int)PokewalkerService.MaxCounter,
                        $"0 - {Number(PokewalkerService.MaxCounter)}");
                    if (value is not { } v || v == (isSteps ? steps : watts)) return;
                    await WriteAsync(s =>
                    {
                        var after = PokewalkerService.SetCounters(s, isSteps ? (uint)v : steps, isSteps ? watts : (uint)v);
                        return $"Pokéwalker: {Number(after.Steps)} steps, {Number(after.Watts)} watts.";
                    });
                    break;
                }
                case PokewalkerPaint.UnlockAllItem:
                    await WriteAsync(s =>
                        $"{PokewalkerService.UnlockAll(s).Courses.Count(c => c.Unlocked)} courses open (every course your game's language has).");
                    break;
                default:
                {
                    var course = _state.Courses[_cursor - PokewalkerPaint.FirstCourseItem];
                    if (!course.AvailableForLanguage && !course.Unlocked)
                    {
                        _status = $"{course.Name} never reached your game's language, so it stays locked.";
                        Invalidate();
                        return;
                    }
                    await WriteAsync(s =>
                    {
                        PokewalkerService.SetCourse(s, course.Index, !course.Unlocked);
                        return $"{course.Name} {(course.Unlocked ? "locked" : "opened")}.";
                    });
                    break;
                }
            }
        }
        finally { _busy = false; }
    }

    private async Task WriteAsync(Func<ISaveEngineSession, string> edit)
    {
        var message = "";
        var saved = await WorldEventsMenu.WriteAsync(_viewModel, s => message = edit(s), Action);
        if (!saved)
        {
            // As the other editors: a refused write leaves the screen.
            _closed.TrySetResult();
            return;
        }
        _state = PokewalkerService.GetState(_session);
        _status = message;
        Invalidate();
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits || _busy) return;
        for (var i = 0; i < hits.Items.Count; i++)
        {
            if (!hits.Items[i].Contains(args.Location)) continue;
            // A tap picks the item; a second tap changes it.
            if (i == _cursor) _ = ActAsync();
            else Focus(i);
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right: Move(button); return true;
            case PadButton.A: _ = ActAsync(); return true;
            case PadButton.B: _closed.TrySetResult(); return true;
            default: return true;
        }
    }

    private static string Number(uint value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string DisplayName(PokewalkerPokemon mon) => string.IsNullOrWhiteSpace(mon.Nickname) ? mon.SpeciesName : mon.Nickname;
}
