using System.Diagnostics;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

public sealed class PokeparkPage : ContentPage, IPadHandler, IPadStickHandler
{
    private readonly PokeparkService _park;
    private readonly ISpriteService _sprites;
    private readonly PokeparkSpriteService _walking;
    private readonly PokeparkNarrativeMemory _narrativeMemory;
    private readonly PokeparkSocialService _social;
    private readonly ParkOfflineJournalService _offlineJournal;
    private readonly PokeparkCareStore _care;
    private readonly PokeparkTreasureStore _treasures;
    private readonly PokeparkDaycareService _daycare;
    private readonly PokeparkWildsStore _wilds;
    private readonly PokeparkWeatherService _weather;
    private readonly HashSet<string> _restored = new();
    private IReadOnlyDictionary<string, (int Friendship, int Rivalry)> _relations = new Dictionary<string, (int, int)>();
    private long _lastCareSave, _lastWeatherCheck;
    private readonly PokeparkScene _scene;
    private const string CaptionsKey = "pkforge.pokepark.captions.v1";
    private readonly ParkCanvas _canvas = new();
    private readonly Grid _host;
    private readonly Label _status;
    private readonly VsyncLoop _timer;
    private float _sinceFrame;
    private readonly Stopwatch _clock = new();
    private readonly SemaphoreSlim _widgetPublishGate = new(1, 1);
    private readonly object _renderGate = new();
    private CancellationTokenSource? _secondaryShowCts;
    private bool _active;
    private bool _menuOpen;
    private long _lastJournalUpdate;
    private int _widgetRevision;
    private int _publishedWidgetRevision;
    private bool _loading;
    private int _selected;

