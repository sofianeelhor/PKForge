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

namespace PKForge.App.Views;

/// <summary>
/// The Bank: the app's own cross-game vault in the PKSM storage world. Each box page
/// is a saturated wallpaper flat with the dot lattice;
/// the second screen shows the highlighted mon. A grabs/places, START opens the
/// mon menu, L/R turn pages. Y marks slots for the organizer, X opens the bank's
/// actions (bulk moves, releases, sorting), and the readout under the grid carries the
/// mark count. Every write is an index write: the bank never touches a save itself.
/// </summary>
public sealed class BankPage : ContentPage, IPadHandler
{
    private const int Columns = 6;
    private const int Rows = 5;

    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly BoxBrowserViewModel _boxViewModel; // for send-to-game and shared status
    private readonly BankFacts _facts;
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly Grid _hostGrid;
    private Label _pageLabel = null!;
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

        var exportArchive = Kit.Capsule("Export", UiTokens.Green);
        exportArchive.Clicked += (_, _) => _ = ExportArchiveAsync();

        var importArchive = Kit.Capsule("Import", UiTokens.Indigo);
        importArchive.Clicked += (_, _) => _ = ImportArchiveAsync();

        var search = Kit.Capsule("Search", UiTokens.MenuBlue);
        search.Clicked += (_, _) => _ = OpenSearchAsync();

        var livingDex = Kit.Capsule("Living dex", UiTokens.MenuBlue);
        livingDex.Clicked += (_, _) => _ = OpenLivingDexAsync();

        var strip = new Grid
        {
            Padding = new Thickness(12, 6, 12, 0),
            ColumnSpacing = 8,
            ColumnDefinitions =
            [
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
            ],
            Children = { _pageLabel, previous, next, addBox, exportArchive, importArchive, search, livingDex },
        };
        Grid.SetColumn(previous, 1);
        Grid.SetColumn(next, 2);
        Grid.SetColumn(addBox, 3);
        Grid.SetColumn(exportArchive, 4);
        Grid.SetColumn(importArchive, 5);
        Grid.SetColumn(search, 6);
        Grid.SetColumn(livingDex, 7);

