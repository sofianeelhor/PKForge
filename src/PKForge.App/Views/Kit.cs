using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The themed component kit. Every screen composes these primitives — panels, buttons,
/// hint bars, chips — in the PKSM/DS-era language rebuilt from the PKForge logo:
/// dark grid fields, layered navy surfaces, cobalt structure, and cyan focus light.
/// Views take colors from UiTokens (mapped from PKForge.Chrome Pksm), never literals.
/// </summary>
public static class Kit
{
    /// <summary>
    /// The page housing backdrop: the logo's crisp navy/cobalt grid.
    /// Prerendered once per size — no per-frame paint storms.
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

    /// <summary>Bakes the shared logo grid into one bitmap.</summary>
    private static SKBitmap RenderBackdrop(SKImageInfo info)
    {
        var bitmap = new SKBitmap(info.Width, info.Height);
        using var canvas = new SKCanvas(bitmap);
        PksmPaint.LogoGrid(canvas, new SKRect(0, 0, info.Width, info.Height));
        return bitmap;
    }

    /// <summary>The hard pixel drop shadow every floating panel wears (3 dp, no blur, no glow).</summary>
    private static Shadow FloatShadow => new()
    {
        Brush = new SolidColorBrush(UiTokens.PanelShadow),
        Opacity = 0.55f,
        Radius = 1,
        Offset = new Point(3, 3),
    };

    /// <summary>The hard pixel drop shadow, for surfaces built outside <see cref="Panel"/>.</summary>
    public static Shadow HardShadow() => FloatShadow;

    /// <summary>Navy body with the faint light along the top edge (the summary panel).</summary>
    private static Brush PanelBrush() => new LinearGradientBrush(
        [new GradientStop(UiTokens.PanelTop, 0f), new GradientStop(UiTokens.Paper, 0.09f)],
        new Point(0, 0), new Point(0, 1));

    /// <summary>
    /// THE panel: navy body, faint top light, 2 dp cobalt bezel, 6 dp corners, hard pixel
    /// drop shadow. One per region - panels never nest; group inside with rows and dividers.
    /// </summary>
    public static Border Panel(View content, double padding = 12) => new()
    {
        Background = PanelBrush(),
        Stroke = UiTokens.ShellEdge,
        StrokeThickness = UiTokens.PanelEdge,
        StrokeShape = new RoundRectangle { CornerRadius = UiTokens.PanelRadius },
        Shadow = FloatShadow,
        Padding = padding,
        Content = content,
    };

    /// <summary>A layered navy device window (alias of <see cref="Panel"/>).</summary>
    public static Border DevicePanel(View content, double padding = 12) => Panel(content, padding);

    /// <summary>Alias kept for views: the panel is the plate now.</summary>
    public static Border TopPlate(View content) => Panel(content);

    /// <summary>The framed screen surface (box grid, summaries): the panel with a tighter inset.</summary>
    public static Border LcdPanel(View content, double padding = 6) => Panel(content, padding);

    /// <summary>
    /// A flat recessed well inside a panel (a preview, a field group): darker navy, no
    /// border, small corners. The one legal way to group inside a panel besides rows.
    /// </summary>
    public static Border Well(View content, double padding = 8) => new()
    {
        BackgroundColor = UiTokens.Well,
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
        Padding = padding,
        Content = content,
    };

    /// <summary>
    /// A list/attribute row inside a panel: no border of its own, a soft stripe on
    /// alternate rows. Focus uses <see cref="SetRowFocus"/> (the selected-button look).
    /// </summary>
    public static Border Row(View content, bool shaded = false, Thickness? padding = null) => new()
    {
        BackgroundColor = shaded ? UiTokens.RowStripe : Colors.Transparent,
        Stroke = Colors.Transparent,
        StrokeThickness = 1.2,
        StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
        Padding = padding ?? new Thickness(10, 6),
        Content = content,
    };

