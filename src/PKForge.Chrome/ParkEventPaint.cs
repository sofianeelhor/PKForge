using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// A Poképark event, announced as the games announce a big encounter, then shown in the park's
/// own panels. First the cinematic: two white flashes, black stripes crossing in from both sides
/// (the wild encounter), then a band bursts open across the black as in Black and White's VS
/// screens, speed lines streaming through it, the Pokémon's animated Showdown sprite sliding in
/// and the event's name slamming in beside it. A secret event shows the Pokémon as a black
/// silhouette with "???" until a white flash reveals it. Then the black parts like a curtain on
/// the park, where the page has flown the camera to the moment, and the title plate, the Pokémon
/// standing big beside the dialogue box, and the choices come in. Everything comes from the time since
/// it started, so one call draws any frame.
/// </summary>
public static class ParkEventPaint
{
    /// <summary>When the cinematic gives way to the park (ms after the start); skipping jumps here.</summary>
    public const long ParkAt = 3400;
    /// <summary>When the choices appear (ms after the start).</summary>
    public const long ChoicesAt = ParkAt + 900;
    private const long DialogueAt = ParkAt + 250, RevealAt = 2000;
    // An evolution takes longer in the band: the glowing silhouette switches between the two forms.
    private const long Stretch = 700, EvolutionRevealAt = RevealAt + Stretch;

    /// <summary>An evolution: the form it is evolving into, whether it stops before the end (B in
    /// the games), and what the band says until then ("What? Eevee is evolving!").</summary>
    public sealed record Evolution(Func<long, SKBitmap?> Into, bool IntoShiny, bool Stops, string Before);

    /// <summary>When the cinematic gives way to the park for this event.</summary>
    public static long ParkAtFor(bool evolution) => ParkAt + (evolution ? Stretch : 0);
    /// <summary>When the choices appear for this event.</summary>
    public static long ChoicesAtFor(bool evolution) => ChoicesAt + (evolution ? Stretch : 0);
    private const float CharactersPerSecond = 45;

    private static readonly SKColor SecretTop = new(0x9a, 0x6a, 0xe0), SecretBottom = new(0x63, 0x3f, 0xa8);
    private static readonly SKColor SpecialLine = new(0xff, 0xd8, 0x00), SecretLine = new(0xb8, 0x8c, 0xff);

    /// <param name="t">Milliseconds since the event started.</param>
    /// <param name="sprites">Each Pokémon in it: its animated sprite frame at a time (Showdown, null while it loads), and whether it is shiny.</param>
    /// <returns>Where each choice is on the screen, for touch (empty before they show).</returns>
    public static SKRect[] Paint(SKCanvas c, int w, int h, long t, string eventName, string speaker, string text, bool secret,
        IReadOnlyList<(Func<long, SKBitmap?> Frame, bool Shiny)> sprites, IReadOnlyList<string> choices, int selected, SKTypeface face,
        Evolution? evolution = null)
    {
        var unit = w / 1920f;
        var stretch = evolution is null ? 0 : Stretch;
        if (t < ParkAt + stretch + 450)
        {
            Encounter(c, w, h, t, unit, ParkAt + stretch);
            Band(c, w, h, t, eventName, secret, sprites, face, unit, evolution, ParkAt + stretch);
            Curtain(c, w, h, t - stretch);
            Flashes(c, t);
            return [];
        }
        // After an evolution, the one standing by the dialogue is the new form (or the old, if it stopped).
        if (evolution is { Stops: false } evolved) sprites = [(evolved.Into, evolved.IntoShiny)];
        Bars(c, w, h, unit);
        TitlePlate(c, h, t - stretch, eventName, secret, face, unit);
        return Dialogue(c, w, h, t - stretch, speaker, text, sprites, choices, selected, face, unit);
    }

    // 0 to 1 over a span, easing out.
    private static float Ease(long t, long from, long span)
    {
        var x = Math.Clamp((t - from) / (float)span, 0, 1);
        return 1 - (1 - x) * (1 - x) * (1 - x);
    }

