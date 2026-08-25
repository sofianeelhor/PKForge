using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The met / origin block - where, when and how a Pokémon was obtained. This is the
/// identity behind legality: origin game, met location, level, date, egg data,
/// language, fateful-encounter flag, and trainer ID. Edits apply to the session in
/// memory; the caller persists. Reusable by any editing session (bank or in-save).
/// </summary>
public static class MetOriginEditor
{
    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            MetInfo m;
            try { m = session.GetMetInfo(box, slot); }
            catch (Exception error) { await EditorMenu.ShowAsync(host, "MET / ORIGIN", error.Message, "OK"); return dirty; }

            var options = new List<PadOption>
            {
                new($"Origin game · {m.VersionName}"),
                new($"Met location · {m.MetLocationName}"),
                new($"Met level · {m.MetLevel}"),
                new($"Met date · {(m.MetDate.Length == 0 ? "unset" : m.MetDate)}"),
                new($"Hatched from egg · {(m.IsEgg ? "yes" : "no")}"),
            };
            if (m.IsEgg)
            {
                options.Add(new($"Egg location · {m.EggLocationName}"));
                options.Add(new($"Egg date · {(m.EggDate.Length == 0 ? "unset" : m.EggDate)}"));
            }
            options.Add(new($"Language · {m.LanguageName}"));
            options.Add(new($"Fateful encounter · {(m.Fateful ? "yes" : "no")}"));
            options.Add(new($"Trainer ID · {m.TID:00000}"));
            options.Add(new($"Secret ID · {m.SID:00000}"));

            var choice = await EditorMenu.ShowAsync(host, "MET / ORIGIN", null, options.ToArray());
            if (choice is null) return dirty;

            if (choice.StartsWith("Origin game", StringComparison.Ordinal))
            {
                var pick = await PickChoiceAsync(host, "ORIGIN GAME", session.GetVersionChoices(), m.Version);
                if (pick is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(Version: v)); dirty = true; }
            }
            else if (choice.StartsWith("Met location", StringComparison.Ordinal))
            {
                var pick = await PickChoiceAsync(host, "MET LOCATION", session.GetLocationChoices(box, slot, egg: false), m.MetLocation);
                if (pick is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(MetLocation: v)); dirty = true; }
            }
            else if (choice.StartsWith("Met level", StringComparison.Ordinal))
            {
                var lv = await StatsPopup.ShowSingleAsync(host, "MET LEVEL", m.MetLevel, 100);
                if (lv is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(MetLevel: v)); dirty = true; }
            }
            else if (choice.StartsWith("Met date", StringComparison.Ordinal))
            {
                if (await EditDateAsync(host, "MET DATE", m.MetDate) is { } d) { session.ApplyMetEdit(box, slot, new MetEdit(MetDate: d)); dirty = true; }
            }
            else if (choice.StartsWith("Hatched from egg", StringComparison.Ordinal))
            {
                session.ApplyMetEdit(box, slot, new MetEdit(IsEgg: !m.IsEgg));
                dirty = true;
            }
            else if (choice.StartsWith("Egg location", StringComparison.Ordinal))
            {
                var pick = await PickChoiceAsync(host, "EGG LOCATION", session.GetLocationChoices(box, slot, egg: true), m.EggLocation);
                if (pick is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(EggLocation: v)); dirty = true; }
            }
            else if (choice.StartsWith("Egg date", StringComparison.Ordinal))
            {
                if (await EditDateAsync(host, "EGG DATE", m.EggDate) is { } d) { session.ApplyMetEdit(box, slot, new MetEdit(EggDate: d)); dirty = true; }
            }
            else if (choice.StartsWith("Language", StringComparison.Ordinal))
            {
                var pick = await PickChoiceAsync(host, "LANGUAGE", session.GetLanguageChoices(box, slot), m.Language);
                if (pick is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(Language: v)); dirty = true; }
            }
            else if (choice.StartsWith("Fateful", StringComparison.Ordinal))
            {
                session.ApplyMetEdit(box, slot, new MetEdit(Fateful: !m.Fateful));
                dirty = true;
            }
            else if (choice.StartsWith("Trainer ID", StringComparison.Ordinal))
            {
                var id = await StatsPopup.ShowSingleAsync(host, "TRAINER ID (TID)", m.TID, 65535);
                if (id is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(TID: v)); dirty = true; }
            }
            else if (choice.StartsWith("Secret ID", StringComparison.Ordinal))
            {
                var id = await StatsPopup.ShowSingleAsync(host, "SECRET ID (SID)", m.SID, 65535);
                if (id is { } v) { session.ApplyMetEdit(box, slot, new MetEdit(SID: v)); dirty = true; }
            }
        }
    }

    private static async Task<int?> PickChoiceAsync(Grid host, string title, IReadOnlyList<NamedChoice> choices, int current)
    {
        var items = choices.Select(c => new PickItem(c.Id, c.Name)).ToList();
        var picked = await PickerMenu.ShowAsync(host, title, items, current);
        return picked?.Id;
    }

    /// <summary>Date entry as yyyy-MM-dd; blank keeps it unchanged, "clear" empties it.</summary>
    private static async Task<string?> EditDateAsync(Grid host, string title, string current)
    {
        var text = await TextPopup.ShowAsync(host, title, "Type a date as YYYY-MM-DD, or 'clear' to unset.");
        if (text is null) return null;
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase)) return "";
        return DateOnly.TryParse(text, out var d) ? d.ToString("yyyy-MM-dd") : null;
    }
}

