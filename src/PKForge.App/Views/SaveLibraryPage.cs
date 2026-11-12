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
/// The Saves section: every save file in one list (those on Home, those hidden, the backup
/// copies), told apart by trainer, folder and date, with what can be done to one without
/// opening it.
/// </summary>
public sealed class SaveLibraryPage : ContentPage, IPadHandler
{
    private const string OpenOption = "Open";
    private const string SnapshotOption = "Take a snapshot";
    private const string IdentityOption = "Name, color, game";
    private const string HideOption = "Hide from Home";
    private const string ShowOption = "Show on Home";
    private const string DetailsOption = "Details";
    private const string JournalOption = "What changed";
    private const string CompareOption = "Compare with another";
    private const string ExportOption = "Copy to a folder";
    private const string HistoryOption = "Restore points";
    private const string RestoreSnapshotOption = "Restore a snapshot";
    private const string VersionsOption = "Other versions";
    private const string ConvertOption = "Convert for another emulator";

    private readonly SaveLibraryViewModel _viewModel;
    private readonly CollectionView _list;
    private readonly HorizontalStackLayout _chips;
    private readonly StatusToast _toast = new(top: 14); // over the page's title row: this page has no title bar
    private readonly Grid _hostGrid;
    private int _index = -1;
    private bool _padSelecting;

    // The fiche on the right: the save under the cursor, read from its file.
    private readonly ISpriteService _sprites;
    private readonly GameCartridgeMark _ficheMark = new() { WidthRequest = 80, HeightRequest = 80 };
    private readonly Image _ficheArt = new() { Aspect = Aspect.AspectFit, IsVisible = false, WidthRequest = 80, HeightRequest = 80 };
    private readonly Label _ficheTitle = FicheLabel(20, UiTokens.Ink0);
    private readonly Label _ficheTag = FicheLabel(UiTokens.TextSmall, UiTokens.InkSoft);
    private readonly Label _ficheFacts = FicheLabel(UiTokens.TextBody, UiTokens.Ink0);
    private readonly Label _ficheWhere = FicheLabel(UiTokens.TextSmall, UiTokens.InkSoft);
    private readonly SKCanvasView _ficheParty = new() { HeightRequest = 60, InputTransparent = true };
    private readonly VerticalStackLayout _fiche;
    private readonly Label _ficheEmpty = FicheLabel(UiTokens.TextBody, UiTokens.InkSoft);
    private SaveLibraryRow? _shown;
    private SaveInsight? _insight;
    private int _ficheTurn;

    private static Label FicheLabel(double size, Color color) => new()
    {
        FontFamily = DsChrome.PixelFont, FontSize = size, TextColor = color, LineBreakMode = LineBreakMode.WordWrap,
    };

    /// <summary>Set by Home: opens a save in the box browser once this page has closed.</summary>
    public Func<DetectedSave, Task>? OpenSave { get; set; }

    /// <summary>Set by Home: opens a save, then its restore points (its snapshots are among them).</summary>
    public Func<DetectedSave, Task>? OpenRestorePoints { get; set; }

    public SaveLibraryPage(SaveLibraryViewModel viewModel, ISpriteService sprites)
    {
        _viewModel = viewModel;
        _sprites = sprites;
        BindingContext = viewModel;
        Title = "Saves";
        BackgroundColor = UiTokens.Housing;
        NavigationPage.SetHasNavigationBar(this, false);

        var back = Kit.MiniCapsule("Back", UiTokens.Ink0);
        back.WidthRequest = 72;
        back.Clicked += async (_, _) => await Navigation.PopAsync();
        Kit.NoNativeFocus(back);
        var header = Kit.HeaderBar("Saves");
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
            FontSize = UiTokens.TextSmall,
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2,
            HorizontalTextAlignment = TextAlignment.End,
            VerticalOptions = LayoutOptions.Center,
        };
        readout.SetBinding(Label.TextProperty, new Binding(nameof(SaveLibraryViewModel.Status), converter: Kit.TidyText));

