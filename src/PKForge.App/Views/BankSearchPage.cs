using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Whole-bank search: one overlay above the vault. Text (species name or nickname),
/// shiny-only, generation and source-game filters narrow every entry at once; five sorts
/// reorder it; the result grid reuses the box-slot renderer so a hit looks exactly like
/// its slot does in the bank. A/tap jumps the bank view to the mon's box and slot.
/// </summary>
public sealed class BankSearchPage : IPadHandler
{
    private const int Columns = BoxGridRenderer.Columns;
    private const int Rows = BoxGridRenderer.Rows;
    private const int PageSize = Columns * Rows;

    private enum SortMode { DexNumber, SpeciesName, Generation, NewestFirst, OldestFirst }

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly Action<BankEntry> _jump;
    private readonly List<BankEntry> _all;
    private readonly List<BankEntry> _view = [];
    private readonly SKCanvasView _canvas;
    private readonly Entry _text;
    private readonly Label _count;
    private readonly Label _cursorInfo;
    private readonly HorizontalStackLayout _chips;

    private string _query = "";
    private bool _shinyOnly;
    private int? _generation;
    private string? _source;
    private SortMode _sort = SortMode.DexNumber;
    private int _page;
    private int _cursor;

    public static Task ShowAsync(Grid host, IBankService bank, IGameDataService data, ISpriteService sprites, Action<BankEntry> jump) =>
        new BankSearchPage(host, bank, data, sprites, jump)._result.Task;

    private BankSearchPage(Grid host, IBankService bank, IGameDataService data, ISpriteService sprites, Action<BankEntry> jump)
    {
        _host = host;
        _data = data;
        _sprites = sprites;
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
            _query = args.NewTextValue ?? "";
            _page = 0;
            _cursor = 0;
            RefreshView();
        };

        _chips = new HorizontalStackLayout { Spacing = 5 };

        _canvas = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        View hints = Kit.HintBar(
            ("A", "Jump", null),
            ("B", "Done", () => Close()),
            ("LR", "Page", null),
            ("X", "Sort", () => _ = ShowSortMenuAsync()),
            ("Y", "Shiny", ToggleShinyOnly));

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

    private string SpeciesName(int species) =>
        species >= 0 && species < _data.SpeciesNames.Count ? _data.SpeciesNames[species] : "";

    private bool MatchesText(BankEntry entry, string query) =>
        entry.Info.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase)
        || SpeciesName(entry.Info.Species).Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string Roman(int gen) => gen switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => $"{gen}",
    };

    private static string SortLabel(SortMode sort) => sort switch
    {
        SortMode.SpeciesName => "A-Z",
        SortMode.Generation => "GEN",
        SortMode.NewestFirst => "NEW",
        SortMode.OldestFirst => "OLD",
        _ => "DEX #",
    };

    private void RefreshView()
    {
        var query = _query.Trim();
        IEnumerable<BankEntry> view = _all;
        if (query.Length > 0) view = view.Where(e => MatchesText(e, query));
        if (_shinyOnly) view = view.Where(e => e.Info.Shiny);
        if (_generation is { } generation) view = view.Where(e => e.Info.Generation == generation);
        if (_source is { } source)
            view = view.Where(e => string.Equals(e.Info.SourceName, source, StringComparison.OrdinalIgnoreCase));
        _view.Clear();
        _view.AddRange(_sort switch
        {
            SortMode.SpeciesName => view
                .OrderBy(e => SpeciesName(e.Info.Species), StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Info.Species),
            SortMode.Generation => view.OrderBy(e => e.Info.Generation).ThenBy(e => e.Info.Species),
            SortMode.NewestFirst => view.OrderByDescending(e => e.AddedUtc),
            SortMode.OldestFirst => view.OrderBy(e => e.AddedUtc),
            _ => view.OrderBy(e => e.Info.Species).ThenBy(e => e.AddedUtc),
        });
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
        AddChip($"GEN: {(_generation is { } gen ? Roman(gen) : "ALL")}", active: _generation is not null,
            () => _ = ShowGenerationMenuAsync());
        AddChip($"SRC: {_source ?? "ALL"}", active: _source is not null, () => _ = ShowSourceMenuAsync());
        AddChip("SHINY", active: _shinyOnly, ToggleShinyOnly);
        AddChip($"SORT: {SortLabel(_sort)}", active: _sort != SortMode.DexNumber, () => _ = ShowSortMenuAsync());
        if (FiltersActive)
            AddChip("RESET", active: true, ResetFilters);
    }

    private bool FiltersActive =>
        _query.Trim().Length > 0 || _shinyOnly || _generation is not null || _source is not null || _sort != SortMode.DexNumber;

    private void ResetFilters()
    {
        _query = "";
        _text.Text = "";
        _shinyOnly = false;
        _generation = null;
        _source = null;
        _sort = SortMode.DexNumber;
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    private void ToggleShinyOnly()
    {
        _shinyOnly = !_shinyOnly;
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

    private async Task ShowGenerationMenuAsync()
    {
        var generations = _all.Select(e => e.Info.Generation).Distinct().Order().ToList();
        var options = new List<PadOption> { new("All generations") };
        options.AddRange(generations.Select(gen => new PadOption($"GEN {Roman(gen)}")));
        var choice = await PadMenu.ShowAsync(_host, "GENERATION", null, options.ToArray());
        if (choice is null) return;
        _generation = choice == "All generations" ? null : RomanToNumber(choice) ?? _generation;
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    private static int? RomanToNumber(string label) => label switch
    {
        "GEN I" => 1, "GEN II" => 2, "GEN III" => 3, "GEN IV" => 4, "GEN V" => 5,
        "GEN VI" => 6, "GEN VII" => 7, "GEN VIII" => 8, "GEN IX" => 9, _ => null,
    };

    private async Task ShowSourceMenuAsync()
    {
        var sources = _all.Select(e => e.Info.SourceName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = new List<PadOption> { new("All sources") };
        options.AddRange(sources.Select(source => new PadOption(source.ToUpperInvariant())));
        var choice = await PadMenu.ShowAsync(_host, "SOURCE GAME", null, options.ToArray());
        if (choice is null) return;
        _source = choice == "All sources" ? null : sources.First(s => s.ToUpperInvariant() == choice);
        _page = 0;
        _cursor = 0;
        RefreshView();
    }

    private async Task ShowSortMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_host, "SORT BY", null,
            new PadOption("Dex #"),
            new PadOption("Species name"),
            new PadOption("Generation"),
            new PadOption("Newest first"),
            new PadOption("Oldest first"));
        if (choice is null) return;
        _sort = choice switch
        {
            "Species name" => SortMode.SpeciesName,
            "Generation" => SortMode.Generation,
            "Newest first" => SortMode.NewestFirst,
            "Oldest first" => SortMode.OldestFirst,
            _ => SortMode.DexNumber,
        };
        RefreshView();
    }

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
            case PadButton.X: _ = ShowSortMenuAsync(); return true;
            case PadButton.Y: ToggleShinyOnly(); return true;
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
