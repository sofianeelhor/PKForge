using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The shared info vocabulary of every editor and picker: type badges in the official
/// type colours, the physical/special/status category icon, a stat old→new delta grid,
/// base-stat bars, tag chips, detail lines and the info card they sit on. One look,
/// defined once - screens compose these instead of styling their own.
/// </summary>
public static class InfoKit
{
    // ---------- Type badge ----------

    /// <summary>Tera Stellar has no type colour of its own; the games draw it prismatic.</summary>
    private static readonly Color StellarColor = Color.FromArgb("#3E8FB0");

    public static Color TypeColor(int type) => type == TypeFacts.Stellar ? StellarColor : TypePalette.ForType(type);

    private static Color TypeInk(int type) => type == TypeFacts.Stellar ? Colors.White : TypePalette.ForegroundForType(type);

    /// <summary>A fixed-width type pill ("FIRE") so badge columns line up down a list.</summary>
    public static Border TypeBadge(int? type = null, double width = 60)
    {
        // The games' rectangular type plate: solid fill, square corners, pixel caps, no outline.
        var label = new Label
        {
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextSmall,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };
        var badge = new Border
        {
            WidthRequest = width,
            HeightRequest = 20,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            Padding = new Thickness(2, 0),
            VerticalOptions = LayoutOptions.Center,
            Content = label,
            InputTransparent = true,
        };
        SetType(badge, type);
        return badge;
    }

    /// <summary>Re-points a badge made by <see cref="TypeBadge"/> (recycled list rows); null hides it.</summary>
    public static void SetType(Border badge, int? type)
    {
        badge.IsVisible = type is { } t && TypeFacts.IsValid(t);
        if (type is not { } value || !badge.IsVisible) return;
        // The summary's type plate: the type colour with a gentle top light and a darker 1 dp edge.
        var fill = TypeColor(value);
        badge.Background = new LinearGradientBrush(
            [new GradientStop(fill.WithLuminosity(Math.Min(1, fill.GetLuminosity() + 0.06f)), 0f), new GradientStop(fill.WithLuminosity(Math.Max(0, fill.GetLuminosity() - 0.04f)), 1f)],
            new Point(0, 0), new Point(0, 1));
        badge.Stroke = fill.WithLuminosity(Math.Max(0, fill.GetLuminosity() - 0.2f));
        if (badge.Content is Label label)
        {
            label.Text = TypeFacts.Name(value).ToUpperInvariant();
            label.TextColor = TypeInk(value);
        }
    }

    /// <summary>One or two type badges side by side (a species' typing).</summary>
    public static View TypeRow(IReadOnlyList<int> types, double width = 60)
    {
        var row = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        foreach (var type in types) row.Children.Add(TypeBadge(type, width));
        return row;
    }

    // ---------- Category icon ----------

    /// <summary>The games' damage-class icon: red-orange burst (physical), blue rings (special), grey orb (status).</summary>
    public sealed class CategoryIcon : SKCanvasView
    {
        private MoveCategory? _category;

        public CategoryIcon(MoveCategory? category = null)
        {
            WidthRequest = 24;
            HeightRequest = 16;
            InputTransparent = true;
            VerticalOptions = LayoutOptions.Center;
            _category = category;
            IsVisible = category is not null;
            PaintSurface += (_, args) => Draw(args.Surface.Canvas, args.Info);
        }

        public MoveCategory? Category
        {
            get => _category;
            set
            {
                if (_category == value) return;
                _category = value;
                IsVisible = value is not null;
                InvalidateSurface();
            }
        }

        private void Draw(SKCanvas c, SKImageInfo info)
        {
            c.Clear(SKColors.Transparent);
            if (_category is not { } category) return;
            DrawCategory(c, new SKRect(1, 1, info.Width - 1, info.Height - 1), category);
        }