        _chips = new HorizontalStackLayout { Spacing = 8 };
        var chipScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _chips,
        };
        viewModel.ChipsChanged += RebuildChips;
        viewModel.ActionReported += _toast.Show;

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(BuildCard),
        };
        _list.SetBinding(ItemsView.ItemsSourceProperty, nameof(SaveLibraryViewModel.Rows));
        _list.SelectionChanged += OnSelected;
        Kit.NoNativeFocus(_list);

        // The fiche: the game's cartridge, who plays it, its party, what it holds, where
        // the file lives, and the three things done most.
        _ficheParty.PaintSurface += PaintGuard.Wrap("SaveLibraryPage.PaintParty", PaintParty);
        var open = Kit.Capsule("Open", UiTokens.Green);
        open.Clicked += async (_, _) => { if (_shown is { } row) await OpenAsync(row); };
        var snapshot = Kit.Capsule("Snapshot", UiTokens.Ink1);
        snapshot.Clicked += async (_, _) => { if (_shown is { } row) await SnapshotAsync(row); };
        var more = Kit.Capsule("More", UiTokens.Ink1);
        more.Clicked += async (_, _) => { if (_shown is { } row) await ShowActionsAsync(row); };
        foreach (var button in new[] { open, snapshot, more }) Kit.NoNativeFocus(button);
        var heading = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children =
            {
                new Grid { WidthRequest = 80, HeightRequest = 80, Children = { _ficheMark, _ficheArt } },
                new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center, Children = { _ficheTitle, _ficheTag } },
            },
        };
        Grid.SetColumn((View)heading.Children[1], 1);
        _fiche = new VerticalStackLayout
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                heading, _ficheParty, _ficheFacts, _ficheWhere,
            },
        };
        // The buttons stay in sight under the fiche, however long it reads.
        var ficheButtons = new HorizontalStackLayout { Spacing = 8, Children = { open, snapshot, more } };
        var ficheBody = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { new ScrollView { Content = _fiche }, ficheButtons },
        };
        Grid.SetRow(ficheButtons, 1);
        ficheButtons.SetBinding(IsVisibleProperty, new Binding(nameof(IsVisible), source: _fiche));
        _ficheEmpty.Text = "Pick a save to see its party, its Pokédex and what you can do with it.";
        _ficheEmpty.VerticalOptions = LayoutOptions.Center;
        _ficheEmpty.HorizontalTextAlignment = TextAlignment.Center;
        var fichePanel = Kit.Well(new Grid { Children = { _ficheEmpty, ficheBody } }, padding: 14);

        var shelves = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new(new GridLength(3, GridUnitType.Star)), new(new GridLength(2, GridUnitType.Star))],
            Children = { _list, fichePanel },
        };
        Grid.SetColumn(fichePanel, 1);

        // One line on top: the filters on the left, what the list holds on the right.
        var filters = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { chipScroll, readout },
        };
        Grid.SetColumn(readout, 1);
        var terminal = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { filters, shelves },
        };
        Grid.SetRow(shelves, 1);

        var footer = DsChrome.Footer(
            ("A", "Actions", () => OnPadButton(PadButton.A)),
            ("+", "Open", () => OnPadButton(PadButton.Start)),
            ("Y", "Snapshot", () => OnPadButton(PadButton.Y)),
            ("X", "Hide / show", () => OnPadButton(PadButton.X)),
            ("LR", "Filter", () => OnPadButton(PadButton.R)),
            ("-", "Hall of Fame", () => OnPadButton(PadButton.Select)),
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
        _hostGrid.Add(_toast);
        Content = _hostGrid;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.LoadAsync();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    private void RebuildChips()
    {
        // The list was just refilled: the cursor goes back on the row it was on.
        // It starts on the first one, so the fiche is never an empty panel.
        if (_viewModel.Rows.Count > 0)
        {
            _index = Math.Clamp(_index, 0, _viewModel.Rows.Count - 1);
            if (!Equals(_list.SelectedItem, _viewModel.Rows[_index])) _padSelecting = true;
            _list.SelectedItem = _viewModel.Rows[_index];
            ShowFiche(_viewModel.Rows[_index]);
        }
        else ShowFiche(null);
        _chips.Children.Clear();
        for (var i = 0; i < _viewModel.Chips.Count; i++)
        {
            var index = i;
            var chip = _viewModel.Chips[i];
            var active = i == _viewModel.ChipIndex;
            var button = new Button
            {
                Text = $"{chip.Label} {chip.Count}",
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
            button.Clicked += (_, _) => SelectChip(index);
            Kit.NoNativeFocus(button);
            _chips.Children.Add(button);
        }
    }

    private void SelectChip(int index)
    {
        _index = -1;
        _list.SelectedItem = null;
        _viewModel.SelectChip(index);
    }

    private static View BuildCard()
    {
        // The game's own cartridge art; PKForge's mark for hacks and games without art.
        var mark = new GameCartridgeMark { WidthRequest = 56, HeightRequest = 56 };
        mark.SetBinding(GameCartridgeMark.GenerationProperty, nameof(SaveLibraryRow.Generation));
        var art = new Image { Aspect = Aspect.AspectFit, IsVisible = false, WidthRequest = 56, HeightRequest = 56 };
        art.BindingContextChanged += async (_, _) =>
        {
            if (art.BindingContext is not SaveLibraryRow row) return;
            art.IsVisible = false;
            mark.IsVisible = true;
            var path = await GameArt.GetIconAsync(row.Entry.Save);
            if (!ReferenceEquals(art.BindingContext, row)) return; // the row was recycled meanwhile
            art.Source = path;
            art.IsVisible = path is not null;
            mark.IsVisible = path is null;
        };
        var icon = new Grid { WidthRequest = 56, HeightRequest = 56, VerticalOptions = LayoutOptions.Center, Children = { mark, art } };

        var title = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation };
        title.SetBinding(Label.TextProperty, nameof(SaveLibraryRow.Title));
        var who = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.TailTruncation };
        who.SetBinding(Label.TextProperty, nameof(SaveLibraryRow.Who));
        var where = new Label { TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.MiddleTruncation };
        where.SetBinding(Label.TextProperty, nameof(SaveLibraryRow.Where));
        var tag = new Label
        {
            TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall,
            VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.End,
        };
        tag.SetBinding(Label.TextProperty, nameof(SaveLibraryRow.Tag));

        var text = new VerticalStackLayout { Spacing = 2, Children = { title, who, where } };
        var row = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { icon, text, tag },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(tag, 2);

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
        await Task.CompletedTask;
        if (_padSelecting) { _padSelecting = false; return; } // the pad moved the cursor itself
        if (args.CurrentSelection.FirstOrDefault() is not SaveLibraryRow row) return;
        // A touch picks the save: its fiche shows on the right, with its buttons.
        _index = _viewModel.Rows.IndexOf(row);
        ShowFiche(row);
    }

    /// <summary>Fills the fiche for <paramref name="row"/>: what the list knows at once, then what the file says.</summary>
    private async void ShowFiche(SaveLibraryRow? row)
    {
        try { await FillFicheAsync(row); }
        catch (Exception error)
        {
            // async void: an escaping exception would close the app.
            AppLog.Error("saves", "The save details could not be shown", error);
            if (ReferenceEquals(_shown, row)) _ficheFacts.Text = "This file could not be read for more.";
        }
    }

    private async Task FillFicheAsync(SaveLibraryRow? row)
    {
        var turn = ++_ficheTurn;
        _shown = row;
        _fiche.IsVisible = row is not null;
        _ficheEmpty.IsVisible = row is null;
        if (row is null) return;

        _ficheTitle.Text = row.Title;
        _ficheTag.Text = row.Tag.Length > 0 ? row.Tag : "On Home";
        _ficheWhere.Text = row.Where;
        _ficheFacts.Text = "Reading the save…";
        _ficheMark.Generation = row.Generation;
        _ficheMark.IsVisible = true;
        _ficheArt.IsVisible = false;
        _insight = null;
        _ficheParty.InvalidateSurface();

        var path = await GameArt.GetIconAsync(row.Entry.Save);
        if (turn != _ficheTurn) return;
        _ficheArt.Source = path;
        _ficheArt.IsVisible = path is not null;
        _ficheMark.IsVisible = path is null;

        // A moment's pause: a cursor running down the list does not read every file.
        await Task.Delay(180);
        if (turn != _ficheTurn) return;
        var insight = await _viewModel.InsightAsync(row);
        if (turn != _ficheTurn) return;
        _insight = insight;
        _ficheFacts.Text = insight is null
            ? "This file could not be read for more."
            : string.Join('\n', SaveInsights.Lines(insight, _viewModel.SpeciesName).Where(line => !line.StartsWith("Party", StringComparison.Ordinal)));
        _ficheParty.InvalidateSurface();
    }

    /// <summary>The party as the box icons, six seats wide.</summary>
    private void PaintParty(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_insight?.PartyMembers is not { Count: > 0 } party) return;
        var seat = args.Info.Width / 6f;
        var height = Math.Min(args.Info.Height, seat * 0.75f);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = 0; i < party.Count && i < 6; i++)
        {
            var look = party[i].Look;
            var dest = SKRect.Create(i * seat + (seat - height / 0.75f) / 2, (args.Info.Height - height) / 2, height / 0.75f, height);
            if (!_sprites.TryGetShowdownIcon(look, RepaintParty, out var sheet, out var cell)) continue; // still loading
            if (sheet is not null)
            {
                canvas.DrawImage(SpriteImages.Of(sheet), SKRect.Create(cell.Left, cell.Top, cell.Width, cell.Height), dest, sampling, null);
                continue;
            }
            if (_sprites.GetSprite(look) is { } sprite) canvas.DrawImage(SpriteImages.Of(sprite), dest, sampling);
            else _sprites.Warm(look, RepaintParty);
        }
    }

    /// <summary>The sprite loader calls back from its own thread: the repaint goes to the UI thread.</summary>
    private void RepaintParty() => MainThread.BeginInvokeOnMainThread(_ficheParty.InvalidateSurface);

    private async Task OpenAsync(SaveLibraryRow row)
    {
        await Navigation.PopAsync();
        if (OpenSave is not null) await OpenSave(row.Entry.Save);
    }

    public bool OnPadButton(PadButton button)
    {
        // The museum (or its loading screen) owns the pad while it opens and stays up.
        if (_museumOpen) return true;
        var row = _index >= 0 && _index < _viewModel.Rows.Count ? _viewModel.Rows[_index] : null;
        switch (button)
        {
            case PadButton.Up: return MoveCursor(-1);
            case PadButton.Down: return MoveCursor(1);
            // With no cursor yet, a button first puts it on the first save.
            case PadButton.A:
                if (row is null) return MoveCursor(1);
                _ = ShowActionsAsync(row);
                return true;
            case PadButton.Y:
                if (row is null) return MoveCursor(1);
                _ = SnapshotAsync(row);
                return true;
            case PadButton.X:
                if (row is null) return MoveCursor(1);
                _ = _viewModel.SetOnShelfAsync(row, !row.Entry.OnShelf);
                return true;
            case PadButton.Start:
                if (row is null) return MoveCursor(1);
                _ = OpenAsync(row);
                return true;
            case PadButton.L: SelectChip(_viewModel.ChipIndex - 1); return true;
            case PadButton.R: SelectChip(_viewModel.ChipIndex + 1); return true;
            case PadButton.Select: _ = MuseumAsync(); return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            default: return true; // own the pad while this page is up
        }
    }

    /// <summary>The Hall of Fame museum over this page; A on a hall opens that save.</summary>
    private async Task MuseumAsync()
    {
        if (_museumOpen) return;
        _museumOpen = true;
        SaveLibraryRow? chosen;
        try { chosen = await MuseumScreen.ShowAsync(_hostGrid, _viewModel, _sprites); }
        catch (Exception error)
        {
            AppLog.Error("museum", "The museum could not open", error);
            return;
        }
        finally { _museumOpen = false; }
        if (chosen is not null) await OpenAsync(chosen);
    }

    private bool _museumOpen;

    private bool MoveCursor(int delta)
    {
        if (_viewModel.Rows.Count == 0) return true;
        _index = Math.Clamp(_index < 0 ? 0 : _index + delta, 0, _viewModel.Rows.Count - 1);
        // Only a real change fires SelectionChanged: set on a clamped move, the flag would stick
        // and swallow the next tap, leaving the highlight on one row and the actions on another.
        if (!Equals(_list.SelectedItem, _viewModel.Rows[_index])) _padSelecting = true;
        _list.SelectedItem = _viewModel.Rows[_index];
        _list.ScrollTo(_index, position: ScrollToPosition.MakeVisible, animate: false);
        ShowFiche(_viewModel.Rows[_index]);
        return true;
    }

    private async Task ShowActionsAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        var options = new List<PadOption>
        {
            new(OpenOption, IconPath: "game", Detail: "Open its boxes."),
            new(SnapshotOption, IconPath: "history", Detail: "Keep it as it is now, by name."),
            new(DetailsOption, IconPath: "info", Detail: "Party, boxes, Pokédex, file."),
            new(JournalOption, IconPath: "report", Detail: "Since its last restore point."),
            new(HistoryOption, IconPath: "history", Detail: row.Snapshots == 0 ? "Put it back as it was." : $"And your {(row.Snapshots == 1 ? "snapshot" : $"{row.Snapshots} snapshots")}."),
            new(IdentityOption, IconPath: "palette", Detail: "Rename, recolor, set the game."),
            row.Entry.OnShelf
                ? new(HideOption, IconPath: "close", Detail: "It stays in this list.")
                : new(ShowOption, IconPath: "show", Detail: "Put it on the Home shelf."),
            new(ExportOption, IconPath: "export", Detail: "Another emulator, a sync folder."),
        };
        if (row.Snapshots > 0)
            options.Insert(2, new PadOption(RestoreSnapshotOption, IconPath: "history", Detail: "Put it back as one of them was."));
        // The playthrough's copies, grouped under this entry.
        if (row.Versions.Count > 0)
            options.Add(new PadOption(VersionsOption, IconPath: "file", Detail: $"{row.Versions.Count} other file{(row.Versions.Count == 1 ? "" : "s")} of this adventure."));
        // Cartridge saves only: .sav, .srm and .dsv are the same memory in other wrappers.
        if (save.Generation is >= 1 and <= 5)
            options.Add(new PadOption(ConvertOption, IconPath: "export", Detail: "A new file next to it; the original stays."));
        // Only when the game has another save to compare it with.
        if (_viewModel.SameGame(row).Count > 0)
            options.Add(new PadOption(CompareOption, IconPath: "report", Detail: "What each one has alone."));
        var choice = await PadMenu.ShowAsync(_hostGrid, row.Title, $"{row.Who}\n{row.Where}", [.. options]);
        switch (choice)
        {
            case HistoryOption:
                await Navigation.PopAsync();
                if (OpenRestorePoints is not null) await OpenRestorePoints(save);
                break;
            case ExportOption:
                await _viewModel.ExportAsync(row);
                break;
            case RestoreSnapshotOption:
                await RestoreSnapshotAsync(row);
                break;
            case VersionsOption:
                await VersionsAsync(row);
                break;
            case ConvertOption:
                await ConvertAsync(row);
                break;
            case CompareOption:
                await CompareAsync(row);
                break;
            case OpenOption:
                await OpenAsync(row);
                break;
            case SnapshotOption:
                await SnapshotAsync(row);
                break;
            case IdentityOption:
                await SaveIdentitySheet.ShowAsync(_hostGrid, _viewModel.Picker, save, offerOpen: false);
                await _viewModel.LoadAsync(keepStatus: true);
                break;
            case HideOption:
                await _viewModel.SetOnShelfAsync(row, onShelf: false);
                break;
            case ShowOption:
                await _viewModel.SetOnShelfAsync(row, onShelf: true);
                break;
            case JournalOption:
            {
                var overlay = LoadingOverlay.Show(_hostGrid, "Reading the save…", "Nothing is written.");
                (BackupInfo? Since, IReadOnlyList<JournalFact> Facts)? journal;
                try { journal = await _viewModel.JournalAsync(row); }
                finally { overlay.Close(); }
                var text = journal switch
                {
                    null => "The save or its restore point could not be read.",
                    { Since: null } => "No restore point of this save yet to compare with. PKForge keeps one each time it writes the save, or when you take a snapshot.",
                    { Since: { } since, Facts: var facts } =>
                        $"Since {(SaveLibrary.SnapshotName(since.ChangeDescription) is { } name ? $"the snapshot \"{name}\"" : "the last restore point")} ({RestorePoints.When(since.CreatedUtc, DateTimeOffset.Now)}):\n{SaveJournal.Report(facts)}",
                };
                await PadMenu.ShowAsync(_hostGrid, $"{row.Title}: what changed", text, "OK");
                break;
            }
            case DetailsOption:
            {
                var overlay = LoadingOverlay.Show(_hostGrid, "Reading the save…", "Nothing is written.");
                SaveInsight? insight;
                try { insight = await _viewModel.InsightAsync(row); }
                finally { overlay.Close(); }
                await PadMenu.ShowAsync(_hostGrid, row.Title, Details(row, insight), "OK");
                break;
            }
        }
    }

    private async Task SnapshotAsync(SaveLibraryRow row)
    {
        var name = await TextPopup.ShowLineAsync(_hostGrid, $"Snapshot of {row.Title}", "A name, like: before the League", "");
        if (name is null) return;
        await _viewModel.SnapshotAsync(row, name);
    }

    /// <summary>Lists the save's snapshots and restores the one picked, once the player confirms.</summary>
    private async Task RestoreSnapshotAsync(SaveLibraryRow row)
    {
        var snapshots = await _viewModel.SnapshotsAsync(row);
        if (snapshots.Count == 0) return;
        var options = snapshots.Select((point, i) => new PadOption($"{i + 1}. {SaveLibrary.SnapshotName(point.ChangeDescription)}", IconPath: "history",
            Detail: $"kept {RestorePoints.When(point.CreatedUtc, DateTimeOffset.Now)}")).ToArray();
        var picked = await PadMenu.ShowAsync(_hostGrid, "Restore a snapshot", Place(row), options);
        if (picked is null) return;
        var snapshot = snapshots[Array.FindIndex(options, option => option.Label == picked)];
        // Hardcore restores are checked for Pokémon that would come back twice: that check lives in Restore points.
        if (HardcoreMode.IsOn)
        {
            await PadMenu.ShowAsync(_hostGrid, "Restore a snapshot",
                $"{HardcoreMode.Marker}: open {row.Title} and restore it from Restore points, which checks that no Pokémon would exist twice.", "OK");
            return;
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Restore this snapshot?",
            $"Put {Place(row)} back as it was in \"{SaveLibrary.SnapshotName(snapshot.ChangeDescription)}\" ({RestorePoints.When(snapshot.CreatedUtc, DateTimeOffset.Now)})? Restore points can undo it.",
            "Restore");
        if (!confirmed) return;
        var overlay = LoadingOverlay.Show(_hostGrid, "Restoring…", "The current state is kept first.");
        try { await _viewModel.RestoreSnapshotAsync(row, snapshot); }
        finally { overlay.Close(); }
    }

    /// <summary>The other files of this playthrough, newest first; picking one offers its own actions.</summary>
    private async Task VersionsAsync(SaveLibraryRow row)
    {
        var options = row.Versions.Select((version, i) => new PadOption($"{i + 1}. {Place(version)}", IconPath: "file",
            Detail: string.Join(" · ", new[] { version.Entry.Tag, version.Entry.Save.LastModified is { } changed ? $"changed {RestorePoints.When(changed, DateTimeOffset.Now)}" : "" }
                .Where(part => part.Length > 0)))).ToArray();
        var picked = await PadMenu.ShowAsync(_hostGrid, $"{row.Title}: other versions", $"Newest first. Shown: {Place(row)}", options);
        if (picked is null) return;
        await ShowActionsAsync(row.Versions[Array.FindIndex(options, option => option.Label == picked)]);
    }

    /// <summary>Offers the other emulator formats; a conversion is written as a new file, never over the original.</summary>
    private async Task ConvertAsync(SaveLibraryRow row)
    {
        var overlay = LoadingOverlay.Show(_hostGrid, "Reading the save…", "Nothing is written.");
        IReadOnlyList<(SaveContainer Target, SaveConversion Result)> conversions;
        try { conversions = await _viewModel.ConversionsAsync(row); }
        finally { overlay.Close(); }
        if (conversions.Count == 0) return;

        static string Label(SaveContainer target) => target switch
        {
            SaveContainer.RetroArch => "RetroArch (.srm)",
            SaveContainer.DeSmuME => "DraStic / DeSmuME (.dsv)",
            _ => "mGBA, melonDS and others (.sav)",
        };
        var options = conversions.Select(option => new PadOption(Label(option.Target), IconPath: "export",
            Detail: option.Result.Succeeded ? $"As {option.Result.FileName}" : option.Result.Refusal)).ToArray();
        var picked = await PadMenu.ShowAsync(_hostGrid, "Convert for another emulator",
            "Only conversions that come back to the very same bytes are offered.", options);
        if (picked is null) return;
        var chosen = conversions[Array.FindIndex(options, option => option.Label == picked)].Result;
        if (!chosen.Succeeded)
        {
            await PadMenu.ShowAsync(_hostGrid, "Convert", chosen.Refusal, "OK");
            return;
        }
        await _viewModel.WriteConversionAsync(row, chosen);
    }

    /// <summary>Picks another save of the same game and says what each holds that the other lacks.</summary>
    private async Task CompareAsync(SaveLibraryRow row)
    {
        var others = _viewModel.SameGame(row);
        if (others.Count == 0) return;
        var other = others[0];
        if (others.Count > 1)
        {
            // Told apart by folder and date: saves of one game share its name, and a copy its file name.
            var options = others.Select((o, i) => new PadOption($"{i + 1}. {Place(o)}", IconPath: "file",
                Detail: o.Entry.Save.LastModified is { } changed ? $"changed {RestorePoints.When(changed, DateTimeOffset.Now)}" : null)).ToArray();
            var picked = await PadMenu.ShowAsync(_hostGrid, "Compare with", Place(row), options);
            if (picked is null) return;
            other = others[Array.FindIndex(options, option => option.Label == picked)];
        }

        var overlay = LoadingOverlay.Show(_hostGrid, "Comparing…", "Both files are only read.");
        SaveComparison? comparison;
        try { comparison = await _viewModel.CompareAsync(row, other); }
        finally { overlay.Close(); }
        if (comparison is null)
        {
            await PadMenu.ShowAsync(_hostGrid, "Compare", "One of the two saves could not be read.", "OK");
            return;
        }

        string Names(IReadOnlyList<SlotSummary> slots) => slots.Count == 0 ? "nothing"
            : string.Join(", ", slots.Take(12).Select(slot => (slot.IsShiny ? "shiny " : "") + (slot.Nickname ?? _viewModel.SpeciesName(slot.Species ?? 0))))
              + (slots.Count > 12 ? $" and {slots.Count - 12} more" : "");
        var message = comparison.OnlyInFirst.Count == 0 && comparison.OnlyInSecond.Count == 0
            ? $"Both hold the same {comparison.InBoth} Pokémon.\n{Place(row)}\n{Place(other)}"
            : $"{comparison.InBoth} Pokémon are in both.\n" +
              $"Only in {Place(row)} ({comparison.OnlyInFirst.Count}): {Names(comparison.OnlyInFirst)}\n" +
              $"Only in {Place(other)} ({comparison.OnlyInSecond.Count}): {Names(comparison.OnlyInSecond)}";
        await PadMenu.ShowAsync(_hostGrid, $"{row.Title}: two saves", message, "OK");
    }

    /// <summary>A save by where it lives ("nds/backup/Platinum.sav"): what tells two files of one game apart.</summary>
    private static string Place(SaveLibraryRow row) =>
        row.Entry.Save.FolderHint is { Length: > 0 } folder ? $"{folder}/{row.Entry.Save.FileName}" : row.Entry.Save.FileName;

    private string Details(SaveLibraryRow row, SaveInsight? insight)
    {
        var save = row.Entry.Save;
        // What is in the save first, then where the file lives.
        var lines = new List<string>();
        if (insight is not null) lines.AddRange(SaveInsights.Lines(insight, _viewModel.SpeciesName));
        else if (save.TrainerName is { Length: > 0 } trainer) lines.Add($"Trainer {trainer}" + (save.PlayTime is { Length: > 0 } time ? $" · {time} played" : ""));
        lines.Add($"{SaveDescriptions.EmulatorName(save.Emulator)} · Generation {save.Generation}" + (save.Language is { Length: > 0 } language ? $" · {language}" : ""));
        lines.Add($"{save.FolderHint ?? "A single file you opened"} · {save.FileName}");
        if (save.LastModified is { } modified) lines.Add($"Last changed {RestorePoints.When(modified, DateTimeOffset.Now)}");
        lines.Add(row.Entry.OnShelf ? "On the Home shelf." : save.IsCopy ? "A backup copy, set aside from Home." : "Hidden from Home.");
        lines.Add(row.Snapshots == 0 ? "No snapshot yet." : $"{row.Snapshots} snapshot{(row.Snapshots == 1 ? "" : "s")} kept in Restore points.");
        return string.Join('\n', lines);
    }
}
