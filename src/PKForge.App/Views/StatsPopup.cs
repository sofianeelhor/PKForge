using PKForge.App.Services;
using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>
/// Manual-edit window for the stat spreads (IVs/EVs): the expert path behind an
/// explicit EDIT button. Six labeled numeric entries, OK/Cancel.
/// </summary>
public static class StatsPopup
{
    /// <summary>Single numeric value popup (item counts and friends). Null on cancel.</summary>
    public static Task<int?> ShowSingleAsync(Grid host, string title, int current, int max)
    {
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry
        {
            Text = current.ToString(),
            Keyboard = Keyboard.Numeric,
            FontSize = 16,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.ShellPress,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(int? value)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(value);
        }

        void Apply()
        {
            if (int.TryParse(entry.Text?.Trim(), out var value))
                Close(Math.Clamp(value, 0, max));
        }

        var ok = Kit.Capsule("APPLY", UiTokens.Green);
        ok.Clicked += (_, _) => Apply();
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                new Label { Text = $"0 - {max} (0 removes)", TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = 12 },
                entry,
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, ok } },
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 360);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Apply);
        return result.Task;
    }

    private static readonly string[] StatNames = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];

    public static Task<int[]?> ShowAsync(Grid host, string title, IReadOnlyList<int> current, int max)
    {
        var result = new TaskCompletionSource<int[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = new Entry[6];

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < 6; i++)
        {
            var caption = new Label { Text = StatNames[i], TextColor = UiTokens.Indigo, FontFamily = DsChrome.PixelFont, FontSize = 11, FontAttributes = FontAttributes.Bold };
            entries[i] = new Entry
            {
                Text = i < current.Count ? current[i].ToString() : "0",
                Keyboard = Keyboard.Numeric,
                FontSize = 16,
                FontFamily = DsChrome.PixelFont,
                TextColor = UiTokens.Ink0,
                BackgroundColor = UiTokens.ShellPress,
            };
            var row = (i / 3) * 2;
            var col = i % 3;
            grid.Add(caption); Grid.SetRow(caption, row); Grid.SetColumn(caption, col);
            grid.Add(entries[i]); Grid.SetRow(entries[i], row + 1); Grid.SetColumn(entries[i], col);
        }

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(int[]? values)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(values);
        }

        void Apply()
        {
            var values = new int[6];
            for (var i = 0; i < 6; i++)
            {
                if (!int.TryParse(entries[i].Text?.Trim(), out var value)) return;
                values[i] = Math.Clamp(value, 0, max);
            }
            Close(values);
        }

        var ok = Kit.Capsule("APPLY", UiTokens.Green);
        ok.Clicked += (_, _) => Apply();
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, ok } };
        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                new Label { Text = $"0 - {max} per stat", TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = 12 },
                grid,
                buttons,
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Apply);
        return result.Task;
    }
}
