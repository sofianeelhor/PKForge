using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The sprite packs, painted by <see cref="SpritePackPaint"/>: each pack with a few of its own
/// sprites shown large, its size, what is on this device and an ACTIVE mark, and the download
/// drawn on its card. Up/Down choose a pack, A downloads, resumes or stops it, Y deletes it,
/// B leaves (pausing a download, which resumes where it left off).
/// Touch: a tap selects a pack, a second tap on it does what A does.
/// </summary>
public sealed class SpritePackScreen : IPadHandler
{
    private static readonly int[] MainSamples = [6, 25, 94, 448];
    private static readonly int[] IconSamples = [25, 6, 94, 448, 150];

    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly SpritePackDownloader _downloader;
    private readonly ISpriteService? _sprites;
    private readonly Action<string> _status;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, SKImage?> _fronts = [];
    private readonly Dictionary<int, SKImage?> _icons = [];
    private IReadOnlyList<SpritePackDownloader.PackStatus> _packs = [];
    private SpritePackHits? _hits;
    private int _cursor;
    private string? _note = "Reading what is on this device…";
    private string? _running;
    private double _progress;
    private string? _progressText;
    private CancellationTokenSource? _cancel;
    private bool _busyMenu;
    private bool _isClosed;

    /// <summary>Opens the screen over <paramref name="host"/>; with <paramref name="download"/> the main pack starts at once.</summary>
    public static Task ShowAsync(Grid host, SpritePackDownloader downloader, Action<string> status, bool download = false)
    {
        var screen = new SpritePackScreen(host, downloader, status);
        _ = screen.LoadAsync(download);
        return screen._closed.Task;
    }

