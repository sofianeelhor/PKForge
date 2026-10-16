using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Infrastructure;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

using static PKForge.App.Views.EditorRows;

namespace PKForge.App.Views;

/// <summary>
/// The Bank: the app's own cross-game vault in the PKSM storage world. Each box page
/// is a saturated wallpaper flat with the dot lattice;
/// the second screen shows the highlighted mon. A grabs/places, START opens the
/// mon menu, L/R turn pages. Y marks slots for the organizer, X opens the bank's
/// actions (bulk moves, releases, sorting), and the readout under the grid carries the
/// mark count. Every write is an index write: the bank never touches a save itself.
/// </summary>
public sealed class BankPage : ContentPage, IPadPagingHandler
{
    private const int Columns = 6;
    private const int Rows = 5;

    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly BoxBrowserViewModel _boxViewModel; // for send-to-game and shared status
    private readonly BankFacts _facts;
    private readonly IGameDataService _data;
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly Grid _hostGrid;
    private readonly SKCanvasView _boxBar;
    private readonly SKCanvasView _panel;
    private string _boxTitle = "";
    private Label _statusLine = null!;

    private int _boxIndex;
    private int _selectedSlot;
    private Guid? _carryId;
    private readonly HashSet<(int Box, int Slot)> _marked = [];
    // The last slot Y marked: the far end of "mark range to here".
    private (int Box, int Slot)? _anchor;

    // The current box's slot->entry map, rebuilt only when the box or bank changes.
    private Dictionary<int, BankEntry> _boxEntries = new();

    public BankPage(IBankService bank, ISpriteService sprites, BoxBrowserViewModel boxViewModel, IGameDataService data)
    {
        _bank = bank;
        _sprites = sprites;
        _boxViewModel = boxViewModel;
        _facts = new BankFacts(bank, data);
        _data = data;
        Title = "Bank";
        NavigationPage.SetHasNavigationBar(this, false);

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _frame = new FrameInvalidator(_canvas);
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        // The box header over the well, as in the storage screen: the chevrons page the
        // boxes, the banner opens the box overview.
        _boxBar = new SKCanvasView { HeightRequest = Design(72), Margin = new Thickness(0, 0, 0, Design(28)) };
        _boxBar.PaintSurface += PaintBoxBar;
        var boxBarTap = new TapGestureRecognizer();
        boxBarTap.Tapped += (_, args) => TapBoxBar(args.GetPosition(_boxBar)?.X ?? _boxBar.Width / 2);
        _boxBar.GestureRecognizers.Add(boxBarTap);

        // The side panel: the selected Pokémon in the editor panel's look, the Bank's tools under it.
        _panel = new SKCanvasView { InputTransparent = true };
        _panel.PaintSurface += PaintPanel;
        Button Tool(string text, Action action)
        {
            var button = EditorTool(text);
            button.HeightRequest = Design(60);
            button.FontSize = EditorText * 0.82;
            button.Padding = new Thickness(Design(6), 0);
            button.Clicked += (_, _) => action();
            return button;
        }
        // One compact row, so the panel above keeps room for the facts.
        var tools = new Grid
        {
            ColumnSpacing = Design(12),
            Margin = new Thickness(0, Design(18), 0, 0),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(new GridLength(1.4, GridUnitType.Star)), new(GridLength.Star), new(GridLength.Star)],
        };
        tools.Add(Tool("Boxes", () => _ = OpenBoxOverviewAsync()), 0, 0);
        tools.Add(Tool("Search", () => _ = OpenSearchAsync()), 1, 0);
        tools.Add(Tool("Living dex", () => _ = OpenLivingDexAsync()), 2, 0);
        tools.Add(Tool("Import", () => _ = ImportArchiveAsync()), 3, 0);
        tools.Add(Tool("Export", () => _ = ExportArchiveAsync()), 4, 0);

