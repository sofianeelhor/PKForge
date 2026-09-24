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
    private ScrollView _shelf = null!;
    private HorizontalStackLayout _shelfItems = null!;
    private int _shelfIndex = -1;
    private DsCard[] _cards = [];
    private int _zone;       // 0 = game shelf, 1 = the destination cards
    private int _cardIndex;
    private PokeparkPage? _parkPage;
    private int _parkNavigationPending;

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
            TextColor = UiTokens.Ink1, VerticalOptions = LayoutOptions.Center,
        };
        var filterCaption = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = 12, FontAttributes = FontAttributes.Bold,
            TextColor = UiTokens.Ink0, VerticalOptions = LayoutOptions.Center,
        };
        filterCaption.SetBinding(Label.TextProperty, new Binding("Caption",
            source: _viewModel.Filter, stringFormat: "FILTER: {0}"));
        var filterChip = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(9, 3),
            Content = filterCaption,
            HorizontalOptions = LayoutOptions.End,
        };
        var filterTap = new TapGestureRecognizer();
        filterTap.Tapped += (_, _) => _ = ShowFilterMenuAsync();
        filterChip.GestureRecognizers.Add(filterTap);
        var headerRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { gamesLabel, filterChip },
        };
        _shelfItems = new HorizontalStackLayout
        {
            Spacing = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        BindableLayout.SetItemsSource(_shelfItems, _viewModel.Groups);
        BindableLayout.SetItemTemplate(_shelfItems, new DataTemplate(BuildCartridgeTile));
        _shelf = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalOptions = LayoutOptions.Center,
            Content = _shelfItems,
        };
        var shelfArea = new Grid
        {
            RowSpacing = 6,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { headerRow, _shelf },
        };
        Grid.SetRow(_shelf, 1);
        BlockNativeFocus(_shelf);

        // The three destinations as PKSM tiles with bundled pixel icons.
        var bank = new DsCard("bank", "Bank") { Tapped = () => _ = PushAsync<BankPage>() };
        var park = new DsCard("park", "Poképark") { Tapped = () => _ = PushParkAsync() };
        var events = new DsCard("events", "Events") { Tapped = () => _ = ShowEventsMenuAsync() };
        var settings = new DsCard("settings", "Settings") { Tapped = () => _ = ShowSettingsAsync() };
        _cards = [bank, park, events, settings];
        foreach (var card in _cards) BlockNativeFocus(card);
        var cards = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            Children = { bank, park, events, settings },
        };
        Grid.SetColumn(park, 1);
        Grid.SetColumn(events, 2);
        Grid.SetColumn(settings, 3);

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
            ("L", "Filter", () => _ = ShowFilterMenuAsync()),
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

    /// <summary>
    /// The shelf's selection is drawn by PKForge (the cyan frame); Android's own focused
    /// state is the grey box that sticks around until the next tap, so it is disabled here.
    /// </summary>
    private static void BlockNativeFocus(View view)
    {
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is Android.Views.View platform)
            {
                platform.Focusable = false;
                platform.FocusableInTouchMode = false;
            }
        };
    }

    /// <summary>
    /// Fires <paramref name="onLongPress"/> once a finger rests on <paramref name="view"/>
    /// for the platform long-press timeout without drifting past the touch slop.
    /// </summary>
    private static void AttachLongPress(View view, Action onLongPress)
    {
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is not Android.Views.View platform) return;
            var slop = Android.Views.ViewConfiguration.Get(platform.Context!)!.ScaledTouchSlop;
            var timeout = Android.Views.ViewConfiguration.LongPressTimeout;
            var token = 0;
            float downX = 0, downY = 0;
            platform.Touch += (_, e) =>
            {
                // Handled is left as the other subscribers set it: the tap recognizer
                // shares this stream and must keep receiving the whole gesture.
                var motion = e.Event!;
                switch (motion.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                        var pressed = ++token;
                        downX = motion.GetX();
                        downY = motion.GetY();
                        view.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(timeout), () =>
                        {
                            if (pressed != token) return;
                            token++;
                            platform.PerformHapticFeedback(Android.Views.FeedbackConstants.LongPress);
                            onLongPress();
                        });
                        break;
                    case Android.Views.MotionEventActions.Move:
                        if (Math.Abs(motion.GetX() - downX) > slop || Math.Abs(motion.GetY() - downY) > slop)
                            token++;
                        break;
                    case Android.Views.MotionEventActions.Up:
                    case Android.Views.MotionEventActions.Cancel:
                        token++;
                        break;
                }
            };
        };
    }

    private bool _welcomeShown;
    private bool _scannedOnce;
    private bool _updateCheckQueued;
    private bool _isAppearing;
    private bool _resumeSubscribed;
    private AvailableAppUpdate? _pendingAuthorizedUpdate;

    /// <summary>The Thor's lower screen is on from launch - the app *is* dual-screen.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isAppearing = true;
        if (!_resumeSubscribed)
        {
            App.Resumed += OnAppResumed;
            _resumeSubscribed = true;
        }
        _zone = 0;
        ClearCardFocus();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // Scan once per launch; coming back from the box must not trigger a re-walk.
        if (!_scannedOnce)
        {
            _scannedOnce = true;
            var park = IPlatformApplication.Current?.Services.GetService<PokeparkService>();
            if (park is not null)
            {
                _ = Task.Run(() =>
                {
                    park.EnsureInitialized();
                    HabitatCatalog.Warm(park.LoadRoster().Select(mon => (mon.Species, mon.Form)));
                });
            }
            _viewModel.RescanCommand.Execute(null);
        }
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable == true)
        {
            try { _ = host.ShowAsync(); }
            catch { }
        }
        if (_parkPage is null)
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (_parkPage is null)
                    _parkPage = IPlatformApplication.Current?.Services.GetService<PokeparkPage>();
            });
        }

        // Returning from Android's install-unknown-apps screen finishes an update the
        // user already accepted with YES. No second question, no manual Check for update.
        if (_pendingAuthorizedUpdate is { } authorized && AppUpdateService.CanInstallUpdates())
        {
            _pendingAuthorizedUpdate = null;
            Dispatcher.DispatchAsync(async () => await InstallUpdateAsync(authorized));
            return;
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
                    new PadOption("Link an emulator", IconPath: "link"),
                    new PadOption("Open a single save file", IconPath: "file"),
                    new PadOption($"Download the sprite pack ({SpritePackDownloader.SizeHint})", IconPath: "download"),
                    new PadOption("Maybe later", IconPath: "close"));
                switch (choice)
                {
                    case "Link an emulator": await ShowLinkMenuAsync(); break;
                    case "Open a single save file": await LinkFileAsync(); break;
                    case var pack when pack?.StartsWith("Download the sprite pack", StringComparison.Ordinal) == true:
                        await DownloadSpritePackAsync();
                        break;
                }
                _viewModel.CompleteSetup();
                await CheckForUpdateAsync(automatic: true);
            });
        }
        else if (!_updateCheckQueued)
        {
            _updateCheckQueued = true;
            Dispatcher.Dispatch(async () => await CheckForUpdateAsync(automatic: true));
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isAppearing = false;
        if (_resumeSubscribed)
        {
            App.Resumed -= OnAppResumed;
            _resumeSubscribed = false;
        }
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    /// <summary>Android settings do not trigger MAUI page appearing again; resume does.</summary>
    private void OnAppResumed()
    {
        if (_pendingAuthorizedUpdate is not { } authorized || !AppUpdateService.CanInstallUpdates())
            return;

        _pendingAuthorizedUpdate = null;
        Dispatcher.DispatchAsync(async () => await InstallUpdateAsync(authorized));
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
            case PadButton.L: _ = ShowFilterMenuAsync(); return true;
            // B on a highlighted cartridge: its identity menu (rename, color, set game).
            case PadButton.B when _zone == 0 && _shelfIndex >= 0 && _shelfIndex < _viewModel.Groups.Count:
                _ = ShowSaveMenuAsync(_viewModel.Groups[_shelfIndex]);
                return true;
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
        var selected = _viewModel.Groups[_shelfIndex];
        for (var i = 0; i < _viewModel.Groups.Count; i++)
            _viewModel.Groups[i].IsSelected = i == _shelfIndex;
        if (_shelfIndex < _shelfItems.Children.Count && _shelfItems.Children[_shelfIndex] is View selectedView)
        {
            var centeredX = Math.Max(0, selectedView.X - Math.Max(0, (_shelf.Width - selectedView.Width) / 2));
            // Smooth scroll for single steps; snap while the pad repeats fast. Overlapping
            // animated scrolls during held navigation made the shelf teleport.
            var snap = Environment.TickCount64 - _lastShelfMoveMs < 200;
            _ = _shelf.ScrollToAsync(centeredX, 0, !snap);
        }
        _lastShelfMoveMs = Environment.TickCount64;
        // The lower screen previews the highlighted game's hero art.
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.PreviewGame = selected.Save;
        return true;
    }

    private long _lastShelfMoveMs;

    private bool OpenShelfSelection()
    {
        if (_shelfIndex < 0 || _shelfIndex >= _viewModel.Groups.Count) return MoveShelf(0);
        _ = OpenCardAsync(_viewModel.Groups[_shelfIndex]);
        return true;
    }

    /// <summary>Narrows the cartridge shelf: every game, one generation, one console,
    /// or plain alphabetical.</summary>
    private async Task ShowFilterMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "FILTER GAMES", null,
            new PadOption("All games", IconPath: "all"),
            new PadOption("Release order", IconPath: "calendar"),
            new PadOption("Alphabetical (A-Z)", IconPath: "alpha"),
            new PadOption("Game Boy (Gen I-II)", IconPath: "platform-gb"),
            new PadOption("GBA (Gen III)", IconPath: "platform-gba"),
            new PadOption("DS (Gen IV-V)", IconPath: "platform-ds"),
            new PadOption("3DS (Gen VI-VII)", IconPath: "azahar"),
            new PadOption("Switch (Gen VII-IX)", IconPath: "eden"),
            new PadOption("Gen I", IconPath: "generation"),
            new PadOption("Gen II", IconPath: "generation"),
            new PadOption("Gen III", IconPath: "generation"),
            new PadOption("Gen IV", IconPath: "generation"),
            new PadOption("Gen V", IconPath: "generation"),
            new PadOption("Gen VI", IconPath: "generation"),
            new PadOption("Gen VII", IconPath: "generation"),
            new PadOption("Gen VIII", IconPath: "generation"),
            new PadOption("Gen IX", IconPath: "generation"));
        var (key, caption) = choice switch
        {
            "All games" => ("all", "ALL"),
            "Release order" => ("release", "RELEASE"),
            "Alphabetical (A-Z)" => ("az", "A-Z"),
            "Game Boy (Gen I-II)" => ("gb", "GAME BOY"),
            "GBA (Gen III)" => ("gba", "GBA"),
            "DS (Gen IV-V)" => ("ds", "DS"),
            "3DS (Gen VI-VII)" => ("3ds", "3DS"),
            "Switch (Gen VII-IX)" => ("switch", "SWITCH"),
            { } gen when gen is not null && gen.StartsWith("Gen ", StringComparison.Ordinal) && int.TryParse(gen[4..], out var n)
                => ($"gen{n}", $"GEN {gen[4..]}"),
            _ => ("all", "ALL"),
        };
        _viewModel.ApplyFilter(key, caption);
        // Groups were replaced wholesale: re-run selection so a cartridge is highlighted.
        MoveShelf(_shelfIndex);
    }

    /// <summary>A cartridge with one save opens it; a shared cartridge asks which save first.</summary>
    private async Task OpenCardAsync(SaveCard card)
    {
        var save = await ChooseSaveAsync(card, "Which save do you want to open?");
        if (save is not null) await OpenSaveAsync(save);
    }

    /// <summary>Long-press / B: the save's identity menu ("Open save" first); a shared cartridge picks the save first.</summary>
    private async Task ShowSaveMenuAsync(SaveCard card)
    {
        var save = await ChooseSaveAsync(card, "Choose the save to rename, recolor or identify.");
        if (save is null) return;
        if (await SaveIdentitySheet.ShowAsync(_hostGrid, _viewModel, save))
            await OpenSaveAsync(save);
    }

    private Task<DetectedSave?> ChooseSaveAsync(SaveCard card, string message) =>
        SavePickerSheet.ChooseFromTileAsync(_hostGrid, card.Saves,
            $"{card.DisplayName.ToUpperInvariant()} · {card.SaveCount} SAVES", message);

    /// <summary>
    /// First open of a save whose editions share one format (FireRed/LeafGreen, Ruby/Sapphire,
    /// Diamond/Pearl): the bytes cannot say which cartridge it is, so the player picks once and
    /// the choice sticks (it stays editable from the save's menu). False when they backed out.
    /// </summary>
    private async Task<bool> ChooseEditionOnceAsync(DetectedSave save)
    {
        var store = _viewModel.Identities;
        var current = store.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
        if (_editionDeferred.Contains(save.DocumentId) || save.Guess is not { } guess
            || !SaveIdentityRules.NeedsEditionChoice(guess, current.GameChoiceId))
            return true;

        const string later = "Decide later";
        var editions = SaveIdentityRules.ChoicesFor(guess.Family, guess.Label).Where(c => !c.IsHack).ToArray();
        var options = editions.Select(c => new PadOption(c.Label, IconPath: "game"))
            .Append(new PadOption(later, IconPath: "close")).ToArray();
        var choice = await PadMenu.ShowAsync(_hostGrid, "WHICH VERSION IS THIS?",
            "Both versions write the same save file, so PKForge cannot tell them apart. " +
            "Pick yours once; you can change it later from the save's menu (long-press).", options);
        if (choice is null) return false;
        if (choice == later)
        {
            _editionDeferred.Add(save.DocumentId);
            return true;
        }
        store.Set(current with { GameChoiceId = editions.First(c => c.Label == choice).Id });
        return true;
    }

    /// <summary>Saves whose edition the player chose to decide later: not asked again this session.</summary>
    private readonly HashSet<string> _editionDeferred = new(StringComparer.Ordinal);

    /// <summary>Platform folders keep emulator choices together for touch and controller use.</summary>
    private async Task ShowLinkMenuAsync()
    {
        while (true)
        {
            var platform = await PadMenu.ShowAsync(_hostGrid, "LINK A STORAGE UNIT", "Choose a platform to see its emulators.",
                new PadOption("Game Boy / Game Boy Color", IconPath: "platform-gb"),
                new PadOption("Game Boy Advance", IconPath: "platform-gba"),
                new PadOption("Nintendo DS", IconPath: "platform-ds"),
                new PadOption("GameCube", IconPath: "platform-gc"),
                new PadOption("Nintendo 3DS", IconPath: "azahar"),
                new PadOption("Nintendo Switch", IconPath: "eden"),
                new PadOption("Single save file", IconPath: "file"));
            if (platform is null) return;
            if (platform == "Single save file") { await LinkFileAsync(); return; }
            var options = platform switch
            {
                "Game Boy / Game Boy Color" => new[]
                {
                    new PadOption("RetroArch", IconPath: "retroarch"),
                    new PadOption("Linkboy", IconPath: "linkboy"),
                    new PadOption("Pizza Boy C (GB/GBC)", IconPath: "pizzaboy"),
                },
                "Game Boy Advance" => new[]
                {
                    new PadOption("RetroArch", IconPath: "retroarch"),
                    new PadOption("Linkboy", IconPath: "linkboy"),
                    new PadOption("Pizza Boy A (GBA)", IconPath: "pizzaboy"),
                },
                "Nintendo DS" => new[]
                {
                    new PadOption("melonDS", IconPath: "melonds"),
                    new PadOption("DraStic", IconPath: "drastic"),
                    new PadOption("RetroArch", IconPath: "retroarch"),
                },
                "GameCube" => new[] { new PadOption("Dolphin", IconPath: "dolphin") },
                "Nintendo 3DS" => new[] { new PadOption("Azahar", IconPath: "azahar") },
                _ => new[] { new PadOption("Eden", IconPath: "eden") },
            };
            var choice = await PadMenu.ShowAsync(_hostGrid, platform.ToUpperInvariant(), null, options);
            if (choice is null) continue;
            var guidance = choice switch
            {
                "Dolphin" => "Save in game and stop emulation before editing Colosseum or XD. Select Dolphin's GC folder, a region folder, or Card A / Card B containing .gci saves. For .raw memory cards, export the game as GCI with Dolphin's Memory Card Manager, or configure that card slot as GCI Folder. After editing, start the game normally; loading an old save state can undo your edits.",
                "DraStic" => "Save in game and close DraStic. Select its backup folder containing .dsv battery saves, or the DraStic data folder. Save states are not supported. Restart the game normally after editing.",
                "Pizza Boy A (GBA)" or "Pizza Boy C (GB/GBC)" => "Save in game and close Pizza Boy. Select the folder containing its battery saves (.sav), not save states. If Android hides the folder, export the battery save in Pizza Boy and link that export. Import the edited export back into Pizza Boy, then restart the game normally.",
                "Azahar" or "Eden" => "Save in game and close the emulator. Select its files root containing the emulated storage. Restart the game normally after editing.",
                _ => "Save in game and close the emulator. Select its saves folder (or the folder containing your battery saves). Save states are not supported. Restart the game normally after editing.",
            };
            var proceed = await PadMenu.ShowAsync(_hostGrid, $"LINK {choice.ToUpperInvariant()}",
                guidance + " If Android does not offer access to the folder, use Single save file with an exported save.",
                new PadOption("Choose folder", IconPath: "folder"), new PadOption("Back", IconPath: "back"));
            if (proceed != "Choose folder") continue;
            switch (choice)
            {
                case "RetroArch": await _viewModel.AddRetroArchCommand.ExecuteAsync(null); break;
                case "melonDS": await _viewModel.AddMelonDsCommand.ExecuteAsync(null); break;
                case "Linkboy": await _viewModel.AddLinkboyCommand.ExecuteAsync(null); break;
                case "Azahar": await _viewModel.AddAzaharCommand.ExecuteAsync(null); break;
                case "Eden": await _viewModel.AddEdenCommand.ExecuteAsync(null); break;
                case "Dolphin": await _viewModel.AddDolphinCommand.ExecuteAsync(null); break;
                case "DraStic": await _viewModel.AddDraSticCommand.ExecuteAsync(null); break;
                case "Pizza Boy A (GBA)": await _viewModel.AddPizzaBoyGbaCommand.ExecuteAsync(null); break;
                case "Pizza Boy C (GB/GBC)": await _viewModel.AddPizzaBoyGbcCommand.ExecuteAsync(null); break;
            }
            return;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var hiddenCount = _viewModel.HiddenSaves.Count;
        var hiddenOption = $"Show hidden saves ({hiddenCount})";
        var options = new List<PadOption>
        {
            new("Link an emulator", IconPath: "link"),
            new("Manage linked storage", IconPath: "sdcard"),
            new("Open a save file", IconPath: "file"),
        };
        if (hiddenCount > 0) options.Add(new PadOption(hiddenOption, IconPath: "show"));
        options.AddRange(
        [
            new PadOption("Restore points", IconPath: "history"),
            new PadOption("About PKForge", IconPath: "info"),
            new PadOption("Check for update", IconPath: "update"),
            new PadOption("Music", IconPath: "music"),
            new PadOption("Misc", IconPath: "gears"),
            new PadOption("Quit PKForge", IconPath: "quit"),
        ]);
        var choice = await PadMenu.ShowAsync(_hostGrid, "SETTINGS", null, [.. options]);
        if (choice == hiddenOption) { await ShowHiddenSavesAsync(); return; }
        switch (choice)
        {
            case "Link an emulator": await ShowLinkMenuAsync(); break;
            case "Manage linked storage": await ShowManageLinkedStorageAsync(); break;
            case "Open a save file": await LinkFileAsync(); break;
            case "Restore points": await PushAsync<BackupHistoryPage>(); break;
            case "About PKForge": await AboutPopup.ShowAsync(_hostGrid); break;
            case "Check for update": await CheckForUpdateAsync(automatic: false); break;
            case "Music": await ShowMusicAsync(); break;
            case "Misc": await ShowMiscAsync(); break;
            case "Quit PKForge":
                if (await PadMenu.ConfirmAsync(_hostGrid, "QUIT PKFORGE?", "Unsaved edits in open menus are already backed up per write.", "Quit"))
                    Microsoft.Maui.Controls.Application.Current?.Quit();
                break;
        }
    }

    /// <summary>Hidden saves, one row each; picking one puts it back on the shelf.</summary>
    private async Task ShowHiddenSavesAsync()
    {
        while (_viewModel.HiddenSaves is { Count: > 0 } hidden)
        {
            var save = await SavePickerSheet.ChooseFromTileAsync(_hostGrid, hidden, "HIDDEN SAVES",
                "Pick a save to show it on Home and in save pickers again.", allowSingle: true);
            if (save is null) return;
            var current = _viewModel.Identities.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
            _viewModel.Identities.Set(current with { Hidden = false });
        }
    }

    private static string IconFor(EmulatorKind kind) => kind switch
    {
        EmulatorKind.RetroArch => "retroarch",
        EmulatorKind.MelonDS => "melonds",
        EmulatorKind.Linkboy => "linkboy",
        EmulatorKind.Azahar => "azahar",
        EmulatorKind.Eden => "eden",
        EmulatorKind.Dolphin => "dolphin",
        EmulatorKind.DraStic => "drastic",
        EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc => "pizzaboy",
        _ => "storage",
    };

    private async Task ShowManageLinkedStorageAsync()
    {
        var roots = IPlatformApplication.Current!.Services.GetRequiredService<IWatchedRootStore>().GetRoots();
        if (roots.Count == 0)
        {
            _viewModel.Status = "No linked storage units.";
            return;
        }

        var options = roots
            .Select(root => new PadOption($"Unlink {root.Kind} · {root.DisplayName}", IconPath: IconFor(root.Kind)))
            .Append(new PadOption("Unlink all storage units", IconPath: "unlink"))
            .Append(new PadOption("Cancel", IconPath: "close"))
            .ToArray();
        var choice = await PadMenu.ShowAsync(_hostGrid, "LINKED STORAGE", "Remove a linked emulator folder without resetting the app.", options);
        if (choice is null or "Cancel") return;

        var store = IPlatformApplication.Current!.Services.GetRequiredService<IWatchedRootStore>();
        if (choice == "Unlink all storage units")
        {
            var confirmedAll = await PadMenu.ConfirmAsync(_hostGrid, "UNLINK ALL STORAGE?",
                "Every linked emulator folder will be removed from PKForge. Your files stay on the device.", "Unlink all");
            if (!confirmedAll) return;
            foreach (var root in roots)
                store.RemoveRoot(root);
            _viewModel.Status = "All linked storage units removed.";
        }
        else
        {
            var index = Array.FindIndex(options, option => option.Label == choice);
            if (index < 0 || index >= roots.Count) return;
            var root = roots[index];
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "UNLINK STORAGE?",
                $"Remove {root.Kind} · {root.DisplayName} from PKForge? Your files stay on the device.", "Unlink");
            if (!confirmed) return;
            store.RemoveRoot(root);
            _viewModel.Status = $"{root.Kind} unlinked.";
        }

        await _viewModel.RescanCommand.ExecuteAsync(null);
    }

    /// <summary>Release checks: automatic failures stay quiet, manual failures explain themselves.</summary>
    private async Task CheckForUpdateAsync(bool automatic)
    {
        var service = IPlatformApplication.Current?.Services.GetService<AppUpdateService>();
        if (service is null) return;

        AppUpdateCheck check;
        try
        {
            check = await service.CheckAsync();
        }
        catch (Exception error)
        {
            Android.Util.Log.Warn("PKForgeUpdate", $"Automatic update check failed: {error}");
            if (!automatic)
                _viewModel.Status = $"Update check failed: {error.Message}";
            return;
        }

        if (check.Update is not { } update || !check.IsAvailable)
        {
            if (!automatic)
                await DialogueBox.ShowSequenceAsync(_hostGrid, check.Message);
            return;
        }
        if (automatic && !service.ShouldPromptAutomatically(update))
            return;
        if (automatic && !_isAppearing)
            return;

        var choice = await DialogueChoiceBox.ShowAsync(_hostGrid,
            $"A new version of PKForge is available! Version {update.Version} is ready to install. Would you like to update now?",
            "YES", "NO", "DON'T REMIND ME");
        switch (choice)
        {
            case "YES":
                await InstallUpdateAsync(update);
                break;
            case "NO":
                _viewModel.Status = $"Version {update.Version} will be offered again next launch.";
                break;
            case "DON'T REMIND ME":
                service.DontRemindMe(update);
                _viewModel.Status = $"I won't remind you about version {update.Version} again.";
                break;
        }
    }

    private async Task InstallUpdateAsync(AvailableAppUpdate update)
    {
        var service = IPlatformApplication.Current!.Services.GetRequiredService<AppUpdateService>();
        var overlay = LoadingOverlay.Show(_hostGrid, "DOWNLOADING THE UPDATE!",
            $"PKForge {update.Version} is on its way. Android will confirm the installation.");
        AppUpdateInstallResult result;
        try
        {
            result = await service.DownloadAndInstallAsync(update, (done, total) =>
            {
                var percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
                overlay.Report(percent, 100);
            }, overlay.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Update paused. You can start it again from Settings.";
            return;
        }
        catch (Exception error)
        {
            Android.Util.Log.Error("PKForgeUpdate", $"Download/install failed: {error}");
            _viewModel.Status = $"Update failed: {error.Message}";
            var openRelease = await PadMenu.ConfirmAsync(_hostGrid, "OPEN THE RELEASE PAGE?",
                "The in-app installer could not finish. The GitHub release page has the same APK.", "Open");
            if (openRelease)
                await Launcher.OpenAsync(update.ReleaseUrl);
            return;
        }
        finally
        {
            overlay.Close();
        }

        switch (result)
        {
            case AppUpdateInstallResult.InstallerOpened:
                _viewModel.Status = "Android is installing the update.";
                break;
            case AppUpdateInstallResult.ReleasePageOpened:
                _viewModel.Status = "The PKForge release page is open.";
                break;
            case AppUpdateInstallResult.InstallPermissionRequired:
                _pendingAuthorizedUpdate = update;
                var permissionChoice = await DialogueChoiceBox.ShowAsync(_hostGrid,
                    "Android needs permission to install PKForge updates. Allow install unknown apps for PKForge, then check again?",
                    "OPEN SETTINGS", "NOT NOW");
                if (permissionChoice == "OPEN SETTINGS")
                    AppUpdateService.OpenInstallPermissionSettings();
                break;
        }
    }

    /// <summary>Background music: library, play/pause, skip, order, autostart.</summary>
    private async Task ShowMusicAsync()
    {
        var music = IPlatformApplication.Current?.Services.GetService<Domain.IMusicPlayer>();
        if (music is null) return;

        while (true)
        {
            var order = music.Order == Domain.MusicOrder.Shuffle ? "Shuffle" : "In order";
            var auto = music.Autostart ? "ON" : "OFF";
            var androidMusic = music as Platforms.Android.MusicPlayer;
            var playing = music.IsPlaying
                ? $"Playing: {music.Library[music.CurrentIndex ?? 0].Title}"
                : $"Library: {music.Library.Count} track(s)";
            if (androidMusic?.LastError is { } err)
                playing += $"\nLast error: {err}";
            var choice = await PadMenu.ShowAsync(_hostGrid, "BACKGROUND MUSIC", playing,
                new PadOption(music.IsPlaying ? "Pause" : "Play", IconPath: music.IsPlaying ? "pause" : "play"),
                new PadOption("Skip to next track", IconPath: "skip"),
                new PadOption($"Add music files ({music.Library.Count})", IconPath: "folder"),
                new PadOption(music.Library.Count > 0 ? "Clear library" : "-", IconPath: "delete"),
                new PadOption($"Order: {order}", IconPath: "shuffle"),
                new PadOption($"Autostart: {auto}", IconPath: "settings"));
            switch (choice)
            {
                case "Play": music.Play(); break;
                case "Pause": music.Pause(); break;
                case "Skip to next track": music.Skip(); break;
                case var add when add?.StartsWith("Add music files", StringComparison.Ordinal) == true:
                {
                    var picker = IPlatformApplication.Current?.Services.GetService<Domain.IDocumentPicker>();
                    if (picker is null) break;
                    var documents = await picker.PickManyAsync();
                    if (documents.Count == 0) break;
                    var added = music.Add(documents);
                    _viewModel.Status = added > 0 ? $"Added {added} track(s) to the music library." : "Those files are already in the library.";
                    break;
                }
                case "Clear library":
                    var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "CLEAR MUSIC LIBRARY?",
                        "Removes every track. Your audio files on storage are untouched.", "Clear");
                    if (confirmed) music.Clear();
                    break;
                case var o when o?.StartsWith("Order: ", StringComparison.Ordinal) == true:
                    music.SetOrder(music.Order == Domain.MusicOrder.InOrder ? Domain.MusicOrder.Shuffle : Domain.MusicOrder.InOrder);
                    break;
                case var a when a?.StartsWith("Autostart: ", StringComparison.Ordinal) == true:
                    music.SetAutostart(!music.Autostart);
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Maintenance actions: the sprite pack, the rescan, and the scan report.</summary>
    private async Task ShowMiscAsync()
    {
        var trainerProfiles = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();
        var choice = await PadMenu.ShowAsync(_hostGrid, "MISC", null,
            new PadOption(trainerProfiles.UseCurrentTrainerForGeneration
                ? "Generated Pokémon obey trainer: ON"
                : "Generated Pokémon obey trainer: OFF", IconPath: "profile"),
            new PadOption($"Download full sprite pack ({SpritePackDownloader.SizeHint})", IconPath: "download"),
            new PadOption("Rescan games", IconPath: "refresh"),
            new PadOption("Scan report", IconPath: "report"),
            new PadOption(Services.HaXMode.IsOn ? "HaX mode: ON" : "HaX mode: OFF", IconPath: "hax"),
            new PadOption(Services.HardcoreMode.IsOn ? "Hardcore mode: ON" : "Hardcore mode: OFF", IconPath: "hardcore"));
        switch (choice)
        {
            case var ownership when ownership?.StartsWith("Generated Pokémon obey trainer:", StringComparison.Ordinal) == true:
                trainerProfiles.SetUseCurrentTrainerForGeneration(!trainerProfiles.UseCurrentTrainerForGeneration);
                _viewModel.Status = trainerProfiles.UseCurrentTrainerForGeneration
                    ? "Generated Pokémon will be owned by the open save's trainer."
                    : "Generated Pokémon may use Auto-Legality trainer data.";
                break;
            case var pack when pack?.StartsWith("Download full sprite pack", StringComparison.Ordinal) == true:
                await DownloadSpritePackAsync();
                break;
            case "Rescan games": await _viewModel.RescanCommand.ExecuteAsync(null); break;
            case "Scan report":
            {
                var action = await PadMenu.ShowAsync(_hostGrid, "SCAN REPORT", _viewModel.ScanReport, "Copy report", "Close");
                if (action == "Copy report")
                {
                    await Clipboard.Default.SetTextAsync(_viewModel.ScanReport);
                    _viewModel.Status = "SCAN REPORT COPIED";
                }
                break;
            }
            case "HaX mode: OFF":
                var on = await PadMenu.ConfirmAsync(_hostGrid, "TURN ON HAX MODE?",
                    "Pickers will offer every option instead of the legal subset (any ability on any mon). " +
                    "Use at your own risk: mons edited this way will show as illegal.", "Turn on");
                if (on) { Services.HaXMode.Set(true); _viewModel.Status = "HaX mode is ON."; }
                break;
            case "HaX mode: ON":
                Services.HaXMode.Set(false);
                _viewModel.Status = "HaX mode is OFF.";
                break;
            case "Hardcore mode: OFF":
                if (await PadMenu.ConfirmAsync(_hostGrid, "TURN ON HARDCORE MODE?", Services.HardcoreMode.SettingExplanation, "Turn on"))
                {
                    Services.HardcoreMode.Set(true);
                    _viewModel.Status = $"{Services.HardcoreMode.Marker}: Hardcore mode is ON - moves only, no edits or copies.";
                }
                break;
            case "Hardcore mode: ON":
                Services.HardcoreMode.Set(false);
                _viewModel.Status = "Hardcore mode is OFF.";
                break;
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
        else if (_viewModel.Status.StartsWith("Could not", StringComparison.Ordinal))
            await PadMenu.ShowAsync(_hostGrid, "SAVE COULD NOT OPEN", _viewModel.Status, "OK");
    }

    /// <summary>
    /// A game as an actual cartridge: fixed uniform size, dark contact strip on top,
    /// and the game art as the cart's label sticker (era-colored plastic behind it).
    /// </summary>
    private const double TilePadding = 8;
    private const double TileInnerWidth = 118;

    private View BuildCartridgeTile()
    {
        const double cartWidth = 76;
        const double cartHeight = 84;

        var contactStrip = new BoxView { HeightRequest = 9, Color = UiTokens.MaroonDeep };

        var gameMark = new GameCartridgeMark();
        gameMark.SetBinding(GameCartridgeMark.GenerationProperty, nameof(DetectedSave.Generation));

        // Use the real game label art whenever we bundle it. The identity mark remains a
        // deliberate, consistent fallback for homebrew, hacks, and unknown saves.
        var gameArt = new Image
        {
            Aspect = Aspect.AspectFit,
            IsVisible = false,
            Margin = new Thickness(3),
        };
        gameArt.BindingContextChanged += async (_, _) =>
        {
            if (gameArt.BindingContext is not SaveCard group) return;
            gameArt.IsVisible = false;
            gameMark.IsVisible = true;
            // Box art follows the game, never the custom name; unnamed hacks keep the mark.
            var path = group.Save.ArtLabel is { } art ? await GameArt.GetIconAsync(art) : null;
            // A recycled template may have moved on while package I/O was in flight.
            if (!ReferenceEquals(gameArt.BindingContext, group)) return;
            gameArt.Source = path;
            gameArt.IsVisible = path is not null;
            gameMark.IsVisible = path is null;
        };
        var sticker = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Margin = new Thickness(7, 5, 7, 7),
            Content = new Grid { Children = { gameMark, gameArt } },
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
        // The plastic is the player's chosen color, else the console era's.
        cartBody.SetBinding(BackgroundColorProperty, new Binding(".", converter: CartColor));
        cartBody.SetBinding(Border.StrokeProperty, new Binding(".", converter: CartEdge));

        var iconHost = new Grid { Children = { cartBody } };

        // Every line gets the tile's full inner width so long names end in an ellipsis
        // instead of being clipped on both sides of a centred run.
        Label ShelfLine(double size, Color color, bool bold = false) => new()
        {
            TextColor = color,
            FontSize = size,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            WidthRequest = TileInnerWidth,
            // The pixel font's first glyph overhangs its advance; keep it off the clip edge.
            Padding = new Thickness(3, 0),
        };

        var name = ShelfLine(13, UiTokens.Ink0, bold: true);
        name.SetBinding(Label.TextProperty, nameof(SaveCard.ShelfTitle));

        var trainer = ShelfLine(10, UiTokens.Ink1);
        trainer.SetBinding(Label.TextProperty, nameof(SaveCard.TrainerLine));

        // Emulator · folder · date: what tells two saves of one game apart at a glance.
        var detail = ShelfLine(9, UiTokens.Ink1);
        detail.SetBinding(Label.TextProperty, nameof(SaveCard.DetailLine));

        // Every tile is exactly the same size: the shelf must read as a row of carts.
        // A shared cartridge (the same game in several files) carries its save count,
        // tucked on the cart's lower corner like a sticker.
        var countLabel = new Label
        {
            TextColor = UiTokens.Ink0,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        countLabel.SetBinding(Label.TextProperty, nameof(SaveCard.CountBadge));
        var countBadge = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.SelectBorder,
            StrokeThickness = 1.5,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            MinimumWidthRequest = 20,
            HeightRequest = 20,
            Padding = new Thickness(5, 0),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, -8, -4),
            InputTransparent = true,
            Content = countLabel,
        };
        countBadge.SetBinding(IsVisibleProperty, nameof(SaveCard.HasSeveralSaves));
        iconHost.Children.Add(countBadge);

        var card = Kit.DevicePanel(new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { iconHost, name, trainer, detail },
        }, padding: TilePadding);
        card.WidthRequest = TileInnerWidth + 2 * TilePadding;
        card.HeightRequest = 162;

        card.Triggers.Add(new DataTrigger(typeof(Border))
        {
            Binding = new Binding(nameof(SaveCard.IsSelected)),
            Value = true,
            Setters =
            {
                new Setter { Property = Border.StrokeProperty, Value = UiTokens.SelectBorder },
                new Setter { Property = Border.StrokeThicknessProperty, Value = 3.0 },
            },
        });
        void Select(SaveCard group)
        {
            _shelfIndex = _viewModel.Groups.IndexOf(group);
            for (var i = 0; i < _viewModel.Groups.Count; i++)
                _viewModel.Groups[i].IsSelected = i == _shelfIndex;
        }

        // Long-press (held ~0.5 s) opens the identity menu; the tap that follows the
        // release is swallowed so the save does not also open. MAUI's pointer
        // recognizer never sees touch once a tap recognizer owns the view on Android,
        // so the hold is timed from the native touch stream it shares with the tap.
        var longPressFired = false;
        AttachLongPress(card, () =>
        {
            if (card.BindingContext is not SaveCard group) return;
            longPressFired = true;
            Select(group);
            _ = ShowSaveMenuAsync(group);
        });

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (longPressFired) { longPressFired = false; return; }
            if (card.BindingContext is not SaveCard group) return;
            Select(group);
            await OpenCardAsync(group);
        };
        card.GestureRecognizers.Add(tap);
        BlockNativeFocus(card);
        return card;
    }

    /// <summary>The events shelf: community collections here, wondercards inside the game.</summary>
    private async Task ShowEventsMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "EVENT DATABASE", null,
            new PadOption($"Community boxes ({Services.CommunityBoxService.RepoTitle})", IconPath: "community"),
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

    private static readonly IValueConverter CartColor = new FuncConverter(card =>
        card is SaveCard c ? SaveColors.For(c.ColorKey, c.Generation) : Kit.EraColor(0));
    private static readonly IValueConverter CartEdge = new FuncConverter(card =>
        (card is SaveCard c ? SaveColors.For(c.ColorKey, c.Generation) : Kit.EraColor(0)).AddLuminosity(-0.15f));

    private sealed class FuncConverter(Func<object?, Color> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private async Task OpenSaveAsync(DetectedSave save)
    {
        if (!await ChooseEditionOnceAsync(save)) return;
        // The pick re-resolves the save: open the version that carries the chosen edition.
        save = _viewModel.Saves.FirstOrDefault(s => s.DocumentId == save.DocumentId) ?? save;
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
        else if (_viewModel.Status.StartsWith("Could not", StringComparison.Ordinal))
            await PadMenu.ShowAsync(_hostGrid, "SAVE COULD NOT OPEN", _viewModel.Status, "OK");
    }

    private async Task PushAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }

    private async Task PushParkAsync()
    {
        if (Interlocked.Exchange(ref _parkNavigationPending, 1) != 0) return;
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("MAUI services are unavailable.");
            _parkPage ??= services.GetRequiredService<PokeparkPage>();
            await Navigation.PushAsync(_parkPage);
        }
        finally
        {
            Volatile.Write(ref _parkNavigationPending, 0);
        }
    }
}

internal static class ViewBuilderExtensions
{
    public static T Also<T>(this T view, Action<T> configure) where T : View
    {
        configure(view);
        return view;
    }
}
