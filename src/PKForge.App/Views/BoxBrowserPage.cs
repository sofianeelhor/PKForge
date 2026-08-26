using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The storage screen, composed like a console UI: thin status strip on top,
/// square box grid left, info/editor panel right, button-hint bar at the bottom.
/// Landscape-only - this app is designed for the AYN Thor, not a phone.
/// </summary>
public sealed class BoxBrowserPage : ContentPage, IPadHandler
{
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService _sprites;
    private readonly ThemeService _theme;
    private readonly SKCanvasView _canvas;
    private readonly SKCanvasView _boxBar;
    private readonly FrameInvalidator _frame;
    private Grid _hostGrid = null!;

    public BoxBrowserPage(BoxBrowserViewModel viewModel, ISpriteService sprites, ThemeService theme)
    {
        _viewModel = viewModel;
        _sprites = sprites;
        _theme = theme;
        BindingContext = viewModel;
        Title = "Storage";
        NavigationPage.SetHasNavigationBar(this, false);

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _frame = new FrameInvalidator(_canvas);
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        // The PKSM box-name bar rides above the grid: cream strip, yellow chevron caps.
        _boxBar = new SKCanvasView { HeightRequest = 26, InputTransparent = true, Margin = new Thickness(2, 0, 2, 4) };
        _boxBar.PaintSurface += PaintBoxBar;

        var screenBody = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { _boxBar, _canvas },
        };
        Grid.SetRow(_canvas, 1);

