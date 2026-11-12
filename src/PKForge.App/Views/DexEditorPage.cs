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
/// The Pokédex as one painted screen (<see cref="DexPaint"/>): the generation in the box
/// banner, the filters (all, seen, caught, missing) and the search on a strip of slanted tabs,
/// every species as box icons on the well's banded rows, and the species under the cursor on
/// a sheet at the right. A (or a second tap) cycles unseen, seen, caught; the sheet's plates
/// set a state directly; L/R hop a generation; Y turns the filter; X opens the actions.
/// Edits are staged and saved through the safe write path on exit.
/// </summary>
public sealed class DexEditorPage : IPadPagingHandler
{
    private static readonly string[] FilterNames = ["All", "Seen", "Caught", "Missing"];
    private const string HowToGet = "How to get this one";
    private const string Research = "Research tasks";
    private const string MoreActions = "More actions (X)";

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
    private readonly SecondScreenState? _secondState;
    private readonly SecondScreenClaim? _secondClaim;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly ContentView _footer = new();
    private readonly Dictionary<SKBitmap, SKImage> _sheets = [];
    private readonly Dictionary<(int Species, bool Shiny), SKImage?> _big = [];
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private HashSet<int> _researchSpecies = [];
    private bool _researchSupported;
    private bool _gapsMode;
    private bool _loaded;
    private string _query = "";
    private string _status = "";
    private int _filter;
    private int _cursor;
    private int _scrollRow;
    private DexHits? _hits;
    private SKPoint? _pressedAt;
    private float _dragRows;
    private bool _dragged;

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session,
        ILegalizerService legalizer, IGameDataService data, ISpriteService sprites)
    {
        try
        {
            await new DexEditorPage(host, viewModel, session, legalizer, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"Dex editor closed. {Domain.ErrorText.Plain(error)}";
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
        // The second screen shows the Pokédex page of the species under the cursor.
        _secondState = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        _secondClaim = _secondState?.Routes.OpenOverlay(SecondScreenOwner.Pokedex);

        _canvas.PaintSurface += PaintGuard.Wrap("DexEditorPage.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        SetFooter();
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, _footer },
        };
        Grid.SetRow(_footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });

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
                    // A species the game has no dex cell for would take the edit and drop it on save.
                    if (_data.SpeciesNames[id].Length == 0 || !_session.IsDexSpecies(id)) continue;
                    var state = _session.GetDexEntry(id);
                    states[id] = (state.Seen, state.Caught);
                }
                var missing = _session.GetMissingSpecies();
                // Legends: Arceus keeps a research page per species; the sheet offers it where there is one.
                var research = LegendsResearchService.Supports(_session);
                var researchSpecies = research ? LegendsResearchService.GetSpecies(_session).ToHashSet() : [];
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var pair in states) _states[pair.Key] = pair.Value;
                    _orderedIds.AddRange(states.Keys.Order());
                    _missing.AddRange(missing);
                    _researchSupported = research;
                    _researchSpecies = researchSpecies;
                    RefreshView();
                    _loaded = true;
                    loader.Close();
                    _router?.Push(this);
                });
            }
            catch (Exception error)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    loader.Close();
                    _viewModel.Status = $"Dex editor failed. {Domain.ErrorText.Plain(error)}";
                    TearDown();
                    _result.TrySetResult(false);
                });
            }
        });
    }

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    /// <summary>The footer says what the buttons do in the mode the dex is in.</summary>
    private void SetFooter() => _footer.Content = _gapsMode
        ? DsChrome.Footer(
            ("A", "Pick", () => OnPadButton(PadButton.A)),
            ("LR", "Generation", () => OnPadButton(PadButton.R)),
            ("Y", "Select all", () => OnPadButton(PadButton.Y)),
            ("X", "Actions", () => OnPadButton(PadButton.X)),
            ("B", "Done", () => OnPadButton(PadButton.B)))
        : DsChrome.Footer(
            ("A", "Cycle", () => OnPadButton(PadButton.A)),
            ("LR", "Generation", () => OnPadButton(PadButton.R)),
            ("Y", "Filter", () => OnPadButton(PadButton.Y)),
            ("X", "Actions", () => OnPadButton(PadButton.X)),
            ("B", "Done", () => OnPadButton(PadButton.B)));

    private int? CurrentSpecies => _cursor < _viewIds.Count ? _viewIds[_cursor] : null;

    // ── Closing ──────────────────────────────────────────────────────────────

    private async Task CloseAsync()
    {
        // One close at a time: B again while the save question or the write is up must not
        // start a second write on the same session.
        if (_closing) return;
        _closing = true;
        try
        {
            if (_staged.Count > 0)
            {
                var choice = await PadMenu.ShowAsync(_host, "Save dex changes?",
                    $"{_staged.Count} species changed.", "Save changes", "Discard changes", "Keep editing");
                if (choice == "Save changes") { await ApplyAndCloseAsync(); return; }
                if (choice == "Keep editing" || choice is null) return;
            }
            TearDown();
            _result.TrySetResult(false);
        }
        finally { _closing = false; }
    }

    private bool _closing;

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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditDex);
            if (!saved)
            {
                // The write aborted (validation, storage, format refusal): keep the
                // editor open with the staged changes instead of closing as if it worked.
                // The box screen's status already says why; this line says what is kept.
                _status = "Not saved: your changes are still here, the file was left as it was.";
                Invalidate();
                return;
            }
        }
        _staged.Clear();
        TearDown();
        _result.TrySetResult(true);
    }

    private void TearDown()
    {
        _secondClaim?.Release();
        if (_secondState is not null) _secondState.PreviewSpecies = null;
        _router?.Remove(this);
        _host.Remove(_overlay);
        foreach (var image in _sheets.Values) image.Dispose();
        foreach (var image in _big.Values) image?.Dispose();
        _sheets.Clear();
        _big.Clear();
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void SelectAllGaps()
    {
        if (!_gapsMode) return;
        if (_fillSelection.Count == _missing.Count) _fillSelection.Clear();
        else
        {
            _fillSelection.Clear();
            _fillSelection.UnionWith(_missing);
        }
        Invalidate();
    }

    private void SetGapsMode(bool on)
    {
        _gapsMode = on;
        _status = "";
        SetFooter();
        RefreshView();
    }

    private async Task ShowActionsAsync()
    {
        if (_gapsMode)
        {
            var gapChoice = await PadMenu.ShowAsync(_host, "Living dex gaps",
                $"{_missing.Count} species missing from storage. {_fillSelection.Count} selected.",
                new PadOption($"Generate selected ({_fillSelection.Count})", IconPath: "create",
                    Detail: "Creates a legal Pokémon of each selected species in your boxes."),
                new PadOption("Jump to a number", IconPath: "search", Detail: "Puts the cursor on a national dex number."),
                new PadOption("Switch to dex editor", IconPath: "pokedex", Detail: "Back to marking species seen and caught."),
                new PadOption("Close", IconPath: "close"));
            if (gapChoice == "Switch to dex editor")
            {
                SetGapsMode(false);
                return;
            }
            if (gapChoice == "Jump to a number") { await JumpToNumberAsync(); return; }
            if (gapChoice != $"Generate selected ({_fillSelection.Count})" || _fillSelection.Count == 0) return;
            var species = _fillSelection.OrderBy(x => x).ToList();
            var overlay = LoadingOverlay.Show(_host, "Generating…", "The legalizer is building each mon offline.");
            try
            {
                await _viewModel.RunMutationAsync(s => _legalizer.FillSpecies(s, species,
                    (done, total) => overlay.Report(done, total)), Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                    action: SaveAction.CreateMon);
                _viewModel.RefreshAllSlots();
                _missing.RemoveAll(_fillSelection.Contains);
                _fillSelection.Clear();
                RefreshView();
            }
            finally { overlay.Close(); }
            return;
        }

        var options = new List<PadOption>
        {
            new(HowToGet, IconPath: "map", Detail: "Where and how to find the species under the cursor."),
            new("Jump to a number", IconPath: "search", Detail: "Puts the cursor on a national dex number."),
            new("Search by name", IconPath: "search", Detail: "Narrows the dex to the names that match."),
            new("Mark everything seen", IconPath: "selectall", Detail: "Every species becomes seen; caught ones stay caught."),
            new("Complete the Pokédex", IconPath: "pokedex", Detail: "Every species becomes seen and caught; you choose to save when you leave."),
            new("Switch to living dex gaps", IconPath: "storage", Detail: "Species missing from your boxes, ready to create."),
            new("Discard staged changes", IconPath: "clear", Detail: "Undoes the changes made since you opened the dex."),
        };
        // Legends: Arceus keeps a research page per species; it is written right away, not staged.
        var researchSpecies = ResearchSpecies();
        if (researchSpecies != 0)
            options.Insert(1, new PadOption(Research, IconPath: "pokedex", Detail: "The species' research tasks: counts, level 10, perfect. Saved right away."));
        if (_researchSupported)
            options.Insert(researchSpecies != 0 ? 2 : 1, new PadOption("Perfect all research", IconPath: "selectall", Detail: "Every species' research tasks maxed and reported. Saved right away."));
        var choice = await PadMenu.ShowAsync(_host, "Dex actions", null, [.. options]);
        switch (choice)
        {
            case Research:
            case HowToGet:
                await RunSheetActionAsync(choice);
                return;
            case "Perfect all research":
                if (await ResearchTasksEditor.PerfectAllAsync(_host, _viewModel))
                    ReloadStates(_orderedIds);
                return;
            case "Jump to a number":
                await JumpToNumberAsync();
                return;
            case "Search by name":
                await SearchAsync();
                return;
            case "Mark everything seen":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, StateOf(id).Caught); // caught species stay caught
                break;
            case "Complete the Pokédex":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, true);
                break;
            case "Switch to living dex gaps":
                SetGapsMode(true);
                return;
            case "Discard staged changes":
                _staged.Clear();
                break;
        }
        Invalidate();
    }

    private int ResearchSpecies() =>
        CurrentSpecies is { } id && _researchSupported && _researchSpecies.Contains(id) ? id : 0;

    /// <summary>The sheet's own rows: what can be done with the species under the cursor.</summary>
    private List<string> SheetActions()
    {
        if (CurrentSpecies is null) return [];
        var list = new List<string> { HowToGet };
        if (!_gapsMode && ResearchSpecies() != 0) list.Add(Research);
        list.Add(MoreActions);
        return list;
    }

    private async Task RunSheetActionAsync(string action)
    {
        switch (action)
        {
            case HowToGet when CurrentSpecies is { } id:
                await EncounterGallery.ShowForSpeciesAsync(_host, _viewModel, _session, id, 0, Invalidate);
                return;
            case Research when ResearchSpecies() is var research and not 0:
                if (await ResearchTasksEditor.ShowAsync(_host, _viewModel, _session, research, _data.SpeciesNames[research]))
                    ReloadStates([research]);
                return;
            case MoreActions:
                await ShowActionsAsync();
                return;
        }
    }

    private async Task SearchAsync()
    {
        var typed = await TextPopup.ShowLineAsync(_host, "Search", "Part of the name, or a number", _query, confirm: "Search");
        if (typed is null) return;
        _query = typed;
        RefreshView();
    }

    /// <summary>Puts the cursor on a number; a filter or search hiding it gives way first.</summary>
    private async Task JumpToNumberAsync()
    {
        var source = _gapsMode ? _missing : _orderedIds;
        if (source.Count == 0) return;
        var typed = await StatsPopup.ShowSingleAsync(_host, "Jump to No.", CurrentSpecies ?? source[0], source.Max());
        if (typed is not { } number) return;
        if (!_viewIds.Contains(number) && source.Contains(number))
        {
            _filter = 0;
            _query = "";
            RefreshView();
        }
        var at = _viewIds.FindIndex(id => id >= number);
        SetCursor(at < 0 ? _viewIds.Count - 1 : at);
    }

    private void Activate(int species)
    {
        if (_gapsMode)
        {
            if (!_fillSelection.Remove(species)) _fillSelection.Add(species);
        }
        else if (_states.ContainsKey(species))
        {
            // Cycle from the CURRENT state (staged wins over the saved state), or every
            // press would restart from the save's original "unseen" and never advance.
            var current = StateOf(species);
            Stage(species, current switch
            {
                { Seen: false } => (true, false),
                { Caught: false } => (true, true),
                _ => (false, false),
            });
        }
        Invalidate();
    }

    /// <summary>Stages a state; going back to what the save holds drops the change.</summary>
    private void Stage(int species, (bool Seen, bool Caught) state)
    {
        if (_states.TryGetValue(species, out var saved) && saved == state) _staged.Remove(species);
        else _staged[species] = state;
    }

    /// <summary>Re-reads saved seen/caught after a write made outside the staging (research marks forms obtained).</summary>
    private void ReloadStates(IEnumerable<int> species)
    {
        foreach (var id in species.ToList())
        {
            var state = _session.GetDexEntry(id);
            _states[id] = (state.Seen, state.Caught);
        }
        Invalidate();
    }

    private (bool Seen, bool Caught) StateOf(int species) =>
        _staged.TryGetValue(species, out var staged) ? staged
        : _states.TryGetValue(species, out var state) ? state
        : (false, false);

    private bool Passes(int id, int filter) => filter switch
    {
        1 => StateOf(id).Seen,
        2 => StateOf(id).Caught,
        3 => !StateOf(id).Caught,
        _ => true,
    };

    /// <summary>Applies the filter and the search to the active list; the cursor keeps its species when it can.</summary>
    private void RefreshView()
    {
        var keep = CurrentSpecies;
        _viewIds.Clear();
        var source = _gapsMode ? _missing : _orderedIds;
        var query = _query.Trim();
        foreach (var id in source)
        {
            if (!_gapsMode && !Passes(id, _filter)) continue;
            if (query.Length == 0
                || _data.SpeciesNames[id].Contains(query, StringComparison.OrdinalIgnoreCase)
                || id.ToString() == query.TrimStart('#', '0'))
                _viewIds.Add(id);
        }
        var at = keep is { } species ? _viewIds.IndexOf(species) : -1;
        SetCursor(Math.Max(0, at));
    }

    // ── Cursor and paging ────────────────────────────────────────────────────

    private int VisibleRows => DexPaint.VisibleRows(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));

    private void SetCursor(int index)
    {
        _cursor = _viewIds.Count == 0 ? 0 : Math.Clamp(index, 0, _viewIds.Count - 1);
        KeepCursorInView();
        if (_secondState is not null && CurrentSpecies is { } id) _secondState.PreviewSpecies = id;
        Invalidate();
    }

    private void KeepCursorInView()
    {
        var columns = DexPaint.Columns;
        var visible = VisibleRows;
        var row = _cursor / columns;
        if (row < _scrollRow) _scrollRow = row;
        else if (row >= _scrollRow + visible) _scrollRow = row - visible + 1;
        var rows = (_viewIds.Count + columns - 1) / columns;
        _scrollRow = Math.Clamp(_scrollRow, 0, Math.Max(0, rows - visible));
    }

    private void Move(int delta)
    {
        if (_viewIds.Count == 0) return;
        var target = _cursor + delta;
        // Up at the top stays; down past the end lands on the last species.
        if (target < 0) target = _cursor;
        else if (target >= _viewIds.Count) target = delta > 0 && _cursor / DexPaint.Columns < (_viewIds.Count - 1) / DexPaint.Columns ? _viewIds.Count - 1 : _cursor;
        SetCursor(target);
    }

    /// <summary>The first species of the next (1) or previous (-1) generation in the list.</summary>
    private void JumpRegion(int direction)
    {
        if (CurrentSpecies is not { } id) return;
        var here = DexRegions.Of(id);
        if (direction < 0)
        {
            var start = _viewIds.FindIndex(x => DexRegions.Of(x) == here);
            // Back from inside a generation goes to its own start first.
            if (start >= 0 && start < _cursor) { SetCursor(start); return; }
            for (var i = _cursor - 1; i >= 0; i--)
                if (DexRegions.Of(_viewIds[i]) != here)
                {
                    var region = DexRegions.Of(_viewIds[i]);
                    SetCursor(_viewIds.FindIndex(x => DexRegions.Of(x) == region));
                    return;
                }
            return;
        }
        for (var i = _cursor + 1; i < _viewIds.Count; i++)
            if (DexRegions.Of(_viewIds[i]) != here) { SetCursor(i); return; }
    }

    private void TurnFilter(int delta)
    {
        if (_gapsMode) return;
        _filter = (_filter + delta + FilterNames.Length) % FilterNames.Length;
        RefreshView();
    }

    // ── Paint ────────────────────────────────────────────────────────────────

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        if (!_loaded && _orderedIds.Count == 0) return;
        _hits = DexPaint.Paint(c, args.Info, BuildScene(), _face);
    }

    private DexScene BuildScene()
    {
        var columns = DexPaint.Columns;
        var first = _scrollRow * columns;
        var last = first + (VisibleRows + 1) * columns;
        var cells = new List<DexSceneCell>(_viewIds.Count);
        for (var i = 0; i < _viewIds.Count; i++)
        {
            var id = _viewIds[i];
            var state = StateOf(id);
            SKImage? sheet = null;
            var source = SKRect.Empty;
            // Only the rows on screen ask for their icons.
            if (i >= first && i < last) (sheet, source) = Icon(id);
            cells.Add(_gapsMode
                ? new DexSceneCell(id, sheet, source, Seen: true, Caught: false, Dimmed: !_fillSelection.Contains(id), Ticked: _fillSelection.Contains(id))
                : new DexSceneCell(id, sheet, source, state.Seen, state.Caught, Staged: _staged.ContainsKey(id)));
        }

        var region = CurrentSpecies is { } at ? DexRegions.Of(at) : null;
        string title;
        if (_gapsMode) title = $"Living dex gaps · {_missing.Count} missing";
        else if (region is null) title = "Pokédex";
        else
        {
            var ids = _orderedIds.Where(id => id >= region.First && id <= region.Last).ToList();
            title = $"{region.Roman} · {region.Name} · {ids.Count(id => StateOf(id).Caught)}/{ids.Count}";
        }
        var canPrevious = CurrentSpecies is { } p && _viewIds.Count > 0 && DexRegions.Of(_viewIds[0]) != DexRegions.Of(p);
        var canNext = CurrentSpecies is { } n && _viewIds.Count > 0 && DexRegions.Of(_viewIds[^1]) != DexRegions.Of(n);

        var seen = 0;
        var caught = 0;
        foreach (var id in _orderedIds)
        {
            var state = StateOf(id);
            if (state.Seen) seen++;
            if (state.Caught) caught++;
        }
        var filters = _gapsMode ? [] : FilterNames.Select((name, i) => (name, _orderedIds.Count(id => Passes(id, i)))).ToList();
        var progress = _gapsMode
            ? $"{_missing.Count} missing · {_fillSelection.Count} picked"
            : $"Seen {seen}/{_states.Count} · caught {caught}/{_states.Count} · {_staged.Count} changed";
        var empty = _query.Length > 0 ? $"No species matches “{_query}”."
            : _gapsMode ? "No gap: every species is in your boxes." : "No species here.";
        return new DexScene(title, canPrevious, canNext, filters, _filter, _query, cells, _cursor, _scrollRow, Sheet(),
            progress, _status, empty);
    }

    private DexSceneSheet? Sheet()
    {
        if (CurrentSpecies is not { } id) return null;
        var state = StateOf(id);
        var region = DexRegions.Of(id);
        var flags = id < _data.FormFlags.Count ? _data.FormFlags[id] : null;
        string? forms = null;
        if (flags is { HasForms: true })
        {
            var kinds = new List<string>();
            if (flags.Mega) kinds.Add("Mega");
            if (flags.Gigantamax) kinds.Add("Gigantamax");
            if (flags.Regional) kinds.Add("regional");
            forms = kinds.Count == 0 ? "Has other forms" : $"Forms: {string.Join(", ", kinds)}";
        }
        return new DexSceneSheet(id, _data.SpeciesNames[id],
            region is null ? "" : $"{region.Name} · Generation {region.Roman}",
            Big(id, false), Big(id, true),
            state.Caught ? 2 : state.Seen ? 1 : 0, _staged.ContainsKey(id), forms, SheetActions(),
            _gapsMode ? _fillSelection.Contains(id) : null);
    }

    private (SKImage? Sheet, SKRect Source) Icon(int species)
    {
        var look = new SpriteLook(species, 0, false);
        if (!_sprites.TryGetShowdownIcon(look, Invalidate, out var bitmap, out var cell) || bitmap is null) return (null, SKRect.Empty);
        if (!_sheets.TryGetValue(bitmap, out var image)) _sheets[bitmap] = image = SKImage.FromBitmap(bitmap);
        return (image, SKRect.Create(cell.Left, cell.Top, cell.Width, cell.Height));
    }

    private SKImage? Big(int species, bool shiny)
    {
        if (_big.TryGetValue((species, shiny), out var known)) return known;
        if (_sprites.GetSprite(species, 0, shiny) is not { } bitmap)
        {
            _sprites.Warm(species, 0, shiny, Invalidate);
            return null;
        }
        // Cropped to the Pokémon itself, so it stands on the sheet's floor.
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var whole = SKImage.FromBitmap(bitmap);
        var image = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
        _big[(species, shiny)] = image;
        return image;
    }

    // ── Touch: tap selects, a second tap acts; a drag on the grid scrolls ──

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
            case SKTouchAction.Moved when _pressedAt is { } start && hits.Grid.Contains(start):
                if (!_dragged && Math.Abs(start.Y - at.Y) < hits.CellHeight / 3) return;
                _dragged = true;
                var rows = (start.Y - at.Y) / hits.CellHeight;
                var step = (int)(rows - _dragRows);
                if (step != 0)
                {
                    _dragRows += step;
                    var total = (_viewIds.Count + hits.Columns - 1) / hits.Columns;
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

    private void OnTap(DexHits hits, SKPoint at)
    {
        if (hits.Previous.Contains(at)) { JumpRegion(-1); return; }
        if (hits.Next.Contains(at)) { JumpRegion(1); return; }
        if (hits.Search.Contains(at)) { _ = SearchAsync(); return; }
        for (var i = 0; i < hits.Filters.Count; i++)
            if (hits.Filters[i].Contains(at)) { _filter = i; RefreshView(); return; }
        foreach (var (index, rect) in hits.Cells)
        {
            if (!rect.Contains(at)) continue;
            if (index == _cursor) Activate(_viewIds[index]);
            else SetCursor(index);
            return;
        }
        for (var i = 0; i < hits.States.Count; i++)
        {
            if (!hits.States[i].Contains(at) || CurrentSpecies is not { } id) continue;
            if (_gapsMode) Activate(id);
            else
            {
                Stage(id, i switch { 0 => (false, false), 1 => (true, false), _ => (true, true) });
                Invalidate();
            }
            return;
        }
        var actions = SheetActions();
        foreach (var (index, rect) in hits.Actions)
            if (rect.Contains(at) && index < actions.Count) { _ = RunSheetActionAsync(actions[index]); return; }
    }

    // ── Pad ──────────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (!_loaded) return true;
        switch (button)
        {
            case PadButton.Up: Move(-DexPaint.Columns); return true;
            case PadButton.Down: Move(DexPaint.Columns); return true;
            case PadButton.Left: Move(-1); return true;
            case PadButton.Right: Move(1); return true;
            case PadButton.L: JumpRegion(-1); return true;
            case PadButton.R: JumpRegion(1); return true;
            case PadButton.A:
                if (CurrentSpecies is { } id) Activate(id);
                return true;
            case PadButton.B: _ = CloseAsync(); return true;
            case PadButton.X:
            case PadButton.Start: _ = ShowActionsAsync(); return true;
            case PadButton.Y:
                if (_gapsMode) SelectAllGaps();
                else TurnFilter(1);
                return true;
            case PadButton.Select:
                if (!_gapsMode) TurnFilter(-1);
                return true;
            default: return true;
        }
    }
}
