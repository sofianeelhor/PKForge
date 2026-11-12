using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Poképark's drawing surface: through the GPU (SKGLView) by default, where scaling the
/// park up to the screen costs next to nothing, or through the CPU (SKCanvasView) for a device
/// whose GPU path misbehaves (Options, Drawing). The page sees one surface either way.
/// </summary>
public sealed class ParkCanvas
{
    public const string RendererKey = "pkforge.pokepark.renderer.v1";

    private readonly SKGLView? _gpu;
    private readonly SKCanvasView? _cpu;

    /// <summary>Draws a frame: the canvas, its width and height in pixels.</summary>
    public event Action<SKCanvas, int, int>? Paint;
    public event EventHandler<SKTouchEventArgs>? Touch;

    public static bool UsesGpu => Preferences.Default.Get(RendererKey, "gpu") != "cpu";

    public ParkCanvas()
    {
        if (UsesGpu)
        {
            _gpu = new SKGLView { EnableTouchEvents = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            _gpu.PaintSurface += PaintGuard.WrapGl("The park", (_, e) => Paint?.Invoke(e.Surface.Canvas, e.BackendRenderTarget.Width, e.BackendRenderTarget.Height));
            _gpu.Touch += (sender, e) => Touch?.Invoke(sender, e);
        }
        else
        {
            _cpu = new SKCanvasView { EnableTouchEvents = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            _cpu.PaintSurface += PaintGuard.Wrap("The park", (_, e) => Paint?.Invoke(e.Surface.Canvas, e.Info.Width, e.Info.Height));
            _cpu.Touch += (sender, e) => Touch?.Invoke(sender, e);
        }
    }

    public View View => (View?)_gpu ?? _cpu!;

    public SKSize CanvasSize => _gpu?.CanvasSize ?? _cpu!.CanvasSize;

    public void InvalidateSurface()
    {
        if (_gpu is not null) _gpu.InvalidateSurface();
        else _cpu!.InvalidateSurface();
    }
}
