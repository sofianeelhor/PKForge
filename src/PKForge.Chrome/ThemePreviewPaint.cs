using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// A theme in miniature, for the color scheme picker: the grid page, the box header with its
/// banner, the box well with its slots, and the editor panel with its rows and a section chip.
/// Drawn from the theme's own roles, never from <see cref="ColorTheme.Current"/>, so every theme
/// can be previewed while another one is in use.
/// </summary>
public static class ThemePreviewPaint
{
    public static void Paint(SKCanvas c, SKRect r, ColorTheme t, SKTypeface typeface)
    {
        var u = r.Height / 100f; // the drawing is laid out on a 160×100 grid
        c.Save();
        c.ClipRoundRect(new SKRoundRect(r, 4 * u), antialias: true);

        // The page: the deck with the logo's grid lines.
        using (var page = Fill(t.Deck)) c.DrawRect(r, page);
        using (var line = new SKPaint { Color = t.Grid.WithAlpha(0x70), StrokeWidth = Math.Max(1, 0.6f * u) })
        {
            for (var x = r.Left + 5 * u; x < r.Right; x += 7 * u) c.DrawLine(x, r.Top, x, r.Bottom, line);
            for (var y = r.Top + 5 * u; y < r.Bottom; y += 7 * u) c.DrawLine(r.Left, y, r.Right, y, line);
        }

        // The box header: frame, banner, chevrons.
        var header = SKRect.Create(r.Left + 4 * u, r.Top + 5 * u, 92 * u, 12 * u);
        using (var frame = Fill(t.Frame)) c.DrawRoundRect(header, 3 * u, 3 * u, frame);
        using (var edge = Stroke(t.FrameEdge, 0.8f * u)) c.DrawRoundRect(header, 3 * u, 3 * u, edge);
        var banner = SKRect.Create(header.Left + 14 * u, header.Top + 1.5f * u, 64 * u, 9 * u);
        using (var path = Slanted(banner, 3 * u))
        using (var gradient = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, banner.Top), new SKPoint(0, banner.Bottom),
                [t.BannerTop, t.BannerBottom], SKShaderTileMode.Clamp),
        })
        {
            c.DrawPath(path, gradient);
            using var rim = Stroke(t.Bright, 0.6f * u);
            c.DrawPath(path, rim);
        }
        using (var font = new SKFont(typeface, 6 * u))
        using (var ink = Fill(t.Bright))
        {
            c.DrawTextFallback("Box 1", banner.MidX, banner.MidY + 2 * u, SKTextAlign.Center, font, ink);
            c.DrawTextFallback("«", header.Left + 6 * u, header.MidY + 2 * u, SKTextAlign.Center, font, ink);
            c.DrawTextFallback("»", header.Right - 6 * u, header.MidY + 2 * u, SKTextAlign.Center, font, ink);
        }

        // The well, with a few Pokémon in its first slots.
        var well = SKRect.Create(r.Left + 4 * u, r.Top + 20 * u, 92 * u, 75 * u);
        using (var fill = Fill(t.Well)) c.DrawRoundRect(well, 4 * u, 4 * u, fill);
        using (var edge = Stroke(t.WellEdge, 0.8f * u)) c.DrawRoundRect(well, 4 * u, 4 * u, edge);
        // Its slots.
        var cell = new SKSize(well.Width / 5, well.Height / 4);
        using (var slot = Fill(t.WellEdge.WithAlpha(0x60)))
            for (var i = 0; i < 20; i++)
                c.DrawCircle(well.Left + (i % 5 + 0.5f) * cell.Width, well.Top + (i / 5 + 0.5f) * cell.Height, 3.2f * u, slot);

        // The editor panel: name tab, rows on alternating bands, a section chip.
        var panel = SKRect.Create(r.Left + 100 * u, r.Top + 5 * u, 56 * u, 90 * u);
        using (var fill = Fill(t.Well)) c.DrawRoundRect(panel, 4 * u, 4 * u, fill);
        using (var edge = Stroke(t.FrameEdge, 0.8f * u)) c.DrawRoundRect(panel, 4 * u, 4 * u, edge);
        var tab = SKRect.Create(panel.Left, panel.Top, 34 * u, 10 * u);
        using (var path = Slanted(tab, 3 * u, leftSquare: true))
        using (var gradient = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, tab.Top), new SKPoint(0, tab.Bottom),
                [t.BannerTop, t.BannerBottom], SKShaderTileMode.Clamp),
        })
            c.DrawPath(path, gradient);

        using var small = new SKFont(typeface, 4.6f * u);
        using var label = Fill(t.Label);
        using var value = Fill(t.Value);
        var rowTop = panel.Top + 13 * u;
        for (var i = 0; i < 6; i++)
        {
            var row = SKRect.Create(panel.Left + 2 * u, rowTop + i * 8 * u, panel.Width - 4 * u, 7 * u);
            if (i == 3)
            {
                // A section chip between its two rails.
                using var rail = Fill(t.Rim.WithAlpha(0xB0));
                c.DrawRect(SKRect.Create(row.Left + 2 * u, row.MidY - 0.3f * u, row.Width - 4 * u, 0.6f * u), rail);
                var chip = SKRect.Create(row.MidX - 12 * u, row.Top + 0.5f * u, 24 * u, 6 * u);
                using var chipPath = Slanted(chip, 2 * u);
                using var chipFill = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, chip.Top), new SKPoint(0, chip.Bottom),
                        [t.ChipTop, t.ChipBottom], SKShaderTileMode.Clamp),
                };
                c.DrawPath(chipPath, chipFill);
                using var chipRim = Stroke(t.Rim, 0.6f * u);
                c.DrawPath(chipPath, chipRim);
                continue;
            }
            using (var band = Fill(t.Band.WithAlpha(i % 2 == 0 ? (byte)150 : (byte)60))) c.DrawRect(row, band);
            c.DrawTextFallback("Nature", row.Left + 2 * u, row.MidY + 1.6f * u, SKTextAlign.Left, small, label);
            c.DrawTextFallback("Timid", row.Left + 24 * u, row.MidY + 1.6f * u, SKTextAlign.Left, small, value);
        }
        c.Restore();
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true };
    private static SKPaint Stroke(SKColor color, float width) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };

    // A banner or tab with its right edge slanting out toward the bottom (and its left edge too
    // unless it is flush with a panel's side).
    private static SKPath Slanted(SKRect r, float slant, bool leftSquare = false)
    {
        var path = new SKPath();
        path.MoveTo(leftSquare ? r.Left : r.Left + slant, r.Top);
        path.LineTo(r.Right - slant, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left, r.Bottom);
        path.Close();
        return path;
    }
}
