using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

public sealed partial class BoxBrowserPage
{
    /// <summary>One tool tile: its full name, what it does, its item sprite, the name on the tile.</summary>
    private sealed record HubTile(string Title, string Detail, string Icon, string Short);

    /// <summary>One group of tiles under its name.</summary>
    private sealed record HubShelf(string Title, IReadOnlyList<HubTile> Tiles);

    /// <summary>
    /// The tool hub behind Save data (Y) and Storage tools (X), painted by
    /// <see cref="SaveDataPaint"/>: a card on top, the tools in framed groups. The d-pad walks
    /// the tiles, L/R jump to the previous or next group, A opens, B closes. A tap picks a
    /// tile; a second tap on the picked one opens it.
    /// </summary>
    private sealed class ToolHubScreen : IPadHandler
    {
        private static ToolHubScreen? _open;

        private readonly IReadOnlyList<HubShelf> _groups;
        private readonly Func<SaveDataCard> _card;
        private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
        // Kept across openings: decoding them from the package in the first paint made every
        // opening of the hub hitch. The tinted art icons follow the colour scheme.
        private static readonly Dictionary<string, SKImage?> _icons = new(StringComparer.Ordinal);
        private static int _iconsTheme = -1;
        private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _overlay;
        private SaveDataHits? _hits;
        private int _tab;
        private int _cursor;

        /// <summary>Repaints the open screen, when a party icon finishes loading.</summary>
        public static void Repaint()
        {
            if (_open is not { } open) return;
            open._cardShown = null; // its icons are what changed
            open._canvas.InvalidateSurface();
        }

        // The card read once per opening (the save cannot change while the hub is up), not on
        // every cursor step: reading the trainer and the party is most of a frame's cost.
        private SaveDataCard? _cardShown;

        /// <summary>Shows the hub; Opened is false when it was closed, Tab and Cursor say where it was left.</summary>
        public static async Task<(bool Opened, int Tab, int Cursor)> ShowAsync(
            Grid host, IReadOnlyList<HubShelf> groups, Func<SaveDataCard> card, int tab, int cursor)
        {
            var screen = new ToolHubScreen(host, groups, card, tab, cursor);
            _open = screen;
            var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            router?.Push(screen);
            try
            {
                var opened = await screen._done.Task;
                return (opened, screen._tab, screen._cursor);
            }
            finally
            {
                router?.Remove(screen);
                host.Remove(screen._overlay);
                if (ReferenceEquals(_open, screen)) _open = null;
            }
        }

        private ToolHubScreen(Grid host, IReadOnlyList<HubShelf> groups, Func<SaveDataCard> card, int tab, int cursor)
        {
            _groups = groups;
            _card = card;
            _tab = Math.Clamp(tab, 0, Math.Max(0, groups.Count - 1));
            _cursor = Math.Clamp(cursor, 0, Math.Max(0, Rows.Count - 1));
            _canvas.PaintSurface += PaintGuard.Wrap("BoxBrowserPage.SaveData.OnPaint", OnPaint);
            _canvas.Touch += OnTouch;
            var footer = DsChrome.Footer(
                ("A", "Open", () => OnPadButton(PadButton.A)),
                ("B", "Close", () => OnPadButton(PadButton.B)));
            var screen = new Grid
            {
                Padding = new Thickness(14, 12, 14, 0),
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { _canvas, footer },
            };
            Grid.SetRow(footer, 1);
            _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
        }

        private IReadOnlyList<HubTile> Rows => _groups.Count == 0 ? [] : _groups[_tab].Tiles;

        // The apps in the order the grid shows them, flowing over its rows.
        private List<(int Shelf, int App)> Flat =>
            [.. _groups.SelectMany((group, s) => group.Tiles.Select((_, a) => (s, a)))];

        private void Go(int index)
        {
            var flat = Flat;
            if (flat.Count == 0) return;
            (_tab, _cursor) = flat[Math.Clamp(index, 0, flat.Count - 1)];
            _canvas.InvalidateSurface();
        }

        private int Here => Flat.IndexOf((_tab, _cursor));

        /// <summary>↑↓: the tool straight above or below, on the next row of frames.</summary>
        private void Vertical(int direction)
        {
            if (_hits is not { } hits) return;
            var here = hits.Apps.FirstOrDefault(app => app.Shelf == _tab && app.App == _cursor);
            if (here.Rect.IsEmpty) return;
            var beyond = hits.Apps.Where(app => direction > 0 ? app.Rect.Top >= here.Rect.Bottom : app.Rect.Bottom <= here.Rect.Top).ToList();
            if (beyond.Count == 0) return;
            var rowTop = direction > 0 ? beyond.Min(app => app.Rect.Top) : beyond.Max(app => app.Rect.Top);
            var best = beyond.Where(app => Math.Abs(app.Rect.Top - rowTop) < 1f)
                .OrderBy(app => Math.Abs(app.Rect.MidX - here.Rect.MidX)).First();
            (_tab, _cursor) = (best.Shelf, best.App);
            _canvas.InvalidateSurface();
        }