    /// <summary>Paints a row focused (cobalt body + pale rim) or back to its resting stripe.</summary>
    public static void SetRowFocus(Border row, bool focused, bool shaded = false)
    {
        row.BackgroundColor = focused ? UiTokens.SelectFill : shaded ? UiTokens.RowStripe : Colors.Transparent;
        row.Stroke = focused ? UiTokens.Rim : Colors.Transparent;
    }

    /// <summary>A 1 dp cobalt hairline between groups inside a panel.</summary>
    public static BoxView Divider(double verticalMargin = 4) => new()
    {
        Color = UiTokens.Divider,
        HeightRequest = 1,
        Margin = new Thickness(0, verticalMargin),
        InputTransparent = true,
    };

    // ---------- Text ----------

    private static readonly HashSet<string> KeepCaps = new(StringComparer.Ordinal)
    {
        "PK", "PKM", "PKX", "QR", "IV", "EV", "OT", "TID", "SID", "RNG", "HP", "PP", "TM", "TR", "HM", "EXP",
        "ID", "GB", "GBC", "GBA", "NDS", "DS", "3DS", "NS", "SAV", "PKSM", "HOME", "OK", "ROM", "PC", "NPC",
        "SAE", "RTC", "USB", "SD", "BST", "JP", "EN", "FR", "DE", "IT", "ES", "KO", "CHS", "CHT", "HGSS", "BW",
        "B2W2", "XY", "ORAS", "SM", "USUM", "LGPE", "SWSH", "BDSP", "PLA", "SV", "FRLG", "RSE", "CFRU", "DPP",
        "UI", "GTS", "PID", "EC", "II", "III", "VI", "XD", "URL", "CSV", "JSON", "HEX",
    };

    private static readonly Dictionary<string, string> Proper = new(StringComparer.Ordinal)
    {
        ["PKFORGE"] = "PKForge", ["SHOWDOWN"] = "Showdown", ["POKÉMON"] = "Pokémon", ["POKéMON"] = "Pokémon",
        ["POKEMON"] = "Pokémon", ["POKÉPARK"] = "Poképark", ["POKéPARK"] = "Poképark", ["POKÉDEX"] = "Pokédex",
        ["POKéDEX"] = "Pokédex", ["PKHEX"] = "PKHeX", ["NUZLOCKE"] = "Nuzlocke", ["POKÉRUS"] = "Pokérus", ["POKéRUS"] = "Pokérus",
    };

