using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Restore points terminal: every pre-write backup as a themed restore card.</summary>
public sealed class BackupHistoryPage : ContentPage, IPadHandler
{
    private readonly BackupHistoryViewModel _viewModel;
    private CollectionView _list = null!;
    private int _index = -1;
    private bool _padSelecting;

    public BackupHistoryPage(BackupHistoryViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        Title = "Restore points";
        BackgroundColor = UiTokens.Housing;
        NavigationPage.SetHasNavigationBar(this, false);

        var back = Kit.MiniCapsule("Back", UiTokens.Ink0);
        back.WidthRequest = 72;
        back.Clicked += async (_, _) => await Navigation.PopAsync();

        var header = Kit.HeaderBar("Restore points");
        header.VerticalOptions = LayoutOptions.Center;
        var titleRow = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { back, header },
        };
        Grid.SetColumn(header, 1);

        var readout = new Label
        {
            TextColor = UiTokens.InkSoft,
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextBody,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        readout.SetBinding(Label.TextProperty, new Binding(nameof(BackupHistoryViewModel.Status), converter: Kit.TidyText));

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(BuildRestoreCard),
        };
        _list.SetBinding(ItemsView.ItemsSourceProperty, nameof(BackupHistoryViewModel.Backups));
        _list.SelectionChanged += OnSelected;