/// <summary>
/// PadMenu's editor twin: the same striped rows, d-pad ownership and hint bar, wearing
/// the editor chrome - the maroon Gen-5 header strip, PixelUI message text, white panel
/// with a warm-grey border. Shared by the bank summary, met/origin and potential editors.
/// </summary>
internal sealed class EditorMenu : IPadHandler
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DsFolderButton> _optionViews = [];
    private readonly PadOption[] _options;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private int _index;

    /// <summary>Shows the menu inside <paramref name="host"/> and returns the chosen option or null.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params string[] options) =>
        new EditorMenu(host, title, message, options.Select(o => new PadOption(o)).ToArray())._result.Task;

    /// <summary>Rich overload: options with accent colors or image icons.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params PadOption[] options) =>
        new EditorMenu(host, title, message, options)._result.Task;

    /// <summary>Two-option confirm box; true when the user picks <paramref name="confirmLabel"/>.</summary>
    public static async Task<bool> ConfirmAsync(Grid host, string title, string message, string confirmLabel = "OK")
    {
        var choice = await ShowAsync(host, title, message, confirmLabel, "Cancel");
        return choice == confirmLabel;
    }

    private EditorMenu(Grid host, string title, string? message, PadOption[] options)
    {
        _host = host;
        _options = options;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        // The editor list is one column of full-width rows: editor labels are long.
        var grid = new Grid { RowSpacing = 6 };
        for (var r = 0; r < options.Length; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < options.Length; i++)
        {
            var captured = options[i].Label;
            var button = new DsFolderButton(options[i], 50) { Tapped = () => Close(captured) };
            _optionViews.Add(button);
            grid.Add(button);
            Grid.SetRow(button, i);
        }

        var content = new VerticalStackLayout { Spacing = 10, Children = { Kit.HeaderBar(title) } };
        if (!string.IsNullOrEmpty(message))
        {
            content.Children.Add(new Label
            {
                Text = message,
                TextColor = UiTokens.Ink1,
                FontFamily = DsChrome.PixelFont,
                FontSize = 13,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
        content.Children.Add(grid);
        content.Children.Add(Kit.HintBar(("A", "CHOOSE", null), ("B", "CANCEL", () => Close(null))));

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 460);
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));

        Highlight(0);
        _router?.Push(this);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Highlight(_index - 1); return true;
            case PadButton.Down: Highlight(_index + 1); return true;
            case PadButton.Left: Highlight(_index - 1); return true;
            case PadButton.Right: Highlight(_index + 1); return true;
            case PadButton.A: Close(_options[_index].Label); return true;
            case PadButton.B: Close(null); return true;
            default: return true; // the menu owns the pad while open
        }
    }

    private void Highlight(int index)
    {
        _index = Math.Clamp(index, 0, _optionViews.Count - 1);
        for (var i = 0; i < _optionViews.Count; i++)
            _optionViews[i].Selected = i == _index;
    }

    private void Close(string? result)
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