        var screen = Kit.LcdPanel(screenBody, padding: 4);
        // The frame and its padding wear the current box wallpaper - no leftover default corners.
        void TintScreen()
        {
            var (_, frame) = BoxGridRenderer.HueFor(_viewModel.BoxIndex);
            screen.BackgroundColor = UiTokens.Wallpaper(_viewModel.BoxIndex);
            screen.Stroke = frame;
        }
        TintScreen();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.BoxIndex) or nameof(BoxBrowserViewModel.Save))
                TintScreen();
        };

        var sidePanel = BuildSidePanel();

        // DS chrome around the box grid + editor.
        var content = new Grid
        {
            Padding = new Thickness(12, 10),
            ColumnSpacing = 12,
            ColumnDefinitions = [new(GridLength.Star), new(new GridLength(330))],
            Children = { screen, sidePanel },
        };
        Grid.SetColumn(sidePanel, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), content } };

        var title = string.IsNullOrEmpty(_viewModel.ConnectedName) ? "Storage" : _viewModel.ConnectedName;
        var footer = DsChrome.Footer(
            ("A", "Grab", null),
            ("B", "Back", () => _ = Navigation.PopAsync()),
            ("LR", "Box", null),
            ("X", "Tools", () => _ = ShowToolsAsync()),
            ("Y", "Save data", () => _ = ShowSaveDataAsync()),
            ("+", "Menu", () => OpenCursorMenu()));

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip(title, "Connected"), bodyHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(footer, 3);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Save) or nameof(BoxBrowserViewModel.BoxIndex)
                or nameof(BoxBrowserViewModel.SelectedSlot) or nameof(BoxBrowserViewModel.VisibleSlots))
            {
                _canvas.InvalidateSurface();
                _boxBar.InvalidateSurface();
            }
        };
        theme.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ThemeService.SkAccent))
                _canvas.InvalidateSurface();
        };
    }

    /// <summary>Draws the box-name bar: cream bar, label, yellow chevron caps when pages exist.</summary>
    private void PaintBoxBar(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var font = new SKFont(PixelTypeface(), 20);
        PksmPaint.BoxNameBar(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height),
            _viewModel.BoxIndex == -1 ? "PARTY" : $"BOX {_viewModel.BoxIndex + 1:00}", font,
            canPrev: _viewModel.BoxIndex > 0,
            canNext: _viewModel.BoxIndex < _viewModel.BoxCount - 1);
    }

    /// <summary>The NDS12 face (the chrome's PixelUI voice), cached once for Skia text.</summary>
    private static SKTypeface _pixelTypeface = null!;

    private static SKTypeface PixelTypeface()
    {
        if (_pixelTypeface is not null) return _pixelTypeface;
        try
        {
            // Bundled font copied to cache once (the ball-icon pattern), then opened by path.
            var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "NDS12.ttf");
            if (!File.Exists(cache))
            {
                using var asset = FileSystem.OpenAppPackageFileAsync("NDS12.ttf").GetAwaiter().GetResult();
                using var output = File.Create(cache);
                asset.CopyTo(output);
            }
            _pixelTypeface = SKTypeface.FromFile(cache);
        }
        catch
        {
            // ignored: fall back to the bold system face below
        }
        _pixelTypeface ??= SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        return _pixelTypeface;
    }


    private View BuildSidePanel()
    {
        // The maroon header strip carries the selected mon's name (the Gen-5 section header).
        var header = (Border)Kit.HeaderBar("Pokémon");
        var headerLabel = (Label)header.Content!;
        headerLabel.SetBinding(Label.TextProperty, new Binding(nameof(BoxBrowserViewModel.Selected), converter: new MonHeaderConverter()));

        // Legality verdict rides the header: the verdict must never scroll or clip away.
        var badge = new Label { FontFamily = DsChrome.PixelFont, FontSize = 16, VerticalTextAlignment = TextAlignment.Center };
        badge.SetBinding(Label.TextProperty, nameof(BoxBrowserViewModel.LegalityBadge));
        badge.SetBinding(Label.TextColorProperty, nameof(BoxBrowserViewModel.LegalityBadge), converter: new LegalityColorConverter());

        // Box paging beside the header (the box-name bar above the grid shows the number).
        var previous = Kit.MiniCapsule("<", UiTokens.Ink0);
        previous.HeightRequest = 32;
        previous.Clicked += (_, _) => _viewModel.PreviousBox();
        var next = Kit.MiniCapsule(">", UiTokens.Ink0);
        next.HeightRequest = 32;
        next.Clicked += (_, _) => _viewModel.NextBox();

        var headerRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { header, badge, previous, next },
        };
        Grid.SetColumn(badge, 1);
        Grid.SetColumn(previous, 2);
        Grid.SetColumn(next, 3);

        // Idle card until a Pokémon is selected; the editor replaces it.
        var idle = new VerticalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                PksmIcons.Icon("storage", 44),
                new Label { Text = "Select a Pokémon", TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = 15 },
                new Label { Text = "Tap an empty slot to add one", TextColor = UiTokens.Ink1, FontSize = 11, HorizontalTextAlignment = TextAlignment.Center },
            },
        };

        var editor = new ScrollView { Content = BuildEditor(), IsVisible = false };

        void SwapPanels()
        {
            var hasSelection = _viewModel.Selected is { IsEmpty: false };
            editor.IsVisible = hasSelection;
            idle.IsVisible = !hasSelection;
        }
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Selected))
                SwapPanels();
        };
        SwapPanels();

        var body = new Grid { Children = { idle, editor } };

        var layout = new Grid
        {
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { headerRow, body },
        };
        Grid.SetRow(body, 1);
        return Kit.DevicePanel(layout, padding: 10);
    }

    private async Task ShowToolsAsync()
    {
        if (_viewModel.SelectMode)
        {
            await ShowOrganizerMenuAsync();
            return;
        }
        var choice = await PadMenu.ShowAsync(_hostGrid, "STORAGE TOOLS", null,
            new PadOption("Organizer (multi-select)", IconPath: "storage"),
            new PadOption("Import .pk files", IconPath: "folder"),
            new PadOption("Import Showdown team", IconPath: "script"),
            new PadOption("Export box to Showdown", IconPath: "script"),
            new PadOption("Generate Living Dex", IconPath: "pokedex"),
            new PadOption("Batch editor", IconPath: "script"));
        switch (choice)
        {
            case "Organizer (multi-select)":
                _viewModel.EnterSelectMode();
                _canvas.InvalidateSurface();
                return;
            case "Import .pk files":
                await BulkImportAsync();
                return;
            case "Import Showdown team":
                await ImportShowdownTeamAsync();
                return;
            case "Export box to Showdown":
                await ExportBoxShowdownAsync();
                return;
            case "Generate Living Dex":
                await GenerateLivingDexAsync();
                return;
            case "Batch editor":
                _viewModel.Status = "BATCH EDITOR - in development.";
                return;
        }
    }

    /// <summary>Bulk actions for the organizer's marked selection.</summary>
    private async Task ShowOrganizerMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, $"ORGANIZER · {_viewModel.MarkedCount} MARKED", null,
            new PadOption("Move selection to box…", IconPath: "storage"),
            new PadOption("Move selection to another game…", IconPath: "storage"),
            new PadOption("Duplicate selection", IconPath: "storage"),
            new PadOption("Move selection to Bank", IconPath: "bank"),
            new PadOption("Export selection (.pk files)", IconPath: "folder"),
            new PadOption("Release selection", IconPath: "hex"),
            new PadOption("Done (exit organizer)", IconPath: "settings"));
        switch (choice)
        {
            case "Move selection to box…":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var boxes = Enumerable.Range(1, _viewModel.BoxCount).Select(n => $"Box {n}").ToArray();
                var target = await PadMenu.ShowAsync(_hostGrid, "MOVE TO WHICH BOX?", null, boxes);
                if (target is null) return;
                var boxIndex = Array.IndexOf(boxes, target);
                await _viewModel.BulkMoveAsync(boxIndex);
                _canvas.InvalidateSurface();
                return;
            }
            case "Move selection to another game…":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var session = _sessionsFor();
                var picker = IPlatformApplication.Current?.Services.GetService<SavePickerViewModel>();
                var transfer = IPlatformApplication.Current?.Services.GetService<Services.TransferService>();
                if (session is null || picker is null || transfer is null) return;

                var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
                var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
                    "MOVE SELECTION TO GAME", $"{_viewModel.MarkedCount} Pokémon leave this box", currentDoc);
                if (target is null) return;

                var confirm = await PadMenu.ConfirmAsync(_hostGrid, "MOVE SELECTION?",
                    $"{_viewModel.MarkedCount} Pokémon will leave this box and join {target.GameLabel}. Mons that cannot enter that format stay here.", "Move all");
                if (!confirm) return;

                var sentSlots = new List<(int Box, int Slot)>();
                var skipped = 0;
                foreach (var (box, markedSlot) in _viewModel.MarkedSlots.ToArray())
                {
                    var export = session.ExportSlot(box, markedSlot);
                    var outcome = await transfer.SendToGameAsync(export.Data, export.FileName, target);
                    if (outcome.Success) sentSlots.Add((box, markedSlot));
                    else skipped++;
                }
                if (sentSlots.Count == 0)
                {
                    _viewModel.Status = skipped > 0 ? $"No Pokémon could enter {target.GameLabel}'s format." : "Transfer failed.";
                    return;
                }
                // Only the mons that actually arrived leave this save; the rest stay marked.
                var moved = await _viewModel.BulkReleaseAsync(sentSlots);
                _viewModel.Status = skipped > 0
                    ? $"Moved {sentSlots.Count} to {target.GameLabel}; {skipped} could not enter that format and stayed."
                    : $"Moved {sentSlots.Count} Pokémon to {target.GameLabel}.";
                _canvas.InvalidateSurface();
                return;
            }
            case "Duplicate selection":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var session = _sessionsFor();
                if (session is null) return;
                var exports = _viewModel.MarkedSlots.Select(m => session.ExportSlot(m.Box, m.Slot).Data).ToList();
                var used = new HashSet<(int Box, int Slot)>();
                await _viewModel.RunMutationAsync(s =>
                {
                    var cloned = 0;
                    foreach (var data in exports)
                    {
                        (int Box, int Slot)? landing = null;
                        foreach (var cand in _viewModel.Save!.Slots.Where(x => x.Box >= 0 && x.Species is null))
                        {
                            if (used.Contains((cand.Box, cand.Slot))) continue;
                            if (!s.ReadEntity(cand.Box, cand.Slot).IsEmpty) continue; // live check: never overwrite
                            landing = (cand.Box, cand.Slot);
                            break;
                        }
                        if (landing is null) break;
                        if (s.ImportSlot(landing.Value.Box, landing.Value.Slot, data))
                        {
                            used.Add(landing.Value);
                            cloned++;
                        }
                    }
                    return new GenerationOutcome(cloned > 0,
                        cloned == 0 ? "No room to clone."
                        : $"Cloned {cloned} Pokémon." + (cloned < exports.Count ? $" {exports.Count - cloned} left (no room)." : ""));
                }, Math.Max(0, _viewModel.SelectedSlot));
                _canvas.InvalidateSurface();
                return;
            }
            case "Move selection to Bank":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var session = _sessionsFor();
                var bank = IPlatformApplication.Current?.Services.GetService<IBankService>();
                var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
                if (session is null || bank is null || engine is null) return;

                // Capture the bytes first, then empty all slots in one safe write, then deposit.
                var deposits = new List<(byte[] Data, BankEntryInfo Info)>();
                foreach (var (box, markedSlot) in _viewModel.MarkedSlots)
                {
                    var export = session.ExportSlot(box, markedSlot);
                    var info = engine.TryDescribeEntity(export.Data, _viewModel.ConnectedName);
                    if (info is not null) deposits.Add((export.Data, info));
                }
                var moved = await _viewModel.BulkReleaseAsync();
                if (moved)
                {
                    foreach (var (data, info) in deposits)
                        bank.Add(data, info);
                    _viewModel.Status = $"Deposited {deposits.Count} Pokémon in the Bank.";
                }
                _canvas.InvalidateSurface();
                return;
            }
            case "Export selection (.pk files)":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var directory = System.IO.Path.Combine(FileSystem.CacheDirectory, "export");
                Directory.CreateDirectory(directory);
                var paths = _viewModel.BulkExport(directory);
                if (paths.Count == 0) return;
                await Share.Default.RequestAsync(new ShareMultipleFilesRequest
                {
                    Title = $"{paths.Count} Pokémon",
                    Files = paths.Select(p => new ShareFile(p)).ToList(),
                });
                _viewModel.Status = $"Exported {paths.Count} Pokémon.";
                _viewModel.ExitSelectMode();
                _canvas.InvalidateSurface();
                return;
            }
            case "Release selection":
            {
                if (_viewModel.MarkedCount == 0) { _viewModel.Status = "Nothing marked."; return; }
                var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "RELEASE SELECTION?",
                    $"Release all {_viewModel.MarkedCount} marked Pokémon? The current save state is kept as a restore point.",
                    "Release all");
                if (!confirmed) return;
                await _viewModel.BulkReleaseAsync();
                _canvas.InvalidateSurface();
                return;
            }
            case "Done (exit organizer)":
                _viewModel.ExitSelectMode();
                _canvas.InvalidateSurface();
                return;
        }
    }

    /// <summary>Multi-pick .pk files and import them into this box's empty slots (one write).</summary>
    private async Task BulkImportAsync()
    {
        var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
        var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
        if (picker is null || access is null) return;

        var documents = await picker.PickManyAsync();
        if (documents.Count == 0) return;

        var payloads = new List<byte[]>();
        foreach (var document in documents)
            payloads.Add((await access.ReadAsync(document.DocumentId)).ToArray());

        await _viewModel.RunMutationAsync(session =>
        {
            var empties = new Queue<int>(_viewModel.VisibleSlots.Where(s => s.Species is null).Select(s => s.Slot));
            var imported = 0;
            var failed = 0;
            foreach (var bytes in payloads)
            {
                if (empties.Count == 0) break;
                if (session.ImportSlot(_viewModel.BoxIndex, empties.Peek(), bytes)) { empties.Dequeue(); imported++; }
                else failed++;
            }
            return new GenerationOutcome(imported > 0,
                $"Imported {imported} Pokémon." + (failed > 0 ? $" {failed} file(s) not recognized." : ""));
        }, Math.Max(0, _viewModel.SelectedSlot));
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>Paste a full Showdown team; each set is legalized into the next empty slot.</summary>
    private async Task ImportShowdownTeamAsync()
    {
        var text = await TextPopup.ShowAsync(_hostGrid, "IMPORT SHOWDOWN TEAM",
            "Paste the whole team (sets separated by blank lines).");
        if (string.IsNullOrWhiteSpace(text)) return;
        var sets = text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var empties = new Queue<int>(_viewModel.VisibleSlots.Where(s => s.Species is null).Select(s => s.Slot));

        var overlay = LoadingOverlay.Show(_hostGrid, "BUILDING YOUR TEAM…",
            $"Legalizing {sets.Length} set(s) offline.");
        try
        {
            var done = 0;
            foreach (var set in sets)
            {
                if (empties.Count == 0 || overlay.Cancellation.IsCancellationRequested) break;
                var slot = empties.Dequeue();
                var ok = await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, set), slot);
                if (!ok) empties.Enqueue(slot); // slot stays free for the next set
                done++;
                overlay.Report(done, sets.Length);
            }
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>Every mon in the current box as one Showdown text: copy or share.</summary>
    private async Task ExportBoxShowdownAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var sets = _viewModel.VisibleSlots
            .Where(s => s.Species is not null)
            .Select(s => session.GetShowdownText(_viewModel.BoxIndex, s.Slot));
        var text = string.Join("\n\n", sets);
        if (text.Length == 0) { _viewModel.Status = "This box is empty."; return; }
        var choice = await PadMenu.ShowAsync(_hostGrid, "BOX AS SHOWDOWN TEAM", null, "Copy to clipboard", "Share as file", "Close");
        switch (choice)
        {
            case "Copy to clipboard":
                await Clipboard.Default.SetTextAsync(text);
                _viewModel.Status = "Box copied as Showdown text.";
                return;
            case "Share as file":
                var path = System.IO.Path.Combine(FileSystem.CacheDirectory, $"box-{_viewModel.BoxIndex + 1}.txt");
                await File.WriteAllTextAsync(path, text);
                await Share.Default.RequestAsync(new ShareFileRequest { Title = "Showdown team", File = new ShareFile(path) });
                return;
        }
    }

    /// <summary>Fills the entire PC with a legal living dex (explicitly destructive; heavily confirmed).</summary>
    private async Task GenerateLivingDexAsync()
    {
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "GENERATE LIVING DEX?",
            "This OVERWRITES every box with one legal Pokémon of every species this game supports. " +
            "The current save state is kept as a restore point. This can take several minutes.",
            "Fill my boxes");
        if (!confirmed) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "BUILDING THE LIVING DEX…",
            "The offline legalizer is generating one of everything. Feel free to admire the walking Pokémon.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) =>
                legalizer.FillLivingDex(s, overlay.Report, overlay.Cancellation.Token), Math.Max(0, _viewModel.SelectedSlot));
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    private async Task ShowSaveDataAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "SAVE DATA", null,
            new PadOption("Trainer card", IconPath: "trainer"),
            new PadOption("Bag & items", IconPath: "bag"),
            new PadOption("Pokédex", IconPath: "pokedex"),
            new PadOption("Wonder cards", IconPath: "events"),
            new PadOption("Restore points", IconPath: "credits"));
        switch (choice)
        {
            case "Trainer card": await ShowTrainerCardAsync(); return;
            case "Bag & items": await ShowBagAsync(); return;
            case "Pokédex": await ShowDexMenuAsync(); return;
            case "Wonder cards":
            {
                var session = _sessionsFor();
                if (session is null) return;
                await EventGallery.ShowAsync(_hostGrid, _viewModel, session, targetSlot: null, () => _canvas.InvalidateSurface());
                return;
            }
            case "Restore points": await PushAsync<BackupHistoryPage>(); return;
        }
    }

    /// <summary>Trainer card: view + edit name, IDs, money, gender - written safely like everything.</summary>
    private async Task ShowTrainerCardAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var trainer = session.GetTrainer();

        var updated = await TrainerCardPopup.ShowAsync(_hostGrid, trainer);
        if (updated is null || updated == trainer) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.SetTrainer(updated);
            return new GenerationOutcome(true, "Trainer card updated.");
        }, Math.Max(0, _viewModel.SelectedSlot));
    }

    /// <summary>Bag: the navy inventory editor - pocket pills, item rows with count discs.</summary>
    private async Task ShowBagAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        await BagEditor.ShowAsync(_hostGrid, session, _viewModel, data);
    }

    /// <summary>
    /// The bag editor overlay: the navy inventory world. Pockets are bag pills (cyan;
    /// yellow-green rim and gold fill when active), items are white PixelUI rows with
    /// round count discs. Tap a name for the exact-count sheet; L/R turn pockets,
    /// up/down walk rows, A activates, B closes.
    /// </summary>
    private sealed class BagEditor : IPadHandler
    {
        private readonly Grid _host;
        private readonly ISaveEngineSession _session;
        private readonly BoxBrowserViewModel _viewModel;
        private readonly IGameDataService _data;
        private readonly ScrollView _scroll;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FlexLayout _pockets = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        private readonly VerticalStackLayout _rows = new() { Spacing = 4 };
        private readonly int _slotSeed;

        private IReadOnlyList<BagPouch> _bag = [];
        private List<BagRow> _itemRows = [];
        private AddRow _addRow = null!;
        private Grid _overlay = null!;
        private int _pouchIndex;
        private int _cursor;

        public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, IGameDataService data)
        {
            var editor = new BagEditor(host, session, viewModel, data, Math.Max(0, viewModel.SelectedSlot));
            await editor.RunAsync();
        }

        private BagEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, IGameDataService data, int slotSeed)
        {
            _host = host;
            _session = session;
            _viewModel = viewModel;
            _data = data;
            _slotSeed = slotSeed;

            var title = new HorizontalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    PksmIcons.Icon("bag", 22, PksmIcons.White),
                    new Label
                    {
                        Text = "BAG",
                        FontFamily = DsChrome.PixelFont,
                        FontSize = 18,
                        TextColor = UiTokens.Paper,
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            };

            _scroll = new ScrollView { Content = _rows };

            var hint = new Label
            {
                Text = "TAP + OR - TO ADJUST · TAP THE NAME FOR AN EXACT COUNT",
                FontFamily = DsChrome.PixelFont,
                FontSize = 11,
                TextColor = UiTokens.BagCyan,
            };

            var body = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children = { title, _pockets, _scroll, hint },
            };
            Grid.SetRow(_pockets, 1);
            Grid.SetRow(_scroll, 2);
            Grid.SetRow(hint, 3);

            var window = new Border
            {
                BackgroundColor = UiTokens.BagNavy,
                Stroke = UiTokens.BagNavyDeep,
                StrokeThickness = 2,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Padding = 14,
                WidthRequest = Math.Min(host.Width > 0 ? host.Width - 24 : 560, 560),
                HeightRequest = host.Height > 0 ? host.Height - 24 : 340,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Content = body,
            };
            _overlay = Kit.AttachOverlay(host, window, Close);
            Kit.AnimateIn(window);
        }

        private async Task RunAsync()
        {
            _bag = _session.GetBag();
            if (_bag.Count == 0)
            {
                _viewModel.Status = "This game exposes no editable bag.";
                Close();
                return;
            }
            Rebuild();
            IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
            try
            {
                await _closed.Task;
            }
            finally
            {
                IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
            }
        }

        private string ItemName(int id) =>
            id < _data.ItemNames.Count && _data.ItemNames[id].Length > 0 ? _data.ItemNames[id] : $"#{id}";

        /// <summary>Re-reads the bag and rebuilds pills + rows (after every write or pouch switch).</summary>
        private void Rebuild()
        {
            _bag = _session.GetBag();
            if (_pouchIndex >= _bag.Count) _pouchIndex = 0;
            var pouch = _bag[_pouchIndex];

            _pockets.Children.Clear();
            for (var i = 0; i < _bag.Count; i++)
            {
                var index = i;
                var pill = new BagPillTab($"{_bag[i].Name.ToUpperInvariant()} ({_bag[i].Items.Count})")
                {
                    Tapped = () => { _pouchIndex = index; _cursor = 0; Rebuild(); },
                };
                pill.Selected = i == _pouchIndex;
                _pockets.Children.Add(pill);
            }

            _rows.Children.Clear();
            _itemRows = [];
            var icons = new List<Image>(pouch.Items.Count);
            foreach (var item in pouch.Items)
            {
                var captured = item;
                var icon = new Image { WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
                icons.Add(icon);
                var row = new BagRow(icon, ItemName(item.Id), item.Count)
                {
                    Tapped = () => _ = EditCountAsync(captured),
                    Minus = () => _ = NudgeAsync(captured, -1),
                    Plus = () => _ = NudgeAsync(captured, +1),
                };
                _itemRows.Add(row);
                _rows.Children.Add(row);
            }

            _addRow = new AddRow { Tapped = () => _ = AddItemAsync() };
            _rows.Children.Add(_addRow);

            Highlight(_cursor);
            _ = LoadIconsAsync(pouch.Items, icons);
        }

        private async Task LoadIconsAsync(IReadOnlyList<BagItem> items, IReadOnlyList<Image> targets)
        {
            var paths = await Task.WhenAll(items.Select(i => ItemArt.GetAsync(ItemName(i.Id))));
            for (var i = 0; i < targets.Count && i < paths.Length; i++)
                if (paths[i] is { } path)
                    targets[i].Source = ImageSource.FromFile(path);
        }

        private async Task EditCountAsync(BagItem item)
        {
            var name = ItemName(item.Id);
            var count = await StatsPopup.ShowSingleAsync(_host, $"{name.ToUpperInvariant()} - QUANTITY", item.Count, 999);
            if (count is null) return;
            await WriteAsync(item.Id, count.Value);
        }

        private async Task NudgeAsync(BagItem item, int delta)
        {
            var count = Math.Clamp(item.Count + delta, 0, 999);
            if (count == item.Count) return;
            await WriteAsync(item.Id, count);
        }

        /// <summary>One safe write (backup + atomic), then a fresh read of the whole bag.</summary>
        private async Task WriteAsync(int itemId, int count)
        {
            var pouchName = _bag[_pouchIndex].Name;
            var name = ItemName(itemId);
            await _viewModel.RunMutationAsync(s =>
            {
                s.SetItemCount(pouchName, itemId, count);
                return new GenerationOutcome(true, count == 0 ? $"{name} removed." : $"{name} ×{count}");
            }, _slotSeed);
            Rebuild();
        }

        private async Task AddItemAsync()
        {
            var pouchName = _bag[_pouchIndex].Name;
            var legalIds = _session.GetPouchLegalItems(pouchName)
                .Where(id => id < _data.ItemNames.Count && _data.ItemNames[id].Length > 0)
                .ToList();
            if (legalIds.Count == 0) { _viewModel.Status = "No item list for this pouch."; return; }

            // Fetch icons up-front for reasonable lists; huge lists warm their tail in the background.
            var itemDirectory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
            var missing = legalIds.Where(id => !File.Exists(System.IO.Path.Combine(itemDirectory, ItemArt.Slug(_data.ItemNames[id]) + ".png"))).ToList();
            if (missing.Count > 0)
            {
                LoadingOverlay? fetchOverlay = missing.Count > 25
                    ? LoadingOverlay.Show(_host, "FETCHING ITEM SPRITES…", "One time only for this pouch.")
                    : null;
                try
                {
                    var head = missing.Take(160).ToList();
                    var fetched = 0;
                    foreach (var chunk in head.Chunk(8))
                    {
                        if (fetchOverlay?.Cancellation.IsCancellationRequested == true) break;
                        await Task.WhenAll(chunk.Select(id => ItemArt.GetAsync(_data.ItemNames[id])));
                        fetched += chunk.Length;
                        fetchOverlay?.Report(fetched, head.Count);
                    }
                    var tail = missing.Skip(160).ToList();
                    if (tail.Count > 0)
                        _ = Task.Run(async () => { foreach (var id in tail) await ItemArt.GetAsync(_data.ItemNames[id]); });
                }
                finally
                {
                    fetchOverlay?.Close();
                }
            }

            var legal = legalIds.Select(id =>
            {
                var cached = System.IO.Path.Combine(itemDirectory, ItemArt.Slug(_data.ItemNames[id]) + ".png");
                return new PickItem(id, _data.ItemNames[id], File.Exists(cached) ? cached : null);
            }).ToList();
            var picked = await PickerMenu.ShowAsync(_host, "ADD ITEM", legal);
            if (picked is null) return;
            var count = await StatsPopup.ShowSingleAsync(_host, $"{ItemName(picked.Id).ToUpperInvariant()} - QUANTITY", 0, 999);
            if (count is null) return;
            await WriteAsync(picked.Id, count.Value);
        }

        private void Highlight(int index)
        {
            _cursor = Math.Clamp(index, 0, _itemRows.Count);
            for (var i = 0; i < _itemRows.Count; i++)
                _itemRows[i].Selected = i == _cursor;
            _addRow.Selected = _cursor == _itemRows.Count;
            var target = _cursor < _itemRows.Count ? (View)_itemRows[_cursor] : _addRow;
            _ = _scroll.ScrollToAsync(target, ScrollToPosition.MakeVisible, false);
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up:
                    Highlight(_cursor - 1);
                    return true;
                case PadButton.Down:
                    Highlight(_cursor + 1);
                    return true;
                case PadButton.Left or PadButton.L:
                    _pouchIndex = (_pouchIndex + _bag.Count - 1) % _bag.Count;
                    _cursor = 0;
                    Rebuild();
                    return true;
                case PadButton.Right or PadButton.R:
                    _pouchIndex = (_pouchIndex + 1) % _bag.Count;
                    _cursor = 0;
                    Rebuild();
                    return true;
                case PadButton.A:
                    if (_cursor < _itemRows.Count)
                        _ = EditCountAsync(_bag[_pouchIndex].Items[_cursor]);
                    else
                        _ = AddItemAsync();
                    return true;
                case PadButton.B:
                    Close();
                    return true;
                default:
                    return true; // modal while open
            }
        }

        private void Close()
        {
            _host.Remove(_overlay);
            _closed.TrySetResult();
        }

        // ── Row chrome, drawn locally in Skia from Pksm tokens ─────────────────

        /// <summary>A pocket tab: the bag pill (cyan idle; yellow-green rim + gold fill active).</summary>
        private sealed class BagPillTab : Grid
        {
            private readonly SKCanvasView _bg;
            private bool _selected;

            public Action? Tapped { get; set; }

            public BagPillTab(string label)
            {
                HeightRequest = 30;
                Margin = new Thickness(0, 0, 6, 6);
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) =>
                    PksmPaint.BagPill(args.Surface.Canvas, new SKRect(0, 2, args.Info.Width, args.Info.Height - 2), _selected);
                Children.Add(_bg);
                Children.Add(new Label
                {
                    Text = label,
                    FontFamily = DsChrome.PixelFont,
                    FontSize = 12,
                    TextColor = UiTokens.Paper,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(14, 0),
                    InputTransparent = true,
                });
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }
        }

        /// <summary>One inventory row: item sprite, white PixelUI name, count, count discs.</summary>
        private sealed class BagRow : Grid
        {
            private readonly SKCanvasView _bg;
            private bool _selected;

            public Action? Tapped { get; set; }
            public Action? Minus { get; set; }
            public Action? Plus { get; set; }

            public BagRow(Image icon, string name, int count)
            {
                HeightRequest = 40;
                ColumnDefinitions =
                [
                    new(new GridLength(32)),
                    new(GridLength.Star),
                    new(new GridLength(54)),
                    new(new GridLength(36)),
                    new(new GridLength(36)),
                ];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);

                var label = new Label
                {
                    Text = name,
                    FontFamily = DsChrome.PixelFont,
                    FontSize = 13,
                    TextColor = UiTokens.Paper,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    InputTransparent = true,
                };
                var counter = new Label
                {
                    Text = $"×{count}",
                    FontFamily = DsChrome.PixelFont,
                    FontSize = 13,
                    TextColor = UiTokens.BagCyan,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                    InputTransparent = true,
                };

                Children.Add(_bg);
                Grid.SetColumnSpan(_bg, 5);
                Children.Add(icon);
                Children.Add(label);
                Children.Add(counter);
                Children.Add(CountDisc(minus: true, () => Minus?.Invoke()));
                Children.Add(CountDisc(minus: false, () => Plus?.Invoke()));
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(counter, 2);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[4], 3);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[5], 4);

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }
        }

        /// <summary>The add-item row: a plus disc and the cyan invitation.</summary>
        private sealed class AddRow : Grid
        {
            private readonly SKCanvasView _bg;
            private bool _selected;

            public Action? Tapped { get; set; }

            public AddRow()
            {
                HeightRequest = 40;
                ColumnDefinitions = [new(new GridLength(32)), new(GridLength.Star)];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);
                Children.Add(_bg);
                Grid.SetColumnSpan(_bg, 2);
                Children.Add(CountDisc(minus: false, () => Tapped?.Invoke()));
                Children.Add(new Label
                {
                    Text = "ADD ITEM",
                    FontFamily = DsChrome.PixelFont,
                    FontSize = 13,
                    TextColor = UiTokens.BagCyan,
                    VerticalTextAlignment = TextAlignment.Center,
                    InputTransparent = true,
                });
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[1], 0);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[2], 1);
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }
        }

        /// <summary>Navy-deep row plate with a cyan edge when the pad cursor is here.</summary>
        private static void DrawRow(SKCanvas canvas, SKImageInfo info, bool selected)
        {
            var r = new SKRect(0, 1, info.Width, info.Height - 1);
            using var fill = new SKPaint { Color = Pksm.BagNavyDeep, IsAntialias = true };
            canvas.DrawRoundRect(r, 4, 4, fill);
            if (!selected) return;
            using var bar = new SKPaint { Color = Pksm.BagCyan, IsAntialias = true };
            canvas.DrawRect(new SKRect(0, 4, 4, info.Height - 4), bar);
        }

        /// <summary>The round count button: navy disc, cyan rim, cyan + or - arms.</summary>
        private static Grid CountDisc(bool minus, Action onTap)
        {
            var disc = new Grid
            {
                HeightRequest = 32,
                WidthRequest = 32,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            var canvas = new SKCanvasView { InputTransparent = true };
            canvas.PaintSurface += (_, args) =>
            {
                var c = args.Surface.Canvas;
                c.Clear(SKColors.Transparent);
                var info = args.Info;
                var radius = Math.Min(info.Width, info.Height) / 2f - 2f;
                var cx = info.Width / 2f;
                var cy = info.Height / 2f;
                var r = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
                using var fill = new SKPaint { Color = Pksm.BagNavy, IsAntialias = true };
                using var inner = new SKPaint { Color = Pksm.BagNavyDeep, IsAntialias = true };
                using var rim = new SKPaint { Color = Pksm.BagCyan, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                using var arm = new SKPaint { Color = Pksm.BagCyan, IsAntialias = true };
                c.DrawOval(r, fill);
                c.DrawOval(SKRect.Inflate(r, -1.5f, -1.5f), inner);
                c.DrawOval(r, rim);
                var a = radius * 0.55f;
                c.DrawRoundRect(new SKRect(cx - a, cy - 1.5f, cx + a, cy + 1.5f), 1.5f, 1.5f, arm);
                if (!minus)
                    c.DrawRoundRect(new SKRect(cx - 1.5f, cy - a, cx + 1.5f, cy + a), 1.5f, 1.5f, arm);
            };
            disc.Children.Add(canvas);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            disc.GestureRecognizers.Add(tap);
            return disc;
        }
    }

    /// <summary>Pokédex progress + the one-tap complete.</summary>
    private async Task ShowDexMenuAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var progress = session.GetDexProgress();
        var choice = await PadMenu.ShowAsync(_hostGrid, "POKéDEX",
            $"Seen {progress.Seen} / {progress.Total} · Caught {progress.Caught} / {progress.Total}",
            "Complete the Pokédex (all seen + caught)", "Close");
        if (choice != "Complete the Pokédex (all seen + caught)") return;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "COMPLETE THE POKéDEX?",
            "Every species will be marked seen and caught. The current save state is kept as a restore point.",
            "Complete it");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(s =>
        {
            s.CompleteDex();
            return new GenerationOutcome(true, "Pokédex completed. Professor Oak is speechless.");
        }, Math.Max(0, _viewModel.SelectedSlot));
    }

    /// <summary>Every button on this screen, in one place.</summary>
    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: return _viewModel.MoveCursor(FocusDirection.Up);
            case PadButton.Down: return _viewModel.MoveCursor(FocusDirection.Down);
            case PadButton.Left: return _viewModel.MoveCursor(FocusDirection.Left);
            case PadButton.Right: return _viewModel.MoveCursor(FocusDirection.Right);
            case PadButton.L: _viewModel.PreviousBox(); return true;
            case PadButton.R: _viewModel.NextBox(); return true;
            case PadButton.A: return ConfirmCursor();
            case PadButton.B:
                if (_viewModel.SelectMode) { _viewModel.ExitSelectMode(); _canvas.InvalidateSurface(); return true; }
                if (_viewModel.CarrySource is not null) { _viewModel.CancelCarry(); _canvas.InvalidateSurface(); return true; }
                _ = Navigation.PopAsync();
                return true;
            case PadButton.X: _ = ShowToolsAsync(); return true;
            case PadButton.Y: _ = ShowSaveDataAsync(); return true;
            case PadButton.Start: return OpenCursorMenu();
            default: return false;
        }
    }

    /// <summary>
    /// A is the hand, like the games' PC: grab the mon under the cursor, carry it,
    /// place (or swap) on the next press. Empty slot with empty hand → add sheet.
    /// </summary>
    private bool ConfirmCursor()
    {
        if (_viewModel.Save is null) return false;
        var slot = Math.Max(0, _viewModel.SelectedSlot);
        _viewModel.SelectSlot(slot);

        if (_viewModel.SelectMode)
        {
            _viewModel.ToggleMark(slot);
            _canvas.InvalidateSurface();
            return true;
        }
        if (_viewModel.CarrySource is not null)
        {
            _ = DropAndRepaintAsync();
            return true;
        }
        if (_viewModel.BeginCarry())
        {
            _canvas.InvalidateSurface();
            return true;
        }
        _ = OfferAddPokemonAsync(slot);
        return true;
    }

    private async Task DropAndRepaintAsync()
    {
        await _viewModel.DropAsync();
        _canvas.InvalidateSurface();
    }

    /// <summary>Start opens the menu for whatever the cursor is on: mon actions or the add sheet.</summary>
    private bool OpenCursorMenu()
    {
        if (_viewModel.Save is null) return false;
        var slot = Math.Max(0, _viewModel.SelectedSlot);
        var slots = _viewModel.VisibleSlots;
        _viewModel.SelectSlot(slot);
        if (slot < slots.Count && slots[slot].Species is not null)
            _ = ShowMonActionsAsync(slot);
        else
            _ = OfferAddPokemonAsync(slot);
        return true;
    }

    /// <summary>What you can do with the mon under the cursor. Editing is live in the side panel already.</summary>
    private async Task ShowMonActionsAsync(int slot)
    {
        var nickname = _viewModel.Selected?.Nickname is { Length: > 0 } nick ? nick : $"slot {slot + 1}";
        var choice = await PadMenu.ShowAsync(_hostGrid, nickname.ToUpperInvariant(), null,
            new PadOption("Edit (side panel)", IconPath: "editor"),
            new PadOption("Move", IconPath: "storage"),
            new PadOption("Duplicate", IconPath: "storage"),
            new PadOption("Send to Bank", IconPath: "bank"),
            new PadOption("Send to another game…", IconPath: "storage"),
            new PadOption("Export .pk file", IconPath: "folder"),
            new PadOption("Show as Showdown set", IconPath: "script"),
            new PadOption("Show as QR code", IconPath: "search"),
            new PadOption("Release", IconPath: "hex"));
        switch (choice)
        {
            case "Move":
                if (_viewModel.BeginCarry()) _canvas.InvalidateSurface();
                return;
            case "Duplicate":
                await DuplicateSlotAsync(slot);
                return;
            case "Send to Bank":
                await SendToBankAsync(slot, nickname);
                return;
            case "Send to another game…":
                await SendSlotToAnotherGameAsync(slot, nickname);
                return;
            case "Export .pk file":
                await ExportSlotAsync(slot);
                return;
            case "Show as Showdown set":
                await ShowShowdownAsync(slot);
                return;
            case "Show as QR code":
                await ShowQrAsync(slot);
                return;
            case "Release":
                await ReleaseSlotAsync(slot, nickname);
                return;
            default:
                return; // Edit: the editor is already open on the right
        }
    }

    /// <summary>Clone the mon in place: party clones append (cap 6), box clones fill the
    /// box's first empty slot. PKSM-style Duplicate, one backup per write.</summary>
    private async Task DuplicateSlotAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var box = _viewModel.BoxIndex;
        var export = session.ExportSlot(box, slot);
        var name = _viewModel.Selected?.Nickname is { Length: > 0 } nick ? nick : "Pokémon";

        if (box == -1)
        {
            var partyCount = Enumerable.Range(0, 6).Count(i => !session.ReadEntity(-1, i).IsEmpty);
            var ok = await _viewModel.RunMutationAsync(s =>
                s.ImportSlot(-1, 0, export.Data)
                    ? new GenerationOutcome(true, $"{name} cloned into the party.")
                    : new GenerationOutcome(false, "The party is full."), slot);
            if (!ok) { _viewModel.Status = "The party is full - no room to clone."; return; }
            _viewModel.SelectSlot(Math.Min(partyCount, 5));
            _canvas.InvalidateSurface();
            return;
        }

        var empty = _viewModel.VisibleSlots.FirstOrDefault(x => x.Species is null)?.Slot ?? -1;
        if (empty < 0)
        {
            _viewModel.Status = "This box is full - no room to clone.";
            return;
        }
        await _viewModel.RunMutationAsync(s =>
            s.ImportSlot(box, empty, export.Data)
                ? new GenerationOutcome(true, $"{name} cloned.")
                : new GenerationOutcome(false, "Clone failed."), empty);
        _viewModel.SelectSlot(empty);
        _canvas.InvalidateSurface();
    }

    /// <summary>
    /// Game-to-game: pick any other detected save, the transfer service converts and
    /// writes there, then the mon leaves this box (a real move, not a copy).
    /// </summary>
    private async Task SendSlotToAnotherGameAsync(int slot, string nickname)
    {
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<SavePickerViewModel>();
        var transfer = services?.GetService<Services.TransferService>();
        var session = _sessionsFor();
        if (picker is null || transfer is null || session is null) return;

        var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
        var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
            "SEND TO GAME", $"{nickname} → pick the destination (the mon leaves this box)", currentDoc);
        if (target is null)
        {
            if (picker.Saves.Count == 0)
                _viewModel.Status = "No other games linked. Link another emulator or save on Home.";
            return;
        }

        var confirm = await PadMenu.ConfirmAsync(_hostGrid, "MOVE TO ANOTHER GAME?",
            $"{nickname} will leave this box and join {target.GameLabel} (box space permitting).", "Move");
        if (!confirm) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        var outcome = await transfer.SendToGameAsync(export.Data, nickname, target);
        _viewModel.Status = outcome.Message;
        if (!outcome.Success) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} moved to {target.GameLabel}.");
        }, slot);
    }

    /// <summary>Writes the decrypted .pk* file and hands it to Android's share sheet.</summary>
    private async Task ExportSlotAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        try
        {
            var export = session.ExportSlot(_viewModel.BoxIndex, slot);
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, export.FileName);
            await File.WriteAllBytesAsync(path, export.Data);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = export.FileName,
                File = new ShareFile(path),
            });
            _viewModel.Status = $"Exported {export.FileName}";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"Export failed: {error.Message}";
        }
    }

    private async Task ShowShowdownAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.GetShowdownText(_viewModel.BoxIndex, slot);
        var choice = await PadMenu.ShowAsync(_hostGrid, "SHOWDOWN SET", text, "Copy to clipboard", "Close");
        if (choice == "Copy to clipboard")
        {
            await Clipboard.Default.SetTextAsync(text);
            _viewModel.Status = "Set copied to clipboard.";
        }
    }

    private async Task ShowQrAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.GetShowdownText(_viewModel.BoxIndex, slot);
        await QrPopup.ShowAsync(_hostGrid, "SHOWDOWN SET · QR", text);
    }

    /// <summary>Deposit: the mon's bytes move into the app's vault, the game slot is emptied (one safe write).</summary>
    private async Task SendToBankAsync(int slot, string nickname)
    {
        var session = _sessionsFor();
        var bank = IPlatformApplication.Current?.Services.GetService<IBankService>();
        var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
        if (session is null || bank is null || engine is null) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        var info = engine.TryDescribeEntity(export.Data, _viewModel.ConnectedName);
        if (info is null)
        {
            _viewModel.Status = "Could not read this mon for the bank.";
            return;
        }

        var ok = await _viewModel.RunMutationAsync(s =>
        {
            s.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} deposited in the Bank.");
        }, slot);
        if (ok)
        {
            bank.Add(export.Data, info);
            _canvas.InvalidateSurface();
        }
    }

    /// <summary>Release with confirmation; the pre-release state stays recoverable as a restore point.</summary>
    private async Task ReleaseSlotAsync(int slot, string nickname)
    {
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "RELEASE?",
            $"Release {nickname} back into the wild? The current save state is kept as a restore point.",
            "Release");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(session =>
        {
            session.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} was released. Bye-bye!");
        }, slot);
        _canvas.InvalidateSurface();
    }

    private async Task ShowSettingsAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "SETTINGS", null,
            "Close save (back to games)", "Restore points", "About PKForge");
        switch (choice)
        {
            case "Close save (back to games)": await Navigation.PopAsync(); break;
            case "Restore points": await PushAsync<BackupHistoryPage>(); break;
            case "About PKForge": _viewModel.Status = "PKForge - open-source save manager. GPLv3."; break;
        }
    }

    /// <summary>The Thor's second screen mirrors the box automatically while this page is open.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        StartSpritePrefetch();
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable != true) return;
        try { _ = host.ShowAsync(); }
        catch { /* single-screen devices and flaky displays must never break the box */ }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        _prefetchCts?.Cancel();
    }

    private CancellationTokenSource? _prefetchCts;

    /// <summary>
    /// Warms the animated sprites for every mon in the open save in the background,
    /// with friendly progress in the status strip - selections feel instant afterwards.
    /// </summary>
    private void StartSpritePrefetch()
    {
        _prefetchCts?.Cancel();
        var save = _viewModel.Save;
        if (save is null) return;
        var pending = save.Slots
            .Where(s => s.Species is not null)
            .Select(s => (Species: s.Species!.Value, s.IsShiny))
            .Distinct()
            .Where(pair => !_sprites.TryGetShowdown(pair.Species, pair.IsShiny, out _))
            .ToList();
        if (pending.Count == 0) return;

        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        _ = Task.Run(async () =>
        {
            var done = 0;
            foreach (var (species, shiny) in pending)
            {
                if (cts.IsCancellationRequested) return;
                var loaded = new TaskCompletionSource();
                _sprites.WarmShowdown(species, shiny, () => loaded.TrySetResult());
                await Task.WhenAny(loaded.Task, Task.Delay(4000, CancellationToken.None));
                done++;
                if (pending.Count > 3 && done % 3 == 0)
                {
                    var progress = $"Catching sprites… {done}/{pending.Count}";
                    MainThread.BeginInvokeOnMainThread(() => _viewModel.Status = progress);
                }
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_viewModel.Status.StartsWith("Catching sprites", StringComparison.Ordinal))
                    _viewModel.Status = "READY";
            });
        }, cts.Token);
    }

    private View BuildEditor()
    {
        // The mon's name lives in the side panel's maroon header now; the editor
        // opens with the legality verdict, then the striped attribute rows.
        var legality = new Label { FontSize = 11, TextColor = UiTokens.Ink1, MaximumHeightRequest = 90 };
        legality.SetBinding(Label.TextProperty, nameof(BoxBrowserViewModel.LegalityText));

        var save = Kit.Capsule("SAVE CHANGES", UiTokens.Green);
        save.Margin = new Thickness(0, 8, 0, 0);
        save.SetBinding(Button.CommandProperty, nameof(BoxBrowserViewModel.SaveEditCommand));

        // Per-mon powers: LEGALIZE is live; the rest are staged.
        var monActions = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var legalize = Kit.Capsule("LEGALIZE", UiTokens.Green);
        legalize.FontSize = 11;
        legalize.Padding = new Thickness(10, 6);
        legalize.Margin = new Thickness(0, 0, 6, 6);
        legalize.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            if (slot < 0) return;
            var overlay = LoadingOverlay.Show(_hostGrid, "LEGALIZING…",
                "Finding the closest real, legal version of this Pokémon.");
            try
            {
                await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot);
                _canvas.InvalidateSurface();
            }
            finally
            {
                overlay.Close();
            }
        };
        monActions.Children.Add(legalize);
        foreach (var (actionLabel, run) in new (string, Func<int, Task>)[]
        {
            ("SHOWDOWN", ShowShowdownAsync),
            ("EXPORT .PK", ExportSlotAsync),
            ("QR", ShowQrAsync),
        })
        {
            var action = Kit.Capsule(actionLabel, UiTokens.Ink1);
            action.FontSize = 11;
            action.Padding = new Thickness(10, 6);
            action.Margin = new Thickness(0, 0, 6, 6);
            action.Clicked += async (_, _) =>
            {
                if (_viewModel.SelectedSlot >= 0)
                    await run(_viewModel.SelectedSlot);
            };
            monActions.Children.Add(action);
        }

        // One-tap spreads: fill the editor fields; SAVE CHANGES commits as usual.
        foreach (var (quickLabel, apply) in new (string, Action)[]
        {
            ("MAX IV", () => _viewModel.EditIvs = "31 31 31 31 31 31"),
            ("0 EV", () => _viewModel.EditEvs = "0 0 0 0 0 0"),
            ("LV 100", () => _viewModel.EditLevel = "100"),
        })
        {
            var quick = Kit.Capsule(quickLabel, UiTokens.Blue);
            quick.FontSize = 11;
            quick.Padding = new Thickness(10, 6);
            quick.Margin = new Thickness(0, 0, 6, 6);
            quick.Clicked += (_, _) => { if (_viewModel.SelectedSlot >= 0) apply(); };
            monActions.Children.Add(quick);
        }

        // Met / origin opens the identity sub-editor, then safely writes (backup + atomic).
        var met = Kit.Capsule("MET / ORIGIN", UiTokens.Cyan);
        met.FontSize = 11;
        met.Padding = new Thickness(10, 6);
        met.Margin = new Thickness(0, 0, 6, 6);
        met.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            var session = _sessionsFor();
            if (slot < 0 || session is null) return;
            var changed = await MetOriginEditor.ShowAsync(_hostGrid, session, _viewModel.BoxIndex, slot);
            if (changed)
                await _viewModel.RunMutationAsync(_ => new GenerationOutcome(true, "Met / origin updated"), slot);
        };
        monActions.Children.Add(met);

        // Potential opens the Tera / Hyper Training / ability slot sub-editor (gen-gated).
        var potential = Kit.Capsule("POTENTIAL", UiTokens.Cyan);
        potential.FontSize = 11;
        potential.Padding = new Thickness(10, 6);
        potential.Margin = new Thickness(0, 0, 6, 6);
        potential.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            var session = _sessionsFor();
            if (slot < 0 || session is null) return;
            var changed = await PotentialEditor.ShowAsync(_hostGrid, session, _viewModel.BoxIndex, slot);
            if (changed)
                await _viewModel.RunMutationAsync(_ => new GenerationOutcome(true, "Potential updated"), slot);
        };
        monActions.Children.Add(potential);

        var shinyToggle = new Switch { OnColor = UiTokens.Gold };
        shinyToggle.SetBinding(Switch.IsToggledProperty, nameof(BoxBrowserViewModel.EditShiny));

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        // Picker fields: the user always reads and chooses names, never ids.
        // Species opens the floating Pokédex (sprites + type/gen filters), not a list.
        var species = NamedPicker("SPECIES", nameof(BoxBrowserViewModel.EditSpecies), data.SpeciesNames, null,
            openPokedex: true, shaded: false);
        var nature = NamedPicker("NATURE", nameof(BoxBrowserViewModel.EditNature), data.NatureNames,
            () => AllItems(data.NatureNames, includeZero: true), shaded: true);
        var ability = NamedPicker("ABILITY", nameof(BoxBrowserViewModel.EditAbility), data.AbilityNames,
            () =>
            {
                // Only the abilities this species can legally carry in the open game.
                var detail = _viewModel.Selected;
                var session = _sessionsFor();
                if (detail is null || session is null) return [];
                return session.GetAbilityChoices(detail.Species, detail.Form)
                    .Select(id => new PickItem(id, id < data.AbilityNames.Count ? data.AbilityNames[id] : $"#{id}"))
                    .ToList();
            }, shaded: false);
        var item = NamedPicker("HELD ITEM", nameof(BoxBrowserViewModel.EditHeldItem), data.ItemNames,
            () => ItemsWithIcons(data.ItemNames), shaded: true);
        var move1 = NamedPicker("MOVE 1", nameof(BoxBrowserViewModel.EditMove1), data.MoveNames, MoveItems, shaded: false);
        var move2 = NamedPicker("MOVE 2", nameof(BoxBrowserViewModel.EditMove2), data.MoveNames, MoveItems, shaded: true);
        var move3 = NamedPicker("MOVE 3", nameof(BoxBrowserViewModel.EditMove3), data.MoveNames, MoveItems, shaded: false);
        var move4 = NamedPicker("MOVE 4", nameof(BoxBrowserViewModel.EditMove4), data.MoveNames, MoveItems, shaded: true);
        var ball = NamedPicker("BALL", nameof(BoxBrowserViewModel.EditBall), data.BallNames, BallItems, shaded: false);

        List<PickItem> MoveItems() => AllItems(data.MoveNames, includeZero: true, zeroLabel: "(none)");

        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                legality,
                species,
                FieldRow("Nickname", nameof(BoxBrowserViewModel.EditNickname), shaded: true),
                FieldRow("Level", nameof(BoxBrowserViewModel.EditLevel), shaded: false),
                nature, ability, item,
                move1, move2, move3, move4,
                StatsField("IVS", nameof(BoxBrowserViewModel.EditIvs), 31, shaded: false),
                StatsField("EVS", nameof(BoxBrowserViewModel.EditEvs), 252, shaded: true),
                ball,
                FieldRow("OT", nameof(BoxBrowserViewModel.EditOt), shaded: true),
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        new Label { Text = "Shiny", FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center },
                        shinyToggle,
                    },
                },
                save,
                monActions,
            },
        };
    }

    /// <summary>A Kit.Field wrapped in the striped attribute-row plate.</summary>
    private static View FieldRow(string caption, string bindingPath, bool shaded) =>
        Striped(Kit.Field(caption, bindingPath), shaded);

    /// <summary>Attribute rows alternate paper / paper-shade plates with a chrome hairline.</summary>
    private static View Striped(View inner, bool shaded) => new Border
    {
        BackgroundColor = shaded ? UiTokens.PaperShade : UiTokens.Paper,
        Stroke = UiTokens.ShellEdge,
        StrokeThickness = 1.2,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
        Padding = new Thickness(10, 4),
        Content = inner,
    };

    private Domain.ISaveEngineSession? _sessionsFor() =>
        IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;

    /// <summary>Item pick list with sprites for everything already in the icon cache (misses warm in the background).</summary>
    private static List<PickItem> ItemsWithIcons(IReadOnlyList<string> names)
    {
        var items = new List<PickItem>(names.Count) { new(0, "(none)") };
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(names[id]) + ".png");
            items.Add(new PickItem(id, names[id], File.Exists(cached) ? cached : null));
        }
        return items;
    }

    private static List<PickItem> AllItems(IReadOnlyList<string> names, bool includeZero, string? zeroLabel = null)
    {
        var items = new List<PickItem>(names.Count);
        for (var id = includeZero ? 0 : 1; id < names.Count; id++)
        {
            var name = id == 0 && zeroLabel is not null ? zeroLabel : names[id];
            if (name.Length > 0)
                items.Add(new PickItem(id, name));
        }
        return items;
    }

    private List<PickItem> BallItems()
    {
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var items = new List<PickItem>();
        for (var id = 1; id < data.BallNames.Count; id++)
        {
            if (data.BallNames[id].Length == 0) continue;
            items.Add(new PickItem(id, data.BallNames[id], BallIconPath(id)));
        }
        return items;
    }

    /// <summary>Bundled ball icon copied to cache once so Image can load it by file path.</summary>
    private static string? BallIconPath(int ball)
    {
        var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, $"ballicon-{ball}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            using var asset = FileSystem.OpenAppPackageFileAsync($"balls/_ball{ball}.png").GetAwaiter().GetResult();
            using var output = File.Create(cache);
            asset.CopyTo(output);
            return cache;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A tappable name field: caption + current value as a name, opens the searchable picker (or the Pokédex).</summary>
    private View NamedPicker(string caption, string vmProperty, IReadOnlyList<string> names, Func<List<PickItem>>? itemsFactory, bool openPokedex = false, bool shaded = false)
    {
        var value = Kit.BlueprintValue(13);
        value.SetBinding(Label.TextProperty, new Binding(vmProperty, converter: new IdNameConverter(names)));

        var chip = new Border
        {
            BackgroundColor = shaded ? UiTokens.PaperShade : UiTokens.Paper,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1.2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(10, 5),
            Content = new Grid
            {
                ColumnDefinitions = [new(new GridLength(78)), new(GridLength.Star), new(GridLength.Auto)],
                Children =
                {
                    new Label { Text = caption, FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1, TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center },
                    value,
                    new Label { Text = ">", FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Blueprint, FontSize = 13, VerticalTextAlignment = TextAlignment.Center },
                },
            },
        };
        var inner = (Grid)chip.Content!;
        Grid.SetColumn(inner.Children[1] as View, 1);
        Grid.SetColumn(inner.Children[2] as View, 2);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            PickItem? picked;
            if (openPokedex)
            {
                var services = IPlatformApplication.Current!.Services;
                var data = services.GetRequiredService<IGameDataService>();
                var session = _sessionsFor();
                if (session is null) return;
                picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            }
            else
            {
                var items = itemsFactory?.Invoke() ?? [];
                if (items.Count == 0) return;
                int? current = int.TryParse(GetVmString(vmProperty), out var id) ? id : null;
                picked = await PickerMenu.ShowAsync(_hostGrid, caption, items, current);
            }
            if (picked is not null)
                SetVmString(vmProperty, picked.Id.ToString());
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    /// <summary>Read-only stat row with an explicit EDIT button for manual (expert) input.</summary>
    private View StatsField(string caption, string vmProperty, int max, bool shaded = false)
    {
        var value = Kit.BlueprintValue(12);
        value.SetBinding(Label.TextProperty, vmProperty);

        var edit = Kit.Capsule("EDIT", UiTokens.Blue);
        edit.FontSize = 10;
        edit.Padding = new Thickness(10, 4);
        edit.Clicked += async (_, _) =>
        {
            var current = (GetVmString(vmProperty) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var v) ? v : 0).ToArray();
            if (current.Length != 6) current = new int[6];
            var updated = await StatsPopup.ShowAsync(_hostGrid, caption, current, max);
            if (updated is not null)
                SetVmString(vmProperty, string.Join(' ', updated));
        };

        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new(new GridLength(78)), new(GridLength.Star), new(GridLength.Auto)],
            Children =
            {
                new Label { Text = caption, FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1, TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center },
                value, edit,
            },
        };
        Grid.SetColumn(value, 1);
        Grid.SetColumn(edit, 2);
        return new Border
        {
            BackgroundColor = shaded ? UiTokens.PaperShade : UiTokens.Paper,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1.2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(10, 4),
            Content = row,
        };
    }

    private string? GetVmString(string property) =>
        typeof(BoxBrowserViewModel).GetProperty(property)?.GetValue(_viewModel) as string;

    private void SetVmString(string property, string value) =>
        typeof(BoxBrowserViewModel).GetProperty(property)?.SetValue(_viewModel, value);

    /// <summary>The editor header line: "Nickname   Lv.X" for the selected mon.</summary>
    private sealed class MonHeaderConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is EntityDetail d && !d.IsEmpty)
                return $"{(string.IsNullOrEmpty(d.Nickname) ? $"#{d.Species}" : d.Nickname)}   Lv.{d.Level}";
            return "Pokémon";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Shows the id's display name; ids without a name fall back to the raw number.</summary>
    private sealed class IdNameConverter(IReadOnlyList<string> names) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string text && int.TryParse(text, out var id) && id >= 0 && id < names.Count && names[id].Length > 0)
                return names[id];
            return value as string ?? "";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private async Task PushAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        if (_viewModel.BoxIndex == -1)
        {
            // The party pseudo-box renders as the navy deck, not the grid.
            PartyView.Paint(args.Surface.Canvas, args.Info, _sprites, _sessionsFor(), _viewModel.SelectedSlot, _frame.Request);
            return;
        }
        BoxGridRenderer.Paint(args.Surface.Canvas, args.Info, _viewModel, _sprites, _theme, _frame.Request);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        // Skia only delivers Released if Pressed was marked handled.
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        var slot = _viewModel.BoxIndex == -1
            ? PartyView.SlotFromTouch(_canvas.CanvasSize, args.Location)
            : BoxGridRenderer.SlotFromTouch(_canvas.CanvasSize, args.Location);
        args.Handled = true;
        if (slot < 0) return;

        // Organizer: taps toggle marks.
        if (_viewModel.SelectMode)
        {
            _viewModel.SelectSlot(slot);
            _viewModel.ToggleMark(slot);
            _canvas.InvalidateSurface();
            return;
        }
        // Touch: first tap selects (summary + editor), tapping the selected mon again grabs it,
        // next tap places. Empty slot with empty hands opens the add sheet.
        var wasSelected = _viewModel.SelectedSlot == slot;
        _viewModel.SelectSlot(slot);
        if (_viewModel.CarrySource is not null)
        {
            _ = DropAndRepaintAsync();
            return;
        }
        var slots = _viewModel.VisibleSlots;
        if (slot < slots.Count && slots[slot].Species is null)
        {
            _ = OfferAddPokemonAsync(slot);
            return;
        }
        if (wasSelected && _viewModel.BeginCarry())
            _canvas.InvalidateSurface();
    }

    /// <summary>An empty slot is an invitation, not a dead cell: offer the ways to fill it.</summary>
    private async Task OfferAddPokemonAsync(int slot)
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, $"ADD A POKéMON - SLOT {slot + 1}", null,
            new PadOption("Create a Pokémon", IconPath: "editor"),
            new PadOption("Paste a Showdown set", IconPath: "script"),
            new PadOption("Import .pk file", IconPath: "folder"),
            new PadOption("From event database", IconPath: "events"));
        switch (choice)
        {
            case "Create a Pokémon":
                await RunGenerateWizardAsync(slot);
                return;
            case "Paste a Showdown set":
                await RunShowdownPasteAsync(slot);
                return;
            case "Import .pk file":
            {
                var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
                var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
                if (picker is null || access is null) return;
                var document = await picker.PickSaveAsync();
                if (document is null) return;
                var bytes = (await access.ReadAsync(document.DocumentId)).ToArray();
                await _viewModel.RunMutationAsync(session =>
                    session.ImportSlot(_viewModel.BoxIndex, slot, bytes)
                        ? new GenerationOutcome(true, $"Imported {document.DisplayName}.")
                        : new GenerationOutcome(false, "That file is not a recognizable Pokémon."), slot);
                _canvas.InvalidateSurface();
                return;
            }
            case "From event database":
            {
                var session = _sessionsFor();
                if (session is null) return;
                await EventGallery.ShowAsync(_hostGrid, _viewModel, session, slot, () => _canvas.InvalidateSurface());
                return;
            }
            default:
                return;
        }
    }

    /// <summary>Step-by-step creation: species → features → offline legalizer → into the slot.</summary>
    private async Task RunGenerateWizardAsync(int slot)
    {
        var services = IPlatformApplication.Current!.Services;
        var data = services.GetRequiredService<IGameDataService>();
        var session = services.GetRequiredService<ISaveSessionService>().CurrentSession;
        if (session is null) return;

        var request = await GenerateWizard.RunAsync(_hostGrid, data, session);
        if (request is null) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "CREATING YOUR POKéMON…",
            "The offline legalizer is finding a real, legal way for this Pokémon to exist.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.Generate(s, _viewModel.BoxIndex, slot, request), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>Paste a competitive set; the legalizer turns it into a legal mon in the slot.</summary>
    private async Task RunShowdownPasteAsync(int slot)
    {
        var text = await TextPopup.ShowAsync(_hostGrid, "PASTE A SHOWDOWN SET",
            "Paste the set exactly as exported from Pokémon Showdown.");
        if (string.IsNullOrWhiteSpace(text)) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "READING THE SET…",
            "The offline legalizer is building a legal Pokémon from your set.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, text), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }
}