    private SpritePackScreen(Grid host, SpritePackDownloader downloader, Action<string> status)
    {
        _host = host;
        _downloader = downloader;
        _status = status;
        _sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _canvas.PaintSurface += PaintGuard.Wrap("SpritePackScreen.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _overlay = Kit.AttachOverlay(host, _canvas);
        _router?.Push(this);
    }

    private async Task LoadAsync(bool download)
    {
        await RefreshAsync();
        if (download && _packs.Count > 0 && !_packs[0].Installed) Start(_packs[0].Name);
    }

    private async Task RefreshAsync()
    {
        try
        {
            _packs = await Task.Run(_downloader.Packs);
            _note = _packs.All(p => p.Installed) ? "Every pack is here: sprites work offline." : "Packs download once; finished parts are kept and it resumes.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _note = $"Could not read the packs. {Domain.ErrorText.Plain(error)}";
        }
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _packs.Count - 1));
        _canvas.InvalidateSurface();
    }

    private SKImage? Front(int species)
    {
        if (_fronts.TryGetValue(species, out var cached)) return cached;
        if (_sprites is null || !_sprites.TryGetShowdownFront(new SpriteLook(species, 0, false), Redraw, out var bitmap)) return null;
        return _fronts[species] = bitmap is null ? null : SKImage.FromBitmap(bitmap);
    }

    private SKImage? Icon(int species, bool bdsp)
    {
        if (_icons.TryGetValue(species, out var cached)) return cached;
        if (_sprites is null) return null;
        var look = new SpriteLook(species, 0, false);
        // The add-on's own icon once it is here; Showdown's box icon as a stand-in until then.
        if (bdsp)
        {
            if (!_sprites.TryGetBdspIcon(look, Redraw, out var icon)) return null;
            if (icon is not null) return _icons[species] = SKImage.FromBitmap(icon);
        }
        if (!_sprites.TryGetShowdownIcon(look, Redraw, out var sheet, out var cell)) return null;
        if (sheet is null) return _icons[species] = null;
        var bounds = StoragePaint.OpaqueBounds(sheet, cell);
        using var whole = SKImage.FromBitmap(sheet);
        return _icons[species] = Kit.SubsetOf(whole, bounds.IsEmpty ? cell : bounds);
    }

    private void Redraw() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private SpritePackCard Card(SpritePackDownloader.PackStatus pack)
    {
        var state = pack.Installed ? SpritePackState.Installed : pack.Started ? SpritePackState.Partial : SpritePackState.Missing;
        var stateText = pack.Installed ? "Downloaded" : pack.Started && pack.IsMain ? $"{pack.Missing} files missing" : "Not downloaded";
        IReadOnlyList<SKImage?> samples = pack.IsMain
            ? [.. MainSamples.Select(Front)]
            : [.. IconSamples.Select(s => Icon(s, pack.Installed))];
        var running = _running == pack.Name;
        return new SpritePackCard(pack.Name, pack.About, $"~{Math.Max(1, pack.Bytes >> 20)} MB", state, stateText, samples,
            running ? _progress : null, running ? _progressText ?? "Starting…" : null);
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        PksmPaint.LogoGrid(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height));
        var scene = new SpritePackScene([.. _packs.Select(Card)], _cursor, _note);
        _hits = SpritePackPaint.Paint(canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits || _busyMenu) return;
        var at = args.Location;
        if (hits.Leave.Contains(at)) { Close(); return; }
        if (hits.Primary.Contains(at)) { Primary(); return; }
        if (hits.Delete.Contains(at)) { _ = DeleteAsync(); return; }
        for (var i = 0; i < hits.Cards.Count; i++)
        {
            if (!hits.Cards[i].Contains(at)) continue;
            var index = hits.First + i;
            // Tap selects; a second tap on the selected pack does what A does.
            if (index == _cursor) Primary();
            else { _cursor = index; _canvas.InvalidateSurface(); }
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busyMenu) return true;
        switch (button)
        {
            case PadButton.Up: Move(-1); return true;
            case PadButton.Down: Move(1); return true;
            case PadButton.A: Primary(); return true;
            case PadButton.Y: _ = DeleteAsync(); return true;
            case PadButton.B: Close(); return true;
            default: return true; // the screen owns the pad while open
        }
    }

    private void Move(int delta)
    {
        if (_packs.Count == 0) return;
        _cursor = Math.Clamp(_cursor + delta, 0, _packs.Count - 1);
        _canvas.InvalidateSurface();
    }

    private void Primary()
    {
        if ((uint)_cursor >= (uint)_packs.Count) return;
        var pack = _packs[_cursor];
        if (_running == pack.Name) { _cancel?.Cancel(); return; }
        if (_running is not null)
        {
            _note = $"{_running} is downloading; one pack at a time.";
            _canvas.InvalidateSurface();
            return;
        }
        if (!pack.Installed) Start(pack.Name);
    }

    private void Start(string name) => _ = RunAsync(name);

    private async Task RunAsync(string name)
    {
        _running = name;
        _progress = 0;
        _progressText = "Starting…";
        _cancel = new CancellationTokenSource();
        _canvas.InvalidateSurface();
        try
        {
            await Task.Run(() => _downloader.RunPackAsync(name, (text, fraction) => MainThread.BeginInvokeOnMainThread(() =>
            {
                _progressText = text;
                _progress = Math.Clamp(fraction, 0, 1);
                if (!_isClosed) _canvas.InvalidateSurface();
            }), _cancel.Token));
            _status(name == _packs.FirstOrDefault()?.Name ? "Sprite pack complete - fully offline now." : $"{name} downloaded.");
        }
        catch (OperationCanceledException)
        {
            _status($"{name} paused - resume anytime from Settings.");
        }
        catch (Exception error)
        {
            _status($"{name} stopped. {Domain.ErrorText.Plain(error)}");
            _note = $"{name} stopped. {Domain.ErrorText.Plain(error)}";
        }
        finally
        {
            _running = null;
            _cancel?.Dispose();
            _cancel = null;
            ClearIcons();
        }
        if (!_isClosed) await RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        if ((uint)_cursor >= (uint)_packs.Count) return;
        var pack = _packs[_cursor];
        if (_running is not null || (!pack.Installed && !pack.Started)) return;
        _busyMenu = true;
        bool sure;
        try
        {
            sure = await PadMenu.ConfirmAsync(_host, $"Delete {pack.Name}?",
                pack.IsMain
                    ? $"Frees about {pack.Bytes >> 20} MB. Sprites then download one by one when a screen shows them (online), as before the pack. You can download it again here anytime."
                    : $"Frees about {pack.Bytes >> 20} MB. Boxes go back to the pixel sprites until it is downloaded again.",
                "Delete");
        }
        finally { _busyMenu = false; }
        if (!sure) return;
        await Task.Run(() => _downloader.DeletePack(pack.Name));
        ClearIcons();
        _status($"{pack.Name} deleted.");
        await RefreshAsync();
    }

    private void Close()
    {
        if (_isClosed) return;
        // Leaving pauses a download, as the old overlay's Cancel did: it resumes where it left off.
        _isClosed = true;
        _cancel?.Cancel();
        _router?.Remove(this);
        _host.Remove(_overlay);
        ClearIcons();
        foreach (var image in _fronts.Values) image?.Dispose();
        _fronts.Clear();
        _closed.TrySetResult();
    }

    /// <summary>Drops the icons made from the packs (they change with a download or a delete);
    /// each is drawn again, from the new files, on the next frame.</summary>
    private void ClearIcons()
    {
        foreach (var image in _icons.Values) image?.Dispose();
        _icons.Clear();
    }
}
