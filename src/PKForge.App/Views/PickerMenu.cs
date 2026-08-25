using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>An entry in a <see cref="PickerMenu"/>: id, display name, optional icon file.</summary>
public sealed record PickItem(int Id, string Name, string? IconPath = null);

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
    private List<PickItem> _filtered;
    private int _index;

    public static Task<PickItem?> ShowAsync(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId = null) =>
        new PickerMenu(host, title, items, currentId)._result.Task;

    private PickerMenu(Grid host, string title, IReadOnlyList<PickItem> items, int? currentId)
    {
        _host = host;
        _all = items;
        _filtered = Filter("");
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        var search = new Entry
        {
            Placeholder = "Search…",
            FontSize = 14,
            TextColor = UiTokens.Paper,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
        };
        search.TextChanged += (_, args) =>
        {
            _filtered = Filter(args.NewTextValue ?? "");
            _index = 0;
            _list!.ItemsSource = _filtered;
            HighlightCurrent();
        };

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _filtered,
            ItemTemplate = new DataTemplate(BuildRow),
        };
        _list.SelectionChanged += (_, args) =>
        {
            if (_padSelecting) { _padSelecting = false; return; } // pad only moves the highlight
            if (args.CurrentSelection.FirstOrDefault() is PickItem picked)
                Close(picked);
        };

        // The list is the Star row so it fills the host-capped window and scrolls itself -
        // never a fixed 340 that pushed the title and hint bar off a 360dp screen.
        var content = new Grid
        {
            RowSpacing = 10,
            VerticalOptions = LayoutOptions.Fill,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children =
            {
                Kit.HeaderBar(title),
                search,
                _list,
                Kit.HintBar(("A", "CHOOSE", null), ("B", "CANCEL", () => Close(null))),
            },
        };
        Grid.SetRow(search, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow((View)content.Children[3], 3);

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 520, scroll: false);
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));

        if (currentId is { } id)
        {
            var current = _filtered.FindIndex(x => x.Id == id);
            if (current >= 0) _index = current;
        }
        HighlightCurrent();
        _router?.Push(this);
    }

    private static View BuildRow()
    {
        var icon = new Image { WidthRequest = 26, HeightRequest = 26, IsVisible = false, VerticalOptions = LayoutOptions.Center };
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(PickItem.IconPath)));
        icon.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(PickItem.IconPath), converter: NotNull));

        var name = new Label { TextColor = UiTokens.Paper, FontFamily = DsChrome.PixelFont, FontSize = 15, VerticalTextAlignment = TextAlignment.Center };
        name.SetBinding(Label.TextProperty, nameof(PickItem.Name));

        var row = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(10, 7),
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { icon, name },
        };
        Grid.SetColumn(name, 1);

        var cell = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            StrokeThickness = 1.5,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = row,
        };
        VisualStateManager.SetVisualStateGroups(cell, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState { Name = "Normal", Setters = { new Setter { Property = Border.StrokeProperty, Value = Colors.Transparent } } },
                    new VisualState { Name = "Selected", Setters = { new Setter { Property = Border.StrokeProperty, Value = UiTokens.Gold }, new Setter { Property = Border.StrokeThicknessProperty, Value = 2.5 } } },
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
        var source = string.IsNullOrWhiteSpace(query)
            ? _all
            : _all.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        return source.Take(MaxVisible).ToList();
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Move(-1); return true;
            case PadButton.Down: Move(1); return true;
            case PadButton.A:
                if (_index >= 0 && _index < _filtered.Count) Close(_filtered[_index]);
                return true;
            case PadButton.B: Close(null); return true;
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

    private void HighlightCurrent()
    {
        if (_filtered.Count == 0) return;
        _index = Math.Clamp(_index, 0, _filtered.Count - 1);
        _padSelecting = true;
        _list.SelectedItem = _filtered[_index];
        _list.ScrollTo(_index, position: ScrollToPosition.Center, animate: false);
    }

    private void Close(PickItem? result)
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
