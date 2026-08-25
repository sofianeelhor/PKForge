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

        var back = Kit.MiniCapsule("BACK", UiTokens.Ink0);
        back.WidthRequest = 72;
        back.Clicked += async (_, _) => await Navigation.PopAsync();

        var header = Kit.HeaderBar("RESTORE POINTS");
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
            TextColor = Color.FromArgb("#9AA5B0"),
            FontFamily = DsChrome.PixelFont,
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        readout.SetBinding(Label.TextProperty, nameof(BackupHistoryViewModel.Status));

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
            Children = { Kit.DevicePanel(readout, padding: 8), _list },
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

        var title = new Label { TextColor = UiTokens.Paper, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        title.SetBinding(Label.TextProperty, nameof(BackupInfo.DisplayName));

        var detail = new Label { TextColor = Color.FromArgb("#9AA5B0"), FontFamily = DsChrome.PixelFont, FontSize = 12 };
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

        var text = new VerticalStackLayout { Spacing = 2, Children = { title, detail } };
        var row = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { icon, text },
        };
        Grid.SetColumn(text, 1);

        var card = Kit.DevicePanel(row, padding: 10);
        card.Margin = new Thickness(2, 4);
        return card;
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

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid,
            "Restore this point?",
            $"Write the {backup.CreatedUtc:yyyy-MM-dd HH:mm} UTC restore point into the connected save? The current state is preserved as a new restore point first.",
            "Restore");
        if (confirmed)
            await _viewModel.RestoreAsync(backup);
    }
}
