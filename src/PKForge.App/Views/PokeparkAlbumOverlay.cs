using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>The photo album over the park: one photo at a time with its caption and date; L and R
/// (or a tap on either side) turn the pages, X (or the Share button) sends the photo to another
/// app, Y (or the Delete button) deletes it after asking, B (or a tap in the middle) closes it. The
/// footer says where the photos are kept.</summary>
public sealed class PokeparkAlbumOverlay : IPadHandler
{
    private readonly Grid _host;
    private readonly SKCanvasView _view = new() { EnableTouchEvents = true };
    private readonly GamepadRouter? _router;
    private readonly TaskCompletionSource _done = new();
    private IReadOnlyList<ParkAlbumStore.Photo> _photos;
    private int _index;
    private SKBitmap? _shown;
    private int _shownIndex = -1;
    private bool _closed, _sharing, _deleting;
    private SKRect _shareButton, _deleteButton;

    public static Task ShowAsync(Grid host) => new PokeparkAlbumOverlay(host)._done.Task;

    private PokeparkAlbumOverlay(Grid host)
    {
        _host = host;
        _photos = PokeparkAlbum.List();
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view.BackgroundColor = Colors.Transparent;
        _view.PaintSurface += PaintGuard.Wrap("PokeparkAlbumOverlay.Paint", Paint);
        _view.Touch += Touch;
        host.Add(_view);
        Grid.SetRowSpan(_view, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_view, Math.Max(1, host.ColumnDefinitions.Count));
        App.Suspended += Suspended;
        _router?.Push(this);
    }

