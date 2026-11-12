using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>The widgets the menu offers, in tab order.</summary>
public enum HomeWidgetTab { Pokepark, Walker, Partner, Egg }

/// <summary>
/// The home-screen widgets menu (Settings › Look and feel, and the Poképark's options), painted by
/// <see cref="WidgetMenuPaint"/>: each widget on a tab with a live pixel preview (the real widget's
/// own frames, flipping as the launcher flips them), its settings, and "Add to home screen" through
/// the launcher's pin request, with how to add it by hand where the launcher cannot. Pad first:
/// L/R or Left/Right change widget, Up/Down a row, A acts, B leaves; a tap selects a row, a second
/// tap acts. Nothing here writes a save.
/// </summary>
public sealed class WidgetMenu : IPadHandler
{
    private sealed record RowDef(string Id, WidgetMenuRow Row);

    private static readonly string[] TabNames = ["Poképark", "Pokéwalker", "Partner", "Day Care egg"];

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly IDispatcherTimer _timer;
    private HomeWidgetSettings _settings = HomeWidgetStore.Settings;
    /// <summary>The Poképark tab shows (and adds) the living park; the still picture is one row away.</summary>
    private bool _parkLiving = true;
    private HomeWidgetTab _tab;
    private int _row;
    private bool _armed;
    private bool _busy;
    private bool _done;
    private string _note = "";
    private WidgetPicture? _picture;
    private SKImage?[] _frames = [];
    private int _frame;
    private int _version;
    private WidgetMenuHits? _hits;

    public static Task ShowAsync(Grid host, HomeWidgetTab start) => new WidgetMenu(host, start)._closed.Task;

    private const string HintKey = "widgets.hint-shown";

