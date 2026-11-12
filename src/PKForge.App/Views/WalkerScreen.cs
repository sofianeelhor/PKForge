using System.Globalization;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The PKForge Pokéwalker, held in the hand (<see cref="WalkerPaint"/>): its LCD shows the Pokémon
/// on its walk, and ◀ ▶ turn through the device's pages (the walk, the Poké Radar, Dowsing, the
/// finds, the HeartGold/SoulSilver sync, the course); the centre button does what the page shows.
/// The pad maps onto the device: left/right (or L/R) are ◀ ▶, A is the centre button, B leaves or
/// stops a search. The drawn buttons and the screen's bushes and grass answer touch. Opened from
/// the Poképark's Options.
/// </summary>
public sealed class WalkerScreen : IPadHandler
{
    private readonly Grid _host;
    private readonly PokewalkerStrolls _strolls;
    private readonly IReadOnlyList<ParkPokemon> _residents;
    private readonly ISpriteService? _sprites;
    private readonly ISaveSessionService? _saves;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly IDispatcherTimer _timer;
    private Grid _overlay = null!;
    private WalkerState _state = new();
    private WalkerHits? _hits;
    private WalkerPage[] _pages = [WalkerPage.Walk];
    private int _page;
    private int _frame;
    private int _ticks;
    private int _pressed = -1;
    private string _status = "";
    private string _lcd = "";
    private bool _busy;

    // The search on the screen, if any.
    private WalkerRadarRound? _radar;
    private WalkerDowsingRound? _dowsing;
    private int _target = -1;
    private int _rustle = -1;

    // The artist's device (shell and buttons), read once off the UI thread; null shows the LCD alone.
    private WalkerDeviceArt? _device;

    // The walking Pokémon in LCD tones, made once per sprite.
    private WalkerSprite? _art;
    private (int Species, int Form, bool Shiny) _artKey;

    public static async Task ShowAsync(Grid host, IReadOnlyList<ParkPokemon> residents)
    {
        if (IPlatformApplication.Current?.Services.GetService<PokewalkerStrolls>() is not { } strolls) return;
        await new WalkerScreen(host, strolls, residents).RunAsync();
    }

