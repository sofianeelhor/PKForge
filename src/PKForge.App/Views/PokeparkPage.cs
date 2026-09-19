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
    private readonly PokeparkScene _scene;
    private readonly SKCanvasView _canvas = new();
    private readonly Grid _host;
    private readonly Label _status;
    private readonly IDispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _active;
    private bool _menuOpen;
    private bool _widgetDirty;
    private long _lastPublished;
    private int _selected;
    private readonly List<string> _recentEncounters = [];

    public PokeparkPage(PokeparkService park, ISpriteService sprites, PokeparkSpriteService walking)
    {
        _park = park; _sprites = sprites; _walking = walking;
        _scene = new(sprites, walking);
        Title = "Poképark";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = UiTokens.Housing;
        _status = new Label { TextColor = UiTokens.Ink1, FontSize = 13, Margin = new Thickness(14, 4) };
        _canvas.EnableTouchEvents = true;
        _canvas.Touch += (_, e) =>
        {
            if (e.ActionType == SKTouchAction.Pressed) { e.Handled = true; return; }
            if (e.ActionType != SKTouchAction.Released || _menuOpen) return;
            var index = _scene.HitTest(e.Location.X / _canvas.CanvasSize.Width, e.Location.Y / _canvas.CanvasSize.Height, _clock.ElapsedMilliseconds);
            if (index >= 0) { SelectResident(index); _ = DetailsAsync(); }
            e.Handled = true;
        };
        _canvas.PaintSurface += (_, e) => _scene.Draw(e.Surface.Canvas, e.Info.Width, e.Info.Height, _clock.ElapsedMilliseconds);
        var panel = Kit.LcdPanel(_canvas, padding: 4);
        panel.Margin = new Thickness(12, 4);
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
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) =>
        {
            UpdateJournal();
            _canvas.InvalidateSurface();
            if (_widgetDirty && _clock.ElapsedMilliseconds - _lastPublished > 2000) PublishWidget();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); _active = true;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        App.Resumed += Resume; App.Suspended += Suspend;
        _clock.Start(); _timer.Start();
        Reload();
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
        _active = false; _timer.Stop(); _clock.Stop();
        App.Resumed -= Resume; App.Suspended -= Suspend;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        if (_widgetDirty) PublishWidget();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Clear();
        base.OnDisappearing();
    }
    private void Resume() { if (_active) { _clock.Start(); _timer.Start(); } }
    private void Suspend() { _timer.Stop(); _clock.Stop(); if (_widgetDirty) PublishWidget(); }

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
        _widgetDirty = true;
        _canvas.InvalidateSurface();
    }
    private void AssetsLoaded() => MainThread.BeginInvokeOnMainThread(() =>
    {
        _widgetDirty = true;
        if (_active) _canvas.InvalidateSurface();
        else PublishWidget();
    });
    private void SelectResident(int index)
    {
        _selected = _scene.Residents.Count == 0 ? 0 : (index + _scene.Residents.Count) % _scene.Residents.Count;
        _scene.SelectedIndex = _scene.Residents.Count == 0 ? -1 : _selected;
        UpdateJournal();
        _canvas.InvalidateSurface();
    }

    private void UpdateJournal()
    {
        var journal = IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>();
        if (journal is null || _scene.Residents.Count == 0) { journal?.Clear(); return; }
        var mon = _scene.Residents[_selected]; var state = _scene.GetResidentState(_selected, _clock.ElapsedMilliseconds); var p = Personality(mon.Id);
        journal.Resident = mon; journal.Mood = state.Mood; journal.Activity = state.Activity; journal.Trait = p.Trait; journal.Likes = p.Likes; journal.Story = p.Story;
    }

    private void PublishWidget()
    {
        var frames = new List<byte[]>();
        using var bitmap = new SKBitmap(PokeparkWidgetPublisher.FrameWidth, PokeparkWidgetPublisher.FrameHeight);
        using var canvas = new SKCanvas(bitmap);
        for (var frame = 0; frame < PokeparkWidgetPublisher.FrameCount; frame++)
        {
            _scene.DrawWidgetFrame(canvas, bitmap.Width, bitmap.Height, frame);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            frames.Add(data.ToArray());
        }
        try { PokeparkWidgetPublisher.Publish(frames); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine($"Poképark widget: {ex.Message}"); }
        _widgetDirty = false; _lastPublished = _clock.ElapsedMilliseconds;
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
                "Auto-fill mode", "Invite Pokémon", "Widget help", "Sprite credits");
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
            }
            if (choice == "Invite Pokémon") await PokeparkSpeechBubble.ShowAsync(_host, "Park guide", "Open a Pokémon’s action menu in your save boxes or Bank, then choose Send to Poképark. Its original stays right where it is.");
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
            var personality = Personality(mon.Id);
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
            var environmentScenes = ParkEnvironmentScenes.For((ParkEnvironmentId)_scene.EnvironmentIndex, action, types, shy: mon.Name.Contains("shy", StringComparison.OrdinalIgnoreCase));
            var encounter = environmentScenes.Count > 0
                ? new ParkEncounter($"environment:{_scene.EnvironmentIndex}:{action}:{_recentEncounters.Count}", environmentScenes[Random.Shared.Next(environmentScenes.Count)].Replace("{name}", mon.Name), action)
                : ParkEncounters.Next(mon.Species, types, mon.Name, action, _recentEncounters);
            _recentEncounters.Add(encounter.Id); if (_recentEncounters.Count > 24) _recentEncounters.RemoveAt(0);
            _scene.Interact(_selected, _clock.ElapsedMilliseconds, action);
            var reaction = encounter.Text;
            ;
            await PokeparkSpeechBubble.ShowAsync(_host, mon.Name, reaction, mon, _sprites, _walking);
        }
        finally { _menuOpen = false; }
    }

    private void SwitchEnvironment(int delta)
    {
        _scene.EnvironmentIndex = (_scene.EnvironmentIndex + delta + 4) % 4;
        _status.Text = $"{_scene.EnvironmentName}  ·  L/R switch habitats";
        UpdateJournal();
        _canvas.InvalidateSurface(); _widgetDirty = true;
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

    private static (string Trait, string Likes, string Story, string Greeting) Personality(string id)
    {
        uint hash = 2166136261;
        foreach (var letter in id) hash = (hash ^ letter) * 16777619;
        return (hash % 6) switch
        {
            0 => ("Curious explorer", "new paths and rustling grass", "Keeps checking whether that flower has grown since yesterday.", "tilts its head, curious about your day."),
            1 => ("Easygoing dreamer", "quiet afternoons and soft grass", "Has found a sunny patch and considers it the best spot in the park.", "gives you a slow, contented nod."),
            2 => ("Friendly neighbor", "company and little greetings", "Always seems to be nearby when a new resident arrives.", "comes closer to keep you company."),
            3 => ("Playful wanderer", "stretching its legs and surprises", "Once followed a drifting leaf all the way across the meadow.", "does a cheerful little wiggle."),
            4 => ("Thoughtful observer", "watching the pond and peaceful corners", "Is convinced the ripples on the pond are trying to say something.", "pauses its daydream to look up at you."),
            _ => ("Flower enthusiast", "colorful petals and fresh air", "Has a favorite flower, but seems to pick a different one every day.", "looks delighted that you stopped by.")
        };
    }

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
