using System.Text;
using PKForge.App.Theme;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace PKForge.App.Views;

/// <summary>Renders text or binary payloads as QR codes in a themed window (Showdown sets, .pk transfers).</summary>
public static class QrPopup
{
    public static Task ShowAsync(Grid host, string title, string payload)
    {
        var matrix = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 512, 512);
        return ShowAsync(host, title, matrix);
    }

    /// <summary>
    /// Renders raw binary (a PKF1 .pk transfer envelope) as a byte-mode QR. The bytes
    /// map one-to-one onto ISO-8859-1 code points - exactly the mapping ZXing-based
    /// scanners reverse back into the same bytes - and level M keeps even the densest
    /// payload (a 344-byte Legends: Arceus entity plus envelope) comfortably inside
    /// one code.
    /// </summary>
    public static Task ShowBinaryAsync(Grid host, string title, byte[] payload)
    {
        var latin1 = Encoding.Latin1.GetString(payload);
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET] = "ISO-8859-1",
        };
        var matrix = new QRCodeWriter().encode(latin1, BarcodeFormat.QR_CODE, 512, 512, hints);
        return ShowAsync(host, title, matrix);
    }

    private static Task ShowAsync(Grid host, string title, BitMatrix matrix)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
