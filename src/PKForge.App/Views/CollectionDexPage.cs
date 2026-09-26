using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The living-dex tracker, HOME-style: every species of the national dex as a sprite
/// cell, colored by what your collection actually contains (bank plus open save), not
/// by dex flags. Normal living dex and shiny living dex each get their own view;
/// per-generation scopes show how far each era is, and the "this game" scope matches
/// the open save. A missing species can jump straight into "How to get" to plan the
/// catch. Read-only: nothing here writes a save or the bank.
/// </summary>
public sealed class CollectionDexPage : IPadPagingHandler
{
    private const int Columns = BoxGridRenderer.Columns;
    private const int Rows = BoxGridRenderer.Rows;
    private const int PageSize = Columns * Rows;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession? _session;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly HashSet<int> _owned = [];
    private readonly HashSet<int> _shiny = [];
    private readonly List<int> _allIds = [];
    private readonly List<int> _viewIds = [];
    private readonly SKCanvasView _canvas;
    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _cursorInfo;
    private readonly HorizontalStackLayout _chips;
    private readonly List<Border> _chipBorders = [];
    private CollectionDexProgress? _progressData;
    private int? _genScope;          // null = all generations; 0 = this game
    private bool _shinyDex;
    private bool _missingOnly;
    private bool _loaded;
    private int _page;
    private int _cursor;

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, IGameDataService data, ISpriteService sprites)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        var session = services.GetService<ISaveSessionService>()?.CurrentSession;
        try
        {
            await new CollectionDexPage(host, viewModel, session, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"Collection dex closed: {error.Message}";
        }
    }

    private CollectionDexPage(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession? session,
        IGameDataService data, ISpriteService sprites)
    {
        _host = host;
        _viewModel = viewModel;
        _session = session;
        _data = data;
        _sprites = sprites;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _title = new Label { Text = "Living dex", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        _progress = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody, HorizontalTextAlignment = TextAlignment.End, HorizontalOptions = LayoutOptions.End };
        _cursorInfo = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody };

        _canvas = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        _chips = new HorizontalStackLayout { Spacing = 5 };

        View hints = Kit.WindowHints(
            ("A", "Actions", () => _ = ShowActionsAsync()),
            ("B", "Done", () => Close()),
            ("LR", "Page", null),
            ("X", "Scope", () => _ = ShowScopeMenuAsync()),
            ("Y", "Missing only", ToggleMissingOnly),
            ("+", "Autopilot", () => _ = OpenAutopilotAsync()));

        var content = new Grid
        {
            RowSpacing = 6,
            // title / chips / GRID (the only elastic row) / cursor line / hints.
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                    Children = { _title, _progress },
                },
                _chips,
                _canvas,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetRow(_chips, 1);
        Grid.SetRow(_canvas, 2);
        Grid.SetRow(_cursorInfo, 3);
        Grid.SetRow(hints, 4);

        var window = Kit.DevicePanel(content, padding: 10);
        window.Margin = new Thickness(24, 12);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => Close();
        scrim.GestureRecognizers.Add(scrimTap);
        _overlay = new Grid { Children = { scrim, window } };
        _host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, _host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, _host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        var loader = LoadingOverlay.Show(_host, "Counting your collection…",
            "Reading the bank and every game on your shelf.");
        _ = Task.Run(async () =>
        {
            var collection = await CollectAsync(_session);

            // National scope: the bank spans every generation, so the tracker always
            // counts all nine; "this game" is only a view filter (RefreshView).
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Apply(collection);
                _allIds.AddRange(Enumerable.Range(1, Math.Min(CollectionDex.MaxSpecies, _data.SpeciesNames.Count - 1))
                    .Where(id => _data.SpeciesNames[id].Length > 0));
                // Loaded first: RefreshView warms the visible page and fills the cursor line.
                _loaded = true;
                RefreshView();
                loader.Close();
                _router?.Push(this);
            });
        });
    }

    /// <summary>Gathers every mon the collection actually holds: bank entries, the
    /// open save (live, including unsaved generated/imported mons), and every other
    /// save on the shelf. Dex flags are ignored by design — a caught-then-released
    /// species is not something you can rebuild a living dex from.</summary>
    private static async Task<List<(int Species, bool Shiny)>> CollectAsync(ISaveEngineSession? open)
    {
        var collection = new List<(int Species, bool Shiny)>();
        var services = IPlatformApplication.Current?.Services;
        var bank = services?.GetService<IBankService>();
        if (bank is not null)
            foreach (var entry in bank.GetAll())
                if (entry.Info.Species > 0) collection.Add((entry.Info.Species, entry.Info.Shiny));
        if (open is not null)
            foreach (var slot in open.Snapshot.Slots)
                if (slot.Species is > 0 && !slot.IsEgg) collection.Add((slot.Species.Value, slot.IsShiny));

        // The other games on the shelf: mons still living in their cartridges count
        // toward the national tracker even though they were never deposited.
        var picker = services?.GetService<ViewModels.SavePickerViewModel>();
        var access = services?.GetService<ISaveFileAccess>();
        var engine = services?.GetService<ISaveEngine>();
        var openId = services?.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
        if (picker is not null && access is not null && engine is not null)
            foreach (var save in picker.Saves)
            {
                if (save.DocumentId == openId) continue;
                try
                {
                    var bytes = await access.ReadAsync(save.DocumentId);
                    using var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
                    foreach (var slot in session.Snapshot.Slots)
                        if (slot.Species is > 0 && !slot.IsEgg) collection.Add((slot.Species.Value, slot.IsShiny));
                }
                catch (Exception)
                {
                    // A save that no longer parses (revoked grant, mid-write file)
                    // must not blank the whole tracker; skip it.
                }
            }
        return collection;
    }

    private void Apply(List<(int Species, bool Shiny)> collection)
    {
        _progressData = CollectionDex.Compute(collection, CollectionDex.MaxSpecies, _data.SpeciesNames);
        _owned.Clear();
        _shiny.Clear();
        foreach (var (species, shiny) in collection)
        {
            if (species < 1 || species >= _data.SpeciesNames.Count || _data.SpeciesNames[species].Length == 0) continue;
            _owned.Add(species);
            if (shiny) _shiny.Add(species);
        }
     }

    private void RefreshChrome()
    {
        if (_progressData is null) return;
        var scope = _shinyDex ? "Shiny living dex" : "Living dex";
        var scoped = ScopedProgress();
        _title.Text = scope;
        _progress.Text = $"{scoped.Owned}/{scoped.Total} · shiny {_progressData.Shiny}/{_progressData.TotalSpecies}";

        _chips.Children.Clear();
        _chipBorders.Clear();
        AddChip("ALL", null);
        if (_session is not null) AddChip("This game", 0);
        foreach (var segment in _progressData.Segments)
            AddChip($"{Roman(segment.Generation)} {(_shinyDex ? segment.Shiny : segment.Owned)}/{segment.Total}", segment.Generation);
    }

    private (int Owned, int Total) ScopedProgress()
    {
        if (_progressData is null) return (0, 0);
        if (_genScope is null)
            return (_shinyDex ? _progressData.Shiny : _progressData.Owned, _progressData.TotalSpecies);
        if (_genScope == 0)
        {
            var cap = _session?.MaxSpeciesId ?? CollectionDex.MaxSpecies;
            var ids = _allIds.Where(id => id <= cap).ToList();
            return (ids.Count(IsOwned), ids.Count);
        }
        var segment = _progressData.Segments.FirstOrDefault(s => s.Generation == _genScope);
        return segment is null ? (0, 0) : (_shinyDex ? segment.Shiny : segment.Owned, segment.Total);
    }

    private static string Roman(int gen) => gen switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => $"{gen}",
    };

    private void AddChip(string label, int? scope)
    {
        var selected = _genScope == scope;
        // A segment in the menu-button language: active = cobalt with the pale rim.
        var chip = Kit.Tab(label, selected);
        chip.Padding = new Thickness(10, 3);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => SetScope(scope);
        chip.GestureRecognizers.Add(tap);
        _chips.Children.Add(chip);
        _chipBorders.Add(chip);
    }

    private void SetScope(int? scope)
    {
        _genScope = scope;
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    private void ToggleMissingOnly()
    {
        _missingOnly = !_missingOnly;
        _page = 0;
        RefreshView();
    }


    private int Count => _viewIds.Count;
    private int PageCount => Math.Max(1, (Count + PageSize - 1) / PageSize);
    private int IdAt(int page, int index) => _viewIds[page * PageSize + index];

    private bool IsOwned(int id) => _shinyDex ? _shiny.Contains(id) : _owned.Contains(id);

    private void RefreshView()
    {
        IEnumerable<int> view = _allIds;
        if (_genScope is > 0)
        {
            var range = CollectionDex.GenRanges.Single(r => r.Generation == _genScope);
            view = view.Where(id => id >= range.First && id <= range.Last);
        }
        else if (_genScope == 0)
        {
            var cap = _session?.MaxSpeciesId ?? CollectionDex.MaxSpecies;
            view = view.Where(id => id <= cap);
        }
        if (_missingOnly) view = view.Where(id => !IsOwned(id));
        _viewIds.Clear();
        _viewIds.AddRange(view);
        _page = Math.Clamp(_page, 0, PageCount - 1);
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, Count - 1));
        RefreshChrome();
        RefreshCursorInfo();
        WarmVisible();
        _canvas.InvalidateSurface();
    }

    /// <summary>Loads the whole visible page at once, so cells fill in one pass instead
    /// of each waiting for its own paint handler to discover it is missing.</summary>
    private void WarmVisible()
    {
        if (!_loaded) return;
        for (var index = 0; index < PageSize; index++)
        {
            var absolute = _page * PageSize + index;
            if (absolute >= Count) break;
            var id = IdAt(_page, index);
            _sprites.Warm(id, 0, _shinyDex && IsOwned(id),
                () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
        }
    }
    private void RefreshCursorInfo()
    {
        if (!_loaded || Count == 0)
        {
            _cursorInfo.Text = "";
            return;
        }
        var id = IdAt(_page, _cursor);
        var name = _data.SpeciesNames[id];
        var state = _shiny.Contains(id)
            ? (_shinyDex ? "Shiny owned" : "Owned + shiny")
            : _owned.Contains(id) ? "Owned" : "Missing";
        _cursorInfo.Text = $"#{id:000} {name} · {state}";
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var wallpaper = BoxGridRenderer.WallpaperAt(2);
        PksmPaint.Wallpaper(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), wallpaper);
        if (!_loaded) return;

        var shadow = Pksm.WallpaperShade(wallpaper);
        using var font = new SKFont { Size = 14, Edging = SKFontEdging.Antialias };
        using var gold = new SKPaint { Color = UiTokens.SkShinyGold, IsAntialias = true };
        for (var index = 0; index < PageSize; index++)
        {
            var rect = BoxGridRenderer.SlotRect(args.Info, index);
            var absolute = _page * PageSize + index;
            var exists = absolute < Count;
            PksmPaint.Slot(canvas, rect, wallpaper, empty: !exists);
            if (!exists) continue;
            var id = IdAt(_page, index);
            var owned = IsOwned(id);

            var sprite = _sprites.GetSprite(id, 0, _shinyDex && owned);
            if (sprite is not null)
            {
                var inset = Math.Min(rect.Width, rect.Height) * 0.04f;
                var box = SKRect.Inflate(rect, -inset, -inset);
                var scale = Math.Min(box.Width / sprite.Width, box.Height / sprite.Height);
                var w = sprite.Width * scale;
                var h = sprite.Height * scale;
                var dest = new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2);
                using var image = SKImage.FromBitmap(sprite);
                if (owned)
                {
                    canvas.DrawImage(image, dest, BoxGridRenderer.SpriteSampling);
                }
                else
                {
                    // Missing species are pure black silhouettes: the "who's that
                    // Pokémon?" read, and no alpha layer that could swallow the sprite
                    // on a dark wallpaper.
                    using var silhouette = new SKPaint
                    {
                        ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn),
                        IsAntialias = false,
                    };
                    canvas.DrawImage(image, dest, BoxGridRenderer.SpriteSampling, silhouette);
                }
            }
            else
            {
                _sprites.Warm(id, 0, _shinyDex && owned,
                    () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
                PksmPaint.CenterText(canvas, _data.SpeciesNames[id], rect.MidX, rect.MidY, font, SKColors.White, shadow, SKTextAlign.Center);
            }

            if (owned) DrawPokeBall(canvas, rect.Right - rect.Width * 0.16f, rect.Bottom - rect.Height * 0.16f, rect.Width * 0.13f);
            if (_shiny.Contains(id))
                canvas.DrawCircle(rect.Left + rect.Width * 0.14f, rect.Bottom - rect.Height * 0.16f, rect.Width * 0.07f, gold);

            if (index == _cursor)
            {
                using var focus = new SKPaint { Color = UiTokens.SkShinyGold, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, IsAntialias = true };
                canvas.DrawRoundRect(SKRect.Inflate(rect, 1.5f, 1.5f), 5, 5, focus);
            }
        }
    }

    /// <summary>A tiny owned badge: the Poké Ball itself, red top / white base.</summary>
    private static void DrawPokeBall(SKCanvas canvas, float cx, float cy, float radius)
    {
        using var red = new SKPaint { Color = new SKColor(0xE3, 0x35, 0x0D), IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = MathF.Max(1f, radius * 0.16f), IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius, white);
        canvas.Save();
        canvas.ClipRect(new SKRect(cx - radius, cy - radius, cx + radius, cy));
        canvas.DrawCircle(cx, cy, radius, red);
        canvas.Restore();
        canvas.DrawCircle(cx, cy, radius, ring);
        canvas.DrawCircle(cx, cy, radius * 0.26f, white);
        canvas.DrawCircle(cx, cy, radius * 0.26f, ring);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;
        if (!_loaded) return;
        var slot = BoxGridRenderer.SlotFromTouch(_canvas.CanvasSize, args.Location);
        if (slot < 0 || _page * PageSize + slot >= Count) return;
        _cursor = slot;
        RefreshCursorInfo();
        _canvas.InvalidateSurface();
        _ = ShowActionsAsync();
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: MoveCursor(-1, 0); return true;
            case PadButton.Right: MoveCursor(1, 0); return true;
            case PadButton.Up: MoveCursor(0, -1); return true;
            case PadButton.Down: MoveCursor(0, 1); return true;
            case PadButton.L: Page(-1); return true;
            case PadButton.R: Page(1); return true;
            case PadButton.A: _ = ShowActionsAsync(); return true;
            case PadButton.B: Close(); return true;
            case PadButton.X: _ = ShowScopeMenuAsync(); return true;
            case PadButton.Y: ToggleMissingOnly(); return true;
            case PadButton.Start: _ = OpenAutopilotAsync(); return true;
            default: return true; // the tracker owns the pad while open
        }
    }

    private void MoveCursor(int dx, int dy)
    {
        if (Count == 0) return;
        var col = _cursor % Columns;
        var row = _cursor / Columns;
        col = Math.Clamp(col + dx, 0, Columns - 1);
        row = Math.Clamp(row + dy, 0, Rows - 1);
        _cursor = Math.Clamp(row * Columns + col, 0, Count - 1);
        RefreshCursorInfo();
        _canvas.InvalidateSurface();
    }

    private void Page(int delta)
    {
        if (PageCount <= 1) return;
        _page = (_page + delta + PageCount) % PageCount;
        _cursor = 0;
        RefreshCursorInfo();
        _canvas.InvalidateSurface();
    }

    private async Task ShowScopeMenuAsync()
    {
        var options = new List<PadOption> { new("All generations") };
        if (_session is not null) options.Add(new PadOption("This game"));
        options.AddRange(_progressData!.Segments.Select(s => new PadOption($"Gen {Roman(s.Generation)} · {(_shinyDex ? s.Shiny : s.Owned)}/{s.Total}")));
        options.Add(new PadOption(_shinyDex ? "View: normal living dex" : "View: SHINY living dex"));
        var choice = await PadMenu.ShowAsync(_host, "Scope", null, options.ToArray());
        if (choice is null) return;
        if (choice == "All generations") SetScope(null);
        else if (choice == "This game") SetScope(0);
        else if (choice.StartsWith("View:", StringComparison.Ordinal)) { _shinyDex = !_shinyDex; RefreshView(); }
        else if (choice.StartsWith("Gen ", StringComparison.Ordinal) && int.TryParse(RomanToNumber(choice[4].ToString()), out var gen))
            SetScope(gen);
    }

    private static string? RomanToNumber(string roman) => roman switch
    {
        "I" => "1", "II" => "2", "III" => "3", "IV" => "4", "V" => "5",
        "VI" => "6", "VII" => "7", "VIII" => "8", "IX" => "9", _ => null,
    };

    private async Task ShowActionsAsync()
    {
        if (!_loaded || Count == 0) return;
        var id = IdAt(_page, _cursor);
        var name = _data.SpeciesNames[id];
        var choice = await PadMenu.ShowAsync(_host, $"#{id:000} {name}", null,
            new PadOption("How to get", IconPath: "map"),
            new PadOption("Close", IconPath: "close"));
        if (choice == "How to get")
        {
            // Straight to the answer for THIS Pokémon: no species wizard, and it works
            // with or without an open save (CATCH is offered only for a game the save can be).
            await EncounterGallery.ShowAllGamesForSpeciesAsync(_host, _viewModel, _session, id, () => { });

            // A catch may have added a species; recount before returning. National scope,
            // same as the initial count: a catch must never shrink the header to game scope.
            Apply(await CollectAsync(_session));
            RefreshView();
        }
    }

    /// <summary>From the tracker straight into the plan that fills it; the count follows the moves.</summary>
    private async Task OpenAutopilotAsync()
    {
        if (!_loaded) return;
        _router?.Remove(this);
        try
        {
            await LivingDexAutopilotPage.ShowAsync(_host, _viewModel, _data, _sprites);
        }
        finally
        {
            _router?.Push(this);
        }
        Apply(await CollectAsync(_session));
        RefreshView();
    }

    private void Close()
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(true);
    }
}
