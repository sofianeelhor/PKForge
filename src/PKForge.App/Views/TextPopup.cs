using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>Multiline text input window (Showdown paste and friends).</summary>
public static class TextPopup
{
    /// <summary>Single-line input (profile names and other short values).</summary>
    public static Task<string?> ShowLineAsync(Grid host, string title, string hint, string current = "")
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry
        {
            Text = current,
            FontSize = 16,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.ShellPress,
            Placeholder = hint,
            PlaceholderColor = UiTokens.Ink1,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            ReturnType = ReturnType.Done,
        };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(string? text)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(text);
        }
        void Save() => Close(entry.Text?.Trim());

        var save = Kit.Capsule("SAVE", UiTokens.Green);
        save.Clicked += (_, _) => Save();
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);
        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                entry,
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, save } },
            },
        };
        overlay = Kit.AttachOverlay(host, Kit.OverlayWindow(host, content, preferredMaxWidth: 420), () => Close(null));
        pad = new PadOverlay(() => Close(null), Save);
        return result.Task;
    }

    public static Task<string?> ShowAsync(Grid host, string title, string hint)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var editor = new Editor
        {
            HeightRequest = 120,
            FontSize = 16,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.ShellPress,
            Placeholder = "Pikachu @ Light Ball\nAbility: Static\nLevel: 50\nShiny: Yes\n- Thunderbolt\n…",
            PlaceholderColor = UiTokens.Ink1,
        };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(string? text)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(text);
        }

        var ok = Kit.Capsule("USE THIS SET", UiTokens.Green);
        ok.Clicked += (_, _) => Close(editor.Text);
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                new Label { Text = hint, TextColor = Color.FromArgb("#9AA5B0"), FontFamily = DsChrome.PixelFont, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap },
                editor,
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, ok } },
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 520);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(cancel: () => Close(null), confirm: () => Close(editor.Text));
        return result.Task;
    }
}
