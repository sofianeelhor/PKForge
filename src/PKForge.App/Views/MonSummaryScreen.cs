using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// What the full-screen summary walks: a box of <see cref="Count"/> slots, which of them
/// hold a mon, where to start, and how to decode one (optionally with its legality
/// verdict, which is slower and arrives second). <see cref="Moved"/> lets the host keep
/// its cursor on the mon being viewed; <see cref="CanEdit"/> offers the X shortcut;
/// <see cref="Icon"/> draws the box overview the second screen shows meanwhile.
/// </summary>
public sealed record SummaryDeck(
    int Count,
    Func<int, bool> Occupied,
    int Start,
    Func<int, bool, Task<MonSummary?>> Load,
    string Context,
    bool CanEdit = false,
    Action<int>? Moved = null,
    Func<int, SlotIcon?>? Icon = null);

/// <summary>How the summary closed: the slot it ended on and whether X asked for the editor.</summary>
public sealed record SummaryResult(int Slot, bool EditRequested);

/// <summary>
/// The full-screen Pokémon summary for single-screen play (and anyone who wants the big
/// view): L/R walk the previous/next mon of the box, skipping empty slots; the d-pad's
/// left/right (or a swipe, or the tabs) turn the page and up/down scroll it; X opens the
/// editor where the host allows it; B closes. Read-only, so it is always available -
/// Hardcore mode included.
/// </summary>
public sealed class MonSummaryScreen : IPadHandler
{
    /// <summary>How long the host's cursor waits before following a burst of L/R presses.</summary>
    private const int HostFollowDelayMs = 300;

    private readonly Grid _host;
    private readonly SummaryDeck _deck;
    private readonly MonSummaryView _view;
    private readonly Label _title;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SecondScreenState? _secondScreen;
    private readonly SecondScreenClaim? _secondClaim;
    private readonly IReadOnlyList<SlotIcon?> _icons;
    private readonly TaskCompletionSource<SummaryResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Decoded summaries for this deck (the summary is read-only, so nothing goes stale while it
    // is open): a revisited or prefetched mon shows in one repaint. UI thread only.
    private readonly Dictionary<int, MonSummary?> _full = new();
    private readonly Dictionary<int, MonSummary?> _quick = new();
    // One decode at a time: the loaders share the live session / engine with nobody else here.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _slot;
    private int _sequence;
    private int _hostSlot;
    private IDispatcherTimer? _follow;
    private bool _closed;

    public static Task<SummaryResult> ShowAsync(Grid host, ISpriteService sprites, SummaryDeck deck, SummaryPage page = SummaryPage.Info) =>
        new MonSummaryScreen(host, sprites, deck, page)._result.Task;

    private MonSummaryScreen(Grid host, ISpriteService sprites, SummaryDeck deck, SummaryPage page)
    {
        var ctorWatch = System.Diagnostics.Stopwatch.StartNew();
        _host = host;
        _deck = deck;
        _slot = _hostSlot = deck.Start;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view = new MonSummaryView(sprites);
        _view.SetPage(page);

        var previous = Kit.MiniCapsule("‹ L", UiTokens.Ink0);
        previous.Clicked += (_, _) => Step(-1);
        var next = Kit.MiniCapsule("R ›", UiTokens.Ink0);
        next.Clicked += (_, _) => Step(1);
        // The app's own header strip (Kit.HeaderBar), centred title.
        var header = (Border)Kit.HeaderBar("Summary");
        _title = (Label)header.Content!;
        _title.HorizontalTextAlignment = TextAlignment.Center;
        var top = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { previous, header, next },
        };
        Grid.SetColumn(header, 1);
        Grid.SetColumn(next, 2);

        var hints = new List<(string, string, Action?)>
        {
            ("◀▶", "Page", () => _view.TurnPage(1)),
            ("LR", "Pokémon", null),
        };
        if (deck.CanEdit) hints.Add(("X", "Edit", () => Close(true)));
        hints.Add(("B", "Close", () => Close(false)));
        var hintBar = Kit.HintBar(hints.ToArray());

        var surface = new Grid
        {
            Padding = new Thickness(12, 10),
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { top, _view, hintBar },
        };
        Grid.SetRow(_view, 1);
        Grid.SetRow(hintBar, 2);
        // The storage world's logo grid behind it, like every other full screen in the app.
        var screen = new Grid { Children = { Kit.DeviceBackground(), surface } };

        _overlay = Kit.AttachOverlay(host, screen);
        _router?.Push(this);
        // The top screen now carries the details: the lower one switches to the box overview
        // (never the same summary twice) until this closes.
        _icons = Enumerable.Range(0, deck.Count).Select(i => deck.Occupied(i) ? deck.Icon?.Invoke(i) : null).ToArray();
        _secondScreen = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        _secondClaim = _secondScreen?.Routes.OpenOverlay(SecondScreenOwner.Summary);
        _ = LoadAsync();
        PerfTrace.Log("screen.open", ctorWatch);
        PerfTrace.UntilIdle("screen.open", host.Dispatcher);
    }

