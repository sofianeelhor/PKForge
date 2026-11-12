using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>A Pokémon in LCD tones: 0 (screen) to 3 (ink), 255 where it is see-through.</summary>
public sealed record WalkerSprite(int Width, int Height, byte[] Tones)
{
    public byte At(int x, int y) => Tones[y * Width + x];
}

/// <summary>
/// The Pokéwalker's little screen as pixels: a 96×64 four-tone buffer, a 3×5 and a 5×7 pixel font,
/// hand-placed icons and scenery, and the conversion of a real sprite to two-bit tones with
/// ordered dithering and an ink outline, as the device's own sprites are drawn.
/// </summary>
public sealed class WalkerLcd
{
    public const int Width = 96;
    public const int Height = 64;

    private readonly byte[] _px = new byte[Width * Height];

    public void Clear(byte tone = 0) => Array.Fill(_px, tone);

    public byte this[int x, int y] => _px[y * Width + x];

    public void Dot(int x, int y, int tone)
    {
        if ((uint)x < Width && (uint)y < Height && tone is >= 0 and <= 3) _px[y * Width + x] = (byte)tone;
    }

    public void Fill(int x, int y, int w, int h, int tone)
    {
        for (var j = y; j < y + h; j++)
            for (var i = x; i < x + w; i++) Dot(i, j, tone);
    }

    public void HLine(int x, int y, int w, int tone) => Fill(x, y, w, 1, tone);

    /// <summary>A pattern of rows: '0'-'3' are tones, anything else is see-through.</summary>
    public void Pattern(string[] rows, int x, int y, bool flip = false, int? only = null)
    {
        var width = rows.Max(r => r.Length);
        for (var j = 0; j < rows.Length; j++)
            for (var i = 0; i < rows[j].Length; i++)
            {
                var ch = rows[j][i];
                if (ch is < '0' or > '3') continue;
                // In one tone, only the ink lines are drawn: an outline of the icon.
                if (only is not null && ch != '3') continue;
                Dot(x + (flip ? width - 1 - i : i), y + j, only ?? ch - '0');
            }
    }

    public void Sprite(WalkerSprite sprite, int x, int y)
    {
        for (var j = 0; j < sprite.Height; j++)
            for (var i = 0; i < sprite.Width; i++)
                if (sprite.At(i, j) is var t and not 255) Dot(x + i, y + j, t);
    }

    /// <summary>Text in the 3×5 font, upper case; returns its width.</summary>
    public int Text(string text, int x, int y, int tone)
    {
        var cx = x;
        foreach (var ch in Fold(text))
        {
            if (Small.TryGetValue(ch, out var glyph))
                for (var j = 0; j < 5; j++)
                    for (var i = 0; i < 3; i++)
                        if (glyph[j * 3 + i] == '#') Dot(cx + i, y + j, tone);
            cx += 4;
        }
        return cx - x - 1;
    }

    public static int TextWidth(string text) => Math.Max(0, Fold(text).Length * 4 - 1);

    /// <summary>Text cut to fit <paramref name="max"/> pixels.</summary>
    public static string Cut(string text, int max)
    {
        var folded = Fold(text);
        var chars = Math.Max(0, (max + 1) / 4);
        return folded.Length <= chars ? folded : folded[..Math.Max(0, chars)];
    }

    /// <summary>Big 5×7 digits, as the device shows its steps; right-aligned at <paramref name="right"/>.</summary>
    public void BigNumber(long value, int right, int y, int tone)
    {
        var text = Math.Clamp(value, 0, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var x = right - (text.Length * 6 - 1);
        foreach (var ch in text)
        {
            var glyph = Big[ch - '0'];
            for (var j = 0; j < 7; j++)
                for (var i = 0; i < 5; i++)
                    if (glyph[j][i] == '#') Dot(x + i, y + j, tone);
            x += 6;
        }
    }

    /// <summary>The screen as a 96×64 image in the given four tones (light to dark).</summary>
    public SKImage ToImage(IReadOnlyList<SKColor> tones)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                bitmap.SetPixel(x, y, tones[_px[y * Width + x]]);
        return SKImage.FromBitmap(bitmap);
    }

