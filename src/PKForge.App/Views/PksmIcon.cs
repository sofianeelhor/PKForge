using PKForge.Chrome;
using SkiaSharp;
namespace PKForge.App.Views;

/// <summary>
/// The bundled PKSM pixel-icon set as MAUI image sources. Icons ship as PNGs under
/// ui/pksm/ (see Resources/UI/ATTRIBUTION.md); they are tinted once on first use —
/// the native periwinkle for colored surfaces, deep indigo for white panels — and cached.
/// </summary>
public static class PksmIcons
{
    private static readonly Dictionary<string, byte[]> Png = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public const string Periwinkle = "periwinkle";
    public const string Indigo = "indigo";
    public const string White = "white";
    public const string Native = "native";

    /// <summary>Semantic → bundled asset file.</summary>
    public static string Asset(string name) => name switch
    {
        "storage" => "icon_storage.png",
        "editor" => "icon_editor.png",
        "events" => "icon_events.png",
        "settings" => "icon_settings.png",
        "bag" => "icon_bag.png",
        "party" => "icon_party.png",
        "shiny" => "icon_shiny.png",
        "item" => "icon_item.png",
        "search" => "icon_search.png",
        "folder" => "icon_folder.png",
        "hex" => "icon_hex.png",
        "script" => "icon_script.png",
        "scripts" => "icon_scripts.png",
        "credits" => "icon_credits.png",
        "box" => "storagemenu_icon_box.png",
        "bank" => "pkf_bank.png",
        "trainer" => "gi_trainer.png",
        "male" => "gi_male.png",
        "female" => "gi_female.png",
        "genderless" => "gi_genderless.png",
        "pokedex" => "gi_pokedex.png",
        "music" => "gi_music.png",
        "play" => "gi_play.png",
        "pause" => "gi_pause.png",
        "skip" => "gi_skip.png",
        "shuffle" => "gi_shuffle.png",
        "power" => "gi_power.png",
        "quit" => "gi_quit.png",
        "restore" => "gi_restore.png",
        "release" => "gi_release.png",
        "padlock" => "gi_padlock-white.png",
        "dice" => "gi_dice-white.png",
        "skull" => "gi_skull-white.png",
        "gears" => "gi_gears-white.png",
        "heart" => "gi_heart-white.png",
        "retroarch" => "emu_retroarch.png",
        "melonds" => "emu_melonds.png",
        "azahar" => "emu_azahar.png",
        "eden" => "emu_eden.png",
        "linkboy" => "emu_linkboy.png",
        _ => "icon_hex.png",
    };

    /// <summary>Decodes a bundled icon, tinted, as PNG bytes (cached). Safe to call from any thread.</summary>
    public static byte[] GetPng(string name, string tint = Indigo)
    {
        lock (Gate)
        {
            var key = $"{name}|{tint}";
            if (Png.TryGetValue(key, out var cached)) return cached;

            var file = Asset(name);
            using var stream = FileSystem.OpenAppPackageFileAsync($"ui/pksm/{file}").GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var raw = ms.ToArray();

            var color = tint switch
            {
                Periwinkle => (SKColor?)null, // native
                Native => (SKColor?)null,
                White => SKColors.White,
                _ => Pksm.IndigoInk,
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
