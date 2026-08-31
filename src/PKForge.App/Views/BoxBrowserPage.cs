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
        _viewModel.Status = "READY";
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
        var choice = await PadMenu.ShowAsync(_hostGrid, "STORAGE TOOLS", null,
            new PadOption("Organizer (multi-select)", IconPath: "storage"),
            new PadOption("Import .pk files", IconPath: "folder"),
            new PadOption("Import Showdown team", IconPath: "script"),
            new PadOption("Export box to Showdown", IconPath: "script"),
            new PadOption("Generate Living Dex", IconPath: "pokedex"),
            new PadOption("Egg factory…", IconPath: "pokedex"),
            new PadOption("Day Care / Nursery", IconPath: "pokedex"),
            new PadOption("Batch editor", IconPath: "script"),
            new PadOption("Presets…", IconPath: "gears"),
            new PadOption("Trainer profiles…", IconPath: "trainer"),
            new PadOption("Nuzlocke report", IconPath: "skull"),
            new PadOption("Manage boxes…", IconPath: "storage"),
            new PadOption("Sort boxes…", IconPath: "restore"));
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
            case "Egg factory…":
                await ShowEggFactoryAsync();
                return;
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
            case "Presets…":
                await ShowPresetsMenuAsync();
                return;
            case "Trainer profiles…":
                await ShowTrainerProfilesAsync();
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
        _viewModel.Status = "READY";
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
            "No selection means the current box. Every write creates a restore point.",
            new PadOption("Select all boxes", IconPath: "storage"),
            new PadOption("Lock / Unlock box(es)", IconPath: "padlock"),
            new PadOption("Copy box(es)…", IconPath: "storage"),
            new PadOption("Delete box(es) (rescue Pokémon)", IconPath: "release"),
            new PadOption(emptyLabel, IconPath: "release"),
            new PadOption("Clear selection", IconPath: "hex"));
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
        var choice = await PadMenu.ShowAsync(_hostGrid, $"ORGANIZER · {_viewModel.MarkedCount} MARKED", null,
            new PadOption("Move selection to box…", IconPath: "storage"),
            new PadOption("Move selection to another game…", IconPath: "storage"),
            new PadOption("Copy selection to another game…", IconPath: "storage"),
            new PadOption("Duplicate selection", IconPath: "storage"),
            new PadOption("Move selection to Bank", IconPath: "bank"),
            new PadOption("Export selection (.pk files)", IconPath: "folder"),
            new PadOption("Release selection", IconPath: "release"),
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
            }, Math.Max(0, _viewModel.SelectedSlot));
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
            new PadOption("Alphabetical", IconPath: "script"),
            new PadOption("Level (strongest first)", IconPath: "sword"),
            new PadOption("IV total (best first)", IconPath: "spark"),
            new PadOption("Type", IconPath: "leaf"),
            new PadOption("Age (oldest first)", IconPath: "restore"),
            new PadOption("Shiny first", IconPath: "spark"));
        if (choice is null) return;

        var scope = await PadMenu.ShowAsync(_hostGrid, "SORT", "Which boxes?",
            new PadOption("This box", IconPath: "storage"),
            new PadOption("All boxes", IconPath: "storage"));
        if (scope is null) return;

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

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "SORT NOW?",
            scope == "This box"
                ? "This box's Pokémon are reordered and compacted to the top."
                : "Every box's Pokémon are pooled, ordered, and compacted from box 1. Empties gather at the end.",
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
            var placed = session.SortBoxes(criteria, boxes);
            return new GenerationOutcome(true, $"Sorted {placed} Pokémon.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
        if (sorted)
            _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>One-tap preset packs: competitive, speedrun, casual - all through the batch editor.</summary>
    private async Task ShowPresetsMenuAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var perfectLabel = caps.IvMax == 15 ? "6DV (perfect DVs)" : "6IV (perfect IVs)";
        var scope = await PadMenu.ShowAsync(_hostGrid, "PRESETS", "Apply to which boxes?",
            new PadOption("This box", IconPath: "storage"),
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
            new PadOption("Level 50 flat", IconPath: "sword"),
            new PadOption("Level 100", IconPath: "sword"),
            new PadOption(perfectLabel, IconPath: "spark"),
            new PadOption("0 Attack IV (special)", IconPath: "spark"),
            new PadOption("0 Speed IV (Trick Room)", IconPath: "spark"),
            new PadOption("Reset EVs", IconPath: "restore"),
            new PadOption("Max friendship", IconPath: "heart"),
            new PadOption("Hyper Train everything", IconPath: "gears"),
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
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
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
        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        stack.Children.Add(reroll);
        stack.Children.Add(close);

        var done = new TaskCompletionSource();
        var overlay = Kit.AttachOverlay(_hostGrid, window, () => done.TrySetResult());
        reroll.Clicked += async (_, _) =>
        {
            var names = data.NatureNames.Where((_, i) => i > 0 && i <= 25).ToList();
            var picked = await PadMenu.ShowAsync(overlay, "PICK A NATURE", null, names.Select(n => new PadOption(n)).ToArray());
            if (picked is null) return;
            var nature = names.IndexOf(picked);
            if (nature < 0) return;
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

    /// <summary>Mass egg generation: living egg dex, or one species filling this box.</summary>
    private async Task ShowEggFactoryAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        var choice = await PadMenu.ShowAsync(_hostGrid, "EGG FACTORY", "Every egg fills an empty PC slot. One backed-up write.",
            new PadOption("One egg of every species", IconPath: "pokedex"),
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

    /// <summary>
    /// The batch editor: instructions like "Level=100", "IV_HP=31", "Shiny=Yes" applied to
    /// every mon in the current box (or all boxes), one safe write. PKHeX syntax.
    /// </summary>
    private async Task RunBatchEditorAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var scope = await PadMenu.ShowAsync(_hostGrid, "BATCH EDITOR", "Apply to which boxes?",
            new PadOption("This box", IconPath: "storage"),
            new PadOption("All boxes", IconPath: "storage"));
        if (scope is null) return;

        var text = await TextPopup.ShowAsync(_hostGrid, "INSTRUCTIONS",
            $"One per line, PKHeX style:\nLevel=100\nIV_HP={caps.IvMax}\nShiny=Yes\nEV_ATK={caps.EvMax}");
        if (string.IsNullOrWhiteSpace(text)) return;

        var instructions = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (instructions.Length == 0) return;

        IReadOnlyList<int>? boxes = scope == "This box" ? [_viewModel.BoxIndex] : null;
        var preview = scope == "This box" ? $"box {_viewModel.BoxIndex + 1}" : "every box";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "APPLY BATCH EDIT?",
            $"{instructions.Length} instruction(s) to every Pokémon in {preview}. Backed up first.", "Apply");
        if (!confirmed) return;

        await _viewModel.RunMutationAsync(session =>
        {
            var touched = session.BatchApply(instructions, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"Batch edit applied to {touched} Pokémon.")
                : new GenerationOutcome(false, "Nothing to edit in those boxes.");
        }, Math.Max(0, _viewModel.SelectedSlot));
        _canvas.InvalidateSurface();
    }

    private async Task ShowSaveDataAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var options = new List<PadOption>
        {
            new PadOption("Trainer card", IconPath: "trainer"),
            new PadOption("Bag & items", IconPath: "bag"),
            new PadOption("Pokédex", IconPath: "pokedex"),
            new PadOption("Fashion", IconPath: "trainer"),
            new PadOption("Trainer records", IconPath: "trainer"),
            new PadOption("Wonder cards", IconPath: "events"),
            new PadOption("Restore points", IconPath: "credits"),
        };
        if (session.GetGrandUndergroundItems().Count != 0)
            options.Insert(2, new PadOption("Grand Underground", IconPath: "bag"));
        if (session.SupportsCompassSettings)
            options.Insert(2, new PadOption("Compass settings", IconPath: "settings"));
        var choice = await PadMenu.ShowAsync(_hostGrid, "SAVE DATA", null, options.ToArray());
        switch (choice)
        {
            case "Trainer card": await ShowTrainerCardAsync(); return;
            case "Bag & items": await ShowBagAsync(); return;
            case "Grand Underground": await GrandUndergroundEditor.ShowAsync(_hostGrid, session, _viewModel); return;
            case "Compass settings": await ShowCompassSettingsAsync(session); return;
            case "Pokédex": await ShowDexMenuAsync(); return;
            case "Fashion": await ShowFashionAsync(); return;
            case "Trainer records": await ShowTrainerRecordsAsync(); return;
            case "Wonder cards":
            {
                var wonderChoice = await PadMenu.ShowAsync(_hostGrid, "WONDER CARDS", null,
                    new PadOption("Event gallery", IconPath: "events"),
                    new PadOption("In-save inbox", IconPath: "events"));
                if (wonderChoice == "In-save inbox")
                {
                    await MysteryGiftInboxEditor.ShowAsync(_hostGrid, session);
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

    /// <summary>
    /// Pokemon Compass romhack settings: the confirmed QoL toggles (exp share, level
    /// cap, spawn rate...). Each change is one backed-up write through the normal path.
    /// </summary>
    private async Task ShowCompassSettingsAsync(Domain.ISaveEngineSession session)
    {
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
                .Append(new PadOption("Close", IconPath: "quit"))
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
                changeDescription: $"Compass: {setting.Name} -> {label}");
            if (ok)
                _viewModel.Status = $"COMPASS: {setting.Name.ToUpperInvariant()} = {label.ToUpperInvariant()}";
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

    private async Task ShowFashionAsync()
    {
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
        }, Math.Max(0, _viewModel.SelectedSlot));
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
            var options = new List<PadOption>
            {
                new("Save current trainer as profile", IconPath: "trainer"),
            };
            if (_viewModel.SelectedSlot >= 0 && profiles.Count > 0)
                options.Add(new("Apply profile to selected Pokémon", IconPath: "editor"));
            if (profiles.Count > 0)
                options.Add(new("Delete a profile", IconPath: "restore"));
            options.Add(new(store.UseCurrentTrainerForGeneration
                ? "Generated Pokémon obey trainer: ON"
                : "Generated Pokémon obey trainer: OFF", IconPath: "gears"));

            var choice = await PadMenu.ShowAsync(_hostGrid, "TRAINER PROFILES",
                profiles.Count == 0 ? "No named profiles yet." : string.Join('\n', profiles.Select(ProfileSummary)),
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
            await _viewModel.RunMutationAsync(s => s.MakeMine(_viewModel.BoxIndex, slot, profile), slot);
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
                new PadOption("Poké Beans", Accent: UiTokens.GiftRed));
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
            var icons = new List<Image>(pouch.Items.Count);
            foreach (var item in pouch.Items)
            {
                var captured = item;
                var shownCount = _pendingCounts.GetValueOrDefault((pouch.Name, item.Id), item.Count);
                var icon = new Image { WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
                icons.Add(icon);
                var row = new BagRow(icon, ItemName(item.Id), shownCount)
                {
                    Tapped = () => _ = EditCountAsync(captured),
                    Minus = () => QueueNudge(captured, -1),
                    Plus = () => QueueNudge(captured, +1),
                };
                _itemRows.Add(row);
                _rows.Children.Add(row);
            }

            var add = Kit.Capsule("+  ADD ITEM", UiTokens.BagCyan);
            add.FontSize = 14;
            add.Margin = new Thickness(4, 6, 4, 2);
            add.Clicked += (_, _) => _ = AddItemAsync();
            _addRow = add;
            _rows.Children.Add(_addRow);

            var presets = Kit.Capsule("ITEM PRESETS", UiTokens.BagCyan);
            presets.FontSize = 14;
            presets.Margin = new Thickness(4, 2, 4, 2);
            presets.Clicked += (_, _) => _ = ShowPresetsAsync();
            _presetRow = presets;
            _rows.Children.Add(_presetRow);

            Highlight(_cursor);
            _ = LoadIconsAsync(pouch.Items, icons);
            Report($"{pouch.Name.ToUpperInvariant()} - {pouch.Items.Count} KINDS - NAME TABLE {_itemNames.Count}");
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
                }, _slotSeed, refreshSlot: false);
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
            }, _slotSeed, refreshSlot: false);
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
            await FlushPendingAsync();
            var choice = await PadMenu.ShowAsync(_host, "ITEM PRESETS", "Only items legal in this game are changed.",
                new PadOption("Refill this pouch to 99", IconPath: "bag"),
                new PadOption("Give every Poké Ball x50", IconPath: "bag"),
                new PadOption("Healing supplies x20", IconPath: "restore"),
                new PadOption("Nuzlocke starter supplies", IconPath: "leaf"),
                new PadOption("Remove every item in this pouch", IconPath: "release"));
            if (choice is null) return;

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
            }, _slotSeed, refreshSlot: false);
            Report(ok ? $"PRESET APPLIED - {changes.Count} ITEMS" : "PRESET FAILED - SEE STATUS");
            Rebuild();
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

        // Party A is the games' summary flow: open the mon's actions (Edit leads into the
        // stats editor). The PC-hand grab belongs to the boxes; party members move through
        // the menu's Move entry so A never silently starts dragging a team member.
        if (_viewModel.BoxIndex == -1)
        {
            var slots = _viewModel.VisibleSlots;
            if (slot < slots.Count && slots[slot].Species is not null)
            {
                _ = ShowMonActionsAsync(slot);
                return true;
            }
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
            new PadOption("Edit", IconPath: "editor"),
            new PadOption("Move", IconPath: "storage"),
            new PadOption("Duplicate", IconPath: "storage"),
            new PadOption("Send to Bank", IconPath: "bank"),
            new PadOption("Copy to Bank", IconPath: "bank"),
            new PadOption("Send to another game…", IconPath: "storage"),
            new PadOption("Copy to another game…", IconPath: "storage"),
            new PadOption("Export .pk file", IconPath: "folder"),
            new PadOption("Show as Showdown set", IconPath: "script"),
            new PadOption("Show as QR code", IconPath: "search"),
            new PadOption("RNG / IVs", IconPath: "dice"),
            new PadOption("Lock / Unlock release", IconPath: "padlock"),
            new PadOption("Release", IconPath: "release"));
        switch (choice)
        {
            case "Edit":
                EnterEditorFocusMode();
                return;
            case "Move":
                if (_viewModel.BeginCarry()) _canvas.InvalidateSurface();
                return;
            case "Duplicate":
                await DuplicateSlotAsync(slot);
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
            case "Export .pk file":
                await ExportSlotAsync(slot);
                return;
            case "Show as Showdown set":
                await ShowShowdownAsync(slot);
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
    private async Task SendSlotToAnotherGameAsync(int slot, string nickname, bool copyInsteadOfMove = false)
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

        var verb = copyInsteadOfMove ? "Copy" : "Move";
        var confirm = await PadMenu.ConfirmAsync(_hostGrid, $"{verb.ToUpperInvariant()} TO ANOTHER GAME?",
            copyInsteadOfMove
                ? $"A copy of {nickname} joins {target.GameLabel}; the original stays here."
                : $"{nickname} will leave this box and join {target.GameLabel} (box space permitting).", verb);
        if (!confirm) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        var outcome = await transfer.SendToGameAsync(export.Data, nickname, target);
        _viewModel.Status = outcome.Message;
        if (!outcome.Success || copyInsteadOfMove) return;

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

    /// <summary>
    /// Deposit: a copy duplicates into the vault and leaves the game untouched; a move
    /// additionally empties the game slot (one safe write, one restore point).
    /// </summary>
    private async Task SendToBankAsync(int slot, string nickname, bool copyInsteadOfMove = false)
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
        }, slot, changeDescription: $"Deposit {nickname} in the Bank ({(_viewModel.BoxIndex == -1 ? $"Party {slot + 1}" : $"Box {_viewModel.BoxIndex + 1}, Slot {slot + 1}")})");
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
            case "About PKForge": await AboutPopup.ShowAsync(_hostGrid); break;
        }
    }

    /// <summary>The Thor's second screen mirrors the box automatically while this page is open.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
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

        List<PickItem> AbilityItems()
        {
            var detail = _viewModel.Selected;
            var session = _sessionsFor();
            if (Services.HaXMode.IsOn) return AllItems(data.AbilityNames, includeZero: true);
            if (detail is null || session is null) return [];
            return session.GetAbilityChoices(detail.Species, detail.Form)
                .Select(id => new PickItem(id, id < data.AbilityNames.Count ? data.AbilityNames[id] : $"#{id}"))
                .ToList();
        }
        List<PickItem> MoveItems() => AllItems(data.MoveNames, includeZero: true, zeroLabel: "(none)");

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
        var nature = FocusBorder(NamedPicker("NATURE", nameof(BoxBrowserViewModel.EditNature), data.NatureNames,
            () => AllItems(data.NatureNames, includeZero: true), shaded: true), "NATURE", async () => await OpenNamedPickerAsync("NATURE", nameof(BoxBrowserViewModel.EditNature), () => AllItems(data.NatureNames, includeZero: true)));
        var ability = FocusBorder(NamedPicker("ABILITY", nameof(BoxBrowserViewModel.EditAbility), data.AbilityNames,
            AbilityItems, shaded: false), "ABILITY", async () => await OpenNamedPickerAsync("ABILITY", nameof(BoxBrowserViewModel.EditAbility), AbilityItems));
        var item = FocusBorder(NamedPicker("HELD ITEM", nameof(BoxBrowserViewModel.EditHeldItem), data.ItemNames,
            () => ItemsWithIcons(data.ItemNames), shaded: true), "HELD ITEM", async () => await OpenNamedPickerAsync("HELD ITEM", nameof(BoxBrowserViewModel.EditHeldItem), () => ItemsWithIcons(data.ItemNames)));
        var move1 = FocusBorder(NamedPicker("MOVE 1", nameof(BoxBrowserViewModel.EditMove1), data.MoveNames, MoveItems, shaded: false), "MOVE 1", async () => await OpenNamedPickerAsync("MOVE 1", nameof(BoxBrowserViewModel.EditMove1), MoveItems));
        var move2 = FocusBorder(NamedPicker("MOVE 2", nameof(BoxBrowserViewModel.EditMove2), data.MoveNames, MoveItems, shaded: true), "MOVE 2", async () => await OpenNamedPickerAsync("MOVE 2", nameof(BoxBrowserViewModel.EditMove2), MoveItems));
        var move3 = FocusBorder(NamedPicker("MOVE 3", nameof(BoxBrowserViewModel.EditMove3), data.MoveNames, MoveItems, shaded: false), "MOVE 3", async () => await OpenNamedPickerAsync("MOVE 3", nameof(BoxBrowserViewModel.EditMove3), MoveItems));
        var move4 = FocusBorder(NamedPicker("MOVE 4", nameof(BoxBrowserViewModel.EditMove4), data.MoveNames, MoveItems, shaded: true), "MOVE 4", async () => await OpenNamedPickerAsync("MOVE 4", nameof(BoxBrowserViewModel.EditMove4), MoveItems));
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
                await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot);
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
                "COSMETICS" => target with { Neighbors = new EditorFocusNeighbors(potentialIndex, awardsIndex, exportIndex, index) },
                "AWARDS" => target with { Neighbors = new EditorFocusNeighbors(cosmeticsIndex, index, qrIndex, index) },
                _ => target,
            })
            .ToArray();

        var monActions = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var button in new[] { legalize, makeMine, showdown, exportPk, qr, met, moveDetails, moveShop, potential, cosmetics, awards })
        {
            button.FontSize = 11;
            button.Padding = new Thickness(10, 6);
            button.Margin = new Thickness(0, 0, 6, 6);
            monActions.Children.Add(button);
        }

        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                legality,
                species, nickname, level, nature, ability, item,
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
            new PadOption("Male", Accent: UiTokens.MenuBlue),
            new PadOption("Female", Accent: UiTokens.GiftRed),
            new PadOption("Genderless", Accent: UiTokens.Ink1));
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

    private async Task OpenStatsEditorAsync(string caption, string vmProperty, Func<int> max)
    {
        var current = (GetVmString(vmProperty) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        if (current.Length != 6) current = new int[6];
        var updated = await StatsPopup.ShowAsync(_hostGrid, caption, current, Math.Max(1, max()));
        if (updated is not null)
            SetVmString(vmProperty, string.Join(' ', updated));
    }

    private async Task RunSubEditorAsync(Func<Grid, Domain.ISaveEngineSession, int, int, Task<bool>> editor, string message)
    {
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
    private View NamedPicker(string caption, string vmProperty, IReadOnlyList<string> names, Func<List<PickItem>>? itemsFactory, bool openPokedex = false, bool shaded = false)
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
        edit.Clicked += async (_, _) =>
        {
            var current = (GetVmString(vmProperty) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var v) ? v : 0).ToArray();
            if (current.Length != 6) current = new int[6];
            var updated = await StatsPopup.ShowAsync(_hostGrid, caption, current, Math.Max(1, max()));
            if (updated is not null)
                SetVmString(vmProperty, string.Join(' ', updated));
        };

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
        if (_viewModel.BoxIndex == -1)
        {
            // Same rule as the pad: a party tap opens the mon's actions, never a grab.
            _ = ShowMonActionsAsync(slot);
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
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, text, Services.HaXMode.IsOn), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }
}