    private static string Fold(string text)
    {
        var decomposed = text.ToUpperInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var output = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            output.Append(ch switch { '♀' => 'F', '♂' => 'M', '·' => '-', '…' => '.', '\'' or '’' => '\'', _ => ch });
        }
        return output.ToString();
    }

    // The 3×5 font: rows top to bottom, '#' lit.
    private static readonly Dictionary<char, string> Small = new()
    {
        ['A'] = ".#.#.#####.##.#",
        ['B'] = "##.#.###.#.###.",
        ['C'] = ".###..#..#...##",
        ['D'] = "##.#.##.##.###.",
        ['E'] = "####..##.#..###",
        ['F'] = "####..##.#..#..",
        ['G'] = ".###..#.##.#.##",
        ['H'] = "#.##.#####.##.#",
        ['I'] = "###.#..#..#.###",
        ['J'] = "..#..#..##.#.#.",
        ['K'] = "#.##.###.#.##.#",
        ['L'] = "#..#..#..#..###",
        ['M'] = "#.########.##.#",
        ['N'] = "##.#.##.##.##.#",
        ['O'] = ".#.#.##.##.#.#.",
        ['P'] = "##.#.###.#..#..",
        ['Q'] = ".#.#.##.###..##",
        ['R'] = "##.#.###.#.##.#",
        ['S'] = ".###...#...###.",
        ['T'] = "###.#..#..#..#.",
        ['U'] = "#.##.##.##.####",
        ['V'] = "#.##.##.##.#.#.",
        ['W'] = "#.##.########.#",
        ['X'] = "#.##.#.#.#.##.#",
        ['Y'] = "#.##.#.#..#..#.",
        ['Z'] = "###..#.#.#..###",
        ['0'] = "####.##.##.####",
        ['1'] = ".#.##..#..#.###",
        ['2'] = "##...#.#.#..###",
        ['3'] = "##...#.#...###.",
        ['4'] = "#.##.####..#..#",
        ['5'] = "####..##...###.",
        ['6'] = ".###..####.####",
        ['7'] = "###..#.#..#..#.",
        ['8'] = "####.#####.####",
        ['9'] = "####.####..###.",
        [' '] = "...............",
        ['!'] = ".#..#..#.....#.",
        ['?'] = "##...#.#.....#.",
        ['.'] = ".............#.",
        [','] = "..........#.#..",
        [':'] = "....#.....#....",
        ['-'] = "......###......",
        ['+'] = "....#.###.#....",
        ['/'] = "..#..#.#.#..#..",
        ['\''] = ".#..#..........",
        ['('] = ".#.#..#..#...#.",
        [')'] = ".#...#..#..#.#.",
    };

    private static readonly string[][] Big =
    [
        [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
        ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
    ];

    private static readonly float[] Bayer =
    [
        0f / 16, 8f / 16, 2f / 16, 10f / 16,
        12f / 16, 4f / 16, 14f / 16, 6f / 16,
        3f / 16, 11f / 16, 1f / 16, 9f / 16,
        15f / 16, 7f / 16, 13f / 16, 5f / 16,
    ];

    /// <summary>
    /// A real sprite as the Pokéwalker would show it: fitted in <paramref name="maxWidth"/>×<paramref name="maxHeight"/>
    /// (outline included), shaded in tones 0-3 with a 4×4 ordered dither, and outlined in ink.
    /// </summary>
    public static WalkerSprite Convert(SKBitmap source, int maxWidth = 52, int maxHeight = 42)
    {
        // Fitted, and grown up to half again when small, so the Pokémon fills the screen as on the device.
        var scale = Math.Min(1.5f, Math.Min((maxWidth - 2) / (float)Math.Max(1, source.Width), (maxHeight - 2) / (float)Math.Max(1, source.Height)));
        var w = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(source.Height * scale));
        using var small = source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? source.Copy();
        var width = small.Width + 2;
        var height = small.Height + 2;
        var tones = new byte[width * height];
        Array.Fill(tones, (byte)255);
        var inside = new bool[width * height];
        for (var y = 0; y < small.Height; y++)
            for (var x = 0; x < small.Width; x++)
            {
                var p = small.GetPixel(x, y);
                if (p.Alpha < 128) continue;
                var luma = (0.299f * p.Red + 0.587f * p.Green + 0.114f * p.Blue) / 255f;
                // Posterised to four tones like the device's sprites: light bodies stay screen
                // colour, shade and detail take the middle tones, the darkest the ink. The ordered
                // dither only blends where a tone changes, so flat areas stay flat.
                var lit = luma + (Bayer[(y & 3) * 4 + (x & 3)] - 0.5f) * 0.12f;
                var tone = lit > 0.70f ? 0 : lit > 0.50f ? 1 : lit > 0.28f ? 2 : 3;
                tones[(y + 1) * width + x + 1] = (byte)tone;
                inside[(y + 1) * width + x + 1] = true;
            }
        // The ink outline round the silhouette, as on the device.
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (inside[y * width + x]) continue;
                var near = (x > 0 && inside[y * width + x - 1]) || (x < width - 1 && inside[y * width + x + 1]) ||
                           (y > 0 && inside[(y - 1) * width + x]) || (y < height - 1 && inside[(y + 1) * width + x]);
                if (near) tones[y * width + x] = 3;
            }
        return new WalkerSprite(width, height, tones);
    }
}

