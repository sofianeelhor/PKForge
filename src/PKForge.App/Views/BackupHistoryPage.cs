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
        Kit.NoNativeFocus(back);

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
        Kit.NoNativeFocus(_list);

        // The filters: one chip per kind of change this save has points for.
        _chips = new HorizontalStackLayout { Spacing = 8 };
        var chipScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _chips,
        };
        viewModel.FiltersChanged += RebuildChips;

        var terminal = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            Children = { Kit.Well(readout, padding: 8), chipScroll, _list },
        };
        Grid.SetRow(chipScroll, 1);
        Grid.SetRow(_list, 2);

        var footer = DsChrome.Footer(
            ("A", "Restore", () => OnPadButton(PadButton.A)),
            ("Y", "Pin", () => OnPadButton(PadButton.Y)),
            ("LR", "Filter", () => OnPadButton(PadButton.R)),
            ("B", "Back", () => OnPadButton(PadButton.B)));

        var panel = Kit.DevicePanel(terminal);
        var root = new Grid
        {
            Padding = new Thickness(14, 10, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { titleRow, panel, footer },
        };
        Grid.SetRow(panel, 1);
        Grid.SetRow(footer, 2);
        _hostGrid = new Grid { Children = { DsChrome.GridBackground(), root } };
        // What a restore or a pin did is said once more as a notice, where the eye is.
        _hostGrid.Add(_toast);
        viewModel.ActionReported += _toast.Show;
        Content = _hostGrid;
    }

    private readonly HorizontalStackLayout _chips;
    private readonly StatusToast _toast = new(top: 14); // over the page's title row: this page has no title bar

    private void RebuildChips()
    {
        // The list was just refilled: the cursor goes back on the row it was on.
        if (_index >= 0 && _viewModel.Backups.Count > 0)
        {
            _index = Math.Min(_index, _viewModel.Backups.Count - 1);
            if (!Equals(_list.SelectedItem, _viewModel.Backups[_index])) _padSelecting = true;
            _list.SelectedItem = _viewModel.Backups[_index];
        }
        _chips.Children.Clear();
        for (var i = 0; i < _viewModel.Filters.Count; i++)
        {
            var index = i;
            var filter = _viewModel.Filters[i];
            var active = i == _viewModel.FilterIndex;
            var chip = new Button
            {
                Text = $"{filter.Label} {filter.Count}",
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                TextColor = UiTokens.Ink0,
                BackgroundColor = active ? UiTokens.SelectFill : UiTokens.RowStripe,
                BorderColor = active ? UiTokens.Rim : Colors.Transparent,
                BorderWidth = 1.5,
                CornerRadius = 8,
                Padding = new Thickness(12, 4),
                HeightRequest = 34,
            };
            chip.Clicked += (_, _) => SelectFilter(index);
            Kit.NoNativeFocus(chip);
            _chips.Children.Add(chip);
        }
    }

    private void SelectFilter(int index)
    {
        _viewModel.SelectFilter(index);
        _index = -1;
        _list.SelectedItem = null;
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
            // With no cursor yet, A and Y first put it on the newest point: nothing is restored
            // or pinned that the player was not looking at.
            case PadButton.A:
                if (_index < 0) return MoveCursor(1);
                if (_index < _viewModel.Backups.Count) _ = ConfirmRestoreAsync(_viewModel.Backups[_index].Info);
                return true;
            case PadButton.Y:
                if (_index < 0) return MoveCursor(1);
                if (_index < _viewModel.Backups.Count) _ = _viewModel.TogglePinAsync(_viewModel.Backups[_index]);
                return true;
            case PadButton.L: SelectFilter(_viewModel.FilterIndex - 1); return true;
            case PadButton.R: SelectFilter(_viewModel.FilterIndex + 1); return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            default: return true; // own the pad while this page is up
        }
    }

    private bool MoveCursor(int delta)
    {
        if (_viewModel.Backups.Count == 0) return true;
        _index = Math.Clamp(_index < 0 ? 0 : _index + delta, 0, _viewModel.Backups.Count - 1);
        // Only a real change fires SelectionChanged; a clamped move must not leave the flag set.
        if (!Equals(_list.SelectedItem, _viewModel.Backups[_index])) _padSelecting = true;
        _list.SelectedItem = _viewModel.Backups[_index];
        _list.ScrollTo(_index, position: ScrollToPosition.MakeVisible, animate: false);
        return true;
    }

    private static View BuildRestoreCard()
    {
        var icon = PksmIcons.Icon("credits", 22);

        // What the point undoes leads; when and which game follow. Every row used to open
        // with the game's name, the same on all of them.
        var title = new Label
        {
            TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15,
            LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2,
        };
        title.SetBinding(Label.TextProperty, nameof(RestorePointRow.Title));

        var detail = new Label { TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall };
        detail.SetBinding(Label.TextProperty, nameof(RestorePointRow.Detail));

        var tag = new Label
        {
            TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall,
            VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.End,
        };
        tag.SetBinding(Label.TextProperty, nameof(RestorePointRow.Tag));

        var text = new VerticalStackLayout { Spacing = 2, Children = { title, detail } };
        var row = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { icon, text, tag },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(tag, 2);

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

    private async void OnSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_padSelecting) { _padSelecting = false; return; } // pad only moves the cursor; A confirms
        if (args.CurrentSelection.FirstOrDefault() is not RestorePointRow row) return;
        ((CollectionView)sender!).SelectedItem = null;
        try { await ConfirmRestoreAsync(row.Info); }
        catch (Exception error)
        {
            // async void: an escaping exception would close the app.
            AppLog.Error("restore", "Restoring from the list failed", error);
            _toast.Show($"Not restored. {Domain.ErrorText.Plain(error)}");
        }
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
            $"Put the save back as it was {RestorePoints.When(backup.CreatedUtc, DateTimeOffset.Now)}? The current state is kept too.",
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
                $"Put the save back as it was {RestorePoints.When(backup.CreatedUtc, DateTimeOffset.Now)}? " +
                "No Pokémon that has left this save since then would come back. The current state is kept too.",
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
