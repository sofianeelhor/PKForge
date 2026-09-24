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

    // ---------- Colour math (shared by every drawn surface) ----------

    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    public static SKColor Lighter(SKColor c, float t) => Mix(c, SKColors.White, t);
    public static SKColor Darker(SKColor c, float t) => Mix(c, SKColors.Black, t);

    /// <summary>A signal colour toned into the navy world (the summary's band accents).</summary>
    public static SKColor Tone(SKColor signal) => Darker(Mix(signal, Pksm.LogoDeck, 0.32f), 0.08f);

    private static void Vertical(SKCanvas c, SKRect r, float radius, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = shader, IsAntialias = true };
        c.DrawRoundRect(r, radius, radius, p);
    }

    // ---------- Windows & panels ----------

    /// <summary>
    /// The device panel (the approved summary panel): navy body, a faint light along the
    /// top edge, the 2 dp cobalt bezel and a hard 3 dp pixel drop shadow. No glow.
    /// </summary>
    public static void Panel(SKCanvas c, SKRect r, SKColor? fill = null, float radius = 6)
    {
        var body = fill ?? Pksm.Paper;
        using (var shadow = Paint(Pksm.LogoVoid.WithAlpha(0x88)))
            c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 3, r.Right + 3, r.Bottom + 3), radius, radius, shadow);
        using (var p = Paint(body)) c.DrawRoundRect(r, radius, radius, p);
        var light = new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Top + Math.Min(26, r.Height / 3));
        Vertical(c, light, Math.Max(1, radius - 2), Lighter(body, 0.05f), body);
        using var edge = Stroke(Pksm.PaperEdge, 2);
        c.DrawRoundRect(SKRect.Inflate(r, -1, -1), radius, radius, edge);
    }

    /// <summary>A dark chrome window: the device panel.</summary>
    public static void DarkWindow(SKCanvas c, SKRect r, float radius = 6) => Panel(c, r, Pksm.Paper, radius);

    /// <summary>
    /// The resting menu button (the summary's idle tab): void outline, navy body with a
    /// whisper of gradient, a 1.5 dp cobalt edge. Quiet - the label carries it.
    /// </summary>
    public static void BlackButton(SKCanvas c, SKRect r, float radius = 4)
    {
        using (var outline = Paint(Pksm.ButtonBlueDeep)) c.DrawRoundRect(r, radius, radius, outline);
        var inner = SKRect.Inflate(r, -1, -1);
        Vertical(c, inner, radius - 1, Lighter(Pksm.LogoDeck, 0.04f), Darker(Pksm.LogoDeck, 0.05f));
        using var edge = Stroke(Pksm.LogoGrid, 1.5f);
        c.DrawRoundRect(SKRect.Inflate(inner, -0.75f, -0.75f), radius - 1, radius - 1, edge);
    }

    /// <summary>
    /// The selected / active button (the summary's active tab): the accent body (cobalt by
    /// default) with a soft top light and the thin pale focus rim. Never a cyan halo.
    /// </summary>
    public static void SelectedButton(SKCanvas c, SKRect r, float radius = 4, SKColor? accent = null)
    {
        var body = accent ?? Pksm.HeaderBlue;
        using (var outline = Paint(Pksm.ButtonBlueDeep)) c.DrawRoundRect(r, radius, radius, outline);
        var inner = SKRect.Inflate(r, -1.5f, -1.5f);
        Vertical(c, inner, radius - 1, Lighter(body, 0.18f), Darker(body, 0.08f));
        using var rim = Stroke(Pksm.Ink.WithAlpha(0x70), 1.2f);
        c.DrawRoundRect(SKRect.Inflate(inner, -0.6f, -0.6f), radius - 1, radius - 1, rim);
    }

    /// <summary>Message window: the device panel.</summary>
    public static void MaroonWindow(SKCanvas c, SKRect r) => Panel(c, r);

    /// <summary>Header strip: accent body (cobalt by default), soft top light, dark outline, white caption.</summary>
    public static void HeaderStrip(SKCanvas c, SKRect r, string label, SKFont font, SKColor? accent = null)
    {
        var body = accent ?? Pksm.HeaderBlue;
        using (var outline = Paint(Pksm.ButtonBlueDeep)) c.DrawRoundRect(r, 4, 4, outline);
        var inner = SKRect.Inflate(r, -1.5f, -1.5f);
        Vertical(c, inner, 3, Lighter(body, 0.16f), Darker(body, 0.1f));
        using (var light = Paint(SKColors.White.WithAlpha(0x16)))
            c.DrawRoundRect(new SKRect(inner.Left + 1, inner.Top + 1, inner.Right - 1, inner.MidY), 2, 2, light);
        var baseline = r.MidY + font.Size * 0.35f;
        using (var sh = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0x90) })
            c.DrawText(label, r.Left + 10 + 1, baseline + 1, SKTextAlign.Left, font, sh);
        using var ink = new SKPaint { Color = SKColors.White };
        c.DrawText(label, r.Left + 10, baseline, SKTextAlign.Left, font, ink);
    }

    /// <summary>List row inside a panel: soft alternating stripe idle; the selected button look chosen.</summary>
    public static void StripeRow(SKCanvas c, SKRect r, bool selected)
    {
        if (selected)
        {
            SelectedButton(c, r, 4);
            return;
        }
        using var p = Paint(Pksm.PaperShade.WithAlpha(0x70));
        c.DrawRoundRect(r, 3, 3, p);
    }

    // ---------- Buttons ----------

    /// <summary>Primary action button: the menu-button language; focused/pressed = the selected look.</summary>
    public static void ChoiceButton(SKCanvas c, SKRect r, bool pressed = false, bool focused = false)
    {
        if (focused || pressed) SelectedButton(c, r);
        else BlackButton(c, r);
    }

    /// <summary>Vertical-stack menu button (View/Clear/Release/...): the same menu-button language.</summary>
    public static void StackButton(SKCanvas c, SKRect r, bool selected)
    {
        if (selected) SelectedButton(c, r);
        else BlackButton(c, r);
    }

    /// <summary>Bag pocket tab: the menu-button language (resting / selected), no pill outline.</summary>
    public static void BagPill(SKCanvas c, SKRect r, bool selected)
    {
        if (selected) SelectedButton(c, r, 4);
        else BlackButton(c, r, 4);
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

    /// <summary>Gift screen language tab: resting menu button, or the selected look in the gift accent.</summary>
    public static void LangChip(SKCanvas c, SKRect r, bool selected, SKColor idle, SKColor active)
    {
        if (selected) SelectedButton(c, r, 3, Tone(active));
        else BlackButton(c, r, 3);
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
    public static void Crosshair(SKCanvas c, SKRect r, float arm = 16, float thick = 4, SKColor? color = null)
    {
        var p = Paint(color ?? Pksm.CursorRed);
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

    /// <summary>
    /// Box banner: a header strip with the name centred and two small menu-button caps
    /// carrying the chevrons (dimmed when there is nowhere to go).
    /// </summary>
    public static void BoxNameBar(SKCanvas c, SKRect r, string label, SKFont font, bool canPrev, bool canNext)
    {
        using (var outline = Paint(Pksm.ButtonBlueDeep)) c.DrawRoundRect(r, 4, 4, outline);
        var inner = SKRect.Inflate(r, -1.5f, -1.5f);
        Vertical(c, inner, 3, Lighter(Pksm.HeaderBlue, 0.16f), Darker(Pksm.HeaderBlue, 0.1f));
        using (var light = Paint(SKColors.White.WithAlpha(0x16)))
            c.DrawRoundRect(new SKRect(inner.Left + 1, inner.Top + 1, inner.Right - 1, inner.MidY), 2, 2, light);
        var baseline = inner.MidY + font.Size * 0.35f;
        using (var sh = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0x90) })
            c.DrawText(label, inner.MidX + 1, baseline + 1, SKTextAlign.Center, font, sh);
        using (var ink = new SKPaint { Color = SKColors.White })
            c.DrawText(label, inner.MidX, baseline, SKTextAlign.Center, font, ink);

        var capWidth = inner.Height * 1.15f;
        void Cap(SKRect cap, bool left, bool enabled)
        {
            BlackButton(c, cap, 3);
            using var path = new SKPath();
            var cx = cap.MidX;
            var arm = Math.Min(4.5f, cap.Height * 0.2f);
            if (left) { path.MoveTo(cx + arm * 0.6f, cap.MidY - arm); path.LineTo(cx - arm * 0.6f, cap.MidY); path.LineTo(cx + arm * 0.6f, cap.MidY + arm); }
            else { path.MoveTo(cx - arm * 0.6f, cap.MidY - arm); path.LineTo(cx + arm * 0.6f, cap.MidY); path.LineTo(cx - arm * 0.6f, cap.MidY + arm); }
            using var chevron = new SKPaint
            {
                Color = enabled ? Pksm.Ink : Pksm.Ink.WithAlpha(0x40), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            };
            c.DrawPath(path, chevron);
        }
        Cap(new SKRect(inner.Left + 2, inner.Top + 2, inner.Left + 2 + capWidth, inner.Bottom - 2), true, canPrev);
        Cap(new SKRect(inner.Right - 2 - capWidth, inner.Top + 2, inner.Right - 2, inner.Bottom - 2), false, canNext);
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
    public static void Selection(SKCanvas c, SKRect r, SKColor? color = null)
    {
        var arm = Math.Min(14, Math.Min(r.Width, r.Height) * 0.4f);
        Crosshair(c, SKRect.Inflate(r, 2, 2), arm, 4, color);
    }

    /// <summary>A slot inside a live rectangle gesture: a translucent wash in the cursor's
    /// colour (marking) or a red one (unmarking), so the player sees the span before release.</summary>
    public static void RangeWash(SKCanvas c, SKRect r, bool mark)
    {
        using var wash = Paint((mark ? Pksm.CursorGreen : Pksm.CursorRed).WithAlpha(0x55));
        c.DrawRoundRect(r, 4, 4, wash);
    }

    /// <summary>Grab state: the slot ghost when carrying a mon (light-blue dashes).</summary>
    public static void CarryGhost(SKCanvas c, SKRect r)
    {
        var p = Stroke(Pksm.SelectBorder, 3);
        p.PathEffect = SKPathEffect.CreateDash([6, 5], 0);
        c.DrawRoundRect(r, 4, 4, p);
    }

    /// <summary>
    /// The organizer's mark on a slot: a cobalt disc with a cyan check, top-left. Shared by the
    /// save-side organizer and the bank vault so a marked mon wears the same badge in both.
    /// </summary>
    public static void MarkBadge(SKCanvas c, SKRect r)
    {
        using var badge = Paint(Pksm.SelectBorder);
        using var check = Stroke(Pksm.IndigoInk, 3);
        check.StrokeCap = SKStrokeCap.Round;
        var size = Math.Min(r.Width, r.Height);
        var cx = r.Left + size * 0.15f;
        var cy = r.Top + size * 0.15f;
        var radius = size * 0.12f;
        c.DrawCircle(cx, cy, radius, badge);
        c.DrawLine(cx - radius * 0.45f, cy, cx - radius * 0.1f, cy + radius * 0.4f, check);
        c.DrawLine(cx - radius * 0.1f, cy + radius * 0.4f, cx + radius * 0.5f, cy - radius * 0.35f, check);
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

    /// <summary>Bottom hint rail: a device panel carrying cyan key discs and pale labels.</summary>
    public static void HintBar(SKCanvas c, SKRect bar, IReadOnlyList<(string Key, string Label)> prompts, SKFont font)
    {
        Panel(c, SKRect.Inflate(bar, -3, -3));
        var total = 0f;
        foreach (var (key, label) in prompts)
            total += Math.Max(font.Size * 1.3f, font.MeasureText(key) + font.Size * 0.9f) + 8 + font.MeasureText(label) + font.Size * 1.4f;
        var x = bar.MidX - (total - font.Size * 1.4f) / 2;
        using var disc = Paint(Pksm.LogoCyan);
        foreach (var (key, label) in prompts)
        {
            var kw = Math.Max(font.Size * 1.3f, font.MeasureText(key) + font.Size * 0.9f);
            var pill = new SKRect(x, bar.MidY - font.Size * 0.62f, x + kw, bar.MidY + font.Size * 0.62f);
            c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, disc);
            CenterText(c, key, pill.MidX - 1, bar.MidY, font, Pksm.LogoVoid, SKColors.Transparent, SKTextAlign.Center);
            CenterText(c, label, pill.Right + 8 - 2, bar.MidY, font, Pksm.Ink, SKColors.Transparent);
            x += kw + 8 + font.MeasureText(label) + font.Size * 1.4f;
        }
    }
}
