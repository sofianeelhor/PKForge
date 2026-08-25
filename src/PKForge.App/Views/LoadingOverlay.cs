using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>
/// The cute waiting screen: a Pokémon strolls while progress ticks up.
/// Non-blocking cancel; closes itself when the caller reports completion.
/// </summary>
public sealed class LoadingOverlay
{
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly Label _progress;
    private readonly ProgressBar _bar;
    private PadOverlay? _pad;

    public CancellationTokenSource Cancellation { get; } = new();

    public static LoadingOverlay Show(Grid host, string title, string subtitle) => new(host, title, subtitle);

    private LoadingOverlay(Grid host, string title, string subtitle)
    {
        _host = host;
        _progress = new Label
        {
            TextColor = UiTokens.Paper,
            FontFamily = DsChrome.PixelFont,
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            Text = "Starting…",
        };
        _bar = new ProgressBar { ProgressColor = UiTokens.Green };

        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.HorizontalOptions = LayoutOptions.Center;
        cancel.Clicked += (_, _) => Cancellation.Cancel();

        var content = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Kit.HeaderBar(title),
                new Label { Text = subtitle, TextColor = UiTokens.InkSoft, FontSize = 12, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap },
                Kit.WalkerStrip(34),
                _bar,
                _progress,
                cancel,
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 440, padding: 18);
        _overlay = Kit.AttachOverlay(host, window, () => Cancellation.Cancel());
        _pad = new PadOverlay(() => Cancellation.Cancel(), () => Cancellation.Cancel());
    }

    public void Report(int done, int total) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _progress.Text = $"{done} / {total}";
        _bar.Progress = total == 0 ? 0 : (double)done / total;
    });

    public void Close() => MainThread.BeginInvokeOnMainThread(() =>
    {
        _host.Remove(_overlay);
        _pad?.Dispose();
    });
}
