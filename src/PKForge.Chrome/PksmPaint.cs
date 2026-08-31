using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The drawn chrome of the Gen-5 era: navy strips, logo-blue buttons, the cobalt +
/// cyan selection, red corner brackets and the red triangle cursor. Content cards
/// are white on the colored worlds. Everything composes from <see cref="Pksm"/> tokens.
/// </summary>
public static class PksmPaint
{
    // ---------- Shared brushes ----------

    private static SKPaint Paint(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill };
    private static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    /// <summary>The exact visual rhythm of the logo background: navy field, crisp cobalt grid.</summary>
    public static void LogoGrid(SKCanvas c, SKRect r, float cell = 32, float lineWidth = 3)
    {
        c.DrawRect(r, Paint(Pksm.Housing));
        using var line = new SKPaint
        {
            Color = Pksm.HousingLine.WithAlpha(0xD0),
            IsAntialias = false,
            StrokeWidth = lineWidth,
        };
        var offsetX = r.Left % cell;
        var offsetY = r.Top % cell;
        for (var x = r.Left - offsetX; x <= r.Right; x += cell)
            c.DrawLine(MathF.Round(x), r.Top, MathF.Round(x), r.Bottom, line);
        for (var y = r.Top - offsetY; y <= r.Bottom; y += cell)
            c.DrawLine(r.Left, MathF.Round(y), r.Right, MathF.Round(y), line);
    }