        /// <summary>L/R: the first app of the previous or next group.</summary>
        private void Group(int delta)
        {
            if (_groups.Count == 0) return;
            _tab = Math.Clamp(_tab + delta, 0, _groups.Count - 1);
            _cursor = 0;
            _canvas.InvalidateSurface();
        }

        // The artist's pixel icons, by the tile's short name. A tile without one keeps the game's
        // item sprite.
        private static readonly Dictionary<string, string> ArtIcons = new(StringComparer.Ordinal)
        {
            ["Card"] = "card", ["Pokédex"] = "dex", ["Records"] = "records", ["Bag"] = "bag",
            ["Poffins"] = "poffin", ["Underground"] = "underground", ["Encounters"] = "encounters",
            ["Honey"] = "honey", ["Flags"] = "flags", ["Gift gallery"] = "giftgallery",
            ["Stored gifts"] = "storedgifts", ["Tickets"] = "tickets", ["Restore"] = "restore",
            ["Export"] = "export", ["Close"] = "close", ["Bytes"] = "bytes",
            ["Select"] = "select", ["Boxes"] = "boxes", ["Sort"] = "sort", ["Files"] = "files",
            ["Showdown"] = "showdown", ["Random"] = "random", ["Eggs"] = "egg", ["Living Dex"] = "livingdex",
            ["Day Care"] = "daycare", ["Batch"] = "batch", ["Battle prep"] = "battleprep",
            ["Presets"] = "preset", ["Rename / OT"] = "renameot", ["How to get"] = "howtoget",
            ["Held items"] = "helditems", ["IVs"] = "ivs", ["Collection"] = "collection",
            ["Legality"] = "legality", ["Audit"] = "audit", ["Nuzlocke"] = "nuzlocke",
        };

        private SKImage? Icon(string name, string shortName)
        {
            if (ArtIcons.TryGetValue(shortName, out var art) && ArtIcon(art) is { } drawn) return drawn;
            return Icon(name);
        }

        /// <summary>One of the artist's white 16x16 icons, in the theme's icon colour.</summary>
        private SKImage? ArtIcon(string art)
        {
            if (_iconsTheme != ColorTheme.Version)
            {
                foreach (var stale in _icons.Keys.Where(name => name.StartsWith("art:", StringComparison.Ordinal)).ToList())
                {
                    _icons.Remove(stale);
                }
                _iconsTheme = ColorTheme.Version;
            }
            var key = "art:" + art;
            if (_icons.TryGetValue(key, out var known)) return known;
            SKImage? image = null;
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/art/{art}.png").GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream);
                if (bitmap is not null)
                {
                    using var tinted = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using (var canvas = new SKCanvas(tinted))
                    using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(ColorTheme.Current.TabIcon, SKBlendMode.SrcIn) })
                    {
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawBitmap(bitmap, 0, 0, paint);
                    }
                    image = SKImage.FromBitmap(tinted);
                }
            }
            catch (Exception error) when (error is IOException or FileNotFoundException) { }
            _icons[key] = image;
            return image;
        }

        private SKImage? Icon(string name)
        {
            if (_icons.TryGetValue(name, out var known)) return known;
            SKImage? image = null;
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/items/{name}.png").GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream);
                if (bitmap is not null)
                {
                    // Trimmed to the item's own pixels, so every tile shows it at the same size.
                    var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
                    using var whole = SKImage.FromBitmap(bitmap);
                    image = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
                }
            }
            catch (Exception error) when (error is IOException or FileNotFoundException) { }
            _icons[name] = image;
            return image;
        }

        private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            var scene = new SaveDataScene(
                _cardShown ??= _card(),
                [.. _groups.Select(group => new SaveDataShelf(group.Title,
                    [.. group.Tiles.Select(row => new SaveDataLine(row.Title, row.Detail, Icon(row.Icon, row.Short), row.Short))]))],
                _tab,
                _cursor);
            _hits = SaveDataPaint.Paint(args.Surface.Canvas, args.Info, scene, PixelTypeface());
        }

        private void Open()
        {
            if (_cursor < Rows.Count) _done.TrySetResult(true);
        }

        private void OnTouch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
            var at = args.Location;
            foreach (var (shelf, app, rect) in hits.Apps)
            {
                if (!rect.Contains(at)) continue;
                if (shelf == _tab && app == _cursor) Open();
                else { _tab = shelf; _cursor = app; _canvas.InvalidateSurface(); }
                return;
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Vertical(-1); return true;
                case PadButton.Down: Vertical(+1); return true;
                case PadButton.Left: Go(Here - 1); return true;
                case PadButton.Right: Go(Here + 1); return true;
                case PadButton.L: Group(-1); return true;
                case PadButton.R: Group(+1); return true;
                case PadButton.A: Open(); return true;
                case PadButton.B: _done.TrySetResult(false); return true;
                default: return true; // modal while open
            }
        }
    }
}
