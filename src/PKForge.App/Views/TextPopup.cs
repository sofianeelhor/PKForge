using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>Multiline text input window (Showdown paste and friends).</summary>
public static class TextPopup
{
    /// <summary>Single-line input (profile names and other short values).</summary>
    public static Task<string?> ShowLineAsync(Grid host, string title, string hint, string current = "", string confirm = "Save", bool numeric = false,
        Action<string>? changed = null)
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
            Keyboard = numeric ? Keyboard.Numeric : Keyboard.Default,
        };

        // A live search follows every keystroke (the pickers filter behind the prompt).
        if (changed is not null) entry.TextChanged += (_, args) => changed(args.NewTextValue ?? "");

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(string? text)
        {
            if (result.Task.IsCompleted) return; // Enter reaches both the field and the pad
            // Put the keyboard away and let go of the field first: removed while focused,
            // it left the on-screen keyboard up over the screen behind.
            if (entry.IsSoftInputShowing()) _ = entry.HideSoftInputAsync(CancellationToken.None);
            entry.Unfocus();
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(text);
        }
        void Save() => Close(entry.Text?.Trim());

        var save = Kit.Capsule(confirm, UiTokens.Green);
        save.Clicked += (_, _) => Save();
        entry.Completed += (_, _) => Save();
        var cancel = Kit.Capsule("Cancel", UiTokens.Ink1);
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
        // No scroll view: it claimed the whole height and left a tall empty panel under one field.
        overlay = Kit.AttachOverlay(host, PromptWindow(host, content), () => Close(null));
        pad = new PadOverlay(() => Close(null), Save);
        // Ready to type at once, the current value selected so typing replaces it: before,
        // the field had to be tapped first, and a pad player could not reach it at all.
        host.Dispatcher.Dispatch(() =>
        {
            entry.Focus();
            entry.CursorPosition = 0;
            entry.SelectionLength = entry.Text?.Length ?? 0;
        });
        return result.Task;
    }

    /// <summary>A one-field prompt: as wide as a prompt should be, as tall as its content.</summary>
    private static Border PromptWindow(Grid host, View content)
    {
        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420, scroll: false);
        window.WidthRequest = window.MaximumWidthRequest;
        return window;
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

        var ok = Kit.Capsule("Use this set", UiTokens.Green);
        ok.Clicked += (_, _) => Close(editor.Text);
        var cancel = Kit.Capsule("Cancel", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                new Label { Text = hint, TextColor = UiTokens.InkSoft, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.WordWrap },
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
