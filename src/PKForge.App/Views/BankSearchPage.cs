using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Whole-bank search: one overlay above the vault. Free text (species name or nickname) plus
/// the collector's filters - shiny, generation, source game, type, legendary/mythical, eggs,
/// level range, gender and default names - narrow every entry at once; twelve sort keys, each
/// either way round, reorder it; START hands the results back as the bank's marks; the
/// result grid reuses the box-slot renderer so a hit looks exactly like its slot does in the
/// bank. The predicates and comparators are <see cref="BankFilter"/>/<see cref="BankSorting"/>,
/// the same vault logic the organizer sorts with. A/tap jumps the bank view to the mon's box
/// and slot.
/// </summary>
public sealed class BankSearchPage : IPadHandler
{
    private const int Columns = BoxGridRenderer.Columns;
    private const int Rows = BoxGridRenderer.Rows;
    private const int PageSize = Columns * Rows;
    private const int MaxLevel = 100;

    /// <summary>Display names indexed by canonical <see cref="ParkType"/> id (1-18), so the
    /// type filter speaks the same numbers the park catalog returns.</summary>
    private static readonly string[] TypeNames =
    [
        "", "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
        "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark", "Fairy",
    ];

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly IBankFacts _facts;
    private readonly Action<BankEntry> _jump;
    private readonly Action<IReadOnlyList<BankEntry>>? _markAll;
    private readonly List<BankEntry> _all;
    private readonly List<BankEntry> _view = [];
    private readonly SKCanvasView _canvas;
    private readonly Entry _text;
    private readonly Label _count;
    private readonly Label _cursorInfo;
    private readonly HorizontalStackLayout _chips;

    private BankFilter _filter = BankFilter.None;
    private BankSortOrder _sort = BankSortOrder.DexNumber;
    private bool _reverse;
    private int _page;
    private int _cursor;

    /// <param name="markAll">When given, START (and the MARK chip) hands every current result back
    /// to the bank as its organizer selection - "select all filtered".</param>
    public static Task ShowAsync(Grid host, IBankService bank, IGameDataService data, ISpriteService sprites,
        Action<BankEntry> jump, Action<IReadOnlyList<BankEntry>>? markAll = null) =>
        new BankSearchPage(host, bank, data, sprites, jump, markAll)._result.Task;

    private BankSearchPage(Grid host, IBankService bank, IGameDataService data, ISpriteService sprites,
        Action<BankEntry> jump, Action<IReadOnlyList<BankEntry>>? markAll)
    {
        _markAll = markAll;
        _host = host;
        _data = data;
        _sprites = sprites;
        _facts = new BankFacts(bank, data);
        _jump = jump;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _all = [.. bank.GetAll()]; // the vault is an in-memory index; the snapshot is the scan

        var title = new Label { Text = "BANK SEARCH", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        _count = new Label
        {
            TextColor = UiTokens.Ink1,
            FontFamily = DsChrome.PixelFont,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.End,
            HorizontalOptions = LayoutOptions.End,
        };
        _cursorInfo = new Label { TextColor = UiTokens.Maroon, FontFamily = DsChrome.PixelFont, FontSize = 13 };

        _text = new Entry
        {
            Placeholder = "NAME OR NICKNAME…",
            PlaceholderColor = UiTokens.Ink1,
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.ShellPress,
            FontFamily = DsChrome.PixelFont,
            FontSize = 13,
            HeightRequest = 36,
            // Names are proper nouns: no red squiggles, no autocorrect.
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
        };
        _text.TextChanged += (_, args) =>
        {
            SetFilter(_filter with { Query = args.NewTextValue ?? "" });
        };

        _chips = new HorizontalStackLayout { Spacing = 5 };

        _canvas = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        View hints = Kit.HintBar(
            ("A", "Jump", null),
            ("B", "Done", () => Close()),
            ("LR", "Page", null),
            ("X", "Filter", () => _ = ShowFilterMenuAsync()),
            ("Y", "Shiny", ToggleShinyOnly),
            ("+", "Mark all", MarkAllResults));

        var content = new Grid
        {
            RowSpacing = 6,
            // title+count / search field / filter chips / GRID (the only elastic row) / cursor line / hints.
            RowDefinitions =
            [
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto),
            ],
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                    Children = { title, _count },
                },
                _text,
                _chips,
                _canvas,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetRow(_text, 1);
        Grid.SetRow(_chips, 2);
        Grid.SetRow(_canvas, 3);
        Grid.SetRow(_cursorInfo, 4);
        Grid.SetRow(hints, 5);

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

        RefreshView();
        _router?.Push(this);
    }

