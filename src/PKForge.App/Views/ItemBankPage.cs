using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Item Bank as its own place (<see cref="ItemBankPaint"/>): the banked items on the left,
/// any shelf game's bag on the right, and items sent either way with an amount. The game is
/// read fresh for every move and written through the transfers' safe path (validate, restore
/// point, atomic write); the bank goes back to what it held if that write fails. The rules are
/// the bag's own (<see cref="ItemBankService"/>, <see cref="ItemBankTransfer"/>).
/// </summary>
public sealed class ItemBankPage : ContentPage, IPadHandler
{
    private readonly ItemBankStore? _store;
    private readonly TransferService? _transfer;
    private readonly SavePickerViewModel? _picker;
    private readonly ISaveSessionService? _sessions;
    private readonly BoxBrowserViewModel? _boxes;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly Grid _hostGrid;

    private ItemBank _bank = ItemBank.Empty;
    private DetectedSave? _target;
    private string? _targetTitle;
    private ISaveEngineSession? _view;
    private IReadOnlyDictionary<string, ItemBankPlace> _places = new Dictionary<string, ItemBankPlace>();
    private string? _family;
    private IReadOnlyList<string> _names = [];
    private IReadOnlyList<BagPouch> _bag = [];

    private List<string?> _tabs = [null];
    private int _tab;
    private IReadOnlyList<ItemBankRow> _bankRows = [];
    private List<BagRow> _bagRows = [];
    private int _bankCursor, _bankScroll, _bagCursor, _bagScroll;
    private bool _bankFocused = true;
    private bool _menu;
    private int _action;
    private int _amount = 1;
    private string _query = "";
    private ItemBankSort _sort = ItemBankSort.Name;
    private string _status = "";
    private bool _busy;

    private ItemBankHits? _hits;
    private SKPoint? _pressedAt;
    private bool _pressedOnBank;
    private float _dragRows;
    private bool _dragged;
    private readonly Dictionary<string, SKImage?> _art = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artLoading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKImage?> _icons = new(StringComparer.Ordinal);

    // The generator (−, or the "Make items" plate): one game family's catalogue, searched by pouch.
    private bool _making;
    private ItemBankFamily? _makeFamily;
    private IReadOnlyList<ItemBankCatalogItem> _catalog = [];
    private List<string?> _makeTabs = [null];
    private int _makeTab;
    private string _makeQuery = "";
    private IReadOnlyList<ItemBankCatalogItem> _makeRows = [];
    private int _makeCursor, _makeScroll;
    private bool _makeMenu;
    private int _makeAmount = 1;
    private string? _lastMade;

    /// <summary>One item of the chosen game's bag, with why it stays when it may not leave.</summary>
    private sealed record BagRow(string Pouch, int Id, int Count, string Name, string? Kept);

