using PKForge.Chrome;
using SkiaSharp;
namespace PKForge.App.Views;

/// <summary>
/// The bundled PKSM pixel-icon set as MAUI image sources. Icons ship as PNGs under
/// ui/pksm/ (see Resources/UI/ATTRIBUTION.md); they are tinted once on first use —
/// native authored color, logo cyan for navy panels, white, or logo-void — and cached.
/// </summary>
public static class PksmIcons
{
    private static readonly Dictionary<string, byte[]> Png = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    // Official Gen VIII status sprites sourced from Bulbagarden Archives. Kept inline
    // because these tiny indexed PNGs are exact game art, not recolorable PKSM glyphs.
    private const string PokerusInfectedPng = "iVBORw0KGgoAAAANSUhEUgAAACoAAAAqCAMAAADyHTlpAAAA2FBMVEXrPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK4NMDeIAAAAR3RSTlMAAQMHCQsMDxEUFxofODo8P0FCRUdTWGFiY2RmaGpscXR4iYyNjpqfsLGztLe4ur6/wMLDxcbP0NHU19na3uDj6ezu8vP0+8JrisoAAAGMSURBVHhetdVrb4IwGIbhTgq4k8d5BMQpimNuIMqGss3hpv3//2idfQuNJvST97cnvYIxmIoulmpNrAJQGow6ZZBvhBAPA9AbQ8d1Hbupc/lMjxNmLfLfUqFDMxYHAh38rkol9o+LfeyEgFWMFBi0N5HKJJnAU8EuyVlRBAO+jEekeYiFlzLpKwhSwMqk3Ia5zOnH/CcXvy8JG7FIDQZchO5SLj9v0ZXLhpFLDc5rdDxxOqejzkaqCQ8tpOJjFzK64FI/CHTG6YyOGh860AaR0irQoZxaQB05nQJ1Oe3REfDxSkebD/eUbqo3PZL1eH2/OaVjIs0BasupDbQpp5X8FcjSEOSLYLvdftFSUa4QryvINoLaAjUzqu4z+Y2ykkzuMMoySVadyweS1UdCUW7X6/U7LclljAWpRqSgUKDYJ4UFKpclkBILt6LUMjqAGcbnJA5htI50xIZ/fmnu+hgH4k+rwyRcxTlcmZieqoHwvsoJHZ6CWHrVmrru2K5obDObwCgX/22glmOii/UH3BMcpnGqzPwAAAAASUVORK5CYII=";
    private const string PokerusCuredPng = "iVBORw0KGgoAAAANSUhEUgAAACoAAAAqCAMAAADyHTlpAAAA21BMVEXrPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK7rPK5Bk8vuAAAASHRSTlMAAQQHCwwNDg8UFxofLTAzODo8P0FHVVhdY2RmaGxtcXN4fYqNmpufsLG3uLq+v8DGyMnN0NHU19na3uDj6evs7vDy8/b3+vtILop8AAABSUlEQVR4Xs3U2VrCMBCG4V/WuoDstLYsshREERBZVFBQanP/VyQ+TZrQNs0p3/H7TDIng7NLq7R7tt1tVTPxLmtOXEJzx0ZSLs0dOcmxJPj2hYSaFaLknUsicvSwbBJJndBMIq0W+Kcrp4ccxOhGb6NvTn4e156dJ8AzPTAAbr6YfL/GxcCzJpfZnUfLAB4YHQEoe3SXFYZKaXDsREUnTGquQIeMDgVKNEorRElLlLbVtEFpT037lNqMGgCeGX0CUGfUDtJV6cogfveXhVWQdomyLqUtNW1RWlXTIqUZV0nToI1FsNlsPo59inIKliHIOmh1gVpgJR1fbuG39uU+BT+L+FUQXtYELznjdrlcvh5bc7mAWMEh0n7zOEmXUx2BOjLZRKjaIfJ1HRHl5mG5yCOyRPBo7s3YU8zh1EohNq3U6P8f+GIa59Yf8aXyE75AKnkAAAAASUVORK5CYII=";

    public const string Periwinkle = "periwinkle";
    public const string Indigo = "indigo";
    public const string Cyan = "cyan";
    public const string Dark = "dark";
    public const string White = "white";
    public const string Native = "native";

    /// <summary>Semantic → bundled asset file (the vocabulary lives in <see cref="PksmIconCatalog"/>).</summary>
    public static string Asset(string name) => PksmIconCatalog.Asset(name);

    /// <summary>Icons whose authored colors carry meaning and must never be monochrome-tinted.</summary>
    public static bool IsNative(string name) => name is
        "retroarch" or "azahar" or "eden" or "ribbons" or "pokerus-infected" or "pokerus-cured";

    /// <summary>Decodes a bundled icon, tinted, as PNG bytes (cached). Safe to call from any thread.</summary>
    public static byte[] GetPng(string name, string tint = Indigo)
    {
        lock (Gate)
        {
            var key = $"{name}|{tint}";
            if (Png.TryGetValue(key, out var cached)) return cached;

            byte[] raw;
            if (name is "pokerus-infected" or "pokerus-cured")
            {
                raw = Convert.FromBase64String(name == "pokerus-infected" ? PokerusInfectedPng : PokerusCuredPng);
            }
            else
            {
                var file = Asset(name);
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/pksm/{file}").GetAwaiter().GetResult();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                raw = ms.ToArray();
            }

            var color = tint switch
            {
                Periwinkle => (SKColor?)null, // native
                Native => (SKColor?)null,
                White => SKColors.White,
                Dark => Pksm.LogoVoid,
                Cyan => Pksm.LogoCyan,
                _ => Pksm.LogoCyan,
            };

            byte[] result;
            if (color is null)
            {
                result = raw;
            }
            else
            {
                using var bmp = SKBitmap.Decode(raw);
                using var canvas = new SKCanvas(bmp);
                using var paint = new SKPaint { Color = color.Value, BlendMode = SKBlendMode.SrcIn };
                canvas.DrawRect(0, 0, bmp.Width, bmp.Height, paint);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                result = data.ToArray();
            }

            Png[key] = result;
            return result;
        }
    }

    /// <summary>A tinted icon as a MAUI image source.</summary>
    public static ImageSource Source(string name, string tint = Indigo)
        => ImageSource.FromStream(() => new MemoryStream(GetPng(name, tint)));

    /// <summary>A ready icon view at a given display size (nearest-neighbor crispness comes from the pixel art itself).</summary>
    public static Image Icon(string name, double size = 28, string tint = Indigo)
        => new()
        {
            Source = Source(name, tint),
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
        };
}