    private async Task LoadAsync()
    {
        var sequence = ++_sequence;
        var slot = _slot;
        var (position, total) = SummaryNavigation.Position(slot, _deck.Count, _deck.Occupied);
        var context = total > 0 ? $"{_deck.Context} · {position} / {total}" : _deck.Context;
        _title.Text = $"Summary · {context}";
        if (_secondScreen is not null)
            _secondScreen.Overview = new SummaryOverview(_deck.Context, _deck.Count, slot, _icons, position, total);
        bool Wanted() => sequence == _sequence && !_closed;
        try
        {
            if (_full.TryGetValue(slot, out var cached))
            {
                _view.Show(cached, emptyText: "Nothing here");
            }
            else
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var quick = await GetAsync(slot, false, Wanted);
                PerfTrace.Log("screen.load.quick", watch);
                if (!Wanted()) return;
                _view.Show(quick, legalityPending: quick is not null, emptyText: "Nothing here");
                if (quick is null) return;
                watch.Restart();
                var full = await GetAsync(slot, true, Wanted);
                PerfTrace.Log("screen.load.full", watch);
                if (!Wanted()) return;
                _view.Show(full ?? quick);
            }
            // Warm the neighbours so the next L/R is a single repaint.
            foreach (var direction in new[] { 1, -1 })
            {
                if (!Wanted()) return;
                if (SummaryNavigation.Step(slot, direction, _deck.Count, _deck.Occupied) is { } neighbour && !_full.ContainsKey(neighbour))
                    await GetAsync(neighbour, true, () => !_closed);
            }
        }
        catch (Exception error)
        {
            if (!Wanted()) return;
            _view.Show(null, emptyText: $"Can't read this Pokémon\n{error.Message}");
        }
    }

    /// <summary>Cached, serialized decode. A request that stopped being wanted while it queued is dropped.</summary>
    private async Task<MonSummary?> GetAsync(int slot, bool legality, Func<bool> wanted)
    {
        if (_full.TryGetValue(slot, out var full)) return full;
        if (!legality && _quick.TryGetValue(slot, out var quick)) return quick;
        await _gate.WaitAsync();
        try
        {
            if (_full.TryGetValue(slot, out full)) return full;
            if (!legality && _quick.TryGetValue(slot, out quick)) return quick;
            if (!wanted()) return null;
            var built = await _deck.Load(slot, legality);
            (legality ? _full : _quick)[slot] = built;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Step(int direction)
    {
        if (_closed) return;
        if (SummaryNavigation.Step(_slot, direction, _deck.Count, _deck.Occupied) is not { } target) return;
        _slot = target;
        ScheduleHostFollow();
        _ = LoadAsync();
    }

    /// <summary>
    /// The host's cursor (and the second-screen inspector behind it) follows once the
    /// L/R burst settles - never on every press, so walking the box stays one repaint.
    /// </summary>
    private void ScheduleHostFollow()
    {
        if (_deck.Moved is null) return;
        // Nothing shows the host while the summary covers the only screen: catch up on close.
        if (!HasSecondScreen) return;
        _follow ??= CreateFollowTimer();
        _follow.Stop();
        _follow.Start();
    }

    private static bool HasSecondScreen =>
        IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>()?.IsAvailable == true;

    private IDispatcherTimer CreateFollowTimer()
    {
        var timer = _host.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(HostFollowDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FollowHost();
        return timer;
    }

    private void FollowHost()
    {
        _follow?.Stop();
        if (_hostSlot == _slot || _deck.Moved is null) return;
        _hostSlot = _slot;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _deck.Moved(_slot);
        PerfTrace.Log("screen.moved-host", watch);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.L: Step(-1); return true;
            case PadButton.R: Step(1); return true;
            case PadButton.Left: _view.TurnPage(-1); return true;
            case PadButton.Right: _view.TurnPage(1); return true;
            case PadButton.Up: _view.ScrollBy(-1); return true;
            case PadButton.Down: _view.ScrollBy(1); return true;
            case PadButton.X:
                if (_deck.CanEdit) Close(true);
                return true;
            case PadButton.B:
            case PadButton.Start:
                Close(false);
                return true;
            default:
                return true; // the summary owns the pad while it is open
        }
    }

    private void Close(bool edit)
    {
        if (_closed) return;
        _closed = true;
        FollowHost();
        _router?.Remove(this);
        _secondClaim?.Release();
        if (_secondScreen is not null) _secondScreen.Overview = null;
        _host.Remove(_overlay);
        _result.TrySetResult(new SummaryResult(_slot, edit));
    }
}

/// <summary>Decodes summaries off the UI thread, from a bank entry's bytes or a live save slot.</summary>
public static class SummaryLoaders
{
    // Bank summaries by (entry, content hash): an entry's bytes only change through an edit,
    // which changes the hash, so a cursor sweeping back over the bank never decodes twice.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, int, int), MonSummary?> BankFull = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, int, int), MonSummary?> BankQuick = new();

    /// <summary>A bank entry opens in its own throwaway entity session, exactly like the bank editor.</summary>
    public static Task<MonSummary?> FromBank(IBankService bank, ISaveEngine engine, IMonSummaryService summaries,
        BankEntry entry, bool analyzeLegality) => Task.Run(() =>
    {
        var data = bank.GetData(entry.Id);
        var hash = new HashCode();
        hash.AddBytes(data);
        var key = (entry.Id, data.Length, hash.ToHashCode());
        if (BankFull.TryGetValue(key, out var full)) return full;
        if (!analyzeLegality && BankQuick.TryGetValue(key, out var quick)) return quick;
        MonSummary? built;
        using (var session = engine.OpenEntitySession(data, entry.Info.Nickname, entry.Info.Format))
            built = session is null ? null : summaries.Build(session, 0, 0, analyzeLegality);
        var cache = analyzeLegality ? BankFull : BankQuick;
        if (cache.Count > 256) cache.Clear();
        cache[key] = built;
        return built;
    });

    public static Task<MonSummary?> FromSlot(ISaveEngineSession session, IMonSummaryService summaries,
        int box, int slot, bool analyzeLegality) => Task.Run(() => summaries.Build(session, box, slot, analyzeLegality));
}
