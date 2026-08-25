using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The themed component kit. Every screen composes these primitives — panels, buttons,
/// hint bars, chips — in the PKSM/DS-era language: white surfaces with warm-grey borders,
/// maroon Gen-5 header strips, cream choice buttons with cyan rims, indigo icon accents.
/// Views take colors from UiTokens (mapped from PKForge.Chrome Pksm), never literals.
/// </summary>
public static class Kit
{
    /// <summary>
    /// The page housing backdrop: pale indigo-tinted paper with the faint dot lattice and
    /// resting pokéball outlines. Prerendered once per size — no per-frame paint storms.
    /// </summary>
    public static SKCanvasView DeviceBackground()
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
                prerendered = RenderBackdrop(info);
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

    /// <summary>Bakes the backdrop (dot lattice + resting pokéballs) into one bitmap.</summary>
    private static SKBitmap RenderBackdrop(SKImageInfo info)
    {
        var bitmap = new SKBitmap(info.Width, info.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Pksm.Housing);

        // Resting pokéball outlines — quiet on the dark grid.
        using var ball = new SKPaint
        {
            Color = Pksm.ChromeLight.WithAlpha(0x2E),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
        };
        foreach (var (fx, fy, radius) in new[] { (0.16f, 0.28f, 90f), (0.52f, 0.78f, 130f), (0.84f, 0.20f, 70f) })
        {
            var x = fx * info.Width;
            var y = fy * info.Height;
            canvas.DrawCircle(x, y, radius, ball);
            canvas.DrawLine(x - radius, y, x + radius, y, ball);
            canvas.DrawCircle(x, y, radius * 0.3f, ball);
        }

        // The B/W system-menu grid.
        using var line = new SKPaint { Color = Pksm.HousingLine, StrokeWidth = 1 };
        for (var x = 0f; x < info.Width; x += 26) canvas.DrawLine(x, 0, x, info.Height, line);
        for (var y = 0f; y < info.Height; y += 26) canvas.DrawLine(0, y, info.Width, y, line);
        return bitmap;
    }

    /// <summary>Soft lift under every floating surface — the molded-plastic panel feel.</summary>
    private static Shadow FloatShadow => new()
    {
        Brush = Brush.Black,
        Opacity = 0.14f,
        Radius = 12,
        Offset = new Point(0, 4),
    };

    /// <summary>A dark chrome window: near-black body, grey border, soft shadow.</summary>
    public static Border DevicePanel(View content, double padding = 12) => new()
    {
        BackgroundColor = UiTokens.Shell,
        Stroke = UiTokens.ShellEdge,
        StrokeThickness = 2,
        StrokeShape = new RoundRectangle { CornerRadius = 8 },
        Shadow = FloatShadow,
        Padding = padding,
        Content = content,
    };

    /// <summary>Alias kept for views: the panel is the plate now.</summary>
    public static Border TopPlate(View content) => DevicePanel(content);

    /// <summary>The framed screen surface (box grid, summaries): dark frame around drawn content.</summary>
    public static Border LcdPanel(View content, double padding = 6) => new()
    {
        BackgroundColor = UiTokens.Shell,
        Stroke = UiTokens.ShellEdge,
        StrokeThickness = 2,
        StrokeShape = new RoundRectangle { CornerRadius = 4 },
        Shadow = FloatShadow,
        Padding = padding,
        Content = content,
    };