        var terminal = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { Kit.Well(readout, padding: 8), _list },
        };
        Grid.SetRow(_list, 1);

        var panel = Kit.DevicePanel(terminal);
        var root = new Grid
        {
            Padding = new Thickness(14, 10),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { titleRow, panel },
        };
        Grid.SetRow(panel, 1);
        _hostGrid = new Grid { Children = { DsChrome.GridBackground(), root } };
        Content = _hostGrid;
    }

    private readonly Grid _hostGrid;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadCommand.Execute(null);
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: return MoveCursor(-1);
            case PadButton.Down: return MoveCursor(1);
            case PadButton.A:
                if (_index >= 0 && _index < _viewModel.Backups.Count) _ = ConfirmRestoreAsync(_viewModel.Backups[_index]);
                return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            default: return true; // own the pad while this page is up
        }
    }

    private bool MoveCursor(int delta)
    {
        if (_viewModel.Backups.Count == 0) return true;
        _index = Math.Clamp(_index < 0 ? 0 : _index + delta, 0, _viewModel.Backups.Count - 1);
        _padSelecting = true;
        _list.SelectedItem = _viewModel.Backups[_index];
        _list.ScrollTo(_index, position: ScrollToPosition.MakeVisible, animate: false);
        return true;
    }

    private static View BuildRestoreCard()
    {
        var icon = PksmIcons.Icon("credits", 22);

        var title = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        title.SetBinding(Label.TextProperty, nameof(BackupInfo.DisplayName));

        var detail = new Label { TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall };
        detail.SetBinding(Label.TextProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(BackupInfo.CreatedUtc), stringFormat: "{0:yyyy-MM-dd HH:mm:ss} UTC"),
                new Binding(nameof(BackupInfo.Format)),
                new Binding(nameof(BackupInfo.SizeBytes), stringFormat: "{0:N0} bytes"),
            },
            StringFormat = "{0} · {1} · {2}",
        });

        var change = new Label
        {
            TextColor = UiTokens.Ink0,
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextSmall,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        change.SetBinding(Label.TextProperty, nameof(BackupInfo.ChangeDescription));
        change.SetBinding(Label.IsVisibleProperty, nameof(BackupInfo.ChangeDescription), converter: NotNullToVisible);

        var text = new VerticalStackLayout { Spacing = 2, Children = { title, detail, change } };
        var row = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { icon, text },
        };
        Grid.SetColumn(text, 1);

        // A flat list row on the page's panel (no card per restore point); the cursor
        // wears the selected-button look.
        var card = Kit.Row(row, shaded: true, new Thickness(10, 8));
        card.Margin = new Thickness(0, 0, 0, 4);
        VisualStateManager.SetVisualStateGroups(card, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState { Name = "Normal", Setters = { new Setter { Property = Border.StrokeProperty, Value = Colors.Transparent }, new Setter { Property = VisualElement.BackgroundColorProperty, Value = UiTokens.RowStripe } } },
                    new VisualState { Name = "Selected", Setters = { new Setter { Property = Border.StrokeProperty, Value = UiTokens.Rim }, new Setter { Property = VisualElement.BackgroundColorProperty, Value = UiTokens.SelectFill } } },
                },
            },
        });
        return card;
    }

    /// <summary>Hides the change line for legacy restore points created before descriptions existed.</summary>
    private static readonly NotNullToVisibleConverter NotNullToVisible = new();

    private sealed class NotNullToVisibleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is string { Length: > 0 };

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private async void OnSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_padSelecting) { _padSelecting = false; return; } // pad only moves the cursor; A confirms
        if (args.CurrentSelection.FirstOrDefault() is not BackupInfo backup) return;
        ((CollectionView)sender!).SelectedItem = null;
        await ConfirmRestoreAsync(backup);
    }

    private async Task ConfirmRestoreAsync(BackupInfo backup)
    {
        if (!_viewModel.CanRestore)
        {
            await PadMenu.ShowAsync(_hostGrid, "Restore", "Connect to a save first - restoring writes into the connected save.", "OK");
            return;
        }

        if (HardcoreMode.IsOn)
        {
            if (!await ConfirmHardcoreRestoreAsync(backup)) return;
            await _viewModel.RestoreAsync(backup);
            return;
        }

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid,
            "Restore this point?",
            $"Write the {backup.CreatedUtc:yyyy-MM-dd HH:mm} UTC restore point into the connected save? The current state is preserved as a new restore point first.",
            "Restore");
        if (confirmed)
            await _viewModel.RestoreAsync(backup);
    }

    /// <summary>
    /// Hardcore restores can duplicate: a Pokémon moved to the Bank or another game since the
    /// restore point would exist again in this save. The Pokémon that would reappear are
    /// named, and the safe choice (Cancel) is the default cursor.
    /// </summary>
    private async Task<bool> ConfirmHardcoreRestoreAsync(BackupInfo backup)
    {
        const string Cancel = "Cancel (keep current save)";
        const string Anyway = "Restore anyway";
        var check = await _viewModel.CheckResurrectionAsync(backup);
        if (check is { IsSafe: true })
            return await PadMenu.ConfirmAsync(_hostGrid, "Restore this point?",
                $"Write the {backup.CreatedUtc:yyyy-MM-dd HH:mm} UTC restore point into the connected save? " +
                "No Pokémon that has left this save since then would come back. The current state is preserved as a new restore point first.",
                "Restore");

        string message;
        if (check is null)
        {
            message = $"{HardcoreMode.Marker}: this restore point could not be compared with the current save. " +
                "If any Pokémon was moved to the Bank or sent to another game after it was taken, restoring brings it back here " +
                "and it would then exist twice - a duplication Hardcore mode does not allow.";
        }
        else
        {
            var names = string.Join(", ", check.Reappearing.Take(6).Select(Describe));
            if (check.Reappearing.Count > 6) names += $" and {check.Reappearing.Count - 6} more";
            var where = new List<string>();
            if (check.InBank > 0) where.Add($"{check.InBank} now in the Bank");
            if (check.Elsewhere > 0) where.Add($"{check.Elsewhere} sent to another game or released");
            message = $"{HardcoreMode.Marker}: restoring would bring back {check.Reappearing.Count} Pokémon that left this save " +
                $"after this point ({string.Join(", ", where)}): {names}. " +
                "Any that still exist in the Bank or another game would then exist twice - a duplication Hardcore mode does not allow. " +
                "Release or withdraw those copies yourself if you restore.";
        }

        var choice = await PadMenu.ShowAsync(_hostGrid, "Duplication risk", message, Cancel, Anyway);
        return choice == Anyway;
    }

    private static string Describe(SlotSummary slot)
    {
        var name = slot.Nickname ?? $"#{slot.Species:000}";
        if (slot.IsEgg) name = $"Egg #{slot.Species:000}";
        return slot.IsShiny ? $"{name} (shiny)" : name;
    }
}
