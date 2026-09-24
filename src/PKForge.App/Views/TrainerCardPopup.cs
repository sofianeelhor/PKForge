using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The trainer card window: name, IDs, money, gender - view and edit. Read-only (Hardcore
/// mode) shows the same card with locked fields, a HARDCORE marker and only CLOSE.
/// </summary>
public static class TrainerCardPopup
{
    /// <summary>Shows the card; returns the edited trainer, or null when cancelled or read-only.</summary>
    public static Task<TrainerInfo?> ShowAsync(Grid host, TrainerInfo current, bool readOnly = false)
    {
        var result = new TaskCompletionSource<TrainerInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Entry MakeEntry(string text, Keyboard? keyboard = null)
        {
            var entry = Kit.TextField(keyboard: keyboard);
            entry.Text = text;
            entry.FontSize = UiTokens.TextTitle;
            entry.IsReadOnly = readOnly;
            return entry;
        }

        var name = MakeEntry(current.Name);
        var tid = MakeEntry(current.TID.ToString(), Keyboard.Numeric);
        var sid = MakeEntry(current.SID.ToString(), Keyboard.Numeric);
        var money = MakeEntry(current.Money.ToString(), Keyboard.Numeric);
        var genderIsFemale = new Switch { OnColor = UiTokens.AccentInfo, ThumbColor = UiTokens.Ink0, IsToggled = current.Gender == 1, IsEnabled = !readOnly };

        View Row(string caption, View value) => Kit.FormRow(caption, value);

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(TrainerInfo? trainer)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(trainer);
        }

        void Save()
        {
            if (readOnly) { Close(null); return; }
            if (!int.TryParse(tid.Text?.Trim(), out var tidValue)) return;
            if (!int.TryParse(sid.Text?.Trim(), out var sidValue)) return;
            if (!uint.TryParse(money.Text?.Trim(), out var moneyValue)) return;
            var trainerName = (name.Text ?? "").Trim();
            if (trainerName.Length == 0) return;
            Close(new TrainerInfo(trainerName, tidValue, sidValue, moneyValue, genderIsFemale.IsToggled ? 1 : 0));
        }

        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        var cancel = Kit.Capsule(readOnly ? "Close" : "Cancel", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);
        buttons.Children.Add(cancel);
        if (!readOnly)
        {
            var save = Kit.Capsule("Save", UiTokens.Green);
            save.Clicked += (_, _) => Save();
            buttons.Children.Add(save);
        }

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar(readOnly ? $"Trainer card · {HardcoreMode.Marker}" : "Trainer card"),
                Row("Name", name),
                Row("TID", tid),
                Row("SID", sid),
                Row("Money", money),
                Row("Female", genderIsFemale),
            },
        };
        if (readOnly)
            content.Children.Add(new Label
            {
                Text = HardcoreMode.StatusFor(SaveAction.EditTrainer),
                FontSize = UiTokens.TextSmall, FontFamily = DsChrome.PixelFont, TextColor = UiTokens.TextTone(UiTokens.GiftRed),
            });
        content.Children.Add(buttons);

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Save);
        return result.Task;
    }
}
