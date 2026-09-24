using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>
/// A DS-style choice box: scrim, device panel, flat options; d-pad up/down + A/B
/// drive it (it takes over the gamepad router while open), touch works too.
/// </summary>
/// <summary>A menu entry: label plus optional colored glyph badge or image icon - never bare text.
/// <paramref name="Detail"/> is an optional quiet line under the label ("PP 35 → 56");
/// the choice result is still the label.</summary>
public sealed record PadOption(string Label, string? Glyph = null, Color? Accent = null, string? IconPath = null, string? Detail = null);

public sealed class PadMenu : IPadHandler
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DsFolderButton> _optionViews = [];
    private readonly PadOption[] _options;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly ScrollView? _scroll;
    private int _index;
    private int _columns = 1;

    /// <summary>Shows the menu inside <paramref name="host"/> (a page's root grid) and returns the chosen option or null.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params string[] options) =>
        new PadMenu(host, title, message, options.Select(o => new PadOption(o)).ToArray())._result.Task;

    /// <summary>Rich overload: options with glyph badges, accent colors, or image icons.</summary>
    public static Task<string?> ShowAsync(Grid host, string title, string? message, params PadOption[] options) =>
        new PadMenu(host, title, message, options)._result.Task;

    /// <summary>Two-option confirm box; true when the user picks <paramref name="confirmLabel"/>.</summary>
    public static async Task<bool> ConfirmAsync(Grid host, string title, string message, string confirmLabel = "OK")
    {
        var choice = await ShowAsync(host, title, message, confirmLabel, "Cancel");
        return choice == confirmLabel;
    }

    private PadMenu(Grid host, string title, string? message, PadOption[] options)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _host = host;
        _options = options;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        // Menus columnize by what the labels need: the longest label decides how many
        // columns fit without marqueeing (~16 chars per 200dp column at 15px).
        var longest = options.Length == 0 ? 0 : options.Max(o => o.Label.Length);
        _columns = options.Length switch
        {
            <= 4 => 1,
            <= 12 => longest > 24 ? 1 : 2,
            _ => longest > 16 ? 2 : 3,
        };
        var buttonHeight = _columns == 1 ? 58.0 : _columns == 2 ? 52.0 : 46.0;

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var c = 0; c < _columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var rowCount = (options.Length + _columns - 1) / _columns;
        for (var r = 0; r < rowCount; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < options.Length; i++)
        {
            var captured = options[i].Label;
            var button = new DsFolderButton(options[i], buttonHeight) { Tapped = () => Close(captured) };
            _optionViews.Add(button);
            grid.Add(button);
            Grid.SetRow(button, i / _columns);
            Grid.SetColumn(button, i % _columns);
        }

        var content = new VerticalStackLayout { Spacing = 10 };
        content.Children.Add(Kit.HeaderBar(title));
        if (!string.IsNullOrEmpty(message))
        {
            content.Children.Add(new Label
            {
                Text = message,
                TextColor = UiTokens.Ink1,
                FontSize = UiTokens.TextSmall,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
        content.Children.Add(grid);
        content.Children.Add(Kit.WindowHints(("A", "Choose", null), ("B", "Cancel", () => Close(null))));

        // PadMenu owns the ScrollView (same structure as OverlayWindow's default) so
        // Highlight can scroll the cursor into view; the window caps to the host.
        // A short menu sizes to its options; only a tall one scrolls (a ScrollView always
        // claimed the full host height and left a tall empty panel under two choices).
        var estimate = 60 + rowCount * (buttonHeight + 8) + 60 + (string.IsNullOrEmpty(message) ? 0 : 20 + message.Length / 60 * 18);
        var hostHeight = host.Height > 0 ? host.Height - 16 : 344;
        View list = estimate < hostHeight - 24 ? content : _scroll = new ScrollView { Content = content };
        // Fit-to-host: the window is capped to the Thor's actual screen (host.Height - 16),
        var window = Kit.OverlayWindow(host, list, preferredMaxWidth: 640, scroll: false);
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));
        Highlight(0);
        _router?.Push(this);
        PerfTrace.Log($"menu.open[{options.Length}]", watch);
        PerfTrace.UntilIdle($"menu.open[{options.Length}]", host.Dispatcher);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Highlight(_index - _columns); return true;
            case PadButton.Down: Highlight(_index + _columns); return true;
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
        // DS selection is a colour highlight (the indigo band + red pointer), never a zoom -
        // scaling clipped the folder against its container. The band IS the cursor.
        for (var i = 0; i < _optionViews.Count; i++)
            _optionViews[i].Selected = i == _index;
        // Tall menus scroll; the highlight must ride along or the cursor vanishes
        // below the fold.
        _scroll?.ScrollToAsync(_optionViews[_index], ScrollToPosition.MakeVisible, animated: false);
    }
    private void Close(string? result)
    {
        if (_router is not null) _router.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(result);
    }
}
