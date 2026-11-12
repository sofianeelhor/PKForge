using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The type plate of the art direction, drawn the same everywhere: a darker rim, the type
/// colour inside, the name in outlined capitals. Also draws the move category plates.
/// </summary>
internal static class TypePlates
{
    private static readonly SKColor Outline = new(0x28, 0x22, 0x34);

    /// <summary>A type's plate; nothing for an unknown type.</summary>
    public static void Paint(SKCanvas c, SKRect r, int type)
    {
        if (!TypeFacts.IsValid(type) && type != TypeFacts.Stellar) return;
        var color = InfoKit.TypeColor(type).ToSKColor();
        Paint(c, r, SummaryChrome.Lighter(color, 0.12f), SummaryChrome.Darker(color, 0.35f), TypeFacts.Name(type).ToUpperInvariant());
    }

    /// <summary>A plate in any colours (the move categories share one blue).</summary>
    public static void Paint(SKCanvas c, SKRect r, SKColor light, SKColor dark, string label)
    {
        var size = r.Height * 0.63f;
        var radius = Math.Max(1, r.Height * 0.06f);
        using (var rim = new SKPaint { Color = dark, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, rim);
        var inset = r.Height * 0.1f;
        using (var fill = new SKPaint { Color = light, IsAntialias = true }) c.DrawRoundRect(SKRect.Inflate(r, -inset, -inset), radius * 0.7f, radius * 0.7f, fill);
        var text = SummaryInk.Fit(label, size, r.Width - inset * 2.4f);
        var baseline = SummaryInk.Center(r.MidY, size);
        var outline = size * 0.08f;
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1) })
            SummaryInk.Draw(c, text, r.MidX + dx * outline, baseline + dy * outline, size, Outline, align: SKTextAlign.Center);
        SummaryInk.Draw(c, text, r.MidX, baseline, size, Pksm.Ink, align: SKTextAlign.Center);
    }

    /// <summary>A view that paints one type's plate; <see cref="Type"/> re-points it.</summary>
    public sealed class View : SKCanvasView
    {
        private int? _type;

        public View()
        {
            InputTransparent = true;
            PaintSurface += PaintGuard.Wrap("TypePlates this", (_, args) =>
            {
                var c = args.Surface.Canvas;
                c.Clear(SKColors.Transparent);
                if (_type is { } type) Paint(c, new SKRect(0, 0, args.Info.Width, args.Info.Height), type);
            });
        }

        public int? Type
        {
            get => _type;
            set
            {
                if (_type == value) return;
                _type = value;
                InvalidateSurface();
            }
        }
    }
}
