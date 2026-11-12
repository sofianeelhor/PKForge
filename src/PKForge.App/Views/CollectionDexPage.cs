using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;
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
/// catch. The forms view lists every collectible form (Unown letters, Vivillon patterns,
/// regional forms...) of the species that have several, missing ones as silhouettes.
/// Read-only: nothing here writes a save or the bank.
/// </summary>
public sealed class CollectionDexPage : IPadPagingHandler
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession? _session;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    // Cells are keys: species in the low 16 bits, form above (form 0 = the species itself).
    private readonly HashSet<int> _owned = [];
    private readonly HashSet<int> _shiny = [];
    private readonly HashSet<int> _ownedForms = [];
    private readonly HashSet<int> _shinyForms = [];
    private readonly List<int> _allIds = [];
    private readonly List<int> _formIds = [];
    private readonly List<int> _viewIds = [];
    private Func<int, int, bool>? _storableHere;
    private readonly DexGridView _grid;
    private readonly SecondScreenState? _secondState;
    private readonly SecondScreenClaim? _secondClaim;
    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _cursorInfo;
    private readonly HorizontalStackLayout _chips;
    private readonly List<Border> _chipBorders = [];
    private CollectionDexProgress? _progressData;
    private int? _genScope;          // null = all generations; 0 = this game
    private bool _shinyDex;
    private bool _formsDex;
    private bool _missingOnly;
    private bool _loaded;

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
            viewModel.Status = $"Collection dex closed. {Domain.ErrorText.Plain(error)}";
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

        _grid = new DexGridView(sprites) { VerticalOptions = LayoutOptions.Fill };
        _grid.Tapped += index => { _ = ShowHowToGetAsync(); };
        // The second screen shows the Pokédex page of the species under the cursor.
        _secondState = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        _secondClaim = _secondState?.Routes.OpenOverlay(SecondScreenOwner.Pokedex);
        _grid.CursorChanged += index =>
        {
            RefreshCursorInfo();
            if (_secondState is not null && index < _viewIds.Count) _secondState.PreviewSpecies = SpeciesOf(_viewIds[index]);
        };

        _chips = new HorizontalStackLayout { Spacing = 5 };

        View hints = Kit.WindowHints(
            ("A", "How to get", () => _ = ShowHowToGetAsync()),
            ("B", "Done", () => Close()),
            ("LR", "Generation", () => OnPadButton(PadButton.R)),
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
                TitleRow(),
                _chips,
                _grid,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetRow(_chips, 1);
        Grid.SetRow(_grid, 2);
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
            var catalog = LivingDexCatalogBuilder.Build();
            var storable = _session is null ? null : LivingDexCatalogBuilder.StorableIn(_session);

            // National scope: the bank spans every generation, so the tracker always
            // counts all nine; "this game" is only a view filter (RefreshView).
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Apply(collection);
                _allIds.AddRange(Enumerable.Range(1, Math.Min(CollectionDex.MaxSpecies, _data.SpeciesNames.Count - 1))
                    .Where(id => _data.SpeciesNames[id].Length > 0));
                _formIds.AddRange(_allIds.Where(id => catalog.Forms.ContainsKey(id))
                    .SelectMany(id => catalog.FormsOf(id).Select(form => Key(id, form))));
                _storableHere = storable;
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
    private static async Task<List<(int Species, int Form, bool Shiny)>> CollectAsync(ISaveEngineSession? open)
    {
        var collection = new List<(int Species, int Form, bool Shiny)>();
        var services = IPlatformApplication.Current?.Services;
        var bank = services?.GetService<IBankService>();
        if (bank is not null)
            foreach (var entry in bank.GetAll())
                if (entry.Info.Species > 0) collection.Add((entry.Info.Species, entry.Info.Form, entry.Info.Shiny));
        if (open is not null)
            foreach (var slot in open.Snapshot.Slots)
                if (slot.Species is > 0 && !slot.IsEgg) collection.Add((slot.Species.Value, slot.Form, slot.IsShiny));

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
                        if (slot.Species is > 0 && !slot.IsEgg) collection.Add((slot.Species.Value, slot.Form, slot.IsShiny));
                }
                catch (Exception)
                {
                    // A save that no longer parses (revoked grant, mid-write file)
                    // must not blank the whole tracker; skip it.
                }
            }
        return collection;
    }

    private void Apply(List<(int Species, int Form, bool Shiny)> collection)
    {
        _progressData = CollectionDex.Compute(collection.Select(c => (c.Species, c.Shiny)), CollectionDex.MaxSpecies, _data.SpeciesNames);
        _owned.Clear();
        _shiny.Clear();
        _ownedForms.Clear();
        _shinyForms.Clear();
        foreach (var (species, form, shiny) in collection)
        {
            if (species < 1 || species >= _data.SpeciesNames.Count || _data.SpeciesNames[species].Length == 0) continue;
            _owned.Add(species);
            _ownedForms.Add(Key(species, form));
            if (shiny)
            {
                _shiny.Add(species);
                _shinyForms.Add(Key(species, form));
            }
        }
    }

    private static int Key(int species, int form) => species | (form << 16);
    private static int SpeciesOf(int key) => key & 0xFFFF;
    private static int FormOf(int key) => key >> 16;

    /// <summary>Owned and total for the cells of one generation (0: this game, null: all), in the current view.</summary>
    private (int Owned, int Total) Tally(int? scope)
    {
        var cells = InScope(_formsDex ? _formIds : _allIds, scope).ToList();
        return (cells.Count(IsOwned), cells.Count);
    }

    private IEnumerable<int> InScope(IEnumerable<int> cells, int? scope)
    {
        if (scope is > 0)
        {
            var range = CollectionDex.GenRanges.Single(r => r.Generation == scope);
            return cells.Where(key => SpeciesOf(key) >= range.First && SpeciesOf(key) <= range.Last);
        }
        if (scope == 0)
        {
            var cap = _session?.MaxSpeciesId ?? CollectionDex.MaxSpecies;
            return cells.Where(key => SpeciesOf(key) <= cap && (!_formsDex || _storableHere?.Invoke(SpeciesOf(key), FormOf(key)) != false));
        }
        return cells;
    }

    private void RefreshChrome()
    {
        if (_progressData is null) return;
        var scope = (_shinyDex, _formsDex) switch
        {
            (true, true) => "Shiny forms",
            (false, true) => "Forms",
            (true, false) => "Shiny living dex",
            _ => "Living dex",
        };
        var scoped = ScopedProgress();
        _title.Text = scope;
        _progress.Text = _formsDex
            ? $"{scoped.Owned}/{scoped.Total} forms"
            : $"{scoped.Owned}/{scoped.Total} · shiny {_progressData.Shiny}/{_progressData.TotalSpecies}";

        _chips.Children.Clear();
        _chipBorders.Clear();
        AddChip("ALL", null);
        if (_session is not null) AddChip("This game", 0);
        foreach (var segment in _progressData.Segments)
        {
            var (owned, total) = _formsDex ? Tally(segment.Generation) : (_shinyDex ? segment.Shiny : segment.Owned, segment.Total);
            if (total > 0) AddChip($"{Roman(segment.Generation)} {owned}/{total}", segment.Generation);
        }
    }

    private (int Owned, int Total) ScopedProgress()
    {
        if (_progressData is null) return (0, 0);
        if (_formsDex) return Tally(_genScope);
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
        RefreshView();
        _grid.SetCursor(0);
    }

    private void ToggleMissingOnly()
    {
        _missingOnly = !_missingOnly;
                RefreshView();
    }


    private int Count => _viewIds.Count;

    /// <summary>The title keeps its width; the counter takes the rest, so they never overlap.</summary>
    private Grid TitleRow()
    {
        var row = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { _title, _progress },
        };
        Grid.SetColumn(_progress, 1);
        return row;
    }

    private bool IsOwned(int key) => _formsDex
        ? (_shinyDex ? _shinyForms : _ownedForms).Contains(key)
        : (_shinyDex ? _shiny : _owned).Contains(SpeciesOf(key));

    private bool IsShinyOwned(int key) => _formsDex ? _shinyForms.Contains(key) : _shiny.Contains(SpeciesOf(key));

    private void RefreshView()
    {
        var view = InScope(_formsDex ? _formIds : _allIds, _genScope);
        if (_missingOnly) view = view.Where(id => !IsOwned(id));
        _viewIds.Clear();
        _viewIds.AddRange(view);
        _grid.Show(_viewIds, LookOf, identify: key => (SpeciesOf(key), FormOf(key)));
        RefreshChrome();
        RefreshCursorInfo();
    }

    /// <summary>Owned entries in colour with their ball, missing ones as silhouettes, shinies starred.</summary>
    private DexGridView.Look LookOf(int key)
    {
        var owned = IsOwned(key);
        return new DexGridView.Look(Seen: owned, Caught: owned, Shiny: _shinyDex && owned, ShinyMark: IsShinyOwned(key));
    }

    private void RefreshCursorInfo()
    {
        if (!_loaded || Count == 0)
        {
            _cursorInfo.Text = "";
            return;
        }
        var key = _viewIds[Math.Min(_grid.Cursor, Count - 1)];
        var id = SpeciesOf(key);
        var state = IsShinyOwned(key)
            ? (_shinyDex ? "Shiny owned" : "Owned + shiny")
            : (_formsDex ? _ownedForms.Contains(key) : _owned.Contains(id)) ? "Owned" : "Missing";
        _cursorInfo.Text = $"#{id:000} {CellName(key)} · {state}";
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: _grid.Move(-1, 0); return true;
            case PadButton.Right: _grid.Move(1, 0); return true;
            case PadButton.Up: _grid.Move(0, -1); return true;
            case PadButton.Down: _grid.Move(0, 1); return true;
            case PadButton.L: _grid.JumpSection(-1); return true;
            case PadButton.R: _grid.JumpSection(1); return true;
            case PadButton.A: _ = ShowHowToGetAsync(); return true;
            case PadButton.B: Close(); return true;
            case PadButton.X: _ = ShowScopeMenuAsync(); return true;
            case PadButton.Y: ToggleMissingOnly(); return true;
            case PadButton.Start: _ = OpenAutopilotAsync(); return true;
            default: return true; // the tracker owns the pad while open
        }
    }

    private async Task ShowScopeMenuAsync()
    {
        var options = new List<PadOption> { new("All generations") };
        if (_session is not null) options.Add(new PadOption("This game", Detail: "Only the species in the open save's game."));
        options.AddRange(_progressData!.Segments.Select(s => (s.Generation, Counts: _formsDex ? Tally(s.Generation) : (_shinyDex ? s.Shiny : s.Owned, s.Total)))
            .Where(s => s.Counts.Item2 > 0)
            .Select(s => new PadOption($"Gen {Roman(s.Generation)} · {s.Counts.Item1}/{s.Counts.Item2}")));
        options.Add(new PadOption(_shinyDex ? "View: normal living dex" : "View: SHINY living dex",
            Detail: _shinyDex ? null : "Only shiny Pokémon count toward each species."));
        options.Add(new PadOption(_formsDex ? "View: one per species" : "View: every form",
            Detail: _formsDex ? "Back to one entry per species." : "Lists each form on its own, like Unown letters and Vivillon patterns."));
        options.Add(new PadOption(DexModesChoice,
            Detail: "Origin dex (each species from its own generation), every form with switches, and a box planner for the Bank."));
        var choice = await PadMenu.ShowAsync(_host, "Scope", null, options.ToArray());
        if (choice is null) return;
        if (choice == DexModesChoice) { await OpenDexModesAsync(); return; }
        if (choice == "All generations") SetScope(null);
        else if (choice == "This game") SetScope(0);
        else if (choice.StartsWith("View: every form", StringComparison.Ordinal) || choice.StartsWith("View: one per", StringComparison.Ordinal))
        {
            _formsDex = !_formsDex;
            RefreshView();
            _grid.SetCursor(0);
        }
        else if (choice.StartsWith("View:", StringComparison.Ordinal)) { _shinyDex = !_shinyDex; RefreshView(); }
        else if (choice.StartsWith("Gen ", StringComparison.Ordinal) && int.TryParse(RomanToNumber(choice[4].ToString()), out var gen))
            SetScope(gen);
    }

    private static string? RomanToNumber(string roman) => roman switch
    {
        "I" => "1", "II" => "2", "III" => "3", "IV" => "4", "V" => "5",
        "VI" => "6", "VII" => "7", "VIII" => "8", "IX" => "9", _ => null,
    };

    private async Task ShowHowToGetAsync()
    {
        if (!_loaded || Count == 0) return;
        var key = _viewIds[Math.Min(_grid.Cursor, Count - 1)];
        var id = SpeciesOf(key);
        // A goes straight to "How to get" for this Pokémon: a menu holding that one choice
        // and a Close that B already gives was a step for nothing. No species wizard, and it
        // works with or without an open save (CATCH is offered only for a game the save can be).
        await EncounterGallery.ShowAllGamesForSpeciesAsync(_host, _viewModel, _session, id, () => { });

        // A catch may have added a species; recount before returning. National scope,
        // same as the initial count: a catch must never shrink the header to game scope.
        Apply(await CollectAsync(_session));
        RefreshView();
    }

    private const string DexModesChoice = "Origin dex, all forms and box planner";

    /// <summary>The Origin Dex, the Extended Living Dex and the box planner; the count follows a Bank arrangement.</summary>
    private async Task OpenDexModesAsync()
    {
        if (!_loaded) return;
        _router?.Remove(this);
        try
        {
            await DexModesPage.ShowAsync(_host, _viewModel, _data, _sprites);
        }
        finally
        {
            _router?.Push(this);
        }
        Apply(await CollectAsync(_session));
        RefreshView();
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

    /// <summary>"Vivillon (Sandstorm)"; the species name alone for form 0 or in the species view.</summary>
    private string CellName(int key)
    {
        var name = _data.SpeciesNames[SpeciesOf(key)];
        if (!_formsDex) return name;
        var form = LivingDexCatalogBuilder.FormName(SpeciesOf(key), FormOf(key));
        return form.Length == 0 ? name : $"{name} ({form})";
    }

    private void Close()
    {
        _secondClaim?.Release();
        if (_secondState is not null) _secondState.PreviewSpecies = null;
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(true);
    }
}
