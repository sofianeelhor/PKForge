using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

/// <summary>
/// A short notice for a screen that has no readout: one line in a small flat plate, dropped
/// into the status strip at the top of the screen. That band only holds the screen's name and
/// the battery, so the notice never covers the cursor, the grid, a menu's rows or the footer
/// hints, and the eye finds it where a handheld puts its system messages. Green for a write that
/// happened, red for a refusal (which stays longer), the house blue for the rest. A newer
/// notice replaces the one on screen in place: a burst never stacks. It never takes input.
/// </summary>
internal sealed class StatusToast : Border
{
    private readonly Label _text;
    private readonly BoxView _lamp;
    private int _showing;

    /// <param name="top">Where the plate sits from the top of its host, in dp: by default just
    /// under the 28 dp title bar, inside the status strip.</param>
    public StatusToast(double top = 31)
    {
        _lamp = new BoxView { WidthRequest = 8, HeightRequest = 8, CornerRadius = 1, VerticalOptions = LayoutOptions.Center };
        _text = new Label
        {
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextBody,
            TextColor = EditorPaint.ChipInk.ToMauiColor(),
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 8,
            Children = { _lamp, _text },
        };
        Grid.SetColumn(_text, 1);

        Content = row;
        Padding = new Thickness(10, 3, 12, 3);
        StrokeThickness = 1.5;
        StrokeShape = new RoundRectangle { CornerRadius = 3 };
        BackgroundColor = EditorPaint.ChipBottom.ToMauiColor();
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Start;
        Margin = new Thickness(16, top, 16, 0);
        MaximumWidthRequest = 480;
        InputTransparent = true;
        ZIndex = 40;
        Opacity = 0;
        IsVisible = false;
    }

    /// <summary>Shows <paramref name="status"/> unless its tone is quiet; a newer notice replaces the one on screen.</summary>
    public void Show(string? status) => Show(status, StatusNotice.ToneOf(status));

    /// <summary>Shows <paramref name="status"/> in a tone the caller already knows (a write it reported).</summary>
    public void Show(string? status, NoticeTone tone)
    {
        if (tone == NoticeTone.Quiet) return;
        var text = Kit.Tidy(status);
        var accent = tone switch
        {
            NoticeTone.Success => UiTokens.Green,
            NoticeTone.Problem => UiTokens.Bad,
            _ => EditorPaint.Cyan.ToMauiColor(),
        };
        _text.Text = text;
        _lamp.Color = accent;
        Stroke = accent;
        if (Parent is Grid host) RaiseIn(host);
        _ = RunAsync(++_showing, StatusNotice.HoldMs(text, tone));
    }

    /// <summary>
    /// Puts the host's toast back on top of every overlay. ZIndex alone did not hold on Android
    /// once a menu was attached after the toast (QA 2026-11-12: "Second screen off…" drew under
    /// the dimmed Look and feel dialog), so the toast is moved to the end of the host's children.
    /// </summary>
    public static void RaiseIn(Grid host)
    {
        var toasts = host.Children.OfType<StatusToast>().ToList();
        foreach (var toast in toasts)
        {
            if (ReferenceEquals(host.Children[^1], toast)) continue;
            host.Children.Remove(toast);
            host.Children.Add(toast);
        }
    }

    private async Task RunAsync(int turn, int holdMs)
    {
        this.CancelAnimations();
        // Full: it drops in as it fades in. Reduced: it only fades. Off: it is simply there, then gone.
        // A notice that replaces one still on screen swaps its words in place, without a second entrance.
        var motion = Services.MotionSetting.Level;
        if (!IsVisible || Opacity < 0.5)
        {
            IsVisible = true;
            TranslationY = motion == Services.MotionLevel.Full ? -8 : 0;
            if (motion == Services.MotionLevel.Off) Opacity = 1;
            else await Task.WhenAll(this.FadeToAsync(1, 120, Easing.CubicOut), this.TranslateToAsync(0, 0, 120, Easing.CubicOut));
        }
        else
        {
            Opacity = 1;
            TranslationY = 0;
        }
        await Task.Delay(holdMs);
        if (turn != _showing) return;
        if (motion == Services.MotionLevel.Off) Opacity = 0;
        else await this.FadeToAsync(0, 200, Easing.CubicIn);
        if (turn == _showing) IsVisible = false;
    }
}