        var screen = Kit.LcdPanel(_canvas, padding: 4);
        var content = new Grid { Padding = new Thickness(12, 8, 12, 10), Children = { screen } };
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
            ("A", "Grab", null),
            ("Y", "Mark", ToggleCursorMark),
            ("X", "Actions", () => _ = ShowBankActionsAsync()),
            ("B", "Back", null),
            ("LR", "Box", null),
            // SELECT (-): a dual-screen device turns the inspector's page below; a single
            // screen opens the full-screen summary instead.
            ("-", HasSecondScreen ? "Page" : "Summary", () => OnPadButton(PadButton.Select)),
            ("+", "Menu", () => _ = OpenCursorMenuAsync()));

        var root = new Grid
        {
            RowDefinitions =
            [
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto),
            ],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip("Bank", "Open"), strip, bodyHost, statusHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(strip, 2);
        Grid.SetRow(bodyHost, 3);
        Grid.SetRow(statusHost, 4);
        Grid.SetRow(footer, 5);

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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
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
            new PadOption("Folder of .pk files", IconPath: "folder"),
            new PadOption("PKSM bank for the 3DS (.bnk)", IconPath: "bank"));
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
            new PadOption("PKSM bank, dumps zip or .pk files", IconPath: "bank"),
            new PadOption("Folder of .pk files", IconPath: "folder"));
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


    private void UpdatePageLabel() => _pageLabel.Text = PksmTransferFlow.BoxNames.Get(_boxIndex) is { } name
        ? $"{_boxIndex + 1:00} / {_bank.BoxCount:00} · {name}"
        : $"{_boxIndex + 1:00} / {_bank.BoxCount:00}";

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
        var options = new List<PadOption> { new("Summary", IconPath: "info") };
        if (Allowed(SaveAction.EditMon))
        {
            options.Add(new PadOption("Edit", IconPath: "editor"));
            options.Add(new PadOption("Evolve…", IconPath: "evolve"));
        }
        options.Add(new PadOption("Send to Poképark", IconPath: "park"));
        if (Allowed(SaveAction.Duplicate)) options.Add(new PadOption("Duplicate", IconPath: "copy"));
        options.Add(new PadOption("Send to game…", IconPath: "send"));
        if (Allowed(SaveAction.Duplicate)) options.Add(new PadOption("Copy to game…", IconPath: "copy"));
        options.Add(new PadOption("Move (carry)", IconPath: "move"));
        options.Add(new PadOption("Export .pk file", IconPath: "export"));
        options.Add(new PadOption("Release from bank", IconPath: "release"));
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

        var options = new List<PadOption>();
        if (selection.Count > 0)
        {
            options.Add(new PadOption("Move to another box…", IconPath: "move"));
            options.Add(new PadOption("Move to a position…", IconPath: "move"));
            if (Allowed(SaveAction.Duplicate))
                options.Add(new PadOption(selection.Count == 1 ? "Duplicate" : "Duplicate all", IconPath: "copy"));
            options.Add(new PadOption(selection.Count == 1 ? "Export .pk file" : "Export .pk files", IconPath: "export"));
            options.Add(new PadOption("Export as a PKSM bank", IconPath: "export"));
            if (targets.ConnectedLabel is not null || targets.Detected.Length > 0)
                options.Add(new PadOption("Send to a game…", IconPath: "send"));
            options.Add(new PadOption("Release from bank…", IconPath: "release"));
        }
        if (_anchor is not null && _anchor != (_boxIndex, _selectedSlot))
            options.Add(new PadOption("Mark range to here", IconPath: "range"));
        options.Add(new PadOption("Mark everything in this box", IconPath: "selectall"));
        options.Add(new PadOption("Invert marks in this box", IconPath: "invert"));
        options.Add(new PadOption("Mark the whole bank", IconPath: "selectall"));
        options.Add(new PadOption("Mark by filter…", IconPath: "filter"));
        if (_marked.Count > 0) options.Add(new PadOption("Clear marks", IconPath: "deselect"));
        options.Add(new PadOption($"Sort box {_boxIndex + 1:00}…", IconPath: "sort"));
        options.Add(new PadOption("Sort the whole bank…", IconPath: "sort"));
        if (_marked.Count > 1) options.Add(new PadOption("Sort the marked into boxes…", IconPath: "sort"));
        options.Add(new PadOption("Compact the bank (close gaps)", IconPath: "compact"));

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
        var cloned = 0;
        foreach (var entry in selection)
        {
            _bank.Add(_bank.GetData(entry.Id), entry.Info);
            cloned++;
        }
        FinishOrganizer(selection.Count == 1
            ? $"{selection[0].Info.Nickname} cloned in the bank."
            : $"{cloned} Pokémon cloned in the bank.");
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
    /// Sends the whole selection to one game. The connected save takes them into its empty
    /// slots in a single safe write; any other detected game receives them one at a time
    /// through the transfer service, previewed once. Only mons that arrived leave the bank;
    /// every write goes through the same validate → backup → atomic path as the boxes.
    /// </summary>
    private async Task SendSelectionToGameAsync(List<BankEntry> selection)
    {
        var services = IPlatformApplication.Current?.Services;
        var transfer = services?.GetService<Services.TransferService>();
        if (transfer is null) return;

        var picked = await PickDestinationAsync($"{selection.Count} selected → pick the destination");
        if (picked is not { } destinationPick) return;

        var sent = new List<Guid>();
        var destination = destinationPick.Label;
        if (destinationPick.Other is null)
        {
            var rooms = _boxViewModel.Save?.Slots.Where(s => s.Species is null)
                .OrderBy(s => s.Box).ThenBy(s => s.Slot).ToList();
            if (rooms is not { Count: > 0 })
            {
                _boxViewModel.Status = "No empty slot in the connected game.";
                return;
            }
            var open = rooms.ToArray();
            // Same trust step as a game-to-game send: the first mon's diff and warnings
            // (a backwards conversion lists its compromises) stand for the batch.
            var firstPick = selection[0];
            var connectedPreview = transfer.PreviewIntoConnected(_bank.GetData(firstPick.Id), firstPick.Info.Nickname, open[0].Box, open[0].Slot, firstPick.Info.Format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, connectedPreview,
                    selection.Count == 1 ? firstPick.Info.Nickname : $"{selection.Count} Pokémon", "the connected game"))
                return;
            var ok = await _boxViewModel.RunMutationAsync(session =>
            {
                var placed = 0;
                foreach (var entry in selection)
                {
                    if (placed >= open.Length) break;
                    var room = open[placed];
                    if (!Services.TransferService.TryImport(session, room.Box, room.Slot, _bank.GetData(entry.Id), out _, entry.Info.Format)) continue;
                    sent.Add(entry.Id);
                    placed++;
                }
                return placed == 0
                    ? new GenerationOutcome(false, "None of the selection can enter this game's format.")
                    : new GenerationOutcome(true, $"{placed} of {selection.Count} joined the game.");
            }, Math.Max(0, _selectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!ok) return;
            destination = "the connected game";
        }
        else
        {
            var target = destinationPick.Other;
            var first = selection[0];
            var preview = await transfer.PreviewAsync(_bank.GetData(first.Id), first.Info.Nickname, target, format: first.Info.Format);
            if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, preview,
                    selection.Count == 1 ? first.Info.Nickname : $"{selection.Count} Pokémon", target.GameLabel))
                return;
            string? stopped = null;
            foreach (var entry in selection)
            {
                try
                {
                    var outcome = await transfer.SendToGameAsync(_bank.GetData(entry.Id), entry.Info.Nickname, target, format: entry.Info.Format);
                    if (outcome.Success) sent.Add(entry.Id);
                }
                catch (Exception error)
                {
                    // Every mon already written to the game still leaves the bank below: no duplicates.
                    stopped = error.Message;
                    break;
                }
            }
            if (sent.Count == 0)
            {
                _boxViewModel.Status = stopped is null
                    ? $"None of the selection could enter {target.GameLabel}'s format."
                    : $"Aborted: {stopped}";
                return;
            }
            if (stopped is not null)
            {
                _bank.RemoveMany(sent);
                FinishOrganizer($"{sent.Count} of {selection.Count} Pokémon left the bank for {destination}, then it stopped: {stopped}");
                return;
            }
        }

        _bank.RemoveMany(sent);
        FinishOrganizer($"{sent.Count} of {selection.Count} Pokémon left the bank for {destination}.");
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
            new PadOption("Create a Pokémon", IconPath: "create"),
            new PadOption("Paste a Showdown set", IconPath: "script"),
            new PadOption("Import .pk file", IconPath: "import"),
            new PadOption("Scan a .pk QR", IconPath: "scan"));
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
                var count = 0;
                var engine = IPlatformApplication.Current!.Services.GetRequiredService<ISaveEngine>();
                foreach (var document in documents)
                {
                    var bytes = (await access.ReadAsync(document.DocumentId)).ToArray();
                    var parsed = engine.TryDescribeEntity(bytes, document.DisplayName, document.DisplayName); // the extension names the format
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
            var bitmap = _sprites.GetSprite(entry.Info.Look);
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
                _sprites.Warm(entry.Info.Look, _frame.Request);
            }
            if (isCarried && index != _selectedSlot)
                PksmPaint.CarryGhost(canvas, rect);
            if (entry.Info.Shiny)
                BoxGridRenderer.DrawSparkle(canvas, rect.Right - rect.Width * 0.14f, rect.Top + rect.Height * 0.16f,
                    Math.Min(rect.Width, rect.Height) * 0.09f, BoxGridRenderer.SparklePaint);
            if (!(isCarried && index != _selectedSlot) && _facts.HeldItem(entry) != 0)
                BoxGridRenderer.DrawHeldItemBadge(canvas, rect);
            if (_marked.Contains((_boxIndex, index)))
                PksmPaint.MarkBadge(canvas, rect);
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
        UpdateStatus();

        if (_carryId is not null) { _ = ConfirmAsync(); return; }
        if (EntryAt(slot) is null) { _ = OpenEmptySlotMenuAsync(); return; }
        if (wasSelected) _ = ConfirmAsync(); // second tap grabs
        _canvas.InvalidateSurface();
    }
}