    // The wild encounter's two white flashes.
    private static void Flashes(SKCanvas c, long t)
    {
        var alpha = t switch
        {
            < 90 => 230f,
            < 170 => 0f,
            < 260 => 230f,
            < 460 => 230f * (1 - (t - 260) / 200f),
            _ => 0f,
        };
        if (alpha > 0) c.DrawColor(SKColors.White.WithAlpha((byte)alpha), SKBlendMode.SrcOver);
    }

    // 0 to 1 overshooting a little before settling, for what slams in.
    private static float Back(long t, long from, long span)
    {
        var x = Math.Clamp((t - from) / (float)span, 0, 1) - 1;
        const float s = 1.7f;
        return 1 + x * x * ((s + 1) * x + s);
    }

    // The wild encounter: black stripes cross in from both sides until the screen is black.
    private static void Encounter(SKCanvas c, int w, int h, long t, float unit, long parkAt)
    {
        if (t >= parkAt - 100) return; // the curtain takes over
        const int stripes = 10;
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
        var height = MathF.Ceiling(h / (float)stripes);
        for (var i = 0; i < stripes; i++)
        {
            var cover = Ease(t, 300 + i * 22, 300) * w;
            if (cover <= 0) continue;
            var left = i % 2 == 0 ? 0 : w - cover;
            c.DrawRect(left, i * height, cover, height, paint);
        }
    }

