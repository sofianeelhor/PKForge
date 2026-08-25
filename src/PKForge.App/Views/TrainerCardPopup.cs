using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>The trainer card window: name, IDs, money, gender - view and edit.</summary>
public static class TrainerCardPopup
{
    public static Task<TrainerInfo?> ShowAsync(Grid host, TrainerInfo current)
    {
        var result = new TaskCompletionSource<TrainerInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Entry MakeEntry(string text, Keyboard? keyboard = null) => new()
        {
            Text = text,
            Keyboard = keyboard ?? Keyboard.Default,
            FontSize = 16,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Paper,
            BackgroundColor = UiTokens.ShellPress,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };

        var name = MakeEntry(current.Name);
        var tid = MakeEntry(current.TID.ToString(), Keyboard.Numeric);
        var sid = MakeEntry(current.SID.ToString(), Keyboard.Numeric);
        var money = MakeEntry(current.Money.ToString(), Keyboard.Numeric);
        var genderIsFemale = new Switch { OnColor = UiTokens.GiftRed, IsToggled = current.Gender == 1 };

        View Row(string caption, View value)
        {
            var grid = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(new GridLength(90)), new(GridLength.Star)],
                Children =
                {
                    new Label { Text = caption, FontSize = 11, FontFamily = DsChrome.PixelFont, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#9AA5B0"), VerticalTextAlignment = TextAlignment.Center },
                    value,
                },
            };
            Grid.SetColumn(value, 1);
            return grid;
        }

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
            if (!int.TryParse(tid.Text?.Trim(), out var tidValue)) return;
            if (!int.TryParse(sid.Text?.Trim(), out var sidValue)) return;
            if (!uint.TryParse(money.Text?.Trim(), out var moneyValue)) return;
            var trainerName = (name.Text ?? "").Trim();
            if (trainerName.Length == 0) return;
            Close(new TrainerInfo(trainerName, tidValue, sidValue, moneyValue, genderIsFemale.IsToggled ? 1 : 0));
        }

        var save = Kit.Capsule("SAVE", UiTokens.Green);
        save.Clicked += (_, _) => Save();
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar("TRAINER CARD"),
                Row("NAME", name),
                Row("TID", tid),
                Row("SID", sid),
                Row("MONEY", money),
                Row("FEMALE", genderIsFemale),
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, save } },
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Save);
        return result.Task;
    }
}
