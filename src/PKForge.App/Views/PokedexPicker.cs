using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The floating Pokédex: every species as a sprite tile on a cozy box-style grid,
/// with live search, type filter gems and generation chips. This is how a Pokémon
/// is picked anywhere in the app - never a plain list.
/// </summary>
public sealed class PokedexPicker : IPadHandler
{
    private static readonly string[] TypeNames =
    [
        "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
        "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark", "Fairy",
    ];

    private static readonly (int Gen, int First, int Last)[] GenRanges =
        [(1, 1, 151), (2, 152, 251), (3, 252, 386), (4, 387, 493), (5, 494, 649), (6, 650, 721), (7, 722, 809), (8, 810, 905), (9, 906, 1025)];

    private sealed class DexEntry(int id, string name, string? iconPath, IReadOnlyList<int> types, int gen) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string? IconPath { get; } = iconPath;
        public IReadOnlyList<int> Types { get; } = types;
        public int Gen { get; } = gen;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly TaskCompletionSource<PickItem?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DexEntry> _all;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly CollectionView _grid;
    private List<DexEntry> _filtered;
    private string _query = "";
    private readonly HashSet<int> _typeFilters = [];
    private int? _genFilter;
    private int _index;
    private DexEntry? _selectedEntry;
    private readonly SecondScreenState? _state;
    private readonly List<Border> _typeChips = [];
    private readonly List<Border> _genChips = [];

    public static async Task<PickItem?> ShowAsync(Grid host, IGameDataService data, ISaveEngineSession session)
    {
        await EnsureIconsAsync(host, data);
        return await new PokedexPicker(host, data, session)._result.Task;
    }

    /// <summary>First run copies all bundled mini sprites to cache behind the cute loader.</summary>
    private static async Task EnsureIconsAsync(Grid host, IGameDataService data)
    {
        var missing = new List<int>();
        for (var id = 1; id < data.SpeciesNames.Count; id++)
        {
            if (data.SpeciesNames[id].Length > 0 && !File.Exists(IconCachePath(id)))
                missing.Add(id);
        }
        if (missing.Count < 30) return; // negligible: let them fill lazily

        var overlay = LoadingOverlay.Show(host, "OPENING THE POKéDEX…", "Preparing the sprite index (one time only).");
        try
        {
            var done = 0;
            foreach (var chunk in missing.Chunk(64))
            {
                await Task.WhenAll(chunk.Select(async id =>
                {
                    try
                    {
                        var target = IconCachePath(id);
                        Stream asset;
                        try
                        {
                            asset = await FileSystem.OpenAppPackageFileAsync($"sprites/b_{id}.png");
                        }
                        catch (FileNotFoundException)
                        {
                            // Gen 9 has no pixel sprites; PKHeX shows official artwork.
                            asset = await FileSystem.OpenAppPackageFileAsync($"artwork/a_{id}.png");
                        }
                        await using (asset)
                            NormalizeIcon(asset, target);
                    }
                    catch { /* no bundled sprite for this id */ }
                }));
                done += chunk.Length;
                overlay.Report(done, missing.Count);
                if (overlay.Cancellation.IsCancellationRequested) return;
            }
        }
        finally
        {
            overlay.Close();
        }
    }

    private static string IconCachePath(int species) =>
        System.IO.Path.Combine(FileSystem.CacheDirectory, $"mini-v2-{species}.png");