    /// <summary>
    /// Normal case for labels that arrive SHOUTED ("SAVE CHANGES" → "Save changes"), keeping
    /// real acronyms (QR, IVs, .PK, OT, TID...). Text with any lowercase is left untouched.
    /// Display only: callers keep matching on their original caption strings.
    /// </summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var letters = 0;
        foreach (var ch in text)
        {
            if (char.IsLower(ch)) return text;
            if (char.IsLetter(ch)) letters++;
        }
        if (letters < 3) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var first = true;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { sb.Append(text[i]); i++; continue; }
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'')) i++;
            var word = text[start..i];
            string outWord;
            // "PASTE A SET": the article follows a word; "A USE · B BACK": the button letter follows a separator.
            var prev = sb.ToString();
            var trimmed = prev.TrimEnd();
            var afterWord = trimmed.Length > 0 && trimmed.Length < prev.Length && char.IsLetter(trimmed[^1]);
            if (word == "A" && afterWord) outWord = "a";
            else if (KeepCaps.Contains(word) || word.Length == 1) outWord = word;
            else if (Proper.TryGetValue(word, out var proper)) outWord = proper;
            else if (word.Length > 2 && word.EndsWith('S') && KeepCaps.Contains(word[..^1])) outWord = word[..^1] + "s";
            else if (word.Any(char.IsDigit) && word.Length <= 4) outWord = word;
            else
            {
                var lower = word.ToLowerInvariant();
                outWord = first ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
            }
            if (word.Any(char.IsLetter)) first = false;
            sb.Append(outWord);
        }
        return sb.ToString();
    }

    /// <summary>Binding converter: shows a bound status line in normal case (see <see cref="Tidy"/>).</summary>
    public static readonly IValueConverter TidyText = new TidyConverter();

    private sealed class TidyConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is string text ? Tidy(text) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Caption / secondary text: 12.5 dp soft ink, normal case.</summary>
    public static Label Caption(string? text = null, Color? color = null) => new()
    {
        Text = text,
        FontSize = UiTokens.TextSmall,
        TextColor = color ?? UiTokens.InkSoft,
        LineBreakMode = LineBreakMode.TailTruncation,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>Body text: 13.5 dp pale ink, wraps.</summary>
    public static Label BodyText(string? text = null, Color? color = null) => new()
    {
        Text = text,
        FontSize = UiTokens.TextBody,
        TextColor = color ?? UiTokens.Ink0,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    /// <summary>A section title inside a panel (pixel voice, 15 dp): hierarchy without another box.</summary>
    public static Label SectionTitle(string text, Color? color = null) => new()
    {
        Text = Tidy(text),
        FontFamily = DsChrome.PixelFont,
        FontSize = UiTokens.TextTitle,
        TextColor = color ?? UiTokens.Ink0,
        VerticalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation,
    };

    /// <summary>
    /// What used to be a badge/chip: plain coloured text (the colour carries the meaning),
    /// bold, 12.5 dp, no outline, no pill.
    /// </summary>
    public static Label Emphasis(string? text, Color color, double size = UiTokens.TextSmall) => new()
    {
        Text = text,
        FontFamily = DsChrome.PixelFont,
        FontSize = Math.Max(size, UiTokens.TextSmall),
        TextColor = UiTokens.TextTone(color),
        LineBreakMode = LineBreakMode.NoWrap,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>Readout text on a panel.</summary>
    public static Label LcdLabel(double size = 13) => new()
    {
        TextColor = UiTokens.Ink0,
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
        text.SetBinding(Label.TextProperty, new Binding(textBindingPath, converter: TidyText));
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

    /// <summary>Neutral/info accents give a resting menu button; signal accents a toned fill.</summary>
    private static Color? ButtonTone(Color accent)
    {
        if (Same(accent, UiTokens.Green) || Same(accent, UiTokens.Ok)) return UiTokens.AccentLegal;
        if (Same(accent, UiTokens.Bad) || Same(accent, UiTokens.RedOrange) || Same(accent, UiTokens.GiftRed)) return UiTokens.AccentDanger;
        if (Same(accent, UiTokens.Gold) || Same(accent, UiTokens.Yellow) || Same(accent, UiTokens.RibbonGold)) return UiTokens.AccentMoves;
        return null;
    }

    private static bool Same(Color a, Color b) =>
        Math.Abs(a.Red - b.Red) < 0.01f && Math.Abs(a.Green - b.Green) < 0.01f && Math.Abs(a.Blue - b.Blue) < 0.01f;

    /// <summary>
    /// The one button: the summary's menu tab / PadMenu button, skinned natively by
    /// <see cref="CapsuleSkin"/> (gradient body, 1 px top light, hard drop, sinks when pressed).
    /// Resting = navy body with the cobalt edge; a signal accent (confirm green, danger red,
    /// gold) becomes a toned fill; <paramref name="primary"/> = the screen's one main action:
    /// its tone (cobalt when neutral) with the pale rim, a brighter light and a taller body.
    /// <paramref name="icon"/> is a semantic <see cref="PksmIconCatalog"/> name shown before the label.
    /// </summary>
    public static Button Capsule(string text, Color accent, bool primary = false, string? icon = null)
    {
        // Only the primary wears a toned fill; secondary signal buttons stay navy and carry
        // their meaning as a slim accent strip on the leading edge (+ icon).
        var signal = ButtonTone(accent);
        var tone = primary ? signal ?? UiTokens.AccentNeutral : null;
        var button = new Button
        {
            Text = Tidy(text),
            FontFamily = DsChrome.PixelFont,
            BackgroundColor = tone ?? UiTokens.ButtonTop,
            BorderColor = primary ? UiTokens.Ink0.WithAlpha(0.7f) : tone is null ? UiTokens.ButtonEdge : UiTokens.Rim,
            BorderWidth = tone is null ? UiTokens.ControlEdge : 1.2,
            TextColor = UiTokens.Ink0,
            FontSize = UiTokens.TextLabel,
            CornerRadius = (int)UiTokens.ControlRadius,
            Padding = new Thickness(14, 7),
            MinimumHeightRequest = primary ? 40 : 34,
        };
        CapsuleSkin.Attach(button, icon, primary, primary ? null : signal is null ? null : accent);
        return button;
    }

    /// <summary>Paints a Capsule as focused (the selected-button look) - gamepad focus, never a glow.</summary>
    public static void SetButtonFocus(Button button, bool focused, Color? restingBackground = null, Color? restingText = null)
    {
        if (focused)
        {
            button.BackgroundColor = UiTokens.SelectFill;
            button.BorderColor = UiTokens.Ink0;
            button.TextColor = UiTokens.SelectInk;
        }
        else
        {
            if (restingBackground is not null) button.BackgroundColor = restingBackground;
            if (restingText is not null) button.TextColor = restingText;
            var toned = restingBackground is not null && !Same(restingBackground, UiTokens.ButtonTop);
            button.BorderColor = CapsuleSkin.IsPrimary(button) ? UiTokens.Ink0.WithAlpha(0.7f) : toned ? UiTokens.Rim : UiTokens.ButtonEdge;
        }
        CapsuleSkin.SetFocused(button, focused);
    }

    /// <summary>
    /// The ‹ L / R › pager button (the summary's corner buttons): void body, pale 1.5 dp
    /// edge, pixel glyph. Bare "&lt;" / "&gt;" become the typographic chevrons.
    /// </summary>
    public static Button MiniCapsule(string glyph, Color accent) => new()
    {
        Text = glyph switch { "<" => "‹", ">" => "›", _ => glyph },
        FontFamily = "Rounded",
        BackgroundColor = UiTokens.MenuBlueDeep,
        BorderColor = UiTokens.Ink0.WithAlpha(0.85f),
        BorderWidth = UiTokens.ControlEdge,
        TextColor = UiTokens.Ink0,
        FontAttributes = FontAttributes.Bold,
        FontSize = 16,
        CornerRadius = (int)UiTokens.PanelRadius,
        WidthRequest = 44,
        HeightRequest = 36,
        Padding = 0,
    };

    /// <summary>A round cyan key disc (footer glyph button: A, B, X, LR ...).</summary>
    public static Border GlyphKey(string glyph, Action? onTap = null)
    {
        var key = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 11 },
            BackgroundColor = UiTokens.BagCyanEdge,
            Padding = new Thickness(glyph.Length > 1 ? 7 : 0, 0),
            MinimumWidthRequest = 22,
            HeightRequest = 22,
            VerticalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = glyph, FontFamily = DsChrome.PixelFont, TextColor = UiTokens.OnAccent, FontSize = UiTokens.TextLabel,
                VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.Center,
            },
        };
        if (onTap is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            key.GestureRecognizers.Add(tap);
        }
        return key;
    }

    /// <summary>
    /// A tab / segment in the menu-button language: resting navy + cobalt edge, active =
    /// the context accent with the pale rim. Re-point with <see cref="SetTab"/>.
    /// </summary>
    public static Border Tab(string text, bool active = false, Color? accent = null)
    {
        var tab = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
            Padding = new Thickness(12, 5),
            Content = new Label
            {
                Text = Tidy(text), FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextLabel,
                HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.NoWrap,
            },
        };
        SetTab(tab, active, accent);
        return tab;
    }

    public static void SetTab(Border tab, bool active, Color? accent = null)
    {
        var body = accent ?? UiTokens.AccentNeutral;
        tab.Background = active
            ? new LinearGradientBrush([new GradientStop(body.WithLuminosity(Math.Min(1, body.GetLuminosity() + 0.08f)), 0), new GradientStop(body, 1)], new Point(0, 0), new Point(0, 1))
            : new LinearGradientBrush([new GradientStop(UiTokens.ButtonTop, 0), new GradientStop(UiTokens.ButtonBottom, 1)], new Point(0, 0), new Point(0, 1));
        tab.Stroke = active ? UiTokens.Rim : UiTokens.ButtonEdge;
        tab.StrokeThickness = active ? 1.2 : UiTokens.ControlEdge;
        if (tab.Content is Label label) label.TextColor = active ? Colors.White : UiTokens.InkSoft;
    }

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

    /// <summary>Screen title on the dark field: pixel voice, 15 dp, normal case.</summary>
    public static Label HousingTitle(string text) => new()
    {
        Text = Tidy(text),
        TextColor = UiTokens.Ink0,
        FontFamily = DsChrome.PixelFont,
        FontSize = UiTokens.TextTitle,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>
    /// The header strip (the summary's): accent body (cobalt by default) with a soft top
    /// light, dark outline, white pixel caption with a 1 dp drop. Content is the Label.
    /// </summary>
    public static View HeaderBar(string title, Color? accent = null)
    {
        var body = accent ?? UiTokens.AccentNeutral;
        return new Border
        {
            Background = new LinearGradientBrush(
                [new GradientStop(body.WithLuminosity(Math.Min(1, body.GetLuminosity() + 0.08f)), 0f), new GradientStop(body.WithLuminosity(Math.Max(0, body.GetLuminosity() - 0.04f)), 1f)],
                new Point(0, 0), new Point(0, 1)),
            Stroke = UiTokens.Outline,
            StrokeThickness = UiTokens.ControlEdge,
            StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
            Padding = new Thickness(10, 5),
            HorizontalOptions = LayoutOptions.Fill,
            Content = new Label
            {
                Text = title,
                TextColor = Colors.White,
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextTitle + 1,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                Shadow = new Shadow { Brush = new SolidColorBrush(UiTokens.PanelShadow), Opacity = 0.56f, Radius = 0, Offset = new Point(1, 1) },
            },
        };
    }

    /// <summary>
    /// The console-style bottom hint bar: round cyan key discs + pale pixel labels on the
    /// panel, tappable for touch parity. This is where the app declares itself gamepad-first.
    /// </summary>
    public static Border HintBar(params (string Glyph, string Label, Action? OnTap)[] hints) => new()
    {
        Background = PanelBrush(),
        Stroke = UiTokens.ShellEdge,
        StrokeThickness = UiTokens.PanelEdge,
        StrokeShape = new RoundRectangle { CornerRadius = UiTokens.PanelRadius },
        Padding = new Thickness(16, 7),
        Content = HintRow(hints),
    };

    /// <summary>
    /// The hint row inside a window: the same key discs on the window's own panel, set off
    /// by a hairline - never a second bordered panel nested in the first.
    /// </summary>
    public static Border WindowHints(params (string Glyph, string Label, Action? OnTap)[] hints) => new()
    {
        BackgroundColor = Colors.Transparent,
        StrokeThickness = 0,
        Padding = new Thickness(0, 0, 0, 2),
        Content = new VerticalStackLayout { Spacing = 7, Children = { Divider(0), HintRow(hints) } },
    };

    private static HorizontalStackLayout HintRow((string Glyph, string Label, Action? OnTap)[] hints)
    {
        var row = new HorizontalStackLayout { Spacing = 22, HorizontalOptions = LayoutOptions.Center };
        foreach (var (glyph, label, onTap) in hints)
        {
            var item = new HorizontalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    GlyphKey(glyph),
                    new Label { Text = Tidy(label), FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Ink0, FontSize = UiTokens.TextTitle, VerticalTextAlignment = TextAlignment.Center },
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
        return row;
    }

    /// <summary>
    /// A labelled fact (was a bordered chip): 12.5 dp caption over the value, no box.
    /// Facts get one each - never merged into a status blob.
    /// </summary>
    public static Border BlueprintChip(string caption, View value) => new()
    {
        BackgroundColor = Colors.Transparent,
        StrokeThickness = 0,
        Padding = new Thickness(0, 2),
        Content = new VerticalStackLayout { Spacing = 1, Children = { Caption(Tidy(caption)), value } },
    };

    /// <summary>Value text for a fact: pixel voice, matches the entries line-for-line.</summary>
    public static Label BlueprintValue(double size = 13.5) => new()
    {
        TextColor = UiTokens.Ink0,
        FontFamily = DsChrome.PixelFont,
        FontSize = Math.Max(size, UiTokens.TextBody),
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

    /// <summary>The generation mark: the console code as era-coloured text (no badge).</summary>
    public static Border GenBadge(int generation) => new()
    {
        BackgroundColor = Colors.Transparent,
        StrokeThickness = 0,
        Padding = 0,
        VerticalOptions = LayoutOptions.Center,
        Content = Emphasis(ConsoleCode(generation), EraColor(generation)),
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

    /// <summary>
    /// The text field: a recessed well (no underline glow), 13.5 dp pale ink, no
    /// autocorrect. Style any Entry made elsewhere with <see cref="StyleEntry"/>.
    /// </summary>
    public static Entry TextField(string? bindingPath = null, Keyboard? keyboard = null)
    {
        var entry = new Entry { Keyboard = keyboard ?? Keyboard.Default };
        StyleEntry(entry);
        if (bindingPath is not null) entry.SetBinding(Entry.TextProperty, bindingPath);
        return entry;
    }

    public static void StyleEntry(Entry entry)
    {
        entry.FontSize = UiTokens.TextBody;
        entry.FontFamily = DsChrome.PixelFont;
        entry.TextColor = UiTokens.Ink0;
        entry.PlaceholderColor = UiTokens.InkSoft;
        entry.BackgroundColor = UiTokens.Well;
        if (entry.HeightRequest < 0) entry.HeightRequest = 36;
        // Nicknames and trainer names are proper nouns: no red squiggles, no autocorrect.
        entry.IsSpellCheckEnabled = false;
        entry.IsTextPredictionEnabled = false;
    }

    /// <summary>
    /// A form row: normal-case caption in a fixed column, the control beside it. Forms are
    /// rows on the window's own panel - no per-field cards.
    /// </summary>
    public static Grid FormRow(string caption, View value, double captionWidth = 90)
    {
        var grid = new Grid
        {
            ColumnSpacing = UiTokens.Space3,
            MinimumHeightRequest = 38,
            ColumnDefinitions = [new(new GridLength(captionWidth)), new(GridLength.Star)],
            Children =
            {
                new Label
                {
                    Text = Tidy(caption), FontSize = UiTokens.TextLabel, FontFamily = DsChrome.PixelFont,
                    TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center,
                },
                value,
            },
        };
        Grid.SetColumn(value, 1);
        return grid;
    }

    /// <summary>Labeled entry row on a panel: 12.5 dp normal-case caption, well field.</summary>
    public static View Field(string caption, string bindingPath)
    {
        var entry = TextField(bindingPath);
        var label = Caption(Tidy(caption));
        var grid = new Grid
        {
            ColumnSpacing = UiTokens.Space2,
            ColumnDefinitions = [new(new GridLength(84)), new(GridLength.Star)],
            Children = { label, entry },
        };
        Grid.SetColumn(entry, 1);
        return grid;
    }

}