    public PokeparkPage(PokeparkService park, ISpriteService sprites, PokeparkSpriteService walking,
        PokeparkNarrativeMemory narrativeMemory, PokeparkSocialService social,
        ParkOfflineJournalService offlineJournal, PokeparkCareStore care, PokeparkTreasureStore treasures,
        PokeparkDaycareService daycare, PokeparkWildsStore wilds, PokeparkWeatherService weather)
    {
        _park = park; _sprites = sprites; _walking = walking;
        _narrativeMemory = narrativeMemory; _social = social; _offlineJournal = offlineJournal; _care = care; _treasures = treasures;
        _daycare = daycare; _wilds = wilds; _weather = weather;
        _scene = new(sprites, walking) { NatureOf = park.NatureOf, ShowCaptions = Preferences.Default.Get(CaptionsKey, true) };
        LoadSky();
        Title = "Poképark";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = UiTokens.Housing;
        _status = new Label { FontSize = UiTokens.TextBody };
        // The camera fits any size at a whole-number scale, so the map takes all the room left.
        _canvas.Touch += (_, e) =>
        {
            e.Handled = true;
            // The first touch on a sleeping park only wakes it.
            if (e.ActionType == SKTouchAction.Pressed && WakeUp()) { _swallowTouch = true; return; }
            if (_swallowTouch) { if (e.ActionType is SKTouchAction.Released or SKTouchAction.Cancelled) _swallowTouch = false; return; }
            _lastInput = _clock.ElapsedMilliseconds;
            if (!_menuOpen) OnTouch(e);
            // A finger lifted while a dialogue is open (a pat that opened one) is still let go of.
            else if (e.ActionType is SKTouchAction.Released or SKTouchAction.Cancelled) _touches.Remove(e.Id);
        };
        _canvas.Paint += (canvas, width, height) =>
        {
            lock (_renderGate)
                _scene.Draw(canvas, width, height, _clock.ElapsedMilliseconds);
        };
        var hints = new List<(string Button, string Label, Action? OnTap)>
        {
            ("B", "Back", () => _ = Navigation.PopAsync()),
            ("A", "Meet Pokémon", () => _ = DetailsAsync()),
            // L and R fly the camera from one place of the park to the next.
            ("LR", "Places", () => FlyToPlace(1)),
            // Up and Down zoom, for players without a touch screen.
            ("↑↓", "Zoom", () => _scene.Viewport.Zoom(1)),
            ("X", "Photo", () => _ = TakePhotoAsync()),
            ("+", "Options", () => _ = OptionsAsync()),
            // − lets the camera follow whatever is happening in the park.
            ("−", "Spectate", ToggleSpectate)
        };
        var footer = DsChrome.Footer([.. hints]);
        footer.VerticalOptions = LayoutOptions.End;
        // The park fills the screen; the status floats over its top-left corner and fades after a
        // while, the button hints sit along the bottom edge.
        _statusPill = new Border
        {
            Content = _status, BackgroundColor = Color.FromRgba(16, 22, 30, 170), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Margin = new Thickness(10, 10), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
            InputTransparent = true
        };
        _status.TextColor = Colors.White;
        _status.Margin = new Thickness(10, 4);
        _status.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Label.Text)) ShowStatus(); };
        // What is going on, always in sight: what the one you follow is doing, and the best thing
        // happening elsewhere in the park.
        _nowPill = new Border
        {
            Content = new VerticalStackLayout { Spacing = 2, Margin = new Thickness(10, 5), Children = { _now, _elsewhere } },
            BackgroundColor = Color.FromRgba(16, 22, 30, 150), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Margin = new Thickness(10, 0, 10, 6), HorizontalOptions = LayoutOptions.Center,
            InputTransparent = true, MaximumWidthRequest = 1100
        };
        _now.TextColor = Colors.White; _now.FontSize = UiTokens.TextBody; _now.LineBreakMode = LineBreakMode.TailTruncation;
        _elsewhere.TextColor = Color.FromRgb(0xf4, 0xc8, 0x4a); _elsewhere.FontSize = UiTokens.TextBody * .85; _elsewhere.LineBreakMode = LineBreakMode.TailTruncation;
        // At the very bottom, just above the button hints.
        _now.HorizontalTextAlignment = _elsewhere.HorizontalTextAlignment = TextAlignment.Center;
        var bottom = _bottom = new VerticalStackLayout { VerticalOptions = LayoutOptions.End, Children = { _nowPill, footer } };
        footer.VerticalOptions = LayoutOptions.Start;
        // The resident card and dialogue box stand above the button hints, never over them.
        footer.SizeChanged += (_, _) => PokeparkCardOverlay.ReservedBottom = footer.Height > 0 ? footer.Height : 0;
        _host = new Grid { Children = { _canvas.View, _statusPill, bottom } };
        Content = _host;
        // PMD frame durations count 60ths of a second. The park ticks on the screen's own
        // refresh, so its frames land evenly (a 16 ms timer drifted and skipped some).
        _timer = new VsyncLoop(elapsed =>
        {
            if (_clock.ElapsedMilliseconds - _lastJournalUpdate >= 250)
            {
                UpdateJournal();
                RecordMoments();
                CheckGameOver();
                PollHighlights();
                UpdateNow();
                UpdateMusic();
                if (_clock.ElapsedMilliseconds - _lastWeatherCheck >= 600_000) { _lastWeatherCheck = _clock.ElapsedMilliseconds; _ = RefreshWeatherAsync(); }
            }
            if (_clock.ElapsedMilliseconds - _lastCareSave >= 30_000) SaveCare();
            CheckLongPress(_clock.ElapsedMilliseconds);
            CheckScreensaver();
            // 60 frames a second while the camera moves or a finger is down, 30 otherwise: the PMD
            // animations and the park's life look the same at 30, and it halves the drawing. As a
            // screensaver, slower still (and slowest with reduced motion), to spare the battery.
            var moving = FullRate();
            // A frame every stride sixtieths of a second, on a refresh: 60, 30 or fewer a second
            // whatever the screen's own rate, and never more often when refreshes come late.
            _sinceFrame += elapsed;
            var period = ParkScreensaver.FrameStride(_asleep, moving, (int)MotionSetting.Level) * 1000f / 60f;
            if (_sinceFrame >= period - 6f)
            {
                _sinceFrame = Math.Clamp(_sinceFrame - period, 0f, period / 2);
                _canvas.InvalidateSurface();
            }
            return true;
        });
    }

    // ── Highlights: something worth watching has started somewhere ──────────────

    private ParkHighlight? _offered;
    private long _offeredAt;
    private bool _spectating;
    private ParkHighlight? _watching;
    private long _watchingSince;
    private const long OfferFor = 6000, IdleSpectate = 30_000;

    // A good moment shows at the top with "X: go see"; in spectator mode the camera goes there by itself.
    private void PollHighlights()
    {
        var now = _clock.ElapsedMilliseconds;
        foreach (var highlight in _scene.Life.TakeHighlights().OrderByDescending(h => h.Event is not null).ThenByDescending(h => h.Priority))
        {
            PerfTrace.Park($"moment ({highlight.Priority}): {highlight.Text}");
            // One of the park's events: it takes the whole screen, once in a while at most.
            if (highlight.Event is not null && !_menuOpen && (_lastEventAt == 0 || now - _lastEventAt > EventsEvery))
            {
                _lastEventAt = now;
                _ = AnnounceAsync(highlight);
                continue;
            }
            if (_spectating)
            {
                if (highlight.Priority >= 2) _scene.Notify(highlight.Text);
                if (_watching is null || _watching.Until <= _scene.Life.Now || highlight.Priority > _watching.Priority) Watch(highlight);
            }
            else if (highlight.Priority >= 2)
            {
                _scene.Notify($"{(highlight.Priority >= 3 ? "★ " : "")}{highlight.Text}   X: go see");
                _offered = highlight; _offeredAt = now;
            }
        }
        if (_spectating && (_watching is null || _watching.Until <= _scene.Life.Now) && now - _watchingSince > IdleSpectate)
        {
            // Nothing big going on: whatever is still worth a look, else someone at random.
            if (_scene.Life.Highlights.FirstOrDefault() is { } any) Watch(any);
            else if (_scene.Residents.Count > 0)
            {
                _watching = null; _watchingSince = now;
                SelectResident(Random.Shared.Next(_scene.Residents.Count));
            }
        }
    }

    private long _lastEventAt;
    private const long EventsEvery = 90_000;

    private async Task AnnounceAsync(ParkHighlight highlight)
    {
        if (_menuOpen) return; _menuOpen = true;
        bool go;
        try
        {
            var cast = highlight.Cast.Select(id => _scene.Life[id]?.Resident).OfType<ParkLifeResident>();
            // The camera goes to the moment while it is announced, a little closer.
            if (_scene.Life[highlight.ActorId] is { } star) { _scene.Viewport.FlyTo(star.X, star.Y - star.Altitude); _scene.Viewport.Zoom(1); }
            go = await PokeparkEventOverlay.ShowAsync(_host, highlight, cast, _sprites, _walking, _spectating);
        }
        finally { _menuOpen = false; }
        if (!go) { _scene.Viewport.Zoom(-1); _scene.Viewport.Resume(); return; }
        if (_spectating) Watch(highlight);
        else GoSee(highlight);
    }

    private void Watch(ParkHighlight highlight)
    {
        _watching = highlight; _watchingSince = _clock.ElapsedMilliseconds;
        GoSee(highlight);
    }

    private void GoSee(ParkHighlight highlight)
    {
        _offered = null;
        for (var i = 0; i < _scene.Residents.Count; i++)
            if (_scene.Residents[i].Id == highlight.ActorId) { SelectResident(i); return; }
        // A wild visitor: the camera glides to it.
        if (_scene.Life[highlight.ActorId] is { } actor) _scene.Viewport.FlyTo(actor.X, actor.Y);
    }

    // A photo of the park as it looks right now, captioned with what is happening, into the album.
    private async Task TakePhotoAsync()
    {
        if (_menuOpen || _scene.Residents.Count == 0) return;
        var (width, height) = ((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height);
        if (width <= 0 || height <= 0) return;
        byte[] jpeg;
        using (var surface = SKSurface.Create(new SKImageInfo(width, height)))
        {
            lock (_renderGate) _scene.Draw(surface.Canvas, width, height, _clock.ElapsedMilliseconds);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            jpeg = data.ToArray();
        }
        var caption = (_watching ?? (Offering ? _offered : null) ?? _scene.Life.Highlights.FirstOrDefault())?.Text is { } happening
            ? happening.Split("!")[0].TrimEnd('.', ' ') + (happening.Contains('!') ? "!" : "")
            : _selected >= 0 && _selected < _scene.Residents.Count && _scene.Life[_scene.Residents[_selected].Id] is { } actor
                ? $"{actor.Resident.Name}: {actor.Activity}"
                : "The Poképark";
        await Task.Run(() => PokeparkAlbum.Save(jpeg, caption, DateTime.Now));
        _scene.Notify("Snap! Photo saved to the album (Options)");
    }

    // ── Screensaver: the park alone on the screen after a while with no input ──────────

    private const string ScreensaverKey = "pkforge.pokepark.screensaver.v1";
    private readonly VerticalStackLayout _bottom;
    private long _lastInput;
    private bool _asleep, _swallowTouch, _sleepSpectating;

    private static int ScreensaverMinutes
    {
        get => Preferences.Default.Get(ScreensaverKey, 0);
        set => Preferences.Default.Set(ScreensaverKey, value);
    }

    private void CheckScreensaver()
    {
        if (_asleep || _menuOpen || _touches.Count > 0 || _scene.Life.Game is not null) return;
        if (!ParkScreensaver.ShouldSleep(ScreensaverMinutes, _lastInput, _clock.ElapsedMilliseconds)) return;
        _asleep = true;
        _statusPill.IsVisible = _bottom.IsVisible = false;
        _scene.Screensaver = true;
        // The camera wanders to whatever is happening, as when spectating.
        _sleepSpectating = !_spectating;
        if (_sleepSpectating) { _spectating = true; _watching = null; _watchingSince = 0; }
        KeepScreenOn(true);
        AppLog.Info("pokepark", "Screensaver on");
    }

    /// <summary>Any input: notes it, and wakes a sleeping park (true when it was asleep, so the
    /// input only wakes it).</summary>
    private bool WakeUp()
    {
        _lastInput = _clock.ElapsedMilliseconds;
        if (!_asleep) return false;
        _asleep = false;
        _statusPill.IsVisible = _bottom.IsVisible = true;
        _scene.Screensaver = false;
        if (_sleepSpectating) { _spectating = false; _watching = null; }
        _sleepSpectating = false;
        KeepScreenOn(false);
        return true;
    }

    // The screen stays on only while the park is a screensaver.
    private static void KeepScreenOn(bool on)
    {
#if ANDROID
        var window = Platform.CurrentActivity?.Window;
        if (on) window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        else window?.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
#endif
    }

    private async Task ScreensaverAsync()
    {
        var choices = ParkScreensaver.Choices.Select(m => new PadOption(m == 0 ? "Off" : $"After {m} minutes",
            Detail: m == ScreensaverMinutes ? "Current" : null)).ToArray();
        var choice = await PadMenu.ShowAsync(_host, "Screensaver",
            "With no touch or button for a while, the park hides its menus and runs on its own, following what happens. " +
            "The screen stays on only then, and it draws fewer frames to spare the battery. Any touch or button wakes it.", choices);
        if (choice is null) return;
        var index = Array.FindIndex(choices, c => c.Label == choice);
        if (index < 0) return;
        ScreensaverMinutes = ParkScreensaver.Choices[index];
        _lastInput = _clock.ElapsedMilliseconds;
        _scene.Notify(ScreensaverMinutes == 0 ? "Screensaver off" : $"Screensaver after {ScreensaverMinutes} minutes with no input");
    }

    private bool Offering => _offered is not null && _clock.ElapsedMilliseconds - _offeredAt < OfferFor;

    private void ToggleSpectate()
    {
        _spectating = !_spectating;
        _watching = null; _watchingSince = 0;
        _scene.Notify(_spectating ? "Spectating: the camera follows what happens in the park" : "Spectating off");
    }

    private void StopSpectating()
    {
        if (!_spectating) return;
        _spectating = false; _watching = null;
        _scene.Notify("Spectating off");
    }

    private (float X, float Y, float Scale) _lastView;

    private bool FullRate()
    {
        var viewport = _scene.Viewport;
        var view = (viewport.CenterX, viewport.CenterY, viewport.Scale);
        var moved = MathF.Abs(view.CenterX - _lastView.X) + MathF.Abs(view.CenterY - _lastView.Y) > .05f || view.Scale != _lastView.Scale;
        _lastView = view;
        return moved || _touches.Count > 0 || viewport.IsMoving;
    }

    private readonly Border _nowPill;
    private readonly Label _now = new(), _elsewhere = new();

    // The park's own music: one for the day, one for the evening and the night (Options, Music).
    private static IMusicPlayer? Music => IPlatformApplication.Current?.Services.GetService<IMusicPlayer>();

    private async Task SnowmanAsync()
    {
        if (_menuOpen) return; _menuOpen = true;
        try
        {
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Snowman", Random.Shared.Next(10) == 0
                ? "You lock eyes with the snowman. It looks at you with a sense of pride and fulfillment. You feel that, just for now, things will be okay."
                : "Just a snowman. Not much to see here.");
        }
        finally { _menuOpen = false; }
    }

    private void UpdateMusic()
    {
        var place = _scene.Life.Phase is ParkDayPhase.Evening or ParkDayPhase.Night ? MusicPlace.ParkNight : MusicPlace.ParkDay;
        if (Music is { } music && music.Place != place) music.EnterPlace(place);
    }

    private void UpdateNow()
    {
        // Hidden while a card or a menu is up: the card says what it is doing.
        if (_menuOpen || _scene.Residents.Count == 0 || _selected < 0 || _selected >= _scene.Residents.Count) { _nowPill.IsVisible = false; return; }
        _nowPill.IsVisible = true;
        var mon = _scene.Residents[_selected];
        var actor = _scene.Life[mon.Id];
        var now = actor is null ? "" : $"RIGHT NOW  ·  {mon.Name}: {actor.Activity}";
        if (_now.Text != now) _now.Text = now;
        var elsewhere = _scene.Life.Highlights.FirstOrDefault(h => h.ActorId != mon.Id) is { } highlight
            ? $"ELSEWHERE  ·  {highlight.Text.Split("! ")[0].TrimEnd('!')}{(highlight.Text.Contains('!') ? "!" : "")}"
            : "";
        if (_elsewhere.Text != elsewhere) { _elsewhere.Text = elsewhere; _elsewhere.IsVisible = elsewhere.Length > 0; }
    }

    private SecondScreenClaim? _secondClaim;
    private readonly Border _statusPill;
    private int _statusShown;

    // The status shows for a few seconds after it changes, then fades so the park is all there is.
    private void ShowStatus()
    {
        var shown = ++_statusShown;
        _statusPill.CancelAnimations();
        _statusPill.Opacity = 1;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(5), () =>
        {
            if (shown == _statusShown) _ = _statusPill.FadeToAsync(0, 600);
        });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); _active = true;
        _secondClaim ??= IPlatformApplication.Current?.Services.GetService<SecondScreenState>()?.Routes.CreateClaim(SecondScreenOwner.Pokepark);
        _secondClaim?.Activate();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Open();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        App.Resumed += Resume; App.Suspended += Suspend;
        _clock.Start(); _timer.Start();
        _lastInput = _clock.ElapsedMilliseconds;
        _ = RefreshWeatherAsync();
        // The persisted roster is cheap and gives the first paint immediately.
        // Initialization only scans on first use and refreshes this view afterwards.
        Reload();
        _ = LoadParkAsync();
        UpdateMusic();
        // A park can be opened from the widget/deep link without passing through
        // HomePage. Ensure Thor's lower display is alive and observing the journal.
        var secondary = IPlatformApplication.Current?.Services.GetService<PKForge.Domain.ISecondaryDisplayHost>();
        if (secondary?.IsAvailable == true)
        {
            _secondaryShowCts?.Cancel();
            _secondaryShowCts?.Dispose();
            _secondaryShowCts = new CancellationTokenSource();
            _ = ShowSecondaryJournalAsync(secondary, _secondaryShowCts.Token);
        }
        if (PendingScreen is { } screen)
        {
            PendingScreen = null;
            Dispatcher.Dispatch(async () => await OpenScreenAsync(screen));
        }
    }

    /// <summary>A screen of the park a launcher widget asked for ("walker"); opened once the park is in front.</summary>
    public string? PendingScreen { get; set; }

    /// <summary>Opens one of the park's screens on behalf of a widget or a shortcut.</summary>
    public async Task OpenScreenAsync(string screen)
    {
        if (screen != "walker") return;
        // The park tells its "while you were away" stories first; the Pokéwalker opens after them.
        await Task.Delay(1500);
        for (var i = 0; i < 600 && _menuOpen && _active; i++) await Task.Delay(200);
        if (!_active || _menuOpen) return;
        _menuOpen = true;
        try { await WalkerScreen.ShowAsync(_host, [.. await Task.Run(_park.LoadRoster)]); }
        finally { _menuOpen = false; }
    }

    private static async Task ShowSecondaryJournalAsync(PKForge.Domain.ISecondaryDisplayHost host, CancellationToken cancellationToken)
    {
        try { await host.ShowPokeparkJournalAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Poképark second screen: {ex.Message}"); }
    }
    protected override void OnDisappearing()
    {
        WakeUp();
        SaveCare();
        _offlineJournal.MarkCurrent();
        _active = false; _timer.Stop(); _clock.Stop();
        _secondaryShowCts?.Cancel();
        _secondaryShowCts?.Dispose();
        _secondaryShowCts = null;
        App.Resumed -= Resume; App.Suspended -= Suspend;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        if (WidgetDirty) _ = PublishWidgetAsync();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Clear();
        _secondClaim?.Release();
        Music?.EnterPlace(MusicPlace.Menus);
        base.OnDisappearing();
    }
    private void Resume() { if (_active) { _clock.Start(); _timer.Start(); } }
    private void Suspend()
    {
        WakeUp();
        SaveCare();
        _offlineJournal.MarkCurrent();
        _timer.Stop(); _clock.Stop(); if (WidgetDirty) _ = PublishWidgetAsync();
    }

    // The story of the time away, and today's anniversaries, told once the park is showing.
    private IReadOnlyList<ParkAbsence.Story> _absence = [];

    private void RunOfflineLife()
    {
        var stories = _absence;
        _absence = [];
        foreach (var moment in stories.Select(s => s.Moment).OfType<ParkMoment>())
            if (_scene.Life[moment.FirstId] is { } first && _scene.Life[moment.SecondId] is { } second)
                _social.Witness(moment, first.Resident.Name, second.Resident.Name);
        var parties = Anniversaries();
        if (stories.Count == 0 && parties.Count == 0) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!_active || _menuOpen) return;
            _menuOpen = true;
            try
            {
                foreach (var story in stories)
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "While you were away", story.Text);
                foreach (var (mon, line) in parties)
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line, () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, "Joyous"));
            }
            finally { _menuOpen = false; RefreshRecent(); }
        });
    }

    // Residents met on this day in an earlier year (or today): each gets its party once a year.
    private List<(ParkPokemon Mon, string Line)> Anniversaries()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var parties = new List<(ParkPokemon, string)>();
        foreach (var mon in _scene.Residents)
        {
            if (_park.MetDateOf(mon) is not { } met || met.Month != today.Month || met.Day != today.Day) continue;
            var key = $"pkforge.pokepark.party.{mon.Id}";
            if (Preferences.Default.Get(key, 0) == today.Year) continue;
            if (_scene.Life.Celebrate(mon.Id, today.Year - met.Year).Line is not { } line) continue;
            Preferences.Default.Set(key, today.Year);
            parties.Add((mon, line));
        }
        return parties;
    }

    private async Task LoadParkAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var needsInitialization = _scene.Residents.Count == 0;
            await _scene.LoadArtAsync().ConfigureAwait(true);
            if (!_active) return;
            _canvas.InvalidateSurface();
            if (needsInitialization)
            {
                // Let the navigation animation and first frame complete before doing
                // optional first-use population work.
                await Task.Delay(350).ConfigureAwait(true);
                await Task.Run(_park.EnsureInitialized).ConfigureAwait(true);
                if (!_active) return;
                Reload();
            }
            RunOfflineLife();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Poképark load: {ex.Message}");
        }
        finally { _loading = false; }
    }

    private void Reload()
    {
        _scene.Residents = _park.LoadRoster();
        // Needs saved at the last visit come back, aged by the time away; once per resident.
        var now = DateTimeOffset.Now;
        // The needs saved at the last visit, aged by the time away, and the story of that time.
        var saved = _care.Load().Where(c => _restored.Add(c.Id)).ToArray();
        var before = saved.ToDictionary(c => c.Id);
        _scene.Life.Restore(saved, now);
        SetUpDaycareAndWilds();
        if (saved.Length > 0 && saved.Max(c => c.LastSeen) is var lastSeen && lastSeen != default)
        {
            var residents = _scene.Life.Actors.Where(a => before.ContainsKey(a.Resident.Id)).ToArray();
            _absence = ParkAbsence.Tell(residents.Select(a => a.Resident).ToArray(), before,
                residents.ToDictionary(a => a.Resident.Id, a => a.Care), lastSeen.LocalDateTime, DateTime.Now, new Random(), _scene.Life.OldFriends);
        }
        foreach (var mon in _scene.Residents) _restored.Add(mon.Id);
        // Friendships are kept by who each resident is, so one that comes back finds its friends.
        _social.Souls = _scene.Residents.ToDictionary(r => r.Id, r => r.Identity);
        _relations = _social.Relations(now);
        if (!_chronicleLoaded)
        {
            _scene.Life.Chronicle = ParkChronicle.Load(Preferences.Default.Get<string?>(ChronicleKey, null));
            _chronicleLoaded = true;
        }
        RefreshRecent();
        _scene.Life.Relation = (a, b) => _relations.GetValueOrDefault(ParkRelationship.KeyFor(a, b));
        SelectResident(Math.Min(_selected, _scene.Residents.Count - 1));
        UpdateStatus();
        UpdateJournal();
        foreach (var mon in _scene.Residents)
        {
            _sprites.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
            _walking.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
            // Portraits for the card and the lower screen, before anyone opens them.
            foreach (var emotion in new[] { "Normal", "Happy", "Joyous" }) _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion);
        }
        MarkWidgetDirty();
        _canvas.InvalidateSurface();
    }
    private void AssetsLoaded() => MainThread.BeginInvokeOnMainThread(() =>
    {
        MarkWidgetDirty();
        if (_active) _canvas.InvalidateSurface();
        else _ = PublishWidgetAsync();
    });
    private void SelectResident(int index)
    {
        var visible = Enumerable.Range(0, _scene.Residents.Count).ToArray();
        if (visible.Length == 0)
        {
            _selected = 0;
            _scene.SelectedIndex = -1;
            UpdateJournal();
            _canvas.InvalidateSurface();
            return;
        }
        var position = Array.IndexOf(visible, index);
        if (position < 0) position = index < 0 ? 0 : Array.FindIndex(visible, candidate => candidate > index);
        if (position < 0) position = 0;
        _selected = visible[(position + visible.Length) % visible.Length];
        _scene.SelectedIndex = _scene.Residents.Count == 0 ? -1 : _selected;
        _scene.Viewport.Resume();
        UpdateJournal();
        _canvas.InvalidateSurface();
    }

    private void UpdateJournal()
    {
        _lastJournalUpdate = _clock.ElapsedMilliseconds;
        var journal = IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>();
        if (journal is null || _selected < 0 || _selected >= _scene.Residents.Count)
        {
            journal?.Clear();
            return;
        }
        var mon = _scene.Residents[_selected]; var p = Personality(mon);
        var card = CardFor(mon);
        journal.Resident = mon; journal.Mood = card.Mood; journal.Activity = card.Activity; journal.Trait = p.Trait; journal.Likes = p.Likes; journal.Story = MemoryOf(mon, p.Story);
        journal.Personality = p.Trait; journal.Needs = card.Needs; journal.Hearts = card.Hearts;
        journal.Taste = ParkTaste.Describe(ParkTaste.NatureOrGuess(_park.NatureOf(mon), mon.Id), _scene.Life[mon.Id]?.Care.TasteKnown ?? 0);
        journal.Recent = _recent;
        journal.Bonds = BondsOf(mon);
    }

    private async Task PublishWidgetAsync()
    {
        if (!PokeparkWidgetPublisher.HasActiveWidgets())
        {
            Volatile.Write(ref _publishedWidgetRevision, Volatile.Read(ref _widgetRevision));
            return;
        }
        if (!await _widgetPublishGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            // The widget paints its own scene from the roster, the journal and the residents'
            // sprites (already loaded here); it never copies this page's camera view.
            var revision = Volatile.Read(ref _widgetRevision);
            await PokeparkWidgetPublisher.RefreshAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Volatile.Write(ref _publishedWidgetRevision, revision);
        }
        finally { _widgetPublishGate.Release(); }
    }

    private async Task OptionsAsync()
    {
        if (_menuOpen) return; _menuOpen = true;
        try
        {
            var current = _park.Settings.Source switch
            {
                PokeparkSource.Save => "Save only",
                PokeparkSource.Bank => "Bank only",
                PokeparkSource.Disabled => "Disabled",
                _ => "Save + Bank"
            };
            var options = new List<string> { "Today's goals", "Photo album", "Daycare", "Pokéwalker", "Home-screen widgets", "How to play", "Park journal", "Time and weather", "Music", "Treasures",
                ScreensaverMinutes == 0 ? "Screensaver: Off" : $"Screensaver: {ScreensaverMinutes} min",
                "Auto-fill mode", $"Auto-fill size: {_park.Settings.Count}", "Invite Pokémon", "Empty park", "Credits", _scene.ShowCaptions ? "Captions: On" : "Captions: Off",
                ParkCanvas.UsesGpu ? "Drawing: GPU" : "Drawing: CPU" };
#if DIAGNOSTIC
            // Frame times over the park, for measuring on a device.
            options.Add(_scene.ShowPerformance ? "Hide performance" : "Show performance");
#endif
            var choice = await PadMenu.ShowAsync(_host, "Poképark options", $"Auto-fill: {current}. Existing residents are never moved or replaced.", [.. options]);
            if (choice is "Drawing: GPU" or "Drawing: CPU")
            {
                var gpu = choice == "Drawing: CPU";
                Preferences.Default.Set(ParkCanvas.RendererKey, gpu ? "gpu" : "cpu");
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Drawing", gpu
                    ? "The park will be drawn by the GPU next time you open it: lighter and smoother."
                    : "The park will be drawn by the CPU next time you open it, in case the GPU drawing shows a problem on this device.");
            }
            if (choice is "Captions: On" or "Captions: Off")
            {
                _scene.ShowCaptions = choice == "Captions: Off";
                Preferences.Default.Set(CaptionsKey, _scene.ShowCaptions);
            }
            if (choice is "Show performance" or "Hide performance") _scene.ShowPerformance = choice == "Show performance";
            if (choice == "Park journal")
                await ShowJournalAsync();
            if (choice == "Photo album")
                await PokeparkAlbumOverlay.ShowAsync(_host);
            if (choice == "Auto-fill mode")
            {
                var mode = await PadMenu.ShowAsync(_host, "Auto-fill park", "Choose where automatic residents may come from. Manual invites remain available in every mode.",
                    "Save + Bank", "Save only", "Bank only", "Disabled");
                var source = mode switch
                {
                    "Save only" => PokeparkSource.Save,
                    "Bank only" => PokeparkSource.Bank,
                    "Disabled" => PokeparkSource.Disabled,
                    _ => PokeparkSource.Both
                };
                _park.SaveSettings(_park.Settings with { Source = source });
                await Task.Run(() =>
                {
                    _park.EnsureInitialized();
                    _park.RefreshAutoFill();
                });
                Reload();
            }
            if (choice?.StartsWith("Auto-fill size", StringComparison.Ordinal) == true) await AutoFillSizeAsync();
            if (choice == "Invite Pokémon") await PokeparkCardOverlay.ShowDialogueAsync(_host, "Park guide", "Open a Pokémon’s action menu in your save boxes or Bank, then choose Send to Poképark. Its original stays right where it is.");
            else if (choice == "Empty park" && _scene.Residents.Count > 0 &&
                     await PadMenu.ConfirmAsync(_host, "Empty the park?", $"Remove all {_scene.Residents.Count} residents from Poképark? Their original Pokémon stay in their save or Bank.", "Empty park"))
            {
                _park.RemoveAllVisitors();
                Reload();
            }
            else if (choice == "Home-screen widgets")
            {
                _menuOpen = false;
                await WidgetMenu.ShowAsync(_host, HomeWidgetTab.Pokepark);
            }
            else if (choice == "Credits") { _menuOpen = false; await CreditsAsync(); }
            else if (choice == "Time and weather") await SkyAsync();
            else if (choice?.StartsWith("Screensaver", StringComparison.Ordinal) == true) await ScreensaverAsync();
            else if (choice == "Music") await MusicMenu.ShowAsync(_host, _scene.Notify);
            else if (choice == "How to play") await GuideAsync();
            else if (choice == "Today's goals") await GoalsAsync();
            else if (choice == "Daycare") { _menuOpen = false; await DaycareAsync(); }
            else if (choice == "Pokéwalker") await WalkerScreen.ShowAsync(_host, [.. _scene.Residents]);
            else if (choice == "Treasures")
            {
                var found = _treasures.Load().OrderByDescending(t => t.Value).Select(t => $"{t.Key} ×{t.Value}").ToArray();
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Treasures", found.Length == 0
                    ? "Nothing dug up yet. Ask a Pokémon to dig from its card: Play, then Dig. Ground and Rock types have the best noses for it."
                    : "Dug up so far: " + string.Join(", ", found) + ".");
            }
        }
        finally { _menuOpen = false; }
    }

    // How many residents auto-fill brings: everyone already there stays, a bigger size invites
    // newcomers now, a smaller one only applies the next time auto-fill picks.
    private async Task AutoFillSizeAsync()
    {
        var sizes = new[] { 3, 6, 9, 12, 15, 18, 24, PokeparkService.MaximumResidents }.Distinct().Where(n => n <= PokeparkService.MaximumResidents).ToArray();
        var current = _park.Settings.Count;
        var pick = await PadMenu.ShowAsync(_host, "Auto-fill size", $"How many Pokémon auto-fill puts in the park (now {current}, up to {PokeparkService.MaximumResidents}). Nobody already living here is sent away.",
            sizes.Select(n => n == current ? $"{n} residents (current)" : $"{n} residents").ToArray());
        if (pick is null || !int.TryParse(pick.Split(' ')[0], out var count) || count == current) return;
        _park.SaveSettings(_park.Settings with { Count = count });
        var arrived = await Task.Run(_park.TopUpAutoFill);
        Reload();
        _scene.Notify(arrived > 0 ? $"{arrived} new {(arrived == 1 ? "resident" : "residents")} moved in"
            : count < _scene.Residents.Count ? $"Auto-fill size {count}: everyone here stays" : $"Auto-fill size {count}");
    }

    // The park's latest moments for the lower screen, newest first.
    private string[] _recent = [];
    private IReadOnlyList<ParkSocialEvent> _journalLines = [];
    private void RefreshRecent()
    {
        _journalLines = _social.Journal(100);
        _recent = _journalLines.Take(6).Select(e => e.Text).ToArray();
    }

    // What the card remembers for a resident: something that really happened to it in the park,
    // the newest while it is fresh, else one of its last few in turn; its standing story when it
    // has none yet.
    private string MemoryOf(ParkPokemon mon, string story)
    {
        var own = _journalLines.Where(e => e.PokemonIds.Contains(mon.Id) && e.Kind != ParkSocialEventKind.Rest).Take(5).ToArray();
        if (own.Length == 0) return story;
        if (DateTimeOffset.Now - own[0].OccurredAt < TimeSpan.FromHours(1)) return own[0].Text;
        return own[(int)(DateTime.Now.Ticks / TimeSpan.FromSeconds(30).Ticks % own.Length)].Text;
    }

    private void UpdateStatus() => _status.Text = _scene.Residents.Count == 0
        ? "Your meadow is waiting. Select a Pokémon in a box or the Bank, then Send to Poképark."
        : $"{_scene.Region.Name}  ·  {_scene.Residents.Count}/{PokeparkService.MaximumResidents} residents  ·  Today's goals {Goals().Progress.Where((_, i) => Goals().Done(i)).Count()}/{Goals().Goals.Count}  ·  Tap a Pokémon to meet it, hold to pat it";

    // Today's three goals, kept until midnight.
    private const string GoalsKey = "pkforge.pokepark.goals.v1";
    private ParkGoalsState? _goals;

    private ParkGoalsState Goals()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_goals is null || _goals.Day != today)
            _goals = ParkGoals.Load(Preferences.Default.Get<string?>(GoalsKey, null), today, _scene.Residents.Count,
                _scene.Residents.Any(r => HabitatCatalog.TypesFor(r.Species, r.Form).Contains(ParkType.Fighting)));
        return _goals;
    }

    // Counts something the player did toward today's goals; a finished goal shows at the top, and all
    // three set off a party with a rare find.
    private void Deed(ParkDeed deed, string id)
    {
        if (_scene.Residents.Count == 0) return;
        var state = ParkGoals.Record(Goals(), deed, id, out var done);
        foreach (var goal in done) _scene.Notify($"Goal done: {goal.Text}");
        if (state.AllDone && !state.Rewarded)
        {
            var reward = ParkGoals.RewardFor(state.Day);
            state = state with { Rewarded = true };
            _treasures.Add(reward);
            _scene.Life.Rejoice();
            _scene.Notify($"All of today's goals done! The park celebrates, and you find {reward.Name}.");
        }
        _goals = state;
        Preferences.Default.Set(GoalsKey, ParkGoals.Save(state));
        MainThread.BeginInvokeOnMainThread(UpdateStatus);
    }

    // Talking to Blissey: leave a Pokémon, pick one up, take the egg, or change whether the daycare writes back to saves.
    private async Task DaycareAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            while (true)
            {
                var roster = _scene.Residents.ToDictionary(r => r.Id);
                var guests = _daycare.Stays.Where(s => roster.ContainsKey(s.ResidentId)).Select(s => roster[s.ResidentId]).ToArray();
                var egg = _daycare.Egg is { } e && roster.TryGetValue(e.First, out var first) && roster.TryGetValue(e.Second, out var second) ? (first, second) : ((ParkPokemon, ParkPokemon)?)null;
                var writes = _park.Settings.DaycareWritesBack;
                var options = new List<PadOption>();
                if (egg is { } waiting) options.Add(new PadOption("Take the egg", IconPath: "park", Detail: $"From {waiting.Item1.Name} and {waiting.Item2.Name}. {(writes ? "It goes to your Bank." : "Writing back is off: it becomes a keepsake.")}"));
                foreach (var guest in guests)
                {
                    var (time, growth) = _daycare.Progress(guest);
                    var detail = growth is { ExpGained: > 0 } g
                        ? $"Here {Stayed(time)}. Level {g.LevelBefore}, would be {g.LevelAfter} on pick-up."
                        : $"Here {Stayed(time)}.";
                    options.Add(new PadOption($"Pick up {Named(guest)}", IconPath: "move", Detail: detail));
                }
                if (guests.Length < PokeparkDaycareService.Capacity && _scene.Residents.Count > guests.Length)
                    options.Add(new PadOption("Leave a Pokémon here", IconPath: "park", Detail: $"{guests.Length} of {PokeparkDaycareService.Capacity} places taken."));
                options.Add(new PadOption($"Write back to saves: {(writes ? "On" : "Off")}", IconPath: "settings",
                    Detail: writes ? "EXP and eggs go back to your saves and Bank, where the game allows it." : "Everything that happens here stays in the park."));
                options.Add(new PadOption("How the daycare works", IconPath: "info"));
                var note = guests.Length == 0 ? "Blissey keeps the daycare by the cabin, and Audino helps." : $"Looking after {string.Join(", ", guests.Select(g => g.Name))}.";
                var choice = await PadMenu.ShowAsync(_host, "Blissey's daycare", note, options.ToArray());
                if (choice is null) return;
                if (choice == "Take the egg" && egg is { } taken)
                {
                    _scene.Life.TakeEgg();
                    var line = _daycare.TakeEgg(taken.Item1, taken.Item2, _treasures.Add);
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Joyous"));
                }
                else if (choice.StartsWith("Pick up ", StringComparison.Ordinal) && guests.FirstOrDefault(g => choice == $"Pick up {Named(g)}") is { } leaving)
                {
                    var line = await _daycare.PickUpAsync(leaving);
                    SetGuests();
                    if (_daycare.Egg is null && _scene.Life.Egg is not null) _scene.Life.TakeEgg();
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Happy"));
                }
                else if (choice == "Leave a Pokémon here")
                {
                    var free = _scene.Residents.Where(r => !_daycare.IsGuest(r.Id)).ToArray();
                    var pick = await PadMenu.ShowAsync(_host, "Who stays with Blissey?", null,
                        free.Select(r => new PadOption(Named(r), Detail: string.IsNullOrEmpty(r.Origin) ? r.Source : $"{r.Origin} · {r.TrainerName}")).ToArray());
                    if (free.FirstOrDefault(r => Named(r) == pick) is { } staying)
                    {
                        var line = _daycare.CheckIn(staying);
                        SetGuests();
                        await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Happy"));
                    }
                }
                else if (choice.StartsWith("Write back to saves", StringComparison.Ordinal))
                    _park.SaveSettings(_park.Settings with { DaycareWritesBack = !writes });
                else if (choice == "How the daycare works")
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey's daycare",
                        "Leave up to four Pokémon with Blissey. Each one follows its own game's daycare: up to Sun and Moon they gain EXP for every hour they stay; the Nurseries of Sword and Shield, Brilliant Diamond and Shining Pearl, and Scarlet and Violet don't train but can give eggs; Let's Go and Legends have no daycare. Two guests who could breed in their game may leave an egg, which Blissey keeps until you come for it. With writing back on, the EXP goes to the real Pokémon on pick-up and the egg goes to your Bank. The save gets a restore point first, and nothing is written if the Pokémon was changed since you left it.",
                        () => _walking.GetPortrait(242, 0, false, "Normal"));
            }
        }
        finally { _menuOpen = false; }
    }

    // A resident's name in a list, with its slot in the park when another has the same name.
    private string Named(ParkPokemon mon) => _scene.Residents.Count(r => r.Name == mon.Name) > 1
        ? $"{mon.Name} ({_scene.Residents.ToList().FindIndex(r => r.Id == mon.Id) + 1})" : mon.Name;

    private static string Stayed(TimeSpan time) => time.TotalHours switch
    {
        < 1 => "less than an hour",
        < 24 => $"{Math.Max(1, (int)time.TotalHours)} h",
        _ => $"{(int)time.TotalDays} d {time.Hours} h"
    };

    private Task GoalsAsync()
    {
        var goals = Goals();
        var lines = goals.Goals.Select((goal, i) => goals.Done(i) ? $"{goal.Text}: done!" : goal.Count > 1 ? $"{goal.Text}: {goals.Progress[i]} of {goal.Count}." : $"{goal.Text}.");
        var end = goals.Rewarded ? " All done for today. New goals tomorrow." : $" Finish all three for a party and a rare find: today it is {ParkGoals.RewardFor(goals.Day).Name}.";
        return PokeparkCardOverlay.ShowDialogueAsync(_host, "Today's goals", string.Join(" ", lines) + end);
    }

    // Who it loves, its best friend and its rival: the species' own feelings plus what happened in the park.
    private string BondsOf(ParkPokemon mon)
    {
        string? friend = null, rival = null; var friendScore = 14; var rivalScore = 14; var love = false; var fear = false;
        foreach (var other in _scene.Residents.Where(r => r.Id != mon.Id))
        {
            var (friendship, rivalry) = _relations.GetValueOrDefault(ParkRelationship.KeyFor(mon.Id, other.Id));
            var feeling = _scene.Life.FeelingOf(mon.Id, other.Id);
            var near = friendship + feeling switch { ParkPairKind.Love => 60, ParkPairKind.Family => 40, ParkPairKind.Friends => 30, _ => 0 };
            var far = rivalry + feeling switch { ParkPairKind.Rivals => 60, ParkPairKind.Fears or ParkPairKind.Hunts => 40, _ => 0 };
            if (near > friendScore) { friendScore = near; friend = other.Name; love = feeling == ParkPairKind.Love; }
            if (far > rivalScore) { rivalScore = far; rival = other.Name; fear = feeling == ParkPairKind.Fears; }
        }
        var parts = new List<string>();
        if (friend is not null) parts.Add(love ? $"in love with {friend}" : $"best friend {friend}");
        if (rival is not null && rival != friend) parts.Add(fear ? $"scared of {rival}" : $"rival {rival}");
        return parts.Count == 0 ? "no close bonds yet" : string.Join(" · ", parts);
    }

    private void SaveCare()
    {
        _lastCareSave = _clock.ElapsedMilliseconds;
        if (_scene.Life.Actors.Count > 0) _care.Save(_scene.Life.Snapshot(DateTimeOffset.Now));
        // The park's ongoing stories (Bidoof's dam, Lapras's regulars…), kept across days.
        Preferences.Default.Set(ChronicleKey, _scene.Life.SaveChronicle());
    }

    private const string ChronicleKey = "pkforge.pokepark.chronicle.v1";
    private bool _chronicleLoaded;

    // Moments seen live go to the journal and move the relationships.
    // The daycare is open on the home meadow; its guests, the egg Blissey keeps, and the Pokémon
    // released to the Wilds who may wander back in.
    private readonly Dictionary<string, bool> _breeds = new();

    private static string BreedKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

    // The guests, and which pairs of them could leave an egg: found here on the UI thread, since it
    // reads their saves and the Bank, never from inside the park's step.
    private void SetGuests()
    {
        var roster = _scene.Residents.ToDictionary(r => r.Id);
        var guests = _daycare.Stays.Select(s => s.ResidentId).ToArray();
        foreach (var (a, i) in guests.Select((g, i) => (g, i)))
            foreach (var b in guests.Skip(i + 1))
            {
                var key = BreedKey(a, b);
                lock (_breeds) if (_breeds.ContainsKey(key)) continue;
                var can = roster.TryGetValue(a, out var x) && roster.TryGetValue(b, out var y) && _daycare.CanBreed(x, y);
                lock (_breeds) _breeds[key] = can;
            }
        _scene.Life.DaycareGuests = guests;
    }

    private void SetUpDaycareAndWilds()
    {
        var life = _scene.Life;
        var roster = _scene.Residents.ToDictionary(r => r.Id);
        foreach (var stay in _daycare.Stays.Where(s => !roster.ContainsKey(s.ResidentId))) _daycare.Forget(stay.ResidentId);
        life.DaycareOpen = true;
        SetGuests();
        // The park asks while it runs on the drawing thread: it only reads what SetGuests worked out.
        life.CanBreed = (a, b) => { lock (_breeds) return _breeds.GetValueOrDefault(BreedKey(a, b)); };
        if (_daycare.Egg is { } egg && life.Egg is null) life.KeepEgg(egg);
        life.OldFriends = _wilds.Load();
    }

    private void RecordMoments()
    {
        if (_scene.Life.Egg is { } found && _daycare.Egg != found) _daycare.KeepEgg(found);
        var moments = _scene.Life.TakeMoments();
        if (moments.Count == 0) return;
        // Pairs with a wild visitor make no lasting relationship; their own stories still count.
        foreach (var moment in moments.Where(m => m.SecondId.Length == 0 || !ParkLife.IsVisitor(m.SecondId) && !ParkLife.IsVisitor(m.FirstId)))
            if (_scene.Life[moment.FirstId] is { } first)
                _social.Witness(moment, first.Resident.Name, _scene.Life[moment.SecondId]?.Resident.Name ?? "");
        _relations = _social.Relations();
        RefreshRecent();
    }

    private ParkCardPaint.Card CardFor(ParkPokemon mon)
    {
        var actor = _scene.Life[mon.Id];
        var care = actor?.Care ?? new ParkCareState(mon.Id);
        var personality = Personality(mon);
        var activity = actor is null ? "Exploring another area" : actor.Indoors ? "Sleeping in the cabin" : actor.Activity;
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        return new ParkCardPaint.Card(mon.Name, mon.Shiny, personality.Trait, actor?.Mood ?? "Content", activity, personality.Likes,
            [care.Hunger, care.Energy, care.Fun, care.Social], care.Hearts(), types.Contains(ParkType.Fighting), BondRows(mon), AboutRows(mon, actor, care));
    }

    // How it gets on with every other resident, strongest bonds first.
    private IReadOnlyList<ParkCardPaint.BondRow> BondRows(ParkPokemon mon) => _scene.Residents.Where(r => r.Id != mon.Id).Select(other =>
    {
        var (friendship, rivalry) = _relations.GetValueOrDefault(ParkRelationship.KeyFor(mon.Id, other.Id));
        return new ParkCardPaint.BondRow(other.Name, _scene.Life.FeelingOf(mon.Id, other.Id), friendship, rivalry);
    }).OrderByDescending(b => (b.Kind == ParkPairKind.None ? 0 : 100) + Math.Abs(b.Friendship) + b.Rivalry).ToArray();

    // Everything the park knows about it, each with what it means.
    private IReadOnlyList<ParkCardPaint.AboutRow> AboutRows(ParkPokemon mon, ParkActor? actor, ParkCareState care)
    {
        var rows = new List<ParkCardPaint.AboutRow>();
        if (actor is null) return rows;
        var resident = actor.Resident;
        // Who it is and where it really lives.
        rows.Add(new("Species", _park.SpeciesName(mon.Species) + (mon.Form > 0 ? $" (form {mon.Form})" : "") + (mon.Shiny ? " · shiny" : "")));
        var game = string.Join(" · ", new[] { mon.Origin, mon.GameName, mon.TrainerName.Length > 0 ? $"trainer {mon.TrainerName}" : "" }.Where(s => s.Length > 0));
        if (game.Length > 0) rows.Add(new("Comes from", game));
        rows.Add(new("Lives in", mon.Source));
        rows.Add(new("Temperament", ParkLore.Explain(resident.Temperament)));
        var nature = _park.NatureOf(mon);
        var taste = ParkTaste.Describe(ParkTaste.NatureOrGuess(nature, mon.Id), care.TasteKnown);
        rows.Add(new("Nature", nature is { } n ? $"{_park.NatureName(n)} · {taste}" : $"unknown until its save is open · {taste}"));
        foreach (var (habit, meaning) in ParkLore.Habits(resident.Species, resident.Types, ParkLife.IsTroll(resident)).Take(2))
            rows.Add(new(habit, meaning));
        if (ParkLore.TouchWarning(resident.Species, resident.Types, resident.Temperament) is { } touch) rows.Add(new("Touch", touch));
        if (care.SparCount > 0) rows.Add(new("Training", $"your best: {care.SparBest} of {ParkLife.SparRounds} blocked over {care.SparCount} sessions"));
        return rows;
    }

    // Takes the player to the resident's own slot: in the Bank, or in the boxes of the save it came
    // from when that save is the one open (the park only ever holds a copy).
    private async Task FindInBoxAsync(ParkPokemon mon)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        var parts = mon.Id.Split(':');
        if (parts[0] == "bank" && Guid.TryParse(parts.ElementAtOrDefault(1), out var id))
        {
            var bank = services.GetRequiredService<IBankService>();
            if (bank.GetAll().FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, $"{mon.Name} is no longer in the Bank. Only its copy lives here now.");
                return;
            }
            var page = services.GetRequiredService<BankPage>();
            page.Reveal(entry.Box, entry.Slot);
            await Navigation.PushAsync(page);
            return;
        }
        if (parts[0] == "save" && parts.Length >= 4 && int.TryParse(parts[2], out var box) && int.TryParse(parts[3], out var slot))
        {
            var saves = services.GetRequiredService<ISaveSessionService>();
            var document = saves.Current?.Document;
            var hash = document is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
            var where = box < 0 ? $"party, slot {slot + 1}" : $"box {box + 1}, slot {slot + 1}";
            if (hash != parts[1])
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name,
                    $"{mon.Name} came from {(mon.Origin.Length > 0 ? mon.Origin : "another save")}. Open that save to find it: {where}.");
                return;
            }
            var boxes = services.GetRequiredService<BoxBrowserPage>();
            await Navigation.PushAsync(boxes);
            services.GetRequiredService<PKForge.App.ViewModels.BoxBrowserViewModel>().JumpTo(box, slot);
            return;
        }
        await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, $"{mon.Name} lives in {mon.Source}.");
    }

    private SKBitmap? PortraitFor(ParkPokemon mon) =>
        _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, ParkLife.PortraitFor(_scene.Life[mon.Id]?.Mood ?? "Content"));

    private async Task DetailsAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "An empty meadow", "Invite a Pokémon from its save-box or Bank action menu with Send to Poképark. Then tap it here to spend some time together.");
                return;
            }
            var mon = _scene.Residents[_selected];
            _scene.CoveredBottom = PokeparkCardOverlay.CoveredHeight(_canvas.CanvasSize.Width);
            var action = await PokeparkCardOverlay.ShowCardAsync(_host, () => CardFor(mon), () => PortraitFor(mon));
            if (action is null) return;
            if (action == "find")
            {
                _menuOpen = false;
                await FindInBoxAsync(mon);
                return;
            }
            if (action == "leave")
            {
                if (await PadMenu.ConfirmAsync(_host, "Leave the park?", $"Remove {mon.Name} from this meadow? Its original stays in its box. You can invite it again anytime.", "Leave park"))
                { _park.RemoveVisitor(mon.Id); _care.Remove(mon.Id); _daycare.Forget(mon.Id); Reload(); }
                return;
            }
            if (action.StartsWith("snack:", StringComparison.Ordinal) && int.TryParse(action[6..], out var flavor))
            {
                await FeedAsync(mon, flavor);
                return;
            }
            if (action.StartsWith("game:", StringComparison.Ordinal))
            {
                await PlayAsync(mon, action[5..]);
                return;
            }
            var state = _scene.GetResidentState(_selected, _clock.ElapsedMilliseconds);
            var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
            var nearby = _scene.Residents.Where((_, i) => i != _selected)
                .OrderBy(_ => Random.Shared.Next()).FirstOrDefault()?.Name;
            var dialogue = ParkDialogueGenerator.Generate(new ParkDialogueContext(
                mon.Species, mon.Name, types, _scene.Habitat,
                state.Activity, state.Mood, ParkLife.PersonalityFor(mon.Id), action, nearby),
                _narrativeMemory.Recent(mon.Id));
            _narrativeMemory.Remember(mon.Id, dialogue.Id);
            var reply = _scene.Interact(_selected, _clock.ElapsedMilliseconds, action);
            if (reply.Accepted && action == "talk") Deed(ParkDeed.Talked, mon.Id);
            // The Pokémon acts it out above while its line shows below: its own line when it has
            // one (a refusal, a species habit), the usual dialogue otherwise.
            var emotion = reply.Line is null ? action switch { "relax" => "Normal", _ => "Happy" } : reply.Emotion;
            await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, reply.Line ?? dialogue.Text,
                () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion));
            SaveCare();
        }
        finally { _menuOpen = false; _scene.CoveredBottom = 0; }
    }

    // A berry from the player: its nature decides whether it is a favorite or a face-pull.
    private async Task FeedAsync(ParkPokemon mon, int flavor)
    {
        var nature = ParkTaste.NatureOrGuess(_park.NatureOf(mon), mon.Id);
        var reply = _scene.Life.Feed(mon.Id, flavor, nature);
        if (reply.Accepted && reply.Taste > 0) Deed(ParkDeed.LovedBerry, mon.Id);
        var berry = ParkTaste.Berries[Math.Clamp(flavor, 0, 4)];
        var taste = NatureFacts.FlavorNames[Math.Clamp(flavor, 0, 4)].ToLowerInvariant();
        var (line, emotion) = reply.Line is { } own ? (own, reply.Emotion) : reply.Taste switch
        {
            > 0 => ($"{mon.Name} gobbles the {berry} Berry and does a little happy dance. {char.ToUpperInvariant(taste[0])}{taste[1..]} things are its favorite!", "Joyous"),
            < 0 => ($"{mon.Name} takes one bite of the {berry} Berry and makes a face. It stays polite about it, but {taste} flavors are not for it.", "Sigh"),
            _ => ($"{mon.Name} munches the {berry} Berry happily, down to the last crumb.", "Happy")
        };
        await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line,
            () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion));
        SaveCare();
    }

    private void FlyToPlace(int delta)
    {
        var place = _scene.FlyToRegion(delta);
        _status.Text = $"{place.Name}  ·  L/R to visit the park's places, A to follow a Pokémon again";
        if (_active) _canvas.InvalidateSurface();
    }

    private bool WidgetDirty => Volatile.Read(ref _widgetRevision) != Volatile.Read(ref _publishedWidgetRevision);
    private void MarkWidgetDirty() => Interlocked.Increment(ref _widgetRevision);

    private async Task PicnicAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Picnic time", "A picnic is better with company! Invite a Pokémon from your boxes or Bank first.");
                return;
            }
            for (var i = 0; i < _scene.Residents.Count; i++)
                _scene.Interact(i, _clock.ElapsedMilliseconds, "snack");
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Picnic time!", "You share a basket of berries with everyone. Happy little sounds drift across the meadow. There’s enough for every resident!");
        }
        finally { _menuOpen = false; }
    }

    private static (string Trait, string Likes, string Story, string Greeting) Personality(ParkPokemon mon)
    {
        uint hash = 2166136261;
        foreach (var letter in mon.Id) hash = (hash ^ letter) * 16777619;
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);

        // A few residents have difficult days, shaped by what they are and where
        // they feel most at home. Their needs can change through future interactions.
        if (hash % 5 == 0)
        {
            if (types.Contains(ParkType.Water))
                return ("Homesick swimmer", "quiet water and familiar shores", "Misses the place it came from and keeps searching the pond for a familiar ripple.", "looks toward the lake, then gives you a small, uncertain wave.");
            if (types.Contains(ParkType.Fire))
                return ("Short-tempered spark", "warm stones and plenty of personal space", "Has been on edge all day and snaps at every leaf that lands nearby.", "huffs a tiny puff of smoke, then tries very hard to calm down.");
            if (types.Contains(ParkType.Electric))
                return ("Overloaded thinker", "quiet shade and gentle company", "The meadow feels too loud today, even when everything is perfectly still.", "flinches at a distant rustle and slowly relaxes when you stay nearby.");
            if (types.Contains(ParkType.Ghost) || types.Contains(ParkType.Dark))
                return ("Lonely night wanderer", "dim corners and one trusted friend", "Has spent most of the day at the edge of the meadow, hoping someone will notice.", "keeps its distance, but does not leave when you sit beside it.");
            if (types.Contains(ParkType.Grass) || types.Contains(ParkType.Bug))
                return ("Wilted little heart", "soft rain and untouched grass", "The meadow feels strangely empty today, and even its favorite flowers cannot cheer it up.", "lowers its gaze, then accepts your quiet company.");
            return ("Uneasy visitor", "calm voices and a little extra patience", "Still has not decided whether this park is somewhere it can truly relax.", "watches you carefully, waiting to see whether you will stay.");
        }

        return (hash % 12) switch
        {
            0 => ("Curious explorer", "new paths and rustling grass", "Keeps checking whether that flower has grown since yesterday.", "tilts its head, curious about your day."),
            1 => ("Easygoing dreamer", "quiet afternoons and soft grass", "Has found a sunny patch and considers it the best spot in the park.", "gives you a slow, contented nod."),
            2 => ("Friendly neighbor", "company and little greetings", "Always seems to be nearby when a new resident arrives.", "comes closer to keep you company."),
            3 => ("Playful wanderer", "stretching its legs and surprises", "Once followed a drifting leaf all the way across the meadow.", "does a cheerful little wiggle."),
            4 => ("Thoughtful observer", "watching the pond and peaceful corners", "Is convinced the ripples on the pond are trying to say something.", "pauses its daydream to look up at you."),
            5 => ("Flower enthusiast", "colorful petals and fresh air", "Has a favorite flower, but seems to pick a different one every day.", "looks delighted that you stopped by."),
            6 => ("Tiny pathfinder", "hidden trails and bent blades of grass", "Found a shortcut through the meadow and now checks whether anyone else has discovered it.", "points proudly toward a nearby path."),
            7 => ("Rain listener", "fresh puddles and soft drumming", "Remembers the last rainstorm by the little rings it left in the pond.", "leans closer as if waiting for the clouds to speak."),
            8 => ("Berry guardian", "ripe berries and generous picnics", "Planted a berry seed near the fence and visits it with great responsibility.", "offers you a berry, then keeps one for later."),
            9 => ("Cloud watcher", "open skies and slow afternoons", "Once spent so long watching clouds that it mistook one for a very sleepy Pokémon.", "looks up, then points at a particularly convincing cloud."),
            10 => ("Grassland guide", "tall grass and familiar footsteps", "Can recognize every regular resident by the sound of their walk through the meadow.", "greets you with a knowing little nod."),
            _ => ("Sunset collector", "golden light and quiet goodbyes", "Keeps returning to the same hill to watch the meadow turn amber at day's end.", "settles beside you for one peaceful moment.")
        };
    }

    private async Task ShowJournalAsync()
    {
        var offline = _offlineJournal.Load().Journal.Select(e => (At: e.Timestamp, e.Text));
        var social = _social.Journal(40).Select(e => (At: e.OccurredAt, e.Text));
        var entries = offline.Concat(social).OrderByDescending(e => e.At).Take(12).ToArray();
        if (entries.Length == 0)
        {
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Park journal",
                "The first pages are still blank. Leave your Pokémon to enjoy the park, then check back later.");
            return;
        }
        var labels = entries.Select((entry, i) =>
            $"{i + 1}. {entry.At.ToLocalTime():ddd HH:mm} · {Shorten(entry.Text, 46)}").ToArray();
        var choice = await PadMenu.ShowAsync(_host, "Park journal",
            "A record of discoveries, friendships, disagreements, and quiet moments while you were away.",
            labels.Concat(["Close"]).ToArray());
        var index = Array.IndexOf(labels, choice);
        if (index >= 0)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Park journal", entries[index].Text);
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    // The PKForge statue's plaque: who made PKForge and the park, what it is built on, and who drew
    // the sprites of the Pokémon living in the park right now.
    private async Task CreditsAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            var names = await _walking.CreditNamesAsync();
            var sprites = new List<ParkCreditsPaint.Entry>();
            foreach (var mon in _scene.Residents.DistinctBy(p => (p.Species, p.Form, p.Shiny)))
            {
                var artists = PokeparkSpriteService.Artists(_walking.GetAttribution(mon.Species, mon.Form, mon.Shiny), names);
                sprites.Add(new(_park.SpeciesName(mon.Species) + (mon.Shiny ? " (shiny)" : ""),
                    artists.Count > 0 ? string.Join(", ", artists) : "PKHeX's sprite, until its animated sprite loads",
                    (mon.Species, mon.Form, mon.Shiny)));
            }
            sprites.Add(new("PMD SpriteCollab", "the community that draws the Mystery Dungeon sprites (CC BY-NC 4.0); the original sprites by CHUNSOFT"));
            ParkCreditsPaint.Section[] sections =
            [
                new("Made by", [new("sofianeelhor", "PKForge and the Poképark")]),
                new("Art", [new("spritedmistery", "the Poképark's map, the PKForge logo and this statue, the app's look, its colors and its buttons"),
                    new("AlchemyBats", "the tileset the park is built on")]),
                new("In the park right now", sprites),
                new("Built on", [
                    new("PKHeX", "Kaphotics and all its contributors: the engine everything runs on, and its sprites"),
                    new("PKHeX-Plugins", "santacrab2 and contributors: Auto Legality Mod, the offline legalizer"),
                    new("PKSM", "FlagBrew: the pixel chrome the app's look builds on"),
                    new("Pokémon Showdown", "Smogon and the artists of its sprite projects: sprites and box icons"),
                    new("PokeAPI", "item art and the evolution lines"),
                    new("SteamGridDB", "its community: the game library's art"),
                    new("game-icons.net", "Lorc, Delapouite, Guard13007, Carl Olsen and others: symbols"),
                    new("Bulbagarden Archives", "the Pokérus sprites"),
                    new("BlupBlurp, Team Lumi", "the Luminescent Platinum data"),
                    new("G0LD, Ruki Studios", "the GS Chronicles data"),
                ]),
                new("Pokémon", [new("Nintendo, Creatures Inc., GAME FREAK inc.", "Pokémon and Pokémon names ©")]),
            ];
            await PokeparkCreditsOverlay.ShowAsync(_host, sections, _walking);
        }
        finally { _menuOpen = false; }
    }

    // The PKForge statue in the middle of the home park (art pixels): tapping it shows the credits.
    private static readonly SKRect StatueArea = new(482, 845, 526, 902);
    // The snowman someone built at the edge of the lava lake, on the volcano.
    private static readonly SKRect SnowmanArea = new(170, 2, 198, 34);

    // Touch: drag or fling to look around, pinch to zoom, tap a Pokémon to meet it,
    // double-tap the grass to go back to following the selected one.
    private const float TapSlop = 24;
    private readonly Dictionary<long, SKPoint> _touches = new();
    private SKPoint _touchStart, _pinchMiddle;
    private long _touchStartTime, _lastMoveTime, _lastTapTime;
    private bool _dragging, _longPressed;
    private string? _throwFor;
    private const long LongPress = 450;
    private float _pinchSpread, _speedX, _speedY;

    private void OnTouch(SKTouchEventArgs e)
    {
        var now = _clock.ElapsedMilliseconds;
        var viewport = _scene.Viewport;
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _touches[e.Id] = e.Location;
                // A finger on the park takes the camera back.
                if (!(Offering && e.Location.Y < _canvas.CanvasSize.Height * .16f)) StopSpectating();
                if (_touches.Count == 1)
                {
                    viewport.Stop();
                    _touchStart = e.Location; _touchStartTime = _lastMoveTime = now;
                    _dragging = false; _longPressed = false; _speedX = _speedY = 0;
                }
                else if (_touches.Count == 2)
                {
                    _dragging = true;
                    BeginPinch();
                }
                break;
            case SKTouchAction.Moved:
                if (!_touches.TryGetValue(e.Id, out var last)) break;
                _touches[e.Id] = e.Location;
                // Brushing: a finger moving over the Pokémon brushes it instead of moving the map.
                if (_touches.Count == 1 && Brushing(last, e.Location)) { _dragging = true; break; }
                if (_touches.Count >= 2)
                {
                    var (middle, spread) = Pinch();
                    viewport.Drag(middle.X - _pinchMiddle.X, middle.Y - _pinchMiddle.Y);
                    if (_pinchSpread > 0) viewport.Pinch(spread / _pinchSpread, middle.X, middle.Y);
                    _pinchMiddle = middle;
                    break;
                }
                if (!_dragging && SKPoint.Distance(e.Location, _touchStart) < TapSlop) break;
                _dragging = true;
                float dx = e.Location.X - last.X, dy = e.Location.Y - last.Y;
                viewport.Drag(dx, dy);
                var dt = Math.Max(1, now - _lastMoveTime);
                _speedX = _speedX * .3f + dx / dt * .7f; _speedY = _speedY * .3f + dy / dt * .7f;
                _lastMoveTime = now;
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                if (!_touches.Remove(e.Id)) break;
                viewport.EndPinch();
                if (_touches.Count == 1) { _speedX = _speedY = 0; break; }
                if (_touches.Count > 1) { BeginPinch(); break; }
                if (e.ActionType == SKTouchAction.Cancelled) break;
                if (_longPressed) break;
                if (_dragging)
                {
                    // Only a finger still moving when it lifts throws the map.
                    if (now - _lastMoveTime < 80) viewport.Fling(_speedX, _speedY);
                }
                else if (now - _touchStartTime < 500) Tap(e.Location, now);
                break;
        }
    }

    private void BeginPinch()
    {
        _scene.Viewport.BeginPinch();
        (_pinchMiddle, _pinchSpread) = Pinch();
    }

    private (SKPoint Middle, float Spread) Pinch()
    {
        var points = _touches.Values.Take(2).ToArray();
        return (new SKPoint((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2),
            SKPoint.Distance(points[0], points[1]));
    }

    // A finger held still on a Pokémon pats it; held on the grass, it calls the selected Pokémon there.
    private void CheckLongPress(long now)
    {
        if (_touches.Count != 1 || _dragging || _longPressed || _menuOpen || _throwFor is not null || now - _touchStartTime < LongPress) return;
        var x = _touchStart.X / _canvas.CanvasSize.Width;
        var y = _touchStart.Y / _canvas.CanvasSize.Height;
        var index = _scene.HitTest(x, y, now);
        if (index < 0 && (_scene.Residents.Count == 0 || _selected >= _scene.Residents.Count)) return;
        _longPressed = true;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
        catch (FeatureNotSupportedException) { }
        if (index < 0)
        {
            var mon = _scene.Residents[_selected];
            var call = _scene.Life.Call(mon.Id, _scene.ScreenToMap(x, y));
            if (call.Accepted) Deed(ParkDeed.Called, mon.Id);
            Say(mon.Id, call);
            return;
        }
        SelectResident(index);
        var id = _scene.Residents[index].Id;
        var pat = _scene.Life.Pet(id);
        if (pat.Accepted) Deed(ParkDeed.Pat, id);
        Say(id, pat);
    }

    // After Play: the next tap (or A) throws the ball for the selected Pokémon.
    private void BeginThrow(ParkPokemon mon)
    {
        _throwFor = mon.Id;
        _status.Text = $"Tap where to throw the ball for {mon.Name}  ·  A throws it ahead  ·  B keeps it";
    }

    private void Throw(ParkPoint? target)
    {
        if (_throwFor is not { } id) return;
        _throwFor = null;
        UpdateStatus();
        if (_scene.Life[id] is not { } actor) return;
        // With the pad, the ball flies a few steps ahead of where the Pokémon looks.
        var ahead = target ?? new ParkPoint(actor.X + MathF.Sin(actor.Facing * MathF.PI / 4) * 70, actor.Y + MathF.Cos(actor.Facing * MathF.PI / 4) * 50);
        var fetch = _scene.Life.Fetch(id, ahead);
        if (fetch.Accepted) Deed(ParkDeed.Fetch, id);
        Say(actor.Resident.Id, fetch);
    }

    // Everything there is to do, page by page, so nothing stays hidden.
    private async Task GuideAsync()
    {
        string[] pages =
        [
            "Tap a Pokémon to open its card. Hold your finger on it to pat it. Drag or lean the left stick to look around; pinch, press Up and Down or lean the right stick to zoom; L and R fly to another place; double-tap the grass or press Left or Right to follow a Pokémon again.",
            "When something worth seeing starts somewhere in the park, it shows at the top: press X or tap it to go and see. Press − to spectate: the camera follows what happens by itself, until you take it back.",
            "Some moments need you. Talk to a Snorlax asleep across the bridge to play the Poké Flute; talk to a Psyduck in trouble in the water to pull it out; a berry means a lot to someone who just found the bush bare.",
            "Press X to take a photo of the park, captioned with what is happening. Your photos are in Options, Photo album.",
            "Hold your finger on the grass to call the Pokémon you picked. One that trusts you comes running; an aloof one may pretend it did not hear.",
            "The card has three pages, Care, Bonds and About: switch with the tabs or L and R. Care shows its needs and what it is doing; Bonds, how it gets on with every other resident; About, its temperament, habits and what touching it may do.",
            "Talk, Snack and Relax build your bond, five hearts at most, a little each day. Snack lets you pick a berry: its nature decides whether it loves it or pulls a face. Tastes show as ??? until you find them.",
            "Play opens the games: throw the ball, hide and seek, races, digging for treasure, brushing, and for Fighting types, training. Whether it plays depends on its temperament and on your bond.",
            "Every resident has needs that fall with time: food, energy, fun and friends. They look after themselves while you are away, but not perfectly. At night they sleep, some in the cabin.",
            "One left hungry and worn out for too long falls unwell: it drags its feet and sweats, and the healers come to check on it. A berry from your hand puts it back on its feet.",
            "On dry evenings a Fire type may light a campfire, and the others come to sit round it until it dies down. Rain puts it out.",
            "Options, Today's goals: three small things to do each day. Do all three and the park throws a party, and you find something rare.",
            $"They have their own lives: friends, lovers, sworn rivals, troublemakers who steal and trip, and someone who stands up to them. The journal tells what happened; {DualScreenLayout.SecondScreenName(IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>()?.Arrangement ?? ScreenArrangement.MainOnTop)} follows the one you picked.",
            "Options: Time and weather keeps the real clock and the day's weather, or lets you pick your own. Treasures lists what was dug up. Wild Pokémon wander in now and then: tap one to say hello, or to befriend it and bring it to your Bank. Options, Screensaver lets the park run on its own when you leave it be.",
        ];
        for (var i = 0; i < pages.Length; i++)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, $"How to play  {i + 1}/{pages.Length}", pages[i]);
    }

    // The player picks: the real clock and the day's weather, or a time and a sky of their own.
    private const string SkyKey = "pkforge.pokepark.sky.v1";

    private void LoadSky()
    {
        var saved = Preferences.Default.Get(SkyKey, "auto|auto").Split('|');
        _scene.PhaseOverride = saved.Length > 0 && Enum.TryParse<ParkDayPhase>(saved[0], out var phase) ? phase : null;
        _scene.WeatherOverride = saved.Length > 1 && Enum.TryParse<ParkWeather>(saved[1], out var weather) ? weather : null;
    }

    private async Task SkyAsync()
    {
        var phase = await PadMenu.ShowAsync(_host, "Time of day",
            "Automatic follows this device's clock: mornings, evenings and nights come as they do outside. Nothing is sent anywhere. Or pick one and the park stays there.",
            "Automatic", "Morning", "Day", "Evening", "Night");
        if (phase is null) return;
        var city = _weather.City;
        var weather = await PadMenu.ShowAsync(_host, "Weather",
            "Automatic changes the sky a few times a day; nothing is sent anywhere. Real weather (optional) follows a city you type: only that name, " +
            "then the city's coordinates, go to Open-Meteo, once an hour at most. Never your location. Or pick a sky and it stays.",
            new PadOption("Automatic", Detail: "The park's own sky."),
            new PadOption(city is null ? "Real weather: Off" : $"Real weather: {city}", Detail: "Sends only a city name."),
            new PadOption("Sunny"), new PadOption("Cloudy"), new PadOption("Rain"), new PadOption("Storm"));
        if (weather is null) return;
        var real = weather.StartsWith("Real weather", StringComparison.Ordinal);
        if (real)
        {
            var typed = await TextPopup.ShowLineAsync(_host, "Real weather",
                "City, e.g. Lyon (only this is sent)", city ?? "", confirm: "OK");
            if (typed is null) return;
            _weather.City = typed;
            real = _weather.City is not null;
        }
        else _weather.City = null;
        Preferences.Default.Set(SkyKey, $"{(phase == "Automatic" ? "auto" : phase)}|{(weather == "Automatic" || real ? "auto" : weather)}");
        LoadSky();
        if (real)
        {
            var reading = await RefreshWeatherAsync();
            _scene.Notify(reading is null
                ? $"Could not read the weather in {_weather.City} right now: the park keeps its own sky"
                : $"Real weather in {_weather.City}: {reading.Weather}");
        }
    }

    // The real weather, when it is on: read again at most once an hour; offline, silently nothing.
    private async Task<ParkWeatherReading?> RefreshWeatherAsync()
    {
        _scene.RealWeather = _weather.Current;
        if (_weather.City is null) return null;
        var reading = await _weather.RefreshAsync();
        _scene.RealWeather = _weather.Current;
        return reading;
    }

    // ── Games ────────────────────────────────────────────────────────────────

    private string? _digFor;
    private int _seekCursor = -1;
    private bool _gameResultShowing;

    private async Task PlayAsync(ParkPokemon mon, string game)
    {
        switch (game)
        {
            case "fetch": BeginThrow(mon); return;
            case "dig":
                _digFor = mon.Id;
                _status.Text = $"Tap where {mon.Name} should dig  ·  A digs right here  ·  B changes your mind";
                return;
        }
        var kind = game switch { "hide" => ParkGameKind.HideAndSeek, "race" => ParkGameKind.Race, "spar" => ParkGameKind.Spar, _ => ParkGameKind.Brush };
        var reply = _scene.Life.StartGame(kind, mon.Id);
        if (reply.Accepted && _scene.Life.Game is { } started && kind is ParkGameKind.HideAndSeek or ParkGameKind.Race)
        {
            var place = kind == ParkGameKind.Race ? ParkMap.Park.RegionAt(ParkMap.Park.AtHome(168, 100).X, ParkMap.Park.AtHome(168, 100).Y)
                : ParkMap.Park.RegionAt(started.Spot.X, started.Spot.Y);
            _scene.Viewport.ShowAll(place.Center.X, place.Center.Y);
        }
        _seekCursor = -1;
        _scene.SeekCursor = null;
        if (reply.Line is { } line)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line, () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, reply.Emotion));
    }

    private void Dig(ParkPoint? target)
    {
        if (_digFor is not { } id) return;
        _digFor = null;
        UpdateStatus();
        if (_scene.Life[id] is not { } actor) return;
        Say(id, _scene.Life.StartDig(id, target ?? new ParkPoint(actor.X, actor.Y + 10)));
    }

    // A tap during a game: look for the hider, cheer a racer, brush, block a strike.
    private bool PlayTap(ParkPoint point, SKPoint location, long now)
    {
        var life = _scene.Life;
        switch (life.Game)
        {
            case { Kind: ParkGameKind.HideAndSeek, Stage: ParkGameStage.Playing } hide:
                if (life.Seek(point)) Deed(ParkDeed.Found, hide.HostId);
                return true;
            case { Kind: ParkGameKind.Race, Stage: ParkGameStage.Playing or ParkGameStage.Countdown } race:
                var index = _scene.HitTest(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height, now);
                if (index >= 0 && race.Players.Contains(_scene.Residents[index].Id)) life.Cheer(_scene.Residents[index].Id);
                return true;
            case { Kind: ParkGameKind.Brush, Stage: ParkGameStage.Playing }:
                life.Brush(12);
                return true;
            case { Kind: ParkGameKind.Spar, Stage: ParkGameStage.Playing }:
                life.Block();
                return true;
            case { Stage: not ParkGameStage.Over }:
                return true;
        }
        return false;
    }

    private void PlayPad()
    {
        var life = _scene.Life;
        switch (life.Game)
        {
            case { Kind: ParkGameKind.HideAndSeek, Stage: ParkGameStage.Playing } hide when _scene.SeekCursor is { } spot:
                if (life.Seek(spot)) Deed(ParkDeed.Found, hide.HostId);
                break;
            case { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(1); break;
            case { Kind: ParkGameKind.Race } race: life.Cheer(race.HostId); break;
            case { Kind: ParkGameKind.Brush }: life.Brush(25); break;
            case { Kind: ParkGameKind.Spar }: life.Block(); break;
        }
    }

    // With the pad, hide and seek steps through the places one could hide.
    private void MoveSeekCursor(int delta)
    {
        if (_scene.Life.Game is not { Kind: ParkGameKind.HideAndSeek } game) return;
        var place = ParkMap.Park.RegionAt(game.Spot.X, game.Spot.Y);
        var spots = ParkMap.Park.Spots.Where(s => s.Kind == ParkSpotKind.Hide && place.Contains(s.X, s.Y)).ToArray();
        if (spots.Length == 0) return;
        _seekCursor = ((_seekCursor + delta) % spots.Length + spots.Length) % spots.Length;
        _scene.SeekCursor = new ParkPoint(spots[_seekCursor].X, spots[_seekCursor].Y);
        _scene.Viewport.Follow(spots[_seekCursor].X, spots[_seekCursor].Y);
    }

    private bool Brushing(SKPoint from, SKPoint to)
    {
        if (_scene.Life.Game is not { Kind: ParkGameKind.Brush, Stage: ParkGameStage.Playing } game || _scene.Life[game.HostId] is not { } host) return false;
        var a = _scene.ScreenToMap(from.X / _canvas.CanvasSize.Width, from.Y / _canvas.CanvasSize.Height);
        var b = _scene.ScreenToMap(to.X / _canvas.CanvasSize.Width, to.Y / _canvas.CanvasSize.Height);
        if (MathF.Abs(b.X - host.X) > 18 || b.Y > host.Y + 6 || b.Y < host.Y - 34) return false;
        _scene.Life.Brush(MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));
        return true;
    }

    private void QuitGame()
    {
        _scene.Life.EndGame();
        _scene.SeekCursor = null;
        _scene.Viewport.Reset();
    }

    // A finished game shows its result once, then ends; dug-up treasure joins the collection.
    private void CheckGameOver()
    {
        if (_gameResultShowing || _menuOpen || _scene.Life.Game is not { Stage: ParkGameStage.Over, Result: { } result } game) return;
        _gameResultShowing = true;
        if (game.Treasure is { } treasure) _treasures.Add(treasure);
        if (ParkGoals.DeedOf(game) is { } deed) Deed(deed, game.HostId);
        var host = _scene.Residents.FirstOrDefault(r => r.Id == game.HostId);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _menuOpen = true;
            try
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, host?.Name ?? "Poképark", result,
                    host is null ? null : () => _walking.GetPortrait(host.Species, host.Form, host.Shiny, "Joyous"));
            }
            finally
            {
                _menuOpen = false;
                _gameResultShowing = false;
                QuitGame();
                SaveCare();
            }
        });
    }

    // A reaction with its own line (a refusal, an accident) shows in the dialogue box.
    private void Say(string id, ParkReply reply)
    {
        if (reply.Line is not { } line || _menuOpen || _scene.Residents.FirstOrDefault(r => r.Id == id) is not { } mon) return;
        _menuOpen = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _scene.CoveredBottom = PokeparkCardOverlay.CoveredHeight(_canvas.CanvasSize.Width);
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line,
                    () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, reply.Emotion));
            }
            finally { _menuOpen = false; _scene.CoveredBottom = 0; }
        });
    }

    private void Tap(SKPoint location, long now)
    {
        var point = _scene.ScreenToMap(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height);
        // The banner at the top: "go see".
        if (Offering && location.Y < _canvas.CanvasSize.Height * .16f) { GoSee(_offered!); return; }
        if (_throwFor is not null) { Throw(point); return; }
        if (_digFor is not null) { Dig(point); return; }
        if (PlayTap(point, location, now)) return;
        // A wild visitor: it may bolt, or leave you a little gift.
        if (_scene.VisitorAt(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height) is { } wild)
        {
            if (ParkLife.IsKeeper(wild)) { if (!_menuOpen) MainThread.BeginInvokeOnMainThread(() => _ = DaycareAsync()); return; }
            // A wild one (not a friend back from the Wilds): say hello, or try to catch it.
            if (_scene.Life.CatchTarget(wild) is not null) { if (!_menuOpen) MainThread.BeginInvokeOnMainThread(() => _ = WildAsync(wild)); return; }
            var reply = _scene.Life.Befriend(wild);
            if (reply.Gift is { } gift) _treasures.Add(gift);
            var friend = _scene.Life.OldFriendOf(wild);
            if (reply.Line is { } line && !_menuOpen)
            {
                _menuOpen = true;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await PokeparkCardOverlay.ShowDialogueAsync(_host, _scene.Life[wild]?.Resident.Name ?? "A wild Pokémon", line,
                            friend is null ? null : () => _walking.GetPortrait(friend.Species, friend.Form, friend.Shiny, reply.Emotion));
                    }
                    finally { _menuOpen = false; }
                });
            }
            return;
        }
        var index = _scene.HitTest(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height, now);
        if (index >= 0)
        {
            SelectResident(index);
            _ = DetailsAsync();
            _lastTapTime = 0;
            return;
        }
        // The statue in the middle of the park: who made all this.
        if (StatueArea.Contains(point.X, point.Y)) { MainThread.BeginInvokeOnMainThread(() => _ = CreditsAsync()); return; }
        if (SnowmanArea.Contains(point.X, point.Y)) { MainThread.BeginInvokeOnMainThread(() => _ = SnowmanAsync()); return; }
        if (now - _lastTapTime < 350) { _scene.Viewport.Reset(); _lastTapTime = 0; }
        else _lastTapTime = now;
    }

    // ── Catching a wild visitor ─────────────────────────────────────────────

    private async Task WildAsync(string id)
    {
        if (_menuOpen || _scene.Life.CatchTarget(id) is not { } target) return;
        _menuOpen = true;
        try
        {
            var choice = await PadMenu.ShowAsync(_host, $"A wild {target.Name}{(target.Shiny ? " (shiny!)" : "")}", $"Level {target.Level}. {target.Hint}",
                new PadOption("Say hello", IconPath: "park", Detail: "It may leave you a little gift, or run off."),
                new PadOption("Try to befriend it", IconPath: "ball", Detail: $"Win it over and it comes with you, to your Bank as a legal Pokémon. {ParkCatch.Tries} tries at most."),
                new PadOption("Leave it be"));
            if (choice == "Say hello")
            {
                var reply = _scene.Life.Befriend(id);
                if (reply.Gift is { } gift) _treasures.Add(gift);
                if (reply.Line is { } line) await PokeparkCardOverlay.ShowDialogueAsync(_host, target.Name, line);
                return;
            }
            if (choice != "Try to befriend it") return;
            while (_scene.Life.CatchTarget(id) is { } now)
            {
                var approach = await PadMenu.ShowAsync(_host, $"Befriend the {now.Name}", $"{now.Hint} {now.TriesLeft} {(now.TriesLeft == 1 ? "try" : "tries")} left.",
                    new PadOption("Offer a berry", IconPath: "berry", Detail: "For the curious and the hungry."),
                    new PadOption("Crouch and wait", IconPath: "park", Detail: "For the shy and the calm."),
                    new PadOption("Toss a ball to play", IconPath: "ball", Detail: "For the playful and the bold."));
                var picked = approach switch
                {
                    "Offer a berry" => ParkApproach.Berry, "Crouch and wait" => ParkApproach.Wait, "Toss a ball to play" => ParkApproach.Ball, _ => (ParkApproach?)null
                };
                if (picked is not { } way) return;
                var result = _scene.Life.TryCatch(id, way);
                await PokeparkCardOverlay.ShowDialogueAsync(_host, now.Name, result.Line);
                if (result.Outcome == ParkCatchOutcome.Caught && result.Target is { } caught)
                {
                    var outcome = await SendCaughtAsync(caught);
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, caught.Name, outcome);
                    return;
                }
                if (result.Outcome == ParkCatchOutcome.Fled) return;
            }
        }
        finally { _menuOpen = false; }
    }

    // A befriended wild one goes to the Bank as a legal Pokémon of a game the player picks:
    // generated by the offline legalizer, as a wild encounter of that game would be.
    private async Task<string> SendCaughtAsync(ParkCatchTarget caught)
    {
        var keepsake = new ParkTreasure($"a feather from a wild {caught.Name} that came along", 2);
        if (HardcoreMode.Blocks(SaveAction.CreateMon, out _))
        {
            _treasures.Add(keepsake);
            return $"Hardcore mode is on, so no Pokémon is made outside the games. The {caught.Name} leaves you a keepsake and goes back to the wild.";
        }
        var services = IPlatformApplication.Current!.Services;
        var open = services.GetService<ISaveSessionService>()?.CurrentSession;
        var eras = new[] { "Generation I", "Generation II", "Generation III", "Generation IV", "Generation V", "Generation VI", "Generation VII", "Generation VIII", "Generation IX" };
        var options = new List<PadOption>();
        var openName = open is null ? null : $"For {open.GameNames.FirstOrDefault() ?? "the open save"}";
        if (openName is not null) options.Add(new PadOption(openName, IconPath: "game", Detail: "Made legal for your open save's game, with your trainer's details."));
        options.AddRange(eras.Select(e => new PadOption(e, Detail: "A placeholder trainer (PKForge), editable later.")));
        var game = await PadMenu.ShowAsync(_host, $"Where does {caught.Name} come from?",
            "It goes to your Bank as a legal Pokémon of the game you pick, caught in a Poké Ball. If it cannot be legal there, it stays a keepsake.", [.. options]);
        if (game is null)
        {
            _treasures.Add(keepsake);
            return $"The {caught.Name} waves goodbye and leaves you a keepsake.";
        }
        var engine = services.GetRequiredService<ISaveEngine>();
        var session = game == openName ? open! : engine.OpenBlankSession(Array.IndexOf(eras, game) + 1);
        var legalizer = services.GetRequiredService<ILegalizerService>();
        var overlay = LoadingOverlay.Show(_host, $"Welcoming {caught.Name}…", "The offline legalizer is at work.");
        GeneratedEntity? made;
        try
        {
            const int PokeBall = 4;
            made = await Task.Run(() => legalizer.GenerateData(session, new GenerationRequest(caught.Species, caught.Level, caught.Shiny, null, null, PokeBall, null))
                ?? (caught.Shiny ? null : legalizer.GenerateData(session, new GenerationRequest(caught.Species, null, false, null, null, null, null))));
        }
        catch (Exception e)
        {
            AppLog.Info("pokepark", $"Catch: generation failed: {e.Message}");
            made = null;
        }
        finally { overlay.Close(); }
        if (made is null)
        {
            _treasures.Add(keepsake);
            return $"No legal {caught.Name} fits {(game == openName ? "that game" : game)}, so nothing was added. It leaves you a keepsake before heading home.";
        }
        var bank = services.GetRequiredService<IBankService>();
        var entry = bank.Add(made.Data, made.Info with { SourceName = "Poképark" });
        AppLog.Info("pokepark", $"Caught a wild {caught.Name} into the Bank");
        return $"{caught.Name} is in your Bank, box {entry.Box + 1}, slot {entry.Slot + 1}.";
    }

    // The left stick glides across the map, gently near the middle and faster as it leans;
    // the right stick zooms in (up) and out (down). A Pokémon picked with Left/Right is followed again.
    public void OnStick(float leftX, float leftY, float rightX, float rightY)
    {
        static float Curve(float value) => value * MathF.Abs(value);
        if ((leftX != 0 || leftY != 0 || rightX != 0 || rightY != 0) && WakeUp()) return;
        if (leftX != 0 || leftY != 0 || rightY != 0) StopSpectating();
        _scene.Viewport.Stick(Curve(leftX), Curve(leftY), Curve(rightY));
    }

    public bool OnPadButton(PadButton button)
    {
        if (WakeUp()) return true;
        switch (button)
        {
            case PadButton.B when _throwFor is not null || _digFor is not null: _throwFor = _digFor = null; UpdateStatus(); return true;
            case PadButton.A when _throwFor is not null: Throw(null); return true;
            case PadButton.A when _digFor is not null: Dig(null); return true;
            case PadButton.B when _scene.Life.Game is not null: QuitGame(); return true;
            case PadButton.A when _scene.Life.Game is not null: PlayPad(); return true;
            case PadButton.Left when _scene.Life.Game is { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(-1); return true;
            case PadButton.Right when _scene.Life.Game is { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(1); return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            case PadButton.X when Offering: GoSee(_offered!); return true;
            case PadButton.Select: ToggleSpectate(); return true;
            case PadButton.X: _ = TakePhotoAsync(); return true;
            case PadButton.A: _ = DetailsAsync(); return true;
            case PadButton.Y: _ = PicnicAsync(); return true;
            case PadButton.L: StopSpectating(); FlyToPlace(-1); return true;
            case PadButton.R: StopSpectating(); FlyToPlace(1); return true;
            case PadButton.Up: _scene.Viewport.Zoom(1); return true;
            case PadButton.Down: _scene.Viewport.Zoom(-1); return true;
            case PadButton.Left: StopSpectating(); SelectResident(_selected - 1); return true;
            case PadButton.Right: StopSpectating(); SelectResident(_selected + 1); return true;
            case PadButton.Start: _ = OptionsAsync(); return true;
            default: return true;
        }
    }
}
