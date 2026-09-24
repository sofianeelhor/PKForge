using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Mystery Gift, as a card album: the gift-plum world holding a searchable, filterable,
/// grouped list of every distribution the open save can take (Pokémon and item cards),
/// a live wonder-card preview of the highlighted card, and a full wonder card with the
/// receive / Bank / export actions. The album logic (titles, languages, variants,
/// filters, sorting, grouping) lives in <see cref="WonderCardAlbum"/>; this file only
/// paints it. Lists and cards are Skia canvases that draw only what is on screen, over
/// the shared sprite cache, so hundreds of cards scroll without view churn.
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
        var history = services.GetService<InjectedGiftHistory>() ?? new InjectedGiftHistory(null);

        // Loading the archive (extract + index once per generation) and describing ~1k cards
        // happens off the UI thread; a small overlay covers the first-open beat.
        var loading = LoadingOverlay.Show(host, "Mystery gift", "Opening the card album…");
        EventGiftSaveProfile? profile;
        IReadOnlyList<WonderCardEntry> entries;
        try
        {
            (profile, entries) = await Task.Run(() =>
            {
                EventArchive.EnsureLoaded(session.Generation);
                var p = service.GetSaveProfile(session);
                var gifts = service.GetGifts(session);
                var e = WonderCardAlbum.CreateEntries(gifts, s => SpeciesName(data, s), p,
                    g => p is not null && history.IsInjected(InjectedGiftHistory.KeyFor(g, p)));
                return (p, e);
            });
        }
        finally
        {
            loading.Close();
        }

        if (entries.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Mystery gift",
                "No event distributions exist for this game's format in the archive.", "OK");
            return;
        }

        var slot = targetSlot ?? viewModel.VisibleSlots.FirstOrDefault(s => s.Species is null)?.Slot ?? -1;
        var context = new AlbumContext(host, viewModel, session, service, sprites, data, history, profile, slot, targetSlot is not null);
        var chosen = await GiftAlbum.ShowAsync(context, entries);
        if (chosen is null) return;

        var gift = chosen.Gift;
        bool received;
        if (gift.Kind == EventGiftKind.Item)
        {
            received = await viewModel.RunMutationAsync(s => service.ReceiveItems(s, gift.Id), 0, refreshSlot: false,
                changeDescription: $"Mystery gift: {chosen.Title}", action: SaveAction.InjectEvent);
        }
        else
        {
            if (slot < 0)
            {
                viewModel.Status = "No empty slot in this box for the gift.";
                return;
            }
            received = await viewModel.RunMutationAsync(s => service.Receive(s, gift.Id, viewModel.BoxIndex, slot), slot, action: SaveAction.InjectEvent);
        }
        if (received && profile is not null)
            history.Record(InjectedGiftHistory.KeyFor(gift, profile));
        repaint();
    }

    internal static string SpeciesName(IGameDataService data, int species) =>
        (uint)species < (uint)data.SpeciesNames.Count ? data.SpeciesNames[species] : $"#{species}";

    /// <summary>Everything the album and its wonder card need from the open session.</summary>
    private sealed record AlbumContext(
        Grid Host, BoxBrowserViewModel ViewModel, ISaveEngineSession Session, IEventDatabaseService Service,
        ISpriteService Sprites, IGameDataService Data, InjectedGiftHistory History, EventGiftSaveProfile? Profile,
        int TargetSlot, bool SlotChosen);

    // ── Shared painting ──────────────────────────────────────────────────────────

    /// <summary>Sprite centered and scaled (nearest-neighbor) into a box; a faint ball while it loads.</summary>
    internal static void PaintMon(SKCanvas canvas, SKImageInfo info, SKBitmap? bitmap, float maxSize)
    {
        canvas.Clear(SKColors.Transparent);
        DrawSprite(canvas, new SKRect(0, 0, info.Width, info.Height), bitmap, maxSize);
    }

    /// <summary>Shrink text to a pixel width with an ellipsis tail.</summary>
    internal static string Fit(SKFont font, string text, float maxWidth)
    {
        if (maxWidth <= 0 || text.Length == 0) return "";
        font = For(font, text);
        if (font.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && font.MeasureText(text[..^1] + "…") > maxWidth)
            text = text[..^1];
        return text[..^1] + "…";
    }

    private static readonly Dictionary<(string, float), SKFont> Fallbacks = [];

    /// <summary>
    /// The pixel face has no Hangul, Han or fullwidth forms (Korean OTs, Chinese titles):
    /// text it cannot draw gets a system face that can, at the same size - never tofu.
    /// </summary>
    private static SKFont For(SKFont font, string text)
    {
        if (text.Length == 0 || font.ContainsGlyphs(text)) return font;
        var missing = text.FirstOrDefault(ch => !char.IsWhiteSpace(ch) && font.GetGlyph(ch) == 0);
        var face = (missing == default ? null : SKFontManager.Default.MatchCharacter(missing)) ?? CjkFace;
        var key = (face.FamilyName, font.Size);
        lock (Fallbacks)
        {
            if (!Fallbacks.TryGetValue(key, out var fallback))
                Fallbacks[key] = fallback = new SKFont(face, font.Size) { Edging = SKFontEdging.Antialias };
            return fallback;
        }
    }

    /// <summary>DrawText through <see cref="For"/>: the pixel face when it can, a fallback when it cannot.</summary>
    private static void Text(SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint) =>
        canvas.DrawText(text, x, y, align, For(font, text), paint);

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/>, ellipsized.</summary>
    private static List<string> Wrap(SKFont font, string text, float width, int maxLines)
    {
        font = For(font, text);
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        for (var i = 0; i < words.Length; i++)
        {
            var candidate = current.Length == 0 ? words[i] : current + " " + words[i];
            if (font.MeasureText(candidate) <= width || current.Length == 0)
            {
                current = candidate;
                continue;
            }
            lines.Add(current);
            current = words[i];
            if (lines.Count == maxLines - 1)
            {
                current = string.Join(' ', words[i..]);
                break;
            }
        }
        if (current.Length > 0) lines.Add(current);
        for (var i = 0; i < lines.Count; i++)
            lines[i] = Fit(font, lines[i], width);
        return lines;
    }

    private static readonly SKSamplingOptions Pixel = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private static void DrawSprite(SKCanvas canvas, SKRect box, SKBitmap? bitmap, float maxSize)
    {
        var cx = box.MidX;
        var cy = box.MidY;
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
        canvas.DrawImage(ImageOf(bitmap), new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), Pixel);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, SKImage> Images = new();

    /// <summary>One SKImage per cached bitmap: scrolling never re-wraps sprites.</summary>
    private static SKImage ImageOf(SKBitmap bitmap)
    {
        if (Images.TryGetValue(bitmap, out var image)) return image;
        bitmap.SetImmutable();
        image = SKImage.FromBitmap(bitmap);
        Images.AddOrUpdate(bitmap, image);
        return image;
    }

    /// <summary>The gift's picture: its Pokémon sprite, or a wrapped present with the item's
    /// icon on it (item cards). Kicks off the cache warm and repaints when ready.</summary>
    private static void DrawGiftArt(SKCanvas canvas, SKRect box, WonderCardEntry entry, ISpriteService sprites, Action repaint, float scale = 1f)
    {
        var gift = entry.Gift;
        var size = Math.Min(box.Width, box.Height) * scale;
        if (gift.Kind == EventGiftKind.Item)
        {
            DrawPresent(canvas, box, size * 0.82f);
            var name = gift.Details?.Items.FirstOrDefault()?.Name;
            if (name is not null && ItemIcons.Get(name, repaint) is { } icon)
            {
                var s = size * 0.46f;
                var r = new SKRect(box.MidX + size * 0.05f, box.MidY + size * 0.02f, box.MidX + size * 0.05f + s, box.MidY + size * 0.02f + s);
                canvas.DrawImage(ImageOf(icon), r, Pixel);
            }
            return;
        }
        var form = gift.Details?.Form ?? 0;
        var bitmap = sprites.GetSprite(gift.Species, form, gift.Shiny);
        if (bitmap is null) sprites.Warm(gift.Species, form, gift.Shiny, repaint);
        DrawSprite(canvas, box, bitmap, size);
    }

    /// <summary>A small wrapped present in the gift palette (item cards' art).</summary>
    private static void DrawPresent(SKCanvas canvas, SKRect box, float size)
    {
        var w = size * 0.72f;
        var h = size * 0.56f;
        var body = new SKRect(box.MidX - w / 2, box.MidY - h / 2 + size * 0.1f, box.MidX + w / 2, box.MidY + h / 2 + size * 0.1f);
        var lid = new SKRect(body.Left - size * 0.05f, body.Top - size * 0.16f, body.Right + size * 0.05f, body.Top + size * 0.02f);
        using var fill = new SKPaint { Color = Pksm.GiftRed, IsAntialias = true };
        using var lidFill = new SKPaint { Color = PksmPaint.Lighter(Pksm.GiftRed, 0.18f), IsAntialias = true };
        using var ribbon = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
        using var edge = new SKPaint { Color = Pksm.LogoVoid, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawRoundRect(body, 2, 2, fill);
        canvas.DrawRoundRect(body, 2, 2, edge);
        canvas.DrawRoundRect(lid, 2, 2, lidFill);
        canvas.DrawRoundRect(lid, 2, 2, edge);
        var band = size * 0.1f;
        canvas.DrawRect(new SKRect(box.MidX - band / 2, lid.Top, box.MidX + band / 2, body.Bottom), ribbon);
        canvas.DrawOval(new SKRect(box.MidX - band * 2.2f, lid.Top - band * 1.4f, box.MidX, lid.Top + band * 0.2f), ribbon);
        canvas.DrawOval(new SKRect(box.MidX, lid.Top - band * 1.4f, box.MidX + band * 2.2f, lid.Top + band * 0.2f), ribbon);
    }

    /// <summary>A tiny gold 4-point star: the shiny mark (gold is reserved for shiny).</summary>
    private static void DrawShinyStar(SKCanvas canvas, float cx, float cy, float r)
    {
        using var paint = new SKPaint { Color = Pksm.ShinyGold, Style = SKPaintStyle.Fill, IsAntialias = true };
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
    /// A short tag ("ENG", "Received") as plain coloured text, drawn left to right; returns its
    /// right edge. No box: signal tones colour the word, neutral (deck) tags use the ink.
    /// </summary>
    private static float DrawTag(SKCanvas canvas, float x, float baseline, string text, SKFont font, SKColor fill, SKColor ink, bool alignRight = false)
    {
        var w = font.MeasureText(text) + 9f;
        var left = alignRight ? x - w : x;
        var rect = new SKRect(left, 0, left + w, 0);
        var neutral = fill.Red == Pksm.LogoDeck.Red && fill.Green == Pksm.LogoDeck.Green && fill.Blue == Pksm.LogoDeck.Blue;
        using (var t = new SKPaint { Color = neutral ? ink : fill.WithAlpha(0xFF), IsAntialias = true })
            Text(canvas, text, rect.MidX, baseline, SKTextAlign.Center, font, t);
        return alignRight ? rect.Left : rect.Right;
    }

    /// <summary>A received check mark in a green disc.</summary>
    private static void DrawCheck(SKCanvas canvas, float cx, float cy, float r)
    {
        using var disc = new SKPaint { Color = PksmPaint.Tone(Pksm.Legal), IsAntialias = true };
        using var rim = new SKPaint { Color = Pksm.Legal, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
        using var tick = new SKPaint { Color = Pksm.Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round };
        canvas.DrawCircle(cx, cy, r, disc);
        canvas.DrawCircle(cx, cy, r, rim);
        canvas.DrawLine(cx - r * 0.45f, cy + r * 0.02f, cx - r * 0.1f, cy + r * 0.38f, tick);
        canvas.DrawLine(cx - r * 0.1f, cy + r * 0.38f, cx + r * 0.48f, cy - r * 0.34f, tick);
    }

    /// <summary>Scales a canvas to dp and returns the dp size.</summary>
    private static SKSize BeginDp(SKCanvas canvas, SKImageInfo info)
    {
        canvas.Clear(SKColors.Transparent);
        var density = (float)DeviceDisplay.MainDisplayInfo.Density;
        if (density <= 0) density = 1;
        canvas.Scale(density);
        return new SKSize(info.Width / density, info.Height / density);
    }

    private static float Density => Math.Max(1f, (float)DeviceDisplay.MainDisplayInfo.Density);

    /// <summary>CJK-capable face for original card titles (the pixel face has no kana).</summary>
    private static SKTypeface CjkFace => _cjk ??= SKFontManager.Default.MatchCharacter('あ') ?? SKTypeface.Default;
    private static SKTypeface? _cjk;

    private static SKFont Font(float size) => new(PixelFont.Face, size) { Edging = SKFontEdging.Antialias };

    /// <summary>The card-album world's card colours: plum wonder-card paper over the navy chrome.</summary>
    private static readonly SKColor CardTop = PksmPaint.Mix(Pksm.GiftPinkLight, Pksm.Paper, 0.45f);
    private static readonly SKColor CardBottom = PksmPaint.Mix(Pksm.GiftPink, Pksm.PaperShade, 0.35f);
    private static readonly SKColor CardRim = PksmPaint.Lighter(Pksm.GiftPinkLight, 0.12f);
    private static readonly SKColor GroupInk = PksmPaint.Lighter(Pksm.GiftRed, 0.25f);

    /// <summary>
    /// The wonder card's body: a hard drop shadow, the plum paper falling to navy, a pale
    /// frame inside a rose rim and a header band - the DS card album's card, in this app's DA.
    /// </summary>
    private static void DrawCardBody(SKCanvas c, SKRect r, float band)
    {
        using (var shadow = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0x88), IsAntialias = true })
            c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 3, r.Right + 3, r.Bottom + 3), 8, 8, shadow);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                   [CardTop, CardBottom], SKShaderTileMode.Clamp))
        using (var body = new SKPaint { Shader = shader, IsAntialias = true })
            c.DrawRoundRect(r, 8, 8, body);
        // Header band: a deeper plum strip with a light line, like the card's printed title bar.
        var head = new SKRect(r.Left + 4, r.Top + 4, r.Right - 4, r.Top + 4 + band);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, head.Top), new SKPoint(0, head.Bottom),
                   [PksmPaint.Lighter(Pksm.GiftPinkLight, 0.1f), PksmPaint.Darker(Pksm.GiftPinkLight, 0.18f)], SKShaderTileMode.Clamp))
        using (var bandPaint = new SKPaint { Shader = shader, IsAntialias = true })
            c.DrawRoundRect(head, 5, 5, bandPaint);
        using (var rim = new SKPaint { Color = CardRim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            c.DrawRoundRect(SKRect.Inflate(r, -1, -1), 8, 8, rim);
        using (var frame = new SKPaint { Color = Pksm.Ink.WithAlpha(0x2C), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            c.DrawRoundRect(SKRect.Inflate(r, -6, -6), 5, 5, frame);
        // Two faint sparkles in the band corners: the gift world's signature.
        PksmPaint.Sparkle(c, new SKPoint(head.Right - 10, head.MidY), 3.2f);
    }

    /// <summary>The stage the gift stands on: a soft radial light in a ring.</summary>
    private static void DrawStage(SKCanvas c, SKPoint center, float radius)
    {
        using (var shader = SKShader.CreateRadialGradient(center, radius,
                   [PksmPaint.Lighter(Pksm.GiftPinkLight, 0.28f).WithAlpha(0xD0), Pksm.GiftPink.WithAlpha(0x40)], SKShaderTileMode.Clamp))
        using (var glow = new SKPaint { Shader = shader, IsAntialias = true })
            c.DrawCircle(center, radius, glow);
        using var ring = new SKPaint { Color = Pksm.Ink.WithAlpha(0x40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
        c.DrawCircle(center, radius, ring);
    }

    private static string CardNumber(EventGift gift) => $"No. {gift.CardId:0000}";

    /// <summary>"Pikachu · Lv. 20" / "Rare Candy ×5 + 1 more".</summary>
    private static string Subject(WonderCardEntry e)
    {
        var gift = e.Gift;
        if (gift.Kind == EventGiftKind.Item)
        {
            var items = gift.Details?.Items ?? [];
            if (items.Count == 0) return "Item";
            var first = items[0].Quantity > 1 ? $"{items[0].Name} x{items[0].Quantity}" : items[0].Name;
            return items.Count > 1 ? $"{first} + {items.Count - 1} more" : first;
        }
        return gift.Details?.IsEgg == true ? $"{e.SpeciesName} Egg" : $"{e.SpeciesName} · Lv. {gift.Level}";
    }

    private static string DateText(WonderCardEntry e) =>
        e.Date is { } d ? d.ToString("yyyy-MM-dd") : e.Year?.ToString() ?? "Undated";

    // ── Item icons (bag art, cached) ────────────────────────────────────────────

    private static class ItemIcons
    {
        private static readonly Dictionary<string, SKBitmap?> Cache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);
        private static readonly Lock Gate = new();

        public static SKBitmap? Get(string name, Action onLoaded)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(name, out var cached)) return cached;
                if (!Loading.Add(name)) return null;
            }
            _ = Task.Run(async () =>
            {
                SKBitmap? bitmap = null;
                try
                {
                    if (await ItemArt.GetAsync(name) is { } path && File.Exists(path))
                        bitmap = SKBitmap.Decode(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
                {
                    // Offline or unknown item: the present alone stands in.
                }
                lock (Gate)
                {
                    Cache[name] = bitmap;
                    Loading.Remove(name);
                }
                if (bitmap is not null) onLoaded();
            });
            return null;
        }
    }

    // ── The album: header, search, filter chips, grouped list, live card preview ──

    private sealed class GiftAlbum : IPadHandler
    {
        private const float RowH = 46f, HeaderH = 24f, RowGap = 3f, ListPad = 5f;

        private readonly TaskCompletionSource<WonderCardEntry?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AlbumContext _ctx;
        private IReadOnlyList<WonderCardEntry> _all;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly SKCanvasView _header;
        private readonly SKCanvasView _chips;
        private readonly SKCanvasView _list;
        private readonly SKCanvasView _preview;
        private readonly Entry _search;
        private readonly List<(SKRect Rect, Action Tap)> _chipHits = [];
        private WonderCardQuery _query = new();
        private WonderCardPage _page = new([], [], 0, 0);
        // The flat line layout of the list: group headers and card rows with their dp offsets.
        private readonly List<(float Y, float H, int Item, int Group)> _lines = [];
        private float[] _itemY = [];
        private float _contentH;
        private float _scroll;
        private float _viewH = 200;
        private int _index;
        private bool _busy;
        private CancellationTokenSource? _searchDebounce;

        public static Task<WonderCardEntry?> ShowAsync(AlbumContext ctx, IReadOnlyList<WonderCardEntry> entries) =>
            new GiftAlbum(ctx, entries)._result.Task;

        private GiftAlbum(AlbumContext ctx, IReadOnlyList<WonderCardEntry> entries)
        {
            _ctx = ctx;
            _all = entries;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            var host = ctx.Host;
            var maxW = host.Width > 0 ? host.Width - 16 : 624;
            var maxH = host.Height > 0 ? host.Height - 12 : 348;
            var previewW = Math.Clamp(maxW * 0.38, 200, 250);

            _header = new SKCanvasView { HeightRequest = 32, InputTransparent = true };
            _header.PaintSurface += PaintHeader;

            _search = Kit.TextField();
            _search.Placeholder = "Search title, Pokémon, OT, move…";
            _search.HeightRequest = 34;
            _search.FontSize = UiTokens.TextSmall;
            _search.TextChanged += (_, args) => OnSearchChanged(args.NewTextValue ?? "");
            _search.Completed += (_, _) => _search.Unfocus();

            _chips = new SKCanvasView { HeightRequest = 28, EnableTouchEvents = true };
            _chips.PaintSurface += PaintChips;
            _chips.Touch += OnChipsTouch;

            _list = new SKCanvasView { EnableTouchEvents = true };
            _list.PaintSurface += PaintList;
            _list.Touch += OnListTouch;

            _preview = new SKCanvasView { EnableTouchEvents = true };
            _preview.PaintSurface += PaintPreview;
            _preview.Touch += (_, args) =>
            {
                args.Handled = true;
                if (args.ActionType == SKTouchAction.Released) OpenCard();
            };

            var top = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(GridLength.Star), new(new GridLength(Math.Clamp(maxW * 0.36, 180, 240)))],
                Children = { _header, _search },
            };
            Grid.SetColumn(_search, 1);

            var body = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(GridLength.Star), new(new GridLength(previewW))],
                Children = { _list, _preview },
            };
            Grid.SetColumn(_preview, 1);

            var hints = Kit.WindowHints(
                ("A", "Open", OpenCard),
                ("X", "Search", FocusSearch),
                ("Y", "Filters", () => _ = ShowFilterMenuAsync()),
                ("LR", "Section", null),
                ("+", "Sort", CycleSort),
                ("B", "Back", () => Close(null)));

            var content = new Grid
            {
                RowSpacing = 6,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children = { top, _chips, body, hints },
            };
            Grid.SetRow(_chips, 1);
            Grid.SetRow(body, 2);
            Grid.SetRow(hints, 3);

            var window = new Border
            {
                BackgroundColor = UiTokens.GiftPink,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Shadow = Kit.HardShadow(),
                Padding = new Thickness(10, 8),
                Content = new Grid { Children = { GiftBackdrop(), content } },
                MaximumWidthRequest = maxW,
                MaximumHeightRequest = maxH,
                WidthRequest = maxW,
                HeightRequest = maxH,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };

            var scrim = new BoxView { Color = UiTokens.Scrim };
            _overlay = new Grid { Children = { scrim, window } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

            _query = WonderCardAlbum.DefaultQuery(entries);
            Requery(keep: null);
            _router?.Push(this);
            Kit.AnimateIn(window);
        }

        private WonderCardEntry? Current => _page.Items.Count == 0 ? null : _page.Items[Math.Clamp(_index, 0, _page.Items.Count - 1)];

        private void Repaint() => MainThread.BeginInvokeOnMainThread(() =>
        {
            _list.InvalidateSurface();
            _preview.InvalidateSurface();
        });

        // ── Query ─────────────────────────────────────────────────────────────

        private void Requery(WonderCardEntry? keep)
        {
            _page = WonderCardAlbum.Query(_all, _query, _ctx.Profile);
            var at = keep is null ? -1 : IndexOfGift(keep.Gift);
            _index = Math.Max(0, at);
            RebuildLines();
            _scroll = 0;
            EnsureVisible();
            _header.InvalidateSurface();
            _chips.InvalidateSurface();
            _list.InvalidateSurface();
            _preview.InvalidateSurface();
        }

        private int IndexOfGift(EventGift gift)
        {
            for (var i = 0; i < _page.Items.Count; i++)
                if (_page.Items[i].Gift.Id == gift.Id || _page.Items[i].Variants.Any(v => v.Gift.Id == gift.Id)) return i;
            return -1;
        }

        private void OnSearchChanged(string text)
        {
            _searchDebounce?.Cancel();
            var cts = _searchDebounce = new CancellationTokenSource();
            // A short debounce: typing stays fluid, the list follows once the finger rests.
            _ = Task.Delay(160, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _query = _query with { Search = text };
                    Requery(keep: Current);
                });
            }, TaskScheduler.Default);
        }

        private void RebuildLines()
        {
            _lines.Clear();
            _itemY = new float[_page.Items.Count];
            var y = ListPad;
            var groups = _page.Groups;
            var g = 0;
            for (var i = 0; i < _page.Items.Count; i++)
            {
                if (g < groups.Count && groups[g].Start == i)
                {
                    _lines.Add((y, HeaderH, -1, g));
                    y += HeaderH + RowGap;
                    g++;
                }
                _itemY[i] = y;
                _lines.Add((y, RowH, i, g - 1));
                y += RowH + RowGap;
            }
            _contentH = y + ListPad;
        }

        private void EnsureVisible()
        {
            if (_page.Items.Count == 0) { _scroll = 0; return; }
            var top = _itemY[_index];
            // Keep the section header in view when the cursor sits on a section's first card.
            var group = _page.Groups.Count > 0 ? _page.GroupOf(_index) : -1;
            if (group >= 0 && _page.Groups[group].Start == _index) top -= HeaderH + RowGap;
            var bottom = _itemY[_index] + RowH + ListPad;
            if (top - ListPad < _scroll) _scroll = Math.Max(0, top - ListPad);
            else if (bottom > _scroll + _viewH) _scroll = bottom - _viewH;
            ClampScroll();
        }

        private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - _viewH));

        // ── Header & chips ────────────────────────────────────────────────────

        private void PaintHeader(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            var strip = new SKRect(0, 1, size.Width - 3, size.Height - 2);
            using var title = Font(15.5f);
            PksmPaint.HeaderStrip(c, strip, "Mystery Gift", title, Pksm.GiftPinkLight);
            using var small = Font(12.5f);
            var counts = _page.Items.Count == _page.Total
                ? $"{_page.Total} cards"
                : $"{_page.Items.Count} of {_page.Total}";
            var received = _page.ReceivedCount > 0 ? $"  ·  {_page.ReceivedCount} received" : "";
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            var text = Fit(small, counts + received, strip.Width - title.MeasureText("Mystery Gift") - 34);
            Text(c, text, strip.Right - 10, strip.MidY + small.Size * 0.36f, SKTextAlign.Right, small, ink);
        }

        private IEnumerable<(string Label, bool On, Action Tap)> Chips()
        {
            var q = _query;
            if (_ctx.Profile is not null)
                yield return ("Fits save", q.CompatibleOnly, () => Set(q with { CompatibleOnly = !q.CompatibleOnly }));
            yield return (q.Kind switch { WonderCardKindFilter.Pokemon => "Pokémon", WonderCardKindFilter.Items => "Items", _ => "All gifts" },
                q.Kind != WonderCardKindFilter.All,
                () => Set(q with { Kind = (WonderCardKindFilter)(((int)q.Kind + 1) % 3) }));
            yield return ("Shiny", q.ShinyOnly, () => Set(q with { ShinyOnly = !q.ShinyOnly }));
            yield return (q.Received switch { WonderCardReceivedFilter.NotReceived => "New only", WonderCardReceivedFilter.Received => "Received", _ => "Any status" },
                q.Received != WonderCardReceivedFilter.Any,
                () => Set(q with { Received = (WonderCardReceivedFilter)(((int)q.Received + 1) % 3) }));
            var extra = (q.Year is null ? 0 : 1) + (q.Species is null ? 0 : 1) + (q.Language is null ? 0 : 1) + (q.Series is null ? 0 : 1);
            yield return (extra > 0 ? $"More · {extra}" : "More…", extra > 0, () => _ = ShowFilterMenuAsync());
            yield return ($"Sort: {SortLabel(q.Sort)}", false, CycleSort);
            yield return ($"Group: {GroupLabel(q.Grouping)}", false, CycleGrouping);
        }

        private void PaintChips(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            _chipHits.Clear();
            using var font = Font(12.5f);
            var chips = Chips().ToArray();
            const float gap = 5f, padX = 9f;
            var natural = chips.Sum(ch => font.MeasureText(ch.Label) + padX * 2) + gap * (chips.Length - 1);
            var squeeze = natural > size.Width - 3 ? (size.Width - 3 - gap * (chips.Length - 1)) / (natural - gap * (chips.Length - 1)) : 1f;
            var x = 0f;
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            foreach (var chip in chips)
            {
                var w = (font.MeasureText(chip.Label) + padX * 2) * squeeze;
                var r = new SKRect(x, 2, x + w, size.Height - 3);
                if (chip.On) PksmPaint.SelectedButton(c, r, 4, Pksm.GiftPinkLight);
                else PksmPaint.BlackButton(c, r, 4);
                Text(c, Fit(font, chip.Label, w - 8), r.MidX, r.MidY + font.Size * 0.36f, SKTextAlign.Center, font, chip.On ? ink : soft);
                _chipHits.Add((r, chip.Tap));
                x += w + gap;
            }
        }

        private void OnChipsTouch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (args.ActionType != SKTouchAction.Released) return;
            var p = new SKPoint(args.Location.X / Density, args.Location.Y / Density);
            foreach (var (rect, tap) in _chipHits)
                if (SKRect.Inflate(rect, 2, 4).Contains(p)) { tap(); return; }
        }

        private void Set(WonderCardQuery query)
        {
            _query = query;
            Requery(keep: Current);
        }

        private static string SortLabel(WonderCardSort sort) => sort switch
        {
            WonderCardSort.Newest => "Newest",
            WonderCardSort.Oldest => "Oldest",
            WonderCardSort.CardNumber => "Card no.",
            WonderCardSort.DexNumber => "Dex no.",
            WonderCardSort.Title => "A-Z",
            WonderCardSort.Level => "Level",
            _ => sort.ToString(),
        };

        private static string GroupLabel(WonderCardGrouping grouping) => grouping switch
        {
            WonderCardGrouping.Year => "Year",
            WonderCardGrouping.Series => "Event",
            WonderCardGrouping.Games => "Game",
            WonderCardGrouping.Species => "Pokémon",
            _ => "None",
        };

        private void CycleSort() => Set(_query with { Sort = (WonderCardSort)(((int)_query.Sort + 1) % 6) });
        private void CycleGrouping() => Set(_query with { Grouping = (WonderCardGrouping)(((int)_query.Grouping + 1) % 5) });

        // ── List ─────────────────────────────────────────────────────────────

        private void PaintList(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            if (Math.Abs(_viewH - size.Height) > 0.5f)
            {
                _viewH = size.Height;
                EnsureVisible();
            }
            var panel = new SKRect(0, 0, size.Width - 3, size.Height - 3);
            PksmPaint.Panel(c, panel, Pksm.PaperShade);
            c.Save();
            c.ClipRoundRect(new SKRoundRect(SKRect.Inflate(panel, -2, -2), 5));

            using var title = Font(13.5f);
            using var small = Font(11.5f);
            using var tagFont = Font(10.5f);
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            using var groupInk = new SKPaint { Color = GroupInk, IsAntialias = true };
            using var hair = new SKPaint { Color = Pksm.PaperEdge.WithAlpha(0xA0), StrokeWidth = 1 };
            using var stripe = new SKPaint { Color = PksmPaint.Lighter(Pksm.PaperShade, 0.045f), IsAntialias = true };

            if (_page.Items.Count == 0)
            {
                Text(c, "No cards match.", panel.MidX, panel.MidY - 6, SKTextAlign.Center, title, ink);
                Text(c, "Clear the search or relax the filters (Y).", panel.MidX, panel.MidY + 14, SKTextAlign.Center, small, soft);
                c.Restore();
                return;
            }

            var left = panel.Left + 5;
            var right = panel.Right - 9; // room for the scrollbar
            foreach (var line in _lines)
            {
                var y = line.Y - _scroll;
                if (y + line.H < 0) continue;
                if (y > size.Height) break;
                if (line.Item < 0)
                {
                    var group = _page.Groups[line.Group];
                    var baseline = y + HeaderH * 0.68f;
                    Text(c, Fit(title, group.Title, right - left - 70), left + 4, baseline, SKTextAlign.Left, title, groupInk);
                    Text(c, group.Count == 1 ? "1 card" : $"{group.Count} cards", right - 2, baseline, SKTextAlign.Right, small, soft);
                    var tw = title.MeasureText(Fit(title, group.Title, right - left - 70));
                    var countW = small.MeasureText(group.Count == 1 ? "1 card" : $"{group.Count} cards");
                    c.DrawLine(left + 12 + tw, y + HeaderH * 0.5f, right - countW - 10, y + HeaderH * 0.5f, hair);
                    continue;
                }
                PaintRow(c, new SKRect(left, y, right, y + RowH), line.Item, title, small, tagFont, ink, soft, stripe);
            }

            // Scrollbar: a slim cobalt thumb showing where in the album we are.
            if (_contentH > _viewH)
            {
                var track = new SKRect(panel.Right - 7, panel.Top + 6, panel.Right - 4, panel.Bottom - 6);
                var thumbH = Math.Max(18, track.Height * _viewH / _contentH);
                var thumbY = track.Top + (track.Height - thumbH) * (_scroll / Math.Max(1, _contentH - _viewH));
                using var trackPaint = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0x90), IsAntialias = true };
                using var thumb = new SKPaint { Color = Pksm.LogoCyan.WithAlpha(0xC0), IsAntialias = true };
                c.DrawRoundRect(track, 1.5f, 1.5f, trackPaint);
                c.DrawRoundRect(new SKRect(track.Left, thumbY, track.Right, thumbY + thumbH), 1.5f, 1.5f, thumb);
            }
            c.Restore();
        }

        private void PaintRow(SKCanvas c, SKRect r, int index, SKFont title, SKFont small, SKFont tagFont, SKPaint ink, SKPaint soft, SKPaint stripe)
        {
            var e = _page.Items[index];
            var selected = index == _index;
            if (selected) PksmPaint.SelectedButton(c, r, 5);
            else c.DrawRoundRect(r, 5, 5, stripe);

            // Art in a small plum stage.
            var art = new SKRect(r.Left + 4, r.Top + 3, r.Left + 4 + RowH - 6, r.Bottom - 3);
            using (var well = new SKPaint { Color = (selected ? Pksm.LogoVoid : Pksm.GiftPink).WithAlpha(0xB0), IsAntialias = true })
                c.DrawRoundRect(art, 4, 4, well);
            DrawGiftArt(c, art, e, _ctx.Sprites, Repaint, 0.98f);
            if (e.Gift.Shiny) DrawShinyStar(c, art.Right - 5, art.Top + 5, 4.5f);

            var textLeft = art.Right + 8;
            var status = r.Right - 6;
            // Right column: language tag (+ variant count) above, received mark below.
            var tagText = e.LanguageTag ?? "ALL";
            if (e.Variants.Count > 1) tagText += $" +{e.Variants.Count - 1}";
            var tagLeft = DrawTag(c, status, r.Top + 17, tagText, tagFont,
                selected ? Pksm.LogoVoid.WithAlpha(0xA0) : Pksm.LogoDeck, Pksm.InkSoft, alignRight: true);
            if (e.Received) DrawCheck(c, status - 8, r.Bottom - 12, 7f);
            else if (!e.Compatible) DrawTag(c, status, r.Bottom - 7, "Other lang.", tagFont, PksmPaint.Tone(Pksm.Illegal), Pksm.Ink, alignRight: true);

            var titleW = tagLeft - textLeft - 6;
            Text(c, Fit(title, e.Title, titleW), textLeft, r.Top + 19, SKTextAlign.Left, title, ink);
            var line2 = $"{Subject(e)}  ·  {CardNumber(e.Gift)}";
            Text(c, Fit(small, line2, status - textLeft - (e.Received ? 22 : e.Compatible ? 4 : 70)), textLeft, r.Top + 37, SKTextAlign.Left, small, soft);
        }

        private float _touchStartY, _scrollStart;
        private bool _dragging;

        private void OnListTouch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            var y = args.Location.Y / Density;
            switch (args.ActionType)
            {
                case SKTouchAction.Pressed:
                    _touchStartY = y;
                    _scrollStart = _scroll;
                    _dragging = false;
                    return;
                case SKTouchAction.Moved:
                    if (!_dragging && Math.Abs(y - _touchStartY) > 8) _dragging = true;
                    if (_dragging)
                    {
                        _scroll = _scrollStart - (y - _touchStartY);
                        ClampScroll();
                        _list.InvalidateSurface();
                    }
                    return;
                case SKTouchAction.Released:
                    if (_dragging) { _dragging = false; return; }
                    var contentY = y + _scroll;
                    foreach (var line in _lines)
                    {
                        if (line.Item < 0 || contentY < line.Y || contentY > line.Y + line.H) continue;
                        if (line.Item == _index) OpenCard();
                        else Select(line.Item);
                        return;
                    }
                    return;
                case SKTouchAction.WheelChanged:
                    _scroll -= args.WheelDelta / Density;
                    ClampScroll();
                    _list.InvalidateSurface();
                    return;
            }
        }

        private void Select(int index)
        {
            if (_page.Items.Count == 0) return;
            _index = Math.Clamp(index, 0, _page.Items.Count - 1);
            EnsureVisible();
            _list.InvalidateSurface();
            _preview.InvalidateSurface();
        }

        /// <summary>L / R: previous / next section start (a page of rows when ungrouped).</summary>
        private void JumpSection(int direction)
        {
            if (_page.Items.Count == 0) return;
            if (_page.Groups.Count == 0)
            {
                Select(_index + direction * Math.Max(1, (int)(_viewH / (RowH + RowGap))));
                return;
            }
            var g = _page.GroupOf(_index);
            var start = _page.Groups[g].Start;
            if (direction < 0)
                Select(_index > start ? start : g > 0 ? _page.Groups[g - 1].Start : start);
            else
                Select(g + 1 < _page.Groups.Count ? _page.Groups[g + 1].Start : _page.Items.Count - 1);
        }

        // ── Preview: the highlighted card, printed ──────────────────────────────

        private void PaintPreview(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            var card = new SKRect(0, 0, size.Width - 4, size.Height - 4);
            DrawCardBody(c, card, 20);
            using var bandFont = Font(11.5f);
            using var title = Font(14f);
            using var small = Font(11.5f);
            using var tagFont = Font(10.5f);
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            using var faint = new SKPaint { Color = Pksm.Ink.WithAlpha(0xB8), IsAntialias = true };
            Text(c, "WONDER CARD", card.Left + 12, card.Top + 18.5f, SKTextAlign.Left, bandFont, ink);
            if (Current is not { } e)
            {
                Text(c, "No card selected", card.MidX, card.MidY, SKTextAlign.Center, title, soft);
                return;
            }
            Text(c, CardNumber(e.Gift), card.Right - 20, card.Top + 18.5f, SKTextAlign.Right, bandFont, faint);

            // Stage + art on the left, the printed title beside it.
            var stageR = Math.Min(40f, (card.Width - 24) * 0.2f);
            var center = new SKPoint(card.Left + 12 + stageR, card.Top + 32 + stageR);
            DrawStage(c, center, stageR);
            DrawGiftArt(c, new SKRect(center.X - stageR, center.Y - stageR, center.X + stageR, center.Y + stageR), e, _ctx.Sprites, Repaint, 0.96f);
            if (e.Gift.Shiny) DrawShinyStar(c, center.X + stageR * 0.78f, center.Y - stageR * 0.78f, 5.5f);
            if (e.Gift.Details?.Ball is > 0 and var ball && e.Gift.Kind == EventGiftKind.Pokemon)
            {
                var icon = _ctx.Sprites.GetBall(ball);
                if (icon is null) _ctx.Sprites.WarmBall(ball, Repaint);
                else c.DrawImage(ImageOf(icon), new SKRect(center.X + stageR - 16, center.Y + stageR - 16, center.X + stageR + 2, center.Y + stageR + 2), Pixel);
            }

            var tx = center.X + stageR + 10;
            var tw = card.Right - 12 - tx;
            var lines = Wrap(title, e.Title, tw, 3);
            var ty = card.Top + 44;
            foreach (var line in lines)
            {
                Text(c, line, tx, ty, SKTextAlign.Left, title, ink);
                ty += 17;
            }
            Text(c, Fit(small, Subject(e), tw), tx, ty + 2, SKTextAlign.Left, small, soft);

            // Tag row: language, region, status.
            var tagY = Math.Max(center.Y + stageR + 18, ty + 22);
            var x = card.Left + 12;
            x = DrawTag(c, x, tagY, e.LanguageTag is { } lang ? lang : "ALL LANG.", tagFont, Pksm.LogoDeck.WithAlpha(0xD0), Pksm.Ink) + 4;
            if (e.Source?.Region is { } region) x = DrawTag(c, x, tagY, region, tagFont, Pksm.LogoDeck.WithAlpha(0xD0), Pksm.Ink) + 4;
            if (e.Received) x = DrawTag(c, x, tagY, "Received", tagFont, PksmPaint.Tone(Pksm.Legal), Pksm.Ink) + 4;
            else if (!e.Compatible) x = DrawTag(c, x, tagY, "Other language", tagFont, PksmPaint.Tone(Pksm.Illegal), Pksm.Ink) + 4;
            else x = DrawTag(c, x, tagY, "New", tagFont, PksmPaint.Tone(Pksm.GiftRed), Pksm.Ink) + 4;

            // Printed facts, as many as fit.
            var facts = PreviewFacts(e).ToArray();
            var fy = tagY + 20;
            var labelW = 44f;
            foreach (var (label, value) in facts)
            {
                if (fy > card.Bottom - 10) break;
                Text(c, label, card.Left + 12, fy, SKTextAlign.Left, small, soft);
                Text(c, Fit(small, value, card.Right - 14 - (card.Left + 12 + labelW)), card.Left + 12 + labelW, fy, SKTextAlign.Left, small, ink);
                fy += 16;
            }
        }

        private static IEnumerable<(string, string)> PreviewFacts(WonderCardEntry e)
        {
            var d = e.Gift.Details;
            yield return ("Date", DateText(e));
            if (e.Gift.Kind == EventGiftKind.Item)
            {
                foreach (var item in d?.Items.Skip(1) ?? [])
                    yield return ("Also", item.Quantity > 1 ? $"{item.Name} x{item.Quantity}" : item.Name);
                yield break;
            }
            yield return ("OT", d is { OriginalTrainer.Length: > 0 } ? $"{d.OriginalTrainer}  ID {d.TrainerId}" : "Yours (your name & ID)");
            if (e.Source is { Name.Length: > 0 } s) yield return ("Event", GiftTitles.TranslateEventName(s.Name) is { Length: > 0 } n ? n : s.Name);
            if (d is { Moves.Count: > 0 }) yield return ("Moves", string.Join(", ", d.Moves));
            yield return ("Item", d is { HeldItemName.Length: > 0 } ? d.HeldItemName : "None");
            if (d is { BallName.Length: > 0 }) yield return ("Ball", d.BallName);
            if (d is { MetLocation.Length: > 0 }) yield return ("Met", d.MetLocation);
            if (e.Source is { Games.Length: > 0 } g) yield return ("Game", g.Games);
            if (e.Variants.Count > 1) yield return ("Copies", string.Join(" ", e.Variants.Select(v => v.LanguageTag ?? "?").Distinct()));
        }

        // ── Filters menu (Y) ─────────────────────────────────────────────────

        private async Task ShowFilterMenuAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                while (true)
                {
                    var q = _query;
                    var options = new List<PadOption>();
                    if (_ctx.Profile is not null)
                        options.Add(new PadOption(Check(q.CompatibleOnly) + "Fits this save", IconPath: "game"));
                    options.Add(new PadOption($"Gifts: {q.Kind switch { WonderCardKindFilter.Pokemon => "Pokémon", WonderCardKindFilter.Items => "Items", _ => "All" }}", IconPath: "events"));
                    options.Add(new PadOption(Check(q.ShinyOnly) + "Shiny only", IconPath: "shiny"));
                    options.Add(new PadOption($"Status: {q.Received switch { WonderCardReceivedFilter.NotReceived => "New only", WonderCardReceivedFilter.Received => "Received", _ => "Any" }}", IconPath: "check"));
                    options.Add(new PadOption($"Pokémon: {(q.Species is { } sp ? SpeciesName(_ctx.Data, sp) : "Any")}", IconPath: "pokedex"));
                    options.Add(new PadOption($"Year: {q.Year?.ToString() ?? "Any"}", IconPath: "calendar"));
                    options.Add(new PadOption($"Language: {q.Language ?? "Any"}", IconPath: "info"));
                    options.Add(new PadOption($"Event: {(q.Series is { Length: > 14 } lengthy ? lengthy[..13] + "…" : q.Series ?? "Any")}", IconPath: "ribbons"));
                    options.Add(new PadOption(Check(q.OnePerEvent) + "One card per event", IconPath: "compact"));
                    options.Add(new PadOption($"Sort: {SortLabel(q.Sort)}", IconPath: "sort"));
                    options.Add(new PadOption($"Group: {GroupLabel(q.Grouping)}", IconPath: "blocks"));
                    if (q.ActiveFilterCount > 0 || q.Search.Length > 0)
                        options.Add(new PadOption("Clear all filters", IconPath: "clear"));
                    options.Add(new PadOption($"Clear marks ({_ctx.History.Count})", IconPath: "delete"));

                    var choice = await PadMenu.ShowAsync(_ctx.Host, "Filter cards",
                        $"{_page.Items.Count} of {_page.Total} cards shown. Filters never block receiving.", options.ToArray());
                    if (choice is null) return;
                    var label = choice.StartsWith("✓ ", StringComparison.Ordinal) ? choice[2..] : choice;
                    switch (label)
                    {
                        case "Fits this save": Set(q with { CompatibleOnly = !q.CompatibleOnly }); break;
                        case "Shiny only": Set(q with { ShinyOnly = !q.ShinyOnly }); break;
                        case "One card per event": Set(q with { OnePerEvent = !q.OnePerEvent }); break;
                        case "Clear all filters":
                            _search.Text = "";
                            Set(new WonderCardQuery { Sort = q.Sort, Grouping = q.Grouping, OnePerEvent = q.OnePerEvent });
                            break;
                        case { } s when s.StartsWith("Gifts:", StringComparison.Ordinal):
                            await PickAsync("Gifts", [(0, "All gifts"), (1, "Pokémon"), (2, "Items")], (int)q.Kind,
                                id => q with { Kind = (WonderCardKindFilter)id });
                            break;
                        case { } s when s.StartsWith("Status:", StringComparison.Ordinal):
                            await PickAsync("Status", [(0, "Any"), (1, "New (not received)"), (2, "Received")], (int)q.Received,
                                id => q with { Received = (WonderCardReceivedFilter)id });
                            break;
                        case { } s when s.StartsWith("Pokémon:", StringComparison.Ordinal): await PickSpeciesAsync(); break;
                        case { } s when s.StartsWith("Year:", StringComparison.Ordinal): await PickYearAsync(); break;
                        case { } s when s.StartsWith("Language:", StringComparison.Ordinal): await PickLanguageAsync(); break;
                        case { } s when s.StartsWith("Event:", StringComparison.Ordinal): await PickSeriesAsync(); break;
                        case { } s when s.StartsWith("Sort:", StringComparison.Ordinal):
                            await PickAsync("Sort", [(0, "Newest first"), (1, "Oldest first"), (2, "Card number"), (3, "Pokédex number"), (4, "Title A-Z"), (5, "Level (high first)")],
                                (int)q.Sort, id => q with { Sort = (WonderCardSort)id });
                            break;
                        case { } s when s.StartsWith("Group:", StringComparison.Ordinal):
                            await PickAsync("Group by", [(0, "Year"), (1, "Event series"), (2, "Game"), (3, "Pokémon"), (4, "No groups")],
                                (int)q.Grouping, id => q with { Grouping = (WonderCardGrouping)id });
                            break;
                        case { } s when s.StartsWith("Clear marks", StringComparison.Ordinal):
                            await ClearHistoryAsync();
                            break;
                    }
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private static string Check(bool on) => on ? "✓ " : "";

        private async Task PickAsync(string title, (int Id, string Name)[] choices, int current, Func<int, WonderCardQuery> apply)
        {
            var picked = await PickerMenu.ShowAsync(_ctx.Host, title, choices.Select(c => new PickItem(c.Id, c.Name)).ToArray(), currentId: current);
            if (picked is not null) Set(apply(picked.Id));
        }

        private async Task PickSpeciesAsync()
        {
            var items = _all.Where(e => e.Gift.Kind == EventGiftKind.Pokemon)
                .GroupBy(e => e.Gift.Species).OrderBy(g => g.Key)
                .Select(g => new PickItem(g.Key, $"{SpeciesName(_ctx.Data, g.Key)}", Detail: $"No. {g.Key:000} · {g.Count()} cards"))
                .Prepend(new PickItem(0, "Any Pokémon")).ToArray();
            var picked = await PickerMenu.ShowAsync(_ctx.Host, "Pokémon", items, currentId: _query.Species ?? 0);
            if (picked is not null) Set(_query with { Species = picked.Id == 0 ? null : picked.Id });
        }

        private async Task PickYearAsync()
        {
            var items = _all.Where(e => e.Year is not null).GroupBy(e => e.Year!.Value).OrderByDescending(g => g.Key)
                .Select(g => new PickItem(g.Key, g.Key.ToString(), Detail: $"{g.Count()} cards"))
                .Prepend(new PickItem(0, "Any year")).ToArray();
            var picked = await PickerMenu.ShowAsync(_ctx.Host, "Year", items, currentId: _query.Year ?? 0);
            if (picked is not null) Set(_query with { Year = picked.Id == 0 ? null : picked.Id });
        }

        private async Task PickLanguageAsync()
        {
            var tags = _all.Select(e => e.LanguageTag).Where(t => t is not null).Distinct().Order().ToArray();
            var items = tags.Select((t, i) => new PickItem(i + 1, GiftLanguages.DisplayName(t!), Detail: $"{t} · {_all.Count(e => e.LanguageTag == t)} cards"))
                .Prepend(new PickItem(0, "Any language")).ToArray();
            var current = _query.Language is { } l ? Array.IndexOf(tags, l) + 1 : 0;
            var picked = await PickerMenu.ShowAsync(_ctx.Host, "Language", items, currentId: current);
            if (picked is not null) Set(_query with { Language = picked.Id == 0 ? null : tags[picked.Id - 1] });
        }

        private async Task PickSeriesAsync()
        {
            var series = _all.GroupBy(e => e.Series, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
                .Select(g => (Name: g.Key, Count: g.Count())).ToArray();
            var items = series.Select((s, i) => new PickItem(i + 1, s.Name, Detail: $"{s.Count} cards"))
                .Prepend(new PickItem(0, "Any event")).ToArray();
            var current = _query.Series is { } cur ? Array.FindIndex(series, s => string.Equals(s.Name, cur, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
            var picked = await PickerMenu.ShowAsync(_ctx.Host, "Event series", items, currentId: current);
            if (picked is not null) Set(_query with { Series = picked.Id == 0 ? null : series[picked.Id - 1].Name });
        }

        private async Task ClearHistoryAsync()
        {
            if (_ctx.History.Count == 0) return;
            var confirmed = await PadMenu.ConfirmAsync(_ctx.Host, "Forget received marks",
                $"Forget all {_ctx.History.Count} recorded gifts? The marks disappear; receiving again still works.", "Forget");
            if (!confirmed) return;
            _ctx.History.Clear();
            _all = _all.Select(e => e with { Received = false }).ToArray();
            Requery(keep: Current);
        }

        // ── Pad ──────────────────────────────────────────────────────────────

        public bool OnPadButton(PadButton button)
        {
            if (_busy) return true;
            switch (button)
            {
                case PadButton.Up: Select(_index - 1); return true;
                case PadButton.Down: Select(_index + 1); return true;
                case PadButton.Left: Select(_index - Math.Max(1, (int)(_viewH / (RowH + RowGap)) - 1)); return true;
                case PadButton.Right: Select(_index + Math.Max(1, (int)(_viewH / (RowH + RowGap)) - 1)); return true;
                case PadButton.L: JumpSection(-1); return true;
                case PadButton.R: JumpSection(1); return true;
                case PadButton.A: OpenCard(); return true;
                case PadButton.X: FocusSearch(); return true;
                case PadButton.Y: _ = ShowFilterMenuAsync(); return true;
                case PadButton.Start: CycleSort(); return true;
                case PadButton.Select: CycleGrouping(); return true;
                case PadButton.B:
                    if (_search.IsFocused) { _search.Unfocus(); return true; }
                    Close(null);
                    return true;
                default: return true; // the album owns the pad while open
            }
        }

        private void FocusSearch()
        {
            if (_search.IsFocused) _search.Unfocus();
            else _search.Focus();
        }

        private async void OpenCard()
        {
            if (_busy || Current is not { } entry) return;
            _busy = true;
            _search.Unfocus();
            try
            {
                var receive = await WonderCardSheet.ShowAsync(_ctx, entry);
                if (receive is not null) Close(receive);
                else Repaint();
            }
            finally
            {
                _busy = false;
            }
        }

        private void Close(WonderCardEntry? result)
        {
            _searchDebounce?.Cancel();
            _router?.Remove(this);
            _ctx.Host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }

    // ── The wonder card: the full printed card with its actions ────────────────

    private sealed class WonderCardSheet : IPadHandler
    {
        private readonly TaskCompletionSource<WonderCardEntry?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AlbumContext _ctx;
        private readonly WonderCardEntry _entry;
        private readonly IReadOnlyList<WonderCardEntry> _variants;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly SKCanvasView _card;
        private readonly SKCanvasView _facts;
        private readonly Button _receive;
        private readonly Button _bank;
        private readonly Button _exportCard;
        private readonly Button _exportPk;
        private readonly Label _note;
        private int _variant;
        private bool _busy;

        public static Task<WonderCardEntry?> ShowAsync(AlbumContext ctx, WonderCardEntry entry) =>
            new WonderCardSheet(ctx, entry)._result.Task;

        private WonderCardEntry Shown => _variants[_variant];

        private WonderCardSheet(AlbumContext ctx, WonderCardEntry entry)
        {
            _ctx = ctx;
            _entry = entry;
            _variants = entry.Variants.Count > 0 ? entry.Variants : [entry];
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            var host = ctx.Host;
            var maxW = host.Width > 0 ? host.Width - 24 : 616;
            var maxH = host.Height > 0 ? host.Height - 16 : 344;

            _card = new SKCanvasView { InputTransparent = true };
            _card.PaintSurface += PaintCard;
            _facts = new SKCanvasView { InputTransparent = true };
            _facts.PaintSurface += PaintFacts;

            _receive = Kit.Capsule("Receive", UiTokens.Green, primary: true, icon: "events");
            _receive.Clicked += (_, _) => Receive();
            _bank = Kit.Capsule("To Bank", UiTokens.Ink1, icon: "bank");
            _bank.Clicked += (_, _) => _ = SendToBankAsync();
            _exportCard = Kit.Capsule("Card file", UiTokens.Ink1, icon: "export");
            _exportCard.Clicked += (_, _) => _ = ExportAsync(entity: false);
            _exportPk = Kit.Capsule(".pk file", UiTokens.Ink1, icon: "export");
            _exportPk.Clicked += (_, _) => _ = ExportAsync(entity: true);
            _note = new Label
            {
                FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.Ink1,
                VerticalTextAlignment = TextAlignment.Center, HorizontalOptions = LayoutOptions.End,
                LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2,
            };

            var actions = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
                Children = { _receive, _bank, _exportCard, _exportPk, _note },
            };
            Grid.SetColumn(_bank, 1);
            Grid.SetColumn(_exportCard, 2);
            Grid.SetColumn(_exportPk, 3);
            Grid.SetColumn(_note, 4);

            var body = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = [new(new GridLength(maxW * 0.56)), new(GridLength.Star)],
                Children = { _card, _facts },
            };
            Grid.SetColumn(_facts, 1);

            var itemCard = Shown.Gift.Kind == EventGiftKind.Item;
            var hintList = new List<(string, string, Action?)> { ("A", itemCard ? "Add to bag" : "Receive", Receive) };
            if (!itemCard) hintList.Add(("X", "To Bank", () => _ = SendToBankAsync()));
            hintList.Add(("Y", "Export", () => _ = ExportMenuAsync()));
            if (_variants.Count > 1) hintList.Add(("LR", "Language", null));
            hintList.Add(("B", "Close", () => Close(null)));
            var hints = Kit.WindowHints([.. hintList]);

            var content = new Grid
            {
                RowSpacing = 8,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
                Children = { body, actions, hints },
            };
            Grid.SetRow(actions, 1);
            Grid.SetRow(hints, 2);

            var window = new Border
            {
                BackgroundColor = UiTokens.GiftPink,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Shadow = Kit.HardShadow(),
                Padding = new Thickness(12, 10),
                Content = new Grid { Children = { GiftBackdrop(), content } },
                WidthRequest = maxW,
                HeightRequest = maxH,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };

            var scrim = new BoxView { Color = UiTokens.Scrim };
            var scrimTap = new TapGestureRecognizer();
            scrimTap.Tapped += (_, _) => Close(null);
            scrim.GestureRecognizers.Add(scrimTap);
            _overlay = new Grid { Children = { scrim, window } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

            RefreshActions();
            _router?.Push(this);
            Kit.AnimateIn(window);
        }

        private bool Blocked => HardcoreMode.Blocks(SaveAction.InjectEvent, out _);

        private void RefreshActions()
        {
            var gift = Shown.Gift;
            var item = gift.Kind == EventGiftKind.Item;
            _receive.Text = item ? "Add to bag"
                : _ctx.TargetSlot < 0 ? "Box is full"
                : _ctx.SlotChosen ? $"Receive into slot {_ctx.TargetSlot + 1}"
                : _ctx.ViewModel.BoxIndex < 0 ? "Receive into party" : $"Receive into Box {_ctx.ViewModel.BoxIndex + 1}";
            _receive.IsEnabled = !Blocked && (item || _ctx.TargetSlot >= 0);
            _bank.IsVisible = !item;
            _exportPk.IsVisible = !item;
            _bank.IsEnabled = !Blocked;
            _exportPk.IsEnabled = !Blocked;
            _note.Text = Blocked ? "Hardcore mode: receiving is off."
                : !item && _ctx.TargetSlot < 0 ? "Free a slot in this box to receive."
                : Shown.Received ? "Already received once - receiving again still works."
                : !Shown.Compatible ? "Card is for another language; it still converts."
                : "";
            _card.InvalidateSurface();
            _facts.InvalidateSurface();
        }

        private void Repaint() => MainThread.BeginInvokeOnMainThread(() =>
        {
            _card.InvalidateSurface();
            _facts.InvalidateSurface();
        });

        private void PaintCard(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            var e = Shown;
            var card = new SKRect(0, 0, size.Width - 4, size.Height - 4);
            DrawCardBody(c, card, 24);
            using var bandFont = Font(13f);
            using var titleFont = Font(17f);
            using var body = Font(12.5f);
            using var tagFont = Font(11f);
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            Text(c, "WONDER CARD", card.Left + 14, card.Top + 21, SKTextAlign.Left, bandFont, ink);
            Text(c, CardNumber(e.Gift), card.Right - 24, card.Top + 21, SKTextAlign.Right, bandFont, ink);

            var stageR = Math.Min(Math.Min(card.Height * 0.27f, card.Width * 0.19f), 80f);
            var center = new SKPoint(card.Left + 16 + stageR, card.Top + 38 + stageR);
            DrawStage(c, center, stageR);
            DrawGiftArt(c, new SKRect(center.X - stageR, center.Y - stageR, center.X + stageR, center.Y + stageR), e, _ctx.Sprites, Repaint, 1.02f);
            if (e.Gift.Shiny) DrawShinyStar(c, center.X + stageR * 0.8f, center.Y - stageR * 0.8f, 7f);

            // Title block beside the stage.
            var tx = center.X + stageR + 12;
            var tw = card.Right - 14 - tx;
            var ty = card.Top + 52;
            foreach (var line in Wrap(titleFont, e.Title, tw, 3))
            {
                Text(c, line, tx, ty, SKTextAlign.Left, titleFont, ink);
                ty += 20;
            }
            Text(c, Fit(body, Subject(e), tw), tx, ty + 2, SKTextAlign.Left, body, soft);
            ty += 20;
            if (e.TitleTranslated && e.OriginalTitle.Length > 0)
            {
                // The printed title in its own script, small, with its language tag.
                var tagRight = DrawTag(c, tx, ty + 2, e.LanguageTag ?? "?", tagFont, Pksm.LogoDeck.WithAlpha(0xD0), Pksm.InkSoft);
                using var cjk = new SKFont(CjkFace, 11.5f) { Edging = SKFontEdging.Antialias };
                Text(c, Fit(cjk, e.OriginalTitle, card.Right - 14 - tagRight - 5), tagRight + 5, ty + 2, SKTextAlign.Left, cjk, soft);
            }

            // Distribution strip across the bottom: event, region, games, date.
            var stripTop = Math.Max(center.Y + stageR + 12, ty + 14);
            var strip = new SKRect(card.Left + 10, stripTop, card.Right - 10, card.Bottom - 10);
            if (strip.Height > 30)
            {
                using (var well = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0x70), IsAntialias = true })
                    c.DrawRoundRect(strip, 5, 5, well);
                var y = strip.Top + 18;
                var x = strip.Left + 10;
                x = DrawTag(c, x, y, e.LanguageTag is { } lang ? GiftLanguages.DisplayName(lang) : "All languages", tagFont, Pksm.LogoDeck, Pksm.Ink) + 5;
                if (e.Source?.Region is { } region) x = DrawTag(c, x, y, region, tagFont, Pksm.LogoDeck, Pksm.Ink) + 5;
                if (e.Source?.Games is { Length: > 0 } games) x = DrawTag(c, x, y, games, tagFont, Pksm.LogoDeck, Pksm.Ink) + 5;
                if (e.Received) DrawTag(c, x, y, "Received", tagFont, PksmPaint.Tone(Pksm.Legal), Pksm.Ink);
                else DrawTag(c, x, y, "New", tagFont, PksmPaint.Tone(Pksm.GiftRed), Pksm.Ink);
                y += 20;
                var eventName = e.Source is { } s ? (GiftTitles.TranslateEventName(s.Name) is { Length: > 0 } n ? n : s.Name) : e.Series;
                if (y < strip.Bottom - 4)
                    Text(c, Fit(body, $"Event: {eventName}", strip.Width - 20), strip.Left + 10, y, SKTextAlign.Left, body, ink);
                y += 17;
                if (y < strip.Bottom - 4)
                    Text(c, Fit(body, $"Distributed: {DateText(e)}", strip.Width - 20), strip.Left + 10, y, SKTextAlign.Left, body, soft);
                y += 17;
                if (_variants.Count > 1 && y < strip.Bottom - 4)
                {
                    var langs = string.Join(" ", _variants.Select(v => v.LanguageTag ?? "?"));
                    Text(c, Fit(body, $"Copies: {langs}  (L/R)", strip.Width - 20), strip.Left + 10, y, SKTextAlign.Left, body, soft);
                }
            }
        }

        private void PaintFacts(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            var size = BeginDp(c, args.Info);
            var panel = new SKRect(0, 0, size.Width - 4, size.Height - 4);
            PksmPaint.Panel(c, panel, Pksm.PaperShade);
            using var head = Font(12.5f);
            using var body = Font(12.5f);
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
            var e = Shown;
            var d = e.Gift.Details ?? new EventGiftDetails();
            var y = panel.Top + 8;
            var left = panel.Left + 10;
            var right = panel.Right - 10;

            void Header(string text)
            {
                var r = new SKRect(left - 4, y, right + 4, y + 20);
                PksmPaint.HeaderStrip(c, r, text, head, Pksm.GiftPinkLight);
                y += 26;
            }
            void Row(string label, string value)
            {
                if (y > panel.Bottom - 6) return;
                Text(c, label, left, y + 11, SKTextAlign.Left, body, soft);
                Text(c, Fit(body, value, right - left - 62), left + 62, y + 11, SKTextAlign.Left, body, ink);
                y += 18;
            }

            if (e.Gift.Kind == EventGiftKind.Item)
            {
                Header("Items");
                foreach (var item in d.Items)
                {
                    if (y > panel.Bottom - 22) break;
                    var box = new SKRect(left, y - 2, left + 18, y + 16);
                    if (ItemIcons.Get(item.Name, Repaint) is { } icon) c.DrawImage(ImageOf(icon), box, Pixel);
                    else DrawPresent(c, box, 18);
                    Text(c, Fit(body, item.Name, right - left - 70), left + 24, y + 11, SKTextAlign.Left, body, ink);
                    Text(c, $"x{item.Quantity}", right, y + 11, SKTextAlign.Right, body, soft);
                    y += 20;
                }
                y += 4;
                Header("Card");
                Row("Goes to", "The bag");
                Row("Card No.", $"{e.Gift.CardId:0000} · {d.Format}");
                Row("Status", e.Received ? "Received before" : "Never received");
                return;
            }

            Header("Pokémon");
            Row("OT", d.OriginalTrainer.Length > 0 ? d.OriginalTrainer : "Yours");
            Row("ID No.", d.TrainerId.Length > 0 ? d.TrainerId : "Yours");
            Row("Ball", d.BallName.Length > 0 ? d.BallName : "—");
            Row("Item", d.HeldItemName.Length > 0 ? d.HeldItemName : "None");
            Row("Met", d.MetLocation.Length > 0 ? d.MetLocation : "—");
            if (d.IsEgg) Row("Hatch", "Arrives as an Egg");

            if (d.Moves.Count > 0 && y < panel.Bottom - 40)
            {
                y += 2;
                Header("Moves");
                // Two columns of move pills.
                var colW = (right - left - 6) / 2;
                for (var i = 0; i < d.Moves.Count; i++)
                {
                    var col = i % 2;
                    var r = new SKRect(left + col * (colW + 6), y, left + col * (colW + 6) + colW, y + 20);
                    if (r.Bottom > panel.Bottom - 4) break;
                    PksmPaint.BlackButton(c, r, 4);
                    Text(c, Fit(body, d.Moves[i], colW - 10), r.MidX, r.MidY + body.Size * 0.36f, SKTextAlign.Center, body, ink);
                    if (col == 1 || i == d.Moves.Count - 1) y += 24;
                }
            }
            CardSection();

            void CardSection()
            {
                if (y > panel.Bottom - 60) return;
                y += 2;
                Header("Card");
                Row("Card No.", $"{e.Gift.CardId:0000} · {d.Format}");
                Row("Status", e.Received ? "Received before" : "Never received");
                if (_variants.Count > 1) Row("Copies", string.Join(" ", _variants.Select(v => v.LanguageTag ?? "?")));
                else Row("Language", e.LanguageTag is { } l ? GiftLanguages.DisplayName(l) : "Every language");
            }
        }

        public bool OnPadButton(PadButton button)
        {
            if (_busy) return true;
            switch (button)
            {
                case PadButton.A: Receive(); return true;
                case PadButton.X: _ = SendToBankAsync(); return true;
                case PadButton.Y: _ = ExportMenuAsync(); return true;
                case PadButton.L: SwitchVariant(-1); return true;
                case PadButton.R: SwitchVariant(1); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private void SwitchVariant(int direction)
        {
            if (_variants.Count < 2) return;
            _variant = (_variant + direction + _variants.Count) % _variants.Count;
            RefreshActions();
        }

        private void Receive()
        {
            if (HardcoreMode.Blocks(SaveAction.InjectEvent, out var status))
            {
                _note.Text = status;
                return;
            }
            if (Shown.Gift.Kind == EventGiftKind.Pokemon && _ctx.TargetSlot < 0) return;
            Close(Shown);
        }

        private async Task SendToBankAsync()
        {
            if (_busy || Shown.Gift.Kind != EventGiftKind.Pokemon) return;
            // A Bank copy fabricates a mon just like receiving does: Hardcore refuses both.
            if (HardcoreMode.Blocks(SaveAction.InjectEvent, out var status))
            {
                _note.Text = status;
                return;
            }
            var services = IPlatformApplication.Current?.Services;
            var bank = services?.GetService<IBankService>();
            var engine = services?.GetService<ISaveEngine>();
            if (bank is null || engine is null) return;
            _busy = true;
            try
            {
                var gift = Shown.Gift;
                var added = await Task.Run(() =>
                {
                    var export = _ctx.Service.ExportEntity(_ctx.Session, gift.Id);
                    if (export is null) return null;
                    var info = engine.TryDescribeEntity(export.Data, $"Mystery Gift · {Shown.Title}", export.Format);
                    return info is null ? null : bank.Add(export.Data, info);
                });
                _note.Text = added is null ? "This card could not be converted for the Bank." : $"Sent to the Bank (Box {added.Box + 1}).";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task ExportMenuAsync()
        {
            if (_busy) return;
            if (Shown.Gift.Kind == EventGiftKind.Item)
            {
                await ExportAsync(entity: false);
                return;
            }
            _busy = true;
            string? choice;
            try
            {
                choice = await PadMenu.ShowAsync(_ctx.Host, "Export", "Share the wonder card itself, or the Pokémon it gives as a .pk file.",
                    new PadOption("Card file", IconPath: "events"), new PadOption("Pokémon (.pk)", IconPath: "export"));
            }
            finally
            {
                _busy = false;
            }
            if (choice == "Card file") await ExportAsync(entity: false);
            else if (choice == "Pokémon (.pk)") await ExportAsync(entity: true);
        }

        private async Task ExportAsync(bool entity)
        {
            if (_busy) return;
            if (HardcoreMode.Blocks(entity ? SaveAction.InjectEvent : SaveAction.ExportFile, out var status))
            {
                _note.Text = status;
                return;
            }
            _busy = true;
            try
            {
                var gift = Shown.Gift;
                var export = await Task.Run(() => entity ? _ctx.Service.ExportEntity(_ctx.Session, gift.Id) : _ctx.Service.ExportCard(_ctx.Session, gift.Id));
                if (export is null)
                {
                    _note.Text = "Nothing to export for this card.";
                    return;
                }
                var path = System.IO.Path.Combine(FileSystem.CacheDirectory, export.FileName);
                await File.WriteAllBytesAsync(path, export.Data);
                await Share.Default.RequestAsync(new ShareFileRequest { Title = Shown.Title, File = new ShareFile(path) });
                _note.Text = $"Exported {export.FileName}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _note.Text = $"Export failed: {error.Message}";
            }
            finally
            {
                _busy = false;
            }
        }

        private void Close(WonderCardEntry? receive)
        {
            _router?.Remove(this);
            _ctx.Host.Remove(_overlay);
            _result.TrySetResult(receive);
        }
    }

    // ── The gift world backdrop ──────────────────────────────────────────────────

    /// <summary>
    /// The mystery-gift backdrop: the gift-plum field scattered with fixed white 4-point
    /// sparkles (deterministic positions, no animation). Prerendered per size.
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
}