        /// <summary>Paints the damage-class icon into <paramref name="r"/> (Skia-drawn surfaces share it).</summary>
        public static void DrawCategory(SKCanvas c, SKRect r, MoveCategory category)
        {
            var (body, edge, glyph) = category switch
            {
                MoveCategory.Physical => (new SKColor(0xC9, 0x2A, 0x19), new SKColor(0x7E, 0x16, 0x0B), new SKColor(0xFF, 0xC4, 0x3A)),
                MoveCategory.Special => (new SKColor(0x3B, 0x58, 0xA8), new SKColor(0x1F, 0x2F, 0x66), new SKColor(0x9C, 0xD4, 0xFF)),
                _ => (new SKColor(0x8C, 0x88, 0x8C), new SKColor(0x55, 0x52, 0x55), new SKColor(0xF4, 0xF4, 0xF4)),
            };
            var radius = r.Height * 0.3f;
            using var fill = new SKPaint { Color = body, IsAntialias = true };
            using var rim = new SKPaint { Color = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, r.Height / 16f) };
            using var ink = new SKPaint { Color = glyph, IsAntialias = true };
            c.DrawRoundRect(r, radius, radius, fill);
            c.DrawRoundRect(r, radius, radius, rim);
            var cx = r.MidX;
            var cy = r.MidY;
            var s = r.Height * 0.36f;
            switch (category)
            {
                case MoveCategory.Physical:
                {
                    // Eight-point impact burst.
                    using var path = new SKPath();
                    for (var i = 0; i < 16; i++)
                    {
                        var angle = i * MathF.PI / 8 - MathF.PI / 2;
                        var len = i % 2 == 0 ? s * 1.35f : s * 0.55f;
                        var p = new SKPoint(cx + MathF.Cos(angle) * len, cy + MathF.Sin(angle) * len);
                        if (i == 0) path.MoveTo(p); else path.LineTo(p);
                    }
                    path.Close();
                    c.DrawPath(path, ink);
                    break;
                }
                case MoveCategory.Special:
                {
                    using var ring = new SKPaint { Color = glyph, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.2f, s * 0.32f) };
                    c.DrawCircle(cx, cy, s * 1.15f, ring);
                    c.DrawCircle(cx, cy, s * 0.35f, ink);
                    break;
                }
                default:
                {
                    // Two interlocking halves, the status orb.
                    c.DrawCircle(cx, cy, s * 1.1f, ink);
                    using var shade = new SKPaint { Color = body, IsAntialias = true };
                    c.DrawCircle(cx + s * 0.35f, cy, s * 0.55f, shade);
                    c.DrawCircle(cx - s * 0.35f, cy, s * 0.22f, shade);
                    break;
                }
            }
        }
    }

    // ---------- Text ----------

    /// <summary>Card heading: pixel voice, 14 dp, one line.</summary>
    public static Label Heading(string? text = null) => new()
    {
        Text = text,
        FontFamily = DsChrome.PixelFont,
        FontSize = UiTokens.TextLabel,
        TextColor = UiTokens.Ink0,
        LineBreakMode = LineBreakMode.TailTruncation,
    };

    /// <summary>A quiet one-line fact under a name ("Pow 90 · Acc 100 · PP 15").</summary>
    public static Label DetailLine(string? text = null, int maxLines = 1) => new()
    {
        Text = text,
        FontSize = UiTokens.TextSmall,
        TextColor = UiTokens.InkSoft,
        MaxLines = maxLines,
        LineBreakMode = maxLines == 1 ? LineBreakMode.TailTruncation : LineBreakMode.WordWrap,
        IsVisible = !string.IsNullOrEmpty(text),
    };

    /// <summary>Body prose inside a card (effects, descriptions): wraps, never truncates.</summary>
    public static Label Body(string? text = null) => new()
    {
        Text = text,
        FontSize = UiTokens.TextSmall,
        TextColor = UiTokens.Ink0,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    /// <summary>A warning/notice line in the card's accent (good = green, bad = red, info = blue).</summary>
    public static Label Note(string? text = null, NoteTone tone = NoteTone.Info) => new()
    {
        Text = text,
        FontSize = UiTokens.TextSmall,
        TextColor = ToneColor(tone),
        LineBreakMode = LineBreakMode.WordWrap,
        IsVisible = !string.IsNullOrEmpty(text),
    };

    public enum NoteTone { Info, Good, Bad }

    public static Color ToneColor(NoteTone tone) => tone switch
    {
        NoteTone.Good => UiTokens.Green,
        NoteTone.Bad => UiTokens.Bad,
        _ => UiTokens.Blueprint,
    };

    /// <summary>
    /// A row's source or slot ("Lv 32", "TM", "Hidden"): plain coloured pixel text on the
    /// right - no outline pill, no tinted box. The colour carries the meaning.
    /// </summary>
    public static Border Tag(string? text = null, Color? accent = null)
    {
        var tag = new Border
        {
            BackgroundColor = Colors.Transparent,
            StrokeThickness = 0,
            Padding = new Thickness(2, 0),
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
            Content = new Label
            {
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall + 0.5,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalTextAlignment = TextAlignment.Center,
            },
        };
        SetTag(tag, text, accent);
        return tag;
    }

    public static void SetTag(Border tag, string? text, Color? accent = null)
    {
        tag.IsVisible = !string.IsNullOrEmpty(text);
        if (tag.Content is Label label)
        {
            label.Text = Kit.Tidy(text);
            label.TextColor = UiTokens.TextTone(accent ?? UiTokens.Blueprint);
        }
    }

    /// <summary>Colour per learn source: level-up blue, machines gold, tutors violet, eggs green, the rest grey.</summary>
    public static Color LearnColor(LearnKind kind) => kind switch
    {
        LearnKind.LevelUp or LearnKind.Evolution => UiTokens.Blueprint,
        LearnKind.Machine => Color.FromArgb("#E2B64A"),
        LearnKind.Tutor => Color.FromArgb("#B294E8"),
        LearnKind.Egg => UiTokens.Green,
        _ => UiTokens.InkSoft,
    };

    // ---------- Numbers ----------

    /// <summary>"90", or an em dash for moves without fixed power.</summary>
    public static string Power(int power) => power > 0 ? power.ToString() : "—";

    /// <summary>"100", or an em dash for moves that never miss.</summary>
    public static string Accuracy(int accuracy) => accuracy > 0 ? accuracy.ToString() : "—";

    /// <summary>The compact move numbers line every move surface uses.</summary>
    public static string MoveNumbers(MoveChoice move) =>
        $"Pow {Power(move.Power)} · Acc {Accuracy(move.Accuracy)} · PP {move.PP}";

    // ---------- Card ----------

    /// <summary>
    /// The info block inside a panel: a flat recessed well (darker navy, no border) - never
    /// a card inside a card.
    /// </summary>
    public static Border Card(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = UiTokens.Space1 };
        foreach (var child in children) stack.Children.Add(child);
        var well = Kit.Well(stack);
        well.Padding = new Thickness(10, 8);
        return well;
    }

    /// <summary>A heading on the left with a trailing view (badges) on the right.</summary>
    public static Grid HeaderRow(View left, View right)
    {
        var grid = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { left, right },
        };
        Grid.SetColumn(right, 1);
        return grid;
    }

    // ---------- Stat delta grid ----------

    /// <summary>
    /// Six stats as old→new with ▲/▼ deltas in a 3 x 2 grid (six columns do not fit a
    /// 360dp phone). The nature, EV/IV and level previews all render through this.
    /// </summary>
    public sealed class StatDeltaGrid : Grid
    {
        private readonly Label[] _cells = new Label[6];

        public StatDeltaGrid()
        {
            ColumnSpacing = 6;
            RowSpacing = 4;
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)];
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)];
            for (var i = 0; i < 6; i++)
            {
                var caption = new Label
                {
                    FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft,
                    Text = NatureFacts.StatNames[i],
                };
                _cells[i] = new Label { FontSize = UiTokens.TextBody, LineBreakMode = LineBreakMode.NoWrap };
                // Plain cells: caption over value, no per-cell box (no cards in cards).
                var cell = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(2, 1), Children = { caption, _cells[i] } };
                this.Add(cell, i % 3, i / 3);
            }
        }

        /// <param name="marker">Optional per-stat suffix for unchanged cells (the nature's own +/−).</param>
        public void Show(IReadOnlyList<int> before, IReadOnlyList<int> after, Func<int, (string Text, Color Color)?>? marker = null)
        {
            for (var i = 0; i < 6; i++)
            {
                var text = new FormattedString();
                var old = i < before.Count ? before[i] : 0;
                var next = i < after.Count ? after[i] : old;
                var d = next - old;
                var tone = d > 0 ? UiTokens.Green : d < 0 ? UiTokens.RedOrange : UiTokens.Ink0;
                if (d == 0)
                    text.Spans.Add(new Span { Text = $"{next}", TextColor = UiTokens.Ink0, FontAttributes = FontAttributes.Bold });
                else
                {
                    text.Spans.Add(new Span { Text = $"{old}→", TextColor = UiTokens.InkSoft });
                    text.Spans.Add(new Span { Text = $"{next}", TextColor = tone, FontAttributes = FontAttributes.Bold });
                    text.Spans.Add(new Span { Text = d > 0 ? $" ▲{d}" : $" ▼{-d}", TextColor = tone, FontSize = UiTokens.TextSmall });
                }
                if (d == 0 && marker?.Invoke(i) is { } mark)
                    text.Spans.Add(new Span { Text = mark.Text, TextColor = mark.Color });
                _cells[i].FormattedText = text;
            }
        }
    }

    // ---------- Base stats ----------

    /// <summary>Six base stats as labelled bars plus the total, Pokédex style.</summary>
    public static View BaseStatBars(BaseStats stats, int total)
    {
        int[] values = [stats.Hp, stats.Atk, stats.Def, stats.SpA, stats.SpD, stats.Spe];
        var grid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 2,
            ColumnDefinitions = [new(new GridLength(34)), new(new GridLength(30)), new(GridLength.Star)],
        };
        for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < 6; i++)
        {
            var value = values[i];
            grid.Add(new Label { Text = NatureFacts.StatNames[i], FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center }, 0, i);
            grid.Add(new Label { Text = value.ToString(), FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center }, 1, i);
            var track = new Grid { HeightRequest = 7, VerticalOptions = LayoutOptions.Center };
            track.Add(new BoxView { Color = UiTokens.PaperShade, CornerRadius = 3 });
            // Bars scale to 180 (the practical ceiling; Blissey's 255 HP just fills it).
            var bar = new BoxView { Color = BaseStatColor(value), CornerRadius = 3, HorizontalOptions = LayoutOptions.Start };
            track.SizeChanged += (_, _) => bar.WidthRequest = Math.Max(3, track.Width * Math.Min(1.0, value / 180.0));
            track.Add(bar);
            grid.Add(track, 2, i);
        }
        grid.Add(new Label { Text = "BST", FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft }, 0, 6);
        grid.Add(new Label { Text = total.ToString(), FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.End }, 1, 6);
        return grid;
    }

    /// <summary>Low base stats read warm, high ones cool - the order the games' own summary bars use.</summary>
    public static Color BaseStatColor(int value) => value switch
    {
        < 50 => Color.FromArgb("#E0603A"),
        < 80 => Color.FromArgb("#E8B02E"),
        < 100 => Color.FromArgb("#A8C83A"),
        < 130 => Color.FromArgb("#3FB56A"),
        _ => Color.FromArgb("#2FA6C4"),
    };
}