    private WalkerScreen(Grid host, PokewalkerStrolls strolls, IReadOnlyList<ParkPokemon> residents)
    {
        _host = host;
        _strolls = strolls;
        _residents = residents;
        _sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();
        _saves = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>();
        _canvas.PaintSurface += PaintGuard.Wrap("WalkerScreen.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("<>", "Pages", () => OnPadButton(PadButton.Right)),
            ("A", "Centre button", () => OnPadButton(PadButton.A)),
            ("B", "Back", () => OnPadButton(PadButton.B)));
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, footer },
        };
        Grid.SetRow(footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
        // The two-frame step, and a fresh count of the steps every half minute.
        _timer = host.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) =>
        {
            _frame ^= 1;
            if (++_ticks % 60 == 0 && !_busy && _radar is null && _dowsing is null) _ = CountAsync();
            Invalidate();
        };
    }

    private async Task RunAsync()
    {
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        _timer.Start();
        try
        {
            // Inside the try: a failed read (storage full, say) still closes the screen and hands
            // the pad back, instead of leaving the overlay and its timer up for good.
            _device = await Task.Run(LoadDevice);
            _state = await Task.Run(_strolls.Load);
            SetPages();
            Invalidate();
            await CountAsync();
            await _closed.Task;
        }
        catch (Exception error)
        {
            AppLog.Error("walker", "The Pokéwalker could not open", error);
        }
        finally
        {
            _timer.Stop();
            router?.Remove(this);
            _host.Remove(_overlay);
        }
    }

    /// <summary>Adds the steps walked since the last look. A search, a sync or a walk home made
    /// meanwhile wins: the count is dropped and taken again on the next tick, from the same
    /// anchors, so no step is lost and no Radar or Dowsing result is overwritten.</summary>
    private bool _resumed;

    private async Task CountAsync()
    {
        if (!_resumed)
        {
            _resumed = true;
            // A homecoming the app was closed in the middle of is finished first, once.
            try
            {
                if (await _strolls.ResumeAsync() is { } finished)
                {
                    _status = finished;
                    _state = await Task.Run(_strolls.Load);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
            {
                // Kept for the next look: every step already done is recorded, nothing is banked twice.
                _status = $"The walk's homecoming could not finish yet. {Domain.ErrorText.Plain(error)}";
            }
        }
        var before = _state;
        var counted = await PokewalkerStrolls.CountAsync(before);
        if (_busy || !ReferenceEquals(_state, before) || ReferenceEquals(counted, before)) return;
        _state = counted;
        Persist();
        SetPages();
        Invalidate();
    }

    private WalkerPage Current => _pages[Math.Clamp(_page, 0, _pages.Length - 1)];

    private void SetPages()
    {
        var current = _pages.Length > 0 ? Current : WalkerPage.Walk;
        var pages = _state.Walk is null
            ? new List<WalkerPage> { WalkerPage.Walk, WalkerPage.Sync, WalkerPage.Info }
            : [WalkerPage.Walk, WalkerPage.Radar, WalkerPage.Dowsing, WalkerPage.Bag, WalkerPage.Sync, WalkerPage.Info];
#if DIAGNOSTIC
        // The emulator has no pedometer to feed: steps by hand, in diagnostic builds only.
        if (_state.Walk is not null) pages.Add(WalkerPage.Debug);
#endif
        _pages = [.. pages];
        _page = Math.Max(0, Array.IndexOf(_pages, current));
    }

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    // The walk file is written off the UI thread, one write at a time, the latest state last.
    private Task _writing = Task.CompletedTask;
    private void Persist()
    {
        var state = _state;
        _writing = _writing.ContinueWith(_ => _strolls.Save(state), TaskScheduler.Default);
    }

    /// <summary>Done when every queued walk file write is (a failed one included: Persist never threw either).</summary>
    private Task Written() => _writing.ContinueWith(static _ => { }, TaskScheduler.Default);

    private bool Searching => _radar is not null || _dowsing is not null;

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var walk = _state.Walk;
        var course = walk is null ? null : WalkerCourses.Find(walk.Course);
        var next = WalkerRules.NextLocked(_state);
        var catches = walk?.Catches.Select(slot => WalkerService.SlotName(walk.Course, slot)).ToArray() ?? [];
        var scene = new WalkerScene(_pages, _page, walk is null ? null : Art(walk), _frame,
            WalkerPaint.SceneryFor(walk?.Course ?? 0), walk?.Steps ?? _state.LifetimeSteps, _state.Watts,
            catches, walk?.Items ?? [],
            Searching ? (_radar is not null ? WalkerRules.Bushes : WalkerRules.Patches) : 0, _target, _rustle, _radar?.Marks ?? 0,
            _lcd.Length > 0 ? _lcd : LcdLine(), course?.Name ?? "Pokéwalker",
            [
                ("Watts", $"{Number(_state.Watts)} W"),
                ("This walk", walk is null ? "Nobody walking" : $"{Number(walk.Steps)} steps"),
                ("Steps in all", Number(_state.LifetimeSteps)),
                ("Next course", next is null ? "All open" : $"{next.Name} · {Number(next.Watts)} W"),
            ],
            _status.Length > 0 ? _status : Hint(), _pressed, Headline(),
            [course?.Name ?? "NO WALK", $"WATTS {_state.Watts}", next is null ? "ALL COURSES OPEN" : $"NEXT {next.Name}",
             (next is null ? "" : $"AT {next.Watts}W - ") + (PokewalkerStrolls.Pedometer(_state) ? "PEDOMETER" : "STROLLING")]);
        _hits = WalkerPaint.Paint(c, args.Info, scene, _face, _device);
    }

    // The LCD's bottom line: what the centre button does here.
    private string LcdLine() => Current switch
    {
        WalkerPage.Walk => _state.Walk is null ? "START A WALK" : "",
        WalkerPage.Radar => _rustle >= 0 ? "WATCH THE GRASS" : _radar is not null ? "WHICH BUSH?" : $"SEARCH {WalkerRules.RadarCost}W",
        WalkerPage.Dowsing => _dowsing is { } d ? $"TRIES: {d.TriesLeft}" : $"SEARCH {WalkerRules.DowsingCost}W",
        WalkerPage.Bag => "COME HOME",
        WalkerPage.Sync => PokewalkerStrolls.CanSync(_saves?.CurrentSession) ? "SYNC" : "NO HG/SS SAVE",
        WalkerPage.Debug => "+1000 STEPS",
        _ => "HELP",
    };

    private string Headline() => Current switch
    {
        WalkerPage.Walk => _state.Walk is { } w ? $"{w.Name}, walking" : "Nobody on a walk",
        WalkerPage.Radar => "Poké Radar",
        WalkerPage.Dowsing => "Dowsing",
        WalkerPage.Bag => "Finds",
        WalkerPage.Sync => "Sync with HeartGold/SoulSilver",
        WalkerPage.Debug => "Debug steps",
        _ => "About the course",
    };

    private string Hint()
    {
        if (_radar is not null) return _rustle >= 0 ? "Watch which bush rustles…" : "Pick the bush that rustled: ◀ ▶ move, the centre button looks. B gives up.";
        if (_dowsing is not null) return "Pick a patch of grass: ◀ ▶ move, the centre button digs. Close means the patch right next to it. B gives up.";
        return Current switch
        {
            WalkerPage.Walk when _state.Walk is null => "The centre button sends a Poképark resident on a walk. ◀ ▶ turn the pages.",
            WalkerPage.Walk => PokewalkerStrolls.Pedometer(_state)
                ? "Steps come from your phone's pedometer, even with PKForge closed. 20 steps make a watt. ◀ ▶ turn the pages."
                : "Strolling: no pedometer here, so it walks 600 steps an hour, up to 3,000 a day. 20 steps make a watt. ◀ ▶ turn the pages.",
            WalkerPage.Radar => $"Spend {WalkerRules.RadarCost} W to look in the grass for the course's Pokémon; rarer ones show after more steps. A walk holds three.",
            WalkerPage.Dowsing => $"Spend {WalkerRules.DowsingCost} W to look for one of the course's items, in two tries. A walk holds three.",
            WalkerPage.Bag => "What the walk holds. The centre button brings the Pokémon home: EXP, friendship, catches to the Bank, items to the Item Bank.",
            WalkerPage.Sync => PokewalkerStrolls.CanSync(_saves?.CurrentSession)
                ? "Adds the walked steps to the game's Pokéwalker, writes the watts, and opens one course there, as the device does."
                : "Open a HeartGold or SoulSilver save to sync its Pokéwalker.",
            WalkerPage.Debug => "Adds 1,000 steps. Diagnostic builds only.",
            _ => "The centre button explains how the Pokéwalker works.",
        };
    }

    private WalkerSprite? Art(WalkerWalk walk)
    {
        var key = (walk.Species, walk.Form, walk.Shiny);
        if (_art is not null && _artKey == key) return _art;
        if (_sprites is null) return null;
        if (_sprites.GetSprite(walk.Species, walk.Form, walk.Shiny) is not { } bitmap)
        {
            _sprites.Warm(walk.Species, walk.Form, walk.Shiny, Invalidate);
            return null;
        }
        if (_artKey == key) return _art;
        // The sprite cache may dispose its bitmap after this frame (it does so on the UI thread):
        // the conversion works on a copy taken here, never on the cached bitmap itself.
        var copy = bitmap.Copy();
        if (copy is null) return _art;
        _artKey = key;
        // Made once, off the UI thread; the screen shows it on the next frame.
        _ = Task.Run(() =>
        {
            using var owned = copy;
            var bounds = StoragePaint.OpaqueBounds(owned, new SKRectI(0, 0, owned.Width, owned.Height));
            using var crop = new SKBitmap();
            var source = bounds.Width > 0 && bounds.Height > 0 && owned.ExtractSubset(crop, bounds) ? crop : owned;
            var art = WalkerLcd.Convert(source);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_artKey != key) return;
                _art = art;
                _canvas.InvalidateSurface();
            });
        });
        return _art;
    }

    /// <summary>The device's PNGs from <c>Resources/UI/walker/</c>; null when any is missing or does
    /// not match <see cref="WalkerDevice"/>'s sizes, so the screen falls back to the bare LCD.</summary>
    private static WalkerDeviceArt? LoadDevice()
    {
        var loaded = new List<SKImage>();
        try
        {
            SKImage Load(string name)
            {
                using var stream = FileSystem.OpenAppPackageFileAsync($"{WalkerDevice.Folder}/{name}").GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException(name);
                var image = SKImage.FromBitmap(bitmap);
                loaded.Add(image);
                return image;
            }
            var shell = Load(WalkerDevice.ShellFile);
            var buttons = WalkerDevice.ButtonFiles.Select(Load).ToArray();
            var pressed = WalkerDevice.PressedFiles.Select(Load).ToArray();
            if (WalkerDevice.Fits(shell, buttons, pressed)) return new WalkerDeviceArt(shell, buttons, pressed);
            AppLog.Warn("walker", "The Pokéwalker PNGs do not match the sizes in WalkerDevice; showing the LCD alone.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // No art yet: the LCD alone.
        }
        foreach (var image in loaded) image.Dispose();
        return null;
    }

    // ---- The buttons ----------------------------------------------------------------------------

    private void Press(int button)
    {
        _pressed = button;
        Invalidate();
        _host.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(140), () =>
        {
            if (_pressed == button) { _pressed = -1; _canvas.InvalidateSurface(); }
        });
    }

    private void Turn(int direction)
    {
        Press(direction < 0 ? 0 : 2);
        if (_rustle >= 0) return;
        if (Searching)
        {
            var count = _radar is not null ? WalkerRules.Bushes : WalkerRules.Patches;
            _target = (_target + direction + count) % count;
        }
        else
        {
            _page = (_page + direction + _pages.Length) % _pages.Length;
            _lcd = "";
            _status = "";
        }
        Invalidate();
    }

    private void Centre()
    {
        Press(1);
        if (_rustle >= 0) return;
        if (Searching) { Pick(_target); return; }
        _ = ActAsync();
    }

    private async Task ActAsync()
    {
        if (_busy) return;
        _busy = true;
        _status = "";
        _lcd = "";
        try
        {
            switch (Current)
            {
                case WalkerPage.Walk:
                    if (_state.Walk is null) await StartAsync();
                    else _lcd = "TURN THE PAGES";
                    break;
                case WalkerPage.Radar: await RadarAsync(); break;
                case WalkerPage.Dowsing: Dowse(); break;
                case WalkerPage.Bag:
                    await Written(); // home starts from the file: the last search is in it first
                    await HomeAsync();
                    break;
                case WalkerPage.Sync:
                    if (!PokewalkerStrolls.CanSync(_saves?.CurrentSession)) { _lcd = "NO HG/SS SAVE"; break; }
                    await Written(); // the sync starts from the file: the last search is in it first
                    _status = await _strolls.SyncAsync();
                    _state = _strolls.Load();
                    _lcd = "SYNCED!";
                    break;
                case WalkerPage.Debug:
                    _state = WalkerRules.AddSteps(_state, 1000);
                    Persist();
                    _lcd = "+1000!";
                    break;
                default:
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Pokéwalker",
                        "Send a Poképark resident on a walk on a course. Every 20 steps make a watt. With a phone's pedometer your own steps count, even while PKForge is closed; a device without one (like the Thor) lets it stroll at its own pace, 600 steps an hour up to 3,000 a day. " +
                        "Watts open new courses as on the real Pokéwalker (courses 9 to 20 also need the National Dex of a synced HeartGold/SoulSilver save). The Poké Radar (10 W) finds the course's Pokémon, rarer ones after more steps; Dowsing (3 W) finds its items. A walk holds three of each. " +
                        "Coming home gives one EXP a step, at most one level, and friendship, written back like the Poképark daycare (same setting, never to a Pokémon changed since). Catches are legal Pokéwalker Pokémon and go to your Bank; items go to your Item Bank.");
                    break;
            }
        }
        finally
        {
            _busy = false;
            SetPages();
            Invalidate();
        }
    }

    private async Task StartAsync()
    {
        var free = _residents.ToArray();
        if (free.Length == 0) { _status = "The park is empty: invite a Pokémon first."; _lcd = "PARK IS EMPTY"; return; }
        var pick = await PadMenu.ShowAsync(_host, "Who goes for a walk?", "Its real Pokémon stays where it is; the walk is written back when it comes home.",
            free.Select((r, i) => new PadOption($"{i + 1}. {r.Name}",
                Detail: string.Join(" · ", new[] { r.Origin.Length > 0 ? r.Origin : r.Source, r.TrainerName }.Where(t => !string.IsNullOrWhiteSpace(t))))).ToArray());
        if (pick is null || !int.TryParse(pick.Split('.')[0], out var number)) return;
        var mon = free[number - 1];
        var open = WalkerRules.Unlocked(_state);
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        var where = await PadMenu.ShowAsync(_host, "Which course?", WalkerRules.NextLocked(_state) is { } next ? $"Next to open: {next.Name}." : null,
            open.Select(c => new PadOption(c.Name, Detail: (WalkerRules.HasAdvantage(c, types) ? "Advantage! " : "") +
                string.Join(", ", Enumerable.Range(0, 6).Select(s => WalkerService.SlotName(c.Index, s)).Distinct()))).ToArray());
        if (open.FirstOrDefault(c => c.Name == where) is not { } course) return;
        var pedometer = false;
        if (WalkerSteps.HasSensor)
        {
            pedometer = await WalkerSteps.IsAllowedAsync();
            if (!pedometer && await PadMenu.ConfirmAsync(_host, "Count your steps?",
                    "PKForge can read your phone's step counter so your own steps count, even while it is closed. It only reads the number of steps; nothing else is read and nothing leaves your phone. Without it, the Pokémon strolls at its own pace.",
                    "Allow"))
                pedometer = await WalkerSteps.RequestAsync();
        }
        _state = await _strolls.StartAsync(mon, course, pedometer);
        _page = 0;
        _lcd = "";
        _status = $"{mon.Name} sets off on {course.Name}!";
        Invalidate();
        await WidgetMenu.OfferOnceAsync(_host, HomeWidgetKind.Walker,
            $"{mon.Name} can walk on your home screen: the Pocket Pokéwalker widget shows its steps and watts while PKForge is closed.");
    }

    private async Task RadarAsync()
    {
        if (WalkerRules.Radar(_state) is not { } started)
        {
            _lcd = _state.Walk is { Catches.Count: >= WalkerRules.MaxHeld } ? "NO ROOM: COME HOME" : $"NEED {WalkerRules.RadarCost}W";
            return;
        }
        (_state, _radar) = started;
        Persist();
        _target = -1;
        // The bush rustles for a moment, then the player picks.
        _rustle = _radar.Bush;
        Invalidate();
        await Task.Delay(1400);
        _rustle = -1;
        _target = 0;
    }

    private void Dowse()
    {
        if (WalkerRules.Dowse(_state) is not { } started)
        {
            _lcd = _state.Walk is { Items.Count: >= WalkerRules.MaxHeld } ? "NO ROOM: COME HOME" : $"NEED {WalkerRules.DowsingCost}W";
            return;
        }
        (_state, _dowsing) = started;
        Persist();
        _target = 0;
    }

    private void Pick(int target)
    {
        if (target < 0) return;
        if (_radar is { } round)
        {
            var (state, outcome) = WalkerRules.Search(_state, round, target);
            var name = _state.Walk is { } walk ? WalkerService.SlotName(walk.Course, round.Slot) : "?";
            _state = state;
            _lcd = outcome switch
            {
                WalkerRadarOutcome.Caught => $"CAUGHT {name}!",
                WalkerRadarOutcome.Fled => $"{name} FLED...",
                WalkerRadarOutcome.Full => "NO ROOM",
                _ => "NOTHING THERE",
            };
            _status = outcome switch
            {
                WalkerRadarOutcome.Caught => $"Caught {name}! It goes to your Bank when the walk ends.",
                WalkerRadarOutcome.Fled => $"{name} got away.",
                WalkerRadarOutcome.WrongBush => "Nothing in that bush: it was another one.",
                _ => "",
            };
            _radar = null;
        }
        else if (_dowsing is { } dig)
        {
            var (state, left, outcome) = WalkerRules.Dig(_state, dig, target);
            _state = state;
            _lcd = outcome switch
            {
                WalkerDowsingOutcome.Found => $"FOUND {dig.Item}!",
                WalkerDowsingOutcome.Close => "CLOSE! 1 TRY LEFT",
                WalkerDowsingOutcome.Far => "FAR... 1 TRY LEFT",
                WalkerDowsingOutcome.Full => "NO ROOM",
                _ => "NOTHING FOUND",
            };
            _status = outcome == WalkerDowsingOutcome.Missed ? $"It was {dig.Item}, hidden in another patch." : "";
            _dowsing = outcome is WalkerDowsingOutcome.Close or WalkerDowsingOutcome.Far ? left : null;
        }
        Persist();
        if (!Searching) _target = -1;
        SetPages();
        Invalidate();
    }

    private void StopSearch()
    {
        if (_rustle >= 0) return;
        _radar = null;
        _dowsing = null;
        _target = -1;
        _lcd = "";
        _status = "You stop searching. The watts are spent.";
        Invalidate();
    }

    private async Task HomeAsync()
    {
        if (_state.Walk is not { } walk) return;
        var held = walk.Catches.Count + walk.Items.Count;
        if (!await PadMenu.ConfirmAsync(_host, $"Bring {walk.Name} home?",
                $"{Number(walk.Steps)} steps on {WalkerCourses.Find(walk.Course)?.Name}{(held > 0 ? $", with {held} find{(held == 1 ? "" : "s")}" : "")}. The walk ends here.", "Come home"))
            return;
        await _writing; // the last radar or dowsing state lands before the walk ends, never after
        var line = await _strolls.ComeHomeAsync(_residents.FirstOrDefault(r => r.Id == walk.ResidentId));
        _state = _strolls.Load();
        _page = 0;
        await PokeparkCardOverlay.ShowDialogueAsync(_host, "Pokéwalker", line);
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits || _busy) return;
        var at = args.Location;
        if (hits.Left.Contains(at)) { Turn(-1); return; }
        if (hits.Right.Contains(at)) { Turn(1); return; }
        if (hits.Centre.Contains(at)) { Centre(); return; }
        if (Searching && _rustle < 0)
            for (var i = 0; i < hits.Targets.Count; i++)
                if (hits.Targets[i].Contains(at)) { _target = i; Press(1); Pick(i); return; }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left or PadButton.L: Turn(-1); return true;
            case PadButton.Right or PadButton.R: Turn(1); return true;
            // In the Radar's two rows of bushes, up and down change the row.
            case PadButton.Up or PadButton.Down when _radar is not null && _rustle < 0:
                _target = (_target + 2) % WalkerRules.Bushes;
                Invalidate();
                return true;
            case PadButton.A: Centre(); return true;
            case PadButton.B:
                if (Searching) StopSearch();
                else _closed.TrySetResult();
                return true;
            default: return true;
        }
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
