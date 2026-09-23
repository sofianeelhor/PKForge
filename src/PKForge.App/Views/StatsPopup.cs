using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

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

    /// <summary>
    /// What the six-value window can preview while typing: the stats before/after the
    /// edited spread, the shared EV total cap, and for IVs the Hidden Power type plus the
    /// IVs a chosen Hidden Power type needs. Every member is read-only and optional.
    /// </summary>
    public sealed record StatsPreview(
        Func<int[], (IReadOnlyList<int> Before, IReadOnlyList<int> After)?> Stats,
        int? TotalCap = null,
        Func<int[], int?>? HiddenPower = null,
        Func<int[], int, IReadOnlyList<int>?>? IvsForHiddenPower = null);

    public static Task<int[]?> ShowAsync(Grid host, string title, IReadOnlyList<int> current, int max, StatsPreview? preview = null)
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
            },
        };
        if (preview is not null)
            content.Children.Add(BuildPreview(host, entries, max, preview));
        content.Children.Add(buttons);

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Apply);
        return result.Task;
    }

    /// <summary>The live card under the entries; it re-reads the entries on every keystroke.</summary>
    private static View BuildPreview(Grid host, Entry[] entries, int max, StatsPreview preview)
    {
        var heading = InfoKit.Heading("RESULTING STATS");
        var total = new Label { FontFamily = DsChrome.PixelFont, FontSize = 11, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.End };
        var grid = new InfoKit.StatDeltaGrid();
        var overCap = InfoKit.Note(tone: InfoKit.NoteTone.Bad);
        var card = InfoKit.Card(InfoKit.HeaderRow(heading, total), grid, overCap);
        total.IsVisible = preview.TotalCap is not null;

        Border? hpBadge = null;
        if (preview.HiddenPower is not null)
        {
            hpBadge = InfoKit.TypeBadge();
            var change = Kit.Capsule("CHANGE", UiTokens.Blue);
            change.FontSize = 10;
            change.Padding = new Thickness(10, 2);
            change.HeightRequest = 30;
            change.Clicked += async (_, _) =>
            {
                var values = Read();
                if (preview.IvsForHiddenPower is null) return;
                var picked = await InfoPickers.ShowHiddenPowerAsync(host, preview.HiddenPower(values),
                    type => preview.IvsForHiddenPower(values, type));
                if (picked is null || preview.IvsForHiddenPower(values, picked.Id) is not { } ivs) return;
                for (var i = 0; i < 6; i++) entries[i].Text = Math.Clamp(ivs[i], 0, max).ToString();
            };
            var hpRow = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children =
                {
                    new Label { Text = "HIDDEN POWER", FontFamily = DsChrome.PixelFont, FontSize = 10, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                    hpBadge,
                    change,
                },
            };
            Grid.SetColumn(hpBadge, 1);
            Grid.SetColumn(change, 2);
            hpBadge.HorizontalOptions = LayoutOptions.Start;
            ((VerticalStackLayout)card.Content!).Children.Add(hpRow);
        }

        int[] Read() => entries.Select(e => int.TryParse(e.Text?.Trim(), out var v) ? Math.Clamp(v, 0, max) : 0).ToArray();

        void Refresh()
        {
            var values = Read();
            if (preview.Stats(values) is { } stats)
            {
                grid.IsVisible = true;
                grid.Show(stats.Before, stats.After);
            }
            else grid.IsVisible = false;
            if (preview.TotalCap is { } cap)
            {
                var over = TrainingBudget.IsOver(values, cap);
                total.Text = TrainingBudget.Label(values, cap);
                total.TextColor = over ? UiTokens.Bad : UiTokens.Ink1;
                overCap.Text = over ? $"Over the {cap} total cap by {TrainingBudget.Total(values) - cap}: this spread is illegal." : null;
                overCap.IsVisible = over;
            }
            if (hpBadge is not null) InfoKit.SetType(hpBadge, preview.HiddenPower!(values));
        }

        foreach (var entry in entries) entry.TextChanged += (_, _) => Refresh();
        Refresh();
        return card;
    }
}