    public ItemBankPage()
    {
        var services = IPlatformApplication.Current?.Services;
        _store = services?.GetService<ItemBankStore>();
        _transfer = services?.GetService<TransferService>();
        _picker = services?.GetService<SavePickerViewModel>();
        _sessions = services?.GetService<ISaveSessionService>();
        _boxes = services?.GetService<BoxBrowserViewModel>();
        NavigationPage.SetHasNavigationBar(this, false);

        _canvas.PaintSurface += PaintGuard.Wrap("ItemBankPage.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("A", "Choose", () => OnPadButton(PadButton.A)),
            ("<>", "Pouch", () => OnPadButton(PadButton.Right)),
            ("LR", "Side", () => OnPadButton(_bankFocused ? PadButton.R : PadButton.L)),
            ("Y", "Search", () => OnPadButton(PadButton.Y)),
            ("X", "Game", () => OnPadButton(PadButton.X)),
            ("+", "Sort", () => OnPadButton(PadButton.Start)),
            ("-", "Make items", () => OnPadButton(PadButton.Select)),
            ("B", "Back", () => OnPadButton(PadButton.B)));
        var body = new Grid
        {
            Padding = new Thickness(14, 12, 14, 8),
            Children = { _canvas },
        };
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), body } };
        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip("Item Bank", "Open"), bodyHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(footer, 3);
        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;
    }

    private bool _started;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        MusicMenu.Enter(MusicPlace.Bank);
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        ReadBank();
        if (!_started)
        {
            _started = true;
            // The game open in the app is the natural partner; any other is one X away.
            if (_sessions?.Current is { } open && Shelf().FirstOrDefault(s => s.DocumentId == open.Document.DocumentId) is { } connected)
                _ = ChooseAsync(connected);
        }
        Rebuild();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        MusicMenu.Leave(MusicPlace.Bank);
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        // A new page is pushed on each visit: once this one is popped (not merely covered),
        // its read-only game session and decoded art go with it.
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove) Release();
    }

    private void Release()
    {
        _view?.Dispose();
        _view = null;
        foreach (var image in _art.Values) image?.Dispose();
        foreach (var image in _icons.Values) image?.Dispose();
        _art.Clear();
        _icons.Clear();
    }

    protected override bool OnBackButtonPressed()
    {
        OnPadButton(PadButton.B);
        return true;
    }

    private IReadOnlyList<DetectedSave> Shelf() => _picker is null ? [] : SaveTargets.LiveFirst(_picker.Saves);

    // ── Reading ──────────────────────────────────────────────────────────────

    private ItemBank ReadStore() => _store?.Read() ?? ItemBank.Empty;

    private void ReadBank()
    {
        try { _bank = ReadStore(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _status = $"The Item Bank could not be read. {Domain.ErrorText.Plain(error)}";
        }
    }

    /// <summary>Opens the chosen game fresh from its file and reads its bag for the right side.</summary>
    private async Task ChooseAsync(DetectedSave target)
    {
        if (_transfer is null) return;
        var keep = _tabs.ElementAtOrDefault(_tab);
        try
        {
            var session = await _transfer.OpenGameAsync(target);
            if (!ItemBankService.IsSupported(session))
            {
                session.Dispose();
                Report($"{target.GameLabel}: the Item Bank cannot read this game's bag.");
                return;
            }
            _view?.Dispose();
            _view = session;
            _target = target;
            _targetTitle = target.GameLabel;
            LoadView();
            // The maker follows the game chosen here: items made for another family would not
            // go into it. Picked again from this game the next time the maker opens.
            if (_makeFamily is { } made && ItemBankCatalog.FamilyOf(session) is { } family && family.Id != made.Id) _makeFamily = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or NotSupportedException or InvalidDataException or ArgumentException)
        {
            Report($"{target.GameLabel} could not be opened. {Domain.ErrorText.Plain(error)}");
            return;
        }
        _tab = Math.Max(0, _tabs.IndexOf(keep));
        _bagCursor = 0;
        _bagScroll = 0;
        _status = "";
        Rebuild();
    }

    private void LoadView()
    {
        if (_view is not { } session) return;
        _bag = session.GetBag();
        _names = session.GetItemNames();
        _places = ItemBankService.Places(session);
        _family = ItemBankService.Family(session);
        _tabs = [null, .. _bag.Select(p => (string?)p.Name)];
    }

    /// <summary>Re-reads the game after a write, so the right side shows what the file holds.</summary>
    private async Task ReloadAsync()
    {
        ReadBank();
        if (_target is { } target && _transfer is not null)
        {
            try
            {
                var session = await _transfer.OpenGameAsync(target);
                _view?.Dispose();
                _view = session;
                LoadView();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                _status = $"{target.GameLabel} could not be read again. {Domain.ErrorText.Plain(error)}";
            }
        }
        Rebuild();
    }

    private string ItemName(int id) => id < _names.Count && _names[id].Length > 0 ? _names[id] : $"#{id}";

    /// <summary>The lists for the tab, search and sort; cursors stay in range.</summary>
    private void Rebuild()
    {
        if (_tab >= _tabs.Count) _tab = 0;
        var pouch = _tabs[_tab];
        _bankRows = ItemBankShelf.Rows(_bank, pouch, _query, _sort,
            _view is null ? null : key => _places.GetValueOrDefault(key), _family,
            (entry, place) => place is { } p ? ItemName(p.ItemId) : entry.Name);
        _bagRows = [];
        if (_view is { } session)
            foreach (var p in _bag.Where(p => pouch is null || p.Name == pouch))
                foreach (var item in p.Items.Where(i => i.Count > 0))
                    _bagRows.Add(new BagRow(p.Name, item.Id, item.Count, ItemName(item.Id), Kept(session, p.Name, item.Id)));
        _bankCursor = Math.Clamp(_bankCursor, 0, Math.Max(0, _bankRows.Count - 1));
        _bagCursor = Math.Clamp(_bagCursor, 0, Math.Max(0, _bagRows.Count - 1));
        if (CurrentCount == 0) _menu = false;
        KeepInView();
        Invalidate();
    }

    private static string? Kept(ISaveEngineSession session, string pouch, int id)
    {
        try { return ItemBankService.WhyKept(session, pouch, id); }
        catch (InvalidOperationException error) { return error.Message; }
    }

    private int CurrentCount => _bankFocused ? _bankRows.Count : _bagRows.Count;
    private ItemBankRow? BankRow => _bankCursor < _bankRows.Count ? _bankRows[_bankCursor] : null;
    private BagRow? BagItem => _bagCursor < _bagRows.Count ? _bagRows[_bagCursor] : null;

    // ── Painting ─────────────────────────────────────────────────────────────

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private void Report(string message)
    {
        _status = message;
        Invalidate();
    }

    private int VisibleRows => ItemBankPaint.VisibleRows(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height), _making);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var visible = ItemBankPaint.VisibleRows(new SKSizeI(args.Info.Width, args.Info.Height), _making);
        var tabs = _tabs.Select(p => p is null
            ? new BagScenePouch("All", Icon("item"), _bank.Entries.Count)
            : new BagScenePouch(BagPouchLooks.Of(p).Title, Icon(BagPouchLooks.Of(p).Icon), _bag.FirstOrDefault(b => b.Name == p)?.Items.Count ?? 0)).ToList();
        var all = _tabs[_tab] is null;
        var bank = _bankRows.Select((row, i) => new ItemBankSceneRow(row.Name, row.Entry.Count,
            Art(row.Name, i, _bankScroll, visible), BankNote(row), Dimmed: _view is not null && !row.CanWithdraw)).ToList();
        var bag = _bagRows.Select((row, i) => new ItemBankSceneRow(row.Name, row.Count, Art(row.Name, i, _bagScroll, visible),
            row.Kept ?? (all ? BagPouchLooks.Of(row.Pouch).Title : null), Dimmed: row.Kept is not null)).ToList();
        var scene = new ItemBankScene(tabs, _tab, bank, _bankCursor, _bankScroll, _targetTitle, bag, _bagCursor, _bagScroll,
            _bankFocused, _query, ItemBankShelf.Label(_sort), Detail(), _menu ? Menu() : null,
            HardcoreMode.Guard.CanEditInventory, _status, Sections(), null, _making ? MakerScene(visible) : null);
        _hits = ItemBankPaint.Paint(c, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    /// <summary>The Bank's two halves, as the Pokémon Bank paints them: this page is "Items".</summary>
    private IReadOnlyList<BankSection> Sections()
    {
        var mons = IPlatformApplication.Current?.Services.GetService<IBankService>()?.Count;
        return
        [
            new BankSection("Pokémon", Icon("bank"), mons is { } count ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : null),
            new BankSection("Items", Icon("bag"), _bank.Entries.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
        ];
    }

    private ItemMakerScene MakerScene(int visible)
    {
        var tabs = _makeTabs.Select((p, t) => p is null
            ? new BagScenePouch("All", Icon("item"), _catalog.Count)
            : new BagScenePouch(BagPouchLooks.Of(p).Title, Icon(BagPouchLooks.Of(p).Icon), _makeTabCounts.ElementAtOrDefault(t))).ToList();
        var all = _makeTabs[_makeTab] is null;
        var counts = BankCounts();
        var rows = _makeRows.Select((item, i) => new ItemBankSceneRow(item.Recipe.Name, counts.GetValueOrDefault(item.Recipe.Key),
            ArtFor(item.EnglishName, i >= _makeScroll && i <= _makeScroll + visible || i == _makeCursor),
            all ? BagPouchLooks.Of(item.Pouch).Title : null)).ToList();
        var facts = new List<string>();
        if (MakeItem is { } chosen)
        {
            var banked = _bank.CountOf(chosen.Recipe.Key);
            facts.Add($"{BagPouchLooks.Of(chosen.Pouch).Title} · Gen {chosen.Recipe.Generation}");
            facts.Add($"Up to {chosen.Recipe.StackCap} in one stack in this game");
            facts.Add(banked > 0 ? $"×{banked} already in the Item Bank" : "None in the Item Bank yet");
            facts.Add(ItemBankKeys.IsMachineKey(chosen.Recipe.Key)
                ? "A machine: only for these games"
                : "Any game that has it can take it");
        }
        return new ItemMakerScene(_makeFamily?.Label ?? "", tabs, _makeTab, rows, _makeCursor, _makeScroll, _makeQuery, facts,
            _makeMenu ? MakeMenu() : null);
    }

    // The maker's counts, kept out of paint: each pouch's size per catalogue, and the bank's
    // count per item key for the bank it was made from (an ItemBank never changes in place).
    private int[] _makeTabCounts = [];
    private ItemBank? _countsOf;
    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    private Dictionary<string, int> BankCounts()
    {
        if (ReferenceEquals(_countsOf, _bank)) return _counts;
        _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in _bank.Entries) _counts.TryAdd(entry.Key, entry.Count);
        _countsOf = _bank;
        return _counts;
    }

    private string BankNote(ItemBankRow row)
    {
        if (row.Reason is { } reason) return reason;
        var from = $"from {ItemBankShelf.LatestGame(row.Entry)}";
        if (row.Place is not { } place) return from;
        var held = _bag.FirstOrDefault(p => p.Name == place.Pouch)?.Items.FirstOrDefault(i => i.Id == place.ItemId)?.Count ?? 0;
        return $"×{held} in {BagPouchLooks.Of(place.Pouch).Title} · {from}";
    }

    private string? Detail()
    {
        if (_bankFocused) return BankRow is { } row ? $"From {ItemBankShelf.Origins(row.Entry)}" : null;
        return BagItem is { } item ? $"×{item.Count} in {BagPouchLooks.Of(item.Pouch).Title} · {_targetTitle}" : null;
    }

    private IReadOnlyList<string> Actions => _bankFocused
        ? [_targetTitle is { } game ? $"Send to {game}" : "Send to a game", "Throw away"]
        : ["To the Item Bank"];

    private int MostAmount => _bankFocused ? BankRow?.Entry.Count ?? 1 : BagItem?.Count ?? 1;

    /// <summary>Why the focused row's move cannot happen, or null when it can.</summary>
    private string? Refusal()
    {
        if (_bankFocused)
        {
            if (_action != 0) return null;
            if (_view is null) return "Choose a game first: X picks one from the shelf.";
            if (!HardcoreMode.Guard.CanEditInventory) return HardcoreMode.StatusFor(SaveAction.EditInventory);
            return BankRow is { CanWithdraw: false } row ? row.Reason ?? "This game cannot hold it." : null;
        }
        if (!HardcoreMode.Guard.CanEditInventory) return HardcoreMode.StatusFor(SaveAction.EditInventory);
        return BagItem?.Kept;
    }

    private ItemBankSceneMenu Menu() => new(Actions, _action, _amount, Refusal());

    private SKImage? Icon(string name)
    {
        if (_icons.TryGetValue(name, out var known)) return known;
        SKImage? image = null;
        try
        {
            using var bitmap = SKBitmap.Decode(PksmIcons.GetPng(name, PksmIcons.White));
            image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception error) when (error is IOException or FileNotFoundException or ArgumentException) { }
        _icons[name] = image;
        return image;
    }

    /// <summary>The item's art from the bag's own source, fetched once, only for the rows on screen.</summary>
    private SKImage? Art(string name, int index, int scroll, int visible) =>
        ArtFor(name, index >= scroll && index <= scroll + visible || name == BankRow?.Name || name == BagItem?.Name);

    private SKImage? ArtFor(string name, bool onScreen)
    {
        if (_art.TryGetValue(name, out var image)) return image;
        if (!onScreen) return null;
        if (_artLoading.Add(name)) _ = LoadArtAsync(name);
        return null;
    }

    private async Task LoadArtAsync(string name)
    {
        var path = await ItemArt.GetAsync(name) ?? ItemArt.PlaceholderPath();
        SKImage? image = null;
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _art[name] = image;
            _canvas.InvalidateSurface();
        });
    }

    // ── Moving around ────────────────────────────────────────────────────────

    private void KeepInView()
    {
        var visible = VisibleRows;
        static int Fit(int cursor, int scroll, int count, int visible)
        {
            if (cursor < scroll) scroll = cursor;
            else if (cursor >= scroll + visible) scroll = cursor - visible + 1;
            return Math.Clamp(scroll, 0, Math.Max(0, count - visible));
        }
        _bankScroll = Fit(_bankCursor, _bankScroll, _bankRows.Count, visible);
        _bagScroll = Fit(_bagCursor, _bagScroll, _bagRows.Count, visible);
    }

    private void MoveCursor(int delta)
    {
        if (CurrentCount == 0) return;
        if (_bankFocused) _bankCursor = Math.Clamp(_bankCursor + delta, 0, _bankRows.Count - 1);
        else _bagCursor = Math.Clamp(_bagCursor + delta, 0, _bagRows.Count - 1);
        KeepInView();
        Invalidate();
    }

    private void SwitchTab(int index)
    {
        if (_tabs.Count == 0) return;
        _tab = ((index % _tabs.Count) + _tabs.Count) % _tabs.Count;
        _bankCursor = _bagCursor = _bankScroll = _bagScroll = 0;
        _menu = false;
        Rebuild();
    }

    private void Focus(bool bank)
    {
        if (_bankFocused == bank) return;
        _bankFocused = bank;
        _menu = false;
        Invalidate();
    }

    private void OpenMenu()
    {
        if (CurrentCount == 0) return;
        _menu = true;
        _action = 0;
        _amount = 1;
        _status = ""; // the last move's report belongs to another item
        Invalidate();
    }

    private void ChangeAmount(int delta)
    {
        _amount = ItemBankShelf.Step(_amount, delta, MostAmount);
        Invalidate();
    }

    private async Task SearchAsync()
    {
        var typed = await TextPopup.ShowLineAsync(_hostGrid, "Search the Item Bank", "Part of the item's name", _query, confirm: "Search");
        if (typed is null) return;
        _query = typed.Trim();
        _bankCursor = _bankScroll = 0;
        Rebuild();
    }

    private void CycleSort()
    {
        _sort = ItemBankShelf.Next(_sort);
        _bankCursor = _bankScroll = 0;
        Rebuild();
    }

    /// <summary>X: any game on the shelf, the open one first; the pick resolves by position.</summary>
    private async Task PickGameAsync()
    {
        var shelf = Shelf();
        if (shelf.Count == 0)
        {
            Report("No games on the shelf. Link an emulator on Home first.");
            return;
        }
        var open = _sessions?.Current?.Document.DocumentId;
        var labels = BankPage.TransferLabels(shelf);
        // Twin saves are long labels: the file and its folder underneath tell them apart at a glance.
        var options = shelf.Select((save, i) => new PadOption(save.DocumentId == open ? $"{labels[i]} (open)" : labels[i], Glyph: "●",
            Accent: SaveColors.For(save.Identity?.ColorKey, save.Generation),
            Detail: SaveTargets.Detail(save))).ToArray();
        var start = _target is { } now ? Math.Max(0, shelf.ToList().FindIndex(s => s.DocumentId == now.DocumentId)) : 0;
        var choice = await PadMenu.ShowAtAsync(_hostGrid, "Trade items with", "The game's bag shows on the right. It is read fresh from its file.", start, options);
        var index = Array.FindIndex(options, o => o.Label == choice);
        if (index < 0) return;
        await ChooseAsync(shelf[index]);
    }

    // ── Acting ───────────────────────────────────────────────────────────────

    private async Task ActAsync()
    {
        if (_busy) return;
        if (Refusal() is { } refusal) { Report(refusal); return; }
        if (_bankFocused && BankRow is { } banked)
        {
            if (_action == 1) await DiscardAsync(banked);
            else await WithdrawAsync(banked);
        }
        else if (!_bankFocused && BagItem is { } item) await DepositAsync(item);
    }

    private Task WithdrawAsync(ItemBankRow row)
    {
        var amount = _amount;
        return MoveAsync($"{row.Name} ×{amount} from the Item Bank", (session, commit) =>
            ItemBankTransfer.WithdrawAsync(session, _store!, commit, row.Entry.Key, amount, row.Name));
    }

    private Task DepositAsync(BagRow item)
    {
        var amount = _amount;
        return MoveAsync($"{item.Name} ×{amount} sent to the Item Bank", (session, commit) =>
            ItemBankTransfer.DepositAsync(session, _store!, commit, item.Pouch, item.Id, amount));
    }

    /// <summary>
    /// One move: the game is read fresh from its file, the bag and bank change together, and the
    /// save is written through the transfers' safe path (the restore point comes first). The
    /// Hardcore guard refuses before anything is read.
    /// </summary>
    private async Task MoveAsync(string description, Func<ISaveEngineSession, Func<Task>, Task<ItemBankMoveResult>> move)
    {
        if (_store is null || _transfer is null || _target is not { } target) return;
        if (HardcoreMode.Blocks(SaveAction.EditInventory, out var hardcore)) { Report(hardcore); return; }
        _busy = true;
        Report("Validating, backing up, writing…");
        try
        {
            using var session = await _transfer.OpenGameAsync(target);
            var snapshot = session.Snapshot;
            // The game open in the boxes is written from its file, then the boxes reopen from what
            // was written: edits made there and not written yet would be dropped without a word.
            var isOpen = _sessions?.Current?.Document.DocumentId == target.DocumentId;
            if (isOpen && _sessions?.CurrentSession is { } live && !live.Serialize().Span.SequenceEqual(session.Serialize().Span))
                _status = $"{target.FileName} is open in the boxes with changes not written yet. Write or undo them there first; nothing was moved.";
            else
            {
                var result = await move(session, () => _transfer.WriteGameAsync(target, snapshot, session, description));
                // The live session was reopened from the written file: the boxes read it again.
                if (isOpen) _boxes?.RefreshFromCurrentSession();
                _status = result.Success ? $"{result.Message} Restart the game to see it." : result.Message;
                if (result.Success) _menu = false;
            }
        }
        catch (Exception error)
        {
            _status = $"Not saved. {Domain.ErrorText.Plain(error)}";
            AppLog.Error("item-bank", "An Item Bank move failed", error);
        }
        finally
        {
            _busy = false;
        }
        await ReloadAsync();
    }

    private async Task DiscardAsync(ItemBankRow row)
    {
        if (_store is null) return;
        var amount = Math.Min(_amount, row.Entry.Count);
        if (!await PadMenu.ConfirmAsync(_hostGrid, $"Throw away {row.Name} ×{amount}?",
            "They leave the Item Bank for good. No game is touched.", "Throw away")) return;
        try
        {
            var result = ItemBankTransfer.Discard(_store, row.Entry.Key, amount, row.Name);
            _status = result.Message;
            _menu = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or InvalidDataException or System.Text.Json.JsonException)
        {
            _status = $"The Item Bank could not be written. {Domain.ErrorText.Plain(error)}";
        }
        ReadBank();
        Rebuild();
    }

    // ── Making items ─────────────────────────────────────────────────────────

    private ItemBankCatalogItem? MakeItem => _makeCursor < _makeRows.Count ? _makeRows[_makeCursor] : null;

    private int MostMake => MakeItem is { } item ? Math.Max(1, ItemBankGenerator.Most(_bank, item.Recipe)) : 1;

    /// <summary>Why A makes nothing for the item under the cursor, or null when it can.</summary>
    private string? MakeRefusal()
    {
        if (!HardcoreMode.Guard.CanEditInventory) return HardcoreMode.StatusFor(SaveAction.EditInventory);
        return MakeItem is { } item ? ItemBankGenerator.Refusal(_bank, item.Recipe) : null;
    }

    private ItemBankSceneMenu MakeMenu() => new(["Make and bank"], 0, _makeAmount, MakeRefusal());

    /// <summary>
    /// −, or the "Make items" plate: the generator, like the Pokémon Bank's "Create a Pokémon".
    /// The family is the chosen game's, else the open game's; with neither, the player picks one,
    /// as the Pokémon generator asks for an era when no save is connected.
    /// </summary>
    private async Task OpenMakerAsync()
    {
        if (_busy) return;
        if (HardcoreMode.Blocks(SaveAction.EditInventory, out var hardcore)) { Report(hardcore); return; }
        var family = _makeFamily
            ?? (_view is { } game ? ItemBankCatalog.FamilyOf(game) : null)
            ?? (_sessions?.CurrentSession is { } open ? ItemBankCatalog.FamilyOf(open) : null)
            ?? await PickFamilyAsync("No game chosen: the items are made for the family you pick. They go to a game later, through the bank.");
        if (family is null) return;
        _menu = false;
        _status = "";
        // The maker opens only on a catalogue: a failed or empty one says why on the bank instead.
        if (await LoadFamilyAsync(family)) _making = true;
        Invalidate();
    }

    private async Task<ItemBankFamily?> PickFamilyAsync(string? subtitle)
    {
        var families = ItemBankCatalog.Families;
        var options = families.Select(f => new PadOption(f.Label, IconPath: "game", Detail: $"Gen {f.Generation} · {f.Games}")).ToArray();
        var start = _makeFamily is { } now ? Math.Max(0, families.ToList().FindIndex(f => f.Id == now.Id)) : families.Count - 1;
        var choice = await PadMenu.ShowAtAsync(_hostGrid, "Make items for which games?", subtitle, start, options);
        var index = Array.FindIndex(options, o => o.Label == choice);
        return index < 0 ? null : families[index];
    }

    /// <summary>Reads the family's catalogue off the UI thread (a blank save's bag rules), then lists it.
    /// False, with the reason on the status line, when there is nothing to list; the catalogue shown stays.</summary>
    private async Task<bool> LoadFamilyAsync(ItemBankFamily family)
    {
        _busy = true;
        Report($"Reading {family.Label}'s items…");
        IReadOnlyList<ItemBankCatalogItem> catalog;
        try { catalog = await Task.Run(() => ItemBankCatalog.Items(family)); }
        catch (Exception error)
        {
            AppLog.Error("item-bank", $"Reading {family.Label}'s items failed", error);
            Report($"{family.Label}: its items could not be read. {Domain.ErrorText.Plain(error)}");
            return false;
        }
        finally { _busy = false; }
        if (catalog.Count == 0)
        {
            Report($"{family.Label}: PKForge cannot read this family's items.");
            return false;
        }
        var keep = _makeTabs.ElementAtOrDefault(_makeTab);
        _makeFamily = family;
        _catalog = catalog;
        _makeTabs = [null, .. catalog.Select(i => i.Pouch).Distinct().Select(p => (string?)p)];
        _makeTabCounts = [.. _makeTabs.Select(p => p is null ? catalog.Count : catalog.Count(i => i.Pouch == p))];
        _makeTab = Math.Max(0, _makeTabs.IndexOf(keep));
        _makeCursor = _makeScroll = 0;
        _makeMenu = false;
        _status = "";
        RebuildMaker();
        return true;
    }

    private void RebuildMaker()
    {
        if (_makeTab >= _makeTabs.Count) _makeTab = 0;
        _makeRows = ItemBankGenerator.Filter(_catalog, i => i.Recipe.Name, i => i.Pouch, _makeTabs[_makeTab], _makeQuery);
        _makeCursor = Math.Clamp(_makeCursor, 0, Math.Max(0, _makeRows.Count - 1));
        if (_makeRows.Count == 0) _makeMenu = false;
        _makeScroll = Fit(_makeCursor, _makeScroll, _makeRows.Count, VisibleRows);
        Invalidate();
    }

    private static int Fit(int cursor, int scroll, int count, int visible)
    {
        if (cursor < scroll) scroll = cursor;
        else if (cursor >= scroll + visible) scroll = cursor - visible + 1;
        return Math.Clamp(scroll, 0, Math.Max(0, count - visible));
    }

    private void MoveMakeCursor(int delta)
    {
        if (_makeRows.Count == 0) return;
        _makeCursor = Math.Clamp(_makeCursor + delta, 0, _makeRows.Count - 1);
        _makeScroll = Fit(_makeCursor, _makeScroll, _makeRows.Count, VisibleRows);
        Invalidate();
    }

    private void SwitchMakeTab(int index)
    {
        if (_makeTabs.Count == 0) return;
        _makeTab = ((index % _makeTabs.Count) + _makeTabs.Count) % _makeTabs.Count;
        _makeCursor = _makeScroll = 0;
        _makeMenu = false;
        RebuildMaker();
    }

    private void OpenMakeMenu()
    {
        if (MakeItem is null) return;
        _makeMenu = true;
        _makeAmount = 1;
        _status = "";
        Invalidate();
    }

    /// <summary>◀ ▶ step by one and wrap, like every count row; L R jump by ten and stop at the ends.</summary>
    private static int Jump(int amount, int delta, int most) => Math.Clamp(amount + delta, 1, Math.Max(1, most));

    private async Task SearchMakerAsync()
    {
        var typed = await TextPopup.ShowLineAsync(_hostGrid, $"Search {_makeFamily?.Label}'s items", "Part of the item's name", _makeQuery, confirm: "Search");
        if (typed is null) return;
        _makeQuery = typed.Trim();
        // A search looks through every pouch: the player may not know where the games keep it.
        if (_makeQuery.Length > 0) _makeTab = 0;
        _makeCursor = _makeScroll = 0;
        RebuildMaker();
    }

    private async Task ChangeFamilyAsync()
    {
        var family = await PickFamilyAsync("The catalogue and the stack size are that family's. A machine made for it only goes back there.");
        if (family is null || family.Id == _makeFamily?.Id) return;
        await LoadFamilyAsync(family);
    }

    /// <summary>Adds the amount to the Item Bank, and only the bank: no save is opened or written.
    /// The game gets them later through the bank's own transfer, its checks and its restore point.</summary>
    private void Make()
    {
        if (_store is null || MakeItem is not { } item) return;
        if (MakeRefusal() is { } refusal) { Report(refusal); return; }
        if (HardcoreMode.Blocks(SaveAction.EditInventory, out var hardcore)) { Report(hardcore); return; }
        try
        {
            var made = ItemBankGenerator.Make(ReadStore(), item.Recipe, _makeAmount);
            _store.Save(made);
            _bank = made;
            _lastMade = item.Recipe.Key;
            _status = $"Made {item.Recipe.Name} ×{_makeAmount} for {item.Recipe.Game}: it is in the Item Bank.";
            _makeMenu = false;
        }
        catch (InvalidOperationException refused) { _status = refused.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _status = $"The Item Bank could not be written. {Domain.ErrorText.Plain(error)}";
            AppLog.Error("item-bank", "Making items failed", error);
        }
        Invalidate();
    }

    /// <summary>Back to the bank, the cursor on the last item made.</summary>
    private void CloseMaker()
    {
        _making = false;
        _makeMenu = false;
        ReadBank();
        Rebuild();
        if (_lastMade is { } key)
        {
            _bankFocused = true;
            var at = _bankRows.ToList().FindIndex(r => r.Entry.Key == key);
            if (at < 0 && _tab != 0) { _tab = 0; Rebuild(); at = _bankRows.ToList().FindIndex(r => r.Entry.Key == key); }
            if (at >= 0) _bankCursor = at;
            KeepInView();
            Invalidate();
        }
    }

    private bool OnMakerButton(PadButton button)
    {
        if (_makeMenu)
        {
            switch (button)
            {
                case PadButton.Left: _makeAmount = ItemBankShelf.Step(_makeAmount, -1, MostMake); Invalidate(); return true;
                case PadButton.Right: _makeAmount = ItemBankShelf.Step(_makeAmount, 1, MostMake); Invalidate(); return true;
                case PadButton.L: _makeAmount = Jump(_makeAmount, -10, MostMake); Invalidate(); return true;
                case PadButton.R: _makeAmount = Jump(_makeAmount, 10, MostMake); Invalidate(); return true;
                case PadButton.A: Make(); return true;
                case PadButton.B: _makeMenu = false; Invalidate(); return true;
                default: return true;
            }
        }
        switch (button)
        {
            case PadButton.Up: MoveMakeCursor(-1); return true;
            case PadButton.Down: MoveMakeCursor(1); return true;
            case PadButton.Left: SwitchMakeTab(_makeTab - 1); return true;
            case PadButton.Right: SwitchMakeTab(_makeTab + 1); return true;
            case PadButton.L: MoveMakeCursor(-Math.Max(1, VisibleRows)); return true;
            case PadButton.R: MoveMakeCursor(Math.Max(1, VisibleRows)); return true;
            case PadButton.A: OpenMakeMenu(); return true;
            case PadButton.Y: _ = SearchMakerAsync(); return true;
            case PadButton.X: _ = ChangeFamilyAsync(); return true;
            case PadButton.B or PadButton.Select: CloseMaker(); return true;
            default: return true;
        }
    }

    private void OnMakerTap(ItemBankHits hits, SKPoint at)
    {
        if (hits.Previous.Contains(at)) { SwitchMakeTab(_makeTab - 1); return; }
        if (hits.Next.Contains(at)) { SwitchMakeTab(_makeTab + 1); return; }
        if (hits.Search.Contains(at)) { _ = SearchMakerAsync(); return; }
        if (hits.Game.Contains(at)) { _ = ChangeFamilyAsync(); return; }
        for (var i = 0; i < hits.Tabs.Count; i++)
            if (hits.Tabs[i].Contains(at)) { SwitchMakeTab(i); return; }
        foreach (var (index, rect) in hits.BankRows)
        {
            if (!rect.Contains(at)) continue;
            if (index == _makeCursor && !_makeMenu) { OpenMakeMenu(); return; }
            _makeCursor = index;
            _makeMenu = false;
            _makeScroll = Fit(_makeCursor, _makeScroll, _makeRows.Count, VisibleRows);
            Invalidate();
            return;
        }
        if (hits.Minus.Contains(at)) { _makeAmount = ItemBankShelf.Step(_makeAmount, -1, MostMake); Invalidate(); return; }
        if (hits.Plus.Contains(at)) { _makeAmount = ItemBankShelf.Step(_makeAmount, 1, MostMake); Invalidate(); return; }
        // The menu's one row is already the selected action: a tap on it makes them.
        if (hits.Actions.Any(a => a.Rect.Contains(at))) Make();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (_making) return OnMakerButton(button);
        if (_menu)
        {
            switch (button)
            {
                case PadButton.Up: _action = Math.Max(0, _action - 1); _amount = Math.Min(_amount, MostAmount); Invalidate(); return true;
                case PadButton.Down: _action = Math.Min(Actions.Count - 1, _action + 1); Invalidate(); return true;
                case PadButton.Left: ChangeAmount(-1); return true;
                case PadButton.Right: ChangeAmount(1); return true;
                case PadButton.L: _amount = Jump(_amount, -10, MostAmount); Invalidate(); return true;
                case PadButton.R: _amount = Jump(_amount, 10, MostAmount); Invalidate(); return true;
                case PadButton.A: _ = ActAsync(); return true;
                case PadButton.B: _menu = false; _status = ""; Invalidate(); return true;
                default: return true;
            }
        }
        switch (button)
        {
            case PadButton.Up: MoveCursor(-1); return true;
            case PadButton.Down: MoveCursor(1); return true;
            case PadButton.Left: SwitchTab(_tab - 1); return true;
            case PadButton.Right: SwitchTab(_tab + 1); return true;
            case PadButton.L: Focus(true); return true;
            case PadButton.R: Focus(false); return true;
            case PadButton.A: OpenMenu(); return true;
            case PadButton.Y: _ = SearchAsync(); return true;
            case PadButton.X: _ = PickGameAsync(); return true;
            case PadButton.Start: CycleSort(); return true;
            case PadButton.Select: _ = OpenMakerAsync(); return true;
            case PadButton.B: _ = Navigation.PopAsync(false); return true;
            default: return false;
        }
    }

    /// <summary>Taps land on what was painted: a tap selects, a second tap acts; a drag scrolls.</summary>
    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_hits is not { } hits) return;
        var at = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                _pressedAt = at;
                _pressedOnBank = hits.BankList.Contains(at);
                _dragRows = 0;
                _dragged = false;
                return;
            case SKTouchAction.Moved when _pressedAt is { } start && (hits.BankList.Contains(start) || hits.BagList.Contains(start)):
                if (!_dragged && Math.Abs(start.Y - at.Y) < hits.RowHeight / 3) return;
                _dragged = true;
                var step = (int)((start.Y - at.Y) / hits.RowHeight - _dragRows);
                if (step == 0) return;
                _dragRows += step;
                var visible = VisibleRows;
                if (_making) _makeScroll = Math.Clamp(_makeScroll + step, 0, Math.Max(0, _makeRows.Count - visible));
                else if (_pressedOnBank) _bankScroll = Math.Clamp(_bankScroll + step, 0, Math.Max(0, _bankRows.Count - visible));
                else _bagScroll = Math.Clamp(_bagScroll + step, 0, Math.Max(0, _bagRows.Count - visible));
                _canvas.InvalidateSurface();
                return;
            case SKTouchAction.Released:
                var tapped = !_dragged;
                _pressedAt = null;
                if (tapped) OnTap(hits, at);
                return;
            case SKTouchAction.Cancelled:
                _pressedAt = null;
                return;
        }
    }

    private void OnTap(ItemBankHits hits, SKPoint at)
    {
        // The Bank's section tabs: "Pokémon" goes back to the Pokémon Bank this page came from.
        if (hits.Sections is { Count: > 0 } sections && sections[0].Contains(at)) { _ = Navigation.PopAsync(false); return; }
        if (_making) { OnMakerTap(hits, at); return; }
        if (hits.Make.Contains(at)) { _ = OpenMakerAsync(); return; }
        if (hits.Previous.Contains(at)) { SwitchTab(_tab - 1); return; }
        if (hits.Next.Contains(at)) { SwitchTab(_tab + 1); return; }
        if (hits.Search.Contains(at)) { _ = SearchAsync(); return; }
        if (hits.Sort.Contains(at)) { CycleSort(); return; }
        if (hits.Game.Contains(at)) { _ = PickGameAsync(); return; }
        for (var i = 0; i < hits.Tabs.Count; i++)
            if (hits.Tabs[i].Contains(at)) { SwitchTab(i); return; }
        if (TapRow(hits.BankRows, at, bank: true) || TapRow(hits.BagRows, at, bank: false)) return;
        if (hits.Minus.Contains(at)) { ChangeAmount(-1); return; }
        if (hits.Plus.Contains(at)) { ChangeAmount(1); return; }
        foreach (var (index, rect) in hits.Actions)
        {
            if (!rect.Contains(at)) continue;
            var again = _action == index;
            _action = index;
            Invalidate();
            if (again) _ = ActAsync();
            return;
        }
        if (hits.BankList.Contains(at)) Focus(true);
        else if (hits.BagList.Contains(at)) Focus(false);
    }

    private bool TapRow(IReadOnlyList<(int Index, SKRect Rect)> rows, SKPoint at, bool bank)
    {
        foreach (var (index, rect) in rows)
        {
            if (!rect.Contains(at)) continue;
            var cursor = bank ? _bankCursor : _bagCursor;
            if (_bankFocused == bank && index == cursor && !_menu) { OpenMenu(); return true; }
            _bankFocused = bank;
            _menu = false;
            if (bank) _bankCursor = index; else _bagCursor = index;
            KeepInView();
            Invalidate();
            return true;
        }
        return false;
    }
}