    /// <summary>
    /// Once ever, after the first walk or the first partner: says the widget exists and offers to open
    /// this menu at its tab. Never again after that, whichever was answered; never when that widget is
    /// already on the home screen.
    /// </summary>
    public static async Task OfferOnceAsync(Grid host, HomeWidgetKind kind, string line)
    {
        try
        {
            if (Preferences.Default.Get(HintKey, false) || HomeWidgets.HasAny(kind)) return;
            Preferences.Default.Set(HintKey, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Java.Lang.Exception) { return; }
        var tab = kind switch { HomeWidgetKind.Walker => HomeWidgetTab.Walker, HomeWidgetKind.Partner => HomeWidgetTab.Partner, _ => HomeWidgetTab.Egg };
        var choice = await PadMenu.ShowAsync(host, "On your home screen too", line + " Home-screen widgets are always in Settings, and in the Poképark's options.",
            new PadOption("Show me", IconPath: "park", Detail: "The widgets, with a live preview."), new PadOption("Not now", IconPath: "close"));
        if (choice == "Show me") await ShowAsync(host, tab);
    }

    private WidgetMenu(Grid host, HomeWidgetTab start)
    {
        _host = host;
        _tab = start;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _canvas.PaintSurface += PaintGuard.Wrap("WidgetMenu.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _overlay = Kit.AttachOverlay(host, _canvas);
        _overlay.Unloaded += (_, _) => Close();
        _router?.Push(this);
        // The preview flips its frames while the menu is open, as the launcher does; stopped on close.
        _timer = host.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.Tick += (_, _) =>
        {
            if (_frames.Length < 2) return;
            _frame = (_frame + 1) % _frames.Length;
            _canvas.InvalidateSurface();
        };
        _timer.Start();
        _ = RebuildAsync();
    }

    private HomeWidgetKind? Kind => _tab switch
    {
        HomeWidgetTab.Walker => HomeWidgetKind.Walker,
        HomeWidgetTab.Partner => HomeWidgetKind.Partner,
        HomeWidgetTab.Egg => HomeWidgetKind.Egg,
        _ => null,
    };

    /// <summary>The widget this tab shows: its own size choice and its own provider.</summary>
    private WidgetSlot Slot => Kind is { } kind ? HomeWidgetSettings.SlotOf(kind) : _parkLiving ? WidgetSlot.LivePark : WidgetSlot.Park;

    /// <summary>The size chosen for this tab's widget; null is Auto (fit the launcher's tile).</summary>
    private HomeWidgetSizeHint? Choice => _settings.LayoutOf(Slot);

    /// <summary>
    /// The layout the preview shows: the one chosen, or with Auto the one the widget on the home screen
    /// has now, else the size it starts at.
    /// </summary>
    private HomeWidgetSizeHint Size => Choice ?? _placed ?? (Kind is null ? HomeWidgetSizeHint.Wide : HomeWidgetSizeHint.Small);

    private HomeWidgetSizeHint? _placed;

    private string SizeValue => Choice is { } chosen ? HomeWidgetSizes.Label(chosen) : $"Auto · {HomeWidgetSizes.Label(Size)}";

    private string Name(HomeWidgetTab tab) => tab switch
    {
        HomeWidgetTab.Pokepark => _parkLiving ? "Living Poképark" : "Poképark",
        HomeWidgetTab.Walker => "Pocket Pokéwalker",
        HomeWidgetTab.Partner => "Partner Pokémon",
        _ => "Day Care egg",
    };

    private bool Placed => Kind is { } kind ? HomeWidgets.HasAny(kind)
        : PokeparkWidgetPublisher.HasActive(_parkLiving ? typeof(LiveParkWidgetProvider) : typeof(PokeparkWidgetProvider));

    private List<RowDef> Rows()
    {
        var rows = new List<RowDef>();
        var size = new RowDef("size", new("Size", SizeValue,
            Choice is null
                ? "Auto: the widget follows its tile on your home screen (2x2, 4x2 or 4x4). Choose a size to keep that layout in any tile."
                : $"The {HomeWidgetSizes.Label(Size)} layout in any tile: centred, never stretched. Kept until you change it."));
        var pin = new RowDef("pin", new("Add to home screen", Placed ? "On your home screen" : "",
            "Your launcher asks where to put it. Where it can't, PKForge says how to add it yourself.", Action: true));
        switch (_tab)
        {
            case HomeWidgetTab.Pokepark:
                rows.Add(new("style", new("Style", _parkLiving ? "Living" : "Still picture",
                    _parkLiving
                        ? "Your residents walk, look about, sit, and sleep at night: a short loop your launcher plays by itself. A new one every half hour."
                        : "One picture of your residents with the park's latest moments. Both styles stay available.")));
                rows.Add(size);
                rows.Add(pin);
                break;
            case HomeWidgetTab.Walker:
                rows.Add(new("big", new("Big number", _settings.WalkerBigNumber ? "Steps today" : "This walk",
                    "What the LCD's big digits count: your steps today, or the steps of the walk.")));
                rows.Add(new("alert", new("Search alert", _settings.WalkerAlert ? "On" : "Off",
                    "A red ! when the watts afford a Poké Radar search or Dowsing.")));
                rows.Add(size);
                rows.Add(pin);
                rows.Add(new("open", new("Open the Pokéwalker", "", "Send a Pokémon on a walk, spend watts.", Action: true)));
                break;
            case HomeWidgetTab.Partner:
                rows.Add(new("partner", new("Partner", _settings.Partner?.Name ?? "None",
                    "From the Poképark or your open save's party. In the Bank or a box: A on a Pokémon, then Set as widget partner.")));
                rows.Add(size);
                rows.Add(pin);
                break;
            default:
                var snapshot = _settings.EggSave is { } id && HomeWidgetStore.Eggs.TryGetValue(id, out var picked) ? picked : null;
                rows.Add(new("save", new("Save", _settings.EggSave is null ? "Last save opened" : snapshot?.SaveName ?? "A save",
                    "Which save's eggs it follows. A save is read when you open it in PKForge.")));
                rows.Add(size);
                rows.Add(pin);
                break;
        }
        return rows;
    }

    private string Blurb() => _tab switch
    {
        HomeWidgetTab.Pokepark => _parkLiving
            ? "Your residents living their life under the park's trees and the sky of the hour. Tap: the Poképark."
            : "Your residents on the grass under the sky of the hour, with the park's latest moments. Tap: the Poképark.",
        HomeWidgetTab.Walker => "The Pokéwalker's LCD: the Pokémon walking, the steps and watts. Tap: the Pokéwalker.",
        HomeWidgetTab.Partner => "Your partner on its battle base. It sleeps at night and tires when the battery is low. Tap: its summary.",
        _ => "The egg wobbles more as it nears hatching. Tap: the save's Day Care.",
    };

    // ── Preview ──────────────────────────────────────────────────────────────────────────────

    private async Task RebuildAsync()
    {
        var version = ++_version;
        var kind = Kind;
        _placed = Choice is null ? HomeWidgets.PlacedLayout(Slot) : null;
        var size = Size;
        var settings = _settings;
        // At a step of two: the scene's pixels stay whole and its pixel-font words stay legible.
        var (w, h) = HomeWidgetPaint.NaturalSize(kind, size);
        if (kind is null && _parkLiving)
        {
            // The living park's own scene, so its step is two as well (its words stay legible at 2x2).
            var (dpW, dpH) = HomeWidgetSizes.Dp(size);
            (w, h) = LivePark.SceneSize(WidgetLayout.ParkSize(size), (double)dpW / dpH);
        }
        (w, h) = (w * 2, h * 2);
        WidgetPicture? picture = null;
        try
        {
            await PixelFont.WarmAsync();
            var context = Android.App.Application.Context;
            var living = _parkLiving;
            // With no park widget placed yet, nothing has written the park's snapshot: the preview
            // writes it (the app's own files, never a save) so it shows the real residents.
            if (kind is null && (PokeparkWidgetPublisher.Read(context) is not { } known || DateTimeOffset.Now - known.WrittenAt > TimeSpan.FromMinutes(25)))
                await PokeparkWidgetPublisher.RefreshAsync(TimeSpan.FromSeconds(4), always: true);
            picture = kind is { } k
                ? await Task.Run(() => HomeWidgets.RenderAsync(context, k, size, w, h, settings, live: false))
                : living
                    ? await Task.Run(() => LiveParkWidget.Render(context, WidgetLayout.ParkSize(size), w, h, 64L << 20, DateTime.Now))
                    : await Task.Run(() => HomeWidgets.RenderPark(context, size, w, h));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            AppLog.Warn("widgets", $"Preview failed: {ex.Message}");
        }
        if (version != _version || _done)
        {
            picture?.Dispose();
            return;
        }
        var old = (_picture, _frames);
        _picture = picture;
        _frames = picture is null ? [] : [.. picture.Frames.Select(SKImage.FromBitmap)];
        _frame = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(150, picture?.IntervalMs ?? 800));
        _canvas.InvalidateSurface();
        // The old frames go once nothing draws them any more.
        _host.Dispatcher.Dispatch(() =>
        {
            foreach (var image in old._frames) image?.Dispose();
            old._picture?.Dispose();
        });
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        if (_done) return;
        var rows = Rows();
        _row = Math.Clamp(_row, 0, Math.Max(0, rows.Count - 1));
        var caption = $"{Name(_tab)} · {SizeValue}";
        var scene = new WidgetMenuScene(TabNames, (int)_tab, [.. rows.Select(r => r.Row)], _row,
            _frames.Length == 0 ? null : _frames[_frame % _frames.Length], caption, _note.Length > 0 ? _note : Blurb(), _armed);
        _hits = WidgetMenuPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var at = args.Location;
        if (hits.Back.Contains(at) || !hits.Panel.Contains(at)) { Close(); return; }
        if (hits.Act.Contains(at)) { _ = ActAsync(); return; }
        for (var i = 0; i < hits.Tabs.Count; i++)
            if (hits.Tabs[i].Contains(at)) { SelectTab(i); return; }
        for (var i = 0; i < hits.Rows.Count; i++)
        {
            if (!hits.Rows[i].Contains(at)) continue;
            // A tap selects; a second tap on the same row acts.
            if (i == _row && _armed) _ = ActAsync();
            else { _row = i; _armed = true; _canvas.InvalidateSurface(); }
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left or PadButton.L: SelectTab((int)_tab - 1); break;
            case PadButton.Right or PadButton.R: SelectTab((int)_tab + 1); break;
            case PadButton.Up: MoveRow(-1); break;
            case PadButton.Down: MoveRow(1); break;
            case PadButton.A: _ = ActAsync(); break;
            case PadButton.B: Close(); break;
        }
        return true; // the menu owns the pad while open
    }

    private void SelectTab(int index)
    {
        var count = TabNames.Length;
        _tab = (HomeWidgetTab)((index % count + count) % count);
        _row = 0;
        _armed = false;
        _note = "";
        _ = RebuildAsync();
        _canvas.InvalidateSurface();
    }

    private void MoveRow(int delta)
    {
        var count = Rows().Count;
        if (count == 0) return;
        _row = ((_row + delta) % count + count) % count;
        _armed = false;
        _canvas.InvalidateSurface();
    }

    /// <summary>Keeps the choices and repaints this tab's widgets on the home screen at once.</summary>
    private void Save(HomeWidgetSettings settings)
    {
        _settings = settings;
        try { HomeWidgetStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("widgets", $"Settings not saved: {ex.Message}");
            _note = "That choice could not be kept. Try again.";
        }
        HomeWidgets.Refresh(Slot);
    }

    private async Task ActAsync()
    {
        if (_busy || _done) return;
        var rows = Rows();
        if (rows.Count == 0) return;
        var row = rows[Math.Clamp(_row, 0, rows.Count - 1)];
        _busy = true;
        _armed = false;
        try
        {
            switch (row.Id)
            {
                case "size":
                    _note = "";
                    Save(_settings.WithLayout(Slot, HomeWidgetSizes.Next(Choice)));
                    if (_note.Length == 0 && Placed)
                        _note = Choice is { } chosen ? $"The widget on your home screen now shows the {HomeWidgetSizes.Label(chosen)} layout."
                            : "The widget on your home screen follows its tile again.";
                    break;
                case "style": _parkLiving = !_parkLiving; break;
                case "big": Save(_settings with { WalkerBigNumber = !_settings.WalkerBigNumber }); break;
                case "alert": Save(_settings with { WalkerAlert = !_settings.WalkerAlert }); break;
                case "pin": await PinAsync(); break;
                case "open":
                    if (IPlatformApplication.Current?.Services.GetService<PokeparkService>() is { } park)
                        await WalkerScreen.ShowAsync(_host, [.. await Task.Run(park.LoadRoster)]);
                    break;
                case "partner": await ChoosePartnerAsync(); break;
                case "save": await ChooseSaveAsync(); break;
            }
        }
        finally { _busy = false; }
        if (_done) return;
        await RebuildAsync();
    }

    private async Task PinAsync()
    {
        if (HomeWidgets.RequestPin(Slot, Choice))
        {
            _note = "Your launcher will ask where to put it.";
            return;
        }
        var name = Name(_tab);
        await PadMenu.ShowAsync(_host, "Add it from your launcher",
            $"This launcher doesn't let apps place widgets, so add {name} yourself. " +
            $"Cocoon: Start › New › Add Widget, then PKForge › {name}. " +
            $"iiSU: add a widget on Home, then PKForge › {name}. " +
            $"Most launchers: hold the home screen › Widgets › PKForge. It fits {HomeWidgetSizes.Label(Size)} best; resize it anytime.", "OK");
    }

    private async Task ChoosePartnerAsync()
    {
        var services = IPlatformApplication.Current!.Services;
        var session = services.GetService<ISaveSessionService>();
        var options = new List<PadOption> { new("From the Poképark", IconPath: "park", Detail: "One of the park's residents.") };
        if (session?.CurrentSession is not null)
            options.Add(new("From the open save's party", IconPath: "game", Detail: "One of the six with you."));
        options.Add(new("From the Bank", IconPath: "bank", Detail: "How to pick one there."));
        if (_settings.Partner is not null) options.Add(new("No partner", IconPath: "close", Detail: "The widget shows an empty base."));
        var choice = await PadMenu.ShowAsync(_host, "Choose a partner", null, [.. options]);
        switch (choice)
        {
            case "From the Poképark":
            {
                var park = services.GetRequiredService<PokeparkService>();
                var roster = await Task.Run(park.LoadRoster);
                if (roster.Count == 0)
                {
                    await PadMenu.ShowAsync(_host, "Poképark", "Nobody lives in the park yet.", "OK");
                    return;
                }
                var pick = await PadMenu.ShowAsync(_host, "From the Poképark", null,
                    [.. roster.Select(r => new PadOption(r.Name, Detail: string.IsNullOrEmpty(r.Origin) ? r.Source : r.Origin))]);
                if (roster.FirstOrDefault(r => r.Name == pick) is not { } mon) return;
                var source = await Task.Run(() => park.SourceOf(mon));
                var partner = source is null ? null
                    : HomeWidgets.PartnerFrom(source.Data, source.Format, source.BankId is null ? source.SourceName : "Bank", source.BankId, null);
                partner ??= new PartnerChoice(mon.Name, mon.Species, mon.Form, mon.Shiny, 0, mon.Source);
                await SetPartnerAsync(partner);
                break;
            }
            case "From the open save's party":
            {
                if (session?.CurrentSession is not { } open || session.Current is not { } current) return;
                var party = open.Snapshot.Slots.Where(s => s.Box == -1 && s.Species is > 0 && !s.IsEgg).ToList();
                if (party.Count == 0)
                {
                    await PadMenu.ShowAsync(_host, "Party", "The open save's party has no Pokémon.", "OK");
                    return;
                }
                var labels = party.Select((s, i) => $"{i + 1}. {s.Nickname}").ToArray();
                var pick = await PadMenu.ShowAsync(_host, "From the party", null, labels);
                var at = Array.IndexOf(labels, pick);
                if (at < 0) return;
                var origin = HomeWidgetStore.Last is { } last && last.DocumentId == current.Document.DocumentId ? last.Name : current.Document.DisplayName;
                var export = await Task.Run(() => open.ExportSlot(-1, party[at].Slot));
                if (HomeWidgets.PartnerFrom(export.Data, export.Format, origin, null, current.Document.DocumentId) is { } partner)
                    await SetPartnerAsync(partner);
                break;
            }
            case "From the Bank":
                await PadMenu.ShowAsync(_host, "From the Bank", "Open the Bank, press A on a Pokémon, then choose Set as widget partner. It works in a save's boxes too.", "OK");
                break;
            case "No partner":
                await HomeWidgets.SetPartnerAsync(null);
                _settings = HomeWidgetStore.Settings;
                break;
        }
    }

    private async Task SetPartnerAsync(PartnerChoice partner)
    {
        var overlay = LoadingOverlay.Show(_host, "Partner", $"Getting {partner.Name} ready…");
        try { await HomeWidgets.SetPartnerAsync(partner); }
        finally { overlay.Close(); }
        _settings = HomeWidgetStore.Settings;
        _note = $"{partner.Name} is your partner.";
    }

    private async Task ChooseSaveAsync()
    {
        var eggs = HomeWidgetStore.Eggs.Values.OrderByDescending(s => s.WrittenAt).ToList();
        var options = new List<PadOption> { new("Last save opened", IconPath: "history", Detail: "Follows whichever save you open.") };
        options.AddRange(eggs.Select(s => new PadOption(s.SaveName, IconPath: "game",
            Detail: s.Eggs.Length > 0 ? $"{s.Eggs.Length} egg{(s.Eggs.Length == 1 ? "" : "s")} in the party" : s.EggWaiting ? "An egg is waiting at the Day Care" : "No egg yet")));
        if (eggs.Count == 0)
            options.Add(new("Open a save first", IconPath: "info", Detail: "PKForge reads a save's eggs when you open it."));
        var pick = await PadMenu.ShowAsync(_host, "Which save?", null, [.. options]);
        if (pick is null || pick == "Open a save first") return;
        var chosen = eggs.FirstOrDefault(s => s.SaveName == pick);
        Save(_settings with { EggSave = pick == "Last save opened" ? null : chosen?.DocumentId });
    }

    private void Close()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        _router?.Remove(this);
        _host.Remove(_overlay);
        var frames = _frames;
        var picture = _picture;
        _host.Dispatcher.Dispatch(() =>
        {
            foreach (var image in frames) image?.Dispose();
            picture?.Dispose();
        });
        _closed.TrySetResult();
    }
}