    // ── Filtering and sorting ──────────────────────────────────────────────────

    private int Count => _view.Count;
    private int PageCount => Math.Max(1, (Count + PageSize - 1) / PageSize);
    private BankEntry EntryAt(int absolute) => _view[absolute];

    private string SpeciesName(int species) => _facts.SpeciesName(species);

    private static string Roman(int gen) => gen switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => $"{gen}",
    };

    /// <summary>Every filter change re-runs the query from the top of the list.</summary>
    private void SetFilter(BankFilter filter)
    {
        _filter = filter;
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    /// <summary>The whole view: the vault through the filter, ordered (pure logic in
    /// <see cref="BankFilter"/> and <see cref="BankSorting"/>, so the grid and the tests
    /// see exactly the same rules).</summary>
    private void RefreshView()
    {
        _view.Clear();
        _view.AddRange(BankSorting.Order(_all.Where(e => _filter.Matches(e, _facts)), _sort, _facts, _reverse));
        _page = Math.Clamp(_page, 0, PageCount - 1);
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, Count - 1));
        RefreshChrome();
        RefreshCursorInfo();
        WarmVisible();
        _canvas.InvalidateSurface();
    }

    private void RefreshChrome()
    {
        _count.Text = $"{Count} OF {_all.Count}";
        _chips.Children.Clear();
        AddChip("SHINY", active: _filter.ShinyOnly, ToggleShinyOnly);
        AddChip($"FILTERS · {_filter.ActiveCount}", active: _filter.ActiveCount > 0, () => _ = ShowFilterMenuAsync());
        AddChip($"GEN: {(_filter.Generation is { } gen ? Roman(gen) : "ALL")}", active: _filter.Generation is not null,
            () => _ = PickGenerationAsync());
        AddChip($"SRC: {_filter.SourceName ?? "ALL"}", active: _filter.SourceName is not null, () => _ = PickSourceAsync());
        AddChip($"SORT: {SortLabel(_sort)}{(_reverse ? " ↓" : "")}", active: IsSorted, () => _ = ShowSortMenuAsync());
        if (_markAll is not null && Count > 0)
            AddChip($"MARK {Count}", active: false, MarkAllResults);
        if (_filter.IsActive || IsSorted)
            AddChip("RESET", active: true, ResetFilters);
    }

    private bool IsSorted => _sort != BankSortOrder.DexNumber || _reverse;

    private void ResetFilters()
    {
        _text.Text = "";
        _sort = BankSortOrder.DexNumber;
        _reverse = false;
        SetFilter(BankFilter.None);
    }

    private void ToggleShinyOnly()
    {
        var shiny = !_filter.ShinyOnly;
        _filter = _filter with { ShinyOnly = shiny };
        _text.Text = _text.Text; // keep the field's text; only the filter changed
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    /// <summary>A filter chip: selected filters go cobalt, idle chips stay recessed.</summary>
    private void AddChip(string label, bool active, Action onTap)
    {
        var chip = new Border
        {
            BackgroundColor = active ? UiTokens.MenuBlue : UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(8, 3),
            Content = new Label
            {
                Text = label,
                TextColor = UiTokens.Ink0,
                FontFamily = DsChrome.PixelFont,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
            },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        chip.GestureRecognizers.Add(tap);
        _chips.Children.Add(chip);
    }

    // ── The collector's filters ────────────────────────────────────────────────

    /// <summary>
    /// X: one menu over every collector filter, each line showing its current setting. Picking
    /// one sets it and re-opens the menu (the chip-flow pattern), so a whole search is built
    /// without leaving the pad.
    /// </summary>
    private async Task ShowFilterMenuAsync()
    {
        while (true)
        {
            var choice = await PadMenu.ShowAsync(_host, $"BANK FILTERS · {Count} MATCH",
                "Pick a filter, set it, and this menu comes back. RESET clears everything.",
                new PadOption($"Shiny only: {OnOff(_filter.ShinyOnly)}", IconPath: "shiny"),
                new PadOption($"Generation: {(_filter.Generation is { } gen ? Roman(gen) : "ALL")}", IconPath: "generation"),
                new PadOption($"Source game: {_filter.SourceName ?? "ALL"}", IconPath: "game"),
                new PadOption($"Type: {(_filter.TypeId is { } type ? TypeNames[type].ToUpperInvariant() : "ALL")}", IconPath: "type"),
                new PadOption($"Rarity: {_filter.Rarity switch
                {
                    BankRarity.Legendary => "LEGENDARY",
                    BankRarity.Mythical => "MYTHICAL",
                    BankRarity.LegendaryOrMythical => "LEGENDARY OR MYTHICAL",
                    _ => "ALL",
                }}", IconPath: "rarity"),
                new PadOption($"Eggs: {OnOff(_filter.EggOnly)}", IconPath: "egg"),
                new PadOption($"Gender: {GenderName(_filter.Gender)}", IconPath: "gender"),
                new PadOption($"Default names only: {OnOff(_filter.DefaultNamedOnly)}", IconPath: "rename"),
                new PadOption($"Lowest level: {_filter.LevelMin?.ToString() ?? "ANY"}", IconPath: "level"),
                new PadOption($"Highest level: {_filter.LevelMax?.ToString() ?? "ANY"}", IconPath: "level"),
                new PadOption("Reset all filters", IconPath: "restore"),
                new PadOption("Done", IconPath: "confirm"));

            switch (choice)
            {
                case null or "Done":
                    RefreshView();
                    return;
                case "Reset all filters":
                    _text.Text = "";
                    _filter = BankFilter.None;
                    break;
                case var _ when choice.StartsWith("Shiny only:", StringComparison.Ordinal):
                    _filter = _filter with { ShinyOnly = !_filter.ShinyOnly };
                    break;
                case var _ when choice.StartsWith("Eggs:", StringComparison.Ordinal):
                    _filter = _filter with { EggOnly = !_filter.EggOnly };
                    break;
                case var _ when choice.StartsWith("Default names only:", StringComparison.Ordinal):
                    _filter = _filter with { DefaultNamedOnly = !_filter.DefaultNamedOnly };
                    break;
                case var _ when choice.StartsWith("Generation:", StringComparison.Ordinal):
                    if (!await PickGenerationAsync()) continue;
                    break;
                case var _ when choice.StartsWith("Source game:", StringComparison.Ordinal):
                    if (!await PickSourceAsync()) continue;
                    break;
                case var _ when choice.StartsWith("Type:", StringComparison.Ordinal):
                    if (!await PickTypeAsync()) continue;
                    break;
                case var _ when choice.StartsWith("Gender:", StringComparison.Ordinal):
                    if (!await PickGenderAsync()) continue;
                    break;
                case var _ when choice.StartsWith("Rarity:", StringComparison.Ordinal):
                    if (!await PickRarityAsync()) continue;
                    break;
                case var _ when choice.StartsWith("Lowest level:", StringComparison.Ordinal):
                    if (!await PickLevelAsync(lowest: true)) continue;
                    break;
                case var _ when choice.StartsWith("Highest level:", StringComparison.Ordinal):
                    if (!await PickLevelAsync(lowest: false)) continue;
                    break;
            }
            _page = 0;
            _cursor = 0;
            RefreshView();
        }
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    /// <summary>Returns false when the picker was dismissed (leave the filter alone).</summary>
    private async Task<bool> PickGenerationAsync()
    {
        var generations = _all.Select(e => e.Info.Generation).Distinct().Order().ToList();
        var options = new List<PadOption> { new("All generations") };
        options.AddRange(generations.Select(gen => new PadOption($"GEN {Roman(gen)}")));
        var choice = await PadMenu.ShowAsync(_host, "GENERATION", "Which era's storage format?", options.ToArray());
        if (choice is null) return false;
        _filter = _filter with { Generation = choice == "All generations" ? null : RomanToNumber(choice) };
        return true;
    }

    private static int? RomanToNumber(string label) => label switch
    {
        "GEN I" => 1, "GEN II" => 2, "GEN III" => 3, "GEN IV" => 4, "GEN V" => 5,
        "GEN VI" => 6, "GEN VII" => 7, "GEN VIII" => 8, "GEN IX" => 9, _ => null,
    };

    private async Task<bool> PickSourceAsync()
    {
        var sources = _all.Select(e => e.Info.SourceName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = new List<PadOption> { new("All sources") };
        options.AddRange(sources.Select(source => new PadOption(source.ToUpperInvariant())));
        var choice = await PadMenu.ShowAsync(_host, "SOURCE GAME", "Which game did they come from?", options.ToArray());
        if (choice is null) return false;
        _filter = _filter with
        {
            SourceName = choice == "All sources" ? null : sources.First(s => s.ToUpperInvariant() == choice),
        };
        return true;
    }

    private async Task<bool> PickTypeAsync()
    {
        var options = new List<PadOption> { new("All types") };
        for (var type = ParkType.Normal; type <= ParkType.Max; type++)
            options.Add(new PadOption(TypeNames[type]));
        var choice = await PadMenu.ShowAsync(_host, "TYPE", "Either of a mon's types may match.", options.ToArray());
        if (choice is null) return false;
        _filter = _filter with
        {
            TypeId = choice == "All types" ? null : Array.IndexOf(TypeNames, choice),
        };
        return true;
    }

    private async Task<bool> PickRarityAsync()
    {
        var choice = await PadMenu.ShowAsync(_host, "RARITY", null,
            new PadOption("All rarities"), new PadOption("Legendary"), new PadOption("Mythical"),
            new PadOption("Legendary or mythical"));
        if (choice is null) return false;
        _filter = _filter with
        {
            Rarity = choice switch
            {
                "Legendary" => BankRarity.Legendary,
                "Mythical" => BankRarity.Mythical,
                "Legendary or mythical" => BankRarity.LegendaryOrMythical,
                _ => BankRarity.Any,
            },
        };
        return true;
    }

    private static string GenderName(int? gender) => gender switch
    {
        0 => "MALE",
        1 => "FEMALE",
        2 => "GENDERLESS",
        _ => "ANY",
    };

    private async Task<bool> PickGenderAsync()
    {
        var choice = await PadMenu.ShowAsync(_host, "GENDER", "Read from each stored Pokémon.",
            new PadOption("Any gender"), new PadOption("Male"), new PadOption("Female"), new PadOption("Genderless"));
        if (choice is null) return false;
        _filter = _filter with
        {
            Gender = choice switch { "Male" => 0, "Female" => 1, "Genderless" => 2, _ => null },
        };
        return true;
    }

    private async Task<bool> PickLevelAsync(bool lowest)
    {
        var options = new List<PadOption> { new(lowest ? "Any lowest level" : "Any highest level") };
        options.AddRange(Enumerable.Range(1, MaxLevel).Select(level => new PadOption($"Lv.{level}")));
        var choice = await PadMenu.ShowAsync(_host, lowest ? "LOWEST LEVEL" : "HIGHEST LEVEL",
            lowest ? "Hide anything below this level." : "Hide anything above this level.", options.ToArray());
        if (choice is null) return false;
        var level = choice.StartsWith("Lv.", StringComparison.Ordinal) ? int.Parse(choice[3..]) : (int?)null;
        if (lowest) _filter = _filter with { LevelMin = level };
        else _filter = _filter with { LevelMax = level };
        return true;
    }

    private async Task ShowSortMenuAsync()
    {
        var picked = await PickSortAsync(_host, "SORT THE RESULTS", null);
        if (picked is null) return;
        (_sort, _reverse, _) = picked.Value;
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    /// <summary>The vault's sort keys, in the order the pickers offer them, each with the words
    /// for its natural direction and for the reversed one.</summary>
    private static readonly (BankSortOrder Order, string Label, string Forward, string Reversed, string Icon)[] SortKeys =
    [
        (BankSortOrder.DexNumber, "Dex number", "lowest first", "highest first", "pokedex"),
        (BankSortOrder.SpeciesName, "Species name", "A-Z", "Z-A", "alpha"),
        (BankSortOrder.Nickname, "Nickname", "A-Z", "Z-A", "rename"),
        (BankSortOrder.LevelDesc, "Level", "strongest first", "weakest first", "level"),
        (BankSortOrder.ShinyFirst, "Shiny", "shinies first", "shinies last", "shiny"),
        (BankSortOrder.Type, "Type", "Normal → Fairy", "Fairy → Normal", "type"),
        (BankSortOrder.Rarity, "Legendary / mythical", "rare first", "rare last", "rarity"),
        (BankSortOrder.Generation, "Generation", "oldest era first", "newest era first", "generation"),
        (BankSortOrder.SourceGame, "Origin game", "A-Z", "Z-A", "game"),
        (BankSortOrder.NewestAdded, "Date deposited", "newest first", "oldest first", "calendar"),
        (BankSortOrder.Gender, "Gender", "♂ ♀ genderless", "genderless ♀ ♂", "gender"),
        (BankSortOrder.Ball, "Poké Ball", "by ball number", "reversed", "bank"),
    ];

    /// <summary>
    /// Two quick picks - the key, then its direction - shared by the search results and the
    /// bank's own box/vault sort, so every sort in the vault speaks the same keys. Returns the
    /// order, whether it is reversed, and the human label for the confirmation line.
    /// </summary>
    public static async Task<(BankSortOrder Order, bool Reverse, string Label)?> PickSortAsync(
        Grid host, string title, string? note)
    {
        var key = await PadMenu.ShowAsync(host, title, note,
            SortKeys.Select(k => new PadOption(k.Label, IconPath: k.Icon)).ToArray());
        if (key is null) return null;
        var picked = SortKeys.First(k => k.Label == key);
        var direction = await PadMenu.ShowAsync(host, picked.Label.ToUpperInvariant(), "Which way round?",
            new PadOption(picked.Forward, IconPath: "sort"), new PadOption(picked.Reversed, IconPath: "reverse"));
        if (direction is null) return null;
        var reverse = direction == picked.Reversed;
        return (picked.Order, reverse, $"{picked.Label} ({(reverse ? picked.Reversed : picked.Forward)})");
    }

    private static string SortLabel(BankSortOrder sort) => sort switch
    {
        BankSortOrder.SpeciesName => "A-Z",
        BankSortOrder.LevelDesc => "LEVEL",
        BankSortOrder.ShinyFirst => "SHINY",
        BankSortOrder.NewestAdded => "DATE",
        BankSortOrder.OldestAdded => "OLD",
        BankSortOrder.Generation => "GEN",
        BankSortOrder.Nickname => "NICK",
        BankSortOrder.Type => "TYPE",
        BankSortOrder.SourceGame => "GAME",
        BankSortOrder.Rarity => "RARE",
        BankSortOrder.Gender => "GENDER",
        BankSortOrder.Ball => "BALL",
        _ => "DEX #",
    };

    // ── The result grid: the bank's own slot language ─────────────────────────

    private void RefreshCursorInfo()
    {
        if (Count == 0)
        {
            _cursorInfo.Text = _all.Count == 0 ? "THE BANK IS EMPTY" : "NO POKéMON MATCH";
            return;
        }
        var entry = EntryAt(_page * PageSize + _cursor);
        var name = SpeciesName(entry.Info.Species);
        var nickname = string.Equals(entry.Info.Nickname, name, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" “{entry.Info.Nickname}”";
        _cursorInfo.Text =
            $"#{entry.Info.Species:000} {name}{nickname} · BOX {entry.Box + 1:00} SLOT {entry.Slot + 1:00} · GEN {entry.Info.Generation} · {entry.Info.SourceName}";
    }

    /// <summary>Loads the whole visible page at once so cells fill in one pass.</summary>
    private void WarmVisible()
    {
        for (var index = 0; index < PageSize; index++)
        {
            var absolute = _page * PageSize + index;
            if (absolute >= Count) break;
            var entry = EntryAt(absolute);
            _sprites.Warm(entry.Info.Species, entry.Info.Form, entry.Info.Shiny,
                () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
        }
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var wallpaper = BoxGridRenderer.WallpaperAt(3);
        PksmPaint.Wallpaper(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), wallpaper);
        for (var index = 0; index < PageSize; index++)
        {
            var rect = BoxGridRenderer.SlotRect(args.Info, index);
            var absolute = _page * PageSize + index;
            var exists = absolute < Count;
            PksmPaint.Slot(canvas, rect, wallpaper, empty: !exists);
            if (!exists) continue;
            var entry = EntryAt(absolute);

            var bitmap = _sprites.GetSprite(entry.Info.Species, entry.Info.Form, entry.Info.Shiny);
            if (bitmap is not null)
            {
                var inset = rect.Width * 0.03f;
                var box = SKRect.Inflate(rect, -inset, -inset);
                var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                var dest = new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2);
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image, dest, BoxGridRenderer.SpriteSampling);
            }
            else
            {
                _sprites.Warm(entry.Info.Species, entry.Info.Form, entry.Info.Shiny,
                    () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
            }
            if (entry.Info.Shiny)
                BoxGridRenderer.DrawSparkle(canvas, rect.Right - rect.Width * 0.14f, rect.Top + rect.Height * 0.16f,
                    Math.Min(rect.Width, rect.Height) * 0.09f, BoxGridRenderer.SparklePaint);
            if (index == _cursor)
                PksmPaint.Selection(canvas, rect);
        }
    }

    // ── Interaction ─────────────────────────────────────────────────────────────

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

    private void Jump()
    {
        if (Count == 0) return;
        var entry = EntryAt(_page * PageSize + _cursor);
        Close();
        _jump(entry);
    }

    /// <summary>Hands every result to the bank as its marked selection and closes.</summary>
    private void MarkAllResults()
    {
        if (_markAll is null || Count == 0) return;
        var results = _view.ToList();
        Close();
        _markAll(results);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;
        var slot = BoxGridRenderer.SlotFromTouch(_canvas.CanvasSize, args.Location);
        if (slot < 0 || _page * PageSize + slot >= Count) return;
        _cursor = slot;
        RefreshCursorInfo();
        _canvas.InvalidateSurface();
        Jump(); // a tap on a hit IS the selection
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
            case PadButton.A: Jump(); return true;
            case PadButton.B: Close(); return true;
            case PadButton.X: _ = ShowFilterMenuAsync(); return true;
            case PadButton.Y: ToggleShinyOnly(); return true;
            case PadButton.Start: MarkAllResults(); return true;
            default: return true; // the search owns the pad while open
        }
    }

    private void Close()
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(true);
    }
}
