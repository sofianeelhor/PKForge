using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// The box manager's rename and wallpaper actions (PKHeX SAV_BoxLayout). Names and wallpapers
/// are how the game shows the box, not Pokémon data; they are still save writes, so they go
/// through the restore-point pipeline and Hardcore mode refuses them (EditTrainer, like the
/// other in-save presentation settings).
/// </summary>
public static class BoxLayoutEditor
{
    public const string RenameLabel = "Rename box…";
    public const string WallpaperLabel = "Box wallpaper…";

    private const SaveAction Action = SaveAction.EditTrainer;

    public static async Task<bool> RenameAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int box)
    {
        if (HardcoreMode.Blocks(Action, out var status))
        {
            viewModel.Status = status;
            return false;
        }
        var current = BoxLayoutService.GetBox(session, box);
        var max = BoxLayoutService.GetNameMaxLength(session);
        var name = await TextPopup.ShowLineAsync(host, $"Rename box {box + 1:00}", $"Up to {max} characters", current.Name);
        if (string.IsNullOrWhiteSpace(name) || name == current.Name) return false;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"Box {box + 1:00} is now \"{BoxLayoutService.Rename(s, box, name).Name}\".", Action);
    }

    public static async Task<bool> PickWallpaperAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, int box)
    {
        if (HardcoreMode.Blocks(Action, out var status))
        {
            viewModel.Status = status;
            return false;
        }
        var current = BoxLayoutService.GetBox(session, box);
        var picked = await WallpaperPicker.ShowAsync(host, $"Wallpaper · {current.Name}", BoxLayoutService.GetWallpapers(session), current.Wallpaper ?? 0);
        if (picked is not { } wallpaper || wallpaper == current.Wallpaper) return false;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"{current.Name}: wallpaper {BoxLayoutService.SetWallpaper(s, box, wallpaper).WallpaperName}.", Action);
    }

    /// <summary>
    /// A grid of the game's wallpapers with PKHeX.Drawing.Misc's pictures (bundled as
    /// wallpapers/*.png); a wallpaper PKHeX has no picture for shows its name only.
    /// D-pad moves, A picks, B cancels; tap picks.
    /// </summary>
    private sealed class WallpaperPicker : IPadHandler
    {
        private const int Columns = 4;
        private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly List<Border> _tiles = [];
        private readonly ScrollView _scroll;
        private int _index;

        public static Task<int?> ShowAsync(Grid host, string title, IReadOnlyList<BoxWallpaperOption> options, int current) =>
            new WallpaperPicker(host, title, options, current)._result.Task;

        private WallpaperPicker(Grid host, string title, IReadOnlyList<BoxWallpaperOption> options, int current)
        {
            _host = host;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            for (var c = 0; c < Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var r = 0; r < (options.Count + Columns - 1) / Columns; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var option in options)
            {
                var tile = Tile(option, option.Index == current);
                var captured = option.Index;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Close(captured);
                tile.GestureRecognizers.Add(tap);
                _tiles.Add(tile);
                grid.Add(tile, option.Index % Columns, option.Index / Columns);
            }
            _scroll = new ScrollView { Content = grid, MaximumHeightRequest = Math.Max(160, host.Height - 140) };
            var content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    Kit.HeaderBar(title),
                    _scroll,
                    Kit.WindowHints(("A", "Choose", null), ("B", "Cancel", () => Close(null))),
                },
            };
            var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 560, scroll: false);
            _overlay = Kit.AttachOverlay(host, window, () => Close(null));
            Highlight(Math.Clamp(current, 0, Math.Max(0, options.Count - 1)));
            _router?.Push(this);
        }

        private static Border Tile(BoxWallpaperOption option, bool isCurrent)
        {
            var stack = new VerticalStackLayout { Spacing = 2 };
            if (option.AssetName is { } asset)
            {
                stack.Children.Add(new Image
                {
                    Source = ImageSource.FromStream(async _ => await OpenAsync(asset)),
                    Aspect = Aspect.AspectFill,
                    HeightRequest = 58,
                    InputTransparent = true,
                });
            }
            stack.Children.Add(new Label
            {
                Text = (isCurrent ? "● " : "") + option.Name,
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                TextColor = UiTokens.Ink0,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            return new Border
            {
                Content = stack,
                Padding = 3,
                StrokeThickness = 2,
                Stroke = Colors.Transparent,
                BackgroundColor = UiTokens.ShellPress,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
            };
        }

        private static async Task<Stream?> OpenAsync(string asset)
        {
            try
            {
                return await FileSystem.OpenAppPackageFileAsync($"wallpapers/{asset}.png");
            }
            catch
            {
                return null; // PKHeX ships no picture under this name; the tile keeps its label
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Left: Highlight(_index - 1); return true;
                case PadButton.Right: Highlight(_index + 1); return true;
                case PadButton.Up: Highlight(_index - Columns); return true;
                case PadButton.Down: Highlight(_index + Columns); return true;
                case PadButton.A: Close(_index); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private void Highlight(int index)
        {
            if (_tiles.Count == 0) return;
            _index = Math.Clamp(index, 0, _tiles.Count - 1);
            for (var i = 0; i < _tiles.Count; i++)
            {
                _tiles[i].Stroke = i == _index ? UiTokens.Rim : Colors.Transparent;
                _tiles[i].BackgroundColor = i == _index ? UiTokens.SelectFill : UiTokens.ShellPress;
            }
            _ = _scroll.ScrollToAsync(_tiles[_index], ScrollToPosition.MakeVisible, false);
        }

        private void Close(int? result)
        {
            _router?.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }
}