    /// <summary>Readout text on a dark surface.</summary>
    public static Label LcdLabel(double size = 13) => new()
    {
        TextColor = UiTokens.Paper,
        FontSize = size,
        FontAttributes = FontAttributes.Bold,
        LineBreakMode = LineBreakMode.TailTruncation,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>Small friendly species that wander across the status bar.</summary>
    private static readonly int[] TickerWalkers = [25, 133, 175, 39, 52, 54, 7, 4, 1, 152, 158, 255];
    private static readonly Random TickerRandom = new();

    /// <summary>
    /// The strolling-Pokémon easter egg on its own transparent strip: a little mon walks
    /// the width, bobbing; a different one starts each lap. Place anywhere.
    /// </summary>
    public static View WalkerStrip(double height = 24)
    {
        var walker = new SKCanvasView { InputTransparent = true, HeightRequest = height };
        var species = TickerWalkers[TickerRandom.Next(TickerWalkers.Length)];
        var x = -40f;
        var tick = 0;
        Services.ISpriteService? sprites = null;
        // Cached bitmap wrapper per species — never decode or re-wrap per frame.
        SKImage? image = null;
        var imageSpecies = -1;

        void DropImage()
        {
            image?.Dispose();
            image = null;
            imageSpecies = -1;
        }

        walker.PaintSurface += (_, args) =>
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            sprites ??= IPlatformApplication.Current?.Services.GetService<Services.ISpriteService>();
            if (imageSpecies != species)
            {
                DropImage();
                var bitmap = sprites?.GetSprite(species, 0, false);
                if (bitmap is null)
                {
                    sprites?.Warm(species, 0, false,
                        () => MainThread.BeginInvokeOnMainThread(walker.InvalidateSurface));
                    return;
                }
                image = SKImage.FromBitmap(bitmap);
                imageSpecies = species;
            }
            if (image is null) return;
            var size = args.Info.Height * 0.94f;
            var scale = Math.Min(size / image.Width, size / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            var bob = (float)Math.Abs(Math.Sin(tick / 3.0)) * args.Info.Height * 0.08f;
            var y = (args.Info.Height - h) / 2f - bob;
            // Sprites face left; the stroll goes right, so mirror around the sprite center.
            canvas.Save();
            canvas.Scale(-1, 1, x + w / 2, 0);
            canvas.DrawImage(image, new SKRect(x, y, x + w, y + h),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            canvas.Restore();
        };

        // The stroll timer only runs while the strip is actually on screen.
        IDispatcherTimer? timer = null;
        walker.Loaded += (_, _) =>
        {
            if (timer is not null) return;
            timer = walker.Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(90);
            timer.Tick += (_, _) =>
            {
                tick++;
                x += 2.2f;
                if (x > (float)walker.CanvasSize.Width + 40f)
                {
                    x = -40f;
                    species = TickerWalkers[TickerRandom.Next(TickerWalkers.Length)];
                }
                walker.InvalidateSurface();
            };
            timer.Start();
        };
        walker.Unloaded += (_, _) =>
        {
            timer?.Stop();
            timer = null;
            DropImage();
        };
        return walker;
    }

    /// <summary>Status readout with the walker strolling a lane beneath the text.</summary>
    public static View Ticker(string textBindingPath)
    {
        var text = LcdLabel();
        text.SetBinding(Label.TextProperty, textBindingPath);
        text.Margin = new Thickness(6, 0);

        var walker = WalkerStrip(20);
        var rows = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(new GridLength(20))],
            Children = { text, walker },
        };
        Grid.SetRow(walker, 1);
        return LcdPanel(rows, padding: 4);
    }

