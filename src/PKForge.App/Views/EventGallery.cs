using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;
/// <summary>
/// Mystery Gift, the way you remember it: a gift-pink world washed with white sparkles,
/// a wall of white wonder cards (sprites are the hero) with a close-up pane for the
/// highlighted gift. A opens the wondercard, B leaves. Cards draw the bundled pixel
/// sprites, so the shelf works fully offline.
/// </summary>
public static class EventGallery
{
    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session, int? targetSlot, Action repaint)
    {
        var services = IPlatformApplication.Current?.Services;
        var service = services?.GetService<IEventDatabaseService>();
        if (services is null || service is null) return;
        var sprites = services.GetRequiredService<ISpriteService>();
        var data = services.GetRequiredService<IGameDataService>();

        var gifts = service.GetGifts(session);
        if (gifts.Count == 0)
        {
            await PadMenu.ShowAsync(host, "MYSTERY GIFT",
                "No event distributions exist for this game's format in the archive.", "OK");
            return;
        }

        while (true)
        {
            var gift = await GiftShelf.ShowAsync(host, gifts, sprites, data);
            if (gift is null) return;

            var slot = targetSlot ?? viewModel.VisibleSlots.FirstOrDefault(s => s.Species is null)?.Slot ?? -1;
            if (slot < 0)
            {
                viewModel.Status = "No empty slot in this box for the gift.";
                return;
            }
            await viewModel.RunMutationAsync(s => service.Receive(s, gift.Id, viewModel.BoxIndex, slot), slot);
            repaint();
            return;
        }
    }

    /// <summary>Sprite centered and scaled (nearest-neighbor) into a box; a faint ball while it loads.</summary>
    private static void PaintMon(SKCanvas canvas, SKImageInfo info, SKBitmap? bitmap, float maxSize)
    {
        canvas.Clear(SKColors.Transparent);
        var cx = info.Width / 2f;
        var cy = info.Height / 2f;
        if (bitmap is null)
        {
            using var ball = new SKPaint { Color = UiTokens.SkEmptyMark, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
            var r = maxSize * 0.3f;
            canvas.DrawCircle(cx, cy, r, ball);
            canvas.DrawLine(cx - r, cy, cx + r, cy, ball);
            canvas.DrawCircle(cx, cy, r * 0.3f, ball);
            return;
        }
        var scale = Math.Min(maxSize / bitmap.Width, maxSize / bitmap.Height);
        var w = bitmap.Width * scale;
        var h = bitmap.Height * scale;
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>Shrink text to a pixel width with an ellipsis tail.</summary>
    private static string Fit(SKFont font, string text, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && font.MeasureText(text[..^1] + "…") > maxWidth)
            text = text[..^1];
        return text[..^1] + "…";
    }

    /// <summary>A tiny gold sparkle for shiny gifts (card corner).</summary>
    private static void PaintSparkle(SKCanvas canvas, float cx, float cy, float r)
    {
        using var paint = new SKPaint { Color = UiTokens.SkShinyGold, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(cx, cy - r);
        path.QuadTo(cx, cy, cx + r, cy);
        path.QuadTo(cx, cy, cx, cy + r);
        path.QuadTo(cx, cy, cx - r, cy);
        path.QuadTo(cx, cy, cx, cy - r);
        path.Close();
        canvas.DrawPath(path, paint);
    }


    /// <summary>
    /// The mystery-gift backdrop: a gift-pink field scattered with fixed white 4-point
    /// sparkles (deterministic positions, no animation). Prerendered per size. Language
    /// selection for wondercards is a future domain feature, so no chip row exists yet.
    /// </summary>
    private static SKCanvasView GiftBackdrop()
    {
        var canvasView = new SKCanvasView { InputTransparent = true };
        SKBitmap? prerendered = null;
        var prerenderedSize = new SKSizeI(-1, -1);
        canvasView.PaintSurface += (_, args) =>
        {
            var info = args.Info;
            if (info.Width <= 0 || info.Height <= 0) return;
            if (prerendered is null || prerenderedSize != info.Size)
            {
                prerendered?.Dispose();
                prerendered = RenderSparkles(info);
                prerenderedSize = info.Size;
            }
            args.Surface.Canvas.DrawBitmap(prerendered, 0, 0);
        };
        canvasView.Unloaded += (_, _) =>
        {
            prerendered?.Dispose();
            prerendered = null;
            prerenderedSize = new SKSizeI(-1, -1);
        };
        return canvasView;
    }

    private static SKBitmap RenderSparkles(SKImageInfo info)
    {
        var bitmap = new SKBitmap(info.Width, info.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Pksm.GiftPink);
        // Deterministic pseudo-random scatter: the same starfield every time it opens.
        for (var i = 0; i < 26; i++)
        {
            if (Hash(i * 3 + 2) < 0.38f) continue;
            var x = 10 + Hash(i) * (info.Width - 20);
            var y = 10 + Hash(i + 97) * (info.Height - 20);
            PksmPaint.Sparkle(canvas, new SKPoint(x, y), 3.5f + Hash(i + 193) * 5.5f);
        }
        return bitmap;
    }

    private static float Hash(int n)
    {
        var v = MathF.Sin(n * 127.1f + 311.7f) * 43758.5453f;
        return v - MathF.Floor(v);
    }

    // ── The shelf: card wall + close-up pane ─────────────────────────────────────

    private sealed class GiftShelf : IPadHandler
    {
        private const float Pad = 10f, Gap = 8f;
        // Card size is derived from the host at runtime (see ctor) - never a fixed 132x122,
        // which overflowed the 640x360 logical screen.
        private readonly float _cardW;
        private readonly float _cardH;

        private readonly TaskCompletionSource<EventGift?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly IReadOnlyList<EventGift> _gifts;
        private readonly ISpriteService _sprites;
        private readonly IGameDataService _data;
        private readonly SKCanvasView _wall;
        private readonly SKCanvasView _preview;
        private readonly Label _giftTitle;
        private readonly Label _giftFacts;
        private int _index;
        private int _scrollRow;
        private int _cols = 4;
        private int _visibleRows = 2;
        private bool _busy;

        public static Task<EventGift?> ShowAsync(Grid host, IReadOnlyList<EventGift> gifts, ISpriteService sprites, IGameDataService data) =>
            new GiftShelf(host, gifts, sprites, data)._result.Task;

        private GiftShelf(Grid host, IReadOnlyList<EventGift> gifts, ISpriteService sprites, IGameDataService data)
        {
            _host = host;
            _gifts = gifts;
            _sprites = sprites;
            _data = data;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            // Fit the Thor's 640x360 logical screen: every extent comes from the host's
            // actual laid-out size, so the shelf can never overflow the frame again.
            var maxW = host.Width > 0 ? host.Width - 24 : 616;
            var maxH = host.Height > 0 ? host.Height - 16 : 344;
            _cardH = (float)Math.Clamp((maxH - 152) / 2, 96, 122); // chrome ~= 120; two rows must show
            _cardW = _cardH * 1.08f;
            var previewW = Math.Clamp(maxW * 0.30, 148, 210);

            _wall = new SKCanvasView { EnableTouchEvents = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            _wall.PaintSurface += PaintWall;
            _wall.Touch += OnWallTouch;

            _preview = new SKCanvasView { HeightRequest = 96, HorizontalOptions = LayoutOptions.Fill };
            _preview.PaintSurface += (_, args) =>
            {
                var gift = _gifts[_index];
                var bitmap = _sprites.GetSprite(gift.Species, 0, gift.Shiny);
                if (bitmap is null) _sprites.Warm(gift.Species, 0, gift.Shiny, Repaint);
                PaintMon(args.Surface.Canvas, args.Info, bitmap, Math.Min(args.Info.Width, args.Info.Height) * 0.9f);
            };
            _giftTitle = new Label
            {
                TextColor = UiTokens.Ink0,
                FontFamily = DsChrome.PixelFont,
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                MaxLines = 2,
            };
            _giftFacts = new Label
            {
                TextColor = UiTokens.Ink1,
                FontFamily = DsChrome.PixelFont,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
            };

            var previewPane = Kit.LcdPanel(new VerticalStackLayout
            {
                Spacing = 6,
                Children = { Kit.HeaderBar("WONDERCARD"), _preview, _giftTitle, _giftFacts },
            }, padding: 10);

            var body = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions = [new(new GridLength(previewW)), new(GridLength.Star)],
                Children = { previewPane, _wall },
            };
            body.SetColumn(_wall, 1);
            var content = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children =
                {
                    Kit.HeaderBar($"MYSTERY GIFT · {gifts.Count}"),
                    body,
                    Kit.HintBar(("A", "WONDERCARD", null), ("B", "BACK", () => Close(null))),
                },
            };
            content.SetRow(body, 1);
            content.SetRow((View)content.Children[2], 2);

            // The gift world itself: a pink panel washed with fixed white sparkles -
            // the white cards and preview pane float on top of it.
            var window = new Border
            {
                BackgroundColor = UiTokens.GiftPink,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.14f, Radius = 12, Offset = new Point(0, 4) },
                Padding = 14,
                Content = new Grid { Children = { GiftBackdrop(), content } },
            };
            window.MaximumWidthRequest = maxW;
            window.MaximumHeightRequest = maxH;
            window.HorizontalOptions = LayoutOptions.Center;
            window.VerticalOptions = LayoutOptions.Center;

            var scrim = new BoxView { Color = UiTokens.Scrim };
            var scrimTap = new TapGestureRecognizer();
            scrimTap.Tapped += (_, _) => Close(null);
            scrim.GestureRecognizers.Add(scrimTap);

            _overlay = new Grid { Children = { scrim, window } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

            RefreshPreview();
            _router?.Push(this);
            Kit.AnimateIn(window);
        }

        private string SpeciesName(int species) =>
            (uint)species < (uint)_data.SpeciesNames.Count ? _data.SpeciesNames[species] : $"#{species}";

        private void Repaint() => MainThread.BeginInvokeOnMainThread(() =>
        {
            _wall.InvalidateSurface();
            _preview.InvalidateSurface();
        });

        private void RefreshPreview()
        {
            var gift = _gifts[_index];
            _giftTitle.Text = gift.Title;
            _giftFacts.Text = $"No. {gift.Species:000} · {SpeciesName(gift.Species)} · Lv. {gift.Level}{(gift.Shiny ? " · SHINY" : "")}";
            _preview.InvalidateSurface();
        }

        private void PaintWall(object? sender, SKPaintSurfaceEventArgs args)
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var info = args.Info;

            _cols = Math.Max(2, (int)((info.Width - Pad * 2 + Gap) / (_cardW + Gap)));
            _visibleRows = Math.Max(1, (int)((info.Height - Pad * 2 + Gap) / (_cardH + Gap)));

            using var nameFont = new SKFont { Size = 12f, Edging = SKFontEdging.Antialias, Embolden = true };
            using var lvFont = new SKFont { Size = 10.5f, Edging = SKFontEdging.Antialias, Embolden = true };
            using var namePaint = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var lvPaint = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            using var cardFill = new SKPaint { Color = Pksm.Paper, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var cardEdge = new SKPaint { Color = Pksm.PaperEdge, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };

            var first = _scrollRow * _cols;
            var last = Math.Min(_gifts.Count, first + _visibleRows * _cols);
            for (var i = first; i < last; i++)
            {
                var gift = _gifts[i];
                var local = i - first;
                var x = Pad + local % _cols * (_cardW + Gap);
                var y = Pad + local / _cols * (_cardH + Gap);
                var rect = new SKRect(x, y, x + _cardW, y + _cardH);

                using (var round = new SKRoundRect(rect, 6f))
                {
                    canvas.DrawRoundRect(round, cardFill);
                    canvas.DrawRoundRect(round, cardEdge);
                }
                var bitmap = _sprites.GetSprite(gift.Species, 0, gift.Shiny);
                if (bitmap is null)
                {
                    _sprites.Warm(gift.Species, 0, gift.Shiny, Repaint);
                    var bx = x + _cardW / 2;
                    var by = y + 40f;
                    using var ball = new SKPaint { Color = UiTokens.SkEmptyMark, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
                    canvas.DrawCircle(bx, by, 20f, ball);
                    canvas.DrawLine(bx - 20f, by, bx + 20f, by, ball);
                    canvas.DrawCircle(bx, by, 6f, ball);
                }
                else
                {
                    var max = _cardH - 48; // texts live in the bottom 44px
                    var scale = Math.Min(max / bitmap.Width, max / bitmap.Height);
                    var w = bitmap.Width * scale;
                    var h = bitmap.Height * scale;
                    using var image = SKImage.FromBitmap(bitmap);
                    canvas.DrawImage(image, new SKRect(x + (_cardW - w) / 2, y + 6 + (max - h) / 2, x + (_cardW + w) / 2, y + 6 + (max + h) / 2),
                        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                }

                if (gift.Shiny)
                    PaintSparkle(canvas, x + _cardW - 14, y + 14, 6f);

                var name = Fit(nameFont, SpeciesName(gift.Species), _cardW - 12);
                canvas.DrawText(name, x + _cardW / 2, y + _cardH - 30, SKTextAlign.Center, nameFont, namePaint);
                canvas.DrawText($"Lv. {gift.Level}", x + _cardW / 2, y + _cardH - 12, SKTextAlign.Center, lvFont, lvPaint);

                if (i == _index)
                    PksmPaint.Selection(canvas, rect);
            }
        }

        private void OnWallTouch(object? sender, SKTouchEventArgs args)
        {
            if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
            if (args.ActionType != SKTouchAction.Released) return;
            args.Handled = true;

            var col = (int)((args.Location.X - Pad) / (_cardW + Gap));
            var row = _scrollRow + (int)((args.Location.Y - Pad) / (_cardH + Gap));
            var index = row * _cols + col;
            if (col < 0 || col >= _cols || (uint)index >= (uint)_gifts.Count) return;
            _index = index;
            RefreshPreview();
            _wall.InvalidateSurface();
            OpenWonderCard();
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Left: Move(_index - 1); return true;
                case PadButton.Right: Move(_index + 1); return true;
                case PadButton.Up: Move(_index - _cols); return true;
                case PadButton.Down: Move(_index + _cols); return true;
                case PadButton.A: OpenWonderCard(); return true;
                case PadButton.B: Close(null); return true;
                default: return true; // the shelf owns the pad while open
            }
        }

        private void Move(int index)
        {
            _index = Math.Clamp(index, 0, _gifts.Count - 1);
            var row = _index / _cols;
            if (row < _scrollRow) _scrollRow = row;
            else if (row >= _scrollRow + _visibleRows) _scrollRow = row - _visibleRows + 1;
            RefreshPreview();
            _wall.InvalidateSurface();
        }

        private async void OpenWonderCard()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var gift = _gifts[_index];
                var receive = await WonderCard.ShowAsync(_host, gift, _sprites, SpeciesName(gift.Species));
                if (receive) Close(gift);
            }
            finally
            {
                _busy = false;
            }
        }

        private void Close(EventGift? result)
        {
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }

    // ── The wondercard: white panel, maroon header, big sprite, RECEIVE ────────

    private sealed class WonderCard : IPadHandler
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;

        public static Task<bool> ShowAsync(Grid host, EventGift gift, ISpriteService sprites, string speciesName) =>
            new WonderCard(host, gift, sprites, speciesName)._result.Task;

        private WonderCard(Grid host, EventGift gift, ISpriteService sprites, string speciesName)
        {
            _host = host;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            var sprite = new SKCanvasView { HeightRequest = 96, HorizontalOptions = LayoutOptions.Fill };
            sprite.PaintSurface += (_, args) =>
            {
                var bitmap = sprites.GetSprite(gift.Species, 0, gift.Shiny);
                if (bitmap is null) sprites.Warm(gift.Species, 0, gift.Shiny,
                    () => MainThread.BeginInvokeOnMainThread(sprite.InvalidateSurface));
                PaintMon(args.Surface.Canvas, args.Info, bitmap, 120f);
            };

            var receive = Kit.Capsule("RECEIVE", UiTokens.Green, primary: true);
            receive.Clicked += (_, _) => Close(true);
            var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
            close.Clicked += (_, _) => Close(false);

            // The classic wondercard: white panel, maroon header, big sprite hero.
            var card = Kit.DevicePanel(new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Kit.HeaderBar("WONDERCARD"),
                    sprite,
                    new Label
                    {
                        Text = gift.Title,
                        TextColor = UiTokens.Ink0,
                        FontFamily = DsChrome.PixelFont,
                        FontSize = 16,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.WordWrap,
                        MaxLines = 2,
                    },
                    new Label
                    {
                        Text = gift.Header,
                        TextColor = UiTokens.Ink1,
                        FontFamily = DsChrome.PixelFont,
                        FontSize = 12,
                        HorizontalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.WordWrap,
                        MaxLines = 2,
                    },
                    new Label
                    {
                        Text = $"No. {gift.Species:000} · {speciesName} · Lv. {gift.Level}{(gift.Shiny ? " · SHINY" : "")}",
                        TextColor = UiTokens.Ink1,
                        FontFamily = DsChrome.PixelFont,
                        FontSize = 12,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center,
                    },
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { receive, close },
                    },
                    Kit.HintBar(("A", "RECEIVE", null), ("B", "CLOSE", () => Close(false))),
                },
            }, padding: 14);
            card.MaximumWidthRequest = 420;
            card.MinimumWidthRequest = 340;
            card.MaximumHeightRequest = host.Height > 0 ? host.Height - 16 : 344; // never taller than the screen
            card.HorizontalOptions = LayoutOptions.Center;
            card.VerticalOptions = LayoutOptions.Center;

            var scrim = new BoxView { Color = UiTokens.Scrim };
            var scrimTap = new TapGestureRecognizer();
            scrimTap.Tapped += (_, _) => Close(false);
            scrim.GestureRecognizers.Add(scrimTap);

            _overlay = new Grid { Children = { scrim, card } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));
            _router?.Push(this);
            Kit.AnimateIn(card);
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.A: Close(true); return true;
                case PadButton.B: Close(false); return true;
                default: return true;
            }
        }

        private void Close(bool receive)
        {
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(receive);
        }
    }
}