    /// <summary>
    /// Crops transparent padding, scales every species to one visual footprint, then
    /// anchors it to a shared baseline. Evolution families no longer jump in apparent size.
    /// </summary>
    private static void NormalizeIcon(Stream source, string target)
    {
        using var bitmap = SKBitmap.Decode(source);
        if (bitmap is null) return;

        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha <= 8) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top) return;

        const int canvasWidth = 72;
        const int canvasHeight = 58;
        const float visualWidth = 54;
        const float visualHeight = 44;
        var sourceRect = new SKRect(left, top, right + 1, bottom + 1);
        var scale = Math.Min(visualWidth / sourceRect.Width, visualHeight / sourceRect.Height);
        var width = MathF.Max(1, MathF.Round(sourceRect.Width * scale));
        var height = MathF.Max(1, MathF.Round(sourceRect.Height * scale));
        var xOffset = MathF.Round((canvasWidth - width) / 2f);
        var baseline = canvasHeight - 5f;
        var destination = new SKRect(xOffset, baseline - height, xOffset + width, baseline);

        using var normalized = new SKBitmap(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.Clear(SKColors.Transparent);
            using var sourceImage = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(sourceImage, sourceRect, destination,
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        using var image = SKImage.FromBitmap(normalized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(target);
        encoded.SaveTo(output);
    }

    private PokedexPicker(Grid host, IGameDataService data, ISaveEngineSession session)
    {
        _host = host;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();

        _all = new List<DexEntry>(data.SpeciesNames.Count);
        for (var id = 1; id < data.SpeciesNames.Count; id++)
        {
            // Only what this save format can hold: a Gen 7 game never gains Gen 8+
            // species, whatever the cartridge mod advertises. HaX mode shows the
            // full national list and generation carries the warning.
            if (!Services.HaXMode.IsOn && id > session.MaxSpeciesId) break;
            if (data.SpeciesNames[id].Length == 0) continue;
            var icon = IconCachePath(id);
            var genIndex = Array.FindIndex(GenRanges, r => id >= r.First && id <= r.Last);
            _all.Add(new DexEntry(id, data.SpeciesNames[id], File.Exists(icon) ? icon : null,
                session.GetSpeciesTypes(id), genIndex >= 0 ? GenRanges[genIndex].Gen : 9));
        }
        _filtered = ApplyFilters();

        var search = new Entry
        {
            Placeholder = "Search a Pokémon…",
            FontSize = 14,
            TextColor = UiTokens.Ink0,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        search.TextChanged += (_, args) => { _query = args.NewTextValue ?? ""; DebounceRefilter(); };

        _grid = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemsLayout = new GridItemsLayout(6, ItemsLayoutOrientation.Vertical) { VerticalItemSpacing = 6, HorizontalItemSpacing = 6 },
            ItemTemplate = new DataTemplate(BuildCell),
            ItemsSource = _filtered,
        };
        var content = new Grid
        {
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
        };
        var title = new Label { Text = "POKéDEX", TextColor = UiTokens.Ink0, FontSize = 15, FontAttributes = FontAttributes.Bold, CharacterSpacing = 2 };
        content.Add(title);
        content.Add(search); Grid.SetRow(search, 1);
        var filters = BuildFilterRows();
        content.Add(filters); Grid.SetRow(filters, 2);
        content.Add(_grid); Grid.SetRow(_grid, 3);
        var hints = Kit.HintBar(
            ("Ⓐ", "CHOOSE", null),
            ("Ⓑ", "CANCEL", () => Close(null)),
            ("Ⓛ Ⓡ", "GEN", null),
            ("Ⓧ", "TYPES", () => _ = ShowTypeFilterMenuAsync()),
            ("Ⓨ", "CLEAR", ClearFilters));
        content.Add(hints); Grid.SetRow(hints, 4);

        var window = Kit.DevicePanel(content, padding: 12);
        window.Margin = new Thickness(30, 16);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => Close(null);
        scrim.GestureRecognizers.Add(scrimTap);

        _overlay = new Grid { Children = { scrim, window } };
        host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        Highlight(0);
        _router?.Push(this);
    }

    private static readonly string[] RomanGens = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];

    /// <summary>Toggles one type in the multi-type filter (a mon must match ALL selected types).</summary>
    private void ToggleType(int type)
    {
        if (!_typeFilters.Remove(type))
            _typeFilters.Add(type);
        RefreshChipStates();
        Refilter();
    }

    private void CycleGen(int direction)
    {
        var current = _genFilter ?? 0; // 0 = ALL
        current = (current + direction + 10) % 10;
        _genFilter = current == 0 ? null : current;
        RefreshChipStates();
        Refilter();
    }

    private void ClearFilters()
    {
        _typeFilters.Clear();
        _genFilter = null;
        RefreshChipStates();
        Refilter();
    }

    private void RefreshChipStates()
    {
        for (var i = 0; i < _typeChips.Count; i++)
            _typeChips[i].Opacity = _typeFilters.Count == 0 ? 0.55 : (_typeFilters.Contains(i) ? 1.0 : 0.3);
        for (var i = 0; i < _genChips.Count; i++)
            _genChips[i].Opacity = _genFilter is null ? 0.55 : (_genFilter == GenRanges[i].Gen ? 1.0 : 0.3);
    }

    /// <summary>Type gems (multi-select) + roman generation chips + CLEAR; all also pad-reachable.</summary>
    private View BuildFilterRows()
    {
        var typeRow = new HorizontalStackLayout { Spacing = 5 };
        for (var type = 0; type < TypeNames.Length; type++)
        {
            var captured = type;
            var chip = new Border
            {
                BackgroundColor = TypePalette.ForType(type),
                StrokeThickness = 0,
                Opacity = 0.55,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(8, 3),
                Content = new Label { Text = TypeNames[type].ToUpperInvariant(), TextColor = TypePalette.ForegroundForType(type), FontSize = 9, FontAttributes = FontAttributes.Bold },
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleType(captured);
            chip.GestureRecognizers.Add(tap);
            _typeChips.Add(chip);
            typeRow.Children.Add(chip);
        }

        var genRow = new HorizontalStackLayout { Spacing = 5 };
        foreach (var (gen, _, _) in GenRanges)
        {
            var captured = gen;
            var chip = new Border
            {
                BackgroundColor = Kit.EraColor(gen),
                StrokeThickness = 0,
                Opacity = 0.55,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(10, 3),
                Content = new Label { Text = $"GEN {RomanGens[gen - 1]}", TextColor = Colors.White, FontSize = 9, FontAttributes = FontAttributes.Bold },
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _genFilter = _genFilter == captured ? null : captured;
                RefreshChipStates();
                Refilter();
            };
            chip.GestureRecognizers.Add(tap);
            _genChips.Add(chip);
            genRow.Children.Add(chip);
        }

        var clear = new Border
        {
            BackgroundColor = UiTokens.MenuBlue,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(10, 3),
            Content = new Label { Text = "CLEAR", TextColor = Colors.White, FontSize = 9, FontAttributes = FontAttributes.Bold },
        };
        var clearTap = new TapGestureRecognizer();
        clearTap.Tapped += (_, _) => ClearFilters();
        clear.GestureRecognizers.Add(clearTap);
        genRow.Children.Add(clear);

        return new VerticalStackLayout
        {
            Spacing = 5,
            Children =
            {
                new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = typeRow },
                genRow,
            },
        };
    }

    /// <summary>A compact logo-deck tile: normalized sprite, strong name plate, cyan focus.</summary>
    private View BuildCell()
    {
        var icon = new Image
        {
            HeightRequest = 48,
            WidthRequest = 62,
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        icon.SetBinding(Image.SourceProperty, nameof(DexEntry.IconPath));

        var name = new Label
        {
            TextColor = UiTokens.Ink0,
            FontFamily = DsChrome.PixelFont,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
        };
        name.SetBinding(Label.TextProperty, nameof(DexEntry.Name));

        var number = new Label
        {
            TextColor = UiTokens.InkSoft,
            FontFamily = DsChrome.PixelFont,
            FontSize = 8,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        number.SetBinding(Label.TextProperty, new Binding(nameof(DexEntry.Id), stringFormat: "No.{0:000}"));

        var namePlate = new Border
        {
            BackgroundColor = UiTokens.MaroonDeep,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            Padding = new Thickness(3, 1),
            Content = new VerticalStackLayout { Spacing = 0, Children = { name, number } },
        };

        var cell = new Border
        {
            HeightRequest = 78,
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1.5,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            Padding = new Thickness(4, 3),
            Content = new Grid
            {
                RowSpacing = 2,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { icon, namePlate },
            },
        };
        Grid.SetRow(namePlate, 1);
        cell.Triggers.Add(new DataTrigger(typeof(Border))
        {
            Binding = new Binding(nameof(DexEntry.IsSelected)),
            Value = true,
            Setters =
            {
                new Setter { Property = Border.StrokeProperty, Value = UiTokens.SelectBorder },
                new Setter { Property = Border.StrokeThicknessProperty, Value = 4.0 },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = UiTokens.SelectFill },
            },
        });
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (cell.BindingContext is DexEntry entry)
                Close(new PickItem(entry.Id, entry.Name, entry.IconPath));
        };
        cell.GestureRecognizers.Add(tap);
        return cell;
    }

    private List<DexEntry> ApplyFilters()
    {
        IEnumerable<DexEntry> source = _all;
        if (_genFilter is { } gen)
        {
            var (_, first, last) = GenRanges.First(r => r.Gen == gen);
            source = source.Where(e => e.Id >= first && e.Id <= last);
        }
        if (_typeFilters.Count > 0)
            source = source.Where(e => _typeFilters.All(t => e.Types.Contains(t)));
        if (!string.IsNullOrWhiteSpace(_query))
            source = source.Where(e => e.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
        return source.ToList();
    }

    private CancellationTokenSource? _searchDebounce;

    /// <summary>
    /// Coalesce rapid keystrokes: rebuilding the 1025-cell grid on every character was the
    /// "typing in the dex feels laggy" cost. Refilter once typing settles (~180 ms).
    /// </summary>
    private void DebounceRefilter()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        Task.Delay(180, cts.Token).ContinueWith(task =>
        {
            if (task.IsCanceled) return;
            MainThread.BeginInvokeOnMainThread(() => { if (!cts.IsCancellationRequested) Refilter(); });
        }, TaskScheduler.Default);
    }

    private void Refilter()
    {
        if (_selectedEntry is not null) _selectedEntry.IsSelected = false;
        _selectedEntry = null;
        _filtered = ApplyFilters();
        _index = 0;
        _grid.ItemsSource = _filtered;
        Highlight(0);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: Highlight(_index - 1); return true;
            case PadButton.Right: Highlight(_index + 1); return true;
            case PadButton.Up: Highlight(_index - 6); return true;
            case PadButton.Down: Highlight(_index + 6); return true;
            case PadButton.A:
                if (_index >= 0 && _index < _filtered.Count)
                {
                    var entry = _filtered[_index];
                    Close(new PickItem(entry.Id, entry.Name, entry.IconPath));
                }
                return true;
            case PadButton.B: Close(null); return true;
            case PadButton.L: CycleGen(-1); return true;
            case PadButton.R: CycleGen(1); return true;
            case PadButton.X: _ = ShowTypeFilterMenuAsync(); return true;
            case PadButton.Y: ClearFilters(); return true;
            default: return true; // the dex owns the pad while open
        }
    }

    private void Highlight(int index)
    {
        if (_filtered.Count == 0)
        {
            if (_selectedEntry is not null) _selectedEntry.IsSelected = false;
            _selectedEntry = null;
            if (_state is not null) _state.PreviewSpecies = null;
            return;
        }
        _index = Math.Clamp(index, 0, _filtered.Count - 1);
        if (_selectedEntry is not null) _selectedEntry.IsSelected = false;
        _selectedEntry = _filtered[_index];
        _selectedEntry.IsSelected = true;
        _grid.ScrollTo(_index, position: ScrollToPosition.Center, animate: false);
        if (_state is not null) _state.PreviewSpecies = _filtered[_index].Id;
    }

    /// <summary>Pad path for the multi-type filter: a menu of types with check marks; pick to toggle.</summary>
    private async Task ShowTypeFilterMenuAsync()
    {
        var options = Enumerable.Range(0, TypeNames.Length)
            .Select(t => (_typeFilters.Contains(t) ? "✓ " : "") + TypeNames[t])
            .Append("Clear all filters")
            .ToArray();
        var choice = await PadMenu.ShowAsync(_host, "FILTER BY TYPE (toggle)", "A Pokémon must match every checked type.", options);
        if (choice is null) return;
        if (choice == "Clear all filters") { ClearFilters(); return; }
        var name = choice.StartsWith("✓ ", StringComparison.Ordinal) ? choice[2..] : choice;
        var type = Array.IndexOf(TypeNames, name);
        if (type >= 0) ToggleType(type);
    }

    private void Close(PickItem? result)
    {
        if (_state is not null) _state.PreviewSpecies = null;
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
