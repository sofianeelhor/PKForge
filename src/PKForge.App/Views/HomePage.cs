using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The console home: a horizontal shelf of game cartridges, a first-run wizard when
/// nothing is linked, and a bottom hint bar. Landscape composition for the AYN Thor.
/// </summary>
public sealed class HomePage : ContentPage, IPadHandler
{
    private readonly SavePickerViewModel _viewModel;
    private CollectionView _shelf = null!;
    private int _shelfIndex = -1;
    private bool _padSelecting;
    private DsCard[] _cards = [];
    private int _zone;       // 0 = game shelf, 1 = the destination cards
    private int _cardIndex;

    public HomePage(SavePickerViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        Title = "PKForge";
        BackgroundColor = UiTokens.Housing;
        NavigationPage.SetHasNavigationBar(this, false);

        // GAMES section: the cartridge shelf (reused), labelled DS-style.
        var gamesLabel = new Label
        {
            Text = "Games", FontFamily = DsChrome.PixelFont, FontSize = 14,
            TextColor = UiTokens.Ink1,
        };
        _shelf = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal) { ItemSpacing = 12 },
            ItemTemplate = new DataTemplate(BuildCartridgeTile),
            VerticalOptions = LayoutOptions.Center,
        };
        _shelf.SetBinding(ItemsView.ItemsSourceProperty, nameof(SavePickerViewModel.Groups));
        _shelf.SelectionChanged += OnGameSelected;
        var shelfArea = new Grid
        {
            RowSpacing = 6,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { gamesLabel, _shelf },
        };
        Grid.SetRow(_shelf, 1);

        // The three destinations as PKSM tiles with bundled pixel icons.
        var bank = new DsCard("bank", "Bank") { Tapped = () => _ = PushAsync<BankPage>() };
        var events = new DsCard("events", "Events") { Tapped = () => _ = ShowEventsMenuAsync() };
        var settings = new DsCard("settings", "Settings") { Tapped = () => _ = ShowSettingsAsync() };
        _cards = [bank, events, settings];
        var cards = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            Children = { bank, events, settings },
        };
        Grid.SetColumn(events, 1);
        Grid.SetColumn(settings, 2);

        var body = new Grid
        {
            Padding = new Thickness(14, 10),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { shelfArea, cards },
        };
        Grid.SetRow(cards, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), body } };

        var footer = DsChrome.Footer(
            ("A", "Open", null),
            ("Y", "Link", () => _ = ShowLinkMenuAsync()),
            ("X", "File", () => _ = LinkFileAsync()),
            ("+", "Settings", () => _ = ShowSettingsAsync()));

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip("PKForge", "Offline"), bodyHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(footer, 3);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;
    }

    private Grid _hostGrid = null!;

    private bool _welcomeShown;
    private bool _scannedOnce;

    /// <summary>The Thor's lower screen is on from launch - the app *is* dual-screen.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _zone = 0;
        ClearCardFocus();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // Scan once per launch; coming back from the box must not trigger a re-walk.
        if (!_scannedOnce)
        {
            _scannedOnce = true;
            _viewModel.RescanCommand.Execute(null);
        }
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable == true)
        {
            try { _ = host.ShowAsync(); }
            catch { }
        }

        // First run: welcome as the same in-world menu everything else uses, no special panel.
        if (_viewModel.ShowWizard && !_welcomeShown)
        {
            _welcomeShown = true;
            Dispatcher.Dispatch(async () =>
            {
                // First run plays like a Pokémon intro: a professor-style dialogue, then the choices.
                await DialogueBox.ShowSequenceAsync(_hostGrid,
                    "Hello! Welcome to PKForge!",
                    "This is a place where your Pokémon from every game can live together, safely.",
                    "I can edit them, keep them in the Bank, and make sure each one is legal.",
                    "And every change is backed up before it is written, so nothing is ever lost.",
                    "Now then... let's find your games!");

                var choice = await PadMenu.ShowAsync(_hostGrid, "Get started", null,
                    new PadOption("Link an emulator", IconPath: "folder"),
                    new PadOption("Open a single save file", IconPath: "search"),
                    new PadOption($"Download the sprite pack ({SpritePackDownloader.SizeHint})", IconPath: "storage"),
                    new PadOption("Maybe later", IconPath: "events"));
                switch (choice)
                {
                    case "Link an emulator": await ShowLinkMenuAsync(); break;
                    case "Open a single save file": await LinkFileAsync(); break;
                    case var pack when pack?.StartsWith("Download the sprite pack", StringComparison.Ordinal) == true:
                        await DownloadSpritePackAsync();
                        break;
                }
                _viewModel.CompleteSetup();
            });
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    /// <summary>Every button on the home screen, in one place.</summary>
    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Down when _zone == 0: _zone = 1; return FocusCard(0);
            case PadButton.Up when _zone == 1: _zone = 0; ClearCardFocus(); return true;
            case PadButton.Left: return _zone == 0 ? MoveShelf(-1) : FocusCard(_cardIndex - 1);
            case PadButton.Right: return _zone == 0 ? MoveShelf(1) : FocusCard(_cardIndex + 1);
            case PadButton.A:
                if (_zone == 1) { _cards[_cardIndex].Tapped?.Invoke(); return true; }
                return OpenShelfSelection();
            case PadButton.X: _ = LinkFileAsync(); return true;
            case PadButton.Y: _ = ShowLinkMenuAsync(); return true;
            case PadButton.R: _ = PushAsync<BackupHistoryPage>(); return true;
            case PadButton.Start: _ = ShowSettingsAsync(); return true;
            default: return false;
        }
    }

    /// <summary>Move the cursor among the Bank/Events/Settings cards (the second focus zone).</summary>
    private bool FocusCard(int index)
    {
        if (_cards.Length == 0) return false;
        _cardIndex = Math.Clamp(index, 0, _cards.Length - 1);
        for (var i = 0; i < _cards.Length; i++) _cards[i].Selected = i == _cardIndex;
        return true;
    }

    private void ClearCardFocus()
    {
        foreach (var card in _cards) card.Selected = false;
    }

    private bool MoveShelf(int delta)
    {
        if (_viewModel.Groups.Count == 0) return false;
        _shelfIndex = Math.Clamp(_shelfIndex < 0 ? 0 : _shelfIndex + delta, 0, _viewModel.Groups.Count - 1);
        _padSelecting = true;
        var selected = _viewModel.Groups[_shelfIndex];
        _shelf.SelectedItem = selected;
        _shelf.ScrollTo(_shelfIndex, position: ScrollToPosition.Center);
        // The lower screen previews the highlighted game's hero art.
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.PreviewGame = selected.Saves[0];
        return true;
    }

    private bool OpenShelfSelection()
    {
        if (_shelfIndex < 0 || _shelfIndex >= _viewModel.Groups.Count) return MoveShelf(0);
        _ = OpenGroupAsync(_viewModel.Groups[_shelfIndex]);
        return true;
    }

    /// <summary>One save opens directly; several saves of one game get a folder picker.</summary>
    private async Task OpenGroupAsync(SaveGroup group)
    {
        if (group.Saves.Count == 1)
        {
            await OpenSaveAsync(group.Saves[0]);
            return;
        }
        var options = group.Saves.Select(SaveOption).ToArray();
        var choice = await PadMenu.ShowAsync(_hostGrid, group.GameLabel.ToUpperInvariant(),
            $"{group.Saves.Count} saves for this game", options);
        if (choice is null) return;
        var index = Array.FindIndex(options, o => o.Label == choice);
        if (index >= 0) await OpenSaveAsync(group.Saves[index]);
    }

    private static PadOption SaveOption(DetectedSave save)
    {
        var label = save.FileName;
        if (!string.IsNullOrEmpty(save.TrainerName)) label += $" · {save.TrainerName}";
        if (save.LastModified is { } modified) label += $" · {modified:yyyy-MM-dd}";
        return new PadOption(label, IconPath: "storage");
    }

    /// <summary>Pad-navigable emulator link menu (Ⓨ) - same choices as the wizard, popup form.</summary>
    private async Task ShowLinkMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "LINK A STORAGE UNIT", null,
            new PadOption("RetroArch (GB/GBC/GBA)", IconPath: "retroarch"),
            new PadOption("melonDS (DS)", IconPath: "melonds"),
            new PadOption("Azahar (3DS)", IconPath: "azahar"),
            new PadOption("Eden (Switch)", IconPath: "eden"),
            new PadOption("Single save file", IconPath: "search"));
        switch (choice)
        {
            case "RetroArch (GB/GBC/GBA)": await _viewModel.AddRetroArchCommand.ExecuteAsync(null); break;
            case "melonDS (DS)": await _viewModel.AddMelonDsCommand.ExecuteAsync(null); break;
            case "Azahar (3DS)": await _viewModel.AddAzaharCommand.ExecuteAsync(null); break;
            case "Eden (Switch)": await _viewModel.AddEdenCommand.ExecuteAsync(null); break;
            case "Single save file": await LinkFileAsync(); break;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "SETTINGS", null,
            new PadOption("Link an emulator", IconPath: "folder"),
            new PadOption("Open a save file", IconPath: "search"),
            new PadOption("Rescan games", IconPath: "hex"),
            new PadOption($"Download full sprite pack ({SpritePackDownloader.SizeHint})", IconPath: "storage"),
            new PadOption("Scan report", IconPath: "script"),
            new PadOption("Restore points", IconPath: "credits"),
            new PadOption("About PKForge", IconPath: "settings"));
        switch (choice)
        {
            case "Link an emulator": await ShowLinkMenuAsync(); break;
            case "Open a save file": await LinkFileAsync(); break;
            case "Rescan games": await _viewModel.RescanCommand.ExecuteAsync(null); break;
            case var pack when pack?.StartsWith("Download full sprite pack", StringComparison.Ordinal) == true:
                await DownloadSpritePackAsync();
                break;
            case "Scan report": await PadMenu.ShowAsync(_hostGrid, "SCAN REPORT", _viewModel.ScanReport, "OK"); break;
            case "Restore points": await PushAsync<BackupHistoryPage>(); break;
            case "About PKForge": _viewModel.Status = "PKForge - open-source save manager & bank. GPLv3."; break;
        }
    }

    /// <summary>Downloads every species' animated + HOME sprites for full offline use.</summary>
    private async Task DownloadSpritePackAsync()
    {
        var downloader = IPlatformApplication.Current?.Services.GetService<SpritePackDownloader>();
        if (downloader is null) return;
        var overlay = LoadingOverlay.Show(_hostGrid, "CATCHING ALL THE SPRITES!",
            "Downloading animated battle sprites and HOME renders for every Pokémon. You can cancel anytime; finished parts are kept and it resumes where it left off.");
        try
        {
            await downloader.RunAsync(overlay.Report, overlay.Cancellation.Token);
            _viewModel.Status = "Sprite pack complete - fully offline now.";
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Sprite pack paused - resume anytime from Settings.";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"Sprite pack stopped: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }
    }

    private async Task LinkFileAsync()
    {
        await _viewModel.LinkFileCommand.ExecuteAsync(null);
        if (_viewModel.OpenedSave)
            await PushAsync<BoxBrowserPage>();
    }

    /// <summary>
    /// A game as an actual cartridge: fixed uniform size, dark contact strip on top,
    /// and the game art as the cart's label sticker (era-colored plastic behind it).
    /// </summary>
    private static View BuildCartridgeTile()
    {
        const double cartWidth = 76;
        const double cartHeight = 84;

        var contactStrip = new BoxView { HeightRequest = 9, Color = Colors.Black.WithAlpha(0.25f) };

        var labelText = new Label
        {
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = UiTokens.Ink0,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        labelText.SetBinding(Label.TextProperty, new Binding(nameof(DetectedSave.Generation), stringFormat: "GEN {0}"));

        var artImage = new Image { Aspect = Aspect.AspectFill, IsVisible = false };

        // The label sticker: white fallback with GEN text, replaced by real art when bundled.
        var sticker = new Border
        {
            BackgroundColor = Colors.White.WithAlpha(0.9f),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Margin = new Thickness(7, 5, 7, 7),
            Content = new Grid { Children = { labelText, artImage } },
        };

        var cartLayout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { contactStrip, sticker },
        };
        Grid.SetRow(sticker, 1);

        var cartBody = new Border
        {
            WidthRequest = cartWidth,
            HeightRequest = cartHeight,
            StrokeThickness = 1.5,
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            HorizontalOptions = LayoutOptions.Center,
            Padding = 0,
            Content = cartLayout,
        };
        cartBody.SetBinding(BackgroundColorProperty, new Binding(nameof(DetectedSave.Generation), converter: GenerationColor));
        cartBody.SetBinding(Border.StrokeProperty, new Binding(nameof(DetectedSave.Generation), converter: GenerationEdge));

        var iconHost = new Grid { Children = { cartBody } };
        iconHost.BindingContextChanged += async (sender, _) =>
        {
            if (((Grid)sender!).BindingContext is not DetectedSave save) return;
            artImage.IsVisible = false;
            labelText.IsVisible = true;
            var path = await GameArt.GetIconAsync(save.GameLabel);
            if (path is null || !ReferenceEquals(((Grid)sender).BindingContext, save)) return;
            artImage.Source = ImageSource.FromFile(path);
            artImage.IsVisible = true;
            labelText.IsVisible = false;
        };

        var name = new Label
        {
            TextColor = UiTokens.Ink0,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaximumWidthRequest = 112,
        };
        name.SetBinding(Label.TextProperty, nameof(DetectedSave.GameLabel));

        var trainer = new Label
        {
            TextColor = UiTokens.Ink1,
            FontSize = 10,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        trainer.SetBinding(Label.TextProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(DetectedSave.TrainerName)),
                new Binding(nameof(DetectedSave.PlayTime)),
            },
            StringFormat = "{0} · {1}",
        });

        // Every tile is exactly the same size: the shelf must read as a row of carts.
        var countChip = new Border
        {
            BackgroundColor = UiTokens.Cyan,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(5, 1),
            HorizontalOptions = LayoutOptions.End,
            IsVisible = false,
            Content = new Label { TextColor = Colors.White, FontSize = 9, FontAttributes = FontAttributes.Bold },
        };
        countChip.Content.SetBinding(Label.TextProperty, new Binding(nameof(SaveGroup.Count), stringFormat: "x{0}"));
        countChip.SetBinding(IsVisibleProperty, new Binding(nameof(SaveGroup.Count), converter: MoreThanOne));

        var card = Kit.DevicePanel(new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { iconHost, name, trainer, countChip },
        }, padding: 8);
        card.WidthRequest = 122;
        card.HeightRequest = 138;

        // Shelf cursor = the same gold frame as the box cursor, replacing the platform hover tint.
        VisualStateManager.SetVisualStateGroups(card, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState
                    {
                        Name = "Normal",
                        Setters =
                        {
                            new Setter { Property = Border.StrokeProperty, Value = UiTokens.ShellEdge },
                            new Setter { Property = Border.StrokeThicknessProperty, Value = 1.5 },
                            new Setter { Property = BackgroundColorProperty, Value = UiTokens.Shell },
                        },
                    },
                    new VisualState
                    {
                        Name = "Selected",
                        Setters =
                        {
                            new Setter { Property = Border.StrokeProperty, Value = UiTokens.SelectBorder },
                            new Setter { Property = Border.StrokeThicknessProperty, Value = 3.0 },
                            new Setter { Property = BackgroundColorProperty, Value = UiTokens.Shell },
                        },
                    },
                },
            },
        });
        return card;
    }

    /// <summary>The events shelf: community collections here, wondercards inside the game.</summary>
    private async Task ShowEventsMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "EVENT DATABASE", null,
            new PadOption($"Community boxes ({Services.CommunityBoxService.RepoTitle})", IconPath: "storage"),
            new PadOption("Wonder cards", IconPath: "events"));
        switch (choice)
        {
            case var boxes when boxes?.StartsWith("Community boxes", StringComparison.Ordinal) == true:
                await CollectionCenter.ShowAsync(_hostGrid);
                break;
            case "Wonder cards":
                await PadMenu.ShowAsync(_hostGrid, "WONDER CARDS",
                    "Wondercards depend on the game they are delivered to. Open a game from the shelf, press the Y button (Save data), and choose Wonder cards there.", "OK");
                break;
        }
    }

    private static readonly IValueConverter GenerationColor = new FuncConverter(gen => Kit.EraColor((int)(gen ?? 0)));
    private static readonly IValueConverter GenerationEdge = new FuncConverter(gen => Kit.EraColor((int)(gen ?? 0)).AddLuminosity(-0.15f));
    private static readonly MoreThanOneConverter MoreThanOne = new();

    private sealed class FuncConverter(Func<object?, Color> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private async void OnGameSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not DetectedSave save) return;
        if (_padSelecting)
        {
            // Pad navigation only moves the highlight; A opens.
            _padSelecting = false;
            return;
        }
        ((CollectionView)sender!).SelectedItem = null;
        await OpenSaveAsync(save);
    }

    private async Task OpenSaveAsync(DetectedSave save)
    {
        if (save.RequiresExtraCare)
        {
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid,
                "EMULATED CONSOLE STORAGE",
                $"{save.GameLabel} lives inside {save.Emulator}'s emulated storage - the delicate path. " +
                "PKForge backs up before every write, but close the emulator first.",
                "Connect");
            if (!confirmed) return;
        }

        await _viewModel.OpenAsync(save);
        if (_viewModel.OpenedSave)
        {
            var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
            if (state is not null) state.PreviewGame = null;
            await PushAsync<BoxBrowserPage>();
        }
    }

    private async Task PushAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }
}

/// <summary>Visible when a game has more than one save (the folder chip).</summary>
internal sealed class MoreThanOneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is int count && count > 1;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

internal static class ViewBuilderExtensions
{
    public static T Also<T>(this T view, Action<T> configure) where T : View
    {
        configure(view);
        return view;
    }
}
