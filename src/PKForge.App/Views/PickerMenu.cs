using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>An entry in a <see cref="PickerMenu"/>: id, display name, optional icon file,
/// optional detail under the name (a nature's "+Atk −SpA", a move's numbers, an item's
/// effect). The init-only extras draw the shared <see cref="InfoKit"/> pieces on the row:
/// a type badge, the move category icon, a right-hand tag ("Lv 32", "HIDDEN") and a muted
/// name for entries outside the legal set.</summary>
public sealed record PickItem(int Id, string Name, string? IconPath = null, string? Detail = null)
{
    public int? TypeId { get; init; }
    public MoveCategory? Category { get; init; }
    public string? Tag { get; init; }
    public Color? TagColor { get; init; }
    public bool Muted { get; init; }
    /// <summary>Search also matches this text (a move's type, an ability's slot).</summary>
    public string? Keywords { get; init; }
}

/// <summary>
/// A two-state list filter with its own chip and the Y button ("Legal only" / "Show all").
/// <paramref name="Keep"/> decides which items the filtered state shows.
/// </summary>
public sealed record PickerFilter(string OnLabel, string OffLabel, Func<PickItem, bool> Keep, bool StartOn = true);

/// <summary>
/// A live panel under a picker's list that follows the highlighted row (the nature
/// picker's stat preview). With a panel, a tap highlights instead of picking so touch
/// users see the preview too; tapping the highlighted row again (or A) picks it.
/// </summary>
public sealed record PickerPreview(View Panel, Action<PickItem?> OnHighlight);

/// <summary>
/// The searchable choice window for big lists (species, moves, items…).
/// Touch: type in the search box, tap a row. Pad: d-pad moves the gold highlight,
/// A chooses, B cancels. Owns the gamepad while open.
/// </summary>
public sealed class PickerMenu : IPadHandler
{
    // Show enough that a gamepad user (no touch keyboard) can d-pad to any entry; the
    // CollectionView virtualizes, so a larger cap is cheap.
    private const int MaxVisible = 1200;

    private readonly TaskCompletionSource<PickItem?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<PickItem> _all;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly CollectionView _list;
    private readonly Label _preview;
    private readonly PickerPreview? _livePreview;
    private readonly PickerFilter? _filter;
    private readonly Label? _filterLabel;
    private bool _filterOn;
    private string _query = "";
    private List<PickItem> _filtered;
    private int _index;

    public static Task<PickItem?> ShowAsync(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId = null,
        PickerPreview? preview = null, PickerFilter? filter = null) =>
        new PickerMenu(host, title, items, currentId, preview, filter)._result.Task;

    private PickerMenu(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId, PickerPreview? livePreview,
        PickerFilter? filter)
    {
        _host = host;
        _all = items;
        _livePreview = livePreview;
        _filter = filter;
        // The current value always stays reachable: a filter that would hide it starts off.
        _filterOn = filter is not null && filter.StartOn
            && (currentId is not { } cur || items.FirstOrDefault(x => x.Id == cur) is not { } held || filter.Keep(held));
        _filtered = Filter("");
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        var search = new Entry
        {
            Placeholder = "Search…",
            FontSize = 14,
            TextColor = UiTokens.Ink0,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
        };
        search.TextChanged += (_, args) =>
        {
            _query = args.NewTextValue ?? "";
            Refilter(keepId: null);
        };

        // The filter chip rides beside the search box; Y toggles it on the pad.
        View searchRow = search;
        if (filter is not null)
        {
            _filterLabel = new Label
            {
                FontFamily = DsChrome.PixelFont, FontSize = 11, FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.NoWrap,
            };
            var chip = new Border
            {
                StrokeThickness = 1.5,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(10, 0),
                Content = _filterLabel,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleFilter();
            chip.GestureRecognizers.Add(tap);
            _filterChip = chip;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Children = { search, chip } };
            Grid.SetColumn(chip, 1);
            searchRow = row;
            PaintFilterChip();
        }

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _filtered,
            ItemTemplate = new DataTemplate(() => BuildRow(livePreview is null ? null : this)),
        };
        _list.SelectionChanged += (_, args) =>
        {
            if (_padSelecting) { _padSelecting = false; UpdatePreview(); return; } // pad only moves the highlight
            if (_livePreview is not null) return; // preview rows own their taps (OnRowTapped)
            if (args.CurrentSelection.FirstOrDefault() is PickItem picked)
                Close(picked);
        };

