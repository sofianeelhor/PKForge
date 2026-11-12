using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The rows of the designer's panels (the Pokémon editor, the creation wizard): bands,
/// captions, values, section chips, text fields and tool buttons, sized in mockup pixels.
/// </summary>
internal static class EditorRows
{
    internal static Color EditorLabel => UiTokens.EditorLabel;
    internal static Color EditorValue => UiTokens.EditorValue;
    internal static Color ToolFill => UiTokens.ToolFill;
    internal static Color ToolEdge => UiTokens.ToolEdge;

    /// <summary>
    /// A length in mockup pixels (the 1920×1080 design) as device-independent units on this
    /// display: exact on the Thor's 1920×1080 screen, scaled to fit the screen elsewhere.
    /// </summary>
    internal static double Design(double pixels)
    {
        var display = DeviceDisplay.MainDisplayInfo;
        var longSide = Math.Max(display.Width, display.Height);
        var shortSide = Math.Min(display.Width, display.Height);
        if (longSide <= 0 || display.Density <= 0) return pixels / 2;
        // The tighter of width and height: a 20:9 phone keeps the mockup's proportions
        // instead of growing past its height (exactly 1 on the Thor's 16:9).
        var fit = Math.Min(longSide / 1920, shortSide / 1080);
        return pixels * fit / display.Density;
    }

    /// <summary>A row's height and text size (the mockup's 64 and 32 pixels).</summary>
    internal static readonly double EditorRowHeight = Design(64);
    internal static readonly double EditorText = Design(32);

    internal static Color RowBand(bool dark) => EditorPaint.RowBand(dark).ToMauiColor();

    /// <summary>The value text of every editor row.</summary>
    internal static Label EditorValueLabel()
    {
        var label = Kit.BlueprintValue(EditorText);
        label.TextColor = EditorValue;
        return label;
    }

    /// <summary>The "›" at the end of a row that opens something.</summary>
    internal static Label EditorChevron() => new()
    {
        Text = "›",
        FontFamily = DsChrome.PixelFont,
        FontSize = EditorText,
        TextColor = EditorValue,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>The row caption: pixel font, the designer's label blue.</summary>
    internal static Label EditorCaption(string caption, double? width = null) => new()
    {
        Text = Kit.Tidy(caption),
        FontFamily = DsChrome.PixelFont,
        FontSize = EditorText,
        TextColor = EditorLabel,
        WidthRequest = width ?? Design(214),
        VerticalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.NoWrap,
    };

    /// <summary>A full-width band: square, flush with the panel edges, the focus rim painted on it.</summary>
    internal static Border EditorBand(View content, bool dark) => new()
    {
        BackgroundColor = RowBand(dark),
        Stroke = Colors.Transparent,
        StrokeThickness = Design(3),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.Rectangle(),
        Padding = new Thickness(Design(34), 0, Design(28), 0),
        MinimumHeightRequest = EditorRowHeight,
        Content = content,
    };

    /// <summary>A section divider: the title in a slanted chip between fading rails.</summary>
    internal static View EditorSection(string title)
    {
        var view = new SKCanvasView { HeightRequest = EditorRowHeight, InputTransparent = true };
        view.PaintSurface += PaintGuard.Wrap("EditorRows view", (_, args) =>
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var unit = Math.Min(args.Info.Width / EditorPaint.DesignWidth, args.Info.Height / 64f);
            EditorPaint.PaintSection(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), title, BoxBrowserPage.PixelTypeface(), unit);
        });
        return view;
    }

    /// <summary>
    /// The editor's text field. Android pads its text fields for touch; this one keeps only a
    /// little bottom padding, which holds the underline below the text, so a field row is as
    /// tall as the other rows.
    /// </summary>
    internal sealed class EditorEntry : Entry
    {
        static EditorEntry()
        {
#if ANDROID
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(nameof(EditorEntry), (handler, view) =>
            {
                if (view is EditorEntry)
                {
                    var bottom = (int)(Design(10) * DeviceDisplay.MainDisplayInfo.Density);
                    handler.PlatformView.SetPadding(handler.PlatformView.PaddingLeft, 0, handler.PlatformView.PaddingRight, bottom);
                }
            });
#endif
        }
    }

    /// <summary>The focused row or button: the section chip's gradient.</summary>
    internal static LinearGradientBrush EditorFocusBrush => new(
        [new GradientStop(EditorPaint.ChipTop.ToMauiColor(), 0), new GradientStop(EditorPaint.ChipBottom.ToMauiColor(), 1)],
        new Point(0, 0), new Point(0, 1));

    internal static Color FocusedCaption => UiTokens.EditorChipInk;

    /// <summary>Turns a row's captions pale while it is focused (they are the label blue at rest).</summary>
    internal static void SetFocusedCaptions(Border row, bool focused)
    {
        foreach (var label in row.GetVisualTreeDescendants().OfType<Label>())
        {
            if (focused && label.TextColor == EditorLabel) label.TextColor = FocusedCaption;
            else if (!focused && label.TextColor == FocusedCaption) label.TextColor = EditorLabel;
        }
    }

    /// <summary>A tool button: flat navy-blue plate, pale edge, pixel label, no icon.</summary>
    internal static Button EditorTool(string text, bool primary = false) => new()
    {
        Text = text,
        FontFamily = DsChrome.PixelFont,
        FontSize = EditorText * 0.94,
        TextColor = primary ? EditorPaint.ChipInk.ToMauiColor() : UiTokens.Ink0,
        BackgroundColor = primary ? EditorPaint.CyanFill.ToMauiColor() : ToolFill,
        BorderColor = primary ? EditorPaint.Cyan.ToMauiColor() : ToolEdge,
        BorderWidth = 1.5,
        CornerRadius = (int)Math.Round(Design(20)),
        Padding = new Thickness(Design(16), 0),
        HeightRequest = Design(72),
        LineBreakMode = LineBreakMode.TailTruncation,
    };
}