    // The share sheet sends the app to the background for a moment: the album stays open for it.
    private void Suspended() { if (!_sharing) Close(); }

    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        var (w, h) = (e.Info.Width, e.Info.Height);
        var unit = w / 1920f;
        c.Clear(new SKColor(10, 14, 24, 235));
        using var font = new SKFont(PixelFont.Face, 34 * unit);
        using var small = new SKFont(PixelFont.Face, 26 * unit);
        using var paint = new SKPaint { Color = EditorPaint.Value, IsAntialias = false };
        if (_photos.Count == 0)
        {
            _shareButton = _deleteButton = SKRect.Empty;
            c.DrawTextFallback("No photos yet. Press X in the park to take one.", w / 2f, h / 2f, SKTextAlign.Center, font, paint);
            paint.Color = EditorPaint.Label;
            c.DrawTextFallback(Fit(Where, small, w * .9f), w / 2f, h / 2f + 50 * unit, SKTextAlign.Center, small, paint);
            return;
        }
        if (_shownIndex != _index)
        {
            _shown?.Dispose();
            _shown = SKBitmap.Decode(_photos[_index].Path);
            _shownIndex = _index;
        }
        var photo = _photos[_index];
        if (_shown is { } bitmap)
        {
            var scale = Math.Min(w * .86f / bitmap.Width, h * .72f / bitmap.Height);
            var dest = SKRect.Create((w - bitmap.Width * scale) / 2, 40 * unit, bitmap.Width * scale, bitmap.Height * scale);
            using var frame = new SKPaint { Color = new SKColor(0xf4, 0xf0, 0xe4) };
            c.DrawRect(SKRect.Inflate(dest, 10 * unit, 10 * unit), frame);
            using var crisp = new SKPaint { IsAntialias = false };
            c.DrawBitmap(bitmap, dest, crisp);
            var y = dest.Bottom + 70 * unit;
            c.DrawTextFallback(Fit(photo.Caption, font, w * .9f), w / 2f, y, SKTextAlign.Center, font, paint);
            paint.Color = EditorPaint.Label;
            c.DrawTextFallback($"{photo.TakenAt:d MMM yyyy, HH:mm}   ·   {_index + 1} / {_photos.Count}   ·   L R  photos   X  share   Y  delete   B  close", w / 2f, y + 50 * unit, SKTextAlign.Center, small, paint);
            // The Share button, bottom right, for a finger.
            _shareButton = SKRect.Create(w - 280 * unit, h - 110 * unit, 240 * unit, 76 * unit);
            ParkCardPaint.Plate(c, _shareButton, true, unit);
            ParkCardPaint.Centered(c, _sharing ? "Sharing…" : "Share", _shareButton.MidX, _shareButton.MidY, font, EditorPaint.ChipInk);
            // Delete, beside Share, and where the photos are kept, bottom left.
            _deleteButton = SKRect.Create(_shareButton.Left - 280 * unit, _shareButton.Top, 240 * unit, 76 * unit);
            ParkCardPaint.Plate(c, _deleteButton, false, unit);
            ParkCardPaint.Centered(c, "Delete", _deleteButton.MidX, _deleteButton.MidY, font, EditorPaint.ChipInk);
            c.DrawTextFallback(Fit(Where, small, _deleteButton.Left - 80 * unit), 40 * unit, _shareButton.MidY + 9 * unit, SKTextAlign.Left, small, paint);
        }
    }

    private static string Where => $"Kept in PKForge's private folder {PokeparkAlbum.Folder}. Share sends a copy out.";

    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureFallback(text) <= width) return text;
        while (text.Length > 1 && font.MeasureFallback(text + "…") > width) text = text[..^1];
        return text + "…";
    }

    private void Touch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;
        if (e.ActionType != SKTouchAction.Released) return;
        var width = _view.CanvasSize.Width;
        if (_deleting) return;
        if (_shareButton.Contains(e.Location)) _ = ShareAsync();
        else if (_deleteButton.Contains(e.Location)) _ = DeleteAsync();
        else if (e.Location.X < width * .25f) Turn(-1);
        else if (e.Location.X > width * .75f) Turn(1);
        else Close();
    }

    public bool OnPadButton(PadButton button)
    {
        if (_deleting) return true;
        switch (button)
        {
            case PadButton.L or PadButton.Left: Turn(-1); break;
            case PadButton.R or PadButton.Right: Turn(1); break;
            case PadButton.X: _ = ShareAsync(); break;
            case PadButton.Y: _ = DeleteAsync(); break;
            case PadButton.B or PadButton.A: Close(); break;
        }
        return true;
    }

    // A copy with a readable name goes to Android's share sheet, its caption as the message.
    private async Task ShareAsync()
    {
        if (_sharing || _photos.Count == 0) return;
        var photo = _photos[_index];
        _sharing = true;
        _view.InvalidateSurface();
        try
        {
            var copy = Path.Combine(FileSystem.CacheDirectory, $"Poképark {photo.TakenAt:yyyy-MM-dd HH-mm-ss}.jpg");
            File.Copy(photo.Path, copy, overwrite: true);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = photo.Caption, File = new ShareFile(copy, "image/jpeg") });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("album", $"Sharing a photo: {error.Message}");
        }
        finally
        {
            _sharing = false;
            if (!_closed) _view.InvalidateSurface();
        }
    }

    // Asks first: a deleted photo is gone for good.
    private async Task DeleteAsync()
    {
        if (_deleting || _sharing || _photos.Count == 0) return;
        var photo = _photos[_index];
        _deleting = true;
        try
        {
            var caption = string.IsNullOrWhiteSpace(photo.Caption) ? "" : $" \u201c{photo.Caption}\u201d";
            if (!await PadMenu.ConfirmAsync(_host, "Delete this photo?",
                    $"The photo{caption} from {photo.TakenAt:d MMM yyyy, HH:mm} will be deleted from the album. This cannot be undone.", "Delete") || _closed) return;
            PokeparkAlbum.Delete(photo);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("album", $"Deleting a photo: {error.Message}");
        }
        finally
        {
            _deleting = false;
        }
        _photos = PokeparkAlbum.List();
        _index = Math.Min(_index, Math.Max(0, _photos.Count - 1));
        _shownIndex = -1;
        if (!_closed) _view.InvalidateSurface();
    }

    private void Turn(int delta)
    {
        if (_photos.Count == 0) return;
        _index = (_index + delta + _photos.Count) % _photos.Count;
        _view.InvalidateSurface();
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        App.Suspended -= Suspended;
        _router?.Remove(this);
        _host.Remove(_view);
        _shown?.Dispose();
        _done.TrySetResult();
    }
}