        // The list is the Star row so it fills the host-capped window and scrolls itself -
        // never a fixed 340 that pushed the title and hint bar off a 360dp screen.
        _preview = new Label
        {
            TextColor = UiTokens.Ink0,
            FontFamily = DsChrome.PixelFont,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var hints = filter is null
            ? Kit.HintBar(("A", "PICK", livePreview is null ? null : PickHighlighted), ("B", "CANCEL", () => Close(null)))
            : Kit.HintBar(("A", "PICK", livePreview is null ? null : PickHighlighted), ("Y", "FILTER", ToggleFilter), ("B", "CANCEL", () => Close(null)));
        var previewBar = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            Padding = new Thickness(10, 2),
            Content = _preview,
        };
        // The selected-item preview used to be layered over the controller hints in a
        // single grid cell. Keep both useful pad affordances, but give each its own row.
        var hintRow = new VerticalStackLayout
        {
            Spacing = 4,
            Padding = new Thickness(0, 3, 0, 0),
            Children = { previewBar, hints },
        };
        if (livePreview is not null) hintRow.Children.Insert(0, livePreview.Panel);

        var content = new Grid
        {
            RowSpacing = 10,
            VerticalOptions = LayoutOptions.Fill,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children =
            {
                Kit.HeaderBar(title),
                searchRow,
                _list,
                hintRow,
            },
        };
        Grid.SetRow(searchRow, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow(hintRow, 3);

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 520, scroll: false);
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));
        search.Unfocus(); // the pad drives first; touch users tap the box to type

        if (currentId is { } id)
        {
            var current = _filtered.FindIndex(x => x.Id == id);
            if (current >= 0) _index = current;
        }
        HighlightCurrent();
        _router?.Push(this);
    }

    private static View BuildRow(PickerMenu? tapOwner)
    {
        var icon = new Image { WidthRequest = 26, HeightRequest = 26, IsVisible = false, VerticalOptions = LayoutOptions.Center };
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(PickItem.IconPath)));
        icon.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(PickItem.IconPath), converter: NotNull));

        var name = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(PickItem.Name));
        // Two lines at most: enough for an item or ability effect, still a scannable list.
        var detail = new Label
        {
            TextColor = UiTokens.InkSoft, FontSize = 11, IsVisible = false, MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation, VerticalTextAlignment = TextAlignment.Center,
        };
        detail.SetBinding(Label.TextProperty, nameof(PickItem.Detail));
        detail.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(PickItem.Detail), converter: NotNull));
        var text = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { name, detail } };

        // Badge column: the type pill over the category icon, both hidden when unused.
        var badge = InfoKit.TypeBadge(null, 58);
        var category = new InfoKit.CategoryIcon { HorizontalOptions = LayoutOptions.Center };
        var badges = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { badge, category } };
        var tag = InfoKit.Tag();

        var row = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(10, 7),
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { icon, badges, text, tag },
        };
        Grid.SetColumn(badges, 1);
        Grid.SetColumn(text, 2);
        Grid.SetColumn(tag, 3);
        row.BindingContextChanged += (_, _) =>
        {
            var item = row.BindingContext as PickItem;
            InfoKit.SetType(badge, item?.TypeId);
            category.Category = item?.Category;
            badges.IsVisible = badge.IsVisible || category.IsVisible;
            InfoKit.SetTag(tag, item?.Tag, item?.TagColor);
            name.TextColor = item?.Muted == true ? UiTokens.InkSoft : UiTokens.Ink0;
            row.Opacity = item?.Muted == true ? 0.72 : 1;
        };

        var cell = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            StrokeThickness = 1.5,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = row,
        };
        if (tapOwner is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { if (cell.BindingContext is PickItem item) tapOwner.OnRowTapped(item); };
            cell.GestureRecognizers.Add(tap);
        }
        VisualStateManager.SetVisualStateGroups(cell, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState { Name = "Normal", Setters = { new Setter { Property = Border.StrokeProperty, Value = Colors.Transparent } } },
                    new VisualState { Name = "Selected", Setters = {
                        new Setter { Property = Border.StrokeProperty, Value = UiTokens.SelectBorder },
                        new Setter { Property = Border.StrokeThicknessProperty, Value = 3 },
                        new Setter { Property = Border.BackgroundColorProperty, Value = UiTokens.SelectFill } } },
                },
            },
        });
        return cell;
    }

    private static readonly IValueConverter NotNull = new NotNullConverter();

    private sealed class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is not null;
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private List<PickItem> Filter(string query)
    {
        IEnumerable<PickItem> source = _all;
        if (_filterOn && _filter is not null) source = source.Where(_filter.Keep);
        if (!string.IsNullOrWhiteSpace(query))
            source = source.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                       || (x.Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        return source.Take(MaxVisible).ToList();
    }

    private Border? _filterChip;

    private void ToggleFilter()
    {
        if (_filter is null) return;
        var keep = _index >= 0 && _index < _filtered.Count ? _filtered[_index].Id : (int?)null;
        _filterOn = !_filterOn;
        PaintFilterChip();
        Refilter(keep);
    }

    private void PaintFilterChip()
    {
        if (_filter is null || _filterChip is null || _filterLabel is null) return;
        _filterLabel.Text = _filterOn ? $"✓ {_filter.OnLabel}" : _filter.OffLabel;
        _filterLabel.TextColor = _filterOn ? UiTokens.SelectInk : UiTokens.Ink1;
        _filterChip.BackgroundColor = _filterOn ? UiTokens.SelectFill : UiTokens.ShellPress;
        _filterChip.Stroke = _filterOn ? UiTokens.SelectBorder : UiTokens.ShellEdge;
    }

    /// <summary>Rebuilds the visible list, keeping the highlight on <paramref name="keepId"/> when it survives.</summary>
    private void Refilter(int? keepId)
    {
        _filtered = Filter(_query);
        _index = keepId is { } id ? Math.Max(0, _filtered.FindIndex(x => x.Id == id)) : 0;
        _list.ItemsSource = _filtered;
        HighlightCurrent();
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Move(-1); return true;
            case PadButton.Down: Move(1); return true;
            case PadButton.A: PickHighlighted(); return true;
            case PadButton.B: Close(null); return true;
            case PadButton.Y: ToggleFilter(); return true;
            default: return true; // the picker owns the pad while open
        }
    }

    private void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, _filtered.Count - 1);
        HighlightCurrent();
    }

    private bool _padSelecting;
    private bool _armed; // preview mode: the highlighted row was aimed by a tap

    /// <summary>Preview mode: the first tap aims (the panel follows), a second tap on the same row picks.</summary>
    private void OnRowTapped(PickItem item)
    {
        var tapped = _filtered.IndexOf(item);
        if (tapped < 0) return;
        if (tapped == _index && _armed) { Close(item); return; }
        _index = tapped;
        HighlightCurrent(scroll: false);
        _armed = true;
    }

    private void PickHighlighted()
    {
        if (_index >= 0 && _index < _filtered.Count) Close(_filtered[_index]);
    }

    private void HighlightCurrent(bool scroll = true)
    {
        if (_filtered.Count == 0) return;
        _index = Math.Clamp(_index, 0, _filtered.Count - 1);
        _padSelecting = true;
        _armed = false;
        _list.SelectedItem = _filtered[_index];
        if (scroll) _list.ScrollTo(_index, position: ScrollToPosition.Center, animate: false);
        UpdatePreview();
    }

    /// <summary>The hint line always says what A will pick, so pad aim can be misread but never mispurchased.</summary>
    private void UpdatePreview()
    {
        var current = _index >= 0 && _index < _filtered.Count ? _filtered[_index] : null;
        if (current is not null)
            _preview.Text = _livePreview is null ? $"A picks: {current.Name}" : $"A or tap again picks: {current.Name}";
        _livePreview?.OnHighlight(current);
    }

    private void Close(PickItem? result)
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
