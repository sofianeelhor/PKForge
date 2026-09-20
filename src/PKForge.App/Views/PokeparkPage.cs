using System.Diagnostics;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

public sealed class PokeparkPage : ContentPage, IPadHandler
{
    private readonly PokeparkService _park;
    private readonly ISpriteService _sprites;
    private readonly PokeparkSpriteService _walking;
    private readonly PokeparkNarrativeMemory _narrativeMemory;
    private readonly PokeparkSocialService _social;
    private readonly ParkOfflineJournalService _offlineJournal;
    private readonly PokeparkScene _scene;
    private readonly SKCanvasView _canvas = new();
    private readonly Grid _host;
    private readonly Label _status;
    private readonly IDispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly SemaphoreSlim _widgetPublishGate = new(1, 1);
    private readonly object _renderGate = new();
    private bool _active;
    private bool _menuOpen;
    private int _widgetRevision;
    private int _publishedWidgetRevision;
    private bool _loading;
    private int _selected;

    public PokeparkPage(PokeparkService park, ISpriteService sprites, PokeparkSpriteService walking,
        PokeparkNarrativeMemory narrativeMemory, PokeparkSocialService social,
        ParkOfflineJournalService offlineJournal)
    {
        _park = park; _sprites = sprites; _walking = walking;
        _narrativeMemory = narrativeMemory; _social = social; _offlineJournal = offlineJournal;
        _scene = new(sprites, walking);
        Title = "Poképark";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = UiTokens.Housing;
        _status = new Label { TextColor = UiTokens.Ink1, FontSize = 13, Margin = new Thickness(14, 4) };
        _canvas.EnableTouchEvents = true;
        _canvas.HorizontalOptions = LayoutOptions.Fill;
        _canvas.VerticalOptions = LayoutOptions.Start;
        _canvas.SizeChanged += (_, _) =>
        {
            if (_canvas.Width > 0)
                _canvas.HeightRequest = _canvas.Width * ParkMap.Height / (double)ParkMap.Width;
        };
        _canvas.Touch += (_, e) =>
        {
            if (e.ActionType == SKTouchAction.Pressed) { e.Handled = true; return; }
            if (e.ActionType != SKTouchAction.Released || _menuOpen) return;
            var index = _scene.HitTest(e.Location.X / _canvas.CanvasSize.Width, e.Location.Y / _canvas.CanvasSize.Height, _clock.ElapsedMilliseconds);
            if (index >= 0) { SelectResident(index); _ = DetailsAsync(); }
            e.Handled = true;
        };
        _canvas.PaintSurface += (_, e) =>
        {
            lock (_renderGate)
                _scene.Draw(e.Surface.Canvas, e.Info.Width, e.Info.Height, _clock.ElapsedMilliseconds);
        };
        var panel = Kit.LcdPanel(_canvas, padding: 0);
        panel.Margin = new Thickness(12, 4, 12, 0);
        panel.HorizontalOptions = LayoutOptions.Fill;
        panel.VerticalOptions = LayoutOptions.Start;
        var footer = DsChrome.Footer(
            ("B", "Back", () => _ = Navigation.PopAsync()),
            ("A", "Meet Pokémon", () => _ = DetailsAsync()),
            ("LR", "Habitat", () => SwitchEnvironment(1)),
            ("+", "Options", () => _ = OptionsAsync()));
        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), _status, panel, footer }
        };
        Grid.SetRow(_status, 1); Grid.SetRow(panel, 2); Grid.SetRow(footer, 3);
        _host = new Grid { Children = { DsChrome.GridBackground(), root } };
        Content = _host;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += (_, _) =>
        {
            UpdateJournal();
            _canvas.InvalidateSurface();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); _active = true;
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Open();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        App.Resumed += Resume; App.Suspended += Suspend;
        _clock.Start(); _timer.Start();
        // The persisted roster is cheap and gives the first paint immediately.
        // Initialization only scans on first use and refreshes this view afterwards.
        Reload();
        _ = LoadParkAsync();
        // A park can be opened from the widget/deep link without passing through
        // HomePage. Ensure Thor's lower display is alive and observing the journal.
        var secondary = IPlatformApplication.Current?.Services.GetService<PKForge.Domain.ISecondaryDisplayHost>();
        if (secondary?.IsAvailable == true)
            _ = ShowSecondaryJournalAsync(secondary);
    }

    private static async Task ShowSecondaryJournalAsync(PKForge.Domain.ISecondaryDisplayHost host)
    {
        try { await host.ShowPokeparkJournalAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Poképark second screen: {ex.Message}"); }
    }
    protected override void OnDisappearing()
    {
        _offlineJournal.MarkCurrent();
        _active = false; _timer.Stop(); _clock.Stop();
        App.Resumed -= Resume; App.Suspended -= Suspend;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        if (WidgetDirty) _ = PublishWidgetAsync();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Clear();
        base.OnDisappearing();
    }
    private void Resume() { if (_active) { _clock.Start(); _timer.Start(); } }
    private void Suspend()
    {
        _offlineJournal.MarkCurrent();
        _timer.Stop(); _clock.Stop(); if (WidgetDirty) _ = PublishWidgetAsync();
    }

    private void RunOfflineLife()
    {
        var offlineResidents = _scene.Residents.Select(mon => new ParkOfflineResident(
            mon.Id, mon.Name, mon.Species, HomeHabitat(mon),
            "exploring and enjoying the park")).ToArray();
        var offline = _offlineJournal.Resume(offlineResidents);
        var socialResidents = _scene.Residents.Select(mon =>
        {
            var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
            return new ParkSocialPokemon(mon.Id, mon.Name, mon.Species, types,
                PersonalityKind(mon.Id), Habitat: HomeHabitat(mon));
        }).ToArray();
        var social = _social.SimulateOffline(socialResidents);
        var newStories = offline.Events.Select(e => e.Text).Concat(social.Select(e => e.Text)).Take(4).ToArray();
        if (newStories.Length == 0) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!_active || _menuOpen) return;
            _menuOpen = true;
            try
            {
                await PokeparkSpeechBubble.ShowAsync(_host, "While you were away",
                    string.Join("\n\n", newStories));
            }
            finally { _menuOpen = false; }
        });
    }

    private async Task LoadParkAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            // Save-backed candidate discovery reads and hashes every populated slot.
            // Keep it off the UI thread so entering the park does not stall navigation.
            await Task.WhenAll(
                Task.Run(_park.EnsureInitialized),
                _scene.WarmEnvironmentsAsync()).ConfigureAwait(true);
            if (!_active) return;
            Reload();
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
        SelectResident(Math.Min(_selected, _scene.Residents.Count - 1));
        _status.Text = _scene.Residents.Count == 0
            ? "Your meadow is waiting. Select a Pokémon in a box or Bank → Send to Poképark."
            : $"{_scene.EnvironmentName}  ·  {_scene.Residents.Count}/12 residents  ·  Tap a Pokémon · A opens its card";
        UpdateJournal();
        foreach (var mon in _scene.Residents)
        {
            _sprites.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
            _walking.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
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
        var visible = Enumerable.Range(0, _scene.Residents.Count).Where(IsVisibleInHabitat).ToArray();
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
        UpdateJournal();
        _canvas.InvalidateSurface();
    }

    private void UpdateJournal()
    {
        var journal = IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>();
        if (journal is null || _scene.Residents.Count == 0 || !IsVisibleInHabitat(_selected))
        {
            journal?.Clear();
            return;
        }
        var mon = _scene.Residents[_selected]; var state = _scene.GetResidentState(_selected, _clock.ElapsedMilliseconds); var p = Personality(mon);
        journal.Resident = mon; journal.Mood = state.Mood; journal.Activity = state.Activity; journal.Trait = p.Trait; journal.Likes = p.Likes; journal.Story = p.Story;
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
            var revision = Volatile.Read(ref _widgetRevision);
            var frames = await Task.Run(() =>
            {
                var result = new List<byte[]>(PokeparkWidgetPublisher.FrameCount);
                using var bitmap = new SKBitmap(PokeparkWidgetPublisher.FrameWidth, PokeparkWidgetPublisher.FrameHeight);
                using var canvas = new SKCanvas(bitmap);
                for (var frame = 0; frame < PokeparkWidgetPublisher.FrameCount; frame++)
                {
                    lock (_renderGate)
                        _scene.DrawWidgetFrame(canvas, bitmap.Width, bitmap.Height, frame);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                    result.Add(data.ToArray());
                }
                return result;
            }).ConfigureAwait(false);
            try { PokeparkWidgetPublisher.Publish(frames); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine($"Poképark widget: {ex.Message}"); }
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
            var choice = await PadMenu.ShowAsync(_host, "Poképark options", $"Auto-fill: {current}. Existing residents are never moved or replaced.",
                "Park journal", "Auto-fill mode", "Invite Pokémon", "Empty park", "Widget help", "Sprite credits");
            if (choice == "Park journal")
                await ShowJournalAsync();
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
            if (choice == "Invite Pokémon") await PokeparkSpeechBubble.ShowAsync(_host, "Park guide", "Open a Pokémon’s action menu in your save boxes or Bank, then choose Send to Poképark. Its original stays right where it is.");
            else if (choice == "Empty park" && _scene.Residents.Count > 0 &&
                     await PadMenu.ConfirmAsync(_host, "Empty the park?", $"Remove all {_scene.Residents.Count} residents from Poképark? Their original Pokémon stay in their save or Bank.", "Empty park"))
            {
                _park.RemoveAllVisitors();
                Reload();
            }
            else if (choice == "Widget help")
                await PadMenu.ShowAsync(_host, "A meadow on your home screen", "Add PKForge Poképark in Cocoon's widget picker. Resize it to give your Pokémon more room. The full meadow shows a quiet, living moment; tap it to return here. Your residents are saved between visits.", "OK");
            else if (choice == "Sprite credits") await CreditsAsync();
        }
        finally { _menuOpen = false; }
    }

    private async Task DetailsAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkSpeechBubble.ShowAsync(_host, "An empty meadow", "Invite a Pokémon from its save-box or Bank action menu with Send to Poképark. Then tap it here to spend some time together.");
                return;
            }
            var mon = _scene.Residents[_selected];
            var state = _scene.GetResidentState(_selected, _clock.ElapsedMilliseconds);
            var personality = Personality(mon);
            var action = await PokeparkResidentCard.ShowAsync(_host, mon, _sprites, _walking,
                state.Mood, state.Activity, personality.Trait, personality.Likes, personality.Story);
            if (action is null) return;
            if (action == "leave")
            {
                if (await PadMenu.ConfirmAsync(_host, "Leave the park?", $"Remove {mon.Name} from this meadow? Its original stays in its box. You can invite it again anytime.", "Leave park"))
                { _park.RemoveVisitor(mon.Id); Reload(); }
                return;
            }
            var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
            var nearby = _scene.Residents.Where((_, i) => i != _selected && IsVisibleInHabitat(i))
                .OrderBy(_ => Random.Shared.Next()).FirstOrDefault()?.Name;
            var dialogue = ParkDialogueGenerator.Generate(new ParkDialogueContext(
                mon.Species, mon.Name, types, (ParkEnvironmentId)_scene.EnvironmentIndex,
                state.Activity, state.Mood, PersonalityKind(mon.Id), action, nearby),
                _narrativeMemory.Recent(mon.Id));
            _narrativeMemory.Remember(mon.Id, dialogue.Id);
            _scene.Interact(_selected, _clock.ElapsedMilliseconds, action);
            await PokeparkSpeechBubble.ShowAsync(_host, mon.Name, dialogue.Text, mon, _sprites, _walking);
        }
        finally { _menuOpen = false; }
    }

    private void SwitchEnvironment(int delta)
    {
        _scene.EnvironmentIndex = (_scene.EnvironmentIndex + delta + 4) % 4;
        SelectResident(0);
        _status.Text = $"{_scene.EnvironmentName}  ·  L/R switch habitats";
        MarkWidgetDirty();
    }

    private bool WidgetDirty => Volatile.Read(ref _widgetRevision) != Volatile.Read(ref _publishedWidgetRevision);
    private void MarkWidgetDirty() => Interlocked.Increment(ref _widgetRevision);

    private bool IsVisibleInHabitat(int index)
    {
        if (index < 0 || index >= _scene.Residents.Count) return false;
        if (_scene.EnvironmentIndex == 0) return true;
        var mon = _scene.Residents[index];
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        return _scene.EnvironmentIndex switch
        {
            1 => types.Contains(ParkType.Water),
            2 => types.Contains(ParkType.Fire) || types.Contains(ParkType.Ground) || types.Contains(ParkType.Rock),
            3 => types.Contains(ParkType.Flying) || types.Contains(ParkType.Ice),
            _ => true
        };
    }

    private async Task PicnicAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkSpeechBubble.ShowAsync(_host, "Picnic time", "A picnic is better with company! Invite a Pokémon from your boxes or Bank first.");
                return;
            }
            for (var i = 0; i < _scene.Residents.Count; i++)
                _scene.Interact(i, _clock.ElapsedMilliseconds, "snack");
            await PokeparkSpeechBubble.ShowAsync(_host, "Picnic time!", "You share a basket of berries with everyone. Happy little sounds drift across the meadow. There’s enough for every resident!");
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

    private static ParkPersonality PersonalityKind(string id)
    {
        uint hash = 2166136261;
        foreach (var letter in id) hash = (hash ^ letter) * 16777619;
        return (hash % 9) switch
        {
            0 => ParkPersonality.Curious,
            1 => ParkPersonality.Calm,
            2 => ParkPersonality.Gentle,
            3 => ParkPersonality.Playful,
            4 => ParkPersonality.Helpful,
            5 => ParkPersonality.Shy,
            6 => ParkPersonality.Energetic,
            7 => ParkPersonality.Proud,
            _ => ParkPersonality.Bold
        };
    }

    private static ParkEnvironmentId HomeHabitat(ParkPokemon mon)
    {
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        if (types.Contains(ParkType.Water)) return ParkEnvironmentId.WaterfallLake;
        if (types.Contains(ParkType.Fire) || types.Contains(ParkType.Ground))
            return ParkEnvironmentId.EmberGrove;
        if (types.Contains(ParkType.Flying) || types.Contains(ParkType.Ice) || types.Contains(ParkType.Rock))
            return ParkEnvironmentId.SkySummit;
        return ParkEnvironmentId.Commons;
    }

    private async Task ShowJournalAsync()
    {
        var offline = _offlineJournal.Load().Journal.Select(e => (At: e.Timestamp, e.Text));
        var social = _social.Journal(40).Select(e => (At: e.OccurredAt, e.Text));
        var entries = offline.Concat(social).OrderByDescending(e => e.At).Take(12).ToArray();
        if (entries.Length == 0)
        {
            await PokeparkSpeechBubble.ShowAsync(_host, "Park journal",
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
            await PokeparkSpeechBubble.ShowAsync(_host, "Park journal", entries[index].Text);
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    private async Task CreditsAsync()
    {
        var residents = _scene.Residents.DistinctBy(p => (p.Species, p.Form, p.Shiny)).ToArray();
        var labels = residents.Select((p, i) => $"{i + 1}. {p.Name}").ToArray();
        var choice = await PadMenu.ShowAsync(_host, "Sprite credits",
            "PMD SpriteCollab · Community sprites: CC BY-NC 4.0. Original PMD sprites: CHUNSOFT. Pokémon © Nintendo / Creatures / GAME FREAK. Sprites displayed with scaling; choose a visitor for its contributors and source.",
            labels.Concat(["Open sprite repository", "Close"]).ToArray());
        if (choice == "Open sprite repository") await Launcher.Default.OpenAsync("https://sprites.pmdcollab.org/");
        var index = Array.IndexOf(labels, choice);
        if (index < 0) return;
        var mon = residents[index];
        var raw = _walking.GetAttribution(mon.Species, mon.Form, mon.Shiny);
        var authors = raw is null ? "Bundled PKHeX sprite; PMD walking sprite unavailable."
            : string.Join(", ", raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t')).Where(parts => parts.Length > 1).Select(parts => parts[1]).Distinct());
        var action = await PadMenu.ShowAsync(_host, mon.Name, authors, "View original credits", "Close");
        if (action == "View original credits") await Launcher.Default.OpenAsync(raw is null
            ? "https://github.com/kwsch/PKHeX"
            : PokeparkSpriteService.GetSourceUrl(mon.Species, mon.Form, mon.Shiny));
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            case PadButton.A:
            case PadButton.X: _ = DetailsAsync(); return true;
            case PadButton.Y: _ = PicnicAsync(); return true;
            case PadButton.L: SwitchEnvironment(-1); return true;
            case PadButton.R: SwitchEnvironment(1); return true;
            case PadButton.Left: SelectResident(_selected - 1); return true;
            case PadButton.Right: SelectResident(_selected + 1); return true;
            case PadButton.Start: _ = OptionsAsync(); return true;
            default: return true;
        }
    }
}