    /// <summary>The diagonal top gloss the era's buttons wore: a light line under the top edge.</summary>
    private static void Gloss(SKCanvas c, SKRect r, float radius)
    {
        var sheen = new SKRect(r.Left + 3, r.Top + 2, r.Right - 3, r.Top + r.Height * 0.28f);
        using var p = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x1A), IsAntialias = true };
        c.DrawRoundRect(sheen, Math.Max(1, radius - 2), Math.Max(1, radius - 2), p);
    }

    // ---------- Windows & panels ----------

    /// <summary>Layered navy content card with a crisp pixel-console bezel.</summary>
    public static void Panel(SKCanvas c, SKRect r, SKColor? fill = null, float radius = 6)
    {
        c.DrawRoundRect(new SKRect(r.Left + 4, r.Top + 4, r.Right + 4, r.Bottom + 4), radius, radius, Paint(Pksm.LogoVoid.WithAlpha(0xB0)));
        c.DrawRoundRect(r, radius, radius, Paint(fill ?? Pksm.Paper));
        c.DrawRoundRect(r, radius, radius, Stroke(Pksm.PaperEdge, 2));
        c.DrawLine(r.Left + radius, r.Top + 2, r.Right - radius, r.Top + 2, Stroke(Pksm.LogoBlue, 2));
    }

    /// <summary>A dark chrome window: layered navy body and cobalt bezel.</summary>
    public static void DarkWindow(SKCanvas c, SKRect r, float radius = 8)
    {
        Panel(c, r, Pksm.Paper, radius);
    }

    /// <summary>The resting logo button: void outline, navy body, cobalt/cyan edge light.</summary>
    public static void BlackButton(SKCanvas c, SKRect r, float radius = 6)
    {
        c.DrawRoundRect(r, radius, radius, Paint(Pksm.ButtonBlueDeep));
        c.DrawRoundRect(SKRect.Inflate(r, -1, -1), radius - 1, radius - 1, Paint(Pksm.LogoDeck));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), radius - 2, radius - 2, Stroke(Pksm.LogoGrid, 2));
        c.DrawLine(r.Left + radius, r.Bottom - 2, r.Right - radius, r.Bottom - 2, Stroke(Pksm.LogoBlue, 2));
    }

    /// <summary>The selected button: cyan focus edge with a cobalt body, readable under white labels.</summary>
    public static void SelectedButton(SKCanvas c, SKRect r, float radius = 6)
    {
        c.DrawRoundRect(r, radius, radius, Paint(Pksm.LogoCyan));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), radius - 1, radius - 1, Paint(Pksm.LogoGrid));
        c.DrawRoundRect(SKRect.Inflate(r, -4, -4), radius - 2, radius - 2, Stroke(Pksm.Ink.WithAlpha(0xB0), 1.5f));
    }

    /// <summary>Message window: white slab, blue border, ink text.</summary>
    public static void MaroonWindow(SKCanvas c, SKRect r)
    {
        c.DrawRoundRect(r, 8, 8, Paint(Pksm.SelectBorder));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), 7, 7, Paint(Pksm.Paper));
    }

    /// <summary>Section header: cobalt strip with a cyan signal edge.</summary>
    public static void HeaderStrip(SKCanvas c, SKRect r, string label, SKFont font)
    {
        c.DrawRoundRect(r, 3, 3, Paint(Pksm.ButtonBlueDeep));
        c.DrawRoundRect(new SKRect(r.Left + 1, r.Top + 1, r.Right - 1, r.Bottom - 2), 2, 2, Paint(Pksm.HeaderBlue));
        c.DrawRect(new SKRect(r.Left + 8, r.Bottom - 3, r.Right - 8, r.Bottom - 1), Paint(Pksm.LogoCyan));
        using var sh = new SKPaint { Color = SKColors.White };
        var baseline = r.MidY + font.Size * 0.35f;
        c.DrawText(label, r.Left + r.Height * 0.5f, baseline, SKTextAlign.Left, font, sh);
    }

    /// <summary>List row on a white card: shade idle, cobalt + cyan bar selected.</summary>
    public static void StripeRow(SKCanvas c, SKRect r, bool selected)
    {
        if (selected)
        {
            c.DrawRect(r, Paint(Pksm.LogoGrid));
            c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 4, r.Bottom), Paint(Pksm.LogoCyan));
        }
        else
        {
            c.DrawRect(r, Paint(Pksm.PaperShade.WithAlpha(0x50)));
        }
    }

    // ---------- Buttons ----------

    /// <summary>Primary action button: glossy black body, cyan rim; selected goes navy + light blue.</summary>
    public static void ChoiceButton(SKCanvas c, SKRect r, bool pressed = false, bool focused = false)
    {
        if (focused || pressed)
        {
            SelectedButton(c, r);
            return;
        }
        c.DrawRoundRect(r, 6, 6, Paint(Pksm.ButtonBlue));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), 5, 5, Paint(Pksm.Paper));
        c.DrawRoundRect(SKRect.Inflate(r, -4, -4), 4, 4, Stroke(Pksm.PaperEdgeDeep, 1.5f));
    }

    /// <summary>Blue vertical-stack menu button (View/Clear/Release/...): recessed-blue family.</summary>
    public static void StackButton(SKCanvas c, SKRect r, bool selected)
    {
        var fill = selected ? Pksm.RecessBlue : Pksm.StorageMenuBlueDeep;
        c.DrawRoundRect(r, 4, 4, Paint(Pksm.IndigoInk));
        c.DrawRoundRect(SKRect.Inflate(r, -1, -1), 3, 3, Paint(fill));
        c.DrawRoundRect(SKRect.Inflate(r, -3, -3), 2, 2, Stroke(Pksm.Ink, 2));
    }

    /// <summary>Bag pocket pill: navy surface, cyan pill, yellow-green rim when selected.</summary>
    public static void BagPill(SKCanvas c, SKRect r, bool selected)
    {
        c.DrawRoundRect(r, r.Height / 2, r.Height / 2, Paint(selected ? Pksm.BagCyanEdge : Pksm.BagCyan));
        c.DrawRoundRect(new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 2), r.Height / 2 - 1, r.Height / 2 - 1, Paint(selected ? Pksm.BagSelected : Pksm.BagNavyDeep));
    }

    /// <summary>Round count button: navy disc with white + or - glyph (bag rows).</summary>
    public static void CountButton(SKCanvas c, SKPoint center, float radius, bool minus)
    {
        var r = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        c.DrawOval(r, Paint(Pksm.BagNavy));
        c.DrawOval(new SKRect(r.Left + 1.5f, r.Top + 1.5f, r.Right - 1.5f, r.Bottom - 1.5f), Paint(Pksm.BagNavyDeep));
        var arm = radius * 0.5f;
        c.DrawRoundRect(new SKRect(center.X - arm, center.Y - 2, center.X + arm, center.Y + 2), 2, 2, Paint(Pksm.BagCyan));
        if (!minus)
            c.DrawRoundRect(new SKRect(center.X - 2, center.Y - arm, center.X + 2, center.Y + arm), 2, 2, Paint(Pksm.BagCyan));
    }

    /// <summary>Gift screen chip: pink-light idle, red selected with white rim.</summary>
    public static void LangChip(SKCanvas c, SKRect r, bool selected, SKColor idle, SKColor active)
    {
        c.DrawRoundRect(r, 3, 3, Paint(selected ? active : idle));
        if (selected)
            c.DrawRoundRect(new SKRect(r.Left + 1, r.Top + 1, r.Right - 1, r.Bottom - 1), 2, 2, Stroke(Pksm.Ink, 1.5f));
    }

    // ---------- Storage world ----------

    /// <summary>Box wallpaper: dark world tint + a quieter echo of the logo grid.</summary>
    public static void Wallpaper(SKCanvas c, SKRect r, SKColor baseColor)
    {
        c.DrawRect(r, Paint(baseColor));
        var grid = Pksm.WallpaperShade(baseColor).WithAlpha(0x60);
        using var p = new SKPaint { Color = grid, IsAntialias = false, StrokeWidth = 2 };
        for (var x = r.Left + 18; x < r.Right; x += 30) c.DrawLine(x, r.Top, x, r.Bottom, p);
        for (var y = r.Top + 18; y < r.Bottom; y += 30) c.DrawLine(r.Left, y, r.Right, y, p);
    }

    /// <summary>Red corner brackets: THE selection on grids and dex cells (Kalos style).</summary>
    public static void Crosshair(SKCanvas c, SKRect r, float arm = 16, float thick = 4)
    {
        var p = Paint(Pksm.CursorRed);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + arm, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + thick, r.Top + arm), p);
        c.DrawRect(new SKRect(r.Right - arm, r.Top, r.Right, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Top, r.Right, r.Top + arm), p);
        c.DrawRect(new SKRect(r.Left, r.Bottom - thick, r.Left + arm, r.Bottom), p);
        c.DrawRect(new SKRect(r.Left, r.Bottom - arm, r.Left + thick, r.Bottom), p);
        c.DrawRect(new SKRect(r.Right - arm, r.Bottom - thick, r.Right, r.Bottom), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Bottom - arm, r.Right, r.Bottom), p);
    }

    /// <summary>White corner brackets framing a whole grid area (the storage frame).</summary>
    public static void FrameBrackets(SKCanvas c, SKRect r, float arm = 16, float thick = 4)
    {
        var p = Paint(Pksm.Ink);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + arm, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + thick, r.Top + arm), p);
        c.DrawRect(new SKRect(r.Right - arm, r.Top, r.Right, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Top, r.Right, r.Top + arm), p);
        c.DrawRect(new SKRect(r.Left, r.Bottom - thick, r.Left + arm, r.Bottom), p);
        c.DrawRect(new SKRect(r.Left, r.Bottom - arm, r.Left + thick, r.Bottom), p);
        c.DrawRect(new SKRect(r.Right - arm, r.Bottom - thick, r.Right, r.Bottom), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Bottom - arm, r.Right, r.Bottom), p);
    }

    /// <summary>Box banner: white rounded bar, ink name, blue caps with white chevrons.</summary>
    public static void BoxNameBar(SKCanvas c, SKRect r, string label, SKFont font, bool canPrev, bool canNext)
    {
        c.DrawRoundRect(r, 5, 5, Paint(Pksm.PaperEdge));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), 4, 4, Paint(Pksm.Paper));
        var inner = SKRect.Inflate(r, -4, -4);
        using var ink = new SKPaint { Color = Pksm.Ink };
        c.DrawText(label, inner.MidX, inner.MidY + font.Size * 0.35f, SKTextAlign.Center, font, ink);
        void Cap(float cx, bool left)
        {
            var cap = new SKRect(cx - 11, inner.Top + 3, cx + 11, inner.Bottom - 3);
            c.DrawRoundRect(cap, 3, 3, Paint(Pksm.ButtonBlueDeep));
            c.DrawRoundRect(SKRect.Inflate(cap, -1, -1), 2, 2, Paint(Pksm.ButtonBlue));
        }
        Cap(inner.Left + 12, true);
        Cap(inner.Right - 12, false);

        void Chevron(float cx, bool left)
        {
            var path = new SKPath();
            if (left) { path.MoveTo(cx + 4, inner.Top + 5); path.LineTo(cx - 4, inner.MidY); path.LineTo(cx + 4, inner.Bottom - 5); }
            else { path.MoveTo(cx - 4, inner.Top + 5); path.LineTo(cx + 4, inner.MidY); path.LineTo(cx - 4, inner.Bottom - 5); }
            c.DrawPath(path, new SKPaint { Color = (left ? canPrev : canNext) ? SKColors.White : new SKColor(0xFF, 0xFF, 0xFF, 0x40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round });
        }
        Chevron(inner.Left + 12, true);
        Chevron(inner.Right - 12, false);
    }

    /// <summary>The red triangle cursor (touch/grid contexts).</summary>
    public static void Pointer(SKCanvas c, SKPoint tip, float size = 14)
    {
        var path = new SKPath();
        path.MoveTo(tip.X, tip.Y);
        path.LineTo(tip.X, tip.Y + size * 1.2f);
        path.LineTo(tip.X + size * 0.8f, tip.Y + size * 0.75f);
        path.Close();
        c.DrawPath(path, Paint(Pksm.CursorRed));
        c.DrawPath(path, Stroke(Pksm.Illegal, 1.5f));
    }

    /// <summary>Selection on a slot: red corner brackets, the one grid selection.</summary>
    public static void Selection(SKCanvas c, SKRect r)
    {
        var arm = Math.Min(14, Math.Min(r.Width, r.Height) * 0.4f);
        Crosshair(c, SKRect.Inflate(r, 2, 2), arm, 4);
    }

    /// <summary>Grab state: the slot ghost when carrying a mon (light-blue dashes).</summary>
    public static void CarryGhost(SKCanvas c, SKRect r)
    {
        var p = Stroke(Pksm.SelectBorder, 3);
        p.PathEffect = SKPathEffect.CreateDash([6, 5], 0);
        c.DrawRoundRect(r, 4, 4, p);
    }

    // ---------- Gift sparkle ----------

    /// <summary>White 4-point sparkle star (mystery-gift screens).</summary>
    public static void Sparkle(SKCanvas c, SKPoint center, float size)
    {
        var path = new SKPath();
        path.MoveTo(center.X, center.Y - size);
        path.LineTo(center.X + size * 0.28f, center.Y - size * 0.28f);
        path.LineTo(center.X + size, center.Y);
        path.LineTo(center.X + size * 0.28f, center.Y + size * 0.28f);
        path.LineTo(center.X, center.Y + size);
        path.LineTo(center.X - size * 0.28f, center.Y + size * 0.28f);
        path.LineTo(center.X - size, center.Y);
        path.LineTo(center.X - size * 0.28f, center.Y - size * 0.28f);
        path.Close();
        c.DrawPath(path, Paint(SKColors.White));
    }

    // ---------- Text ----------

    /// <summary>Pixel-font text helper.</summary>
    public static void DrawText(SKCanvas c, string text, float x, float y, SKFont font, SKPaint paint, SKTextAlign align)
    {
        c.DrawText(text, x, y, align, font, paint);
    }

    /// <summary>Text with the classic 2px offset game shadow. y is the baseline.</summary>
    public static void ShadowText(SKCanvas c, string text, float x, float y, SKFont font, SKColor color, SKColor shadow, SKTextAlign align = SKTextAlign.Left)
    {
        using (var sp = new SKPaint { Color = shadow })
            c.DrawText(text, x + 2, y + 2, align, font, sp);
        using (var fp = new SKPaint { Color = color })
            c.DrawText(text, x, y, align, font, fp);
    }

    /// <summary>Vertical-center variant: centers on the given y.</summary>
    public static void CenterText(SKCanvas c, string text, float x, float yCenter, SKFont font, SKColor color, SKColor shadow, SKTextAlign align = SKTextAlign.Left)
        => ShadowText(c, text, x, yCenter + font.Size * 0.35f, font, color, shadow, align);

    // ---------- Storage slots ----------

    /// <summary>An empty box slot: only a faint waiting ball on the wallpaper (the games
    /// draw no cell frames - the sprite itself is the slot).</summary>
    public static void Slot(SKCanvas c, SKRect r, SKColor wallpaper, bool empty)
    {
        if (!empty) return;
        var shade = Pksm.WallpaperShade(wallpaper);
        var ball = new SKRect(r.MidX - r.Width * 0.16f, r.MidY - r.Width * 0.16f, r.MidX + r.Width * 0.16f, r.MidY + r.Width * 0.16f);
        var p = Stroke(shade.WithAlpha(0x66), 2);
        c.DrawOval(ball, p);
        c.DrawLine(ball.MidX, ball.Top, ball.MidX, ball.Bottom, p);
        c.DrawOval(new SKRect(ball.MidX - 3, ball.MidY - 3, ball.MidX + 3, ball.MidY + 3), p);
    }

    /// <summary>Bottom hint rail: continuous dark console chrome with cyan key discs.</summary>
    public static void HintBar(SKCanvas c, SKRect bar, IReadOnlyList<(string Key, string Label)> prompts, SKFont font)
    {
        c.DrawRect(bar, Paint(Pksm.LogoVoid));
        c.DrawRect(new SKRect(bar.Left, bar.Top, bar.Right, bar.Top + 2), Paint(Pksm.LogoGrid));
        var x = bar.Left + 24;
        foreach (var (key, label) in prompts)
        {
            var kw = key.Length * font.Size * 0.62f + 14;
            var disc = new SKRect(x, bar.MidY - font.Size * 0.62f, x + kw, bar.MidY + font.Size * 0.62f);
            c.DrawOval(disc, Paint(Pksm.LogoCyan));
            c.DrawOval(disc, Stroke(Pksm.LogoGrid, 1.5f));
            CenterText(c, key, disc.MidX, bar.MidY, font, Pksm.LogoVoid, SKColors.Transparent, SKTextAlign.Center);
            CenterText(c, label, disc.Right + 10, bar.MidY, font, Pksm.Ink, SKColors.Transparent);
            x += kw + 10 + label.Length * font.Size * 0.62f + 34;
        }
    }
}