    /// <summary>
    /// A PKSM choice button (the STATS/MOVES/SAVE language): cream fill, cyan rim,
    /// ink text. Primary actions get the lift.
    /// </summary>
    public static Button Capsule(string text, Color accent, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = UiTokens.ChoiceFill,
            BorderColor = UiTokens.ChoiceRim,
            BorderWidth = 2,
            TextColor = UiTokens.Paper,
            FontAttributes = FontAttributes.Bold,
            FontSize = 13,
            CornerRadius = 6,
            Padding = new Thickness(14, 8),
        };
        if (primary) button.Shadow = FloatShadow;
        return button;
    }

    /// <summary>A compact stack-style blue button (box paging arrows, rail actions).</summary>
    public static Button MiniCapsule(string glyph, Color accent) => new()
    {
        Text = glyph,
        FontFamily = "Rounded",
        BackgroundColor = UiTokens.MenuBlueDeep,
        BorderColor = UiTokens.Paper,
        BorderWidth = 1.5,
        TextColor = UiTokens.Paper,
        FontAttributes = FontAttributes.Bold,
        FontSize = 14,
        CornerRadius = 5,
        WidthRequest = 44,
        HeightRequest = 36,
        Padding = 0,
    };

    /// <summary>A blinky device indicator light (static for now; animation comes later).</summary>
    public static Ellipse StatusLight(Color color, double size = 12) => new()
    {
        Fill = new SolidColorBrush(color),
        Stroke = new SolidColorBrush(Colors.White.WithAlpha(0.55f)),
        StrokeThickness = 1.5,
        WidthRequest = size,
        HeightRequest = size,
        VerticalOptions = LayoutOptions.Center,
    };

    /// <summary>Screen title: quiet slate caps.</summary>
    public static Label HousingTitle(string text) => new()
    {
        Text = text,
        TextColor = UiTokens.Ink0,
        FontSize = 15,
        FontAttributes = FontAttributes.Bold,
        CharacterSpacing = 2,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>
    /// The Gen-5 maroon header strip: PKSM's section header. White text on maroon,
    /// a dark bottom edge. Use at the top of panels and screens.
    /// </summary>
    public static View HeaderBar(string title)
    {
        return new Border
        {
            BackgroundColor = UiTokens.Maroon,
            Stroke = UiTokens.MaroonDeep,
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            Padding = new Thickness(12, 5),
            HorizontalOptions = LayoutOptions.Fill,
            Content = new Label
            {
                Text = title,
                TextColor = Colors.White,
                FontFamily = DsChrome.PixelFont,
                FontSize = 16,
                VerticalTextAlignment = TextAlignment.Center,
            },
        };
    }

    /// <summary>
    /// The console-style bottom hint bar: blue key discs + pixel labels, tappable for
    /// touch parity. This is where the app declares itself gamepad-first.
    /// </summary>
    public static Border HintBar(params (string Glyph, string Label, Action? OnTap)[] hints)
    {
        var row = new HorizontalStackLayout { Spacing = 20, HorizontalOptions = LayoutOptions.Center };
        foreach (var (glyph, label, onTap) in hints)
        {
            var item = new HorizontalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    new Border
                    {
                        Stroke = new SolidColorBrush(UiTokens.Paper),
                        StrokeThickness = 1.5,
                        StrokeShape = new RoundRectangle { CornerRadius = 10 },
                        BackgroundColor = UiTokens.MenuBlue,
                        Padding = new Thickness(6, 2),
                        Content = new Label
                        {
                            Text = glyph, FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Paper, FontSize = 12,
                            FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center,
                            HorizontalTextAlignment = TextAlignment.Center,
                        },
                    },
                    new Label { Text = label, FontFamily = DsChrome.PixelFont, TextColor = Colors.White, FontSize = 13, FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center },
                },
            };
            if (onTap is not null)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => onTap();
                item.GestureRecognizers.Add(tap);
            }
            row.Children.Add(item);
        }
        return new Border
        {
            BackgroundColor = Color.FromArgb("#10131A"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 7),
            Content = row,
        };
    }

    /// <summary>
    /// A soft callout chip: solid hairline, tiny caption over the value.
    /// Facts get one chip each — never merged into a status blob.
    /// </summary>
    public static Border BlueprintChip(string caption, View value)
    {
        var captionLabel = new Label
        {
            Text = caption,
            TextColor = UiTokens.Blueprint,
            FontSize = 8,
            CharacterSpacing = 2,
            FontAttributes = FontAttributes.Bold,
        };
        return new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 1.2,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(10, 4, 10, 6),
            Content = new VerticalStackLayout { Spacing = 1, Children = { captionLabel, value } },
        };
    }

    /// <summary>Value text inside a chip.</summary>
    public static Label BlueprintValue(double size = 12) => new()
    {
        TextColor = UiTokens.Ink0,
        FontSize = size,
        FontAttributes = FontAttributes.Bold,
        LineBreakMode = LineBreakMode.TailTruncation,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>Era color per generation (Game Boy gray through Paldea purple).</summary>
    public static Color EraColor(int generation) => generation switch
    {
        1 or 2 => Color.FromArgb("#9AA6B2"),
        3 => Color.FromArgb("#8B7BD8"),
        4 => Color.FromArgb("#5E86D4"),
        5 => Color.FromArgb("#4E5A68"),
        6 => Color.FromArgb("#4FB6DB"),
        7 => Color.FromArgb("#EE9A4A"),
        8 => Color.FromArgb("#D45C6D"),
        9 => Color.FromArgb("#9A6AD8"),
        _ => UiTokens.Ink1,
    };

    /// <summary>Console family code per generation — the "icon" on the GEN chip.</summary>
    public static string ConsoleCode(int generation) => generation switch
    {
        1 or 2 => "GB",
        3 => "GBA",
        4 or 5 => "DS",
        6 or 7 => "3DS",
        8 or 9 => "NS",
        _ => "?",
    };

    /// <summary>Small era-colored console badge (the generation icon).</summary>
    public static Border GenBadge(int generation) => new()
    {
        BackgroundColor = EraColor(generation),
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 4 },
        Padding = new Thickness(6, 1),
        VerticalOptions = LayoutOptions.Center,
        Content = new Label
        {
            Text = ConsoleCode(generation),
            TextColor = Colors.White,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
        },
    };

    /// <summary>Pop-in entrance for floating windows: quick fade + settle.</summary>
    public static void AnimateIn(View window)
    {
        window.Opacity = 0;
        window.Scale = 0.92;
        _ = window.FadeToAsync(1, 130, Easing.CubicOut);
        _ = window.ScaleToAsync(1, 160, Easing.SpringOut);
    }

    /// <summary>
    /// The shared fit-to-host rule for every overlay window (the Thor logical screen is
    /// about 640x360 dp, so fixed pixel requests overflowed). The window is a device panel
    /// capped at host size minus margins, centered; content scrolls when it cannot shrink.
    /// </summary>
    public static Border OverlayWindow(Grid host, View content, double preferredMaxWidth = 520, double padding = 14, bool scroll = true)
    {
        var maxWidth = host.Width > 0 ? host.Width - 24 : 616;
        var maxHeight = host.Height > 0 ? host.Height - 16 : 344;
        var window = DevicePanel(
            scroll ? new ScrollView { Content = content } : content,
            padding: padding);
        window.MaximumWidthRequest = Math.Min(maxWidth, preferredMaxWidth);
        window.MaximumHeightRequest = maxHeight;
        window.HorizontalOptions = LayoutOptions.Center;
        window.VerticalOptions = LayoutOptions.Center;
        return window;
    }

    /// <summary>
    /// Layers scrim + window over the whole host grid (spanning every row/column) and
    /// plays the pop-in. Returns the overlay grid so the caller can remove it on close.
    /// </summary>
    public static Grid AttachOverlay(Grid host, View window, Action? onScrimTap = null)
    {
        var scrim = new BoxView { Color = UiTokens.Scrim };
        if (onScrimTap is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onScrimTap();
            scrim.GestureRecognizers.Add(tap);
        }
        var overlay = new Grid { Children = { scrim, window } };
        host.Add(overlay);
        Grid.SetRowSpan(overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, host.ColumnDefinitions.Count));
        AnimateIn(window);
        return overlay;
    }

    /// <summary>Labeled entry row on a panel.</summary>
    public static View Field(string caption, string bindingPath)
    {
        var entry = new Entry
        {
            FontSize = 13,
            TextColor = UiTokens.Paper,
            BackgroundColor = UiTokens.ShellPress,
            HeightRequest = 36,
            // Nicknames and trainer names are proper nouns: no red squiggles, no autocorrect.
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        entry.SetBinding(Entry.TextProperty, bindingPath);
        var label = new Label
        {
            Text = caption,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = UiTokens.Ink1,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var grid = new Grid
        {
            ColumnDefinitions = [new(new GridLength(78)), new(GridLength.Star)],
            Children = { label, entry },
        };
        Grid.SetColumn(entry, 1);
        return grid;
    }
}
