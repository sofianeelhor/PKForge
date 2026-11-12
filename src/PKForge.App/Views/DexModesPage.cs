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
/// The Origin Dex, the Extended Living Dex and its box planner on one painted screen
/// (<see cref="DexModesPaint"/>). Origin: a species counts only through a Pokémon obtained in
/// its own generation; the sheet lists where the native and the other candidates are.
/// Extended: every collectible form, with a switch per category. Boxes: the extended dex laid
/// out in boxes, applied to the Bank as one placement batch after a confirmation, nothing lost.
/// Reads the Bank and every save; only "Apply to the Bank" writes, and only slot placements.
/// </summary>
public sealed class DexModesPage : IPadPagingHandler
{
    private enum Mode { Origin, Extended, Boxes }

    private static readonly string[] Romans = ["ALL", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
    private static readonly (DexFormCategories Category, string Label)[] Toggles =
    [
        (DexFormCategories.Regional, "Regional forms"),
        (DexFormCategories.OtherForms, "Other forms"),
        (DexFormCategories.GenderDifferences, "Gender differences"),
        (DexFormCategories.Gigantamax, "Gigantamax"),
        (DexFormCategories.AlcremieSweets, "Alcremie sweets"),
    ];
    private static readonly (DexBoxBreaks Breaks, string Label, string Count)[] Breaks =
    [
        (DexBoxBreaks.Continuous, "Run on", "straight"),
        (DexBoxBreaks.KeepSpeciesTogether, "Species", "together"),
        (DexBoxBreaks.Generations, "Generations", "own boxes"),
    ];

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession? _session;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly IBankService? _bank;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly ContentView _footer = new();
    private readonly Dictionary<SKBitmap, SKImage> _sheets = [];
    private readonly Dictionary<(int Species, int Form, bool Shiny), SKImage?> _big = [];
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private List<DexMon> _collection = [];
    private IReadOnlyList<DexSpeciesForms> _catalog = [];
    private OriginDexReport? _origin;
    private ExtendedDexReport? _extended;
    private IReadOnlyList<DexPlannedBox> _layout = [];
    private readonly List<int> _view = [];   // Origin: entry index; Extended: cell index
    private Mode _mode = Mode.Origin;
    private DexFormCategories _categories = DexFormCategories.Standard;
    private DexBoxBreaks _breaks = DexBoxBreaks.KeepSpeciesTogether;
    private bool _shiny;
    private bool _missingOnly;
    private int _generation;                 // 0 = all
    private int _box;
    private int _cursor;
    private int _scrollRow;
    private string _status = "";
    private bool _loaded;
    private DexModesHits? _hits;
    private SKPoint? _pressedAt;
    private float _dragRows;
    private bool _dragged;

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, IGameDataService data, ISpriteService sprites)
    {
        var session = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;
        try
        {
            await new DexModesPage(host, viewModel, session, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"Dex modes closed. {Domain.ErrorText.Plain(error)}";
        }
    }

    private DexModesPage(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession? session, IGameDataService data, ISpriteService sprites)
    {
        _host = host;
        _viewModel = viewModel;
        _session = session;
        _data = data;
        _sprites = sprites;
        var services = IPlatformApplication.Current?.Services;
        _router = services?.GetService<GamepadRouter>();
        _bank = services?.GetService<IBankService>();

        _canvas.PaintSurface += PaintGuard.Wrap("DexModesPage.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _footer.Content = DsChrome.Footer(
            ("A", "Select", () => OnPadButton(PadButton.A)),
            ("LR", "Tab", () => OnPadButton(PadButton.R)),
            ("Y", "Missing only", () => OnPadButton(PadButton.Y)),
            ("X", "Mode", () => OnPadButton(PadButton.X)),
            ("B", "Done", () => OnPadButton(PadButton.B)));
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, _footer },
        };
        Grid.SetRow(_footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });

        var loader = LoadingOverlay.Show(_host, "READING YOUR COLLECTION…", "Every Bank slot and every save on the shelf, with where each Pokémon comes from.");
        _ = Task.Run(async () =>
        {
            try
            {
                var collection = await CollectAsync();
                var catalog = DexModeSources.Catalog();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    loader.Close();
                    if (loader.Cancellation.IsCancellationRequested)
                    {
                        // B or Cancel on the loading screen: back to where the player was.
                        TearDown();
                        _result.TrySetResult(false);
                        return;
                    }
                    _collection = collection;
                    _catalog = catalog;
                    Recompute();
                    _loaded = true;
                    _router?.Push(this);
                });
            }
            catch (Exception error)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    loader.Close();
                    _viewModel.Status = $"Dex modes failed. {Domain.ErrorText.Plain(error)}";
                    TearDown();
                    _result.TrySetResult(false);
                });
            }
        });
    }

    /// <summary>The Bank, the open save (live) and every other save on the shelf.</summary>
    private async Task<List<DexMon>> CollectAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        var list = new List<DexMon>();
        if (_bank is not null) list.AddRange(DexModeSources.FromBank(_bank));
        var current = services?.GetService<ISaveSessionService>()?.Current?.Document;
        if (_session is not null)
            list.AddRange(DexModeSources.FromSession(_session, current?.DocumentId ?? "open", current?.DisplayName ?? "Open save"));

        var picker = services?.GetService<SavePickerViewModel>();
        var access = services?.GetService<ISaveFileAccess>();
        var engine = services?.GetService<ISaveEngine>();
        if (picker is null || access is null || engine is null) return list;
        foreach (var save in picker.Saves)
        {
            if (save.DocumentId == current?.DocumentId) continue;
            try
            {
                var bytes = await access.ReadAsync(save.DocumentId);
                using var other = engine.OpenSession(bytes, save.EngineHint, save.Format);
                list.AddRange(DexModeSources.FromSession(other, save.DocumentId, save.GameLabel));
            }
            catch (Exception)
            {
                // A save that no longer parses must not blank the whole dex; skip it.
            }
        }
        return list;
    }

    // ── State ────────────────────────────────────────────────────────────────

    private void Recompute()
    {
        var species = Enumerable.Range(1, Math.Min(CollectionDex.MaxSpecies, _data.SpeciesNames.Count - 1))
            .Where(id => _data.SpeciesNames[id].Length > 0);
        _origin = OriginDex.Compute(_shiny ? _collection.Where(m => m.Shiny) : _collection, species);
        _extended = ExtendedDex.Compute(_catalog, _collection, _categories, _shiny);
        _layout = DexBoxPlanner.Layout(_extended.Cells, _breaks);
        _plan = null;
        _box = Math.Clamp(_box, 0, Math.Max(0, _layout.Count - 1));
        RefreshView();
    }

    private bool InGeneration(int species) => _generation == 0 || OriginDex.IntroGeneration(species) == _generation;

    private void RefreshView()
    {
        _view.Clear();
        if (_mode == Mode.Origin && _origin is not null)
        {
            for (var i = 0; i < _origin.Entries.Count; i++)
            {
                var e = _origin.Entries[i];
                if (InGeneration(e.Species) && (!_missingOnly || !e.Owned)) _view.Add(i);
            }
        }
        else if (_mode == Mode.Extended && _extended is not null)
        {
            for (var i = 0; i < _extended.Cells.Count; i++)
            {
                var cell = _extended.Cells[i];
                if (InGeneration(cell.Slot.Species) && (!_missingOnly || !cell.Owned)) _view.Add(i);
            }
        }
        SetCursor(_cursor);
    }

    private int Count => _mode == Mode.Boxes ? DexModesPaint.BoxColumns * DexModesPaint.BoxRows : _view.Count;

    private int Columns => _mode == Mode.Boxes ? DexModesPaint.BoxColumns : DexModesPaint.Columns;

    private int VisibleRows => _mode == Mode.Boxes ? DexModesPaint.BoxRows
        : DexModesPaint.VisibleRows(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));

    private void SetCursor(int index)
    {
        _cursor = Count == 0 ? 0 : Math.Clamp(index, 0, Count - 1);
        var row = _cursor / Columns;
        if (row < _scrollRow) _scrollRow = row;
        else if (row >= _scrollRow + VisibleRows) _scrollRow = row - VisibleRows + 1;
        var rows = (Count + Columns - 1) / Columns;
        _scrollRow = _mode == Mode.Boxes ? 0 : Math.Clamp(_scrollRow, 0, Math.Max(0, rows - VisibleRows));
        Invalidate();
    }

    private void Move(int delta)
    {
        if (Count == 0) return;
        var target = _cursor + delta;
        if (target < 0 || target >= Count) return;
        SetCursor(target);
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        _cursor = 0;
        _scrollRow = 0;
        _status = "";
        RefreshView();
    }

    /// <summary>L/R: a generation (dex modes) or a box (planner).</summary>
    private void Turn(int delta)
    {
        if (_mode == Mode.Boxes)
        {
            if (_layout.Count == 0) return;
            _box = (_box + delta + _layout.Count) % _layout.Count;
            SetCursor(_cursor);
            return;
        }
        _generation = (_generation + delta + Romans.Length) % Romans.Length;
        _cursor = 0;
        _scrollRow = 0;
        RefreshView();
    }

    private void SelectTab(int tab)
    {
        if (_mode == Mode.Boxes)
        {
            _breaks = Breaks[Math.Clamp(tab, 0, Breaks.Length - 1)].Breaks;
            _box = 0;
            Recompute();
            return;
        }
        _generation = tab;
        _cursor = 0;
        _scrollRow = 0;
        RefreshView();
    }

    private void Toggle(DexFormCategories category)
    {
        _categories ^= category;
        _cursor = 0;
        Recompute();
    }

    // ── What sits under the cursor ───────────────────────────────────────────

    private OriginDexEntry? CurrentOrigin => _mode == Mode.Origin && _cursor < _view.Count ? _origin!.Entries[_view[_cursor]] : null;

    private ExtendedDexCell? CurrentCell
    {
        get
        {
            if (_extended is null) return null;
            if (_mode == Mode.Extended) return _cursor < _view.Count ? _extended.Cells[_view[_cursor]] : null;
            if (_mode == Mode.Boxes && _box < _layout.Count && _cursor < _layout[_box].Cells.Count
                && _layout[_box].Cells[_cursor] is { } at) return _extended.Cells[at];
            return null;
        }
    }

    private string Name(int species, int form, string label)
    {
        var name = species < _data.SpeciesNames.Count ? _data.SpeciesNames[species] : $"#{species}";
        if (label.Length > 0) return $"{name} ({label})";
        var formName = form > 0 ? LivingDexCatalogBuilder.FormName(species, form) : "";
        return formName.Length > 0 ? $"{name} ({formName})" : name;
    }

    private static string Origin(DexMon mon) => mon.OriginGame.Length > 0 ? $"{mon.Where.Describe()} · {mon.OriginGame}" : mon.Where.Describe();

    private List<string> Actions() => _mode switch
    {
        Mode.Origin => CurrentOrigin is { } e
            ? [$"How to get it in a Gen {Romans[e.Generation]} game", _shiny ? "Shiny: on" : "Shiny: off"]
            : [_shiny ? "Shiny: on" : "Shiny: off"],
        Mode.Extended => ["Plan the boxes", _shiny ? "Shiny: on" : "Shiny: off"],
        _ => ["Apply to the Bank…", "Back to the list"],
    };

    private List<DexModesRow> Rows()
    {
        var rows = new List<DexModesRow>();
        if (_mode == Mode.Origin)
        {
            if (CurrentOrigin is not { } e) return rows;
            rows.Add(new("Counts", e.Owned ? $"Yes · {Origin(e.Native[0])}" : e.OnlyElsewhere ? "No: only from other eras" : "No: none owned"));
            foreach (var mon in e.Native.Skip(1).Take(2)) rows.Add(new("Native", Origin(mon)));
            foreach (var mon in e.Elsewhere.Take(e.Owned ? 1 : 3)) rows.Add(new("Elsewhere", Origin(mon)));
            var more = e.Native.Count + e.Elsewhere.Count - rows.Count;
            if (more > 0) rows.Add(new("And", $"{more} more"));
            return rows;
        }
        if (_mode == Mode.Extended && _extended is not null)
        {
            foreach (var (category, label) in Toggles)
            {
                var (owned, total) = _extended.Tally(category);
                rows.Add(new(label, _categories.HasFlag(category) ? $"{owned}/{total}" : "off", _categories.HasFlag(category)));
            }
            if (CurrentCell is { } cell) rows.Add(new("Filled by", cell.Filled is { } m ? Origin(m) : "Missing"));
            return rows;
        }
        if (_bank is null || _extended is null) return rows;
        // Planned once per layout, not on every repaint: it copies and sorts the whole Bank.
        var plan = _plan ??= BankPlan();
        rows.Add(new("Plan", $"{_layout.Count} boxes from Bank box {plan.StartBox + 1:00}"));
        rows.Add(new("Will land", $"{plan.Placed} Pokémon"));
        rows.Add(new("Move out of the way", plan.Displaced == 0 ? "none" : $"{plan.Displaced} to box {plan.StartBox + plan.Boxes + 1:00} on"));
        rows.Add(new("Locked boxes", "never touched"));
        if (CurrentCell is { } here) rows.Add(new("This slot", here.Filled is { } m ? m.Where.Describe() : "stays empty"));
        return rows;
    }

    private DexBankArrangement? _plan;

    private DexBankArrangement BankPlan()
    {
        var all = _bank!.GetAll();
        var locked = _bank.LockedBoxes;
        var start = DexBoxPlanner.FirstFreeStretch(all, _bank.BoxCount, locked, _layout.Count);
        return DexBoxPlanner.PlanBank(_extended!.Cells, _layout, all, start, locked);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private async Task RunActionAsync(string action)
    {
        if (action.StartsWith("Shiny:", StringComparison.Ordinal)) { _shiny = !_shiny; Recompute(); return; }
        if (action == "Plan the boxes") { SetMode(Mode.Boxes); return; }
        if (action == "Back to the list") { SetMode(Mode.Extended); return; }
        if (action.StartsWith("How to get", StringComparison.Ordinal) && CurrentOrigin is { } e)
        {
            _router?.Remove(this);
            try { await EncounterGallery.ShowAllGamesForSpeciesAsync(_host, _viewModel, _session, e.Species, Invalidate); }
            finally { _router?.Push(this); }
            return;
        }
        if (action == "Apply to the Bank…") await ApplyAsync();
    }

    /// <summary>One placement batch, confirmed first. Every Pokémon keeps exactly one slot.</summary>
    private async Task ApplyAsync()
    {
        if (_bank is null || _extended is null || _layout.Count == 0) return;
        var plan = BankPlan();
        if (plan.Refusal is { } refusal) { _status = refusal; Invalidate(); return; }
        if (plan.Placements.Count == 0) { _status = "No Bank Pokémon fills this plan: nothing to move."; Invalidate(); return; }
        _router?.Remove(this);
        string? choice;
        try
        {
            choice = await PadMenu.ShowAsync(_host, "Arrange the Bank?",
                $"{plan.Placed} Pokémon go into boxes {plan.StartBox + 1:00}-{plan.StartBox + plan.Boxes:00}"
                + (plan.Displaced > 0 ? $"; {plan.Displaced} already there move after them." : ".")
                + " Nothing is created or released, locked boxes are left alone.",
                "Arrange", "Cancel");
        }
        finally { _router?.Push(this); }
        if (choice != "Arrange") return;
        try
        {
            while (_bank.BoxCount <= plan.Placements.Max(p => p.Box)) _bank.AddBox();
            var moved = _bank.Place(plan.Placements);
            _status = $"{moved} Pokémon moved · the plan starts at Bank box {plan.StartBox + 1:00}.";
            _viewModel.Status = _status;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        {
            _status = $"Not arranged. {Domain.ErrorText.Plain(error)}";
        }
        // Every save on the shelf is read again: off the UI thread, behind the loading screen.
        var loader = LoadingOverlay.Show(_host, "READING YOUR COLLECTION…", "Every Bank slot and every save on the shelf, with where each Pokémon comes from.");
        try { _collection = await Task.Run(CollectAsync); }
        catch (Exception error)
        {
            AppLog.Error("dex-modes", "Reading the collection again failed", error);
            _status = $"{_status} The counts were not refreshed. {Domain.ErrorText.Plain(error)}";
        }
        finally { loader.Close(); }
        Recompute();
    }

    private async Task ShowModeMenuAsync()
    {
        _router?.Remove(this);
        string? choice;
        try
        {
            choice = await PadMenu.ShowAsync(_host, "Dex mode", null,
                new PadOption("Origin dex", Detail: "A species counts only from a game of its own generation."),
                new PadOption("Extended living dex", Detail: "Every form, with a switch for each kind."),
                new PadOption("Box planner", Detail: "The extended dex laid out in boxes, appliable to the Bank."),
                new PadOption(_shiny ? "Shiny: on" : "Shiny: off", Detail: "Only shiny Pokémon count."));
        }
        finally { _router?.Push(this); }
        switch (choice)
        {
            case "Origin dex": SetMode(Mode.Origin); break;
            case "Extended living dex": SetMode(Mode.Extended); break;
            case "Box planner": SetMode(Mode.Boxes); break;
            case { } shiny when shiny.StartsWith("Shiny", StringComparison.Ordinal): _shiny = !_shiny; Recompute(); break;
        }
    }

    private void Activate()
    {
        if (_mode == Mode.Origin && CurrentOrigin is not null) _ = RunActionAsync(Actions()[0]);
    }

    private void TearDown()
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
        foreach (var image in _sheets.Values) image.Dispose();
        foreach (var image in _big.Values) image?.Dispose();
        _sheets.Clear();
        _big.Clear();
    }

    private void Close()
    {
        TearDown();
        _result.TrySetResult(true);
    }

    // ── Paint ────────────────────────────────────────────────────────────────

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        if (!_loaded) return;
        _hits = DexModesPaint.Paint(c, args.Info, BuildScene(), _face);
    }

    private DexModesScene BuildScene()
    {
        var cells = new List<DexModesCell>();
        var first = _scrollRow * Columns;
        var last = first + (VisibleRows + 1) * Columns;
        string title, progress;
        IReadOnlyList<(string, string)> tabs;
        var tab = _generation;
        if (_mode == Mode.Origin)
        {
            for (var i = 0; i < _view.Count; i++)
            {
                var e = _origin!.Entries[_view[i]];
                var (sheet, source) = i >= first && i < last ? Icon(e.Species, 0) : (null, SKRect.Empty);
                cells.Add(new DexModesCell(e.Species, sheet, source, e.Owned, e.OnlyElsewhere, e.ShinyOwned));
            }
            var segment = _origin!.Segments.FirstOrDefault(s => s.Generation == _generation);
            title = _generation == 0 ? $"Origin dex · {_origin.Native}/{_origin.Total}" : $"Origin dex · Gen {Romans[_generation]} · {segment?.Native}/{segment?.Total}";
            tabs = [.. Romans.Select((r, g) => (r, g == 0 ? $"{_origin.Native}/{_origin.Total}"
                : _origin.Segments.FirstOrDefault(s => s.Generation == g) is { } s ? $"{s.Native}/{s.Total}" : ""))];
            progress = $"Native {_origin.Native}/{_origin.Total} · shiny {_origin.NativeShiny} · {_origin.Entries.Count(e => e.OnlyElsewhere)} owned only from other eras";
        }
        else if (_mode == Mode.Extended)
        {
            for (var i = 0; i < _view.Count; i++)
            {
                var cell = _extended!.Cells[_view[i]];
                var (sheet, source) = i >= first && i < last ? Icon(cell.Slot.Species, cell.Slot.Form) : (null, SKRect.Empty);
                cells.Add(new DexModesCell(cell.Slot.Species, sheet, source, cell.Owned, Shiny: cell.Filled?.Shiny == true,
                    Variant: cell.Category != DexFormCategories.None));
            }
            var byGen = _extended!.ByGeneration();
            title = $"Extended dex · {_extended.Owned}/{_extended.Total}";
            tabs = [.. Romans.Select((r, g) => (r, g == 0 ? $"{_extended.Owned}/{_extended.Total}"
                : byGen.FirstOrDefault(s => s.Generation == g) is var s && s.Total > 0 ? $"{s.Owned}/{s.Total}" : ""))];
            progress = $"{_extended.Owned}/{_extended.Total} entries · {_extended.Total - _extended.Owned} missing";
        }
        else
        {
            var box = _box < _layout.Count ? _layout[_box] : null;
            for (var i = 0; i < DexModesPaint.BoxColumns * DexModesPaint.BoxRows; i++)
            {
                if (box is null || i >= box.Cells.Count || box.Cells[i] is not { } at)
                {
                    cells.Add(new DexModesCell(0, null, SKRect.Empty, false, Empty: true));
                    continue;
                }
                var cell = _extended!.Cells[at];
                var (sheet, source) = Icon(cell.Slot.Species, cell.Slot.Form);
                cells.Add(new DexModesCell(cell.Slot.Species, sheet, source, cell.Owned, Shiny: cell.Filled?.Shiny == true,
                    Variant: cell.Category != DexFormCategories.None));
            }
            title = box is null ? "Box planner" : $"Box {_box + 1:00} / {_layout.Count} · {box.Title}";
            tabs = [.. Breaks.Select(b => (b.Label, b.Count))];
            tab = Array.FindIndex(Breaks, b => b.Breaks == _breaks);
            var gaps = _extended!.Total - _extended.Owned;
            progress = $"{_layout.Count} boxes · {_extended.Owned} filled · {gaps} gaps";
        }

        int number = 0;
        string name = "", line = "";
        SKImage? sprite = null;
        var owned = false;
        if (CurrentOrigin is { } o)
        {
            number = o.Species;
            name = Name(o.Species, 0, "");
            line = $"Generation {Romans[o.Generation]} species · counts from a Gen {Romans[o.Generation]} game";
            sprite = Big(o.Species, 0, _shiny);
            owned = o.Owned;
        }
        else if (CurrentCell is { } cell)
        {
            number = cell.Slot.Species;
            name = Name(cell.Slot.Species, cell.Slot.Form, cell.Label);
            line = _mode == Mode.Boxes
                ? $"Box {_box + 1:00}, slot {_cursor + 1:00}" + (cell.Filled is { } m ? $" · {m.Where.Describe()}" : " · missing")
                : $"{CategoryName(cell.Category)} · Generation {Romans[OriginDex.IntroGeneration(cell.Slot.Species)]} species";
            sprite = Big(cell.Slot.Species, cell.Slot.Form, _shiny);
            owned = cell.Owned;
        }
        var canPrevious = _mode == Mode.Boxes ? _layout.Count > 1 : true;
        var empty = _missingOnly ? "Nothing missing here." : "No entry here.";
        var hint = _mode == Mode.Boxes ? "Nothing is lost: every Pokémon keeps a slot." : "";
        return new DexModesScene(title, canPrevious, canPrevious, tabs, tab, cells, _cursor, _scrollRow, _mode == Mode.Boxes,
            number, name, line, sprite, owned, Rows(), Actions(), progress, _status.Length > 0 ? _status : hint, empty);
    }

    private static string CategoryName(DexFormCategories category) => category switch
    {
        DexFormCategories.Regional => "Regional form",
        DexFormCategories.OtherForms => "Form",
        DexFormCategories.GenderDifferences => "Female look",
        DexFormCategories.Gigantamax => "Gigantamax-capable",
        DexFormCategories.AlcremieSweets => "Alcremie sweet",
        _ => "Base entry",
    };

    private (SKImage? Sheet, SKRect Source) Icon(int species, int form)
    {
        if (!_sprites.TryGetShowdownIcon(new SpriteLook(species, form, false), Invalidate, out var bitmap, out var cell) || bitmap is null)
            return (null, SKRect.Empty);
        if (!_sheets.TryGetValue(bitmap, out var image)) _sheets[bitmap] = image = SKImage.FromBitmap(bitmap);
        return (image, SKRect.Create(cell.Left, cell.Top, cell.Width, cell.Height));
    }

    private SKImage? Big(int species, int form, bool shiny)
    {
        if (_big.TryGetValue((species, form, shiny), out var known)) return known;
        if (_sprites.GetSprite(species, form, shiny) is not { } bitmap)
        {
            _sprites.Warm(species, form, shiny, Invalidate);
            return null;
        }
        // Cropped to the Pokémon itself, so it stands on the sheet's floor.
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var whole = SKImage.FromBitmap(bitmap);
        var image = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
        _big[(species, form, shiny)] = image;
        return image;
    }

    // ── Touch ────────────────────────────────────────────────────────────────

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_hits is not { } hits || !_loaded) return;
        var at = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                _pressedAt = at;
                _dragRows = 0;
                _dragged = false;
                return;
            case SKTouchAction.Moved when _pressedAt is { } start && hits.Grid.Contains(start) && _mode != Mode.Boxes:
                if (!_dragged && Math.Abs(start.Y - at.Y) < hits.CellHeight / 3) return;
                _dragged = true;
                var step = (int)((start.Y - at.Y) / hits.CellHeight - _dragRows);
                if (step != 0)
                {
                    _dragRows += step;
                    var total = (Count + hits.Columns - 1) / hits.Columns;
                    _scrollRow = Math.Clamp(_scrollRow + step, 0, Math.Max(0, total - hits.VisibleRows));
                    _canvas.InvalidateSurface();
                }
                return;
            case SKTouchAction.Released:
                var tapped = !_dragged;
                _pressedAt = null;
                if (tapped) OnTap(hits, at);
                return;
            case SKTouchAction.Cancelled:
                _pressedAt = null;
                return;
        }
    }

    private void OnTap(DexModesHits hits, SKPoint at)
    {
        if (hits.Previous.Contains(at)) { Turn(-1); return; }
        if (hits.Next.Contains(at)) { Turn(1); return; }
        for (var i = 0; i < hits.Tabs.Count; i++)
            if (hits.Tabs[i].Contains(at)) { SelectTab(i); return; }
        foreach (var (index, rect) in hits.Cells)
        {
            if (!rect.Contains(at)) continue;
            if (index == _cursor) Activate();
            else SetCursor(index);
            return;
        }
        foreach (var (index, rect) in hits.Rows)
            if (rect.Contains(at) && _mode == Mode.Extended && index < Toggles.Length) { Toggle(Toggles[index].Category); return; }
        var actions = Actions();
        foreach (var (index, rect) in hits.Actions)
            if (rect.Contains(at) && index < actions.Count) { _ = RunActionAsync(actions[index]); return; }
    }

    // ── Pad ──────────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (!_loaded) return true;
        switch (button)
        {
            case PadButton.Up: Move(-Columns); return true;
            case PadButton.Down: Move(Columns); return true;
            case PadButton.Left: Move(-1); return true;
            case PadButton.Right: Move(1); return true;
            case PadButton.L: Turn(-1); return true;
            case PadButton.R: Turn(1); return true;
            case PadButton.A: Activate(); return true;
            case PadButton.B: Close(); return true;
            case PadButton.X: _ = ShowModeMenuAsync(); return true;
            case PadButton.Start: _ = RunActionAsync(Actions()[0]); return true;
            case PadButton.Y:
                if (_mode != Mode.Boxes) { _missingOnly = !_missingOnly; _cursor = 0; RefreshView(); }
                return true;
            default: return true;
        }
    }
}
