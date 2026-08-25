using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The drawn chrome of the PKSM/DS language: white windows with warm-grey borders, maroon
/// header strips, cream choice buttons with cyan rims, the blue vertical menu stack, gift-pink
/// sparkles, box wallpapers and crosshairs. Everything composes from <see cref="Pksm"/> tokens.
/// Pixel-snap your rects: these painters draw on integer coordinates.
/// </summary>
public static class PksmPaint
{
    // ---------- Shared brushes ----------

    private static SKPaint Paint(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill };
    private static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    // ---------- Windows & panels ----------

    /// <summary>White panel with a warm-grey 2px border and a subtle bottom shade: the PKSM card.</summary>
    public static void Panel(SKCanvas c, SKRect r, SKColor? fill = null, float radius = 6)
    {
        c.DrawRoundRect(r, radius, radius, Paint((fill ?? Pksm.Paper).WithAlpha(0xFF)));
        c.DrawRoundRect(r, radius, radius, Stroke(Pksm.Chrome, 2));
        // 1px inner light rim on the top edge, like the molded plastic of the box-name bar.
        var rim = new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Top + 4);
        c.DrawRect(rim, Paint(Pksm.Paper));
    }

    /// <summary>Gen-5 message window: flat maroon slab with a darker outline.</summary>
    public static void MaroonWindow(SKCanvas c, SKRect r)
    {
        c.DrawRect(r, Paint(Pksm.Maroon));
        c.DrawRect(r, Stroke(Pksm.MaroonDeep, 2));
    }

    /// <summary>Gen-5 section header: maroon strip with a dark bottom edge, white label, inset on a white panel.</summary>
    public static void HeaderStrip(SKCanvas c, SKRect r, string label, SKFont font)
    {
        c.DrawRoundRect(r, 3, 3, Paint(Pksm.MaroonDeep));
        c.DrawRoundRect(new SKRect(r.Left + 1, r.Top + 1, r.Right - 1, r.Bottom - 3), 2, 2, Paint(Pksm.Maroon));
        using var sh = new SKPaint { Color = SKColors.White };
        var baseline = r.MidY + font.Size * 0.35f;
        c.DrawText(label, r.Left + r.Height * 0.5f, baseline, SKTextAlign.Left, font, sh);
    }

    /// <summary>Alternating stripe row (like eventmenu bars): use for list rows on white panels.</summary>
    public static void StripeRow(SKCanvas c, SKRect r, bool selected)
    {
        if (selected)
        {
            c.DrawRect(r, Paint(Pksm.IndigoLight));
            c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 4, r.Bottom), Paint(Pksm.Indigo));
        }
        else
        {
            c.DrawRect(r, Paint(Pksm.PaperShade.WithAlpha(0x30)));
        }
    }

    // ---------- Buttons ----------

    /// <summary>Blue vertical-stack menu button (View/Clear/Release/...): dark outline, blue fill, white inner border.</summary>
    public static void StackButton(SKCanvas c, SKRect r, bool selected)
    {
        var fill = selected ? Pksm.StorageMenuBlue : Pksm.StorageMenuBlueDeep;
        c.DrawRoundRect(r, 4, 4, Paint(Pksm.IndigoInk));
        c.DrawRoundRect(SKRect.Inflate(r, -1, -1), 3, 3, Paint(fill));
        c.DrawRoundRect(SKRect.Inflate(r, -3, -3), 2, 2, Stroke(Pksm.Paper, 2));
    }

    /// <summary>The STATS/MOVES/SAVE choice: cream fill, cyan rim, white inner rim; gold focus ring.</summary>
    public static void ChoiceButton(SKCanvas c, SKRect r, bool pressed = false, bool focused = false)
    {
        var fill = pressed ? Pksm.ChoiceFillPress : Pksm.ChoiceFill;
        var rim = pressed ? Pksm.ChoiceRimDeep : Pksm.ChoiceRim;
        c.DrawRoundRect(r, 6, 6, Paint(rim));
        c.DrawRoundRect(SKRect.Inflate(r, -2, -2), 5, 5, Paint(fill));
        c.DrawRoundRect(SKRect.Inflate(r, -3, -3), 4, 4, Stroke(Pksm.Paper, 1.5f));
        if (focused)
            c.DrawRoundRect(SKRect.Inflate(r, 4, 4), 8, 8, Stroke(Pksm.FocusGold, 3));
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

    /// <summary>Gift screen Yes/No chip: grey idle, red yes/dark no.</summary>
    public static void LangChip(SKCanvas c, SKRect r, bool selected, SKColor idle, SKColor active)
    {
        c.DrawRoundRect(r, 3, 3, Paint(selected ? active : idle));
        if (selected)
            c.DrawRoundRect(new SKRect(r.Left + 1, r.Top + 1, r.Right - 1, r.Bottom - 1), 2, 2, Stroke(Pksm.Paper, 1.5f));
    }

    // ---------- Storage world ----------

    /// <summary>Box wallpaper: saturated flat + faint 8px dot lattice, the PC-box feel.</summary>
    public static void Wallpaper(SKCanvas c, SKRect r, SKColor baseColor)
    {
        c.DrawRect(r, Paint(baseColor));
        var dot = Pksm.WallpaperShade(baseColor).WithAlpha(0x28);
        var d = Paint(dot);
        for (var y = (int)r.Top + 6; y < r.Bottom - 3; y += 12)
            for (var x = (int)r.Left + 6; x < r.Right - 3; x += 12)
                c.DrawRect(new SKRect(x, y, x + 3, y + 3), d);
    }

    /// <summary>White corner brackets framing the box grid (the storage crosshair).</summary>
    public static void Crosshair(SKCanvas c, SKRect r, float arm = 16, float thick = 4)
    {
        var p = Paint(Pksm.Paper);
        // tl
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + arm, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + thick, r.Top + arm), p);
        // tr
        c.DrawRect(new SKRect(r.Right - arm, r.Top, r.Right, r.Top + thick), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Top, r.Right, r.Top + arm), p);
        // bl
        c.DrawRect(new SKRect(r.Left, r.Bottom - thick, r.Left + arm, r.Bottom), p);
        c.DrawRect(new SKRect(r.Left, r.Bottom - arm, r.Left + thick, r.Bottom), p);
        // br
        c.DrawRect(new SKRect(r.Right - arm, r.Bottom - thick, r.Right, r.Bottom), p);
        c.DrawRect(new SKRect(r.Right - thick, r.Bottom - arm, r.Right, r.Bottom), p);
    }

    /// <summary>Box-name bar drawn (when the bitmap is not used): cream bar, grey border, yellow caps with chevrons.</summary>
    public static void BoxNameBar(SKCanvas c, SKRect r, string label, SKFont font, bool canPrev, bool canNext)
    {
        c.DrawRoundRect(r, 4, 4, Paint(Pksm.Chrome));
        var inner = new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 2);
        c.DrawRoundRect(inner, 3, 3, Paint(new SKColor(0xFF, 0xF7, 0xEE)));
        using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
        DrawText(c, label, inner.MidX, inner.MidY, font, ink, SKTextAlign.Center);

        var cap = inner.Height - 8;
        void Cap(float x, bool left)
        {
            if (!(left ? canPrev : canNext)) return;
            var rect = new SKRect(x, inner.Top + 4, x + cap, inner.Bottom - 4);
            c.DrawRoundRect(rect, 2, 2, Paint(new SKColor(0xF2, 0xC1, 0x4E)));
            c.DrawRoundRect(rect, 2, 2, Stroke(new SKColor(0xB8, 0x8A, 0x24), 1.5f));
            var m = rect.MidX;
            var my = rect.MidY;
            var path = new SKPath();
            if (left)
            {
                path.MoveTo(m + 3, my - 4); path.LineTo(m - 3, my); path.LineTo(m + 3, my + 4);
            }
            else
            {
                path.MoveTo(m - 3, my - 4); path.LineTo(m + 3, my); path.LineTo(m - 3, my + 4);
            }
            c.DrawPath(path, new SKPaint { Color = new SKColor(0x5A, 0x45, 0x12), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 });
        }
        Cap(inner.Left + 4, true);
        Cap(inner.Right - cap - 4, false);
    }

    /// <summary>The red glove cursor: a chunky triangle pointer.</summary>
    public static void Pointer(SKCanvas c, SKPoint tip, float size = 14)
    {
        var path = new SKPath();
        path.MoveTo(tip.X, tip.Y);
        path.LineTo(tip.X, tip.Y + size * 1.2f);
        path.LineTo(tip.X + size * 0.8f, tip.Y + size * 0.75f);
        path.Close();
        c.DrawPath(path, Paint(Pksm.CursorRed));
        c.DrawPath(path, Stroke(new SKColor(0x8F, 0x27, 0x1E), 1.5f));
    }

    /// <summary>Selection frame around a slot: white outer, dark inner, corner ticks.</summary>
    public static void Selection(SKCanvas c, SKRect r)
    {
        c.DrawRoundRect(SKRect.Inflate(r, 3, 3), 4, 4, Stroke(Pksm.Paper, 3));
        c.DrawRoundRect(SKRect.Inflate(r, 5, 5), 5, 5, Stroke(Pksm.FocusGold, 2));
    }

    /// <summary>Grab state: the slot ghost when carrying a mon (dashed gold corners).</summary>
    public static void CarryGhost(SKCanvas c, SKRect r)
    {
        var p = Stroke(Pksm.FocusGold, 3);
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

    /// <summary>Baseline-aware pixel text: the NDS12 face renders crispest with AA off.</summary>
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

    /// <summary>A box slot on the wallpaper: soft white fill, crisp white border, faint ball outline when empty.</summary>
    public static void Slot(SKCanvas c, SKRect r, SKColor wallpaper, bool empty)
    {
        var fill = new SKColor(0xFF, 0xFF, 0xFF, 0x5C);
        c.DrawRoundRect(r, 4, 4, Paint(fill));
        c.DrawRoundRect(r, 4, 4, Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0xB4), 1.5f));
        if (empty)
        {
            var ball = new SKRect(r.MidX - r.Width * 0.18f, r.MidY - r.Width * 0.18f, r.MidX + r.Width * 0.18f, r.MidY + r.Width * 0.18f);
            c.DrawOval(ball, Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0x60), 2));
            c.DrawLine(ball.MidX, ball.Top, ball.MidX, ball.Bottom, Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0x60), 2));
        }
    }

    /// <summary>Bottom hint bar: translucent dark strip, white border, button-key + label pairs.</summary>
    public static void HintBar(SKCanvas c, SKRect bar, IReadOnlyList<(string Key, string Label)> prompts, SKFont font)
    {
        c.DrawRect(bar, Paint(new SKColor(0x1E, 0x28, 0x22, 0xB4)));
        c.DrawRect(new SKRect(bar.Left, bar.Top, bar.Right, bar.Top + 2), Paint(new SKColor(0xFF, 0xFF, 0xFF, 0x90)));
        var x = bar.Left + 24;
        foreach (var (key, label) in prompts)
        {
            var kw = key.Length * font.Size * 0.62f + 14;
            var disc = new SKRect(x, bar.MidY - font.Size * 0.62f, x + kw, bar.MidY + font.Size * 0.62f);
            c.DrawOval(disc, Paint(Pksm.StorageMenuBlue));
            c.DrawOval(disc, Stroke(Pksm.Paper, 1.5f));
            CenterText(c, key, disc.MidX, bar.MidY, font, Pksm.Paper, SKColors.Transparent, SKTextAlign.Center);
            CenterText(c, label, disc.Right + 10, bar.MidY, font, Pksm.Paper, SKColors.Black.WithAlpha(0x50));
            x += kw + 10 + label.Length * font.Size * 0.62f + 34;
        }
    }
}
