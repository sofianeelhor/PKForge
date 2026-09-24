using System.Text;
using PKForge.App.Views;
using PKForge.Domain;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace PKForge.App.Services;

/// <summary>
/// PKF1 .pk QR transfer, the app's QR surface's service half. The envelope itself
/// is pure Domain logic (QrEntityCodec, unit-tested without MAUI); this side owns
/// the QR specifics - rendering envelopes as byte-mode codes through QrPopup, and
/// reading one back from a picked screenshot or photo, previewing the mon it
/// carries, and handing confirmed bytes to whatever import path the caller owns.
/// </summary>
/// <summary>A confirmed QR receive: the raw .pk bytes plus the bank facts to file them under.</summary>
public sealed record QrReceivedEntity(byte[] Data, BankEntryInfo Info);

public static class QrEntityService
{
    private const int DecodeCap = 1600;

    public static byte[] MakePayload(byte[] pkBytes, int generation, string speciesName) =>
        QrEntityCodec.MakePayload(pkBytes, generation, speciesName);

    public static QrEntityPayload? TryParse(byte[] payload) =>
        payload is null ? null : QrEntityCodec.TryParse(payload);

    /// <summary>
    /// Picks an image, reads one .pk QR out of it, and previews the mon (species,
    /// level, OT, generation) for confirmation. Returns the received entity on
    /// Receive; null when the user backs out at any step. Every dead end explains
    /// itself on screen, so callers only handle success.
    /// </summary>
    public static async Task<QrReceivedEntity?> ScanAsync(Grid host, ISaveEngine engine, string? contextNote = null)
    {
        var pick = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Pick a screenshot or photo of the QR code",
            FileTypes = FilePickerFileType.Images,
        });
        if (pick is null) return null;

        byte[] image;
        try
        {
            image = await File.ReadAllBytesAsync(pick.FullPath);
        }
        catch (Exception)
        {
            await PadMenu.ShowAsync(host, "QR", "That image could not be read.", "OK");
            return null;
        }

        var payload = await Task.Run(() => TryDecodeQr(image));
        if (payload is null)
        {
            await PadMenu.ShowAsync(host, "QR", "No QR code found in that image.", "OK");
            return null;
        }

        var parsed = QrEntityCodec.TryParse(payload);
        if (parsed is null)
        {
            await PadMenu.ShowAsync(host, "QR", "That QR code is not a PKForge .pk transfer.", "OK");
            return null;
        }

        // The PKF1 envelope carries the generation, not the format: it settles PK6 vs PK7
        // (same size), while Gen 8/9 siblings still fall back to PKHeX's heuristics.
        var format = parsed.Generation == 6 ? "PK6" : null;
        var info = engine.TryDescribeEntity(parsed.EntityBytes, "QR transfer", format);
        if (info is null)
        {
            await PadMenu.ShowAsync(host, "QR", "The QR code's Pokémon data could not be read.", "OK");
            return null;
        }

        var name = info.Nickname.Length > 0 ? info.Nickname : parsed.SpeciesName;
        var message =
            $"{name} · {parsed.SpeciesName}\nLv. {info.Level} · OT {OriginalTrainerOf(engine, parsed.EntityBytes, info.Format)} · Gen {parsed.Generation}";
        if (contextNote is not null) message += $"\n\n{contextNote}";
        var confirmed = await PadMenu.ConfirmAsync(host, "POKéMON FOUND", message, "Receive");
        return confirmed ? new QrReceivedEntity(parsed.EntityBytes, info) : null;
    }

    /// <summary>The OT for the preview line; a throwaway entity session is the engine's own display truth.</summary>
    private static string OriginalTrainerOf(ISaveEngine engine, byte[] entityBytes, string? format)
    {
        var session = engine.OpenEntitySession(entityBytes, format: format);
        if (session is null) return "—";
        using (session)
        {
            var ot = session.ReadEntity(0, 0).OriginalTrainer;
            return string.IsNullOrWhiteSpace(ot) ? "—" : ot;
        }
    }

    /// <summary>Reads one QR's raw bytes out of an image; null when it holds no QR.</summary>
    private static byte[]? TryDecodeQr(byte[] imageBytes)
    {
        try
        {
            using var decoded = SKBitmap.Decode(imageBytes);
            if (decoded is null) return null;
            using var scaled = ScaledForDecode(decoded);
            var rgba = TightRgba(scaled);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                    // Byte-mode payloads ride on ISO-8859-1, so every byte 0x00-0xFF
                    // maps to exactly one char and back - the reverse of QrPopup's
                    // binary encoding.
                    CharacterSet = "ISO-8859-1",
                },
            };
            var result = reader.Decode(new RGBLuminanceSource(
                rgba, scaled.Width, scaled.Height, RGBLuminanceSource.BitmapFormat.RGBA32));
            return result is null ? null : Encoding.Latin1.GetBytes(result.Text);
        }
        catch (Exception)
        {
            return null; // undecodable pixels and broken images both mean "no QR"
        }
    }

    /// <summary>Shrinks oversized photos; returns the source itself when already small.</summary>
    private static SKBitmap ScaledForDecode(SKBitmap source)
    {
        var widest = Math.Max(source.Width, source.Height);
        if (widest <= DecodeCap) return source;
        var scale = (float)DecodeCap / widest;
        var scaled = new SKBitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Opaque);
        scaled.ScalePixels(source, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return scaled;
    }

    /// <summary>Tight RGBA rows for RGBLuminanceSource, redrawing odd layouts onto that format.</summary>
    private static byte[] TightRgba(SKBitmap source)
    {
        var info = source.Info;
        if (info.ColorType == SKColorType.Rgba8888 && info.RowBytes == info.Width * 4)
            return source.GetPixelSpan().ToArray();
        var rgba = new byte[info.Width * info.Height * 4];
        using var surface = SKSurface.Create(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(source, 0, 0);
        using var pixels = surface.PeekPixels();
        pixels!.GetPixelSpan().CopyTo(rgba);
        return rgba;
    }
}
