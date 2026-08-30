using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>
/// The About window: version, authorship, and credits, in the same paper-panel
/// chrome as the trainer card. Gamepad-native: B or the CLOSE capsule dismisses.
/// </summary>
public static class AboutPopup
{
    private static byte[]? _logoPng;

    public static Task ShowAsync(Grid host)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = AppInfo.Current.VersionString;
        var diagnostic = AppInfo.Current.PackageName?.EndsWith(".debug", StringComparison.OrdinalIgnoreCase) == true;

        View Row(string caption, string value)
        {
            var valueLabel = new Label
            {
                Text = value,
                FontSize = 14,
                FontFamily = DsChrome.PixelFont,
                TextColor = UiTokens.Ink0,
                VerticalTextAlignment = TextAlignment.Center,
            };
            var grid = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = [new(new GridLength(140)), new(GridLength.Star)],
                Children =
                {
                    new Label { Text = caption, FontSize = 11, FontFamily = DsChrome.PixelFont, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#9AA5B0"), VerticalTextAlignment = TextAlignment.Center },
                    valueLabel,
                },
            };
            Grid.SetColumn(valueLabel, 1);
            return grid;
        }

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult();
        }

        var close = Kit.Capsule("CLOSE", UiTokens.Ink1);
        close.Clicked += (_, _) => Close();

        Label Small(string text, Color? color = null) => new()
        {
            Text = text,
            FontSize = 11,
            FontFamily = DsChrome.PixelFont,
            TextColor = color ?? Color.FromArgb("#9AA5B0"),
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar("ABOUT PKFORGE"),
                new Label { Text = "PKFORGE", FontSize = 20, FontAttributes = FontAttributes.Bold, FontFamily = DsChrome.PixelFont, TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.Center },
                Small("Pokémon save manager and bank"),
                Row("VERSION", diagnostic ? $"v{version} · DIAGNOSTIC" : $"v{version}"),
                Row("DEVELOPED BY", "@22sh"),
                Row("LOGO BY", "@spritedmistery"),
                Small("ENGINE PKHEX · CHROME PKSM (GPL-3)"),
                Small("SPRITES (C) NINTENDO · CREATURES · GAME FREAK"),
                Small("GITHUB.COM/SOFIANEELHOR/PKFORGE", UiTokens.MenuBlue),
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { close } },
            },
        };

        var source = LogoSource();
        if (source is not null)
        {
            content.Children.Insert(1, new Image
            {
                Source = source,
                HeightRequest = 112,
                WidthRequest = 112,
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Center,
            });
        }

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 420);
        overlay = Kit.AttachOverlay(host, window, () => Close());
        pad = new PadOverlay(() => Close(), () => Close());
        return result.Task;
    }

    /// <summary>The bundled logo (Resources/AppIcon/pkforge.png, shipped as ui/logo.png).</summary>
    private static ImageSource? LogoSource()
    {
        if (_logoPng is null)
        {
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync("ui/logo.png").GetAwaiter().GetResult();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _logoPng = copy.ToArray();
            }
            catch
            {
                return null; // the panel works without art; never block About on assets
            }
        }
        var png = _logoPng;
        return ImageSource.FromStream(() => new MemoryStream(png));
    }
}
