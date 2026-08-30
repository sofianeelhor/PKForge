using Microsoft.Maui.Controls.Shapes;
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
/// The Bank: the app's own cross-game vault in the PKSM storage world. Each box page
/// is a saturated wallpaper flat with the dot lattice;
/// the second screen shows the highlighted mon. A grabs/places, START opens the
/// mon menu, L/R turn pages.
/// </summary>
public sealed class BankPage : ContentPage, IPadHandler
{
    private const int Columns = 6;
    private const int Rows = 5;

    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly BoxBrowserViewModel _boxViewModel; // for send-to-game and shared status
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly Grid _hostGrid;
    private Label _pageLabel = null!;

    private int _boxIndex;
    private int _selectedSlot;
    private Guid? _carryId;

    // The current box's slot->entry map, rebuilt only when the box or bank changes.
    private Dictionary<int, BankEntry> _boxEntries = new();

    public BankPage(IBankService bank, ISpriteService sprites, BoxBrowserViewModel boxViewModel)
    {
        _bank = bank;
        _sprites = sprites;
        _boxViewModel = boxViewModel;
        Title = "Bank";
        NavigationPage.SetHasNavigationBar(this, false);

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _frame = new FrameInvalidator(_canvas);
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        // The box info panel: a maroon header strip carrying the page readout,
        // flanked by the paging buttons and add-box.
        var previous = Kit.MiniCapsule("<", UiTokens.Ink0);
        previous.HeightRequest = 32;
        previous.Clicked += (_, _) => ChangeBox(-1);
        var next = Kit.MiniCapsule(">", UiTokens.Ink0);
        next.HeightRequest = 32;
        next.Clicked += (_, _) => ChangeBox(1);
        _pageLabel = (Label)((Border)Kit.HeaderBar("00 / 00")).Content!;
        _pageLabel.HorizontalTextAlignment = TextAlignment.Center;

        var addBox = Kit.Capsule("+ Box", UiTokens.Green);
        addBox.Clicked += (_, _) => { _bank.AddBox(); UpdatePageLabel(); _canvas.InvalidateSurface(); };

        var strip = new Grid
        {
            Padding = new Thickness(12, 6, 12, 0),
            ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { _pageLabel, previous, next, addBox },
        };
        Grid.SetColumn(previous, 1);
        Grid.SetColumn(next, 2);
        Grid.SetColumn(addBox, 3);

        var screen = Kit.LcdPanel(_canvas, padding: 4);
        var content = new Grid { Padding = new Thickness(12, 8, 12, 10), Children = { screen } };
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), content } };

        var footer = DsChrome.Footer(
            ("A", "Grab", null),
            ("B", "Back", () => _ = Navigation.PopAsync()),
            ("LR", "Box", null),
            ("+", "Menu", () => _ = OpenCursorMenuAsync()));

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip("Bank", "Open"), strip, bodyHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(strip, 2);
        Grid.SetRow(bodyHost, 3);
        Grid.SetRow(footer, 4);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;
        UpdatePageLabel();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable == true) { try { _ = host.ShowAsync(); } catch { } }
        RefreshBoxEntries();
        UpdatePreview();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.PreviewSpecies = null;
    }


    private void UpdatePageLabel() => _pageLabel.Text = $"{_boxIndex + 1:00} / {_bank.BoxCount:00}";

    private void ChangeBox(int delta)
    {
        _boxIndex = Math.Clamp(_boxIndex + delta, 0, _bank.BoxCount - 1);
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        _canvas.InvalidateSurface();
    }

    /// <summary>Rebuild the current box's slot map from the bank. Call after any mutation.</summary>
    private void RefreshBoxEntries()
    {
        var map = new Dictionary<int, BankEntry>();
        foreach (var entry in _bank.GetAll())
            if (entry.Box == _boxIndex) map[entry.Slot] = entry;
        _boxEntries = map;
    }

    private BankEntry? EntryAt(int slot) => _boxEntries.GetValueOrDefault(slot);

    private void UpdatePreview()
    {
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is null) return;
        state.PreviewSpecies = EntryAt(_selectedSlot)?.Info.Species;
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: return MoveCursor(0, -1);
            case PadButton.Down: return MoveCursor(0, 1);
            case PadButton.Left: return MoveCursor(-1, 0);
            case PadButton.Right: return MoveCursor(1, 0);
            case PadButton.L: ChangeBox(-1); return true;
            case PadButton.R: ChangeBox(1); return true;
            case PadButton.A: _ = ConfirmAsync(); return true;
            case PadButton.B:
                if (_carryId is not null) { _carryId = null; _canvas.InvalidateSurface(); return true; }
                _ = Navigation.PopAsync();
                return true;
            case PadButton.Start: _ = OpenCursorMenuAsync(); return true;
            default: return false;
        }
    }

    private bool MoveCursor(int dx, int dy)
    {
        var col = Math.Clamp(_selectedSlot % Columns + dx, 0, Columns - 1);
        var row = Math.Clamp(_selectedSlot / Columns + dy, 0, Rows - 1);
        _selectedSlot = row * Columns + col;
        UpdatePreview();
        _canvas.InvalidateSurface();
        return true;
    }

    private async Task ConfirmAsync()
    {
        if (_carryId is { } carrying)
        {
            _bank.Move(carrying, _boxIndex, _selectedSlot);
            _carryId = null;
            RefreshBoxEntries();
            _canvas.InvalidateSurface();
            return;
        }
        var entry = EntryAt(_selectedSlot);
        if (entry is not null)
        {
            _carryId = entry.Id;
            _canvas.InvalidateSurface();
            return;
        }
        await OpenEmptySlotMenuAsync();
    }

    private async Task OpenCursorMenuAsync()
    {
        RefreshBoxEntries(); // never decide the menu from a stale slot map
        var entry = EntryAt(_selectedSlot);
        if (entry is null)
        {
            await OpenEmptySlotMenuAsync();
            return;
        }

        var choice = await PadMenu.ShowAsync(_hostGrid, entry.Info.Nickname.ToUpperInvariant(),
            $"From {entry.Info.SourceName} · Gen {entry.Info.Generation} · deposited {entry.AddedUtc:yyyy-MM-dd}",
            new PadOption("Edit", IconPath: "editor"),
            new PadOption("Duplicate", IconPath: "storage"),
            new PadOption("Send to game…", IconPath: "storage"),
            new PadOption("Copy to game…", IconPath: "storage"),
            new PadOption("Move (carry)", IconPath: "storage"),
            new PadOption("Export .pk file", IconPath: "folder"),
            new PadOption("Release from bank", IconPath: "release"));
        switch (choice)
        {
            case "Edit":
                await EditEntryAsync(entry);
                return;
            case "Duplicate":
                var clone = _bank.Add(_bank.GetData(entry.Id), entry.Info);
                _boxIndex = clone.Box;
                _selectedSlot = clone.Slot;
                RefreshBoxEntries();
                UpdatePageLabel();
                UpdatePreview();
                _boxViewModel.Status = $"{entry.Info.Nickname} cloned in the bank.";
                _canvas.InvalidateSurface();
                return;
            case "Send to game…":
                await SendToGamePickerAsync(entry);
                return;
            case "Copy to game…":
                await SendToGamePickerAsync(entry, keepOriginal: true);
                return;
            case "Move (carry)":
                _carryId = entry.Id;
                _canvas.InvalidateSurface();
                return;
            case "Export .pk file":
                await ExportAsync(entry);
                return;
            case "Release from bank":
                var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "RELEASE FROM BANK?",
                    $"Release {entry.Info.Nickname}? Bank releases are permanent (the bank has no restore points yet).",
                    "Release");
                if (!confirmed) return;
                _bank.Remove(entry.Id);
                RefreshBoxEntries();
                UpdatePreview();
                _canvas.InvalidateSurface();
                return;
        }
    }

    /// <summary>Full field editor for a stored mon, written back in place on save.</summary>
    private async Task EditEntryAsync(BankEntry entry)
    {
        var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
        var saved = await BankEntryEditor.ShowAsync(_hostGrid, _bank, engine, entry);
        if (saved)
        {
            RefreshBoxEntries();
            UpdatePreview();
            _boxViewModel.Status = $"{EntryAt(_selectedSlot)?.Info.Nickname ?? "Pokémon"} updated in the bank.";
            _canvas.InvalidateSurface();
        }
    }

    private async Task OpenEmptySlotMenuAsync()
    {
        var session = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;
        var choice = await PadMenu.ShowAsync(_hostGrid, "ADD TO BANK", null,
            new PadOption("Create a Pokémon", IconPath: "editor"),
            new PadOption("Paste a Showdown set", IconPath: "script"),
            new PadOption("Import .pk file", IconPath: "folder"));
        switch (choice)
        {
            case "Create a Pokémon" or "Paste a Showdown set" when session is null:
            {
                // No game connected: pick the format, generate against a blank save with
                // a placeholder identity (OT "PKForge", editable afterwards in the editor).
                var eras = new[] { "Generation I", "Generation II", "Generation III", "Generation IV", "Generation V",
                    "Generation VI", "Generation VII", "Generation VIII", "Generation IX" };
                var era = await PadMenu.ShowAsync(_hostGrid, "CREATE FOR WHICH GAME ERA?",
                    "No save connected. The mon gets a placeholder identity in the format you pick; edit it after.",
                    eras);
                if (era is null) return;
                var generation = Array.IndexOf(eras, era) + 1;
                var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
                session = engine.OpenBlankSession(generation);
                if (choice == "Create a Pokémon") goto case "Create a Pokémon";
                goto case "Paste a Showdown set";
            }
            case "Create a Pokémon":
            {
                var services = IPlatformApplication.Current!.Services;
                var data = services.GetRequiredService<IGameDataService>();
                var request = await GenerateWizard.RunAsync(_hostGrid, data, session!);
                if (request is null) return;
                var legalizer = services.GetRequiredService<ILegalizerService>();
                var overlay = LoadingOverlay.Show(_hostGrid, "CREATING FOR THE BANK…", "The offline legalizer is at work.");
                try
                {
                    var generated = await Task.Run(() => legalizer.GenerateData(session!, request));
                    if (generated is null) { _boxViewModel.Status = "No legal combination found."; return; }
                    Deposit(generated);
                }
                finally { overlay.Close(); }
                return;
            }
            case "Paste a Showdown set":
            {
                var text = await TextPopup.ShowAsync(_hostGrid, "PASTE A SHOWDOWN SET", "The set becomes a legal mon stored in the bank.");
                if (string.IsNullOrWhiteSpace(text)) return;
                var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
                var overlay = LoadingOverlay.Show(_hostGrid, "READING THE SET…", "The offline legalizer is at work.");
                try
                {
                    var generated = await Task.Run(() => legalizer.GenerateDataFromShowdown(session!, text, Services.HaXMode.IsOn));
                    if (generated is null) { _boxViewModel.Status = "Could not build a legal mon from that set."; return; }
                    Deposit(generated);
                }
                finally { overlay.Close(); }
                return;
            }
            case "Import .pk file":
            {
                var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
                var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
                if (picker is null || access is null) return;
                var documents = await picker.PickManyAsync();
                var count = 0;
                var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
                foreach (var document in documents)
                {
                    var bytes = (await access.ReadAsync(document.DocumentId)).ToArray();
                    var parsed = engine.TryDescribeEntity(bytes, document.DisplayName);
                    if (parsed is null) continue;
                    _bank.Add(bytes, parsed);
                    count++;
                }
                _boxViewModel.Status = count > 0 ? $"Deposited {count} Pokémon into the bank." : "No recognizable Pokémon in those files.";
                RefreshBoxEntries();
                _canvas.InvalidateSurface();
                return;
            }
        }
    }

    private void Deposit(GeneratedEntity generated)
    {
        var entry = _bank.Add(generated.Data, generated.Info);
        _boxIndex = entry.Box;
        _selectedSlot = entry.Slot;
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        _boxViewModel.Status = $"{generated.Info.Nickname} deposited in the bank.";
        _canvas.InvalidateSurface();
    }

    /// <summary>Withdraw into ANY detected game: pick the destination, the transfer service
    /// converts the format, backs up, and writes. No need to connect the save first.</summary>
    private async Task SendToGamePickerAsync(BankEntry entry, bool keepOriginal = false)
    {
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<SavePickerViewModel>();
        var transfer = services?.GetService<Services.TransferService>();
        if (picker is null || transfer is null) return;

        var sessions = services?.GetService<ISaveSessionService>();
        var connected = sessions?.Current;
        var detected = connected is null
            ? picker.Saves.ToArray()
            : picker.Saves.Where(s => s.DocumentId != connected.Document.DocumentId).ToArray();
        if (connected is null && detected.Length == 0)
        {
            _boxViewModel.Status = "No games linked. Link an emulator on Home first.";
            return;
        }

        var connectedLabel = connected is null ? null : $"{connected.Document.DisplayName} (connected)";
        var options = connectedLabel is null
            ? detected.Select(s => new PadOption(s.GameLabel, IconPath: "storage")).ToArray()
            : new[] { new PadOption(connectedLabel, IconPath: "storage") }
                .Concat(detected.Select(s => new PadOption(s.GameLabel, IconPath: "storage"))).ToArray();

        var choice = await PadMenu.ShowAsync(_hostGrid, "SEND TO GAME",
            $"{entry.Info.Nickname} → pick the destination", options);
        if (choice is null) return;

        var bytes = _bank.GetData(entry.Id);
        var nickname = entry.Info.Nickname;

        if (connected is not null && choice == connectedLabel)
        {
            // The connected save goes through the live session, first empty slot of any box.
            var landing = _boxViewModel.Save?.Slots.FirstOrDefault(s => s.Species is null);
            if (landing is null)
            {
                _boxViewModel.Status = "No empty slot in the connected game.";
                return;
            }
            var ok = await _boxViewModel.RunMutationAsync(session =>
                session.ImportSlot(landing.Box, landing.Slot, bytes)
                    ? new GenerationOutcome(true, $"{nickname} joined the game (box {landing.Box + 1}).")
                    : new GenerationOutcome(false, "This mon cannot enter this game's format."), landing.Slot);
            if (!ok) return;
        }
        else
        {
            var index = Array.FindIndex(options, o => o.Label == choice) - (connectedLabel is null ? 0 : 1);
            if (index < 0 || index >= detected.Length) return;
            var outcome = await transfer.SendToGameAsync(bytes, nickname, detected[index]);
            _boxViewModel.Status = outcome.Message;
            if (!outcome.Success) return;
        }

        if (!keepOriginal)
        {
            _bank.Remove(entry.Id);
            RefreshBoxEntries();
            UpdatePreview();
        }
        _canvas.InvalidateSurface();
    }

    private async Task ExportAsync(BankEntry entry)
    {
        try
        {
            var bytes = _bank.GetData(entry.Id);
            var name = $"{entry.Info.Species:000} - {entry.Info.Nickname}.pk{entry.Info.Generation}";
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllBytesAsync(path, bytes);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(path) });
        }
        catch (Exception error)
        {
            _boxViewModel.Status = $"Export failed: {error.Message}";
        }
    }

    // ── Rendering: the shared storage world ─────────────────────────────────

    // The grid chrome (wallpaper flat, crosshair, slots, selection) all comes from
    // BoxGridRenderer and PksmPaint - one paint language, zero duplicated constants.
    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        var wallpaper = BoxGridRenderer.WallpaperAt(_boxIndex);
        var cell = BoxGridRenderer.GridMetrics(info).Cell;
        BoxGridRenderer.PaintBackdrop(canvas, info, _boxIndex);

        var entries = _boxEntries;
        for (var index = 0; index < Columns * Rows; index++)
        {
            var rect = BoxGridRenderer.SlotRect(info, index);
            var has = entries.TryGetValue(index, out var entry);
            PksmPaint.Slot(canvas, rect, wallpaper, empty: !has);
            if (!has)
            {
                if (index == _selectedSlot)
                    PksmPaint.Selection(canvas, rect);
                continue;
            }

            var isCarried = _carryId == entry!.Id;
            var drawRect = isCarried && index == _selectedSlot
                ? new SKRect(rect.Left, rect.Top - cell * 0.18f, rect.Right, rect.Bottom - cell * 0.18f)
                : rect;
            var bitmap = _sprites.GetSprite(entry.Info.Species, entry.Info.Form, entry.Info.Shiny);
            if (bitmap is not null)
            {
                if (isCarried && index != _selectedSlot)
                    canvas.SaveLayer(BoxGridRenderer.GhostPaint);
                var inset = rect.Width * 0.03f;
                var box = SKRect.Inflate(drawRect, -inset, -inset);
                var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                var dest = new SKRect(drawRect.MidX - w / 2, drawRect.MidY - h / 2, drawRect.MidX + w / 2, drawRect.MidY + h / 2);
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image, dest, BoxGridRenderer.SpriteSampling);
                if (isCarried && index != _selectedSlot)
                    canvas.Restore();
            }
            else
            {
                _sprites.Warm(entry.Info.Species, entry.Info.Form, entry.Info.Shiny, _frame.Request);
            }
            if (isCarried && index != _selectedSlot)
                PksmPaint.CarryGhost(canvas, rect);
            if (entry.Info.Shiny)
                BoxGridRenderer.DrawSparkle(canvas, rect.Right - rect.Width * 0.14f, rect.Top + rect.Height * 0.16f,
                    Math.Min(rect.Width, rect.Height) * 0.09f, BoxGridRenderer.SparklePaint);
            if (index == _selectedSlot)
                PksmPaint.Selection(canvas, rect);
        }
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;

        var slot = BoxGridRenderer.SlotFromTouch(_canvas.CanvasSize, args.Location);
        if (slot < 0) return;

        var wasSelected = _selectedSlot == slot;
        _selectedSlot = slot;
        UpdatePreview();

        if (_carryId is not null) { _ = ConfirmAsync(); return; }
        if (EntryAt(slot) is null) { _ = OpenEmptySlotMenuAsync(); return; }
        if (wasSelected) _ = ConfirmAsync(); // second tap grabs
        _canvas.InvalidateSurface();
    }
}