/// <summary>Hand-placed pixel art for the LCD: page icons, status icons, scenery.</summary>
public static class WalkerArt
{
    public static readonly string[] Note = ["...333.", "...3.33", "...3...", "...3...", ".333...", "3333...", ".33...."];
    public static readonly string[] Ball = ["..333..", ".32223.", "3222223", "3333333", "3000303", ".30003.", "..333.."];
    public static readonly string[] Bag = ["..333..", ".3...3.", "3333333", "3000003", "3033303", "3000003", "3333333"];
    public static readonly string[] Walk = [".33....", "3333...", "3333...", ".33..3.", "....333", "....333", ".....3."];
    public static readonly string[] Radar = ["...3...", "...3...", ".......", "...3...", ".33333.", "3333333", "3333333"];
    public static readonly string[] Dowse = ["3......", ".3...3.", "..3.333", "...333.", "...3.3.", "..3....", "......."];
    public static readonly string[] Sync = ["..3333.", ".3....3", "3....33", ".......", "33....3", "3....3.", ".3333.."];
    public static readonly string[] Info = [".33333.", "3..3..3", "3.....3", "3..3..3", "3..3..3", "3..3..3", ".33333."];
    public static readonly string[] Plus = ["...3...", "...3...", "...3...", "3333333", "...3...", "...3...", "...3..."];
    public static readonly string[] Watt = ["..33...", ".33....", "33333..", "..33...", ".33....", "33.....", "3......"];

    public static readonly string[] Tree =
    [
        "....3333....",
        "..33222233..",
        ".3221122223.",
        "322111222223",
        "322112222223",
        "322222222223",
        ".3222222223.",
        "..33222233..",
        "....3333....",
        ".....33.....",
        ".....33.....",
        "....3333....",
    ];

    public static readonly string[] Pine =
    [
        ".....3.....",
        "....323....",
        "...32123...",
        "..3222223..",
        "...32223...",
        "..3212223..",
        ".322222223.",
        "..3222223..",
        ".322222223.",
        "33333333333",
        ".....3.....",
        "....333....",
    ];

    public static readonly string[] Rock =
    [
        "...3333...",
        "..321123..",
        ".32112223.",
        "3221222223",
        "3222222223",
        ".33333333.",
    ];

    public static readonly string[] House =
    [
        ".....33.....",
        "...332233...",
        ".3322222233.",
        "333333333333",
        ".3000000003.",
        ".3033003303.",
        ".3033003303.",
        ".3000003303.",
        ".3333333333.",
    ];

    public static readonly string[] Stalactite = ["3333333", ".32223.", "..323..", "..323..", "...3..."];

    public static readonly string[] Bush =
    [
        "........3333..........",
        ".....33321123333......",
        "...3322112222222333...",
        "..322112222222222223..",
        ".32211222222222222223.",
        "3221122222222222222223",
        "3221222222222222222223",
        "3222222222222222222223",
        "3222232222322222322223",
        ".33333333333333333333.",
    ];

    public static readonly string[] Patch =
    [
        "..3....3....3..",
        ".323..323..323.",
        ".323..323..323.",
        "32223322233222.3",
        "333333333333333.",
    ];

    public static readonly string[] Cursor = ["33333", ".333.", "..3.."];
}
