using PKForge.App.Theme;
using SkiaSharp;
using ZXing;
using ZXing.QrCode;

namespace PKForge.App.Views;

/// <summary>Renders text as a QR code in a themed window (Showdown sets, share codes).</summary>
public static class QrPopup
{
    public static Task ShowAsync(Grid host, string title, string payload)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = new QRCodeWriter();
        var matrix = writer.encode(payload, BarcodeFormat.QR_CODE, 512, 512);
        var path = System.IO.Path.Combine(FileSystem.CacheDirectory, "qr-latest.png");
        using (var bitmap = new SKBitmap(matrix.Width, matrix.Height))
        {
            for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                bitmap.SetPixel(x, y, matrix[x, y] ? SKColors.Black : SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            encoded.SaveTo(file);
        }

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            host.Remove(overlay);
            pad?.Dispose();
            done.TrySetResult();
        }

        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        close.HorizontalOptions = LayoutOptions.Center;
        close.Clicked += (_, _) => Close();

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar(title),
                Kit.LcdPanel(new Image
                {
                    Source = ImageSource.FromFile(path),
                    WidthRequest = 240,
                    HeightRequest = 240,
                    HorizontalOptions = LayoutOptions.Center,
                }, padding: 8),
                close,
            },
        };

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 300, padding: 16);
        overlay = Kit.AttachOverlay(host, window, Close);
        pad = new PadOverlay(Close, Close);
        return done.Task;
    }
}