    // The VS band: it bursts open from a line, speed lines stream through it, the Pokémon slides in
    // and the name slams in beside it; a secret shows a silhouette until a flash reveals it, an
    // evolution glows white and switches between its two forms faster and faster until the flash.
    private static void Band(SKCanvas c, int w, int h, long t, string eventName, bool secret, IReadOnlyList<(Func<long, SKBitmap?> Frame, bool Shiny)> sprites,
        SKTypeface face, float unit, Evolution? evolution, long parkAt)
    {
        var open = Ease(t, 750, 220) * (1 - Ease(t, parkAt - 350, 250));
        var reveal = evolution is null ? RevealAt : EvolutionRevealAt;
        var hidden = secret || evolution is not null;
        if (open <= 0) return;
        var line = secret ? SecretLine : SpecialLine;
        var half = 240f * unit * open;
        var band = new SKRect(0, h / 2f - half, w, h / 2f + half);
        using var paint = new SKPaint { IsAntialias = false };
        paint.Color = secret ? new SKColor(0x22, 0x14, 0x3a) : new SKColor(0x14, 0x22, 0x44);
        c.DrawRect(band, paint);

        // The name hits: the band shakes for a moment.
        var shake = t is >= 1330 and < 1480 ? (1480 - t) / 150f * 9f * unit
            : evolution is not null && t >= reveal && t < reveal + 200 ? (reveal + 200 - t) / 200f * 12f * unit : 0;
        c.Save();
        c.Translate(shake * MathF.Sin(t * .9f), shake * MathF.Cos(t * 1.3f));
        c.ClipRect(band);
        // Speed lines, streaming right to left at different speeds.
        for (var i = 0; i < 34; i++)
        {
            var seed = (uint)(i * 2654435761);
            var y = band.Top + (seed % 1000) / 1000f * band.Height;
            var length = (120 + seed % 380) * unit;
            var speed = (2.2f + (seed >> 10) % 100 / 40f) * unit;
            var x = w - ((t * speed + seed % 2000 * unit) % (w + length));
            paint.Color = (i % 4 == 0 ? SKColors.White : line).WithAlpha((byte)(i % 4 == 0 ? 120 : 70));
            c.DrawRect(x, y, length, MathF.Max(2, (2 + seed % 4) * unit), paint);
        }

        // The Pokémon slide in from the right one after the other and land left of the middle,
        // big and crisp, with a hop as they land (their cry, in the games).
        var count = evolution is null ? Math.Min(3, sprites.Count) : Math.Min(1, sprites.Count);
        for (var i = count - 1; i >= 0; i--)
        {
            var (frame, white, shiny) = (sprites[i].Frame(t), false, sprites[i].Shiny);
            if (evolution is { } evo && t >= 1300)
            {
                if (t < reveal)
                {
                    // Faster and faster: the switches speed up as the reveal comes.
                    var seconds = (t - 1300) / 1000f;
                    var p = (t - 1300f) / (reveal - 1300);
                    var into = (int)(seconds * (2.5f + 22f * p * p / 3)) % 2 == 1;
                    (frame, white) = (into ? evo.Into(t) ?? frame : frame, true);
                }
                else if (!evo.Stops) (frame, shiny) = (evo.Into(t) ?? frame, evo.IntoShiny);
            }
            if (frame is null) continue;
            var seen = Opaque(frame);
            if (seen.IsEmpty) continue;
            var start = 900 + i * 120L;
            var target = (count == 1 ? 330f : 250f) * unit;
            var scale = MathF.Max(1, MathF.Floor(target / MathF.Max(seen.Height, seen.Width * .75f)));
            float width = seen.Width * scale, height = seen.Height * scale;
            var land = w * (count == 1 ? .27f : .14f + i * .13f);
            var slide = 1 - Ease(t, start, 380);
            var x = land + slide * w * .8f;
            var hop = t > start + 380 && t < start + 680 ? MathF.Sin((t - start - 380) / 300f * MathF.PI) * 26f * unit : 0;
            var bottom = band.Bottom - 30f * unit - (count > 1 && i == 1 ? 40f * unit : 0) - hop;
            var dest = SKRect.Create(x - width / 2, bottom - height, width, height);
            using var crisp = new SKPaint { IsAntialias = false };
            if (secret && t < RevealAt)
                crisp.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(10, 6, 18), SKBlendMode.SrcIn);
            if (white)
                crisp.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0xf6, 0xfa, 0xff), SKBlendMode.SrcIn);
            if (slide > .05f)
                for (var k = 3; k >= 1; k--)
                {
                    using var ghost = new SKPaint { IsAntialias = false, Color = SKColors.White.WithAlpha((byte)(70 * slide / k)),
                        ColorFilter = SKColorFilter.CreateBlendMode(line, SKBlendMode.SrcIn) };
                    c.DrawBitmap(frame, seen, SKRect.Create(dest.Left + k * 80f * unit * slide, dest.Top, width, height), ghost);
                }
            c.DrawBitmap(frame, seen, dest, crisp);
            // A shiny's sparkle, as when one comes out in battle: once it shows its colors.
            var shown = hidden ? reveal : start + 380;
            if (shiny && t >= shown && t < shown + 900) Sparkle(c, dest, t - shown, unit);
        }

        // The words: what kind of event, and its name slamming in from the right.
        var revealed = !hidden || t >= reveal;
        var textX = w * (count > 1 ? .5f : .45f);
        var headline = revealed ? eventName : evolution?.Before ?? "???";
        using var small = new SKFont(face, 44f * unit);
        using var big = new SKFont(face, 88f * unit);
        // A long headline gets smaller to fit beside the Pokémon.
        big.Size = MathF.Min(big.Size, big.Size * (w - textX - 50f * unit) / MathF.Max(1, big.MeasureFallback(headline)));
        var slam = Back(t, 1150, 360);
        if (t >= 1150)
        {
            var dx = (1 - slam) * w * .6f;
            Shadowed(c, secret ? (revealed ? "SECRET EVENT" : "???") : "SPECIAL EVENT", textX + dx, band.MidY - 60f * unit, small, line, unit);
            Shadowed(c, headline, textX + dx * 1.3f, band.MidY + 40f * unit, big, SKColors.White, unit);
        }
        c.Restore();

        // Bright rims on the band.
        paint.Color = line;
        var rim = MathF.Max(3, 5f * unit);
        c.DrawRect(0, band.Top - rim, w, rim, paint);
        c.DrawRect(0, band.Bottom, w, rim, paint);

        // A secret's reveal: a white flash over the band.
        if (hidden && t >= reveal && t < reveal + 260)
        {
            paint.Color = SKColors.White.WithAlpha((byte)(255 * (1 - (t - reveal) / 260f)));
            c.DrawRect(band, paint);
        }
    }

    // The Pokémon standing big beside the dialogue: they hop in one after the other, a shadow under each.
    private static void Stand(SKCanvas c, IReadOnlyList<(Func<long, SKBitmap?> Frame, bool Shiny)> sprites, float midX, float floor, long t, float unit)
    {
        var count = Math.Min(3, sprites.Count);
        using var crisp = new SKPaint { IsAntialias = false };
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90) };
        for (var i = 0; i < count; i++)
        {
            if (sprites[i].Frame(t) is not { } frame) continue;
            var seen = Opaque(frame);
            if (seen.IsEmpty) continue;
            var start = DialogueAt + i * 110L;
            if (t < start) continue;
            var target = (count == 1 ? 400f : 230f) * unit;
            var scale = MathF.Max(1, MathF.Floor(target / MathF.Max(seen.Height, seen.Width * .8f)));
            float width = seen.Width * scale, height = seen.Height * scale;
            var x = midX + (i - (count - 1) / 2f) * 125f * unit;
            var feet = floor - 30f * unit - (count == 3 && i == 1 ? 50f * unit : 0);
            // In with a hop, then a slow breath.
            var hop = MathF.Sin(Math.Clamp((t - start) / 380f, 0, 1) * MathF.PI) * 70f * unit;
            var drop = (1 - Ease(t, start, 260)) * 40f * unit;
            x = Math.Max(x, 24f * unit + width / 2);
            c.DrawOval(x, feet, width * .32f, 12f * unit, shadow);
            c.DrawBitmap(frame, seen, SKRect.Create(x - width / 2, feet - height - hop + drop, width, height), crisp);
        }
    }

    // The visible part of a sprite frame (Showdown's frames have room around the Pokémon).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, object> Bounds = new();

    private static SKRectI Opaque(SKBitmap bitmap)
    {
        if (Bounds.TryGetValue(bitmap, out var known)) return (SKRectI)known;
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 16)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        var found = right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
        Bounds.AddOrUpdate(bitmap, found);
        return found;
    }

    // The games' shiny sparkle: four-pointed stars bursting out from the Pokémon and fading.
    private static void Sparkle(SKCanvas c, SKRect around, long t, float unit)
    {
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 8; i++)
        {
            var delay = i % 2 * 180;
            var life = (t - delay) / 600f;
            if (life is < 0 or > 1) continue;
            var angle = i * MathF.PI / 4 + .3f;
            var distance = (around.Width * .35f + 60f * unit) * (.4f + life * .8f);
            var x = around.MidX + MathF.Cos(angle) * distance;
            var y = around.MidY + MathF.Sin(angle) * distance * .8f;
            var size = 14f * unit * MathF.Sin(life * MathF.PI);
            paint.Color = (i % 2 == 0 ? SKColors.White : new SKColor(0xff, 0xf0, 0x9a)).WithAlpha((byte)(255 * (1 - life * .5f)));
            using var star = new SKPath();
            star.MoveTo(x, y - size * 2); star.LineTo(x + size * .4f, y - size * .4f); star.LineTo(x + size * 2, y);
            star.LineTo(x + size * .4f, y + size * .4f); star.LineTo(x, y + size * 2); star.LineTo(x - size * .4f, y + size * .4f);
            star.LineTo(x - size * 2, y); star.LineTo(x - size * .4f, y - size * .4f); star.Close();
            c.DrawPath(star, paint);
        }
    }

    // The black parts like a curtain, top up and bottom down, on the park.
    private static void Curtain(SKCanvas c, int w, int h, long t)
    {
        if (t < ParkAt - 100) return;
        var open = Ease(t, ParkAt - 100, 450);
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
        // Before it opens, the stripes already cover everything; this keeps the black whole.
        c.DrawRect(0, 0, w, h / 2f * (1 - open), paint);
        c.DrawRect(0, h - h / 2f * (1 - open), w, h / 2f * (1 - open), paint);
    }

    private static void Shadowed(SKCanvas c, string text, float x, float y, SKFont font, SKColor color, float unit)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = new SKColor(0, 0, 0, 200) };
        c.DrawTextFallback(text, x + 5f * unit, y + 5f * unit, SKTextAlign.Left, font, paint);
        paint.Color = color;
        c.DrawTextFallback(text, x, y, SKTextAlign.Left, font, paint);
    }

    private static void Bars(SKCanvas c, int w, int h, float unit)
    {
        var bar = 56f * unit;
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
        c.DrawRect(0, 0, w, bar, paint);
        c.DrawRect(0, h - bar, w, bar, paint);
    }

    // The title plate slides in from the left, as a new place's name does in the games.
    private static void TitlePlate(SKCanvas c, int h, long t, string eventName, bool secret, SKTypeface face, float unit)
    {
        var shown = Ease(t, ParkAt + 100, 380);
        if (shown <= 0) return;
        using var big = new SKFont(face, 50f * unit);
        var width = Math.Max(620f * unit, big.MeasureFallback(eventName) + 90f * unit);
        var plate = SKRect.Create(40f * unit - (1 - shown) * (width + 60f * unit), 56f * unit + 30f * unit, width, 160f * unit);
        ParkCardPaint.Frame(c, plate, unit);

        var tab = SKRect.Create(plate.Left + 26f * unit, plate.Top + 22f * unit, 330f * unit, 50f * unit);
        if (secret) ParkCardPaint.Tab(c, tab, SecretTop, SecretBottom, Pksm.Ink, unit);
        else ParkCardPaint.Tab(c, tab, StoragePaint.BannerTop, StoragePaint.BannerBottom, Pksm.Ink, unit);
        using var small = new SKFont(face, 28f * unit);
        ParkCardPaint.Text(c, secret ? "SECRET EVENT" : "SPECIAL EVENT", tab.Left + 22f * unit, tab.MidY, small,
            secret ? SKColors.White : Pksm.Ink, tab.Width - 44f * unit);
        ParkCardPaint.Text(c, eventName, plate.Left + 30f * unit, tab.Bottom + 42f * unit, big, EditorPaint.Value, plate.Width - 60f * unit);
    }

    // The park's dialogue box rising at the bottom, the text typing out, the choices beside it.
    private static SKRect[] Dialogue(SKCanvas c, int w, int h, long t, string speaker, string text,
        IReadOnlyList<(Func<long, SKBitmap?> Frame, bool Shiny)> sprites, IReadOnlyList<string> choices, int selected, SKTypeface face, float unit)
    {
        if (t < DialogueAt) return [];
        var choiceWidth = choices.Count > 0 ? 380f * unit : 0;
        var bottom = h - 56f * unit - 24f * unit;
        // The Pokémon stand big on the park at the left, the dialogue box beside them.
        var stage = 470f * unit;
        var box = new SKRect(40f * unit + stage, bottom - ParkCardPaint.DesignHeight * unit, w - 40f * unit - (choiceWidth > 0 ? choiceWidth + 24f * unit : 0), bottom);
        Stand(c, sprites, 40f * unit + stage / 2, bottom, t, unit);
        var rise = 1 - Ease(t, DialogueAt, 150);
        c.Save();
        c.Translate(0, rise * 60f * unit);
        var typed = Math.Min(text.Length, (int)((t - DialogueAt - 150) / 1000f * CharactersPerSecond));
        ParkCardPaint.PaintDialogue(c, box, speaker, text[..Math.Max(0, typed)], null, false, t, face, unit);
        c.Restore();

        var shown = Ease(t, ChoicesAt, 200);
        if (choiceWidth <= 0 || shown <= 0) return [];
        var panel = new SKRect(w - 40f * unit - choiceWidth, box.Top, w - 40f * unit, box.Bottom);
        c.Save();
        c.Translate((1 - shown) * 40f * unit, 0);
        ParkCardPaint.Frame(c, panel, unit);
        var rects = new SKRect[choices.Count];
        var gap = 22f * unit;
        var height = (panel.Height - 56f * unit - gap * (choices.Count - 1)) / choices.Count;
        using var font = new SKFont(face, 32f * unit);
        for (var i = 0; i < choices.Count; i++)
        {
            var rect = SKRect.Create(panel.Left + 28f * unit, panel.Top + 28f * unit + i * (height + gap), panel.Width - 56f * unit, height);
            var plate = ParkCardPaint.Plate(c, rect, i == selected, unit);
            ParkCardPaint.Centered(c, choices[i], plate.MidX, plate.MidY, font, i == selected ? EditorPaint.ChipInk : EditorPaint.Value);
            rects[i] = rect;
        }
        c.Restore();
        return rects;
    }
}
