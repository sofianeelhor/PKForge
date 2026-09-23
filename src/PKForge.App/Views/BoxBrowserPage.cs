using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKHeX.Core;
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
    private readonly SKCanvasView _boxNeighbors;
    private readonly FrameInvalidator _frame;
    private readonly ContentView _footerHost;
    private readonly Grid _storageContent;
    private readonly View _sidePanel;
    private ScrollView? _editorScroll;
    private EditorFocusTarget[] EditorFocusTargets = [];
    private Grid _hostGrid = null!;
    private long _partyPulseStart = Environment.TickCount64;
    private int _lastAimSlot = -1;
    private IDispatcherTimer? _partyPulseTimer;
    private IDispatcherTimer? _boxManagePulseTimer;
    private bool _boxManageMode;
    private bool _boxManageBusy;
    private bool _boxHeld;
    private bool _editorFocusMode;
    private int _editorFocusIndex;
    private int _heldBox;
    private int _slotBeforeBoxManage = -1;
    private readonly HashSet<int> _lockedSlots = [];
    private readonly HashSet<int> _markedBoxes = [];

    /// <summary>The party cursor breathes: a light repaint loop that only runs on the party view.</summary>
    private void EnsurePartyPulse()
    {
        if (_partyPulseTimer is not null || _viewModel.SelectedSlot < 0) return;
        _partyPulseStart = Environment.TickCount64;
        _partyPulseTimer = Dispatcher.CreateTimer();
        _partyPulseTimer.Interval = TimeSpan.FromMilliseconds(45);
        _partyPulseTimer.Tick += (_, _) => _frame.Request();
        _partyPulseTimer.Start();
    }

    private void StopPartyPulse()
    {
        _partyPulseTimer?.Stop();
        _partyPulseTimer = null;
    }

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

        _boxNeighbors = new SKCanvasView { HeightRequest = 100, IsVisible = false, InputTransparent = true };
        _boxNeighbors.PaintSurface += PaintBoxNeighbors;

        var screenBody = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            Children = { _boxBar, _boxNeighbors, _canvas },
        };
        Grid.SetRow(_boxNeighbors, 1);
        Grid.SetRow(_canvas, 2);

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

        _sidePanel = BuildSidePanel();

        // DS chrome around the box grid + editor.
        _storageContent = new Grid
        {
            Padding = new Thickness(12, 10),
            ColumnSpacing = 12,
            ColumnDefinitions = [new(GridLength.Star), new(new GridLength(330))],
            Children = { screen, _sidePanel },
        };
        Grid.SetColumn(_sidePanel, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), _storageContent } };

        var title = string.IsNullOrEmpty(_viewModel.ConnectedName) ? "Storage" : _viewModel.ConnectedName;
        _footerHost = new ContentView { Content = DsChrome.Footer(
            ("A", "Grab", null),
            ("B", "Back", () => _ = Navigation.PopAsync()),
            ("LR", "Box", null),
            ("X", "Tools", () => _ = ShowToolsAsync()),
            ("Y", "Save data", () => _ = ShowSaveDataAsync()),
            ("+", "Menu", () => OpenCursorMenu())) };

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip(title, "Connected"), bodyHost, _footerHost },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(_footerHost, 3);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Save) or nameof(BoxBrowserViewModel.BoxIndex)
                or nameof(BoxBrowserViewModel.SelectedSlot) or nameof(BoxBrowserViewModel.VisibleSlots))
            {
                RefreshLockedSlots();
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
        var bounds = new SKRect(0, 0, args.Info.Width, args.Info.Height);
        var fontSize = 20f;
        if (_boxManageMode && _boxHeld)
        {
            var phase = (Environment.TickCount64 % 900) / 900d * Math.PI * 2;
            var breath = (float)((Math.Sin(phase) + 1) * 0.5);
            bounds.Inflate(-2f - breath * 3f, -1f - breath);
            fontSize += breath * 1.5f;
        }
        using var font = new SKFont(PixelTypeface(), fontSize);
        var boxName = _viewModel.BoxIndex == -1 ? "PARTY" : $"BOX {_viewModel.BoxIndex + 1:00}";
        if (_boxManageMode) boxName = _boxHeld ? $"HOLDING · {boxName}" : $"MANAGE · {boxName}";
        PksmPaint.BoxNameBar(canvas, bounds, boxName, font,
            canPrev: _viewModel.BoxIndex > 0,
            canNext: _viewModel.BoxIndex < _viewModel.BoxCount - 1);
    }

    /// <summary>A compact three-box map keeps both neighbors understandable while ordering.</summary>
    private void PaintBoxNeighbors(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(Pksm.Housing);
        var session = _sessionsFor();
        if (!_boxManageMode || session is null) return;
        using var label = new SKFont(PixelTypeface(), 13);
        var gap = 10f;
        var cardWidth = (args.Info.Width - gap * 4) / 3f;
        for (var offset = -1; offset <= 1; offset++)
        {
            var box = _viewModel.BoxIndex + offset;
            var left = gap + (offset + 1) * (cardWidth + gap);
            var rect = new SKRect(left, 3, left + cardWidth, args.Info.Height - 3);
            if ((uint)box >= (uint)_viewModel.BoxCount) continue;
            var locked = DocumentId is not null && Protection.IsBoxLocked(DocumentId!, box);
            using var background = new SKPaint { Color = BoxGridRenderer.WallpaperAt(box), IsAntialias = true };
            using var border = new SKPaint
            {
                Color = locked ? Pksm.Illegal : _markedBoxes.Contains(box) ? Pksm.SelectBorder : offset == 0 ? Pksm.ShinyGold : SKColors.White,
                Style = SKPaintStyle.Stroke, StrokeWidth = offset == 0 ? 4 : 2, IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, 7, 7, background);
            canvas.DrawRoundRect(rect, 7, 7, border);
            PksmPaint.CenterText(canvas, $"{(offset < 0 ? "L  " : offset > 0 ? "R  " : "")}{session.GetBoxName(box)}",
                rect.MidX, rect.Top + 13, label, SKColors.White, Pksm.WallpaperShade(background.Color), SKTextAlign.Center);
            if (locked)
                PksmPaint.CenterText(canvas, "LOCK", rect.Right - 22, rect.Bottom - 8, label,
                    SKColors.White, Pksm.Illegal, SKTextAlign.Center);

            var slots = Enumerable.Range(0, BoxGridRenderer.Columns * BoxGridRenderer.Rows)
                .Select(slot => session.ReadEntity(box, slot)).ToArray();
            var dotW = (rect.Width - 12) / BoxGridRenderer.Columns;
            var dotH = (rect.Height - 24) / BoxGridRenderer.Rows;
            using var occupied = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
            using var shiny = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
            for (var slot = 0; slot < slots.Length; slot++)
            {
                if (slots[slot].IsEmpty) continue;
                var x = rect.Left + 6 + (slot % BoxGridRenderer.Columns + 0.5f) * dotW;
                var y = rect.Top + 21 + (slot / BoxGridRenderer.Columns + 0.5f) * dotH;
                var radius = Math.Min(dotW, dotH) * 0.42f;
                var sprite = _sprites.GetSprite(slots[slot].Species, slots[slot].Form, slots[slot].IsShiny);
                if (sprite is null)
                {
                    _sprites.Warm(slots[slot].Species, slots[slot].Form, slots[slot].IsShiny, _boxNeighbors.InvalidateSurface);
                    canvas.DrawCircle(x, y, radius * 0.65f, slots[slot].IsShiny ? shiny : occupied);
                    continue;
                }
                using var image = SKImage.FromBitmap(sprite);
                var scale = Math.Min(radius * 2 / image.Width, radius * 2 / image.Height);
                var width = image.Width * scale;
                var height = image.Height * scale;
                canvas.DrawImage(image, new SKRect(x - width / 2, y - height / 2, x + width / 2, y + height / 2), BoxGridRenderer.SpriteSampling);
            }
        }
    }

    private void SetStorageFooter() => _footerHost.Content = DsChrome.Footer(
        ("A", "Grab", null), ("B", "Back", () => _ = Navigation.PopAsync()), ("LR", "Box", null),
        ("X", "Tools", () => _ = ShowToolsAsync()), ("Y", "Save data", () => _ = ShowSaveDataAsync()),
        ("+", "Menu", () => OpenCursorMenu()));

    private void SetEditorFooter() => _footerHost.Content = DsChrome.Footer(
        ("↑↓", "Navigate", null),
        ("A", "Use field", () => ActivateEditorFocus()),
        ("B", "Box", ExitEditorFocusMode),
        ("+", "Box", ExitEditorFocusMode));

    private void SetBoxManageFooter() => _footerHost.Content = DsChrome.Footer(
        ("A", _boxHeld ? "Drop box" : "Hold box", () => OnPadButton(PadButton.A)),
        ("B", "Done", ExitBoxManageMode),
        ("LR", _boxHeld ? "Swap" : "Browse", null),
        ("Y", _markedBoxes.Contains(_viewModel.BoxIndex) ? "Deselect" : "Select", ToggleMarkedBox),
        ("X", "Bulk actions", () => _ = ShowBoxBulkActionsAsync()));

    // ── Hardcore mode ─────────────────────────────────────────────────────────
    // One guard per surface, never a condition buried inside a flow. Allowed()/Menu()
    // decide what a menu may offer, so a blocked entry is simply absent and comes back
    // the moment the mode is turned off; Denied()/ReportHardcoreAsync() stop a flow
    // that was reached some other way (a panel button, a touch target) with the reason
    // on screen; and every save write still passes the guard in
    // BoxBrowserViewModel.RunMutationAsync.

    private static SaveGuard Guard => HardcoreMode.Guard;

    /// <summary>A menu entry that exists only while Hardcore mode allows its action; null hides it.</summary>
    private static PadOption? Allowed(SaveAction action, PadOption option) =>
        Guard.Allows(action) ? option : null;

    /// <summary>The menu's options without the entries Hardcore mode refuses.</summary>
    private static PadOption[] Menu(params PadOption?[] options) =>
        options.Where(option => option is not null).Select(option => option!).ToArray();

    /// <summary>Menu copy: what the surface wanted to say, plus the Hardcore line while it is on.</summary>
    private static string? Note(string? message) =>
        HardcoreMode.IsOn
            ? string.IsNullOrEmpty(message) ? HardcoreMode.StatusLine : $"{message}\n{HardcoreMode.StatusLine}"
            : message;

    /// <summary>True when Hardcore mode refuses <paramref name="action"/>; the reason lands in the status strip.</summary>
    private bool Denied(SaveAction action)
    {
        if (!HardcoreMode.Blocks(action, out var status)) return false;
        _viewModel.Status = status;
        return true;
    }

    /// <summary>The same refusal for a flow with no menu to filter; the reason is shown, never swallowed.</summary>
    private async Task<bool> DeniedAsync(SaveAction action)
    {
        if (!Denied(action)) return false;
        await PadMenu.ShowAsync(_hostGrid, "HARDCORE MODE", _viewModel.Status, "OK");
        return true;
    }

    /// <summary>The idle status line, carrying the marker so the mode is visible on the screen it gates.</summary>
    private void ShowReady() => _viewModel.Status = HardcoreMode.IsOn
        ? $"READY · {HardcoreMode.StatusLine}"
        : "READY";

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
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { header, previous, next },
        };
        Grid.SetColumn(previous, 1);
        Grid.SetColumn(next, 2);

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

        _editorScroll = new ScrollView { Content = BuildEditor(), IsVisible = false };
        var editor = _editorScroll;

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

    private void EnterEditorFocusMode()
    {
        if (_viewModel.Selected is null) return;
        _editorFocusMode = true;
        _editorFocusIndex = 0;
        SetEditorFooter();
        UpdateEditorFocusVisuals();
        _viewModel.Status = $"EDITOR FOCUS - {EditorFocusTargets[_editorFocusIndex].Caption} · A USE · B BOX";
    }

    private void ExitEditorFocusMode()
    {
        _editorFocusMode = false;
        UpdateEditorFocusVisuals();
        SetStorageFooter();
        ShowReady();
    }

    private void MoveEditorFocus(int delta)
    {
        var current = EditorFocusTargets[_editorFocusIndex];
        if (current.Neighbors is { } neighbors)
        {
            _editorFocusIndex = delta < 0 ? neighbors.Up : neighbors.Down;
        }
        else
        {
            _editorFocusIndex = (_editorFocusIndex + delta + EditorFocusTargets.Length) % EditorFocusTargets.Length;
        }
        UpdateEditorFocusVisuals();
        _viewModel.Status = $"EDITOR FOCUS - {EditorFocusTargets[_editorFocusIndex].Caption} · A USE · B BOX";
    }

    private void MoveEditorFocusHorizontal(int delta)
    {
        var current = EditorFocusTargets[_editorFocusIndex];
        if (current.Neighbors is { } neighbors)
        {
            _editorFocusIndex = delta < 0 ? neighbors.Left : neighbors.Right;
            UpdateEditorFocusVisuals();
            _viewModel.Status = $"EDITOR FOCUS - {EditorFocusTargets[_editorFocusIndex].Caption} · A USE · B BOX";
            return;
        }

        if (current.NumericBindingPath is { } bindingPath)
        {
            var value = int.TryParse(GetVmString(bindingPath), out var level) ? level : 0;
            SetVmString(bindingPath, Math.Clamp(value + delta, 1, 100).ToString());
        }
    }

    private void ActivateEditorFocus()
    {
        _ = EditorFocusTargets[_editorFocusIndex].Activate();
    }

    private void UpdateEditorFocusVisuals()
    {
        foreach (var target in EditorFocusTargets)
        {
            switch (target.View)
            {
                case Border border:
                    border.Stroke = UiTokens.ShellEdge;
                    border.StrokeThickness = 1.2;
                    border.ClearValue(VisualElement.ShadowProperty);
                    break;
                case Button button:
                    if (target.OriginalBackground is { } originalBackground)
                        button.BackgroundColor = originalBackground;
                    if (target.OriginalTextColor is { } originalTextColor)
                        button.TextColor = originalTextColor;
                    break;
            }
        }

        if (!_editorFocusMode) return;
        var focused = EditorFocusTargets[_editorFocusIndex];
        switch (focused.View)
        {
            case Border border:
                border.Stroke = UiTokens.SelectBorder;
                border.StrokeThickness = 4;
                border.Shadow = new Shadow
                {
                    Brush = new SolidColorBrush(UiTokens.SelectBorder),
                    Opacity = 0.65f,
                    Radius = 6,
                    Offset = new Point(0, 0),
                };
                break;
            case Button button:
                button.BackgroundColor = UiTokens.SelectBorder;
                button.TextColor = UiTokens.OnAccent;
                break;
        }

        if (_editorScroll is not null)
            _ = _editorScroll.ScrollToAsync(focused.View, ScrollToPosition.MakeVisible, false);
    }

    private sealed record EditorFocusTarget(View View, string Caption, Func<Task> Activate,
        string? NumericBindingPath = null, Color? OriginalBackground = null, Color? OriginalTextColor = null,
        EditorFocusNeighbors? Neighbors = null);

    private sealed record EditorFocusNeighbors(int Left, int Right, int Up, int Down);

    private async Task ShowToolsAsync()
    {
        if (_viewModel.SelectMode)
        {
            await ShowOrganizerMenuAsync();
            return;
        }
        // Box-level bulk tools run on the stock engine only: romhack sessions refuse
        // sort/batch ops, so their entries are hidden instead of failing mid-flow.
        var boxTools = _sessionsFor()?.SupportsBoxTools == true;
        var options = new List<PadOption> { new("Organizer (multi-select)", IconPath: "select") };
        options.AddRange(Menu(
            Allowed(SaveAction.CreateMon, new("Import .pk files", IconPath: "import")),
            Allowed(SaveAction.CreateMon, new("Import Showdown team", IconPath: "script")),
            new("Export box to Showdown", IconPath: "script"),
            Allowed(SaveAction.CreateMon, new("Generate Living Dex", IconPath: "create")),
            new("How to get a Pokémon…", IconPath: "map"),
            Allowed(SaveAction.CreateMon, new("Egg factory…", IconPath: "egg")),
            new("Day Care / Nursery", IconPath: "daycare")));
        if (_sessionsFor() is { } honeySession && PKForge.Engine.HoneyTreeService.IsSupported(honeySession))
            options.Add(new("Honey trees", IconPath: "tree"));
        if (boxTools)
        {
            options.AddRange(Menu(
                Allowed(SaveAction.BatchEdit, new("Battle prep", IconPath: "battle")),
                Allowed(SaveAction.BatchEdit, new("Batch editor", IconPath: "batch")),
                Allowed(SaveAction.BatchEdit, new("Batch rename / OT…", IconPath: "rename")),
                Allowed(SaveAction.CreateMon, new("Random team…", IconPath: "dice"))));
        }
        options.AddRange(Menu(
            Allowed(SaveAction.BatchEdit, new("Presets…", IconPath: "preset")),
            new("Trainer profiles…", IconPath: "profile"),
            new("Legality check", IconPath: "check"),
            new("Audit report", IconPath: "report"),
            new("Nuzlocke report", IconPath: "skull"),
            new("Collection dex…", IconPath: "pokedex")));
        if (boxTools)
            options.Add(new("Sort boxes…", IconPath: "sort"));
        var choice = await PadMenu.ShowAsync(_hostGrid, "STORAGE TOOLS", Note(null), options.ToArray());
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
            case "How to get a Pokémon…":
            {
                // Game-scoped on purpose: this is "how do I get it in the game I'm editing".
                // The all-games answer lives in the bank/living dex.
                var session = _sessionsFor();
                if (session is not null)
                    await EncounterGallery.ShowGameScopedAsync(_hostGrid, _viewModel, session, () => _canvas.InvalidateSurface());
                return;
            }
            case "Battle prep":
                await ShowBattlePrepAsync();
                return;
            case "Egg factory…":
                await ShowEggFactoryAsync();
                return;
            case "Honey trees":
                {
                    var session = _sessionsFor();
                    if (session is not null)
                        await HoneyTreeEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
            case "Day Care / Nursery":
                {
                    var session = _sessionsFor();
                    if (session is not null)
                        await DaycareEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
            case "Batch editor":
                await RunBatchEditorAsync();
                return;
            case "Batch rename / OT…":
                await ShowBatchRenameAsync();
                return;
            case "Random team…":
                await ShowRandomTeamAsync();
                return;
            case "Presets…":
                await ShowPresetsMenuAsync();
                return;
            case "Trainer profiles…":
                await ShowTrainerProfilesAsync();
                return;
            case "Legality check":
                await ShowLegalityCheckAsync();
                return;
            case "Audit report":
                await ShowAuditReportAsync();
                return;
            case "Nuzlocke report":
                await ShowNuzlockeReportAsync();
                return;
            case "Manage boxes…":
                EnterBoxManageMode();
                return;
            case "Sort boxes…":
                await ShowSortMenuAsync();
                return;
            case "Collection dex…":
            {
                var services = IPlatformApplication.Current?.Services;
                var data = services?.GetService<IGameDataService>();
                var sprites = services?.GetService<ISpriteService>();
                if (data is not null && sprites is not null)
                    await CollectionDexPage.ShowAsync(_hostGrid, _viewModel, data, sprites);
                return;
            }
        }
    }

    /// <summary>Game-style box ordering lives directly in storage: the current box is
    /// picked up, breathes in the normal box bar, and L/R trades it with its neighbor.</summary>
    private void EnterBoxManageMode()
    {
        if (_viewModel.BoxCount == 0) return;
        if (_viewModel.BoxIndex < 0) _viewModel.BoxIndex = 0;
        _boxManageMode = true;
        _boxHeld = false;
        _heldBox = _viewModel.BoxIndex;
        _slotBeforeBoxManage = _viewModel.SelectedSlot;
        _viewModel.SelectedSlot = -1;
        _markedBoxes.Clear();
        if (_viewModel.SelectMode) _viewModel.ExitSelectMode();
        _viewModel.CancelCarry();
        _viewModel.Status = "BOX MANAGER - A HOLD · L/R BROWSE · Y SELECT · X ACTIONS";
        _sidePanel.IsVisible = false;
        _storageContent.ColumnDefinitions[1].Width = new GridLength(0);
        _storageContent.ColumnSpacing = 0;
        _canvas.EnableTouchEvents = false;
        _boxNeighbors.IsVisible = true;
        _boxNeighbors.HeightRequest = 100;
        SetBoxManageFooter();
        if (_boxManagePulseTimer is null)
        {
            _boxManagePulseTimer = Dispatcher.CreateTimer();
            _boxManagePulseTimer.Interval = TimeSpan.FromMilliseconds(60);
            _boxManagePulseTimer.Tick += BoxManagePulse;
        }
        _boxBar.InvalidateSurface();
    }

    private void BoxManagePulse(object? sender, EventArgs args) => _boxBar.InvalidateSurface();

    private void UpdateBoxManagePulse()
    {
        if (_boxManageMode && _boxHeld) _boxManagePulseTimer?.Start();
        else _boxManagePulseTimer?.Stop();
    }

    private void ExitBoxManageMode()
    {
        if (!_boxManageMode) return;
        _boxManageMode = false;
        _boxHeld = false;
        _markedBoxes.Clear();
        _boxManagePulseTimer?.Stop();
        _boxNeighbors.IsVisible = false;
        _canvas.EnableTouchEvents = true;
        _sidePanel.IsVisible = true;
        _storageContent.ColumnDefinitions[1].Width = new GridLength(330);
        _storageContent.ColumnSpacing = 12;
        _viewModel.SelectedSlot = _slotBeforeBoxManage;
        SetStorageFooter();
        ShowReady();
        _boxBar.InvalidateSurface();
    }

    private async Task ShiftHeldBoxAsync(int delta)
    {
        if (_boxManageBusy) return;
        var target = _heldBox + delta;
        if ((uint)target >= (uint)_viewModel.BoxCount)
        {
            _viewModel.Status = delta < 0 ? "BOX IS ALREADY FIRST" : "BOX IS ALREADY LAST";
            return;
        }

        _boxManageBusy = true;
        try
        {
            var from = _heldBox;
            var ok = await _viewModel.RunMutationAsync(session =>
            {
                session.SwapBoxes(from, target);
                return new GenerationOutcome(true, $"Box {from + 1:00} swapped with box {target + 1:00}.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!ok) return;
            _heldBox = target;
            _viewModel.BoxIndex = target;
            _viewModel.RefreshAllSlots();
            _viewModel.Status = $"HOLDING BOX {_heldBox + 1:00} - L/R SWAP · A DROP";
            _canvas.InvalidateSurface();
            _boxBar.InvalidateSurface();
            _boxNeighbors.InvalidateSurface();
        }
        finally { _boxManageBusy = false; }
    }

    private void BrowseManagedBoxes(int delta)
    {
        var target = _viewModel.BoxIndex + delta;
        if ((uint)target >= (uint)_viewModel.BoxCount) return;
        _viewModel.BoxIndex = target;
        _heldBox = target;
        SetBoxManageFooter();
        _boxNeighbors.InvalidateSurface();
    }

    private void ToggleMarkedBox()
    {
        var box = _viewModel.BoxIndex;
        if (!_markedBoxes.Remove(box)) _markedBoxes.Add(box);
        _viewModel.Status = _markedBoxes.Count == 0
            ? "BOX MANAGER - no boxes selected"
            : $"BOX MANAGER - {_markedBoxes.Count} BOX(ES) SELECTED";
        SetBoxManageFooter();
        _boxNeighbors.InvalidateSurface();
    }

    private async Task ShowBoxBulkActionsAsync()
    {
        if (_boxHeld || _boxManageBusy) return;
        var selected = _markedBoxes.Count == 0 ? new[] { _viewModel.BoxIndex } : _markedBoxes.OrderBy(x => x).ToArray();
        var noun = selected.Length == 1 ? $"BOX {selected[0] + 1:00}" : $"{selected.Length} SELECTED BOXES";
        var emptyLabel = _markedBoxes.Count > 0 ? "Empty selected boxes" : "Empty all boxes";
        var choice = await PadMenu.ShowAsync(_hostGrid, $"BOX ACTIONS · {noun}",
            Note("No selection means the current box. Every write creates a restore point."),
            Menu(
                new PadOption("Select all boxes", IconPath: "selectall"),
                new PadOption("Lock / Unlock box(es)", IconPath: "padlock"),
                Allowed(SaveAction.Duplicate, new("Copy box(es)…", IconPath: "copy")),
                new PadOption("Delete box(es) (rescue Pokémon)", IconPath: "delete"),
                new PadOption(emptyLabel, IconPath: "clear"),
                new PadOption("Clear selection", IconPath: "deselect")));
        if (choice == "Select all boxes")
        {
            _markedBoxes.Clear();
            foreach (var box in Enumerable.Range(0, _viewModel.BoxCount)) _markedBoxes.Add(box);
            _viewModel.Status = $"BOX MANAGER - {_markedBoxes.Count} BOXES SELECTED";
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        if (choice == "Clear selection")
        {
            _markedBoxes.Clear();
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        if (choice == "Lock / Unlock box(es)")
        {
            var docId = DocumentId;
            if (docId is null) return;
            var lockedCount = 0;
            foreach (var box in selected)
                if (Protection.ToggleBox(docId, box)) lockedCount++;
            _viewModel.Status = $"BOX LOCKS UPDATED - {Protection.LockedBoxes(docId).Count} BOX(ES) LOCKED";
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        var lockedTargets = selected.Where(box => DocumentId is not null && Protection.IsBoxLocked(DocumentId!, box)).ToList();
        if ((choice == "Empty selected boxes" || choice == "Delete box(es) (rescue Pokémon)") && lockedTargets.Count > 0)
        {
            _viewModel.Status = $"{lockedTargets.Count} TARGET BOX(ES) ARE LOCKED";
            return;
        }
        if (choice == emptyLabel && choice == "Empty selected boxes")
        {
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "EMPTY SELECTED BOXES?",
                $"Release every Pokémon in {selected.Length} box(es). A restore point is created first.", "Empty");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in selected) session.ClearBox(box);
                return new GenerationOutcome(true, $"Emptied {selected.Length} box(es).");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Release);
        }
        else if (choice == "Empty all boxes")
        {
            var docId = DocumentId;
            if (docId is not null && Protection.LockedBoxes(docId).Count > 0)
            {
                _viewModel.Status = "LOCKED BOXES ARE PROTECTED - UNLOCK OR EMPTY THEM ONE BY ONE";
                return;
            }
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "EMPTY EVERY BOX?",
                "Release every boxed Pokémon. Party Pokémon are untouched. A restore point is created first.", "Empty all");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in Enumerable.Range(0, _viewModel.BoxCount)) session.ClearBox(box);
                return new GenerationOutcome(true, "All storage boxes emptied.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Release);
        }
        else if (choice == "Copy box(es)…")
        {
            var starts = Enumerable.Range(0, _viewModel.BoxCount - selected.Length + 1)
                .Select(i => $"Start at box {i + 1:00}").ToArray();
            var targetChoice = await PadMenu.ShowAsync(_hostGrid, "COPY BOXES", "Choose the first destination box. Existing contents there will be replaced.", starts);
            var start = Array.IndexOf(starts, targetChoice);
            if (start < 0) return;
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "REPLACE DESTINATION BOXES?",
                $"Copy {selected.Length} box(es) starting at box {start + 1:00}. Destination contents will be replaced.", "Copy");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                var copies = selected.Select(source => Enumerable.Range(0, BoxGridRenderer.Columns * BoxGridRenderer.Rows)
                    .Where(slot => !session.ReadEntity(source, slot).IsEmpty)
                    .Select(slot => (Slot: slot, Data: session.ExportSlot(source, slot).Data)).ToArray()).ToArray();
                for (var i = 0; i < copies.Length; i++)
                {
                    session.ClearBox(start + i);
                    foreach (var copy in copies[i])
                        session.ImportSlot(start + i, copy.Slot, copy.Data);
                }
                return new GenerationOutcome(true, $"Copied {copies.Length} box(es) starting at box {start + 1:00}.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Duplicate);
        }
        else if (choice == "Delete box(es) (rescue Pokémon)")
        {
            var rescueNote = selected.Length >= _viewModel.BoxCount
                ? "Every box is selected: rescued Pokémon have nowhere to go, so they are released."
                : "Pokémon are rescued into free slots first.";
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "DELETE SELECTED BOXES?",
                $"Remove {selected.Length} box(es) from the order. {rescueNote}", "Delete");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in selected.OrderByDescending(x => x)) session.DeleteBox(box);
                return new GenerationOutcome(true, $"Removed {selected.Length} box(es) from the order.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
        }
        else return;

        _markedBoxes.Clear();
        _viewModel.RefreshAllSlots();
        _heldBox = _viewModel.BoxIndex;
        SetBoxManageFooter();
        _canvas.InvalidateSurface();
        _boxNeighbors.InvalidateSurface();
    }

    /// <summary>Bulk actions for the organizer's marked selection.</summary>
    private async Task ShowOrganizerMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, $"ORGANIZER · {_viewModel.MarkedCount} MARKED", Note(null),
            Menu(
                new PadOption("Move selection to box…", IconPath: "move"),
                new PadOption("Move selection to another game…", IconPath: "send"),
                Allowed(SaveAction.Duplicate, new("Copy selection to another game…", IconPath: "copy")),
                Allowed(SaveAction.Duplicate, new("Duplicate selection", IconPath: "copy")),
                new PadOption("Move selection to Bank", IconPath: "bank"),
                new PadOption("Export selection (.pk files)", IconPath: "export"),
                new PadOption("Release selection", IconPath: "release"),
                new PadOption("Done (exit organizer)", IconPath: "confirm")));
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
                // The marked mons leave this save after landing: it must accept that write.
                if (_viewModel.OpenSaveRefusesWrites()) return;

                var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
                var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
                    "MOVE SELECTION TO GAME", $"{_viewModel.MarkedCount} Pokémon leave this box", currentDoc);
                if (target is null) return;

                var confirm = await PadMenu.ConfirmAsync(_hostGrid, "MOVE SELECTION?",
                    $"{_viewModel.MarkedCount} Pokémon will leave this box and join {target.GameLabel}. Mons that cannot enter that format stay here.", "Move all");
                if (!confirm) return;

                // Bulk preview: the first marked mon's conversion diff stands for the
                // batch (per-mon prompts over N mons would be a questionnaire).
                var firstMarked = _viewModel.MarkedSlots.First();
                var firstExport = session.ExportSlot(firstMarked.Box, firstMarked.Slot);
                var firstPreview = await transfer.PreviewAsync(firstExport.Data, firstExport.FileName, target);
                if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, firstPreview,
                        $"1 of {_viewModel.MarkedCount} Pokémon", target.GameLabel)) return;

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
                var sources = _viewModel.MarkedSlots.ToArray();
                var used = new HashSet<(int Box, int Slot)>();
                await _viewModel.RunMutationAsync(s =>
                {
                    var cloned = 0;
                    foreach (var source in sources)
                    {
                        (int Box, int Slot)? landing = null;
                        foreach (var cand in _viewModel.Save!.Slots.Where(x => x.Box >= 0))
                        {
                            if (used.Contains((cand.Box, cand.Slot))) continue;
                            if (!s.ReadEntity(cand.Box, cand.Slot).IsEmpty) continue; // live check: never overwrite
                            landing = (cand.Box, cand.Slot);
                            break;
                        }
                        if (landing is null) break;
                        if (s.DuplicateSlot(source.Box, source.Slot, landing.Value.Box, landing.Value.Slot))
                        {
                            used.Add(landing.Value);
                            cloned++;
                        }
                    }
                    return new GenerationOutcome(cloned > 0,
                        cloned == 0 ? "No Pokémon could be duplicated. Check free storage."
                        : $"Cloned {cloned} Pokémon." + (cloned < sources.Length ? $" {sources.Length - cloned} could not be duplicated." : ""));
                }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.Duplicate);
                _viewModel.RefreshAllSlots();
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
                var docId = DocumentId;
                var session = _sessionsFor();
                if (docId is not null && session is not null)
                {
                    var locked = _viewModel.MarkedSlots
                        .Where(m => !Protection.CanRelease(docId, m.Box, m.Slot, session.GetRngInfo(m.Box, m.Slot).Pid))
                        .ToList();
                    if (locked.Count > 0)
                    {
                        _viewModel.Status = $"{locked.Count} MARKED MON(ES) ARE LOCKED - UNLOCK OR UNMARK THEM";
                        return;
                    }
                }
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
        if (Denied(SaveAction.CreateMon)) return;
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
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.CreateMon);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>Paste a full Showdown team; each set is legalized into the next empty slot.</summary>
    private async Task ImportShowdownTeamAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
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
                var ok = await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, set, Services.HaXMode.IsOn), slot);
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
        if (Denied(SaveAction.CreateMon)) return;
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "GENERATE LIVING DEX?",
            "This OVERWRITES every box with one legal Pokémon of every species this game supports. " +
            "The current save state is kept as a restore point. This can take several minutes.",
            "Fill my boxes");
        if (!confirmed) return;

        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "No save connected."; return; }
        byte[]? bundle = null;
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync($"dex/dex-g{session.Generation}.bin.gz");
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bundle = memory.ToArray();
        }
        catch
        {
            _viewModel.Status = "No living dex bundle for this generation yet.";
            return;
        }

        var overlay = LoadingOverlay.Show(_hostGrid, "FILLING THE LIVING DEX…",
            "One of every species, copied from the pre-generated dex. This is a straight write.");
        try
        {
            await _viewModel.RunMutationAsync(s =>
            {
                var outcome = ((ILegalizerService)IPlatformApplication.Current!.Services.GetRequiredService(typeof(ILegalizerService)))
                    .FillLivingDex(s, bundle!);
                return outcome;
            }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.CreateMon);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>Auto-sort: pick a criteria, pick a scope, one backed-up write compacts mons front.</summary>
    private async Task ShowSortMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "SORT", "How should the boxes be ordered?",
            new PadOption("Dex number", IconPath: "pokedex"),
            new PadOption("Alphabetical", IconPath: "alpha"),
            new PadOption("Level (strongest first)", IconPath: "level"),
            new PadOption("IV total (best first)", IconPath: "stats"),
            new PadOption("Type", IconPath: "type"),
            new PadOption("Age (oldest first)", IconPath: "calendar"),
            new PadOption("Shiny first", IconPath: "shiny"));
        if (choice is null) return;

        var scope = await PadMenu.ShowAsync(_hostGrid, "SORT", "Which boxes?",
            new PadOption("This box", IconPath: "box"),
            new PadOption("All boxes", IconPath: "storage"));

        var direction = await PadMenu.ShowAsync(_hostGrid, "SORT", "Which direction?",
            new PadOption("Normal order", IconPath: "sort"),
            new PadOption("Reversed", IconPath: "reverse"));
        if (direction is null) return;
        var reverse = direction == "Reversed";

        var criteria = choice switch
        {
            "Alphabetical" => Domain.SortCriteria.Alphabetical,
            "Level (strongest first)" => Domain.SortCriteria.LevelDesc,
            "IV total (best first)" => Domain.SortCriteria.IvTotalDesc,
            "Type" => Domain.SortCriteria.Type,
            "Age (oldest first)" => Domain.SortCriteria.AgeOldest,
            "Shiny first" => Domain.SortCriteria.ShinyFirst,
            _ => Domain.SortCriteria.DexNumber,
        };
        IReadOnlyList<int>? boxes = scope == "This box" ? [_viewModel.BoxIndex] : null;

        var directionNote = reverse ? " (reversed)" : "";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "SORT NOW?",
            (scope == "This box"
                ? "This box's Pokémon are reordered and compacted to the top."
                : "Every box's Pokémon are pooled, ordered, and compacted from box 1. Empties gather at the end.")
            + directionNote,
            "Sort");
        if (!confirmed) return;

        var sorted = await _viewModel.RunMutationAsync(session =>
        {
            // Locked boxes keep their contents exactly where they are.
            if (boxes is null && DocumentId is { } docId)
            {
                var locked = Protection.LockedBoxes(docId);
                if (locked.Count > 0)
                    boxes = Enumerable.Range(0, _viewModel.BoxCount).Where(box => !locked.Contains(box)).ToList();
            }
            var placed = session.SortBoxes(criteria, boxes, reverse);
            return new GenerationOutcome(true, $"Sorted {placed} Pokémon{directionNote}.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
        if (sorted)
            _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>One-tap preset packs: competitive, speedrun, casual - all through the batch editor.</summary>
    private async Task ShowPresetsMenuAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var perfectLabel = caps.IvMax == 15 ? "6DV (perfect DVs)" : "6IV (perfect IVs)";
        var scope = await PadMenu.ShowAsync(_hostGrid, "PRESETS", "Apply to which boxes?",
            new PadOption("This box", IconPath: "box"),
            new PadOption("All unlocked boxes", IconPath: "storage"));
        if (scope is null) return;
        IReadOnlyList<int>? boxes;
        if (scope == "This box") boxes = [_viewModel.BoxIndex];
        else
        {
            var docId = DocumentId;
            var unlocked = Enumerable.Range(0, _viewModel.BoxCount)
                .Where(box => docId is null || !Protection.IsBoxLocked(docId, box)).ToList();
            if (unlocked.Count == 0) { _viewModel.Status = "EVERY BOX IS LOCKED"; return; }
            boxes = unlocked;
        }

        var choice = await PadMenu.ShowAsync(_hostGrid, "PRESETS", "One backed-up write applies everything.",
            new PadOption("Level 50 flat", IconPath: "level"),
            new PadOption("Level 100", IconPath: "level"),
            new PadOption(perfectLabel, IconPath: "stats"),
            new PadOption("0 Attack IV (special)", IconPath: "stats"),
            new PadOption("0 Speed IV (Trick Room)", IconPath: "stats"),
            new PadOption("Reset EVs", IconPath: "restore"),
            new PadOption("Max friendship", IconPath: "heart"),
            new PadOption("Hyper Train everything", IconPath: "train"),
            new PadOption("Export box (Showdown)", IconPath: "script"),
            new PadOption("Import Showdown sets to this box", IconPath: "script"));
        if (choice is null) return;

        if (choice == "Export box (Showdown)")
        {
            await ExportBoxShowdownFromEngineAsync();
            return;
        }
        if (choice == "Import Showdown sets to this box")
        {
            await ImportShowdownSetsToBoxAsync();
            return;
        }

        IReadOnlyList<string> instructions = choice switch
        {
            "Level 50 flat" => ["Level=50"],
            "Level 100" => ["Level=100"],
            var perfect when perfect == perfectLabel =>
                [.. new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" }.Select(stat => $"IV_{stat}={caps.IvMax}")],
            "0 Attack IV (special)" => ["IV_ATK=0"],
            "0 Speed IV (Trick Room)" => ["IV_SPE=0"],
            "Reset EVs" => ["EV_HP=0", "EV_ATK=0", "EV_DEF=0", "EV_SPA=0", "EV_SPD=0", "EV_SPE=0"],
            "Max friendship" => ["Friendship=255"],
            "Hyper Train everything" => ["HyperTrain"],
            _ => [],
        };
        if (instructions.Count == 0) return;

        var scopeText = boxes.Count == _viewModel.BoxCount ? "every unlocked box" : $"box {_viewModel.BoxIndex + 1:00}";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "APPLY PRESET?",
            $"{choice} on {scopeText}. Backed up first.", "Apply");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(s =>
        {
            var touched = s.BatchApply(instructions, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"Preset applied to {touched} Pokémon.")
                : new GenerationOutcome(false, "Nothing to edit there.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    private async Task ExportBoxShowdownFromEngineAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.ExportBoxShowdown(_viewModel.BoxIndex);
        if (text.Length == 0) { _viewModel.Status = "THIS BOX IS EMPTY"; return; }
        var path = Path.Combine(FileSystem.CacheDirectory, $"box-{_viewModel.BoxIndex + 1:00}-showdown.txt");
        File.WriteAllText(path, text);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = $"Box {_viewModel.BoxIndex + 1:00} Showdown", File = new ShareFile(path) });
    }

    private async Task ImportShowdownSetsToBoxAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var text = await TextPopup.ShowAsync(_hostGrid, "IMPORT SHOWDOWN SETS",
            "Paste one set per Pokémon (blank line between sets). Each fills an empty slot in this box.");
        if (string.IsNullOrWhiteSpace(text)) return;
        var sets = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sets.Length == 0) return;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "GENERATE SETS?",
            $"{sets.Length} legal Pokémon are generated into this box's empty slots.", "Generate");
        if (!confirmed) return;
        var box = _viewModel.BoxIndex;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var overlay = LoadingOverlay.Show(_hostGrid, "GENERATING SETS…", "The legalizer builds each set offline.");
        try
        {
            await _viewModel.RunMutationAsync(s =>
            {
                var placed = 0;
                foreach (var set in sets)
                {
                    for (var slot = 0; slot < BoxGridRenderer.Rows * BoxGridRenderer.Columns; slot++)
                    {
                        if (!s.ReadEntity(box, slot).IsEmpty) continue;
                        if (legalizer.GenerateFromShowdown(s, box, slot, set, Services.HaXMode.IsOn).Success) { placed++; break; }
                    }
                }
                return placed > 0
                    ? new GenerationOutcome(true, $"Imported {placed} sets into box {box + 1:00}.")
                    : new GenerationOutcome(false, "No empty slots (or no readable sets).");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.CreateMon);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>The speedrunner view: PID, EC, IVs, and a shiny-safe nature reroll.</summary>
    private async Task ShowRngAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var box = _viewModel.BoxIndex;
        var rng = session.GetRngInfo(box, slot);
        if (!rng.NatureRerollSupported)
        {
            _viewModel.Status = "THIS GENERATION HAS NO NATURES";
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var current = rng;
        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar("RNG / IVs"),
                new Label { Text = $"PID      {current.Pid:X8}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = current.EncryptionConstant is { } ec ? $"EC       {ec:X8}" : "EC       (not in this generation)", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"IVs      {string.Join("/", current.IVs)}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"NATURE   {data.NatureNames[Math.Clamp(current.Nature, 0, data.NatureNames.Count - 1)]}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"SHINY    {(current.Shiny ? "YES" : "NO")}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = current.Shiny ? UiTokens.Gold : UiTokens.Ink0 },
            },
        };
        var window = Kit.OverlayWindow(_hostGrid, stack);

        var reroll = Kit.Capsule("REROLL NATURE (KEEPS SHINY)", UiTokens.Green);
        // A nature reroll edits the mon, so Hardcore mode hides the affordance and leaves
        // the read-out (PID, EC, IVs) alone - viewing stays, editing does not.
        reroll.IsVisible = Guard.CanEditMon;
        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        if (HardcoreMode.IsOn)
            stack.Children.Add(new Label
            {
                Text = HardcoreMode.StatusLine,
                FontFamily = DsChrome.PixelFont,
                FontSize = 11,
                TextColor = UiTokens.Bad,
            });
        stack.Children.Add(reroll);
        stack.Children.Add(close);

        var done = new TaskCompletionSource();
        var overlay = Kit.AttachOverlay(_hostGrid, window, () => done.TrySetResult());
        reroll.Clicked += async (_, _) =>
        {
            // Ids come from the pick itself: an index into a filtered name list skipped Hardy
            // and shifted every choice by one.
            var session = _sessionsFor();
            var preview = session is null ? null : NaturePicker.Service?.PreviewSlot(session, box, slot);
            var picked = await NaturePicker.ShowAsync(overlay, data.NatureNames, current.Nature, preview, "PICK A NATURE");
            if (picked is null) return;
            var nature = picked.Id;
            var targetBox = box;
            var targetSlot = slot;
            var ok = await _viewModel.RunMutationAsync(s => s.RerollNatureKeepShiny(targetBox, targetSlot, nature)
                ? new GenerationOutcome(true, "Nature rerolled; shiny state kept.")
                : new GenerationOutcome(false, "Could not find a matching PID. Try again."), targetSlot);
            if (ok)
            {
                current = _sessionsFor()!.GetRngInfo(targetBox, targetSlot);
                _viewModel.Status = $"PID {current.Pid:X8} · NATURE {data.NatureNames[Math.Clamp(current.Nature, 0, data.NatureNames.Count - 1)]} · SHINY {(current.Shiny ? "YES" : "NO")}";
            }
            _canvas.InvalidateSurface();
        };
        close.Clicked += (_, _) => { _hostGrid.Remove(overlay); done.TrySetResult(); };
        await done.Task;
    }

    /// <summary>First catch per route from met data: the post-run Nuzlocke audit.</summary>
    private async Task ShowNuzlockeReportAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var report = session.GetNuzlockeReport();
        if (report.Count == 0)
        {
            _viewModel.Status = "NO CATCH DATA IN THIS SAVE";
            return;
        }

        var rows = new VerticalStackLayout { Spacing = 4 };
        foreach (var group in report.GroupBy(c => c.Route))
        {
            rows.Children.Add(new Label
            {
                Text = group.Key.ToUpperInvariant(),
                FontFamily = DsChrome.PixelFont,
                FontSize = 14,
                TextColor = UiTokens.Maroon,
            });
            foreach (var catchRow in group)
                rows.Children.Add(new Label
                {
                    Text = $"   {(catchRow.FirstCatch ? "FIRST" : "dupe")} - {catchRow.Name}{(catchRow.MetDate is { } d ? $" ({d})" : "")}",
                    FontSize = 12,
                    TextColor = catchRow.FirstCatch ? UiTokens.Ink0 : UiTokens.Ink1,
                });
        }

        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Kit.HeaderBar("NUZLOCKE REPORT"), new ScrollView { Content = rows, MaximumHeightRequest = 420 }, close },
        };
        var done = new TaskCompletionSource();
        var overlay = Kit.AttachOverlay(_hostGrid, Kit.OverlayWindow(_hostGrid, content), () => done.TrySetResult());
        close.Clicked += (_, _) => { _hostGrid.Remove(overlay); done.TrySetResult(); };
        await done.Task;
    }

    /// <summary>The whole-save legality sweep: off-thread scan behind the walking
    /// overlay, cached per write-generation so reopening the tool is instant until the
    /// save changes. Dots appear on the current box; illegal mons offer repairs.</summary>
    private async Task ShowLegalityCheckAsync()
    {
        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "Open a save first."; return; }
        if (!session.SupportsLegalityAnalysis)
        {
            await PadMenu.ShowAsync(_hostGrid, "LEGALITY CHECK",
                "This save is a romhack format without offline legality tables - nothing to check here.", "OK");
            return;
        }

        var overlay = default(LoadingOverlay);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var results = await _viewModel.GetLegalitySweepAsync(
                () =>
                {
                    overlay = LoadingOverlay.Show(_hostGrid, "CHECKING LEGALITY…", "Scanning the party and every box.");
                    overlay.Cancellation.Token.Register(cancellation.Cancel);
                },
                (done, total) => overlay?.Report(done, total),
                cancellation.Token);
            if (results.Count == 0)
            {
                _viewModel.Status = "This save stores no Pokémon.";
                return;
            }
            var illegal = results.Where(r => !r.Valid).ToList();

            var choice = illegal.Count == 0
                ? await PadMenu.ShowAsync(_hostGrid, "LEGALITY CHECK", $"All {results.Count} Pokémon are legal.", "OK")
                : await PadMenu.ShowAsync(_hostGrid, "LEGALITY CHECK",
                    Note($"{illegal.Count} illegal of {results.Count}. The red-dotted slots in this box fail PKHeX's checks."),
                    Menu(Allowed(SaveAction.EditMon, new("Legalize all illegal", IconPath: "fix")),
                        new PadOption("Close", IconPath: "close")));
            if (choice == "Legalize all illegal")
                await LegalizeAllIllegalAsync(illegal);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Legality check cancelled.";
        }
        finally
        {
            overlay?.Close();
        }
    }

    /// <summary>One confirmed, backed-up write that repairs every flagged slot: the
    /// engine legalizes each mon in place inside a single mutation.</summary>
    private async Task LegalizeAllIllegalAsync(IReadOnlyList<SlotLegality> illegal)
    {
        var session = _sessionsFor();
        var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
        if (session is null || legalizer is null) return;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "LEGALIZE ALL ILLEGAL?",
            $"{illegal.Count} Pokémon will be rewritten to their closest legal versions. " +
            "The current state stays available as a restore point.", "LEGALIZE");
        if (!confirmed) return;

        var targets = illegal.Select(v => (v.Box, v.Slot)).ToList();
        var overlay = LoadingOverlay.Show(_hostGrid, "LEGALIZING…", "Repairing every flagged Pokémon in one write.");
        try
        {
            var ok = await _viewModel.RunMutationAsync(
                s => legalizer.LegalizeSlots(s, targets, (done, total) => overlay.Report(done, total)),
                Math.Max(0, _viewModel.SelectedSlot),
                refreshSlot: false,
                changeDescription: $"Legalize {targets.Count} flagged Pokémon",
                action: SaveAction.EditMon);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
            if (!ok)
                await PadMenu.ShowAsync(_hostGrid, "LEGALIZE ALL", _viewModel.Status, "OK");
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>The clone/hack audit: identical (EC,PID) or (PID,OT,TID) groups across
    /// the whole save plus impossible values from the legality sweep, as one scrollable
    /// report. The bank is excluded on purpose: its entries keep no EC/PID fingerprints.</summary>
    private async Task ShowAuditReportAsync()
    {
        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "Open a save first."; return; }
        if (!session.SupportsLegalityAnalysis)
        {
            await PadMenu.ShowAsync(_hostGrid, "AUDIT REPORT",
                "This save is a romhack format without offline legality tables - nothing to audit here.", "OK");
            return;
        }

        var overlay = default(LoadingOverlay);
        using var cancellation = new CancellationTokenSource();
        IReadOnlyList<SlotLegality> sweep;
        IReadOnlyList<MonFingerprint> fingerprints;
        try
        {
            sweep = await _viewModel.GetLegalitySweepAsync(
                () =>
                {
                    overlay = LoadingOverlay.Show(_hostGrid, "AUDITING…", "Fingerprinting every Pokémon and checking values.");
                    overlay.Cancellation.Token.Register(cancellation.Cancel);
                },
                (done, total) => overlay?.Report(done, total),
                cancellation.Token);
            fingerprints = await Task.Run(() => CollectFingerprints(session), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Audit cancelled.";
            overlay?.Close();
            return;
        }
        finally
        {
            overlay?.Close();
        }

        var clones = CollectionAudit.GroupClones(fingerprints);
        var illegal = sweep.Where(r => !r.Valid).ToList();
        _viewModel.Status = clones.Count == 0 && illegal.Count == 0
            ? "AUDIT: NO CLONES, NO IMPOSSIBLE VALUES"
            : $"AUDIT: {clones.Count} CLONE GROUPS, {illegal.Count} FLAGGED";

        var report = BuildAuditReport(clones, illegal, fingerprints.Count);
        var rows = new VerticalStackLayout { Spacing = 4 };
        foreach (var line in report)
        {
            var isHeader = line.StartsWith("## ", StringComparison.Ordinal);
            rows.Children.Add(new Label
            {
                Text = isHeader ? line[3..].ToUpperInvariant() : line,
                FontFamily = DsChrome.PixelFont,
                FontSize = isHeader ? 14 : 12,
                TextColor = isHeader ? UiTokens.Maroon : UiTokens.Ink0,
            });
        }

        var share = Kit.Capsule("SHARE REPORT", UiTokens.Cyan);
        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { share, close } };
        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Kit.HeaderBar("AUDIT REPORT"), new ScrollView { Content = rows, MaximumHeightRequest = 420 }, buttons },
        };

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlayGrid = null!;
        PadOverlay pad = null!;
        void Close()
        {
            _hostGrid.Remove(overlayGrid);
            pad?.Dispose();
            done.TrySetResult();
        }
        overlayGrid = Kit.AttachOverlay(_hostGrid, Kit.OverlayWindow(_hostGrid, content), Close);
        pad = new PadOverlay(Close, Close);
        close.Clicked += (_, _) => Close();
        share.Clicked += async (_, _) => await ShareAuditReportAsync(string.Join(Environment.NewLine, report.Select(Strip)));
        await done.Task;

        static string Strip(string line) => line.StartsWith("## ", StringComparison.Ordinal) ? line[3..] : line;
    }

    /// <summary>The identity facts the clone grouping runs on, for every occupied slot.</summary>
    private static IReadOnlyList<MonFingerprint> CollectFingerprints(Domain.ISaveEngineSession session)
    {
        var fingerprints = new List<MonFingerprint>();
        foreach (var summary in session.Snapshot.Slots)
        {
            if (summary.Species is null) continue;
            var rng = session.GetRngInfo(summary.Box, summary.Slot);
            var detail = session.ReadEntity(summary.Box, summary.Slot);
            var tid = session.GetMetInfo(summary.Box, summary.Slot).TID;
            fingerprints.Add(new MonFingerprint(
                rng.Pid, rng.EncryptionConstant, detail.OriginalTrainer, tid,
                summary.Box == -1 ? $"Party {summary.Slot + 1}" : $"B{summary.Box + 1}-{summary.Slot + 1}",
                detail.Nickname is { Length: > 0 } ? detail.Nickname : detail.SpeciesName));
        }
        return fingerprints;
    }

    /// <summary>Plain-text audit body: clone sections then impossible values.</summary>
    private static IReadOnlyList<string> BuildAuditReport(
        IReadOnlyList<CloneGroup> clones, IReadOnlyList<SlotLegality> illegal, int total)
    {
        var lines = new List<string>
        {
            $"{total} Pokémon audited in this save.",
            string.Empty,
        };
        if (clones.Count == 0)
        {
            lines.Add("No clones: every Pokémon has a unique identity fingerprint.");
        }
        else
        {
            lines.Add($"## Clones ({clones.Count} groups)");
            foreach (var group in clones)
            {
                lines.Add($"{group.KeyKind} match ×{group.Members.Count}:");
                lines.AddRange(group.Members.Select(m => $"   {m.SlotLabel} - {m.DisplayName}"));
            }
        }
        lines.Add(string.Empty);
        if (illegal.Count == 0)
        {
            lines.Add("No impossible values: every Pokémon passed PKHeX's checks.");
        }
        else
        {
            lines.Add($"## Impossible values ({illegal.Count})");
            foreach (var verdict in illegal)
            {
                var label = verdict.Box == -1 ? $"Party {verdict.Slot + 1}" : $"B{verdict.Box + 1}-{verdict.Slot + 1}";
                lines.Add($"{label} - {verdict.Problem}");
            }
        }
        lines.Add(string.Empty);
        lines.Add("Bank audit is future work: bank entries keep no EC/PID fingerprint.");
        return lines;
    }

    /// <summary>Hands the audit text to Android's share sheet as a file (the .pk export path).</summary>
    private static async Task ShareAuditReportAsync(string text)
    {
        try
        {
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, "pkforge-audit.txt");
            await File.WriteAllTextAsync(path, text);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "PKForge audit report",
                File = new ShareFile(path),
            });
        }
        catch (Exception error)
        {
            // Sharing is best-effort; the on-screen report is the source of truth.
            _ = error.Message;
        }
    }

    /// <summary>Mass egg generation: living egg dex, or one species filling this box.</summary>
    private async Task ShowEggFactoryAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        var choice = await PadMenu.ShowAsync(_hostGrid, "EGG FACTORY", "Every egg fills an empty PC slot. One backed-up write.",
            new PadOption("One egg of every species", IconPath: "egg"),
            new PadOption("Pick a species…", IconPath: "pokedex"));
        if (choice is null) return;

        IReadOnlyList<int> species;
        if (choice == "Pick a species…")
        {
            var picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            if (picked is null) return;
            species = [picked.Id];
        }
        else
        {
            species = Enumerable.Range(1, Math.Min(data.SpeciesNames.Count - 1, session.GetDexProgress().Total))
                .Where(id => data.SpeciesNames[id].Length > 0)
                .ToList();
        }

        var caps = session.GetTrainingCaps();
        var statKind = caps.IvMax == 15 ? "DVs" : "IVs";
        var maxIv = await PadMenu.ConfirmAsync(_hostGrid, $"PERFECT {statKind.ToUpperInvariant()}?",
            $"Every egg gets {caps.IvMax} {statKind} in all six stats.", $"Yes, 6{(caps.IvMax == 15 ? "DV" : "IV")}");
        var shiny = await PadMenu.ConfirmAsync(_hostGrid, "SHINY EGGS?", "Every egg hatches shiny.", "Yes, shiny");

        var options = new Domain.EggOptions(maxIv, shiny);
        var overlay = LoadingOverlay.Show(_hostGrid, "GENERATING EGGS…", "The legalizer builds and egg-ifies each species offline.");
        try
        {
            var list = species;
            await _viewModel.RunMutationAsync(s => legalizer.GenerateEggs(s, list, options,
                (done, total) => overlay.Report(done, total)), Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>One-tap battle preparation: heal, PP Max, flat rules - one backed-up write per action.</summary>
    private async Task ShowBattlePrepAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, "BATTLE PREP", "Every action is one backed-up write.",
            new PadOption("Heal party", IconPath: "heal"),
            new PadOption("Heal all (party + boxes)", IconPath: "heal"),
            new PadOption("PP Max all moves", IconPath: "moves"),
            new PadOption("Set party to Lv50 (flat rules)", IconPath: "level"),
            new PadOption("Set all to Lv100", IconPath: "level"));
        if (choice is null) return;

        var party = new[] { -1 };
        var everywhere = PartyAndUnlockedBoxes();
        var everywhereText = everywhere.Count == 1
            ? "the party only (every box is locked)"
            : "the party and every unlocked box";
        IReadOnlyList<string> instructions;
        IReadOnlyList<int> boxes;
        string what;
        if (choice == "Heal party")
        {
            instructions = ["Heal"];
            boxes = party;
            what = "the party";
        }
        else if (choice == "Heal all (party + boxes)")
        {
            instructions = ["Heal"];
            boxes = everywhere;
            what = everywhereText;
        }
        else if (choice == "PP Max all moves")
        {
            // PP Ups persist even in box storage; current PP only lives in party data,
            // so HealPP tops off the party and is a no-op write for boxed mons.
            instructions = ["Move1_PPUps=3", "Move2_PPUps=3", "Move3_PPUps=3", "Move4_PPUps=3", "HealPP"];
            boxes = everywhere;
            what = everywhereText;
        }
        else if (choice == "Set party to Lv50 (flat rules)")
        {
            instructions = ["Level=50"];
            boxes = party;
            what = "the party";
        }
        else
        {
            instructions = ["Level=100"];
            boxes = everywhere;
            what = everywhereText;
        }

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, $"{choice.ToUpperInvariant()}?",
            $"Applies to {what}. Backed up first.", choice);
        if (!confirmed) return;

        await _viewModel.RunMutationAsync(s =>
        {
            var touched = s.BatchApply(instructions, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"{choice} applied to {touched} Pokémon.")
                : new GenerationOutcome(false, "Nothing to edit there.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>The party plus every unlocked box: battle prep's whole-save scope.</summary>
    private IReadOnlyList<int> PartyAndUnlockedBoxes()
    {
        var docId = DocumentId;
        return [-1, .. Enumerable.Range(0, _viewModel.BoxCount)
            .Where(box => docId is null || !Protection.IsBoxLocked(docId, box))];
    }

    /// <summary>
    /// The batch editor: stack operations as chips (level, IVs, EV spread, heal, training,
    /// friendship, nicknames), then apply them to a scope - the organizer selection, one
    /// box, or every box - in a single backed-up write. The PKHeX instruction strings are
    /// shown before the write; experts can paste their own on top.
    /// </summary>
    private async Task RunBatchEditorAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var statKind = caps.IvMax == 15 ? "DVs" : "IVs";

        // Scope first. Locked boxes refuse batch edits (same rule as sort and presets).
        var scopeOptions = new List<PadOption>();
        if (_viewModel.MarkedCount > 0)
            scopeOptions.Add(new($"Selected Pokémon ({_viewModel.MarkedCount})", IconPath: "select"));
        scopeOptions.Add(new("This box", IconPath: "box"));
        scopeOptions.Add(new("All boxes", IconPath: "storage"));
        var scope = await PadMenu.ShowAsync(_hostGrid, "BATCH EDITOR", "Apply to which Pokémon?", scopeOptions.ToArray());
        if (scope is null) return;
        var selection = scope.StartsWith("Selected", StringComparison.Ordinal);

        IReadOnlyList<(int Box, int Slot)>? slots = null;
        IReadOnlyList<int>? boxes = null;
        if (selection)
        {
            var docId = DocumentId;
            slots = _viewModel.MarkedSlots
                .Where(m => docId is null || !Protection.IsBoxLocked(docId, m.Box))
                .ToList();
            if (slots.Count == 0) { _viewModel.Status = "EVERY MARKED BOX IS LOCKED"; return; }
        }
        else if (scope == "This box")
        {
            if (DocumentId is { } docId && _viewModel.BoxIndex >= 0 && Protection.IsBoxLocked(docId, _viewModel.BoxIndex))
            {
                _viewModel.Status = "THIS BOX IS LOCKED";
                return;
            }
            boxes = [_viewModel.BoxIndex];
        }
        else
        {
            var docId = DocumentId;
            boxes = Enumerable.Range(0, _viewModel.BoxCount)
                .Where(box => docId is null || !Protection.IsBoxLocked(docId, box)).ToList();
            if (boxes.Count == 0) { _viewModel.Status = "EVERY BOX IS LOCKED"; return; }
        }

        // Chip flow: each entry toggles; the menu re-opens until Apply or Cancel.
        var level = false;
        var maxIvs = false;
        var heal = false;
        var train = false;
        var friendship = false;
        var speciesNames = false;
        string[]? evSpread = null;
        var evSummary = "none";
        IReadOnlyList<string>? expertLines = null;
        while (true)
        {
            PadOption Chip(string label, bool on, string icon) => new($"{(on ? "✓ " : "")}{label}", IconPath: icon);
            var levelChip = Chip("Level = 100", level, "level");
            var ivChip = Chip($"Max {statKind} ({caps.IvMax} everywhere)", maxIvs, "stats");
            var evChip = new PadOption($"EV spread: {evSummary}", IconPath: "stats");
            var healChip = Chip("Heal + PP Max", heal, "heart");
            var trainChip = Chip("Hyper Train (legal $suggest)", train, "gears");
            var friendChip = Chip("Max friendship", friendship, "heart");
            var nameChip = Chip("Nicknames → species names", speciesNames, "script");
            var pick = await PadMenu.ShowAsync(_hostGrid, "BATCH EDITOR", "Toggle operations, then apply.",
                levelChip, ivChip, evChip, healChip, trainChip, friendChip, nameChip,
                new PadOption("Expert instructions…", IconPath: "code"),
                new PadOption("Apply", IconPath: "confirm"),
                new PadOption("Cancel", IconPath: "close"));
            if (pick is null or "Cancel") return;
            if (pick == "Apply") break;
            if (pick == levelChip.Label) level = !level;
            else if (pick == ivChip.Label) maxIvs = !maxIvs;
            else if (pick == healChip.Label) heal = !heal;
            else if (pick == trainChip.Label) train = !train;
            else if (pick == friendChip.Label) friendship = !friendship;
            else if (pick == nameChip.Label) speciesNames = !speciesNames;
            else if (pick == evChip.Label)
            {
                var spreads = new (string Label, string Summary, string[] Instructions)[]
                {
                    ("Physical sweeper (252 Atk / 252 Spe / 4 HP)", "Atk/Spe 252+4", ["EV_HP=4", "EV_ATK=252", "EV_SPE=252"]),
                    ("Special sweeper (252 SpA / 252 Spe / 4 HP)", "SpA/Spe 252+4", ["EV_HP=4", "EV_SPA=252", "EV_SPE=252"]),
                    ("Physical wall (252 HP / 252 Def / 4 SpD)", "HP/Def 252+4", ["EV_HP=252", "EV_DEF=252", "EV_SPD=4"]),
                    ("Special wall (252 HP / 252 SpD / 4 Def)", "HP/SpD 252+4", ["EV_HP=252", "EV_SPD=252", "EV_DEF=4"]),
                    ("Reset EVs (all zero)", "reset", ["EV_HP=0", "EV_ATK=0", "EV_DEF=0", "EV_SPA=0", "EV_SPD=0", "EV_SPE=0"]),
                    ("Keep EVs as they are", "none", []),
                };
                var spreadPick = await PadMenu.ShowAsync(_hostGrid, "EV SPREAD", "One spread replaces the previous choice.",
                    spreads.Select(s => new PadOption(s.Label, IconPath: "stats")).ToArray());
                var chosen = spreads.FirstOrDefault(s => s.Label == spreadPick);
                if (chosen.Label is not null)
                {
                    evSpread = chosen.Instructions;
                    evSummary = chosen.Summary;
                }
            }
            else
            {
                var text = await TextPopup.ShowAsync(_hostGrid, "EXPERT INSTRUCTIONS",
                    $"One per line, PKHeX style, added on top of the chips:\nLevel=100\nIV_HP={caps.IvMax}\nShiny=Yes\nEV_ATK={caps.EvMax}");
                if (!string.IsNullOrWhiteSpace(text))
                    expertLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        var plan = new List<string>();
        if (level) plan.Add("Level=100");
        if (maxIvs)
            foreach (var stat in new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" })
                plan.Add($"IV_{stat}={caps.IvMax}");
        if (evSpread is { Length: > 0 }) plan.AddRange(evSpread);
        if (heal)
        {
            // PP Ups first so Heal's built-in PP refill tops off at the raised maximum.
            plan.AddRange(["Move1_PPUps=3", "Move2_PPUps=3", "Move3_PPUps=3", "Move4_PPUps=3", "Heal"]);
        }
        if (train) plan.Add("HyperTrain=$suggest");
        if (friendship) plan.Add("Friendship=255");
        if (speciesNames) plan.Add("IsNicknamed=false");
        if (expertLines is not null) plan.AddRange(expertLines.Where(line => line.Length > 0));
        if (plan.Count == 0) { _viewModel.Status = "NO OPERATIONS SELECTED"; return; }

        var mons = 0;
        if (slots is not null)
        {
            mons = slots.Count(s => !session.ReadEntity(s.Box, s.Slot).IsEmpty);
        }
        else
        {
            foreach (var box in boxes!)
            {
                var slotCount = box == -1 ? 6 : BoxGridRenderer.Columns * BoxGridRenderer.Rows;
                for (var slot = 0; slot < slotCount; slot++)
                    if (!session.ReadEntity(box, slot).IsEmpty) mons++;
            }
        }
        var scopeText = selection
            ? $"{slots!.Count} selected Pokémon"
            : boxes!.Count == 1
                ? (_viewModel.BoxIndex == -1 ? "the party" : $"box {boxes[0] + 1:00}")
                : (boxes.Count == _viewModel.BoxCount ? "every box" : $"{boxes.Count} unlocked boxes");

        // Dry run first: the plan runs on copies so the confirm can say what would change
        // and what would turn illegal before anything is written.
        BatchDryRun? dryRun = null;
        if (InfoPickers.Info is { } info)
        {
            var rehearsal = LoadingOverlay.Show(_hostGrid, "CHECKING…", "Trying the batch edit on copies first.");
            try { dryRun = await Task.Run(() => info.DryRunBatch(session, plan, boxes, slots)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
            finally { rehearsal.Close(); }
        }
        if (dryRun is { Targeted: > 0, Affected: 0 })
        {
            await PadMenu.ShowAsync(_hostGrid, "NOTHING WOULD CHANGE", $"The {dryRun.Targeted} Pokémon in {scopeText} already match every operation.", "OK");
            return;
        }
        var impact = dryRun is null
            ? $"Will apply {plan.Count} operation(s) to {mons} Pokémon in {scopeText}."
            : $"{dryRun.Affected} of {dryRun.Targeted} Pokémon in {scopeText} would change." +
              (dryRun.BecomeIllegal > 0 ? $"\n⚠ {dryRun.BecomeIllegal} would become ILLEGAL." : "\nNone would become illegal.") +
              (dryRun.AlreadyIllegal > 0 ? $"\n{dryRun.AlreadyIllegal} of them are already illegal." : "");
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, dryRun is { BecomeIllegal: > 0 } ? "APPLY? SOME WOULD TURN ILLEGAL" : "APPLY BATCH EDIT?",
            $"{impact}\n{string.Join(" · ", plan)}\nOne backed-up write.",
            "Apply");
        if (!confirmed) return;

        var ok = await _viewModel.RunMutationAsync(s =>
        {
            var touched = slots is not null ? s.BatchApplySlots(slots, plan) : s.BatchApply(plan, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"Batch applied to {touched} Pokémon (legality not re-checked).")
                : new GenerationOutcome(false, "Nothing to edit in that scope.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        if (ok)
            _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    private async Task ShowSaveDataAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var options = Menu(
            new PadOption("Trainer card", IconPath: "trainer"),
            new PadOption("Bag & items", IconPath: "bag"),
            new PadOption("Pokédex", IconPath: "pokedex"),
            Allowed(SaveAction.EditTrainer, new("Fashion", IconPath: "fashion")),
            new PadOption("Trainer records", IconPath: "records"),
            Allowed(SaveAction.WriteRawBytes, new("Byte manipulation", IconPath: "hex")),
            new PadOption("Wonder cards", IconPath: "events"),
            new PadOption("Export modified save", IconPath: "export"),
            new PadOption("Restore points", IconPath: "history")).ToList();
        if (session.GetGrandUndergroundItems().Count != 0 && Guard.Allows(SaveAction.EditInventory))
            options.Insert(2, new PadOption("Grand Underground", IconPath: "underground"));
        if (session.SupportsCompassSettings && Guard.Allows(SaveAction.EditTrainer))
            options.Insert(2, new PadOption("Compass settings", IconPath: "settings"));
        var choice = await PadMenu.ShowAsync(_hostGrid, "SAVE DATA", Note(null), options.ToArray());
        switch (choice)
        {
            case "Trainer card": await ShowTrainerCardAsync(); return;
            case "Bag & items": await ShowBagAsync(); return;
            case "Grand Underground": await GrandUndergroundEditor.ShowAsync(_hostGrid, session, _viewModel); return;
            case "Compass settings": await ShowCompassSettingsAsync(session); return;
            case "Pokédex": await ShowDexMenuAsync(); return;
            case "Fashion": await ShowFashionAsync(); return;
            case "Trainer records": await ShowTrainerRecordsAsync(); return;
            case "Byte manipulation": await HexEditorPage.ShowAsync(_hostGrid, _viewModel, session); return;
            case "Wonder cards":
            {
                var wonderChoice = await PadMenu.ShowAsync(_hostGrid, "WONDER CARDS", Note(null),
                    Menu(Allowed(SaveAction.InjectEvent, new PadOption("Event gallery", IconPath: "events")),
                        new PadOption("In-save inbox", IconPath: "inbox"),
                        Engine.KeyItemEventService.IsSupported(session) ? new PadOption("Key item events", IconPath: "key") : null,
                        Engine.SaveBlockEditorService.IsSupported(session) ? new PadOption("Save blocks", IconPath: "blocks") : null));
                if (wonderChoice == "In-save inbox")
                {
                    await MysteryGiftInboxEditor.ShowAsync(_hostGrid, session);
                    return;
                }
                if (wonderChoice == "Key item events")
                {
                    await KeyItemEventsEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
                if (wonderChoice == "Save blocks")
                {
                    await SaveBlockEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
                if (wonderChoice != "Event gallery") return;
                Services.EventArchive.EnsureLoaded(session.Generation);
                await EventGallery.ShowAsync(_hostGrid, _viewModel, session, targetSlot: null, () => _canvas.InvalidateSurface());
                return;
            }
            case "Restore points": await PushAsync<BackupHistoryPage>(); return;
        }
    }

    /// <summary>Exports the live edited save through the Android share sheet.</summary>
    private async Task ExportModifiedSaveAsync(Domain.ISaveEngineSession session)
    {
        try
        {
            var sourceName = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?
                .Current?.Document.DisplayName;
            var originalName = string.IsNullOrWhiteSpace(sourceName) ? "pkforge-save" : sourceName;
            var baseName = Path.GetFileNameWithoutExtension(originalName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "pkforge-save";
            foreach (var invalid in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(invalid, '_');
            var extension = Path.GetExtension(originalName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".sav";
            var path = Path.Combine(FileSystem.CacheDirectory, $"{baseName}-modified{extension}");
            await File.WriteAllBytesAsync(path, session.Serialize().ToArray());
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Export modified save",
                File = new ShareFile(path),
            });
            _viewModel.Status = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"Save export failed: {error.Message}";
        }
    }

    /// <summary>
    /// Pokemon Compass romhack settings: the confirmed QoL toggles (exp share, level
    /// cap, spawn rate...). Each change is one backed-up write through the normal path.
    /// </summary>
    private async Task ShowCompassSettingsAsync(Domain.ISaveEngineSession session)
    {
        if (Denied(SaveAction.EditTrainer)) return;
        while (true)
        {
            var settings = session.GetCompassSettings();
            if (settings.Count == 0)
            {
                _viewModel.Status = "No Compass settings found in this save.";
                return;
            }

            var options = settings
                .Select(setting => new PadOption($"{setting.Name}: {setting.Choices[setting.Selected]}", IconPath: "settings"))
                .Append(new PadOption("Close", IconPath: "close"))
                .ToArray();
            var choice = await PadMenu.ShowAsync(_hostGrid, "COMPASS SETTINGS",
                "Pokemon Compass options. One backed-up write per change.", options);
            if (choice is null or "Close") return;

            var setting = settings.FirstOrDefault(s => choice.StartsWith(s.Name + ":", StringComparison.Ordinal));
            if (setting is null) return;
            var picked = await PickerMenu.ShowAsync(_hostGrid, setting.Name,
                setting.Choices.Select((label, index) => new PickItem(index, label)).ToList(), setting.Selected);
            if (picked is null) continue;
            var label = setting.Choices[picked.Id];

            var ok = await _viewModel.RunMutationAsync(s =>
                s.SetCompassSetting(setting.Id, picked.Id)
                    ? new GenerationOutcome(true, $"{setting.Name} set to {label}.")
                    : new GenerationOutcome(false, "That Compass setting could not be applied."),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"Compass: {setting.Name} -> {label}",
                action: SaveAction.EditTrainer);
            if (ok)
                _viewModel.Status = $"COMPASS: {setting.Name.ToUpperInvariant()} = {label.ToUpperInvariant()}";
        }
    }

    /// <summary>Trainer card: view + edit name, IDs, money, gender - written safely like everything; view only in Hardcore.</summary>
    private async Task ShowTrainerCardAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var trainer = session.GetTrainer();

        // Hardcore: a read-only card (locked fields, no SAVE), so there is nothing to refuse.
        var readOnly = Guard.Blocks(SaveAction.EditTrainer);
        var updated = await TrainerCardPopup.ShowAsync(_hostGrid, trainer, readOnly);
        if (readOnly || updated is null || updated == trainer) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.SetTrainer(updated);
            return new GenerationOutcome(true, "Trainer card updated.");
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
    }

    private async Task ShowFashionAsync()
    {
        if (Denied(SaveAction.EditTrainer)) return;
        var session = _sessionsFor();
        if (session is null) return;
        if (!session.SupportsLegalFashionUnlock)
        {
            await EditorMenu.ShowAsync(_hostGrid, "FASHION",
                "Legal wardrobe unlocks are currently available for Pokémon Sword and Shield only.", "OK");
            return;
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "UNLOCK LEGAL FASHION?",
            "Unlock every outfit this Sword or Shield save can legitimately own. A restore point is created first.", "Unlock");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(s =>
        {
            s.UnlockAllLegalFashion();
            return new GenerationOutcome(true, "All legal fashion items unlocked.");
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
    }

    private async Task ShowTrainerRecordsAsync()
    {
        var session = _sessionsFor();
        if (session is not null) await TrainerRecordsEditor.ShowAsync(_hostGrid, session);
    }

    private async Task ShowTrainerProfilesAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var store = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();

        while (true)
        {
            var profiles = store.Profiles;
            var options = Menu(
                new("Save current trainer as profile", IconPath: "profile"),
                _viewModel.SelectedSlot >= 0 && profiles.Count > 0
                    ? Allowed(SaveAction.EditMon, new("Apply profile to selected Pokémon", IconPath: "profile"))
                    : null,
                profiles.Count > 0 ? new("Delete a profile", IconPath: "delete") : null,
                new(store.UseCurrentTrainerForGeneration
                    ? "Generated Pokémon obey trainer: ON"
                    : "Generated Pokémon obey trainer: OFF", IconPath: "profile")).ToList();

            var choice = await PadMenu.ShowAsync(_hostGrid, "TRAINER PROFILES",
                Note(profiles.Count == 0 ? "No named profiles yet." : string.Join('\n', profiles.Select(ProfileSummary))),
                options.ToArray());
            if (choice is null) return;

            if (choice == "Save current trainer as profile")
            {
                var name = await TextPopup.ShowLineAsync(_hostGrid, "PROFILE NAME", "Profile name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                store.Save(name, session.GetTrainer());
                _viewModel.Status = $"Trainer profile '{name.Trim()}' saved.";
                continue;
            }
            if (choice.StartsWith("Generated Pokémon obey trainer:", StringComparison.Ordinal))
            {
                store.SetUseCurrentTrainerForGeneration(!store.UseCurrentTrainerForGeneration);
                continue;
            }

            var labels = profiles.Select(ProfileSummary).ToArray();
            var selected = await PadMenu.ShowAsync(_hostGrid,
                choice == "Delete a profile" ? "DELETE PROFILE" : "APPLY TRAINER PROFILE", null, labels);
            var index = Array.IndexOf(labels, selected);
            if (index < 0) continue;
            var profile = profiles[index];
            if (choice == "Delete a profile")
            {
                var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "DELETE PROFILE?", profile.DisplayName, "Delete");
                if (confirmed) store.Delete(profile.Id);
                continue;
            }

            var slot = _viewModel.SelectedSlot;
            await _viewModel.RunMutationAsync(s => s.MakeMine(_viewModel.BoxIndex, slot, profile), slot, action: SaveAction.EditMon);
            _canvas.InvalidateSurface();
            return;
        }
    }

    private static string ProfileSummary(TrainerProfile profile) =>
        $"{profile.DisplayName} · {profile.OriginalTrainer} · {profile.TID}/{profile.SID} · {(profile.Gender == 1 ? "F" : "M")}";

    /// <summary>Bag: the navy inventory editor - pocket pills, item rows with count discs.</summary>
    private async Task ShowBagAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        if (session.GetPokeBeans().Count != 0)
        {
            var choice = await PadMenu.ShowAsync(_hostGrid, "BAG & ITEMS", null,
                new PadOption("Bag", IconPath: "bag"),
                new PadOption("Poké Beans", IconPath: "item"));
            if (choice is null) return;
            if (choice == "Poké Beans")
            {
                await PokeBeansEditor.ShowAsync(_hostGrid, session, _viewModel);
                return;
            }
        }
        await BagEditor.ShowAsync(_hostGrid, session, _viewModel, data);
    }

    /// <summary>
    /// The bag editor overlay: the navy inventory world. Pockets are bag pills (cyan;
    /// yellow-green rim and gold fill when active), items are white PixelUI rows with
    /// round count discs. Tap a name for the exact-count sheet; left/right adjust the
    /// selected count, shoulder L/R turn pockets, up/down walk rows, A activates.
    /// </summary>
    private sealed class BagEditor : IPadHandler
    {
        private readonly Grid _host;
        private readonly ISaveEngineSession _session;
        private readonly BoxBrowserViewModel _viewModel;
        private readonly IGameDataService _data;
        private readonly ScrollView _scroll;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HorizontalStackLayout _pockets = new() { Spacing = 12, HorizontalOptions = LayoutOptions.Center };
        private readonly VerticalStackLayout _rows = new() { Spacing = 4 };
        private readonly int _slotSeed;

        private IReadOnlyList<BagPouch> _bag = [];
        private List<BagRow> _itemRows = [];
        private View _addRow = null!;
        private View _presetRow = null!;
        private Grid _overlay = null!;
        private int _pouchIndex;
        private int _cursor;
        private readonly Dictionary<(string Pouch, int Item), int> _pendingCounts = [];
        private readonly SemaphoreSlim _pendingWriteGate = new(1, 1);
        private int _pendingRevision;
        private bool _closing;

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
            // Resolve this once from the open save. Leaving it empty made every row
            // render as #id and filtered every legal id out of ADD ITEM.
            _itemNames = session.GetItemNames();
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
                        TextColor = UiTokens.Ink0,
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            };

            _scroll = new ScrollView { Content = _rows };

            var hint = new Label
            {
                Text = "LEFT / RIGHT ADJUST · L / R CHANGE POUCH · A OPENS EXACT COUNT",
                FontFamily = DsChrome.PixelFont,
                FontSize = 11,
                TextColor = UiTokens.BagCyan,
            };
            // Live status INSIDE the bag window: outcomes must be visible here, not on
            // the page footer hidden behind this overlay. Every async action reports.
            BagStatus = new Label
            {
                Text = "",
                FontFamily = DsChrome.PixelFont,
                FontSize = 11,
                TextColor = UiTokens.Ink0,
                LineBreakMode = LineBreakMode.WordWrap,
                MaxLines = 2,
            };

            var body = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
                Children = { title, _pockets, _scroll, hint, BagStatus },
            };
            Grid.SetRow(_pockets, 1);
            Grid.SetRow(_scroll, 2);
            Grid.SetRow(hint, 3);
            Grid.SetRow(BagStatus, 4);

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
            _overlay = Kit.AttachOverlay(host, window, () => _ = CloseAsync());
            Kit.AnimateIn(window);
        }

        private async Task RunAsync()
        {
            _bag = _session.GetBag();
            if (_bag.Count == 0)
            {
                _viewModel.Status = "This game exposes no editable bag.";
                await CloseAsync();
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

        internal Label BagStatus = null!;

        private void Report(string message)
        {
            MainThread.BeginInvokeOnMainThread(() => BagStatus.Text = message);
        }
        /// <summary>The Hardcore refusal inside the bag: the reason lands in the
        /// window's own status line, never swallowed. Doubles as the no-op handler for
        /// the disabled row buttons.</summary>
        private bool Refuse()
        {
            if (!HardcoreMode.Blocks(SaveAction.EditInventory, out var status)) return false;
            Report(status);
            return true;
        }

        // The OPEN GAME's item table: Gen 1 Rare Candy lives at a different index than
        // in the modern list, which is what misnamed everything before.
        private readonly IReadOnlyList<string> _itemNames;
        private string ItemName(int id) =>
            id < _itemNames.Count && _itemNames[id].Length > 0 ? _itemNames[id] : $"#{id}";

        private async Task CyclePouchAsync(int delta)
        {
            if (_bag.Count == 0) return;
            await FlushPendingAsync();
            _pouchIndex = ((_pouchIndex + delta) % _bag.Count + _bag.Count) % _bag.Count;
            _cursor = 0;
            Rebuild();
        }

        private async Task PickPouchAsync()
        {
            var pouches = _bag.Select((p, i) => new PadOption($"{p.Name.ToUpperInvariant()} ({p.Items.Count})", IconPath: "bag")).ToArray();
            var choice = await PadMenu.ShowAsync(_host, "POUCH", null, pouches);
            if (choice is null) return;
            var index = Array.FindIndex(pouches, o => o.Label == choice);
            if (index >= 0) { _pouchIndex = index; _cursor = 0; Rebuild(); }
        }

        /// <summary>Re-reads the bag and rebuilds the switcher + rows (after every write or pouch switch).</summary>
        private void Rebuild()
        {
            _bag = _session.GetBag();
            if (_pouchIndex >= _bag.Count) _pouchIndex = 0;
            var pouch = _bag[_pouchIndex];

            // One compact switcher row instead of wrapping pills: Gen 4's eight pouches
            // were eating half the window and hiding the bag. Arrows cycle; tapping the
            // name opens the full pouch list; L/R on the pad do the same.
            _pockets.Children.Clear();
            var prev = Kit.MiniCapsule("<", UiTokens.BagCyan);
            prev.Clicked += (_, _) => _ = CyclePouchAsync(-1);
            var next = Kit.MiniCapsule(">", UiTokens.BagCyan);
            next.Clicked += (_, _) => _ = CyclePouchAsync(+1);
            var label = new Label
            {
                Text = $" {_bag[_pouchIndex].Name.ToUpperInvariant()} ({pouch.Items.Count}) ",
                FontFamily = DsChrome.PixelFont,
                FontSize = 17,
                FontAttributes = FontAttributes.Bold,
                TextColor = UiTokens.Ink0,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            var tapList = new TapGestureRecognizer();
            tapList.Tapped += (_, _) => _ = PickPouchAsync();
            label.GestureRecognizers.Add(tapList);
            _pockets.Children.Add(prev);
            _pockets.Children.Add(label);
            _pockets.Children.Add(next);

            _rows.Children.Clear();
            _itemRows = [];
            // Hardcore mode: the bag stays readable and the pouch switcher still works,
            // but every editing affordance is disabled and says why.
            var canEdit = Guard.CanEditInventory;
            var icons = new List<Image>(pouch.Items.Count);
            foreach (var item in pouch.Items)
            {
                var captured = item;
                var shownCount = _pendingCounts.GetValueOrDefault((pouch.Name, item.Id), item.Count);
                var icon = new Image { WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
                icons.Add(icon);
                var row = new BagRow(icon, ItemName(item.Id), shownCount)
                {
                    Tapped = canEdit ? () => _ = EditCountAsync(captured) : () => Refuse(),
                    Minus = canEdit ? () => QueueNudge(captured, -1) : () => Refuse(),
                    Plus = canEdit ? () => QueueNudge(captured, +1) : () => Refuse(),
                };
                _itemRows.Add(row);
                _rows.Children.Add(row);
            }

            var add = Kit.Capsule("+  ADD ITEM", UiTokens.BagCyan);
            add.FontSize = 14;
            add.Margin = new Thickness(4, 6, 4, 2);
            add.IsEnabled = canEdit;
            add.Clicked += (_, _) => _ = AddItemAsync();
            _addRow = add;
            _rows.Children.Add(_addRow);

            var presets = Kit.Capsule("ITEM PRESETS", UiTokens.BagCyan);
            presets.FontSize = 14;
            presets.Margin = new Thickness(4, 2, 4, 2);
            presets.IsEnabled = canEdit;
            presets.Clicked += (_, _) => _ = ShowPresetsAsync();
            _presetRow = presets;
            _rows.Children.Add(_presetRow);

            Highlight(_cursor);
            _ = LoadIconsAsync(pouch.Items, icons);
            Report(canEdit
                ? $"{pouch.Name.ToUpperInvariant()} - {pouch.Items.Count} KINDS - NAME TABLE {_itemNames.Count}"
                : HardcoreMode.StatusLine);
        }

        private async Task LoadIconsAsync(IReadOnlyList<BagItem> items, IReadOnlyList<Image> targets)
        {
            var placeholder = ItemArt.PlaceholderPath();
            var paths = await Task.WhenAll(items.Select(i => ItemArt.GetAsync(ItemName(i.Id))));
            for (var i = 0; i < targets.Count && i < paths.Length; i++)
                targets[i].Source = ImageSource.FromFile(paths[i] ?? placeholder);
        }

        private async Task EditCountAsync(BagItem item)
        {
            if (Refuse()) return;
            await FlushPendingAsync();
            var name = ItemName(item.Id);
            var current = _session.GetBag().SelectMany(p => p.Items).FirstOrDefault(i => i.Id == item.Id)?.Count ?? item.Count;
            var count = await StatsPopup.ShowSingleAsync(_host, $"{name.ToUpperInvariant()} - QUANTITY", current, 999);
            if (count is null) return;
            await WriteAsync(item.Id, count.Value);
        }

        /// <summary>Quantity changes feel immediate, but a quick run of pad presses becomes
        /// one validation, restore point, and SAF write after the user pauses.</summary>
        private void QueueNudge(BagItem item, int delta)
        {
            if (_cursor >= _itemRows.Count) return;
            var pouchName = _bag[_pouchIndex].Name;
            var key = (pouchName, item.Id);
            var current = _pendingCounts.GetValueOrDefault(key, item.Count);
            var count = Math.Clamp(current + delta, 0, 999);
            if (count == current) return;
            _pendingCounts[key] = count;
            _itemRows[_cursor].SetCount(count);
            Report($"{ItemName(item.Id).ToUpperInvariant()} x{count} - SAVING...");
            var revision = ++_pendingRevision;
            _ = SaveAfterPauseAsync(revision);
        }

        private async Task SaveAfterPauseAsync(int revision)
        {
            await Task.Delay(550);
            if (revision == _pendingRevision)
                await FlushPendingAsync();
        }

        private async Task<bool> FlushPendingAsync()
        {
            await _pendingWriteGate.WaitAsync();
            try
            {
                if (_pendingCounts.Count == 0) return true;
                var changes = _pendingCounts.Select(x => (x.Key.Pouch, x.Key.Item, Count: x.Value)).ToArray();
                _pendingCounts.Clear();
                var outcome = await _viewModel.RunMutationAsync(s =>
                {
                    foreach (var change in changes)
                        s.SetItemCount(change.Pouch, change.Item, change.Count);
                    return new GenerationOutcome(true, $"Updated {changes.Length} item{(changes.Length == 1 ? "" : "s")}.");
                }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
                Report(outcome ? "ITEM QUANTITY SAVED TO FILE - RESTART THE GAME TO LOAD IT" : "WRITE FAILED - SEE STATUS");
                Rebuild();
                return outcome;
            }
            finally
            {
                _pendingWriteGate.Release();
            }
        }

        /// <summary>One safe write (backup + atomic), then a fresh read of the whole bag.</summary>
        private async Task WriteAsync(int itemId, int count)
        {
            await FlushPendingAsync();
            var pouchName = _bag[_pouchIndex].Name;
            var name = ItemName(itemId);
            var stored = count;
            var outcome = await _viewModel.RunMutationAsync(s =>
            {
                stored = s.SetItemCount(pouchName, itemId, count);
                return new GenerationOutcome(true, stored == 0 ? $"{name} removed." : $"{name} ×{stored}");
            }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
            Report(outcome ? $"SAVED TO FILE: {(stored == 0 ? $"{name} REMOVED" : $"{name} x{stored}")} - RESTART GAME" : "WRITE FAILED - SEE STATUS");
            Rebuild();
        }

        private async Task AddItemAsync()
        {
            await FlushPendingAsync();
            var pouchName = _bag[_pouchIndex].Name;
            var gameItems = _itemNames; // the open game's own table
            var legalIds = _session.GetPouchLegalItems(pouchName)
                .Where(id => id < gameItems.Count && gameItems[id].Length > 0)
                .ToList();
            if (legalIds.Count == 0)
            {
                Report($"NO ADDABLE ITEMS IN {pouchName.ToUpperInvariant()} - TAP THE POUCH NAME TO SWITCH");
                return;
            }

            // Open the picker IMMEDIATELY with cached-or-placeholder art. The old flow
            // blocked on fetching every sprite first, which on a cold cache read as
            // "the button does nothing" for the better part of a minute.
            var itemDirectory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
            var placeholder = ItemArt.PlaceholderPath();
            var legal = legalIds.Select(id =>
            {
                var cached = System.IO.Path.Combine(itemDirectory, ItemArt.Slug(gameItems[id]) + ".png");
                return new PickItem(id, gameItems[id], File.Exists(cached) ? cached : placeholder);
            }).ToList();
            Report($"ADDING TO {pouchName.ToUpperInvariant()} - {legal.Count} ITEMS");
            _ = Task.Run(async () =>
            {
                foreach (var id in legalIds)
                    await ItemArt.GetAsync(gameItems[id]);
            });
            PickItem? picked;
            try
            {
                picked = await PickerMenu.ShowAsync(_host, $"ADD - {pouchName.ToUpperInvariant()}", legal);
            }
            catch (Exception ex)
            {
                Report($"PICKER FAILED: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (picked is null) { Report("CANCELLED"); return; }
            var count = await StatsPopup.ShowSingleAsync(_host, $"{ItemName(picked.Id).ToUpperInvariant()} - QUANTITY", 0, 999);
            if (count is null) return;
            await WriteAsync(picked.Id, count.Value);
        }

        private static readonly HashSet<string> BallNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Poké Ball", "Great Ball", "Ultra Ball", "Master Ball", "Safari Ball", "Sport Ball",
            "Fast Ball", "Level Ball", "Lure Ball", "Heavy Ball", "Love Ball", "Friend Ball", "Moon Ball",
            "Net Ball", "Dive Ball", "Nest Ball", "Repeat Ball", "Timer Ball", "Luxury Ball", "Premier Ball",
            "Dusk Ball", "Heal Ball", "Quick Ball", "Cherish Ball", "Park Ball", "Dream Ball", "Beast Ball",
            "Strange Ball",
        };

        private static readonly HashSet<string> HealingNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Potion", "Super Potion", "Hyper Potion", "Max Potion", "Full Restore", "Fresh Water",
            "Soda Pop", "Lemonade", "Moomoo Milk", "Energy Powder", "Energy Root", "Revive", "Max Revive",
            "Antidote", "Burn Heal", "Ice Heal", "Awakening", "Paralyze Heal", "Full Heal",
        };

        private async Task ShowPresetsAsync()
        {
            if (!await FlushPendingAsync()) return;
            var choice = await PadMenu.ShowAsync(_host, "ITEM PRESETS", "Only items legal in this game are changed.",
                new PadOption("Save current bag as preset", IconPath: "preset"),
                new PadOption("My item presets", IconPath: "preset"),
                new PadOption("Refill this pouch to 99", IconPath: "fill"),
                new PadOption("Give every Poké Ball x50", IconPath: "ball"),
                new PadOption("Healing supplies x20", IconPath: "heal"),
                new PadOption("Nuzlocke starter supplies", IconPath: "skull"),
                new PadOption("Remove every item in this pouch", IconPath: "clear"));
            if (choice is null) return;

            if (choice is "Save current bag as preset" or "My item presets")
            {
                try
                {
                    var store = new PKForge.Infrastructure.ItemPresetStore(Path.Combine(FileSystem.AppDataDirectory, "item-presets.json"));
                    if (choice == "Save current bag as preset") await SavePersonalPresetAsync(store);
                    else await ShowPersonalPresetsAsync(store);
                }
                catch (Exception ex)
                {
                    Report($"PRESET ERROR: {ex.Message}");
                    await PadMenu.ShowAsync(_host, "ITEM PRESET ERROR", ex.Message, "OK");
                }
                return;
            }

            if (choice == "Remove every item in this pouch")
            {
                var confirmed = await PadMenu.ConfirmAsync(_host, "EMPTY THIS POUCH?",
                    $"Every item in {_bag[_pouchIndex].Name} is removed. A restore point is created first.", "Empty");
                if (!confirmed) return;
            }

            var changes = BuildPreset(choice);
            if (changes.Count == 0)
            {
                Report("NO COMPATIBLE ITEMS FOR THIS PRESET");
                return;
            }

            var ok = await _viewModel.RunMutationAsync(s =>
            {
                foreach (var change in changes)
                    s.SetItemCount(change.Pouch, change.Item, change.Count);
                return new GenerationOutcome(true, $"Preset changed {changes.Count} items.");
            }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
            Report(ok ? $"PRESET APPLIED - {changes.Count} ITEMS" : "PRESET FAILED - SEE STATUS");
            Rebuild();
        }

        private async Task SavePersonalPresetAsync(PKForge.Infrastructure.ItemPresetStore store, ItemPreset? existing = null)
        {
            var names = _session.GetItemNames();
            var entries = _session.GetBag().SelectMany(pouch => pouch.Items
                .Where(item => item.Count > 0 && item.Id > 0 && item.Id < names.Count
                    && !string.IsNullOrWhiteSpace(names[item.Id])
                    && _session.GetPouchLegalItems(pouch.Name).Contains(item.Id))
                .Select(item => new ItemPresetEntry(pouch.Name, item.Id, names[item.Id], item.Count))).ToArray();
            if (entries.Length == 0)
            {
                Report("NO COMPATIBLE ITEMS TO SAVE IN A PRESET");
                return;
            }
            var name = existing?.Name ?? await TextPopup.ShowLineAsync(_host, "PRESET NAME", "Name your bag preset (60 characters max)");
            if (string.IsNullOrWhiteSpace(name)) return;
            store.Save(new ItemPreset(existing?.Id ?? Guid.NewGuid().ToString("N"), name, _session.Generation, entries));
            Report($"PRESET SAVED - {entries.Length} ITEMS");
        }

        private async Task ShowPersonalPresetsAsync(PKForge.Infrastructure.ItemPresetStore store)
        {
            while (true)
            {
                var presets = store.Read();
                if (presets.Count == 0)
                {
                    await PadMenu.ShowAsync(_host, "MY ITEM PRESETS", "Adjust your bag, then choose Save current bag as preset.", "OK");
                    return;
                }
                var labels = presets.Select(p => $"{p.Name} (Gen {p.Generation})").ToArray();
                var selected = await PadMenu.ShowAsync(_host, "MY ITEM PRESETS", "Presets set saved quantities. Other items stay in your bag.", labels);
                var index = Array.IndexOf(labels, selected);
                if (index < 0) return;
                var preset = presets[index];
                var action = await PadMenu.ShowAsync(_host, preset.Name.ToUpperInvariant(),
                    $"{preset.Items.Count} items. Compatible items apply within Gen {preset.Generation}.",
                    new PadOption("Apply preset", IconPath: "confirm"),
                    new PadOption("Update from current bag", IconPath: "refresh"),
                    new PadOption("Rename", IconPath: "rename"),
                    new PadOption("Delete", IconPath: "delete"));
                if (action == "Rename")
                {
                    var name = await TextPopup.ShowLineAsync(_host, "RENAME PRESET", "Preset name", preset.Name);
                    if (!string.IsNullOrWhiteSpace(name)) store.Save(preset with { Name = name });
                }
                else if (action == "Delete")
                {
                    if (await PadMenu.ConfirmAsync(_host, "DELETE PRESET?", preset.Name, "Delete")) store.Delete(preset.Id);
                }
                else if (action == "Update from current bag")
                {
                    if (await PadMenu.ConfirmAsync(_host, "UPDATE PRESET?", "Replace this preset with the current bag and its generation?", "Update"))
                        await SavePersonalPresetAsync(store, preset);
                }
                else if (action == "Apply preset")
                {
                    var pouches = _session.GetBag().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                    var changes = preset.CompatibleItems(_session.Generation, _session.GetItemNames(),
                        pouch => pouches.Contains(pouch) ? _session.GetPouchLegalItems(pouch) : []);
                    if (changes.Count == 0)
                    {
                        await PadMenu.ShowAsync(_host, "NO COMPATIBLE ITEMS", $"This preset was saved for Gen {preset.Generation}. Only matching legal items can be applied.", "OK");
                        continue;
                    }
                    if (!await PadMenu.ConfirmAsync(_host, "APPLY ITEM PRESET?",
                        $"Set quantities for {changes.Count} items; skip {preset.Items.Count - changes.Count} incompatible items. Game quantity limits apply. Other items are kept.", "Apply")) continue;
                    var ok = await _viewModel.RunMutationAsync(s =>
                    {
                        var original = s.GetBag().SelectMany(pouch => pouch.Items.Select(item =>
                            (Key: (pouch.Name, item.Id), item.Count)))
                            .ToDictionary(item => item.Key, item => item.Count);
                        var applied = new List<ItemPresetEntry>();
                        try
                        {
                            foreach (var item in changes)
                            {
                                s.SetItemCount(item.Pouch, item.ItemId, item.Count);
                                applied.Add(item);
                            }
                        }
                        catch
                        {
                            // A full pouch must not leave half of a preset in the live session.
                            foreach (var item in applied.AsEnumerable().Reverse())
                                s.SetItemCount(item.Pouch, item.ItemId, original.GetValueOrDefault((item.Pouch, item.ItemId)));
                            throw;
                        }
                        return new GenerationOutcome(true, $"Preset changed {changes.Count} items.");
                    }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
                    Report(ok ? $"PRESET APPLIED - {changes.Count} ITEMS" : "PRESET FAILED - SEE STATUS");
                    Rebuild();
                    return;
                }
            }
        }

        private List<(string Pouch, int Item, int Count)> BuildPreset(string choice)
        {
            if (choice == "Refill this pouch to 99")
                return _bag[_pouchIndex].Items.Select(i => (_bag[_pouchIndex].Name, i.Id, 99)).ToList();
            if (choice == "Remove every item in this pouch")
                return _bag[_pouchIndex].Items.Select(i => (_bag[_pouchIndex].Name, i.Id, 0)).ToList();

            var desired = choice switch
            {
                "Give every Poké Ball x50" => BallNames.ToDictionary(name => name, _ => 50, StringComparer.OrdinalIgnoreCase),
                "Healing supplies x20" => HealingNames.ToDictionary(name => name, _ => 20, StringComparer.OrdinalIgnoreCase),
                "Nuzlocke starter supplies" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Poké Ball"] = 10,
                    ["Potion"] = 10,
                    ["Antidote"] = 3,
                    ["Paralyze Heal"] = 3,
                    ["Escape Rope"] = 2,
                },
                _ => [],
            };

            var result = new List<(string Pouch, int Item, int Count)>();
            foreach (var pouch in _bag)
            {
                foreach (var id in _session.GetPouchLegalItems(pouch.Name))
                {
                    var name = ItemName(id);
                    if (desired.TryGetValue(name, out var count))
                        result.Add((pouch.Name, id, count));
                }
            }
            return result;
        }

        private void Highlight(int index)
        {
            _cursor = Math.Clamp(index, 0, _itemRows.Count + 1);
            for (var i = 0; i < _itemRows.Count; i++)
                _itemRows[i].Selected = i == _cursor;
            if (_addRow is Button capsule)
                capsule.BackgroundColor = _cursor == _itemRows.Count ? UiTokens.ChoiceFillPress : UiTokens.ChoiceFill;
            if (_presetRow is Button preset)
                preset.BackgroundColor = _cursor == _itemRows.Count + 1 ? UiTokens.ChoiceFillPress : UiTokens.ChoiceFill;
            var target = _cursor < _itemRows.Count ? (View)_itemRows[_cursor]
                : _cursor == _itemRows.Count ? _addRow : _presetRow;
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
                case PadButton.Left:
                    if (_cursor < _itemRows.Count)
                        QueueNudge(_bag[_pouchIndex].Items[_cursor], -1);
                    return true;
                case PadButton.Right:
                    if (_cursor < _itemRows.Count)
                        QueueNudge(_bag[_pouchIndex].Items[_cursor], +1);
                    return true;
                case PadButton.L:
                    _ = CyclePouchAsync(-1);
                    return true;
                case PadButton.R:
                    _ = CyclePouchAsync(+1);
                    return true;
                case PadButton.A:
                    if (_cursor < _itemRows.Count)
                        _ = EditCountAsync(_bag[_pouchIndex].Items[_cursor]);
                    else if (_cursor == _itemRows.Count)
                        _ = AddItemAsync();
                    else
                        _ = ShowPresetsAsync();
                    return true;
                case PadButton.B:
                    _ = CloseAsync();
                    return true;
                default:
                    return true; // modal while open
            }
        }

        private async Task CloseAsync()
        {
            if (_closing) return;
            _closing = true;
            await FlushPendingAsync();
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
                    TextColor = UiTokens.Ink0,
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
            private readonly Label _counter;
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
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    InputTransparent = true,
                };
                _counter = new Label
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
                Children.Add(_counter);
                Children.Add(CountDisc(minus: true, () => Minus?.Invoke()));
                Children.Add(CountDisc(minus: false, () => Plus?.Invoke()));
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(_counter, 2);
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

            public void SetCount(int count) => _counter.Text = $"×{count}";
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
        // The dex editor's whole job is setting seen/caught flags; the read-only
        // collection dex ("Collection dex…" in STORAGE TOOLS) stays for viewing.
        if (Denied(SaveAction.EditDex)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        await DexEditorPage.ShowAsync(_hostGrid, _viewModel, session, legalizer, data, _sprites);
        _viewModel.Status = "Pokédex editor closed.";
    }

    /// <summary>Every button on this screen, in one place.</summary>
    public bool OnPadButton(PadButton button)
    {
        if (_boxManageMode)
        {
            switch (button)
            {
                case PadButton.L:
                    if (_boxHeld) _ = ShiftHeldBoxAsync(-1); else BrowseManagedBoxes(-1);
                    return true;
                case PadButton.R:
                    if (_boxHeld) _ = ShiftHeldBoxAsync(1); else BrowseManagedBoxes(1);
                    return true;
                case PadButton.Y: ToggleMarkedBox(); return true;
                case PadButton.X: _ = ShowBoxBulkActionsAsync(); return true;
                case PadButton.A:
                    _boxHeld = !_boxHeld;
                    _heldBox = _viewModel.BoxIndex;
                    _viewModel.Status = _boxHeld
                        ? $"HOLDING BOX {_heldBox + 1:00} - L/R SWAP · A DROP"
                        : "BOX MANAGER - A HOLD · L/R BROWSE · Y SELECT · X ACTIONS";
                    SetBoxManageFooter();
                    UpdateBoxManagePulse();
                    _boxBar.InvalidateSurface();
                    return true;
                case PadButton.B: ExitBoxManageMode(); return true;
                default: return true;
            }
        }

        if (_editorFocusMode)
        {
            switch (button)
            {
                case PadButton.Up: MoveEditorFocus(-1); return true;
                case PadButton.Down: MoveEditorFocus(1); return true;
                case PadButton.Left: MoveEditorFocusHorizontal(-1); return true;
                case PadButton.Right: MoveEditorFocusHorizontal(1); return true;
                case PadButton.A:
                case PadButton.X:
                case PadButton.Y:
                    ActivateEditorFocus();
                    return true;
                case PadButton.B:
                case PadButton.Start:
                    ExitEditorFocusMode();
                    return true;
                default:
                    return true;
            }
        }

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
            if (_viewModel.BoxIndex == -1 && _viewModel.SelectedSlot != _viewModel.CarrySource.Value.Slot)
            {
                // Party swap: the first A aims (preview), the second A on the same slot confirms.
                if (_lastAimSlot == _viewModel.SelectedSlot) { _lastAimSlot = -1; _ = DropAndRepaintAsync(); }
                else { _lastAimSlot = _viewModel.SelectedSlot; _canvas.InvalidateSurface(); }
            }
            else
            {
                _lastAimSlot = -1;
                _ = DropAndRepaintAsync();
            }
            return true;
        }
        _lastAimSlot = -1;

        // A is the hand everywhere, party included: grab, carry, place or swap
        // (the party drop is the two-step aim). The mon's actions stay on Start.

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

    /// <summary>The slot's verdict: the sweep's cached answer when fresh, else a one-slot
    /// analysis off-thread (the same report the side panel shows).</summary>
    private async Task<SlotLegality?> CurrentLegalityAsync(int slot)
    {
        var cached = _viewModel.LegalitySweep?.FirstOrDefault(v => v.Box == _viewModel.BoxIndex && v.Slot == slot);
        if (cached is not null) return cached;
        var session = _sessionsFor();
        var legality = IPlatformApplication.Current?.Services.GetService<ILegalityService>();
        if (session is null || legality is null || !session.SupportsLegalityAnalysis) return null;
        return await Task.Run(() =>
        {
            var report = legality.Analyze(session, _viewModel.BoxIndex, slot);
            return new SlotLegality(_viewModel.BoxIndex, slot, report.Valid,
                report.Valid ? string.Empty : report.Lines.FirstOrDefault() ?? "Illegal.", report.Lines);
        });
    }


    /// <summary>What you can do with the mon under the cursor. Editing is live in the side panel already.</summary>
    private async Task ShowMonActionsAsync(int slot)
    {
        var nickname = _viewModel.Selected?.Nickname is { Length: > 0 } nick ? nick : $"slot {slot + 1}";
        // An illegal mon leads with its repairs; the sweep's cached verdict answers
        // instantly, otherwise this one slot is analyzed before the menu opens.
        var verdict = await CurrentLegalityAsync(slot);
        var options = new List<PadOption>();
        if (verdict is { Valid: false })
        {
            options.Add(new("Explain legality", IconPath: "info"));
            options.AddRange(Menu(
                Allowed(SaveAction.EditMon, new("Legalize this one", IconPath: "fix")),
                Allowed(SaveAction.EditMon, new("Legalize all illegal", IconPath: "fix"))));
        }
        options.AddRange(Menu(
            new PadOption("Edit", IconPath: "editor"),
            Allowed(SaveAction.EditMon, new("Evolve…", IconPath: "evolve")),
            new PadOption("Send to Poképark", IconPath: "park"),
            new PadOption("Move", IconPath: "move"),
            Allowed(SaveAction.Duplicate, new("Duplicate", IconPath: "copy")),
            new PadOption("Send to Bank", IconPath: "bank"),
            Allowed(SaveAction.Duplicate, new("Copy to Bank", IconPath: "copy")),
            new PadOption("Send to another game…", IconPath: "send"),
            Allowed(SaveAction.Duplicate, new("Copy to another game…", IconPath: "copy")),
            new PadOption("Export .pk file", IconPath: "export"),
            new PadOption("Show as Showdown set", IconPath: "script"),
            new PadOption("Show as QR code", IconPath: "qr"),
            new PadOption("Show as .pk QR", IconPath: "qr"),
            new PadOption("RNG / IVs", IconPath: "dice"),
            new PadOption("Lock / Unlock release", IconPath: "padlock"),
            new PadOption("Release", IconPath: "release")));
        var choice = await PadMenu.ShowAsync(_hostGrid, nickname.ToUpperInvariant(),
            Note(verdict is { Valid: false } ? verdict.Problem : null), options.ToArray());
        switch (choice)
        {
            case "Explain legality":
            {
                var detail = string.IsNullOrWhiteSpace(_viewModel.LegalityText)
                    ? verdict?.Problem ?? "No legality details were reported."
                    : _viewModel.LegalityText;
                await ShowLegalityReportAsync(detail);
                return;
            }
            case "Legalize this one":
            {
                var overlay = LoadingOverlay.Show(_hostGrid, "LEGALIZING…", "Finding the closest real, legal version of this Pokémon.");
                try
                {
                    await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot, SaveAction.EditMon);
                    _canvas.InvalidateSurface();
                }
                finally { overlay.Close(); }
                return;
            }
            case "Send to Poképark":
                _viewModel.Status = IPlatformApplication.Current!.Services.GetRequiredService<PokeparkService>().AddSaveVisitor(_viewModel.BoxIndex, slot);
                return;
            case "Edit":
                EnterEditorFocusMode();
                return;
            case "Move":
                if (_viewModel.BeginCarry()) _canvas.InvalidateSurface();
                return;
            case "Duplicate":
                await DuplicateSlotAsync(slot);
                return;
            case "Evolve…":
                await EvolveSlotAsync(slot);
                return;
            case "Send to Bank":
                await SendToBankAsync(slot, nickname);
                return;
            case "Copy to Bank":
                await SendToBankAsync(slot, nickname, copyInsteadOfMove: true);
                return;
            case "Send to another game…":
                await SendSlotToAnotherGameAsync(slot, nickname);
                return;
            case "Copy to another game…":
                await SendSlotToAnotherGameAsync(slot, nickname, copyInsteadOfMove: true);
                return;
            case "Legalize all illegal":
            {
                var overlay = default(LoadingOverlay);
                IReadOnlyList<SlotLegality> sweep;
                try
                {
                    sweep = await _viewModel.GetLegalitySweepAsync(
                        () => overlay = LoadingOverlay.Show(_hostGrid, "CHECKING LEGALITY…", "Scanning the party and every box."),
                        (done, total) => overlay?.Report(done, total));
                }
                finally
                {
                    overlay?.Close();
                }
                var illegal = sweep.Where(r => !r.Valid).ToList();
                if (illegal.Count == 0) { _viewModel.Status = "Nothing else is flagged."; return; }
                await LegalizeAllIllegalAsync(illegal);
                return;
            }
            case "Show as .pk QR":
            {
                var session = _sessionsFor();
                if (session is null) return;
                var export = session.ExportSlot(_viewModel.BoxIndex, slot);
                var detail = session.ReadEntity(_viewModel.BoxIndex, slot);
                await QrPopup.ShowBinaryAsync(_hostGrid, $"{detail.SpeciesName.ToUpperInvariant()} · .PK QR",
                    Services.QrEntityService.MakePayload(export.Data, session.Generation, detail.SpeciesName));
                return;
            }
            case "Export .pk file":
                await ExportSlotAsync(slot);
                return;
            case "Show as QR code":
                await ShowQrAsync(slot);
                return;
            case "RNG / IVs":
                await ShowRngAsync(slot);
                return;
            case "Lock / Unlock release":
            {
                var docId = DocumentId;
                var session = _sessionsFor();
                if (docId is null || session is null) return;
                var pid = session.GetRngInfo(_viewModel.BoxIndex, slot).Pid;
                var locked = Protection.ToggleMon(docId, _viewModel.BoxIndex, slot, pid);
                RefreshLockedSlots();
                _canvas.InvalidateSurface();
                _viewModel.Status = locked ? "MON LOCKED - RELEASE BLOCKED" : "MON UNLOCKED";
                return;
            }
            case "Release":
                await ReleaseSlotAsync(slot, nickname);
                return;
            default:
                return; // Edit: the editor is already open on the right
        }
    }

    /// <summary>Manual evolution (trade evolutions without a second console): the evolution
    /// card plans it, the edit commits through RunMutationAsync (backup, Hardcore guard).</summary>
    private async Task EvolveSlotAsync(int slot)
    {
        // Evolving rewrites the mon, so Hardcore mode refuses it like any other edit.
        if (await DeniedAsync(SaveAction.EditMon)) return;
        var session = _sessionsFor();
        var service = IPlatformApplication.Current?.Services.GetService<IEvolutionService>();
        if (session is null || service is null || _viewModel.IsBusy) return;
        var box = _viewModel.BoxIndex;
        var request = await EvolutionCard.RunAsync(_hostGrid, session, box, slot);
        if (request is null) return;
        var ok = await _viewModel.RunMutationAsync(s => service.Evolve(s, box, slot, request), slot,
            changeDescription: "Evolve", action: SaveAction.EditMon);
        _canvas.InvalidateSurface();
        await PadMenu.ShowAsync(_hostGrid, ok ? "CONGRATULATIONS!" : "EVOLUTION FAILED", _viewModel.Status, "OK");
    }

    /// <summary>Clone the mon in place: party clones append (cap 6), box clones fill the
    /// box's first empty slot. PKSM-style Duplicate, one backup per write.</summary>
    private async Task DuplicateSlotAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null || _viewModel.IsBusy) return;
        var box = _viewModel.BoxIndex;
        var source = session.ReadEntity(box, slot);
        if (source.IsEmpty) return;
        var name = source.Nickname is { Length: > 0 } nick ? nick : "Pokémon";
        var destination = box == -1
            ? Enumerable.Range(0, 6).Count(i => !session.ReadEntity(-1, i).IsEmpty)
            : _viewModel.VisibleSlots.Select(x => x.Slot).FirstOrDefault(i => session.ReadEntity(box, i).IsEmpty, -1);
        if (destination < 0 || (box == -1 && destination >= 6))
        {
            _viewModel.Status = box == -1 ? "The party is full - no room to duplicate." : "This box is full - no room to duplicate.";
            await PadMenu.ShowAsync(_hostGrid, "DUPLICATE", _viewModel.Status, "OK");
            return;
        }
        var ok = await _viewModel.RunMutationAsync(s =>
            s.DuplicateSlot(box, slot, box, destination)
                ? new GenerationOutcome(true, $"{name} duplicated.")
                : new GenerationOutcome(false, "Could not duplicate into that slot."), destination, action: SaveAction.Duplicate);
        if (!ok)
        {
            await PadMenu.ShowAsync(_hostGrid, "DUPLICATE FAILED", _viewModel.Status, "OK");
            return;
        }
        _viewModel.RefreshAllSlots(includeEmptyPartySlots: box == -1);
        _viewModel.SelectSlot(destination);
        _canvas.InvalidateSurface();
    }

    /// <summary>
    /// Batch identity toolkit: reset nicknames to species names, or adopt the
    /// connected trainer's OT across a scope. One write, one restore point; the
    /// confirmation says plainly that legality is not re-checked afterwards.
    /// </summary>
    private async Task ShowBatchRenameAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;

        var scope = _viewModel.MarkedCount > 0
            ? await PadMenu.ShowAsync(_hostGrid, "APPLY TO…", null,
                $"Marked Pokémon ({_viewModel.MarkedCount})", "This box", "All boxes", "Cancel")
            : await PadMenu.ShowAsync(_hostGrid, "APPLY TO…", null, "This box", "All boxes", "Cancel");
        if (scope is null or "Cancel") return;

        var op = await PadMenu.ShowAsync(_hostGrid, "WHICH OPERATION?", null,
            "Reset nicknames to species names",
            "Set OT to mine (adopt my trainer ID)",
            "Cancel");
        if (op is null or "Cancel") return;

        var marked = _viewModel.MarkedSlots.ToArray();
        var slots = scope.StartsWith("Marked", StringComparison.Ordinal) ? marked
            : scope == "This box"
                ? _viewModel.Save?.Slots.Where(x => x.Box == _viewModel.BoxIndex)
                    .Select(x => x.Slot).Distinct().OrderBy(x => x)
                    .Select(s => (_viewModel.BoxIndex, Slot: s)).ToArray()
                    ?? Enumerable.Range(0, 30).Select(s => (_viewModel.BoxIndex, Slot: s)).ToArray()
                : null; // all boxes: the engine walks every slot itself
        var affected = slots?.Length ?? -1;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "APPLY TO EVERY MATCHING POKÉMON?",
            $"{op} · {(affected < 0 ? "party + every box" : $"{affected} slot(s)")}\n" +
            "Nickname and OT changes are NOT legality-re-checked afterwards.", "Apply");
        if (!confirmed) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "APPLYING…", "One write, one restore point.");
        try
        {
            if (op.StartsWith("Reset nicknames", StringComparison.Ordinal))
            {
                await _viewModel.RunMutationAsync(s =>
                {
                    var touched = slots is null ? s.BatchApply(["IsNicknamed=false"]) : s.BatchApplySlots(slots, ["IsNicknamed=false"]);
                    return new GenerationOutcome(touched > 0, $"Nicknames reset for {touched} Pokémon (legality not re-checked).");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            }
            else
            {
                var profileStore = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();
                var profile = profileStore.Profiles.FirstOrDefault(); // null = MakeMine derives from the save itself
                await _viewModel.RunMutationAsync(s =>
                {
                    var targets = slots is null
                        ? s.Snapshot.Slots.Where(x => x.Box >= 0 && x.Species is > 0).Select(x => (x.Box, x.Slot)).ToArray()
                        : slots!;
                    var touched = 0;
                    foreach (var (box, monSlot) in targets)
                        if (s.ReadEntity(box, monSlot).Species > 0 && s.MakeMine(box, monSlot, profile).Success) touched++;
                    return new GenerationOutcome(touched > 0, $"OT adopted for {touched} Pokémon (legality not re-checked).");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            }
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>
    /// Rolls a random legal team from the game's own species pool and offers to
    /// place it in the party (overwriting) or the first empty box slots.
    /// </summary>
    private async Task ShowRandomTeamAsync()
    {
        var session = _sessionsFor();
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        var legalizer = services?.GetService<ILegalizerService>();
        if (session is null || data is null || legalizer is null) return;

        var countChoice = await PadMenu.ShowAsync(_hostGrid, "TEAM SIZE", null, "1", "2", "3", "4", "5", "6");
        if (countChoice is null || !int.TryParse(countChoice, out var count)) return;
        var levelChoice = await PadMenu.ShowAsync(_hostGrid, "LEVEL BAND", null,
            "Lv 50", "Lv 100", "Wild card (5-70)");
        if (levelChoice is null) return;
        var (lo, hi) = levelChoice switch
        {
            "Lv 50" => (50, 50),
            "Lv 100" => (100, 100),
            _ => (5, 70),
        };
        var filters = await PadMenu.ShowAsync(_hostGrid, "FILTERS", null,
            "No restrictions", "No legendaries", "No duplicates", "No legendaries, no duplicates");
        if (filters is null) return;
        var noLegendaries = filters.Contains("legendary", StringComparison.OrdinalIgnoreCase);
        var noDuplicates = filters.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

        // The game's own pool: species the engine can produce for this save.
        var max = session.MaxSpeciesId;
        var pool = new List<int>(max);
        for (var species = 1; species <= max; species++)
            if (species < data.SpeciesNames.Count && data.SpeciesNames[species].Length > 0)
                pool.Add(species);
        var options = new RandomTeamOptions(count, lo, hi, noLegendaries, noDuplicates, AllowNfe: true);
        var team = RandomTeamPlanner.Plan(pool, new HashSet<int>(), options, Random.Shared);

        var roster = string.Join(", ", team.Select(t => $"{data.SpeciesNames[t.Species]} Lv{t.Level}"));
        var placement = await PadMenu.ShowAsync(_hostGrid, "ROLL THIS TEAM?",
            roster + "\nEach mon is generated legal for this game.", "Into the party", "Into first empty box slots", "Cancel");
        if (placement is null or "Cancel") return;

        var overlay = LoadingOverlay.Show(_hostGrid, "GENERATING TEAM…", "The offline legalizer is at work.");
        try
        {
            var generated = new List<(byte[] Data, int Level, string Name)>();
            foreach (var (species, level) in team)
            {
                var mon = await Task.Run(() => legalizer.GenerateData(session,
                    new GenerationRequest(species, level, Shiny: false, null, null, null, null)));
                if (mon is not null) generated.Add((mon.Data, level, data.SpeciesNames[species]));
            }
            if (generated.Count == 0) { _viewModel.Status = "The legalizer could not build this team."; return; }

            if (placement == "Into the party")
            {
                var ok = await _viewModel.RunMutationAsync(s =>
                {
                    for (var i = 0; i < generated.Count; i++)
                        s.ImportSlot(-1, i, generated[i].Data);
                    return new GenerationOutcome(true, $"Random team of {generated.Count} now in the party.");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.CreateMon);
                if (!ok) return;
            }
            else
            {
                var empties = _viewModel.Save?.Slots.Where(x => x.Box >= 0 && x.Species is null)
                    .Select(x => (x.Box, x.Slot)).Take(generated.Count).ToArray();
                if (empties is null || empties.Length < generated.Count)
                {
                    _viewModel.Status = $"Not enough empty box slots ({generated.Count} needed).";
                    return;
                }
                var ok = await _viewModel.RunMutationAsync(s =>
                {
                    for (var i = 0; i < generated.Count; i++)
                        s.ImportSlot(empties[i].Box, empties[i].Slot, generated[i].Data);
                    return new GenerationOutcome(true, $"Random team of {generated.Count} placed in the boxes.");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.CreateMon);
                if (!ok) return;
            }
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>
    /// Game-to-game: pick any other detected save, the transfer service converts and
    /// writes there, then the mon leaves this box (a real move, not a copy).
    /// </summary>
    private async Task SendSlotToAnotherGameAsync(int slot, string nickname, bool copyInsteadOfMove = false)
    {
        // Copy-to-game duplicates; the move is the only transfer Hardcore mode allows.
        if (copyInsteadOfMove && Denied(SaveAction.Duplicate)) return;
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<SavePickerViewModel>();
        var transfer = services?.GetService<Services.TransferService>();
        var session = _sessionsFor();
        if (picker is null || transfer is null || session is null) return;
        // A move must be able to release the original before the destination is written.
        if (!copyInsteadOfMove && _viewModel.OpenSaveRefusesWrites()) return;

        var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
        var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
            "SEND TO GAME", $"{nickname} → pick the destination (the mon leaves this box)", currentDoc);
        if (target is null)
        {
            if (picker.Saves.Count == 0)
                _viewModel.Status = "No other games linked. Link another emulator or save on Home.";
            return;
        }

        var verb = copyInsteadOfMove ? "Copy" : "Move";
        var confirm = await PadMenu.ConfirmAsync(_hostGrid, $"{verb.ToUpperInvariant()} TO ANOTHER GAME?",
            copyInsteadOfMove
                ? $"A copy of {nickname} joins {target.GameLabel}; the original stays here."
                : $"{nickname} will leave this box and join {target.GameLabel} (box space permitting).", verb);
        if (!confirm) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        // Cross-generation conversions change things; the diff preview is the trust
        // step before the write. Same-generation sends show an empty diff.
        var preview = await transfer.PreviewAsync(export.Data, nickname, target);
        if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, preview, nickname, target.GameLabel)) return;
        var outcome = await transfer.SendToGameAsync(export.Data, nickname, target);
        _viewModel.Status = outcome.Message;
        if (!outcome.Success || copyInsteadOfMove) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} moved to {target.GameLabel}.");
        }, slot, action: SaveAction.Move);
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

    /// <summary>
    /// Deposit: a copy duplicates into the vault and leaves the game untouched; a move
    /// additionally empties the game slot (one safe write, one restore point).
    /// </summary>
    private async Task SendToBankAsync(int slot, string nickname, bool copyInsteadOfMove = false)
    {
        // "Copy to Bank" duplicates (the original stays); "Send to Bank" is a move and
        // Hardcore mode keeps it, so the two paths are told apart right here.
        if (copyInsteadOfMove && Denied(SaveAction.Duplicate)) return;
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

        if (copyInsteadOfMove)
        {
            bank.Add(export.Data, info);
            _viewModel.Status = $"{nickname} copied to the Bank. The original stays in the game.";
            return;
        }

        var ok = await _viewModel.RunMutationAsync(s =>
        {
            s.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} deposited in the Bank.");
        }, slot, changeDescription: $"Deposit {nickname} in the Bank ({(_viewModel.BoxIndex == -1 ? $"Party {slot + 1}" : $"Box {_viewModel.BoxIndex + 1}, Slot {slot + 1}")})",
            action: SaveAction.Move);
        if (ok)
        {
            bank.Add(export.Data, info);
            _canvas.InvalidateSurface();
        }
    }

    /// <summary>Release with confirmation; the pre-release state stays recoverable as a restore point.</summary>
    private async Task ReleaseSlotAsync(int slot, string nickname)
    {
        var docId = DocumentId;
        var session = _sessionsFor();
        if (docId is not null && session is not null)
        {
            var rng = session.GetRngInfo(_viewModel.BoxIndex, slot);
            if (!Protection.CanRelease(docId, _viewModel.BoxIndex, slot, rng.Pid))
            {
                _viewModel.Status = $"{nickname.ToUpperInvariant()} IS LOCKED - UNLOCK IT FIRST";
                return;
            }
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "RELEASE?",
            $"Release {nickname} back into the wild? The current save state is kept as a restore point.",
            "Release");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(session =>
        {
            session.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} was released. Bye-bye!");
        }, slot, action: SaveAction.Release);
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
            case "About PKForge": await AboutPopup.ShowAsync(_hostGrid); break;
        }
    }

    /// <summary>The Thor's second screen mirrors the box automatically while this page is open.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // The mode gates this whole screen, so the strip says so the moment it opens.
        if (HardcoreMode.IsOn) _viewModel.Status = HardcoreMode.StatusLine;
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable != true) return;
        try { _ = host.ShowAsync(); }
        catch { /* single-screen devices and flaky displays must never break the box */ }
    }

    protected override void OnDisappearing()
    {
        if (_boxManageMode) ExitBoxManageMode();
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    private View BuildEditor()
    {
        EditorFocusTargets = [];
        _editorFocusIndex = 0;

        var legality = new Button
        {
            Text = "ILLEGAL - VIEW REPORT",
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.Bad,
            FontFamily = DsChrome.PixelFont,
            FontSize = 12,
            HeightRequest = 34,
            CornerRadius = 6,
            IsVisible = false,
        };
        legality.Clicked += async (_, _) =>
        {
            var detail = string.IsNullOrWhiteSpace(_viewModel.LegalityText) ? "No legality details were reported." : _viewModel.LegalityText;
            await ShowLegalityReportAsync(detail);
        };

        void UpdateLegalityAction()
        {
            legality.IsVisible = _viewModel.LegalityBadge == "✗";
        }
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.LegalityBadge))
                UpdateLegalityAction();
        };
        UpdateLegalityAction();

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        View FocusBorder(View inner, string caption, Func<Task> activate, string? numericBindingPath = null)
        {
            var border = (Border)inner;
            EditorFocusTargets = [.. EditorFocusTargets, new EditorFocusTarget(border, caption, activate, numericBindingPath)];
            return border;
        }

        Button FocusButton(Button button, string caption)
        {
            EditorFocusTargets = [.. EditorFocusTargets, new EditorFocusTarget(button, caption, () => { button.SendClicked(); return Task.CompletedTask; },
                OriginalBackground: button.BackgroundColor, OriginalTextColor: button.TextColor)];
            return button;
        }

        // Abilities follow the pending species edit, so a changed species offers its own.
        List<PickItem> AbilityItems()
        {
            var (species, form) = PendingSpeciesForm();
            return InfoPickers.AbilityItems(data, _sessionsFor(), species, form);
        }
        List<PickItem> MoveItems() => AllItems(data.MoveNames, includeZero: true, zeroLabel: "(none)");
        Task OpenMove(string caption, string vmProperty) => OpenMovePickerAsync(data, caption, vmProperty);
        Task OpenItem() => OpenHeldItemPickerAsync(data);

        View? nicknameRow = null;
        View? levelRow = null;
        View? otRow = null;

        var species = FocusBorder(NamedPicker("SPECIES", nameof(BoxBrowserViewModel.EditSpecies), data.SpeciesNames, null,
            openPokedex: true, shaded: false), "SPECIES", async () =>
        {
            var session = _sessionsFor();
            if (session is null) return;
            var picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            if (picked is not null) SetVmString(nameof(BoxBrowserViewModel.EditSpecies), picked.Id.ToString());
        });
        var nickname = FocusBorder(FieldRow("Nickname", nameof(BoxBrowserViewModel.EditNickname), shaded: true), "NICKNAME", () =>
        {
            if (nicknameRow is not null) FocusEntry(nicknameRow);
            return Task.CompletedTask;
        });
        nicknameRow = nickname;
        var level = FocusBorder(FieldRow("Level", nameof(BoxBrowserViewModel.EditLevel), shaded: false), "LEVEL", () =>
        {
            if (levelRow is not null) FocusEntry(levelRow);
            return Task.CompletedTask;
        }, nameof(BoxBrowserViewModel.EditLevel));
        levelRow = level;
        var nature = FocusBorder(NamedPicker("NATURE", nameof(BoxBrowserViewModel.EditNature), NaturePicker.DisplayNames(data.NatureNames),
            null, shaded: true, open: () => OpenNaturePickerAsync(data)), "NATURE", () => OpenNaturePickerAsync(data));
        // Gen 1/2 have no natures: the row would edit nothing.
        nature.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 3;
        var ability = FocusBorder(NamedPicker("ABILITY", nameof(BoxBrowserViewModel.EditAbility), data.AbilityNames,
            AbilityItems, shaded: false), "ABILITY", async () => await OpenNamedPickerAsync("ABILITY", nameof(BoxBrowserViewModel.EditAbility), AbilityItems));
        var item = FocusBorder(NamedPicker("HELD ITEM", nameof(BoxBrowserViewModel.EditHeldItem), data.ItemNames,
            () => ItemsWithIcons(data.ItemNames), shaded: true, open: OpenItem), "HELD ITEM", OpenItem);
        var move1 = FocusBorder(NamedPicker("MOVE 1", nameof(BoxBrowserViewModel.EditMove1), data.MoveNames, MoveItems, shaded: false,
            open: () => OpenMove("MOVE 1", nameof(BoxBrowserViewModel.EditMove1))), "MOVE 1", () => OpenMove("MOVE 1", nameof(BoxBrowserViewModel.EditMove1)));
        var move2 = FocusBorder(NamedPicker("MOVE 2", nameof(BoxBrowserViewModel.EditMove2), data.MoveNames, MoveItems, shaded: true,
            open: () => OpenMove("MOVE 2", nameof(BoxBrowserViewModel.EditMove2))), "MOVE 2", () => OpenMove("MOVE 2", nameof(BoxBrowserViewModel.EditMove2)));
        var move3 = FocusBorder(NamedPicker("MOVE 3", nameof(BoxBrowserViewModel.EditMove3), data.MoveNames, MoveItems, shaded: false,
            open: () => OpenMove("MOVE 3", nameof(BoxBrowserViewModel.EditMove3))), "MOVE 3", () => OpenMove("MOVE 3", nameof(BoxBrowserViewModel.EditMove3)));
        var move4 = FocusBorder(NamedPicker("MOVE 4", nameof(BoxBrowserViewModel.EditMove4), data.MoveNames, MoveItems, shaded: true,
            open: () => OpenMove("MOVE 4", nameof(BoxBrowserViewModel.EditMove4))), "MOVE 4", () => OpenMove("MOVE 4", nameof(BoxBrowserViewModel.EditMove4)));
        var stats = StatsRow("STATS", nameof(BoxBrowserViewModel.EditStats), shaded: true);
        var ivs = FocusBorder(StatsField("IVS", nameof(BoxBrowserViewModel.EditIvs), () => _sessionsFor()?.GetTrainingCaps().IvMax ?? 31, shaded: false), "IVS", async () => await OpenStatsEditorAsync("IVS", nameof(BoxBrowserViewModel.EditIvs), () => _sessionsFor()?.GetTrainingCaps().IvMax ?? 31));
        var evs = FocusBorder(StatsField("EVS", nameof(BoxBrowserViewModel.EditEvs), () => _sessionsFor()?.GetTrainingCaps().EvMax ?? 252, shaded: true), "EVS", async () => await OpenStatsEditorAsync("EVS", nameof(BoxBrowserViewModel.EditEvs), () => _sessionsFor()?.GetTrainingCaps().EvMax ?? 252));
        var ball = FocusBorder(NamedPicker("BALL", nameof(BoxBrowserViewModel.EditBall), data.BallNames, BallItems, shaded: false), "BALL", async () => await OpenNamedPickerAsync("BALL", nameof(BoxBrowserViewModel.EditBall), BallItems));
        var genderValue = Kit.BlueprintValue(13);
        var genderChevron = new Label
        {
            Text = ">", FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Blueprint,
            FontSize = 13, VerticalTextAlignment = TextAlignment.Center,
        };
        var gender = FocusBorder(RowChrome("GENDER", genderValue, false, genderChevron), "GENDER", async () => await OpenGenderPickerAsync());
        var genderTap = new TapGestureRecognizer();
        genderTap.Tapped += async (_, _) => await OpenGenderPickerAsync();
        gender.GestureRecognizers.Add(genderTap);
        genderValue.Text = _viewModel.EditGender switch { "0" => "Male", "1" => "Female", _ => "Genderless" };
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.EditGender) or nameof(BoxBrowserViewModel.Selected))
                genderValue.Text = _viewModel.EditGender switch { "0" => "Male", "1" => "Female", _ => "Genderless" };
        };
        var ot = FocusBorder(FieldRow("OT", nameof(BoxBrowserViewModel.EditOt), shaded: true), "OT", () =>
        {
            if (otRow is not null) FocusEntry(otRow);
            return Task.CompletedTask;
        });
        otRow = ot;
        var shinyToggle = new Switch { OnColor = UiTokens.Gold };
        shinyToggle.SetBinding(Switch.IsToggledProperty, nameof(BoxBrowserViewModel.EditShiny));
        var shiny = FocusBorder(Striped(new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "Shiny", FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center },
                shinyToggle,
            },
        }, false), "SHINY", () =>
        {
            shinyToggle.IsToggled = !shinyToggle.IsToggled;
            return Task.CompletedTask;
        });

        var legalize = FocusButton(Kit.Capsule("LEGALIZE", UiTokens.Green), "LEGALIZE");
        legalize.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            if (slot < 0) return;
            var overlay = LoadingOverlay.Show(_hostGrid, "LEGALIZING…", "Finding the closest real, legal version of this Pokémon.");
            try
            {
                await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot, SaveAction.EditMon);
                _canvas.InvalidateSurface();
            }
            finally { overlay.Close(); }
        };

        var makeMine = FocusButton(Kit.Capsule("MAKE MINE", UiTokens.Gold), "MAKE MINE");
        makeMine.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            if (slot < 0) return;
            await _viewModel.RunMutationAsync(s => s.MakeMine(_viewModel.BoxIndex, slot), slot);
            _canvas.InvalidateSurface();
        };

        var showdown = FocusButton(Kit.Capsule("SHOWDOWN", UiTokens.Ink1), "SHOWDOWN");
        showdown.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ShowShowdownAsync(_viewModel.SelectedSlot); };
        var exportPk = FocusButton(Kit.Capsule("EXPORT .PK", UiTokens.Ink1), "EXPORT .PK");
        exportPk.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ExportSlotAsync(_viewModel.SelectedSlot); };
        var qr = FocusButton(Kit.Capsule("QR", UiTokens.Ink1), "QR");
        qr.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ShowQrAsync(_viewModel.SelectedSlot); };

        var save = FocusButton(Kit.Capsule("SAVE CHANGES", UiTokens.Green), "SAVE CHANGES");
        save.Margin = new Thickness(0, 8, 0, 0);
        save.SetBinding(Button.CommandProperty, nameof(BoxBrowserViewModel.SaveEditCommand));

        var met = FocusButton(Kit.Capsule("MET / ORIGIN", UiTokens.Cyan), "MET / ORIGIN");
        met.Clicked += async (_, _) => await RunSubEditorAsync(MetOriginEditor.ShowAsync, "Met / origin updated");
        var moveDetails = FocusButton(Kit.Capsule("MOVE DETAILS", UiTokens.Cyan), "MOVE DETAILS");
        moveDetails.Clicked += async (_, _) => await RunSubEditorAsync(MoveDetailsEditor.ShowAsync, "Move details updated");
        var moveShop = FocusButton(Kit.Capsule("MOVE SHOP", UiTokens.Cyan), "MOVE SHOP");
        moveShop.Clicked += async (_, _) => await RunSubEditorAsync(MoveShopEditor.ShowAsync, "Move Shop updated");
        var potential = FocusButton(Kit.Capsule("POTENTIAL", UiTokens.Cyan), "POTENTIAL");
        potential.Clicked += async (_, _) => await RunSubEditorAsync(PotentialEditor.ShowAsync, "Potential updated");
        var cosmetics = FocusButton(Kit.Capsule("COSMETICS", UiTokens.Cyan), "COSMETICS");
        cosmetics.Clicked += async (_, _) => await RunSubEditorAsync(CosmeticsEditor.ShowAsync, "Cosmetics updated");
        var awards = FocusButton(Kit.Capsule("AWARDS", UiTokens.Cyan), "AWARDS");
        awards.Clicked += async (_, _) => await RunSubEditorAsync(AwardsEditor.ShowAsync, "Awards updated");
        var ribbonAlbum = FocusButton(Kit.Capsule("RIBBON ALBUM", UiTokens.Cyan), "RIBBON ALBUM");
        ribbonAlbum.Clicked += async (_, _) => await RunSubEditorAsync(
            (host, session, box, slot) => ShowRibbonAlbumAsync(host, session, box, slot), "Ribbon album updated");

        var lastFieldIndex = Array.FindLastIndex(EditorFocusTargets, target => target.Neighbors is null && target.View is Border);
        int IndexOfCaption(string caption) => Array.FindIndex(EditorFocusTargets, target => target.Caption == caption);
        var saveIndex = IndexOfCaption("SAVE CHANGES");
        var legalizeIndex = IndexOfCaption("LEGALIZE");
        var makeMineIndex = IndexOfCaption("MAKE MINE");
        var showdownIndex = IndexOfCaption("SHOWDOWN");
        var exportIndex = IndexOfCaption("EXPORT .PK");
        var qrIndex = IndexOfCaption("QR");
        var metIndex = IndexOfCaption("MET / ORIGIN");
        var moveDetailsIndex = IndexOfCaption("MOVE DETAILS");
        var moveShopIndex = IndexOfCaption("MOVE SHOP");
        var potentialIndex = IndexOfCaption("POTENTIAL");
        var cosmeticsIndex = IndexOfCaption("COSMETICS");
        var ribbonAlbumIndex = IndexOfCaption("RIBBON ALBUM");
        var awardsIndex = IndexOfCaption("AWARDS");

        EditorFocusTargets = EditorFocusTargets
            .Select((target, index) => target.Caption switch
            {
                "SAVE CHANGES" => target with { Neighbors = new EditorFocusNeighbors(index, index, lastFieldIndex, legalizeIndex) },
                "LEGALIZE" => target with { Neighbors = new EditorFocusNeighbors(index, makeMineIndex, saveIndex, exportIndex) },
                "MAKE MINE" => target with { Neighbors = new EditorFocusNeighbors(legalizeIndex, showdownIndex, saveIndex, qrIndex) },
                "SHOWDOWN" => target with { Neighbors = new EditorFocusNeighbors(makeMineIndex, index, saveIndex, metIndex) },
                "EXPORT .PK" => target with { Neighbors = new EditorFocusNeighbors(index, qrIndex, legalizeIndex, moveDetailsIndex) },
                "QR" => target with { Neighbors = new EditorFocusNeighbors(exportIndex, metIndex, makeMineIndex, awardsIndex) },
                "MET / ORIGIN" => target with { Neighbors = new EditorFocusNeighbors(qrIndex, moveDetailsIndex, showdownIndex, awardsIndex) },
                "MOVE DETAILS" => target with { Neighbors = new EditorFocusNeighbors(metIndex, moveShopIndex, exportIndex, index) },
                "MOVE SHOP" => target with { Neighbors = new EditorFocusNeighbors(moveDetailsIndex, potentialIndex, exportIndex, index) },
                "POTENTIAL" => target with { Neighbors = new EditorFocusNeighbors(moveShopIndex, cosmeticsIndex, exportIndex, index) },
                "AWARDS" => target with { Neighbors = new EditorFocusNeighbors(cosmeticsIndex, ribbonAlbumIndex, qrIndex, index) },
                "RIBBON ALBUM" => target with { Neighbors = new EditorFocusNeighbors(awardsIndex, index, qrIndex, index) },
                _ => target,
            })
            .ToArray();

        var monActions = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var button in new[] { legalize, makeMine, showdown, exportPk, qr, met, moveDetails, moveShop, potential, cosmetics, awards, ribbonAlbum })
        {
            button.FontSize = 11;
            button.Padding = new Thickness(10, 6);
            button.Margin = new Thickness(0, 0, 6, 6);
            monActions.Children.Add(button);
        }

        // Hardcore mode: the panel stays readable, but the note says up front why APPLY
        // and the mon-edit buttons refuse - rather than failing silently on press.
        var hardcoreNote = new Label
        {
            Text = HardcoreMode.StatusLine,
            FontFamily = DsChrome.PixelFont,
            FontSize = 11,
            TextColor = UiTokens.Bad,
            HorizontalTextAlignment = TextAlignment.Center,
            IsVisible = HardcoreMode.IsOn,
        };

        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                hardcoreNote,
                legality,
                species, nickname, level, LevelInfoCard(), nature, ability, item,
                move1, move2, move3, move4,
                stats, ivs, evs, ball, gender, ot, shiny,
                save,
                monActions,
            },
        };
    }

    private Task ShowLegalityReportAsync(string detail)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            _hostGrid.Remove(overlay);
            pad?.Dispose();
            done.TrySetResult();
        }

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar("ILLEGALITY REPORT"),
                new ScrollView
                {
                    HeightRequest = 220,
                    Content = new Label
                    {
                        Text = detail,
                        TextColor = UiTokens.Ink0,
                        FontSize = 12,
                        LineBreakMode = LineBreakMode.WordWrap,
                    },
                },
                Kit.HintBar(("B", "Back", Close)),
            },
        };

        var window = Kit.OverlayWindow(_hostGrid, content, preferredMaxWidth: 340, padding: 14);
        overlay = Kit.AttachOverlay(_hostGrid, window, Close);
        pad = new PadOverlay(Close, Close);
        return done.Task;
    }

    /// <summary>The ribbon album: every ribbon the format stores as tappable tiles with
    /// the legal-obtainable set lit (PKHeX's own per-encounter rules), an award-all
    /// shortcut, and marking chips that stamp the whole organizer selection at once.</summary>
    private async Task<bool> ShowRibbonAlbumAsync(Grid host, Domain.ISaveEngineSession session, int box, int slot)
    {
        var ribbons = session.GetRibbons(box, slot);
        if (ribbons.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "RIBBON ALBUM", "This Pokémon format stores no ribbons or marks.", "OK");
            return false;
        }

        // Ribbon legality runs a full PKHeX analysis per Pokémon: keep it off the UI thread.
        var loading = LoadingOverlay.Show(host, "RIBBON ALBUM", "Checking which ribbons this Pokémon can legally earn…");
        IReadOnlyDictionary<string, int> maxima;
        try
        {
            maxima = await Task.Run(() => session.GetObtainableRibbonMaxima(box, slot));
        }
        finally
        {
            loading.Close();
        }

        var dirty = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            host.Remove(overlay);
            pad?.Dispose();
            done.TrySetResult();
        }

        var summary = Kit.LcdLabel(12);
        void UpdateSummary(IReadOnlyList<RibbonAlbumEntry> entries)
        {
            var owned = entries.Count(e => e.Value != 0);
            var obtainable = entries.Count(e => e.Obtainable);
            summary.Text = $"{owned} owned · {obtainable} more obtainable · lit = legal for this Pokémon";
        }

        var tiles = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceBetween,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
        };

        async Task ToggleAsync(RibbonAlbumEntry entry)
        {
            if (entry.MaxValue == 1)
            {
                session.SetRibbon(box, slot, entry.Id, entry.Value == 0 ? 1 : 0);
                dirty = true;
            }
            else
            {
                var value = await StatsPopup.ShowSingleAsync(host, entry.Name, entry.Value, entry.MaxValue);
                if (value is not { } count || count == entry.Value) return;
                session.SetRibbon(box, slot, entry.Id, count);
                dirty = true;
            }
            Rebuild();
        }

        void Rebuild()
        {
            var entries = RibbonAlbum.Build(session.GetRibbons(box, slot), maxima);
            UpdateSummary(entries);
            tiles.Children.Clear();
            foreach (var entry in entries)
            {
                var current = entry;
                var image = new Image { WidthRequest = 30, HeightRequest = 30, HorizontalOptions = LayoutOptions.Center };
                var path = RibbonIconPath(entry.Id);
                if (path is not null) image.Source = ImageSource.FromFile(path);
                var name = new Label
                {
                    Text = entry.Name,
                    TextColor = UiTokens.Ink0,
                    FontSize = 7,
                    FontFamily = DsChrome.PixelFont,
                    HorizontalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1,
                };
                var tile = new Border
                {
                    WidthRequest = 52,
                    HeightRequest = 52,
                    Padding = new Thickness(2),
                    StrokeThickness = entry.Value != 0 ? 2.5f : 1.5f,
                    Stroke = entry.Value != 0 ? UiTokens.Gold : entry.Obtainable ? UiTokens.Green : UiTokens.ShellEdge,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                    Content = new VerticalStackLayout { Children = { image, name } },
                    Opacity = entry.Value != 0 || entry.Obtainable ? 1 : 0.35,
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await ToggleAsync(current);
                tile.GestureRecognizers.Add(tap);
                tiles.Children.Add(tile);
            }
        }

        // Marking chips: the organizer's stamps. With a marked selection they stamp
        // every marked mon (one write on close); otherwise just this Pokémon.
        var markings = session.GetCosmetics(box, slot).Markings;
        var targets = _viewModel.MarkedCount > 0
            ? _viewModel.MarkedSlots.ToList()
            : new List<(int Box, int Slot)> { (box, slot) };
        var markRow = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
        var markCaption = new Label
        {
            Text = _viewModel.MarkedCount > 0 ? $"MARKS →{_viewModel.MarkedCount} MARKED:" : "MARKS →",
            TextColor = UiTokens.Ink1,
            FontFamily = DsChrome.PixelFont,
            FontSize = 9,
            VerticalTextAlignment = TextAlignment.Center,
        };
        markRow.Children.Add(markCaption);
        for (var i = 0; i < markings.Count; i++)
        {
            var index = i;
            var marking = markings[i];
            var chip = Kit.Capsule(marking.Name.ToUpperInvariant(), MarkingColor(marking.Value));
            chip.FontSize = 9;
            chip.Padding = new Thickness(8, 4);
            chip.Margin = new Thickness(0, 0, 4, 4);
            chip.Clicked += (_, _) =>
            {
                // Cycle off → blue → pink (where the format has it) → off.
                var next = (marking.Value + 1) % (marking.MaxValue + 1);
                foreach (var target in targets)
                {
                    var cosmetics = session.GetCosmetics(target.Box, target.Slot);
                    var values = cosmetics.Markings.Select(m => m.Value).ToArray();
                    if (index >= values.Length) continue;
                    values[index] = next;
                    session.ApplyCosmeticEdit(target.Box, target.Slot, new CosmeticEdit(Markings: values));
                }
                dirty = true;
                marking = markings[index] with { Value = next };
                chip.BackgroundColor = MarkingColor(next);
            };
            markRow.Children.Add(chip);
        }

        var awardAll = Kit.Capsule("AWARD ALL OBTAINABLE", UiTokens.Green);
        awardAll.Clicked += (_, _) =>
        {
            var changed = session.AwardAllObtainableRibbons(box, slot);
            if (changed > 0) dirty = true;
            summary.Text = changed > 0
                ? $"Awarded {changed} more ribbons."
                : "Nothing more this Pokémon can legally earn.";
            Rebuild();
        };
        var close = Kit.Capsule("DONE", UiTokens.Ink1);

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar("RIBBON ALBUM"),
                summary,
                new ScrollView { Content = tiles, MaximumHeightRequest = 260 },
                markings.Count > 0 ? markRow : new BoxView { HeightRequest = 0 },
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { awardAll, close } },
                Kit.HintBar(("TAP", "Toggle ribbon", null), ("B", "Back", Close)),
            },
        };

        Rebuild();
        overlay = Kit.AttachOverlay(host, Kit.OverlayWindow(host, content, preferredMaxWidth: 440, padding: 12), Close);
        pad = new PadOverlay(Close, Close);
        await done.Task;
        return dirty;
    }

    private static Color MarkingColor(int value) => value switch
    {
        1 => UiTokens.MenuBlue,
        2 => UiTokens.GiftPinkLight,
        _ => UiTokens.Ink1,
    };

    /// <summary>Bundled ribbon artwork copied to cache once so Image can load it by file path.</summary>
    private static string? RibbonIconPath(string id)
    {
        var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, $"ribbon-{id.ToLowerInvariant()}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            using var asset = FileSystem.OpenAppPackageFileAsync($"ribbons/{id.ToLowerInvariant()}.png").GetAwaiter().GetResult();
            using var output = File.Create(cache);
            asset.CopyTo(output);
            return cache;
        }
        catch
        {
            return null;
        }
    }

    private void FocusEntry(View row)
    {
        if (row is Border { Content: Grid grid })
            foreach (var child in grid.Children)
                if (child is Entry entry)
                {
                    entry.Focus();
                    return;
                }
    }

    private async Task OpenGenderPickerAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "GENDER", null,
            new PadOption("Male", IconPath: "male"),
            new PadOption("Female", IconPath: "female"),
            new PadOption("Genderless", IconPath: "genderless"));
        var gender = choice switch { "Male" => 0, "Female" => 1, "Genderless" => 2, _ => (int?)null };
        if (gender is { } value)
        {
            SetVmString(nameof(BoxBrowserViewModel.EditGender), value.ToString());
            UpdateGenderRow();
        }
    }

    private void UpdateGenderRow()
    {
        foreach (var target in EditorFocusTargets)
        {
            if (target.Caption != "GENDER" || target.View is not Border { Content: Grid grid }) continue;
            foreach (var child in grid.Children)
                if (child is Label { Text: not "GENDER" } label)
                {
                    label.Text = GetVmString(nameof(BoxBrowserViewModel.EditGender)) switch
                    {
                        "0" => "Male",
                        "1" => "Female",
                        _ => "Genderless",
                    };
                    return;
                }
        }
    }

    private async Task OpenNamedPickerAsync(string caption, string vmProperty, Func<List<PickItem>> itemsFactory)
    {
        var items = itemsFactory();
        if (items.Count == 0) return;
        int? current = int.TryParse(GetVmString(vmProperty), out var id) ? id : null;
        var picked = await PickerMenu.ShowAsync(_hostGrid, caption, items, current);
        if (picked is not null)
            SetVmString(vmProperty, picked.Id.ToString());
    }

    /// <summary>
    /// The nature row: labelled choices plus live stats that use the editor's pending
    /// species, level, IVs and EVs, so the numbers match what SAVE would write.
    /// </summary>
    private async Task OpenNaturePickerAsync(IGameDataService data)
    {
        var session = _sessionsFor();
        var slot = _viewModel.SelectedSlot;
        if (session is null || slot < 0 || session.Generation <= 2) return;
        static int? Int(string? text) => int.TryParse(text, out var v) ? v : null;
        static IReadOnlyList<int>? Six(string? text)
        {
            var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = parts.Select(p => int.TryParse(p, out var v) ? v : -1).ToArray();
            return values.Length == 6 && values.All(v => v >= 0) ? values : null;
        }
        NatureStatPreview? preview = null;
        try
        {
            preview = NaturePicker.Service?.PreviewSlot(session, _viewModel.BoxIndex, slot, new StatPreviewOverrides(
                Int(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))),
                Int(GetVmString(nameof(BoxBrowserViewModel.EditLevel))),
                Six(GetVmString(nameof(BoxBrowserViewModel.EditIvs))),
                Six(GetVmString(nameof(BoxBrowserViewModel.EditEvs)))));
        }
        catch (ArgumentException) { } // a stale slot: labels still help
        var picked = await NaturePicker.ShowAsync(_hostGrid, data.NatureNames, Int(GetVmString(nameof(BoxBrowserViewModel.EditNature))), preview);
        if (picked is not null)
            SetVmString(nameof(BoxBrowserViewModel.EditNature), picked.Id.ToString());
    }

    private async Task OpenStatsEditorAsync(string caption, string vmProperty, Func<int> max)
    {
        var current = (GetVmString(vmProperty) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        if (current.Length != 6) current = new int[6];
        var updated = await StatsPopup.ShowAsync(_hostGrid, caption, current, Math.Max(1, max()),
            TrainingPreview(vmProperty == nameof(BoxBrowserViewModel.EditIvs)));
        if (updated is not null)
            SetVmString(vmProperty, string.Join(' ', updated));
    }

    private static int? ParseInt(string? text) => int.TryParse(text, out var v) ? v : null;

    private static IReadOnlyList<int>? ParseSix(string? text)
    {
        var values = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var v) ? v : -1).ToArray();
        return values.Length == 6 && values.All(v => v >= 0) ? values : null;
    }

    /// <summary>The editor's unsaved species/level/IVs/EVs: previews compute what SAVE would write.</summary>
    private StatPreviewOverrides PendingOverrides() => new(
        ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))),
        ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditLevel))),
        ParseSix(GetVmString(nameof(BoxBrowserViewModel.EditIvs))),
        ParseSix(GetVmString(nameof(BoxBrowserViewModel.EditEvs))));

    /// <summary>The pending species (and its form: kept only while the species is unchanged).</summary>
    private (int Species, int Form) PendingSpeciesForm()
    {
        var detail = _viewModel.Selected;
        var species = ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))) ?? detail?.Species ?? 0;
        return (species, detail is not null && detail.Species == species ? detail.Form : 0);
    }

    /// <summary>
    /// Live preview for the IV/EV window: the six stats with the edited spread against the
    /// editor's pending ones, the EV total against the format's cap, and for IVs the
    /// Hidden Power type the spread gives (with a chooser that re-rolls the low bits).
    /// </summary>
    private StatsPopup.StatsPreview? TrainingPreview(bool ivs)
    {
        var session = _sessionsFor();
        var slot = _viewModel.SelectedSlot;
        var box = _viewModel.BoxIndex;
        if (session is null || slot < 0) return null;
        var stats = NaturePicker.Service;
        var info = InfoPickers.Info;
        var pending = PendingOverrides();
        (IReadOnlyList<int>, IReadOnlyList<int>)? Stats(int[] values)
        {
            if (stats is null) return null;
            try
            {
                var before = stats.PreviewSlot(session, box, slot, pending);
                var after = stats.PreviewSlot(session, box, slot, ivs ? pending with { IVs = values } : pending with { EVs = values });
                return before is null || after is null ? null : (before.Current, after.Current);
            }
            catch (ArgumentException) { return null; }
        }
        var cap = ivs ? null : TrainingBudget.TotalCap(session.Generation, session.GetTrainingCaps());
        Func<int[], int?>? hiddenPower = ivs && info is not null ? values => info.GetHiddenPowerType(session, values) : null;
        Func<int[], int, IReadOnlyList<int>?>? ivsFor = ivs && info is not null ? (values, type) => info.GetIVsForHiddenPower(session, values, type) : null;
        return new StatsPopup.StatsPreview(Stats, cap, hiddenPower, ivsFor);
    }

    /// <summary>
    /// Under the LEVEL row: EXP at the typed level, EXP to the next, the stat change
    /// against the stored level, and a warning when the level falls below the met level
    /// (which no real Pokémon can be). Read-only; SAVE applies the level as before.
    /// </summary>
    private View LevelInfoCard()
    {
        var exp = InfoKit.DetailLine();
        exp.TextColor = UiTokens.Ink1;
        var warn = InfoKit.Note(tone: InfoKit.NoteTone.Bad);
        var grid = new InfoKit.StatDeltaGrid { IsVisible = false };
        var card = InfoKit.Card(exp, grid, warn);
        card.IsVisible = false;

        void Refresh()
        {
            var session = _sessionsFor();
            var slot = _viewModel.SelectedSlot;
            var info = InfoPickers.Info;
            var selected = _viewModel.Selected;
            if (session is null || info is null || slot < 0 || selected is null || selected.IsEmpty
                || ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditLevel))) is not { } level)
            {
                card.IsVisible = false;
                return;
            }
            LevelInfo? facts;
            try { facts = info.GetLevelInfo(session, _viewModel.BoxIndex, slot, level, PendingOverrides() with { Level = null }); }
            catch (ArgumentException) { facts = null; }
            if (facts is null) { card.IsVisible = false; return; }
            card.IsVisible = true;
            exp.Text = facts.Level >= 100
                ? $"EXP {facts.ExpAtLevel:N0} · max level"
                : $"EXP {facts.ExpAtLevel:N0} at Lv {facts.Level} · {facts.ExpToNext:N0} to Lv {facts.Level + 1}";
            exp.IsVisible = true;
            var changed = facts.Level != selected.Level && facts.StatsNow.Count == 6 && facts.StatsAtLevel.Count == 6;
            grid.IsVisible = changed;
            if (changed) grid.Show(facts.StatsNow, facts.StatsAtLevel);
            warn.Text = facts.BelowMetLevel ? $"Below its met level ({facts.MetLevel}): this would be illegal." : null;
            warn.IsVisible = facts.BelowMetLevel;
        }

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.EditLevel) or nameof(BoxBrowserViewModel.Selected)
                or nameof(BoxBrowserViewModel.EditSpecies) or nameof(BoxBrowserViewModel.EditIvs) or nameof(BoxBrowserViewModel.EditEvs))
                Refresh();
        };
        Refresh();
        return card;
    }

    /// <summary>The move rows: legal moves first with how they are learned, live details below.</summary>
    private async Task OpenMovePickerAsync(IGameDataService data, string caption, string vmProperty)
    {
        var (species, form) = PendingSpeciesForm();
        var picked = await InfoPickers.ShowMovesAsync(_hostGrid, caption, data, _sessionsFor(), _viewModel.BoxIndex,
            _viewModel.SelectedSlot, ParseInt(GetVmString(vmProperty)), species > 0 ? species : null, form);
        if (picked is not null)
            SetVmString(vmProperty, picked.Id.ToString());
    }

    /// <summary>The held-item row: holdable items first with their effects, sprites where cached.</summary>
    private async Task OpenHeldItemPickerAsync(IGameDataService data)
    {
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        var placeholder = ItemArt.PlaceholderPath();
        string? Icon(int id)
        {
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(data.ItemNames[id]) + ".png");
            return File.Exists(cached) ? cached : placeholder;
        }
        var picked = await InfoPickers.ShowHeldItemsAsync(_hostGrid, "HELD ITEM", data, _sessionsFor(),
            ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditHeldItem))), Icon);
        if (picked is not null)
            SetVmString(nameof(BoxBrowserViewModel.EditHeldItem), picked.Id.ToString());
    }

    private async Task RunSubEditorAsync(Func<Grid, Domain.ISaveEngineSession, int, int, Task<bool>> editor, string message)
    {
        // Met/origin, move details, move shop, potential, cosmetics, awards and the
        // ribbon album all mutate the live session as they go - before the write funnel
        // ever sees them - so Hardcore mode stops them at this one door.
        if (Denied(SaveAction.EditMon)) return;
        var slot = _viewModel.SelectedSlot;
        var session = _sessionsFor();
        if (slot < 0 || session is null) return;
        var changed = await editor(_hostGrid, session, _viewModel.BoxIndex, slot);
        if (changed)
            await _viewModel.RunMutationAsync(_ => new GenerationOutcome(true, message), slot);
    }

    /// <summary>A Kit.Field wrapped in the striped attribute-row plate.</summary>
    private View FieldRow(string caption, string bindingPath, bool shaded)
    {
        var entry = new Entry
        {
            FontSize = 13,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Ink0,
            BackgroundColor = Colors.Transparent,
            HeightRequest = 34,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        entry.SetBinding(Entry.TextProperty, bindingPath);
        return RowChrome(caption, entry, shaded);
    }

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

    private Services.ProtectionStore Protection =>
        IPlatformApplication.Current!.Services.GetRequiredService<Services.ProtectionStore>();

    private string? DocumentId =>
        IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;

    /// <summary>Item pick list with sprites for everything already in the icon cache (misses warm in the background).</summary>
    private static List<PickItem> ItemsWithIcons(IReadOnlyList<string> names)
    {
        var items = new List<PickItem>(names.Count) { new(0, "(none)") };
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(names[id]) + ".png");
            items.Add(new PickItem(id, names[id], File.Exists(cached) ? cached : ItemArt.PlaceholderPath()));
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
    private View NamedPicker(string caption, string vmProperty, IReadOnlyList<string> names, Func<List<PickItem>>? itemsFactory, bool openPokedex = false, bool shaded = false,
        Func<Task>? open = null)
    {
        var value = Kit.BlueprintValue(13);
        value.SetBinding(Label.TextProperty, new Binding(vmProperty, converter: new IdNameConverter(names)));

        var chevron = new Label
        {
            Text = ">", FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Blueprint,
            FontSize = 13, VerticalTextAlignment = TextAlignment.Center,
        };
        var chip = RowChrome(caption, value, shaded, chevron);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (open is not null) { await open(); return; }
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

    /// <summary>One shared row chrome for the side panel: fixed caption column, aligned values.</summary>
    private View RowChrome(string caption, View content, bool shaded, View? trailing = null)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new(new GridLength(52)), new(GridLength.Star), new(GridLength.Auto)],
            Children =
            {
                new Label
                {
                    Text = caption, FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1.5,
                    TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.NoWrap,
                },
                content,
            },
        };
        Grid.SetColumn(content, 1);
        if (trailing is not null)
        {
            grid.Children.Add(trailing);
            Grid.SetColumn(trailing, 2);
        }
        return new Border
        {
            BackgroundColor = shaded ? UiTokens.PaperShade : UiTokens.Paper,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1.2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(10, 6),
            Content = grid,
        };
    }

    /// <summary>Read-only computed stats: six labeled cells (HP/ATK/DEF/SPA/SPD/SPE) in two rows.</summary>
    private View StatsRow(string caption, string vmProperty, bool shaded)
    {
        string[] labels = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];
        var grid = new Grid
        {
            RowSpacing = 4,
            ColumnSpacing = 14,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
        };
        var converter = new StatCellConverter();
        for (var i = 0; i < 6; i++)
        {
            // Tight fixed columns: icon 14, label 28, value 34 - compact cells that never
            // clip 3-digit values and read as "HP 384", not "HP .... 384".
            var cell = new Grid
            {
                ColumnSpacing = 4,
                ColumnDefinitions = [new(new GridLength(14)), new(new GridLength(28)), new(new GridLength(34))],
                Children =
                {
                    StatBadge((byte)i),
                    new Label
                    {
                        Text = labels[i], FontFamily = DsChrome.PixelFont, FontSize = 12, FontAttributes = FontAttributes.Bold,
                        TextColor = StatColor(i).WithLuminosity(0.28f), VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            };
            var value = Kit.BlueprintValue(12);
            value.HorizontalTextAlignment = TextAlignment.Start;
            value.SetBinding(Label.TextProperty, new Binding(vmProperty, converter: converter, converterParameter: i.ToString()));
            cell.Children.Add(value);
            cell.SetColumn(cell.Children[0], 0);
            cell.SetColumn(cell.Children[1], 1);
            cell.SetColumn(value, 2);
            grid.Add(cell);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
        }
        return RowChrome(caption, grid, shaded);
    }

    /// <summary>Stat identity colors, muted for a light panel (HP red, ATK orange, DEF blue, SPA violet, SPD green, SPE gold).</summary>
    private static Color StatColor(int stat) => stat switch
    {
        0 => Color.FromArgb("#C64B4B"),
        1 => Color.FromArgb("#C98A3D"),
        2 => Color.FromArgb("#4E7FB8"),
        3 => Color.FromArgb("#8A6BB8"),
        4 => Color.FromArgb("#5D9B62"),
        _ => Color.FromArgb("#B8A03E"),
    };

    /// <summary>A 16px drawn pixel badge per stat: heart, sword, shield, spark, leaf, wing.</summary>
    private static SKCanvasView StatBadge(byte stat)
    {
        var view = new SKCanvasView { WidthRequest = 14, HeightRequest = 14, InputTransparent = true, VerticalOptions = LayoutOptions.Center };
        var color = StatColor(stat).ToSKColor();
        view.PaintSurface += (_, args) =>
        {
            var c = args.Surface.Canvas;
            c.Clear(SKColors.Transparent);
            using var p = new SKPaint { Color = color, IsAntialias = false };
            var w = args.Info.Width / 16f;
            void Px(int x, int y) => c.DrawRect(x * w, y * w, w + 0.5f, w + 0.5f, p);
            switch (stat)
            {
                case 0: // heart
                    foreach (var (x, y) in new[] { (4,3),(5,3),(10,3),(11,3),(3,4),(6,4),(9,4),(12,4),(3,5),(6,5),(9,5),(12,5),(4,6),(11,6),(5,7),(10,7),(6,8),(9,8),(7,9),(8,9),(7,4),(8,4),(7,5),(8,5) }) Px(x, y);
                    break;
                case 1: // sword (diagonal)
                    foreach (var (x, y) in new[] { (10,3),(11,3),(11,4),(9,5),(10,5),(8,6),(9,6),(7,7),(8,7),(6,8),(7,8),(5,9),(6,9),(4,10),(5,10),(3,11),(4,11),(6,5),(5,6),(9,3) }) Px(x, y);
                    break;
                case 2: // shield
                    foreach (var (x, y) in new[] { (4,3),(5,3),(6,3),(7,3),(8,3),(9,3),(10,3),(11,3),(4,4),(11,4),(4,5),(11,5),(4,6),(11,6),(5,7),(10,7),(6,8),(9,8),(7,9),(8,9) }) Px(x, y);
                    break;
                case 3: // spark
                    foreach (var (x, y) in new[] { (7,2),(6,4),(8,4),(5,6),(7,6),(9,6),(7,7),(6,8),(8,8),(4,7),(10,7),(7,10),(7,3),(7,9) }) Px(x, y);
                    break;
                case 4: // leaf
                    foreach (var (x, y) in new[] { (8,3),(9,3),(7,4),(10,4),(6,5),(10,5),(6,6),(9,6),(5,7),(8,7),(6,8),(7,8),(5,9),(6,9),(4,10),(5,10) }) Px(x, y);
                    break;
                default: // wing / speed streak
                    foreach (var (x, y) in new[] { (3,4),(4,4),(5,4),(6,4),(5,5),(7,5),(6,6),(8,6),(7,7),(9,7),(8,8),(10,8),(9,9),(11,9),(4,7),(5,8),(3,6) }) Px(x, y);
                    break;
            }
        };
        return view;
    }

    /// <summary>Picks one stat out of the space-separated EditStats string by index.</summary>
    private sealed class StatCellConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not string text || parameter is not string indexText || !int.TryParse(indexText, out var index))
                return "-";
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return index < parts.Length ? parts[index] : "-";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Read-only stat row with an explicit EDIT button for manual (expert) input.</summary>
    private View StatsField(string caption, string vmProperty, Func<int> max, bool shaded = false)
    {
        var value = Kit.BlueprintValue(12);
        value.SetBinding(Label.TextProperty, vmProperty);

        var edit = Kit.Capsule("EDIT", UiTokens.Blue);
        edit.FontSize = 10;
        edit.Padding = new Thickness(10, 4);
        edit.Clicked += async (_, _) => await OpenStatsEditorAsync(caption, vmProperty, max);

        var row = RowChrome(caption, value, shaded, edit);
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
            // The party pseudo-box renders as the navy deck, not the grid. The selected
            // card breathes like the games' party cursor.
            var phase = (float)((Environment.TickCount64 - _partyPulseStart) / 1000.0 * Math.PI * 2 / 1.4);
            PartyView.Paint(args.Surface.Canvas, args.Info, _sprites, _sessionsFor(), _viewModel.SelectedSlot, _frame.Request, _viewModel.CarrySource, phase);
            EnsurePartyPulse();
            return;
        }
        StopPartyPulse();
        BoxGridRenderer.Paint(args.Surface.Canvas, args.Info, _viewModel, _sprites, _theme, _frame.Request, _lockedSlots);
    }

    /// <summary>Locked-mon badges for the current box; refreshed on box/mutation changes, never per frame.</summary>
    private void RefreshLockedSlots()
    {
        _lockedSlots.Clear();
        var docId = DocumentId;
        var session = _sessionsFor();
        if (docId is null || session is null || _viewModel.BoxIndex < 0) return;
        foreach (var summary in _viewModel.VisibleSlots)
        {
            if (summary.Species is null) continue;
            var pid = session.GetRngInfo(summary.Box, summary.Slot).Pid;
            if (Protection.IsMonLocked(docId, summary.Box, summary.Slot, pid))
                _lockedSlots.Add(summary.Slot);
        }
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
            if (_viewModel.BoxIndex == -1 && slot != _viewModel.CarrySource.Value.Slot)
            {
                // Aim with the first tap (preview), confirm with the second on the same slot.
                if (wasSelected) _ = DropAndRepaintAsync();
                else _canvas.InvalidateSurface();
            }
            else
            {
                _ = DropAndRepaintAsync();
            }
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
        // Every way in this sheet fabricates a Pokémon, so Hardcore mode refuses the
        // whole sheet - with the reason on screen instead of an empty menu.
        if (await DeniedAsync(SaveAction.CreateMon)) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, $"ADD A POKéMON - SLOT {slot + 1}", null,
            new PadOption("Create a Pokémon", IconPath: "create"),
            new PadOption("Paste a Showdown set", IconPath: "script"),
            new PadOption("Import .pk file", IconPath: "import"),
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
                        : new GenerationOutcome(false, "That file is not a recognizable Pokémon."), slot, action: SaveAction.CreateMon);
                _canvas.InvalidateSurface();
                return;
            }
            case "From event database":
            {
                var session = _sessionsFor();
                if (session is null) return;
                Services.EventArchive.EnsureLoaded(session.Generation);
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
        if (Denied(SaveAction.CreateMon)) return;
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
        if (Denied(SaveAction.CreateMon)) return;
        var text = await TextPopup.ShowAsync(_hostGrid, "PASTE A SHOWDOWN SET",
            "Paste the set exactly as exported from Pokémon Showdown.");
        if (string.IsNullOrWhiteSpace(text)) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "READING THE SET…",
            "The offline legalizer is building a legal Pokémon from your set.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, text, Services.HaXMode.IsOn), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }
}
