using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The dex as a PC box: a 6x5 grid of sprites on wallpaper pages. The cursor only
/// moves with the pad; A cycles unseen → seen → caught. Touch taps the cell directly.
/// Edits are staged and saved through the safe write path on exit.
/// </summary>
public sealed class DexEditorPage : IPadHandler
{
    private const int Columns = BoxGridRenderer.Columns;
    private const int Rows = BoxGridRenderer.Rows;
    private const int PageSize = Columns * Rows;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession _session;
    private readonly ILegalizerService _legalizer;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly Dictionary<int, (bool Seen, bool Caught)> _states = [];
    private readonly Dictionary<int, (bool Seen, bool Caught)> _staged = [];
    private readonly HashSet<int> _fillSelection = [];
    private readonly List<int> _missing = [];
    private readonly List<int> _orderedIds = [];
    private readonly List<int> _viewIds = [];
    private readonly SKCanvasView _canvas;
    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _cursorInfo;
    private bool _gapsMode;
    private bool _loaded;
    private int _page;
    private int _cursor; // index within the current page
    private string _query = "";

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session,
        ILegalizerService legalizer, IGameDataService data, ISpriteService sprites)
    {
        try
        {
            await new DexEditorPage(host, viewModel, session, legalizer, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"Dex editor closed: {error.Message}";
        }
    }

    private DexEditorPage(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session,
        ILegalizerService legalizer, IGameDataService data, ISpriteService sprites)
    {
        _host = host;
        _viewModel = viewModel;
        _session = session;
        _legalizer = legalizer;
        _data = data;
        _sprites = sprites;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _title = new Label { Text = "POKéDEX", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        _progress = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = 13, HorizontalTextAlignment = TextAlignment.End, HorizontalOptions = LayoutOptions.End };
        _cursorInfo = new Label { TextColor = UiTokens.Maroon, FontFamily = DsChrome.PixelFont, FontSize = 13 };

        _canvas = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        View hints = Kit.HintBar(
            ("A", "Cycle", null),
            ("B", "Done", () => _ = CloseAsync()),
            ("LR", "Page", null),
            ("Y", "Select all (gaps)", SelectAllGaps),
            ("X", "Actions", () => _ = ShowActionsAsync()));

        var search = new Entry
        {
            Placeholder = "Search a Pokémon…",
            FontSize = 14,
            TextColor = UiTokens.Ink0,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
            HeightRequest = 36,
            Margin = new Thickness(4, 0),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        search.TextChanged += (_, args) =>
        {
            _query = args.NewTextValue ?? "";
            RefreshView();
        };

        var content = new Grid
        {
            RowSpacing = 6,
            // header / search / SPRITE GRID (the only elastic row) / cursor line / hints.
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children =
            {
                    new Grid
                    {
                        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                        Children = { _title, _progress },
                    },
                    search,
                    _canvas,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetRow(search, 1);
        Grid.SetRow(_canvas, 2);
        Grid.SetRow(_cursorInfo, 3);
        Grid.SetRow(hints, 4);

        var window = Kit.DevicePanel(content, padding: 10);
        window.Margin = new Thickness(24, 12);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => _ = CloseAsync();
        scrim.GestureRecognizers.Add(scrimTap);
        _overlay = new Grid { Children = { scrim, window } };
        _host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, _host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, _host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        var loader = LoadingOverlay.Show(_host, "OPENING THE POKéDEX…", "Reading every dex cell and storage slot.");
        _ = Task.Run(async () =>
        {
            try
            {
                var states = new Dictionary<int, (bool, bool)>();
                var total = _session.GetDexProgress().Total;
                var max = Math.Min(_data.SpeciesNames.Count, total + 1);
                for (var id = 1; id < max; id++)
                {
                    if (_data.SpeciesNames[id].Length == 0) continue;
                    var state = _session.GetDexEntry(id);
                    states[id] = (state.Seen, state.Caught);
                }
                var missing = _session.GetMissingSpecies();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var pair in states) _states[pair.Key] = pair.Value;
                    _orderedIds.AddRange(states.Keys.Order());
                    _missing.AddRange(missing);
                    RefreshView();
                    _loaded = true;
                    loader.Close();
                    RefreshChrome();
                    _router?.Push(this);
                });
            }
            catch (Exception error)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    loader.Close();
                    _viewModel.Status = $"Dex editor failed: {error.Message}";
                    TearDown();
                    _result.TrySetResult(false);
                });
            }
        });
    }

    private int Count => _viewIds.Count;
    private int PageCount => Math.Max(1, (Count + PageSize - 1) / PageSize);
    private int IdAt(int page, int index) => _viewIds[page * PageSize + index];

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var wallpaper = BoxGridRenderer.WallpaperAt(_gapsMode ? 4 : 0);
        PksmPaint.Wallpaper(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), wallpaper);
        if (!_loaded) return;

        var shadow = Pksm.WallpaperShade(wallpaper);
        using var font = new SKFont { Size = 14, Edging = SKFontEdging.Antialias };
        for (var index = 0; index < PageSize; index++)
        {
            var rect = BoxGridRenderer.SlotRect(args.Info, index);
            var absolute = _page * PageSize + index;
            var exists = absolute < Count;
            PksmPaint.Slot(canvas, rect, wallpaper, empty: !exists);
            if (!exists) continue;
            var id = IdAt(_page, index);

            var sprite = _sprites.GetSprite(id, 0, false);
            var state = StateOf(id);
            var dim = _gapsMode ? !_fillSelection.Contains(id) : !state.Seen;
            if (sprite is not null)
            {
                if (dim) canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha(0x55) });
                var inset = Math.Min(rect.Width, rect.Height) * 0.04f;
                var box = SKRect.Inflate(rect, -inset, -inset);
                var scale = Math.Min(box.Width / sprite.Width, box.Height / sprite.Height);
                var w = sprite.Width * scale;
                var h = sprite.Height * scale;
                var dest = new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2);
                using var image = SKImage.FromBitmap(sprite);
                canvas.DrawImage(image, dest, BoxGridRenderer.SpriteSampling);
                if (dim) canvas.Restore();
            }
            else
            {
                _sprites.Warm(id, 0, false, _canvas.InvalidateSurface);
                PksmPaint.CenterText(canvas, _data.SpeciesNames[id], rect.MidX, rect.MidY, font, SKColors.White, shadow, SKTextAlign.Center);
            }

            if (_gapsMode)
            {
                if (_fillSelection.Contains(id)) DrawCheck(canvas, rect);
            }
            else if (state.Caught)
            {
                DrawPokeBall(canvas, rect.Right - rect.Width * 0.16f, rect.Bottom - rect.Height * 0.16f, rect.Width * 0.13f);
            }
            else if (state.Seen)
            {
                using var gold = new SKPaint { Color = UiTokens.SkShinyGold, IsAntialias = true };
                canvas.DrawCircle(rect.Right - rect.Width * 0.14f, rect.Bottom - rect.Height * 0.16f, rect.Width * 0.07f, gold);
            }

            if (index == _cursor)
            {
                using var gold = new SKPaint { Color = UiTokens.SkShinyGold, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, IsAntialias = true };
                canvas.DrawRoundRect(SKRect.Inflate(rect, 1.5f, 1.5f), 5, 5, gold);
                PksmPaint.CenterText(canvas, $"#{id:000} {_data.SpeciesNames[id]}", rect.MidX, rect.Bottom - 8, font,
                    SKColors.White, shadow, SKTextAlign.Center);
            }
        }
    }

    private static void DrawCheck(SKCanvas canvas, SKRect rect)
    {
        using var badge = new SKPaint { Color = Pksm.SelectBorder, IsAntialias = true };
        using var check = new SKPaint { Color = Pksm.IndigoInk, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        var size = Math.Min(rect.Width, rect.Height);
        var cx = rect.Left + size * 0.15f;
        var cy = rect.Top + size * 0.15f;
        var r = size * 0.13f;
        canvas.DrawCircle(cx, cy, r, badge);
        canvas.DrawLine(cx - r * 0.45f, cy, cx - r * 0.1f, cy + r * 0.4f, check);
        canvas.DrawLine(cx - r * 0.1f, cy + r * 0.4f, cx + r * 0.5f, cy - r * 0.35f, check);
    }

    /// <summary>A tiny caught badge: the Poké Ball itself, red top / white base.</summary>
    private static void DrawPokeBall(SKCanvas canvas, float cx, float cy, float radius)
    {
        using var red = new SKPaint { Color = new SKColor(0xE8, 0x48, 0x3C), IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var line = new SKPaint { Color = SKColors.White, StrokeWidth = radius * 0.28f, IsAntialias = true };
        using var rim = new SKPaint { Color = new SKColor(0x2B, 0x2B, 0x2B), Style = SKPaintStyle.Stroke, StrokeWidth = radius * 0.16f, IsAntialias = true };
        canvas.DrawCircle(cx, cy - radius * 0.02f, radius, red);
        canvas.DrawRect(new SKRect(cx - radius, cy, cx + radius, cy + radius), white);
        canvas.DrawLine(cx - radius, cy, cx + radius, cy, line);
        canvas.DrawCircle(cx, cy, radius, rim);
        canvas.DrawCircle(cx, cy, radius * 0.3f, line);
        canvas.DrawCircle(cx, cy, radius * 0.3f, new SKPaint { Color = SKColors.White, IsAntialias = true });
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;
        var slot = BoxGridRenderer.SlotFromTouch(_canvas.CanvasSize, args.Location);
        if (slot < 0 || _page * PageSize + slot >= Count) return;
        _cursor = slot;
        Activate(IdAt(_page, slot));
    }

    private async Task CloseAsync()
    {
        if (_staged.Count > 0)
        {
            var choice = await PadMenu.ShowAsync(_host, "SAVE DEX CHANGES?",
                $"{_staged.Count} species changed.", "Save changes", "Discard changes", "Keep editing");
            if (choice == "Save changes") { await ApplyAndCloseAsync(); return; }
            if (choice == "Keep editing" || choice is null) return;
        }
        TearDown();
        _result.TrySetResult(false);
    }

    private async Task ApplyAndCloseAsync()
    {
        var staged = _staged.ToDictionary(x => x.Key, x => x.Value);
        if (staged.Count > 0)
        {
            var saved = await _viewModel.RunMutationAsync(s =>
            {
                foreach (var (species, state) in staged)
                    s.SetDexEntry(species, state.Seen, state.Caught);
                return new GenerationOutcome(true, $"Dex updated for {staged.Count} species.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
            if (!saved)
            {
                // The write aborted (validation, storage, format refusal): keep the
                // editor open with the staged changes instead of closing as if it worked.
                _viewModel.Status = "DEX WRITE FAILED - CHANGES KEPT, CHECK STATUS";
                return;
            }
        }
        _staged.Clear();
        TearDown();
        _result.TrySetResult(true);
    }

    private void TearDown()
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
    }

    private void SwitchPage(int delta)
    {
        _page = ((_page + delta) % PageCount + PageCount) % PageCount;
        ClampCursor();
        RefreshChrome();
    }

    private void ClampCursor()
    {
        var max = Math.Min(PageSize - 1, Count - 1 - _page * PageSize);
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, max));
    }

    private void SelectAllGaps()
    {
        if (!_gapsMode) return;
        if (_fillSelection.Count == _missing.Count) _fillSelection.Clear();
        else
        {
            _fillSelection.Clear();
            _fillSelection.UnionWith(_missing);
        }
        RefreshChrome();
    }

    private async Task ShowActionsAsync()
    {
        if (_gapsMode)
        {
            var gapChoice = await PadMenu.ShowAsync(_host, "LIVING DEX GAPS",
                $"{_missing.Count} species missing from storage. {_fillSelection.Count} selected.",
                new PadOption($"Generate selected ({_fillSelection.Count})", IconPath: "pokedex"),
                new PadOption("Switch to dex editor", IconPath: "pokedex"),
                new PadOption("Close"));
            if (gapChoice == "Switch to dex editor")
            {
                _gapsMode = false;
                RefreshView();
                return;
            }
            if (gapChoice != $"Generate selected ({_fillSelection.Count})" || _fillSelection.Count == 0) return;
            var species = _fillSelection.OrderBy(x => x).ToList();
            var overlay = LoadingOverlay.Show(_host, "GENERATING…", "The legalizer is building each mon offline.");
            try
            {
                await _viewModel.RunMutationAsync(s => _legalizer.FillSpecies(s, species,
                    (done, total) => overlay.Report(done, total)), Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
                _viewModel.RefreshAllSlots();
                _missing.RemoveAll(_fillSelection.Contains);
                _fillSelection.Clear();
                RefreshView();
            }
            finally { overlay.Close(); }
            return;
        }

        var choice = await PadMenu.ShowAsync(_host, "DEX ACTIONS", null,
            new PadOption("Mark everything seen", IconPath: "pokedex"),
            new PadOption("Complete the Pokédex", IconPath: "pokedex"),
            new PadOption("Switch to living dex gaps", IconPath: "storage"),
            new PadOption("Discard staged changes", IconPath: "hex"));
        switch (choice)
        {
            case "Mark everything seen":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, _staged.TryGetValue(id, out var current) && current.Caught);
                break;
            case "Complete the Pokédex":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, true);
                break;
            case "Switch to living dex gaps":
                _gapsMode = true;
                RefreshView();
                return;
            case "Discard staged changes":
                _staged.Clear();
                break;
        }
        RefreshChrome();
    }

    private void Activate(int species)
    {
        if (_gapsMode)
        {
            if (!_fillSelection.Remove(species)) _fillSelection.Add(species);
        }
        else if (_states.TryGetValue(species, out var state))
        {
            // Cycle from the CURRENT state (staged wins over the saved state), or every
            // press would restart from the save's original "unseen" and never advance.
            var current = StateOf(species);
            _staged[species] = current switch
            {
                { Seen: false } => (true, false),
                { Caught: false } => (true, true),
                _ => (false, false),
            };
        }
        RefreshChrome();
    }

    private (bool Seen, bool Caught) StateOf(int species) =>
        _staged.TryGetValue(species, out var staged) ? staged
        : _states.TryGetValue(species, out var state) ? state
        : (false, false);

    private void RefreshChrome()
    {
        _title.Text = _gapsMode ? $"LIVING DEX GAPS · {_missing.Count} MISSING" : "POKéDEX";
        var seen = 0;
        var caught = 0;
        foreach (var id in _orderedIds)
        {
            var state = StateOf(id);
            if (state.Seen) seen++;
            if (state.Caught) caught++;
        }
        _progress.Text = _gapsMode
            ? $"{_fillSelection.Count} SELECTED · PAGE {_page + 1}/{PageCount}"
            : $"SEEN {seen}/{_states.Count} · CAUGHT {caught}/{_states.Count} · {_staged.Count} STAGED · PAGE {_page + 1}/{PageCount}";

        var absolute = _page * PageSize + _cursor;
        if (absolute < Count)
        {
            var id = IdAt(_page, _cursor);
            var state = StateOf(id);
            _cursorInfo.Text = _gapsMode
                ? $"#{id:000} {_data.SpeciesNames[id]} — {(_fillSelection.Contains(id) ? "SELECTED FOR GENERATION" : "NOT SELECTED")}"
                : $"#{id:000} {_data.SpeciesNames[id]} — {(state.Caught ? "CAUGHT" : state.Seen ? "SEEN" : "UNSEEN")}";
        }
        else
        {
            _cursorInfo.Text = "";
        }
        _canvas.InvalidateSurface();
    }

    /// <summary>Applies the search to the active list; resets paging and repaints.</summary>
    private void RefreshView()
    {
        _viewIds.Clear();
        var source = _gapsMode ? _missing : _orderedIds;
        var query = _query.Trim();
        foreach (var id in source)
        {
            if (query.Length == 0
                || _data.SpeciesNames[id].Contains(query, StringComparison.OrdinalIgnoreCase)
                || id.ToString() == query)
                _viewIds.Add(id);
        }
        _page = 0;
        _cursor = 0;
        RefreshChrome();
    }

    public bool OnPadButton(PadButton button)
    {
        if (!_loaded) return true;
        switch (button)
        {
            case PadButton.Up: _cursor -= Columns; ClampCursor(); RefreshChrome(); return true;
            case PadButton.Down: _cursor += Columns; ClampCursor(); RefreshChrome(); return true;
            case PadButton.Left:
                if (_cursor % Columns == 0) { SwitchPage(-1); return true; }
                _cursor--; RefreshChrome(); return true;
            case PadButton.Right:
                if (_cursor % Columns == Columns - 1) { SwitchPage(1); return true; }
                _cursor++; ClampCursor(); RefreshChrome(); return true;
            case PadButton.L: SwitchPage(-1); return true;
            case PadButton.R: SwitchPage(1); return true;
            case PadButton.A:
            {
                var absolute = _page * PageSize + _cursor;
                if (absolute < Count) Activate(IdAt(_page, _cursor));
                return true;
            }
            case PadButton.B: _ = CloseAsync(); return true;
            case PadButton.X:
            case PadButton.Start: _ = ShowActionsAsync(); return true;
            case PadButton.Y: SelectAllGaps(); return true;
            default: return true;
        }
    }
}