        var left = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { _boxBar, _canvas },
        };
        Grid.SetRow(_canvas, 1);
        var right = new Grid
        {
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _panel, tools },
        };
        Grid.SetRow(tools, 1);
        var content = new Grid
        {
            Padding = new Thickness(Design(48), Design(32), Design(28), Design(20)),
            ColumnSpacing = Design(32),
            ColumnDefinitions = [new(new GridLength(1072, GridUnitType.Star)), new(new GridLength(740, GridUnitType.Star))],
            Children = { left, right },
        };
        Grid.SetColumn(right, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), content } };

        // The bank's own readout: the organizer's mark count and every action's report.
        // It mirrors the shared view-model status, so the vault and the boxes agree.
        _statusLine = Kit.LcdLabel(UiTokens.TextBody);
        _statusLine.FontFamily = DsChrome.PixelFont;
        _statusLine.SetBinding(Label.TextProperty, new Binding(nameof(BoxBrowserViewModel.Status), converter: Kit.TidyText));
        _statusLine.BindingContext = _boxViewModel;
        var statusHost = Kit.LcdPanel(_statusLine, padding: 6);
        statusHost.Margin = new Thickness(12, 0, 12, 6);

        var footer = DsChrome.Footer(
            ("A", "Grab", () => OnPadButton(PadButton.A)),
            ("Y", "Mark", ToggleCursorMark),
            ("X", "Actions", () => _ = ShowBankActionsAsync()),
            ("B", "Back", () => OnPadButton(PadButton.B)),
            ("LR", "Box", () => OnPadButton(PadButton.R)),
            // SELECT (-): a dual-screen device turns the inspector's page below; a single
            // screen opens the full-screen summary instead.
            ("-", HasSecondScreen ? "Page" : "Summary", () => OnPadButton(PadButton.Select)),
            ("+", "Menu", () => _ = OpenCursorMenuAsync()));

        // The strip's word follows Bank Sync ("Open" while this Bank is local).
        var strip = DsChrome.StatusStrip("Bank", out var flags, SyncFlag(Sync?.Status));
        _syncFlag = flags[0];
        var root = new Grid
        {
            RowDefinitions =
            [
                new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto),
            ],
            Children = { DsChrome.TitleBar(), strip, bodyHost, statusHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(statusHost, 3);
        Grid.SetRow(footer, 4);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;
        UpdatePageLabel();
        UpdateStatus();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // The lower screen belongs to the Bank's inspector while this page is in front.
        _secondClaim ??= IPlatformApplication.Current?.Services.GetService<SecondScreenState>()?.Routes.CreateClaim(SecondScreenOwner.Bank);
        _secondClaim?.Activate();
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable == true) { try { _ = host.ShowAsync(); } catch { } }
        RefreshBoxEntries();
        UpdatePreview();
        UpdateStatus();
        StartFactRotation();
        if (Sync is { } sync)
        {
            sync.StatusChanged += OnSyncStatus;
            _syncFlag.Text = SyncFlag(sync.Status);
        }
        if (Synced is { } synced) synced.RemoteChangesApplied += OnRemoteChanges;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Sync is { } sync) sync.StatusChanged -= OnSyncStatus;
        if (Synced is { } synced) synced.RemoteChangesApplied -= OnRemoteChanges;
        _factTimer?.Stop();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        _inspectCancel?.Cancel();
        // Releasing the claim hands the lower screen back (and drops any overlay opened on it).
        _secondClaim?.Release();
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.Inspected = null;
        // Lazy held-item migration: persist what this visit probed from old entries' bytes.
        var facts = _facts;
        _ = Task.Run(facts.FlushHeldItemBackfill);
    }

    private Label _syncFlag = null!;

    private static PKForge.Sync.SyncCoordinator? Sync =>
        IPlatformApplication.Current?.Services.GetService<PKForge.Sync.SyncCoordinator>();

    private static PKForge.Sync.SyncedBankService? Synced =>
        IPlatformApplication.Current?.Services.GetService<PKForge.Sync.SyncedBankService>();

    /// <summary>The Bank's word in the status strip: "Open" while local, the sync state while connected.</summary>
    private static string SyncFlag(PKForge.Sync.SyncStatus? status) => status?.Phase switch
    {
        null or PKForge.Sync.SyncPhase.NotConnected => "Open",
        PKForge.Sync.SyncPhase.Syncing => "Syncing",
        PKForge.Sync.SyncPhase.Offline => "Offline",
        PKForge.Sync.SyncPhase.Idle when status.WritesDisabled => "Read-only",
        PKForge.Sync.SyncPhase.Idle when status.Conflicts > 0 => status.Conflicts == 1 ? "1 conflict" : $"{status.Conflicts} conflicts",
        PKForge.Sync.SyncPhase.Idle when status.PendingChanges => "Sync pending",
        PKForge.Sync.SyncPhase.Idle => "Synced",
        PKForge.Sync.SyncPhase.NeedsJoin => "Sync setup",
        _ => "Sync paused",
    };

    private void OnSyncStatus(object? sender, PKForge.Sync.SyncStatus status) =>
        MainThread.BeginInvokeOnMainThread(() => _syncFlag.Text = SyncFlag(status));

    /// <summary>Another device changed the Bank: show it without leaving the page.</summary>
    private void OnRemoteChanges(object? sender, EventArgs args) => MainThread.BeginInvokeOnMainThread(() => ChangeBox(0));

    private SecondScreenClaim? _secondClaim;

    private async Task OpenLivingDexAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        if (data is null) return;
        await CollectionDexPage.ShowAsync(_hostGrid, _boxViewModel, data, _sprites);
    }

    private async Task OpenSearchAsync()
    {
        var data = IPlatformApplication.Current?.Services?.GetService<IGameDataService>();
        if (data is null) return;
        try
        {
            await BankSearchPage.ShowAsync(_hostGrid, _bank, data, _sprites, JumpTo, MarkResults);
        }
        catch (Exception error)
        {
            _boxViewModel.Status = $"Search closed: {error.Message}";
        }
    }

    /// <summary>A search hit lands the vault on the mon's box and slot, cursor on it.</summary>
    private void JumpTo(BankEntry entry)
    {
        _boxIndex = Math.Clamp(entry.Box, 0, _bank.BoxCount - 1);
        _selectedSlot = entry.Slot;
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    /// <summary>"Select all filtered": the search's results become the organizer's marks, and
    /// the vault lands on the first of them so X acts on the whole set straight away.</summary>
    private void MarkResults(IReadOnlyList<BankEntry> results)
    {
        _marked.Clear();
        foreach (var entry in results) _marked.Add((entry.Box, entry.Slot));
        _anchor = null;
        var first = results.OrderBy(e => e.Box).ThenBy(e => e.Slot).First();
        JumpTo(first);
    }

    /// <summary>True when Hardcore mode refuses <paramref name="action"/>; the reason lands in
    /// the status strip and a dialog, never swallowed - the same shape as the boxes.</summary>
    private async Task<bool> DeniedAsync(SaveAction action)
    {
        if (!HardcoreMode.Blocks(action, out var status)) return false;
        _boxViewModel.Status = status;
        await PadMenu.ShowAsync(_hostGrid, "Hardcore mode", status, "OK");
        return true;
    }

    /// <summary>Menu copy plus the Hardcore line while it is on.</summary>
    private static string? Note(string? message) =>
        HardcoreMode.IsOn
            ? string.IsNullOrEmpty(message) ? HardcoreMode.StatusLine : $"{message}\n{HardcoreMode.StatusLine}"
            : message;

    private static bool Allowed(SaveAction action) => HardcoreMode.Guard.Allows(action);

    private async Task ExportArchiveAsync()
    {
        var format = await PadMenu.ShowAsync(_hostGrid, "Export",
            "A .pk folder is read by PKHeX and PKForge; a PKSM bank goes back to your 3DS.",
            new PadOption("Folder of .pk files", IconPath: "folder", Detail: "One .pk file per Pokémon, readable by PKHeX and PKForge."),
            new PadOption("PKSM bank for the 3DS (.bnk)", IconPath: "bank", Detail: "One bank file to copy back to PKSM on your 3DS."));
        if (format is null) return;
        if (format.StartsWith("PKSM", StringComparison.Ordinal))
        {
            await ExportPksmAsync(null);
            return;
        }
        var choice = await PadMenu.ShowAsync(_hostGrid, "Export archive",
            "A folder of .pk files plus a manifest — readable by PKHeX and PKForge alike.",
            new PadOption("Whole bank", IconPath: "bank"),
            new PadOption("This box only", IconPath: "box"));
        if (choice is null) return;

        var candidates = choice == "This box only"
            ? _bank.GetAll().Where(e => e.Box == _boxIndex).ToArray()
            : null;
        if (candidates is { Length: 0 })
        {
            _boxViewModel.Status = "Nothing to export — this box is empty.";
            return;
        }
        await ExportToFolderAsync(candidates, choice == "This box only" ? "this box" : "the whole bank");
    }

    /// <summary>Writes .pk files plus the manifest into a picked folder - the archive path
    /// shared by the strip's EXPORT and the organizer's bulk export. Null candidates = the bank.</summary>
    private async Task ExportToFolderAsync(IReadOnlyList<BankEntry>? candidates, string label)
    {
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IFolderPicker>();
        var files = services?.GetService<IFolderFileAccess>();
        if (picker is null || files is null) return;

        var folder = await picker.PickFolderAsync();
        if (folder is null) return;
        var overlay = LoadingOverlay.Show(_hostGrid, "Packing the archive…", $"Writing .pk files to {folder.DisplayName}.");
        try
        {
            var exported = await Task.Run(() => BankArchive.ExportAsync(_bank, files, folder.TreeId, candidates));
            _boxViewModel.Status = exported == 0
                ? "Nothing to export — the bank is empty."
                : $"Archive written: {exported} Pokémon from {label} → {folder.DisplayName}.";
        }
        catch (Exception error)
        {
            _boxViewModel.Status = $"Export failed: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>PKSM bank export: the whole bank or this box keep their layout; a selection is packed.</summary>
    private async Task ExportPksmAsync(List<BankEntry>? selection)
    {
        IReadOnlyList<BankEntry> entries;
        PksmExportLayout layout;
        string label;
        if (selection is not null)
        {
            (entries, layout, label) = (selection, PksmExportLayout.Pack, $"{selection.Count} selected");
        }
        else
        {
            var scope = await PadMenu.ShowAsync(_hostGrid, "Export PKSM bank",
                "Box layout and box names are kept. Gen 9, BDSP and Legends mons have no PKSM slot and are left out.",
                new PadOption("Whole bank", IconPath: "bank"),
                new PadOption("This box only", IconPath: "box"));
            if (scope is null) return;
            var box = scope == "This box only";
            entries = box ? _bank.GetAll().Where(e => e.Box == _boxIndex).ToArray() : _bank.GetAll();
            (layout, label) = (PksmExportLayout.KeepPositions, box ? $"box {_boxIndex + 1:00}" : "the whole bank");
        }
        var status = await PksmTransferFlow.ExportAsync(_hostGrid, _bank, entries, layout, label);
        if (status is not null) _boxViewModel.Status = status;
    }

    /// <summary>PKSM import (.bnk/.json, dumps zip, loose .pk) with a preview before any write.</summary>
    private async Task ImportPksmAsync()
    {
        var status = await PksmTransferFlow.ImportAsync(_hostGrid, _bank);
        if (status is not null) _boxViewModel.Status = status;
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        _canvas.InvalidateSurface();
    }

    private async Task ImportArchiveAsync()
    {
        var format = await PadMenu.ShowAsync(_hostGrid, "Import",
            "From a 3DS: copy /3ds/PKSM/banks/ (or /3ds/PKSM/dumps/) off the SD card to this device, then pick the files.",
            new PadOption("PKSM bank, dumps zip or .pk files", IconPath: "bank", Detail: "Files copied off your 3DS; you see a preview before anything is added."),
            new PadOption("Folder of .pk files", IconPath: "folder", Detail: "Every .pk file in a folder joins the bank; exact copies are skipped."));
        if (format is null) return;
        if (format.StartsWith("PKSM", StringComparison.Ordinal))
        {
            await ImportPksmAsync();
            return;
        }
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IFolderPicker>();
        var files = services?.GetService<IFolderFileAccess>();
        var engine = services?.GetService<ISaveEngine>();
        if (picker is null || files is null || engine is null) return;

        // Importing files fabricates bank entries from outside data: creation, not a move.
        if (await DeniedAsync(SaveAction.CreateMon)) return;
        var folder = await picker.PickFolderAsync();
        if (folder is null) return;
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Import from folder?",
            $"Every recognized .pk file in {folder.DisplayName} joins the bank. Exact copies of mons already stored are skipped.",
            "Import");
        if (!confirmed) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "Reading the archive…", $"Scanning {folder.DisplayName}.");
        try
        {
            var summary = await Task.Run(() => BankArchive.ImportAsync(_bank,
                (bytes, name) => engine.TryDescribeEntity(bytes, name, name), // the extension names the format
                files, folder.TreeId, (done, total) => overlay.Report(done, total), overlay.Cancellation.Token));
            _boxViewModel.Status = summary.Imported == 0 && summary.SkippedDuplicates == 0
                ? "No recognizable Pokémon in that folder."
                : $"{summary.Imported} imported · {summary.SkippedDuplicates} duplicate(s) skipped"
                  + (summary.Rejected > 0 ? $" · {summary.Rejected} unreadable" : "") + ".";
        }
        catch (OperationCanceledException)
        {
            _boxViewModel.Status = "Import cancelled.";
        }
        catch (Exception error)
        {
            _boxViewModel.Status = $"Import failed: {error.Message}";
        }
        finally
        {
            overlay.Close();
            RefreshBoxEntries();
            UpdatePageLabel();
            _canvas.InvalidateSurface();
        }
    }


    private void UpdatePageLabel()
    {
        _boxTitle = BankBoxDecor.Names.Get(_boxIndex) is { } name
            ? $"{_boxIndex + 1:00} / {_bank.BoxCount:00} · {name}"
            : $"Box {_boxIndex + 1:00} / {_bank.BoxCount:00}";
        _boxBar.InvalidateSurface();
    }

    private void ChangeBox(int delta)
    {
        _boxIndex = Math.Clamp(_boxIndex + delta, 0, _bank.BoxCount - 1);
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    /// <summary>Rebuild the current box's slot map from the bank. Call after any mutation.</summary>
    private void RefreshBoxEntries()
    {
        var map = new Dictionary<int, BankEntry>();
        var live = new HashSet<(int Box, int Slot)>();
        foreach (var entry in _bank.GetAll())
        {
            live.Add((entry.Box, entry.Slot));
            if (entry.Box == _boxIndex) map[entry.Slot] = entry;
        }
        _boxEntries = map;
        // Never keep a mark on a slot the bank no longer holds (release, move, sort).
        _marked.IntersectWith(live);
    }

    private BankEntry? EntryAt(int slot) => _boxEntries.GetValueOrDefault(slot);

    private CancellationTokenSource? _inspectCancel;

    /// <summary>
    /// Feeds the second-screen inspector with the mon under the cursor: the summary first,
    /// the legality verdict second (it is the slow half). A short settle delay keeps a fast
    /// cursor sweep from decoding every slot it passes. Single-screen devices skip the work.
    /// </summary>
    private void UpdatePreview()
    {
        RequestPanelSummary();
        var services = IPlatformApplication.Current?.Services;
        var state = services?.GetService<SecondScreenState>();
        if (state is null) return;
        _inspectCancel?.Cancel();
        var caption = $"Bank · Box {_boxIndex + 1:00} · slot {_selectedSlot + 1:00}";
        var entry = EntryAt(_selectedSlot);
        if (entry is null)
        {
            state.Inspected = new InspectorContent(null, false, caption);
            return;
        }
        if (services?.GetService<ISecondaryDisplayHost>()?.IsAvailable != true) return;
        var engine = services.GetService<ISaveEngine>();
        var summaries = services.GetService<IMonSummaryService>();
        if (engine is null || summaries is null) return;
        var cancel = _inspectCancel = new CancellationTokenSource();
        _ = InspectAsync(state, engine, summaries, entry, caption, cancel.Token);
    }

    private async Task InspectAsync(SecondScreenState state, ISaveEngine engine, IMonSummaryService summaries,
        BankEntry entry, string caption, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(60, cancel);
            var loadWatch = System.Diagnostics.Stopwatch.StartNew();
            var quick = await SummaryLoaders.FromBank(_bank, engine, summaries, entry, analyzeLegality: false);
            PerfTrace.Log("bank.inspect.quick", loadWatch);
            if (cancel.IsCancellationRequested) return;
            // A revisited entry comes back from the summary cache with its verdict already in.
            var settled = quick?.Legal is not null;
            state.Inspected = new InspectorContent(quick, quick is not null && !settled, caption);
            if (quick is null || settled) return;
            loadWatch.Restart();
            var full = await SummaryLoaders.FromBank(_bank, engine, summaries, entry, analyzeLegality: true);
            PerfTrace.Log("bank.inspect.full", loadWatch);
            if (cancel.IsCancellationRequested) return;
            state.Inspected = new InspectorContent(full ?? quick, false, caption);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancel.IsCancellationRequested) state.Inspected = new InspectorContent(null, false, caption);
        }
    }

    /// <summary>SELECT on a dual-screen device: turn the inspector's page.</summary>
    private void TurnInspectorPage()
    {
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.InspectorPage = SummaryNavigation.Turn(state.InspectorPage, 1);
    }

    private static bool HasSecondScreen =>
        IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>()?.IsAvailable == true;

    /// <summary>
    /// The full-screen summary of the cursor's mon. L/R inside it walk this bank box's
    /// occupied slots and carry the cursor along; X hands over to the bank editor when
    /// editing is allowed (Hardcore keeps the summary, drops the shortcut).
    /// </summary>
    private async Task OpenSummaryAsync()
    {
        RefreshBoxEntries();
        if (EntryAt(_selectedSlot) is null) { _boxViewModel.Status = "Nothing to summarize in an empty slot."; return; }
        var services = IPlatformApplication.Current!.Services;
        var engine = services.GetRequiredService<ISaveEngine>();
        var summaries = services.GetRequiredService<IMonSummaryService>();
        var entries = _boxEntries;
        var box = _boxIndex;
        var deck = new SummaryDeck(
            Columns * Rows,
            slot => entries.ContainsKey(slot),
            _selectedSlot,
            (slot, legality) => entries.TryGetValue(slot, out var e)
                ? SummaryLoaders.FromBank(_bank, engine, summaries, e, legality)
                : Task.FromResult<MonSummary?>(null),
            $"Bank · Box {box + 1:00}",
            CanEdit: Allowed(SaveAction.EditMon),
            Icon: slot => entries.TryGetValue(slot, out var e) ? new SlotIcon(e.Info.Species, e.Info.Form, e.Info.Shiny, _facts.HeldItem(e) != 0, e.Info.Traits ?? default) : null,
            Moved: slot =>
            {
                if (_boxIndex != box) return;
                _selectedSlot = slot;
                UpdatePreview();
                UpdateStatus();
                _canvas.InvalidateSurface();
            });
        var page = services.GetService<SecondScreenState>()?.InspectorPage ?? SummaryPage.Info;
        var result = await MonSummaryScreen.ShowAsync(_hostGrid, _sprites, deck, page);
        if (result.EditRequested && entries.TryGetValue(result.Slot, out var chosen))
            await EditEntryAsync(chosen);
    }

    // ── The organizer: marks, count, status ──────────────────────────────────

    /// <summary>The readout under the grid: the marked count while marking, otherwise the
    /// mon under the cursor. The same words reach the shared status the boxes show.</summary>
    private void UpdateStatus()
    {
        if (_marked.Count > 0)
        {
            _boxViewModel.Status = $"{_marked.Count} marked · X actions · B clears";
            return;
        }
        var entry = EntryAt(_selectedSlot);
        _boxViewModel.Status = entry is null
            ? $"Box {_boxIndex + 1:00} slot {_selectedSlot + 1:00} empty" + (HardcoreMode.IsOn ? $" · {HardcoreMode.Marker}" : " · A adds")
            : $"{Describe(entry)} · Y mark · X actions";
    }

    private string Describe(BankEntry entry)
    {
        var species = _facts.SpeciesName(entry.Info.Species);
        var nickname = BankFilter.IsDefaultNamed(entry.Info.Nickname, species) ? "" : $" “{entry.Info.Nickname}”";
        return $"#{entry.Info.Species:000} {species}{nickname} Lv.{entry.Info.Level} · Gen {entry.Info.Generation} · {entry.Info.SourceName}";
    }

    /// <summary>The cursor slot's mark, as the Y button and the footer chip both do it.</summary>
    private void ToggleCursorMark()
    {
        if (EntryAt(_selectedSlot) is null) { _boxViewModel.Status = "Nothing to mark in an empty slot."; return; }
        if (_marked.Remove((_boxIndex, _selectedSlot))) _anchor = null;
        else { _marked.Add((_boxIndex, _selectedSlot)); _anchor = (_boxIndex, _selectedSlot); }
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    /// <summary>The marked entries, in slot order, looked up fresh from the bank.
    /// With nothing marked the cursor's mon is the selection, so X always acts on something.</summary>
    private List<BankEntry> Selection()
    {
        var all = _bank.GetAll();
        if (_marked.Count == 0)
            return all.Where(e => e.Box == _boxIndex && e.Slot == _selectedSlot).ToList();
        return all.Where(e => _marked.Contains((e.Box, e.Slot)))
            .OrderBy(e => e.Box).ThenBy(e => e.Slot)
            .ToList();
    }

    private void ClearMarks()
    {
        _marked.Clear();
        _anchor = null;
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    private void MarkThisBox()
    {
        foreach (var entry in _boxEntries.Values) _marked.Add((entry.Box, entry.Slot));
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    private void MarkWholeBank()
    {
        foreach (var entry in _bank.GetAll()) _marked.Add((entry.Box, entry.Slot));
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    /// <summary>Flips every mark in this box: the quick "everything but these" selection.</summary>
    private void InvertThisBox()
    {
        foreach (var entry in _boxEntries.Values)
            if (!_marked.Remove((entry.Box, entry.Slot))) _marked.Add((entry.Box, entry.Slot));
        _anchor = null;
        UpdateStatus();
        _canvas.InvalidateSurface();
    }

    /// <summary>Range mark: every occupied slot from the last Y mark to the cursor, across
    /// box pages, joins the selection.</summary>
    private void MarkRangeToCursor()
    {
        if (_anchor is not { } anchor) return;
        foreach (var slot in BankSelection.Range(anchor, (_boxIndex, _selectedSlot), _bank.GetAll()))
            _marked.Add(slot);
        _anchor = (_boxIndex, _selectedSlot);
        UpdateStatus();
        _canvas.InvalidateSurface();
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
            case PadButton.Y: ToggleCursorMark(); return true;
            case PadButton.X: _ = ShowBankActionsAsync(); return true;
            case PadButton.B:
                if (_marked.Count > 0) { ClearMarks(); return true; }
                if (_carryId is not null) { _carryId = null; _canvas.InvalidateSurface(); return true; }
                _ = Navigation.PopAsync();
                return true;
            case PadButton.Start: _ = OpenCursorMenuAsync(); return true;
            case PadButton.Select:
                if (HasSecondScreen) TurnInspectorPage(); else _ = OpenSummaryAsync();
                return true;
            default: return false;
        }
    }

    private bool MoveCursor(int dx, int dy)
    {
        var col = Math.Clamp(_selectedSlot % Columns + dx, 0, Columns - 1);
        var row = Math.Clamp(_selectedSlot / Columns + dy, 0, Rows - 1);
        _selectedSlot = row * Columns + col;
        UpdatePreview();
        if (_marked.Count == 0) UpdateStatus();
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
            // The panel and the lower screen still describe what the slot held before the drop.
            UpdatePreview();
            UpdateStatus();
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

        // Hardcore mode keeps every move and drops every copy: the read-only Summary is always
        // offered, the editor only when editing is allowed; Duplicate and Copy to game are not offered at all.
        var options = new List<PadOption> { new("Summary", IconPath: "info", Detail: "A read-only look at this Pokémon.") };
        if (Allowed(SaveAction.EditMon))
        {
            options.Add(new PadOption("Edit", IconPath: "editor"));
            options.Add(new PadOption("Evolve…", IconPath: "evolve", Detail: "Evolves it right here in the bank."));
        }
        options.Add(new PadOption("Send to Poképark", IconPath: "park", Detail: "It joins the Poképark visitors. The bank is not changed."));
        if (Allowed(SaveAction.Duplicate)) options.Add(new PadOption("Duplicate", IconPath: "copy", Detail: "An exact copy goes into the first free bank slot."));
        options.Add(new PadOption("Send to game…", IconPath: "send", Detail: "Moves it into a game's boxes and out of the bank."));
        if (Allowed(SaveAction.Duplicate)) options.Add(new PadOption("Copy to game…", IconPath: "copy", Detail: "Puts a copy in a game's boxes; this one stays in the bank."));
        options.Add(new PadOption("Move (carry)", IconPath: "move", Detail: "Pick it up and drop it in another slot."));
        options.Add(new PadOption("Export .pk file", IconPath: "export", Detail: "Saves this Pokémon as a single .pk file."));
        options.Add(new PadOption("Release from bank", IconPath: "release", Detail: "Removes it from the bank for good; there is no undo."));
        var choice = await PadMenu.ShowAsync(_hostGrid, entry.Info.Nickname,
            Note($"From {entry.Info.SourceName} · Gen {entry.Info.Generation} · deposited {entry.AddedUtc:yyyy-MM-dd}"),
            options.ToArray());
        switch (choice)
        {
            case "Send to Poképark":
                _boxViewModel.Status = IPlatformApplication.Current!.Services.GetRequiredService<PokeparkService>().AddBankVisitor(entry.Id);
                await PadMenu.ShowAsync(_hostGrid, "Poképark", _boxViewModel.Status, new PadOption("OK"));
                return;
            case "Summary":
                await OpenSummaryAsync();
                return;
            case "Edit":
                await EditEntryAsync(entry);
                return;
            case "Evolve…":
                await EvolveEntryAsync(entry);
                return;
            case "Duplicate":
                if (await DeniedAsync(SaveAction.Duplicate)) return;
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
                if (await DeniedAsync(SaveAction.Duplicate)) return;
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
                var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Release from bank?",
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

    // ── The organizer's actions: bulk moves, keepsakes, releases, sorting ─────

    /// <summary>
    /// X: everything the marked selection can do, plus the vault's own organizer tools.
    /// With nothing marked the cursor's mon is the selection, so X always acts on something.
    /// </summary>
    private async Task ShowBankActionsAsync()
    {
        RefreshBoxEntries(); // never decide the menu from a stale slot map
        var selection = Selection();
        var noun = _marked.Count > 0 ? $"{_marked.Count} marked"
            : selection.Count > 0 ? $"slot {_selectedSlot + 1:00}"
            : "Nothing selected";
        var targets = TransferTargets();

        var options = new List<PadOption> { new("Boxes…", IconPath: "box", Detail: "Every box at a glance, to reorder, rename or restyle them.") };
        if (selection.Count > 0)
        {
            options.Add(new PadOption("Move to another box…", IconPath: "move", Detail: "They fill a box's empty slots, in order."));
            options.Add(new PadOption("Move to a position…", IconPath: "move", Detail: "They land from a slot you pick; what is there slides back."));
            if (Allowed(SaveAction.Duplicate))
                options.Add(new PadOption(selection.Count == 1 ? "Duplicate" : "Duplicate all", IconPath: "copy", Detail: "Each copy goes into the first free bank slot."));
            options.Add(new PadOption(selection.Count == 1 ? "Export .pk file" : "Export .pk files", IconPath: "export", Detail: "Writes one .pk file each into a folder you pick."));
            options.Add(new PadOption("Export as a PKSM bank", IconPath: "export", Detail: "Packs them into one bank file for PKSM on the 3DS."));
            if (targets.ConnectedLabel is not null || targets.Detected.Length > 0)
                options.Add(new PadOption("Send to a game…", IconPath: "send", Detail: "Moves them into a game; only those that arrive leave the bank."));
            options.Add(new PadOption("Release from bank…", IconPath: "release", Detail: "Removes them from the bank for good; there is no undo."));
        }
        if (_anchor is not null && _anchor != (_boxIndex, _selectedSlot))
            options.Add(new PadOption("Mark range to here", IconPath: "range", Detail: "Marks every Pokémon from your last mark to the cursor."));
        options.Add(new PadOption("Mark everything in this box", IconPath: "selectall"));
        options.Add(new PadOption("Invert marks in this box", IconPath: "invert", Detail: "Marked Pokémon in this box are unmarked, the rest marked."));
        options.Add(new PadOption("Mark the whole bank", IconPath: "selectall"));
        options.Add(new PadOption("Mark by filter…", IconPath: "filter", Detail: "Search the bank and mark what matches."));
        if (_marked.Count > 0) options.Add(new PadOption("Clear marks", IconPath: "deselect"));
        options.Add(new PadOption($"Sort box {_boxIndex + 1:00}…", IconPath: "sort", Detail: "Reorders this box by dex number, level, type and more."));
        options.Add(new PadOption("Sort the whole bank…", IconPath: "sort", Detail: "Reorders every Pokémon in the bank by the order you pick."));
        if (_marked.Count > 1) options.Add(new PadOption("Sort the marked into boxes…", IconPath: "sort", Detail: "The marked move, sorted, into new boxes after the last one used."));
        options.Add(new PadOption("Compact the bank (close gaps)", IconPath: "compact", Detail: "Fills every empty slot from box 1 on, keeping the order."));
        options.Add(new PadOption($"Rename box {_boxIndex + 1:00}…", IconPath: "rename"));
        options.Add(new PadOption($"Wallpaper for box {_boxIndex + 1:00}…", IconPath: "fashion"));
        if (_bank.BoxCount > 1)
            options.Add(new PadOption($"Delete box {_boxIndex + 1:00}…", IconPath: "delete", Detail: "Its Pokémon move to free slots in other boxes first."));

        var choice = await PadMenu.ShowAsync(_hostGrid, $"Bank actions · {noun}",
            Note(_marked.Count > 0
                ? "Marks keep across box pages. Sorting rewrites the bank's own index."
                : "Y marks slots; with nothing marked X acts on the cursor slot."),
            options.ToArray());
        switch (choice)
        {
            case null:
                return;
            case "Move to another box…":
                await MoveSelectionToBoxAsync(selection);
                return;
            case "Move to a position…":
                await MoveSelectionToPositionAsync(selection);
                return;
            case "Duplicate" or "Duplicate all":
                if (await DeniedAsync(SaveAction.Duplicate)) return;
                DuplicateSelection(selection);
                return;
            case "Export .pk file" or "Export .pk files":
                await ExportToFolderAsync(selection, $"{selection.Count} selected");
                return;
            case "Export as a PKSM bank":
                await ExportPksmAsync(selection);
                return;
            case "Send to a game…":
                await SendSelectionToGameAsync(selection);
                return;
            case "Release from bank…":
                await ReleaseSelectionAsync(selection);
                return;
            case "Mark range to here":
                MarkRangeToCursor();
                return;
            case "Mark everything in this box":
                MarkThisBox();
                return;
            case "Invert marks in this box":
                InvertThisBox();
                return;
            case "Mark the whole bank":
                MarkWholeBank();
                return;
            case "Mark by filter…":
                await OpenSearchAsync();
                return;
            case "Clear marks":
                ClearMarks();
                return;
            case string label when label.StartsWith("Sort box ", StringComparison.Ordinal):
                await SortAsync(_boxIndex);
                return;
            case "Sort the whole bank…":
                await SortAsync(null);
                return;
            case "Sort the marked into boxes…":
                await SortSelectionAsync(selection);
                return;
            case "Compact the bank (close gaps)":
                await CompactAsync();
                return;
            case "Boxes…":
                await OpenBoxOverviewAsync();
                return;
            case string label when label.StartsWith("Rename box ", StringComparison.Ordinal):
                await RenameBoxAsync(_boxIndex);
                return;
            case string label when label.StartsWith("Wallpaper for box ", StringComparison.Ordinal):
                await PickWallpaperAsync(_boxIndex);
                return;
            case string label when label.StartsWith("Delete box ", StringComparison.Ordinal):
                await DeleteBoxAsync(_boxIndex);
                return;
        }
    }

    /// <summary>Bulk move: the selection takes the target box's gaps, in order.</summary>
    private async Task MoveSelectionToBoxAsync(List<BankEntry> selection)
    {
        var boxes = Enumerable.Range(1, _bank.BoxCount).Select(n => $"Box {n:00}").ToArray();
        var target = await PadMenu.ShowAsync(_hostGrid, "Move to which box?",
            $"{selection.Count} selected · the box's empty slots take them in order.", boxes);
        if (target is null) return;
        var box = Array.IndexOf(boxes, target);
        var before = _bank.GetAll();
        var placements = BankPlacement.IntoBox(selection.Select(e => e.Id).ToArray(), box, before);
        if (placements.Count == 0)
        {
            _boxViewModel.Status = $"Nothing moved: box {box + 1:00} is full.";
            return;
        }
        var moved = _bank.Place(placements);
        RemapMarks(before, placements);
        var waiting = selection.Count(e => e.Box != box) - moved;
        FinishOrganizer($"{moved} moved into box {box + 1:00}"
            + (waiting > 0 ? $" · {waiting} stayed (no room)." : "."));
    }

    /// <summary>Bulk move to an exact spot: the selection fills forward from a chosen slot
    /// and the box's other entries slide back.</summary>
    private async Task MoveSelectionToPositionAsync(List<BankEntry> selection)
    {
        var boxes = Enumerable.Range(1, _bank.BoxCount).Select(n => $"Box {n:00}").ToArray();
        var target = await PadMenu.ShowAsync(_hostGrid, "Move to which box?", $"{selection.Count} selected.", boxes);
        if (target is null) return;
        var box = Array.IndexOf(boxes, target);
        var slots = Enumerable.Range(1, IBankService.SlotsPerBox).Select(n => $"Slot {n:00}").ToArray();
        var startChoice = await PadMenu.ShowAsync(_hostGrid, $"Fill forward from slot? (box {box + 1:00})",
            "The selection lands here in order; whatever is already there slides back.", slots);
        if (startChoice is null) return;
        var start = Array.IndexOf(slots, startChoice);

        var before = _bank.GetAll();
        var placements = BankPlacement.InsertRun(selection.Select(e => e.Id).ToArray(), box, start, before);
        if (placements.Count == 0)
        {
            _boxViewModel.Status = $"No room in box {box + 1:00} from slot {start + 1:00}.";
            return;
        }
        var ids = selection.Select(e => e.Id).ToHashSet();
        var landed = placements.Count(p => ids.Contains(p.Id));
        var moved = _bank.Place(placements);
        RemapMarks(before, placements);
        FinishOrganizer($"{landed} placed from box {box + 1:00} slot {start + 1:00} · {moved} slots changed"
            + (landed < selection.Count ? $" · {selection.Count - landed} had no room." : "."));
    }

    /// <summary>Keepsakes: the selection is copied, each copy landing in the first gap.</summary>
    private void DuplicateSelection(List<BankEntry> selection)
    {
        // One index write: every copy lands, or none does.
        try { _bank.AddMany([.. selection.Select(e => new BankDeposit(_bank.GetData(e.Id), e.Info))]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _boxViewModel.Status = $"Nothing was cloned: {error.Message}";
            return;
        }
        FinishOrganizer(selection.Count == 1
            ? $"{selection[0].Info.Nickname} cloned in the bank."
            : $"{selection.Count} Pokémon cloned in the bank.");
    }

    private async Task ReleaseSelectionAsync(List<BankEntry> selection)
    {
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Release from the bank?",
            selection.Count == 1
                ? $"Release {selection[0].Info.Nickname}? Bank releases are permanent (the bank has no restore points yet)."
                : $"Release all {selection.Count} selected Pokémon? Bank releases are permanent (the bank has no restore points yet).",
            selection.Count == 1 ? "Release" : "Release all");
        if (!confirmed) return;
        var released = _bank.RemoveMany(selection.Select(e => e.Id).ToArray());
        FinishOrganizer($"Released {released} Pokémon from the bank.");
    }

    /// <summary>
    /// Sends the whole selection to one game, all or none. The connected save or any other
    /// detected game takes every one in a single safe write (validate → backup → atomic), previewed
    /// once, or nothing is written when one cannot enter; only then does the selection leave the
    /// bank in one index write, and if the bank cannot let go the game gives them back.
    /// </summary>
    private async Task SendSelectionToGameAsync(List<BankEntry> selection)
    {
        var services = IPlatformApplication.Current?.Services;
        var transfer = services?.GetService<Services.TransferService>();
        if (transfer is null) return;

        var picked = await PickDestinationAsync($"{selection.Count} selected → pick the destination");
        if (picked is not { } destinationPick) return;

        var items = selection.Select(e => new PKForge.Engine.TransferItem(_bank.GetData(e.Id), e.Info.Format, e.Info.Nickname)).ToArray();
        var ids = selection.Select(e => e.Id).ToArray();
        var subject = selection.Count == 1 ? selection[0].Info.Nickname : $"{selection.Count} Pokémon";
        PKForge.Engine.BatchTransferOutcome outcome;
        if (destinationPick.Other is null)
        {
            const string destination = "the connected game";
            var open = SlotPlanning.FreeSlotsFrom(_boxViewModel.Save?.Slots ?? [], null).ToArray();
            if (open.Length == 0)
            {
                _boxViewModel.Status = "No empty slot in the connected game.";
                return;
            }
            // Same trust step as a game-to-game send: the first mon's diff and warnings
            // (a backwards conversion lists its compromises) stand for the batch.
            var connectedPreview = transfer.PreviewIntoConnected(items[0].Data, items[0].Nickname, open[0].Box, open[0].Slot, items[0].Format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, connectedPreview, subject, destination))
                return;
            var rooms = open.Take(items.Length).ToArray();
            outcome = await PKForge.Engine.BankTransfers.WithdrawAsync(_bank, ids, destination,
                async () => await _boxViewModel.RunMutationAsync(session =>
                    PKForge.Engine.BankTransfers.ImportAll(session, items, rooms, out var refusal) is var failed and >= 0
                        ? new GenerationOutcome(false, failed >= items.Length
                            ? $"the connected game has room for {rooms.Length} of {items.Length} Pokémon."
                            : refusal is null
                                ? Services.TransferService.Refusal(items[failed].Data, items[failed].Nickname, session.Snapshot, destination, items[failed].Format)
                                : $"{items[failed].Nickname} cannot go to {destination}. {refusal}")
                        : new GenerationOutcome(true, $"{subject} joined the game."),
                    Math.Max(0, _selectedSlot), refreshSlot: false, action: SaveAction.Move) ? null : _boxViewModel.Status,
                async () => await _boxViewModel.RunMutationAsync(session =>
                {
                    foreach (var room in rooms) session.ReleaseSlot(room.Box, room.Slot);
                    return new GenerationOutcome(true, $"{subject} went back to the bank.");
                }, Math.Max(0, _selectedSlot), refreshSlot: false, action: SaveAction.Move) ? null : _boxViewModel.Status);
        }
        else
        {
            var target = destinationPick.Other;
            var preview = await transfer.PreviewAsync(items[0].Data, items[0].Nickname, target, format: items[0].Format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, preview, subject, target.GameLabel))
                return;
            IReadOnlyList<SlotRef> landed = [];
            outcome = await PKForge.Engine.BankTransfers.WithdrawAsync(_bank, ids, target.GameLabel,
                async () =>
                {
                    (var refusal, landed) = await transfer.SendManyToGameAsync(items, target, $"{subject} arrived from the bank");
                    return refusal;
                },
                async () =>
                {
                    await transfer.ReleaseFromGameAsync(target, landed, $"{subject} went back to the bank");
                    return null;
                });
        }

        if (outcome.Success || outcome.Duplicated) FinishOrganizer(outcome.Message);
        else _boxViewModel.Status = outcome.Message;
    }

    /// <summary>
    /// The bank's toolkit for sorting: a real reorder of the vault's slots through the index,
    /// by any of the collector's keys either way round. Stored bytes never move and no save is
    /// involved; marks ride along with their mons.
    /// </summary>
    private async Task SortAsync(int? box)
    {
        var picked = await BankSearchPage.PickSortAsync(_hostGrid,
            box is null ? "Sort the whole bank" : $"Sort box {box.Value + 1:00}",
            "A real reorder of the bank's slots - stored bytes never move.");
        if (picked is null) return;
        var (order, reverse, label) = picked.Value;

        var before = _bank.GetAll();
        var scope = box is { } target ? before.Where(e => e.Box == target).ToList() : before.ToList();
        if (scope.Count < 2)
        {
            _boxViewModel.Status = "Nothing to sort there yet.";
            return;
        }
        var where = box is { } sorted ? $"box {sorted + 1:00}" : "the whole bank";
        await ApplyLayoutAsync(before, BankPlacement.Reorder(box, BankSorting.Order(scope, order, _facts, reverse)),
            label, where, scope.Count);
    }

    /// <summary>
    /// Sorts only the marked mons into a run of fresh boxes after the last box anything else
    /// uses, so a collection (all the shinies, one type, a generation) gets its own shelf
    /// without disturbing the rest. The new boxes come from the same single index write.
    /// </summary>
    private async Task SortSelectionAsync(List<BankEntry> selection)
    {
        var picked = await BankSearchPage.PickSortAsync(_hostGrid, $"Sort {selection.Count} marked",
            "They move, in order, into new boxes after the last one in use.");
        if (picked is null) return;
        var (order, reverse, label) = picked.Value;

        var before = _bank.GetAll();
        var chosen = selection.Select(e => e.Id).ToHashSet();
        var shelf = before.Where(e => !chosen.Contains(e.Id)).Select(e => e.Box + 1).DefaultIfEmpty(0).Max();
        var boxes = (selection.Count + IBankService.SlotsPerBox - 1) / IBankService.SlotsPerBox;
        await ApplyLayoutAsync(before, BankPlacement.Reorder(shelf, BankSorting.Order(selection, order, _facts, reverse)),
            label, boxes == 1 ? $"box {shelf + 1:00}" : $"boxes {shelf + 1:00}-{shelf + boxes:00}", selection.Count);
    }

    // ── Box management ─────────────────────────────────────────────────────

    /// <summary>
    /// The one write path for whole-box changes: the bank rewrites every entry's box in one
    /// index write, then everything keyed by box index follows (names, wallpapers, the living
    /// dex region, marks, the cursor). False when the bank refused it; nothing changed then.
    /// </summary>
    public bool ApplyBoxRemap(BankBoxRemap remap)
    {
        // Hardcore mode allows this: whole boxes move, no Pokémon data is edited or copied.
        if (remap.IsIdentity) return true;
        try
        {
            _bank.RemapBoxes(remap);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        {
            _boxViewModel.Status = error.Message;
            return false;
        }
        // Names, wallpapers and the living dex region follow their boxes together.
        BankBoxDecor.Decor.Remap(remap);
        var marks = _marked.Where(m => remap.Map(m.Box) is not null).Select(m => (remap.Map(m.Box)!.Value, m.Slot)).ToList();
        _marked.Clear();
        _marked.UnionWith(marks);
        if (_anchor is { } anchor) _anchor = remap.Map(anchor.Box) is { } box ? (box, anchor.Slot) : null;
        _boxIndex = Math.Clamp(remap.Map(_boxIndex) ?? _boxIndex, 0, _bank.BoxCount - 1);
        ChangeBox(0);
        return true;
    }

    /// <summary>
    /// Deletes a box. An empty one just goes; a full one first moves its Pokémon into free
    /// spaces in the other boxes (in order, new boxes at the end if needed), so nothing is lost.
    /// </summary>
    public async Task<bool> DeleteBoxAsync(int box)
    {
        var inside = _bank.GetAll().Where(e => e.Box == box).OrderBy(e => e.Slot).ToList();
        var later = _bank.BoxCount - box - 1;
        var shift = later > 0 ? $" The {later} box{(later == 1 ? "" : "es")} after it move{(later == 1 ? "s" : "")} down one." : "";
        var message = inside.Count == 0
            ? $"It is empty.{shift}"
            : $"Its {inside.Count} Pokémon move to free spaces in your other boxes first, so none are lost.{shift}";
        if (!await PadMenu.ConfirmAsync(_hostGrid, $"Delete {BankBoxDecor.Label(box)}?", message, "Delete"))
            return false;

        if (inside.Count > 0)
        {
            var taken = _bank.GetAll().Where(e => e.Box != box).Select(e => (e.Box, e.Slot)).ToHashSet();
            var placements = new List<(Guid Id, int Box, int Slot)>(inside.Count);
            var cursor = 0;
            foreach (var entry in inside)
            {
                while (cursor / IBankService.SlotsPerBox == box || taken.Contains((cursor / IBankService.SlotsPerBox, cursor % IBankService.SlotsPerBox))) cursor++;
                placements.Add((entry.Id, cursor / IBankService.SlotsPerBox, cursor % IBankService.SlotsPerBox));
                cursor++;
            }
            try
            {
                _bank.Place(placements);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            {
                _boxViewModel.Status = error.Message;
                return false;
            }
        }
        if (!ApplyBoxRemap(BankBoxRemap.Remove(_bank.BoxCount, box))) return false;
        _boxViewModel.Status = inside.Count == 0 ? "Box deleted." : $"Box deleted; its {inside.Count} Pokémon moved to free spaces.";
        return true;
    }

    public async Task<bool> RenameBoxAsync(int box)
    {
        var name = await TextPopup.ShowLineAsync(_hostGrid, $"Rename box {box + 1:00}", "Up to 24 characters · empty for its number",
            BankBoxDecor.Names.Get(box) ?? "");
        if (name is null) return false;
        name = name.Trim();
        if (name.Length > 24) name = name[..24];
        BankBoxDecor.Names.SetMany([(box, name)]);
        UpdatePageLabel();
        _canvas.InvalidateSurface(); // the side panel shows the box name on empty slots
        _boxViewModel.Status = name.Length == 0 ? $"Box {box + 1:00} shows its number again." : $"Box {box + 1:00} is now \"{name}\".";
        return true;
    }

    public async Task<bool> PickWallpaperAsync(int box)
    {
        var chosen = await BankWallpaperPicker.ShowAsync(_hostGrid, $"Wallpaper · {BankBoxDecor.Label(box)}", BankBoxArt.Of(box));
        if (chosen is null) return false;
        BankBoxDecor.Wallpapers.Set(box, chosen);
        _canvas.InvalidateSurface();
        return true;
    }

    private async Task OpenBoxOverviewAsync()
    {
        var opened = await BankBoxOverview.ShowAsync(this, _hostGrid, _bank, _sprites, _boxIndex);
        if (opened is { } box)
        {
            _boxIndex = Math.Clamp(box, 0, _bank.BoxCount - 1);
            _selectedSlot = 0;
        }
        ChangeBox(0);
    }

    /// <summary>Closes every gap in the vault while keeping the current order: packed box by
    /// box, slot by slot, from box 1 slot 1.</summary>
    private async Task CompactAsync()
    {
        var before = _bank.GetAll();
        var ordered = before.OrderBy(e => e.Box).ThenBy(e => e.Slot).ToList();
        await ApplyLayoutAsync(before, BankPlacement.Reorder(null, ordered), "Compact", "the whole bank", ordered.Count);
    }

    /// <summary>
    /// The one write path for every rearrangement: say how many mons change slot, confirm, then
    /// apply the whole layout in a single index write. Marks follow their mons, the cursor
    /// follows the mon it was on, and nothing is written when the layout is already settled.
    /// </summary>
    private async Task ApplyLayoutAsync(IReadOnlyList<BankEntry> before,
        IReadOnlyList<(Guid Id, int Box, int Slot)> placements, string label, string where, int scopeCount)
    {
        var moving = BankPlacement.ChangedCount(placements, before);
        if (moving == 0)
        {
            _boxViewModel.Status = $"Nothing to do: {where} is already in that order.";
            return;
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Rearrange now?",
            $"{label} across {where}: {moving} of {scopeCount} Pokémon change slot. "
            + "The bank's index is rewritten in one go and the marks follow their Pokémon.",
            "Sort");
        if (!confirmed) return;

        var cursorId = before.FirstOrDefault(e => e.Box == _boxIndex && e.Slot == _selectedSlot)?.Id;
        var moved = _bank.Place(placements);
        RemapMarks(before, placements);
        _anchor = null;
        foreach (var (id, box, slot) in placements)
        {
            if (id != cursorId) continue;
            _boxIndex = box;
            _selectedSlot = slot;
            break;
        }
        FinishOrganizer($"{label} · {where} · {moved} moved.");
    }

    /// <summary>Marks are slot references: a rearrangement has to carry them to the mon's
    /// new slot, or they would end up pointing at whoever took the old one.</summary>
    private void RemapMarks(IReadOnlyList<BankEntry> before, IReadOnlyList<(Guid Id, int Box, int Slot)> placements)
    {
        if (_marked.Count == 0) return;
        var wasAt = before.ToDictionary(e => (e.Box, e.Slot), e => e.Id);
        var destination = placements.ToDictionary(p => p.Id, p => (p.Box, p.Slot));
        var rebuilt = new HashSet<(int Box, int Slot)>();
        foreach (var mark in _marked)
        {
            if (wasAt.TryGetValue(mark, out var id) && destination.TryGetValue(id, out var target)) rebuilt.Add(target);
            else rebuilt.Add(mark);
        }
        _marked.Clear();
        _marked.UnionWith(rebuilt);
    }

    /// <summary>Repaint after any organizer write and report it, keeping the marked count
    /// visible so the organizer never loses its place.</summary>
    private void FinishOrganizer(string status)
    {
        RefreshBoxEntries();
        UpdatePageLabel();
        UpdatePreview();
        _boxViewModel.Status = _marked.Count > 0 ? $"{status} · {_marked.Count} still marked" : status;
        _canvas.InvalidateSurface();
    }

    /// <summary>The connected save plus every other detected game, as the transfer picker
    /// offers them - shared by the single-mon and the bulk sends.</summary>
    private static (string? ConnectedLabel, DetectedSave[] Detected) TransferTargets()
    {
        var services = IPlatformApplication.Current?.Services;
        var connected = services?.GetService<ISaveSessionService>()?.Current;
        var detected = services?.GetService<SavePickerViewModel>()?.Saves.ToArray() ?? [];
        if (connected is null) return (null, detected);
        // The connected save by the player's own name when the shelf knows it.
        var connectedName = detected.FirstOrDefault(s => s.DocumentId == connected.Document.DocumentId)?.GameLabel
            ?? connected.Document.DisplayName;
        return ($"{connectedName} (connected)",
            detected.Where(s => s.DocumentId != connected.Document.DocumentId).ToArray());
    }

    /// <summary>
    /// The destination picker both sends share: the connected save first, then every other
    /// detected game. Twin saves of one game are told apart the way the save picker does it
    /// (trainer · date, then #n), and the pick resolves by position, never by a label that two
    /// saves could share. <c>Other</c> is null when the connected save was chosen.
    /// </summary>
    private async Task<(string Label, DetectedSave? Other)?> PickDestinationAsync(string subtitle)
    {
        var (connectedLabel, detected) = TransferTargets();
        if (connectedLabel is null && detected.Length == 0)
        {
            _boxViewModel.Status = "No games linked. Link an emulator on Home first.";
            return null;
        }

        var labels = detected.Select(save =>
        {
            var label = save.GameLabel;
            if (detected.Count(other => other.GameLabel == save.GameLabel) > 1)
            {
                if (!string.IsNullOrEmpty(save.TrainerName)) label += $" · {save.TrainerName}";
                label += $" · {save.FileName}";
                if (save.LastModified is { } modified) label += $" · {modified:yyyy-MM-dd}";
            }
            return label;
        }).ToArray();
        var pristine = labels.ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            if (pristine.Count(label => label == pristine[i]) <= 1) continue;
            labels[i] += $" (#{Enumerable.Range(0, i + 1).Count(j => pristine[j] == pristine[i])})";
        }

        var options = new List<PadOption>();
        if (connectedLabel is not null) options.Add(new PadOption(connectedLabel, IconPath: "game"));
        options.AddRange(labels.Select((label, i) => new PadOption(label, Glyph: "●",
            Accent: SaveColors.For(detected[i].Identity?.ColorKey, detected[i].Generation))));
        var choice = await PadMenu.ShowAsync(_hostGrid, "Send to game", subtitle, options.ToArray());
        if (choice is null) return null;
        var index = options.FindIndex(o => o.Label == choice);
        if (connectedLabel is not null)
        {
            if (index == 0) return ("the connected game", null);
            index--;
        }
        if (index < 0 || index >= detected.Length) return null;
        return (labels[index], detected[index]);
    }

    /// <summary>Full field editor for a stored mon, written back in place on save.</summary>
    /// <summary>Evolves a banked mon in its own format (trade evolutions included) and stores
    /// the result back in place. No save is involved, so there is no bag or dex to update.</summary>
    private async Task EvolveEntryAsync(BankEntry entry)
    {
        if (await DeniedAsync(SaveAction.EditMon)) return;
        if (PKForge.Engine.EntityBytes.RomHackGame(entry.Info.Format) is { } hack)
        {
            await PadMenu.ShowAsync(_hostGrid, "Evolution", $"Pokémon from {hack} can only be evolved in their own game.", "OK");
            return;
        }
        var services = IPlatformApplication.Current!.Services;
        var engine = services.GetRequiredService<ISaveEngine>();
        var service = services.GetRequiredService<IEvolutionService>();
        using var session = engine.OpenEntitySession(_bank.GetData(entry.Id), entry.Info.Nickname, entry.Info.Format);
        if (session is null)
        {
            await PadMenu.ShowAsync(_hostGrid, "Evolution", "This Pokémon's data could not be read.", "OK");
            return;
        }
        var request = await EvolutionCard.RunAsync(_hostGrid, session, 0, 0);
        if (request is null) return;
        var outcome = service.Evolve(session, 0, 0, request);
        if (outcome.Success)
        {
            var export = session.ExportSlot(0, 0);
            var info = engine.TryDescribeEntity(export.Data, entry.Info.SourceName, export.Format) ?? entry.Info;
            _bank.Replace(entry.Id, export.Data, info);
            RefreshBoxEntries();
            ForgetPanelSummary(); // same id, new Pokémon: the panel must read it again
            UpdatePreview();
            _canvas.InvalidateSurface();
        }
        _boxViewModel.Status = outcome.Message;
        await PadMenu.ShowAsync(_hostGrid, outcome.Success ? "Congratulations!" : "Evolution failed", outcome.Message, "OK");
    }

    private async Task EditEntryAsync(BankEntry entry)
    {
        var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
        var saved = await BankEntryEditor.ShowAsync(_hostGrid, _bank, engine, entry);
        if (saved)
        {
            RefreshBoxEntries();
            ForgetPanelSummary(); // same id, edited bytes: the panel must read it again
            UpdatePreview();
            _boxViewModel.Status = $"{EntryAt(_selectedSlot)?.Info.Nickname ?? "Pokémon"} updated in the bank.";
            _canvas.InvalidateSurface();
        }
    }

    private async Task OpenEmptySlotMenuAsync()
    {
        var session = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;
        // Every way into an empty bank slot fabricates a mon from outside the games; Hardcore
        // mode fills the vault only by moving mons in from a save.
        if (await DeniedAsync(SaveAction.CreateMon)) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, "Add to bank", null,
            new PadOption("Create a Pokémon", IconPath: "create", Detail: "Build a legal Pokémon step by step."),
            new PadOption("Paste a Showdown set", IconPath: "script", Detail: "Turns a Pokémon Showdown text set into a Pokémon."),
            new PadOption("Import .pk file", IconPath: "import", Detail: "Adds Pokémon from .pk files you pick."),
            new PadOption("Scan a .pk QR", IconPath: "scan", Detail: "Pick a picture of a .pk QR code to add its Pokémon."));
        switch (choice)
        {
            case "Create a Pokémon" or "Paste a Showdown set" when session is null:
            {
                // No game connected: pick the format, generate against a blank save with
                // a placeholder identity (OT "PKForge", editable afterwards in the editor).
                var eras = new[] { "Generation I", "Generation II", "Generation III", "Generation IV", "Generation V",
                    "Generation VI", "Generation VII", "Generation VIII", "Generation IX" };
                var era = await PadMenu.ShowAsync(_hostGrid, "Create for which game era?",
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
                var overlay = LoadingOverlay.Show(_hostGrid, "Creating for the bank…", "The offline legalizer is at work.");
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
                var text = await TextPopup.ShowAsync(_hostGrid, "Paste a Showdown set", "The set becomes a legal mon stored in the bank.");
                if (string.IsNullOrWhiteSpace(text)) return;
                var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
                var overlay = LoadingOverlay.Show(_hostGrid, "Reading the set…", "The offline legalizer is at work.");
                try
                {
                    var generated = await Task.Run(() => legalizer.GenerateDataFromShowdown(session!, text, Services.HaXMode.IsOn));
                    if (generated is null) { _boxViewModel.Status = "Could not build a legal mon from that set."; return; }
                    Deposit(generated);
                }
                finally { overlay.Close(); }
                return;
            }
            case "Scan a .pk QR":
            {
                var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
                var received = await Services.QrEntityService.ScanAsync(_hostGrid, engine);
                if (received is null) return;
                Deposit(new GeneratedEntity(received.Data, received.Info));
                _boxViewModel.Status = "Received a Pokémon over QR.";
                return;
            }
            case "Import .pk file":
            {
                var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
                var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
                if (picker is null || access is null) return;
                var documents = await picker.PickManyAsync();
                if (documents.Count == 0) return;
                var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
                // All or none: every file is read first, then the bank takes them in one write.
                var deposits = new List<BankDeposit>(documents.Count);
                foreach (var document in documents)
                {
                    var bytes = (await access.ReadAsync(document.DocumentId)).ToArray();
                    if (engine.TryDescribeEntity(bytes, document.DisplayName, document.DisplayName) is not { } parsed) // the extension names the format
                    {
                        _boxViewModel.Status = $"Nothing was deposited: {document.DisplayName} is not a recognizable Pokémon.";
                        return;
                    }
                    deposits.Add(new BankDeposit(bytes, parsed));
                }
                try
                {
                    _bank.AddMany(deposits);
                    _boxViewModel.Status = $"Deposited {deposits.Count} Pokémon into the bank.";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _boxViewModel.Status = $"Nothing was deposited: {error.Message}";
                }
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
        // A send leaves the bank (a move); keeping the original is a copy, which Hardcore refuses
        // here as well - the transfer service writes other games without the save-side guard.
        if (keepOriginal && await DeniedAsync(SaveAction.Duplicate)) return;
        var services = IPlatformApplication.Current?.Services;
        var transfer = services?.GetService<Services.TransferService>();
        if (transfer is null) return;

        var picked = await PickDestinationAsync($"{entry.Info.Nickname} → pick the destination");
        if (picked is not { } destination) return;

        var bytes = _bank.GetData(entry.Id);
        var nickname = entry.Info.Nickname;
        var format = entry.Info.Format;

        if (destination.Other is null)
        {
            // The connected save goes through the live session, first empty slot of any box.
            var landing = _boxViewModel.Save?.Slots.FirstOrDefault(s => s.Species is null);
            if (landing is null)
            {
                _boxViewModel.Status = "No empty slot in the connected game.";
                return;
            }
            var connectedPreview = transfer.PreviewIntoConnected(bytes, nickname, landing.Box, landing.Slot, format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, connectedPreview, nickname, "the connected game")) return;
            var ok = await _boxViewModel.RunMutationAsync(session =>
                Services.TransferService.TryImport(session, landing.Box, landing.Slot, bytes, out var refusal, format)
                    ? new GenerationOutcome(true, $"{nickname} joined the game (box {landing.Box + 1}).")
                    : new GenerationOutcome(false, refusal ?? Services.TransferService.Refusal(bytes, nickname, session.Snapshot, "this game", format)), landing.Slot,
                action: keepOriginal ? SaveAction.Duplicate : SaveAction.Move);
            if (!ok) return;
        }
        else
        {
            var target = destination.Other;
            var preview = await transfer.PreviewAsync(bytes, nickname, target, format: format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, preview, nickname, target.GameLabel)) return;
            var outcome = await transfer.SendToGameAsync(bytes, nickname, target, format: format);
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
            var name = $"{entry.Info.Species:000} - {entry.Info.Nickname}{BankEntryFiles.ExtensionFor(entry.Info)}";
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
    // The box header's height in the mockup: its unit is its height over this.
    private const float HeaderDesignHeight = 72f;

    private void PaintBoxBar(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var unit = args.Info.Height / HeaderDesignHeight;
        using var font = new SKFont(BoxBrowserPage.PixelTypeface(), 34f * unit);
        StoragePaint.BoxHeader(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), _boxTitle, font,
            canPrev: _boxIndex > 0, canNext: _boxIndex < _bank.BoxCount - 1, unit);
    }

    private void TapBoxBar(double x)
    {
        var cap = Math.Max(44, StoragePaint.BannerRect(new SKRect(0, 0, (float)_boxBar.Width, (float)_boxBar.Height),
            (float)_boxBar.Height / HeaderDesignHeight).Left);
        if (x < cap) ChangeBox(-1);
        else if (x > _boxBar.Width - cap) ChangeBox(1);
        else _ = OpenBoxOverviewAsync();
    }

    /// <summary>
    /// The box in the storage look (<see cref="BoxGridRenderer.PaintScene"/>): Pokémon standing
    /// on their rows, the light-pool cursor, the Pokémon in hand; the box's own Bank wallpaper
    /// under a navy veil.
    /// </summary>
    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var slots = new SlotSummary[Columns * Rows];
        for (var index = 0; index < slots.Length; index++)
            slots[index] = _boxEntries.TryGetValue(index, out var entry) ? Slot(entry, index) : new SlotSummary(_boxIndex, index, null, null, false, true);
        var carried = CarriedEntry();
        var origin = _boxEntries.Values.FirstOrDefault(e => e.Id == _carryId) is { } source ? source.Slot : (int?)null;
        var scene = new BoxGridRenderer.StorageScene(slots, _selectedSlot, origin,
            carried is null ? null : Slot(carried, _selectedSlot), Marking: false,
            index => _marked.Contains((_boxIndex, index)), _ => null, _boxIndex);
        BoxGridRenderer.PaintScene(args.Surface.Canvas, args.Info, scene, _sprites, _frame.Request, _hand, (c, well, unit) =>
        {
            // A wallpaper picture goes through the save boxes' painter (frame cropped, the
            // player's Box background style); a flat colour tints the well.
            if (BankBoxArt.Art(_boxIndex, _frame.Request) is { } art)
            {
                using var image = SKImage.FromBitmap(art.Art);
                StoragePaint.Wallpaper(c, well, image, art.Average, BoxBackground.Style, unit);
                return;
            }
            var inner = SKRect.Inflate(well, -3 * unit, -3 * unit);
            using var tint = new SKPaint { Color = PksmPaint.Mix(BankBoxArt.Tone(_boxIndex), StoragePaint.Well, 0.7f), IsAntialias = true };
            c.DrawRoundRect(inner, 25 * unit, 25 * unit, tint);
        });
        _panel.InvalidateSurface();
    }

    /// <summary>A Bank entry as a storage slot (what the box painter draws).</summary>
    private SlotSummary Slot(BankEntry entry, int slot) =>
        new(_boxIndex, slot, entry.Info.Species, entry.Info.Nickname, entry.Info.Shiny, true, entry.Info.Form,
            HeldItem: _facts.HeldItem(entry), Traits: entry.Info.Traits ?? default);

    private readonly CarryHand _hand = new();

    // The entry in hand, looked up once per carry: it travels across boxes, out of _boxEntries.
    private BankEntry? _carriedEntry;

    private BankEntry? CarriedEntry()
    {
        if (_carryId is not { } id) return null;
        if (_carriedEntry?.Id != id) _carriedEntry = _bank.GetAll().FirstOrDefault(e => e.Id == id);
        return _carriedEntry;
    }

    // ── The side panel ─────────────────────────────────────────────────────

    private Guid? _factFor;
    private int _factPick;
    private IDispatcherTimer? _factTimer;

    // The panel's full summary of the selected entry, decoded in the background.
    private MonSummary? _panelSummary;
    private Guid? _panelFor;
    private CancellationTokenSource? _panelCancel;

    /// <summary>Drops the panel's summary so the next preview decodes the entry again (after an in-place edit).</summary>
    private void ForgetPanelSummary()
    {
        _panelCancel?.Cancel();
        _panelFor = null;
        _panelSummary = null;
        _carriedEntry = null;
    }

    /// <summary>Decodes the selected entry's summary for the side panel (debounced, latest wins).</summary>
    private void RequestPanelSummary()
    {
        var entry = EntryAt(_selectedSlot);
        if (entry?.Id == _panelFor) return;
        _panelCancel?.Cancel();
        _panelFor = entry?.Id;
        _panelSummary = null;
        if (entry is null) return;
        var services = IPlatformApplication.Current?.Services;
        if (services?.GetService<ISaveEngine>() is not { } engine || services.GetService<IMonSummaryService>() is not { } summaries) return;
        var cancel = _panelCancel = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(40, cancel.Token);
                var summary = await SummaryLoaders.FromBank(_bank, engine, summaries, entry, analyzeLegality: false);
                if (cancel.IsCancellationRequested) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (cancel.IsCancellationRequested || _panelFor != entry.Id) return;
                    _panelSummary = summary;
                    _canvas.InvalidateSurface();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            {
                // Unreadable entry: the panel keeps the facts the index already has.
            }
        });
    }

    /// <summary>
    /// The selected Pokémon at a glance. The Bank keeps its own identity inside the panel
    /// look: the name and species tabs and a soft wash take the Pokémon's first type colour,
    /// its HOME render stands in a lit card beside the facts, and one of its Pokédex entries
    /// (a different one every visit and every little while) sits in a card along the bottom.
    /// Rows that do not fit are left out rather than squeezed.
    /// </summary>
    private void PaintPanel(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = new SKRect(0, 0, args.Info.Width, args.Info.Height);
        var unit = args.Info.Width / EditorPaint.DesignWidth;
        StoragePaint.WellPanel(canvas, bounds, unit);
        const float text = 30, rowHeight = 60;
        var width = EditorPaint.DesignWidth;
        var height = args.Info.Height / unit;
        var face = BoxBrowserPage.PixelTypeface();
        canvas.Save();
        canvas.Scale(unit);

        if (EntryAt(_selectedSlot) is not { } entry)
        {
            var count = _boxEntries.Count;
            EditorPaint.PaintSection(canvas, new SKRect(0, 40, width, 104), _boxTitle, face, 1);
            SummaryInk.Draw(canvas, count == 0 ? "This box is empty." : $"An empty space · {count} / {IBankService.SlotsPerBox} in this box",
                width / 2, SummaryInk.Center(150, text), text, EditorPaint.ExpInk, align: SKTextAlign.Center);
            canvas.Restore();
            return;
        }

        var info = entry.Info;
        var summary = _panelSummary is { } loaded && _panelFor == entry.Id ? loaded : null;
        var species = summary?.SpeciesName
            ?? ((uint)info.Species < (uint)_data.SpeciesNames.Count ? _data.SpeciesNames[info.Species] : $"#{info.Species}");
        var name = summary?.DisplayName ?? (string.IsNullOrWhiteSpace(info.Nickname) ? species : info.Nickname);
        SKColor? accent = summary?.Types is { Count: > 0 } types ? InfoKit.TypeColor(types[0]).ToSKColor() : null;

        // A soft wash of the type colour down from the top.
        if (accent is { } wash)
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height * 0.75f),
                [wash.WithAlpha(70), wash.WithAlpha(0)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(3, 3, width - 3, height - 3), 25, 25, paint);
        }
        var from = string.IsNullOrWhiteSpace(info.SourceName) ? $"Gen {info.Generation}" : $"From {info.SourceName}";
        EditorPaint.PaintHeader(canvas, new SKRect(0, 0, width, EditorPaint.DesignHeaderHeight),
            new EditorPaint.Header(name, info.Level, info.Shiny, summary?.Gender ?? -1, species, from, accent), face, 1);

        // The Pokédex entry card along the bottom, as tall as its text.
        const float dexText = 28;
        var (dexLines, version) = DexEntryLines(entry.Id, info.Species, dexText, width - 100);
        var dexHeight = dexLines.Count == 0 ? 0 : 40 + dexLines.Count * SummaryInk.Leading(dexText);
        var dexTop = height - 24 - dexHeight;

        // The HOME render in a lit card on the left; the facts on the right.
        var top = EditorPaint.DesignHeaderHeight + 18;
        var bottom = (dexLines.Count > 0 ? dexTop : height) - 18;
        var portrait = new SKRect(24, top, 280, Math.Min(bottom, top + 330));
        PaintPortrait(canvas, portrait, info.Look, accent);

        var rows = new List<(string Label, Action<float> Value)>();
        const float labelX = 304, valueX = 440;
        void Text(string label, string? value) => rows.Add((label, cy =>
            SummaryInk.Draw(canvas, SummaryInk.Fit(value ?? "None", text, width - valueX - 24), valueX, SummaryInk.Center(cy, text), text, EditorPaint.Value)));
        Text($"No.{info.Species:000}", DexFacts.Default.Genus(info.Species) ?? species);
        if (summary is not null)
        {
            if (summary.Types.Count > 0)
                rows.Add(("Type", cy =>
                {
                    var x = valueX;
                    foreach (var type in summary.Types)
                    {
                        TypePlates.Paint(canvas, new SKRect(x, cy - 19, x + 124, cy + 19), type);
                        x += 134;
                    }
                }));
            Text("Nature", summary.NatureName);
            Text("Ability", summary.AbilityName);
            Text("Item", summary.HeldItem > 0 ? summary.HeldItemName : "None");
            Text("OT", summary.TrainerId is { } tid ? $"{summary.OriginalTrainer} · {tid:00000}" : summary.OriginalTrainer);
            rows.Add(("Ball", cy =>
            {
                var x = valueX;
                if (_sprites.GetBall(summary.Ball) is { } ball)
                {
                    using var ballImage = SKImage.FromBitmap(ball);
                    canvas.DrawImage(ballImage, SKRect.Create(x, cy - 20, 40, 40), BoxGridRenderer.SpriteSampling);
                    x += 52;
                }
                else _sprites.WarmBall(summary.Ball, _frame.Request);
                SummaryInk.Draw(canvas, SummaryInk.Fit(summary.BallName, text, width - x - 24), x, SummaryInk.Center(cy, text), text, EditorPaint.Value);
            }));
        }
        var y = top;
        for (var i = 0; i < rows.Count && y + rowHeight <= bottom; i++)
        {
            using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0) }) canvas.DrawRect(new SKRect(292, y, width - 3, y + rowHeight), band);
            SummaryInk.Draw(canvas, rows[i].Label, labelX, SummaryInk.Center(y + rowHeight / 2, text), text, EditorPaint.Label);
            rows[i].Value(y + rowHeight / 2);
            y += rowHeight;
        }

        if (dexLines.Count > 0)
        {
            var card = new SKRect(24, dexTop, width - 24, height - 24);
            using (var fill = new SKPaint { Color = ColorTheme.Current.DexCard.WithAlpha(235), IsAntialias = true }) canvas.DrawRoundRect(card, 18, 18, fill);
            using (var rim = new SKPaint { Color = (accent is { } a ? PksmPaint.Mix(a, SKColors.White, 0.2f) : EditorPaint.Cyan).WithAlpha(170), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
                canvas.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), 18, 18, rim);
            PaintDexEntry(canvas, SKRect.Inflate(card, -26, -20), dexLines, version, dexText);
        }
        canvas.Restore();
    }

    /// <summary>
    /// The HOME render on a card lit in the type colour; the pixel sprite stands in only when
    /// no render will come, and while it loads the card stays empty for a blink.
    /// </summary>
    private void PaintPortrait(SKCanvas canvas, SKRect card, SpriteLook look, SKColor? accent)
    {
        var glow = accent ?? EditorPaint.Cyan;
        using (var shader = SKShader.CreateRadialGradient(new SKPoint(card.MidX, card.MidY + card.Height * 0.12f), card.Width * 0.75f,
                   [PksmPaint.Mix(glow, SKColors.White, 0.1f).WithAlpha(120), PksmPaint.Mix(glow, StoragePaint.Well, 0.75f).WithAlpha(230)], SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader, IsAntialias = true })
            canvas.DrawRoundRect(card, 22, 22, fill);
        using (var rim = new SKPaint { Color = PksmPaint.Mix(glow, SKColors.White, 0.3f).WithAlpha(200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
            canvas.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), 22, 22, rim);

        var home = _sprites.GetHome(look);
        if (home is null) _sprites.WarmHome(look, _frame.Request);
        var standIn = home is null && _sprites.HomeUnavailable(look);
        var picture = home ?? (standIn ? _sprites.GetSprite(look) : null);
        if (standIn && picture is null) _sprites.Warm(look, _frame.Request);
        if (picture is null) return;
        var side = Math.Min(card.Width, card.Height) - 24;
        var scale = side / Math.Max(picture.Width, picture.Height);
        var w = picture.Width * scale;
        var h = picture.Height * scale;
        using var image = SKImage.FromBitmap(picture);
        canvas.DrawImage(image, new SKRect(card.MidX - w / 2, card.MidY - h / 2, card.MidX + w / 2, card.MidY + h / 2),
            home is null ? BoxGridRenderer.SpriteSampling : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private const int DexMaxLines = 3;

    /// <summary>
    /// The Pokédex entry to show, wrapped, and its game. Only entries that fit whole in
    /// <see cref="DexMaxLines"/> lines (the game's name included) are picked; a species whose
    /// entries are all longer shows its shortest, cut at the last line.
    /// </summary>
    private (List<string> Lines, string Version) DexEntryLines(Guid entryId, int species, float size, float width)
    {
        var entries = DexFacts.Default.Entries(species);
        if (entries.Count == 0) return ([], "");
        List<string> Wrap(DexEntry fact)
        {
            var lines = SummaryInk.Wrap(fact.Text, size, width);
            var credit = SummaryInk.Width($"Pokémon {fact.Version}", size) + 16;
            // The game's name rides the last line when there is room, else it takes its own.
            if (lines.Count == 0 || SummaryInk.Width(lines[^1], size) + credit > width) lines.Add("");
            return lines;
        }
        var wrapped = entries.Select(Wrap).ToList();
        var fitting = Enumerable.Range(0, entries.Count).Where(i => wrapped[i].Count <= DexMaxLines).ToList();
        if (_factFor != entryId)
        {
            _factFor = entryId;
            _factPick = Random.Shared.Next(Math.Max(1, fitting.Count));
        }
        if (fitting.Count == 0)
        {
            var shortest = Enumerable.Range(0, entries.Count).MinBy(i => wrapped[i].Count);
            var cut = wrapped[shortest].Take(DexMaxLines).ToList();
            cut[^1] = SummaryInk.Fit(cut[^1] + " …", size, width - SummaryInk.Width($"Pokémon {entries[shortest].Version}", size) - 16);
            return (cut, entries[shortest].Version);
        }
        var pick = fitting[_factPick % fitting.Count];
        return (wrapped[pick], entries[pick].Version);
    }

    /// <summary>The entry's text in bright ink, its game's name closing the last line.</summary>
    private static void PaintDexEntry(SKCanvas canvas, SKRect area, List<string> lines, string version, float size)
    {
        var y = area.Top + size;
        foreach (var line in lines)
        {
            SummaryInk.Draw(canvas, line, area.Left, y, size, Pksm.Ink);
            y += SummaryInk.Leading(size);
        }
        SummaryInk.Draw(canvas, $"Pokémon {version}", area.Right, y - SummaryInk.Leading(size), size, EditorPaint.Cyan, align: SKTextAlign.Right);
    }

    private static SKColor ToSk(Color color) =>
        new((byte)(color.Red * 255), (byte)(color.Green * 255), (byte)(color.Blue * 255));

    /// <summary>A new Pokédex entry every little while, like flipping through the dex.</summary>
    private void StartFactRotation()
    {
        _factTimer ??= Dispatcher.CreateTimer();
        _factTimer.Interval = TimeSpan.FromSeconds(14);
        _factTimer.Tick -= RotateFact;
        _factTimer.Tick += RotateFact;
        _factTimer.Start();
    }

    private void RotateFact(object? sender, EventArgs e)
    {
        if (EntryAt(_selectedSlot) is null) return;
        _factPick++;
        _panel.InvalidateSurface();
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;

        var slot = BoxGridRenderer.StorageSlotFromTouch(_canvas.CanvasSize, args.Location);
        if (slot < 0) return;

        var wasSelected = _selectedSlot == slot;
        _selectedSlot = slot;
        UpdatePreview();
        UpdateStatus();

        if (_carryId is not null) { _ = ConfirmAsync(); return; }
        if (EntryAt(slot) is null) { _ = OpenEmptySlotMenuAsync(); return; }
        if (wasSelected) _ = ConfirmAsync(); // second tap grabs
        _canvas.InvalidateSurface();
    }
}
