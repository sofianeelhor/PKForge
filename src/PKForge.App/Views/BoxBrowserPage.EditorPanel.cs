using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

using static PKForge.App.Views.EditorRows;

namespace PKForge.App.Views;

/// <summary>
/// The editor panel's look, in the designer's art direction (see <see cref="EditorPaint"/>):
/// the drawn header and backdrop, the section chips, and the row kinds the plain attribute
/// row does not cover (moves, stats, gender and shiny, legality, tools).
/// </summary>
public sealed partial class BoxBrowserPage
{
    private SKCanvasView? _editorHeader;
    private SKCanvasView? _editorBackdrop;
    private string? _editorExpLine;

    /// <summary>The panel shell: the drawn well and watermark behind, the drawn header on top.</summary>
    private Grid EditorShell(View body)
    {
        _editorBackdrop = new SKCanvasView { InputTransparent = true };
        _editorBackdrop.PaintSurface += PaintGuard.Wrap("The editor backdrop", PaintEditorBackdrop);
        _editorHeader = new SKCanvasView { HeightRequest = Design(EditorPaint.DesignHeaderHeight), InputTransparent = true };
        _editorHeader.PaintSurface += PaintGuard.Wrap("The editor header", PaintEditorHeader);

        var layout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { _editorHeader, body },
        };
        Grid.SetRow(body, 1);
        body.Margin = new Thickness(Design(3), Design(16), Design(3), Design(24));

        ViewModelChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Selected) or nameof(BoxBrowserViewModel.EditNickname)
                or nameof(BoxBrowserViewModel.EditLevel) or nameof(BoxBrowserViewModel.EditShiny)
                or nameof(BoxBrowserViewModel.EditGender) or nameof(BoxBrowserViewModel.EditSpecies))
            {
                _editorHeader.IsVisible = _viewModel.Selected is { IsEmpty: false };
                _editorHeader.InvalidateSurface();
                _editorBackdrop.InvalidateSurface();
            }
        };
        _editorHeader.IsVisible = _viewModel.Selected is { IsEmpty: false };
        return new Grid { Children = { _editorBackdrop, layout } };
    }

    /// <summary>The look being edited: pending species and shiny, the stored form and traits.</summary>
    private SpriteLook? EditedLook()
    {
        if (_viewModel.Selected is not { IsEmpty: false } selected) return null;
        var (species, form) = PendingSpeciesForm();
        return SpriteLook.Of(species > 0 ? species : selected.Species, form, _viewModel.EditShiny,
            ParseInt(_viewModel.EditGender) ?? selected.Gender);
    }

    private void PaintEditorBackdrop(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = new SKRect(0, 0, args.Info.Width, args.Info.Height);
        var unit = args.Info.Width / EditorPaint.DesignWidth;
        StoragePaint.WellPanel(canvas, bounds, unit);
        if (EditedLook() is not { } look) return;
        void Redraw() => MainThread.BeginInvokeOnMainThread(() => _editorBackdrop?.InvalidateSurface());
        // The Gen 5 front, as on the summary; the box sprite only when Showdown has none.
        if (!_sprites.TryGetShowdownFront(look, Redraw, out var sprite)) return;
        sprite ??= _sprites.GetSprite(look);
        if (sprite is null)
        {
            _sprites.Warm(look, Redraw);
            return;
        }
        var image = Services.SpriteImages.Of(sprite);
        EditorPaint.PaintWatermark(canvas, bounds, image, unit, BoxGridRenderer.SpriteSampling);
    }

    private void PaintEditorHeader(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_viewModel.Selected is not { IsEmpty: false } selected) return;
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var (species, _) = PendingSpeciesForm();
        if (species <= 0) species = selected.Species;
        var speciesName = (uint)species < (uint)data.SpeciesNames.Count ? data.SpeciesNames[species] : $"#{species}";
        var name = string.IsNullOrWhiteSpace(_viewModel.EditNickname) ? speciesName : _viewModel.EditNickname;
        var header = new EditorPaint.Header(name, ParseInt(_viewModel.EditLevel) ?? selected.Level, _viewModel.EditShiny,
            ParseInt(_viewModel.EditGender) ?? selected.Gender, speciesName, _editorExpLine);
        var unit = Math.Min(args.Info.Width / EditorPaint.DesignWidth, args.Info.Height / EditorPaint.DesignHeaderHeight);
        EditorPaint.PaintHeader(canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), header, PixelTypeface(), unit);
    }

    /// <summary>The EXP line the header shows beside the species tab; null hides it.</summary>
    private void SetEditorExpLine(string? line)
    {
        if (_editorExpLine == line) return;
        _editorExpLine = line;
        _editorHeader?.InvalidateSurface();
    }

    /// <summary>
    /// Gender and shiny share one band, as the designer drew them: the gender glyph opens the
    /// gender picker, the star toggles shiny. Each half is its own focus stop.
    /// </summary>
    private (View Row, Border Gender, Border Shiny) GenderShinyRow()
    {
        var genderValue = EditorValueLabel();
        var gender = EditorBand(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { EditorCaption("Gender", Design(144)), genderValue },
        }, dark: true);
        var star = EditorValueLabel();
        var shiny = EditorBand(new HorizontalStackLayout
        {
            Spacing = 10,
            Children = { EditorCaption("Shiny", Design(128)), star },
        }, dark: true);

        void Refresh()
        {
            genderValue.Text = _viewModel.EditGender switch { "0" => "♂", "1" => "♀", _ => "–" };
            star.Text = _viewModel.EditShiny ? "★" : "☆";
            star.TextColor = _viewModel.EditShiny ? UiTokens.Gold : EditorValue;
        }
        ViewModelChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.EditGender) or nameof(BoxBrowserViewModel.EditShiny)
                or nameof(BoxBrowserViewModel.Selected))
                Refresh();
        };
        Refresh();

        var genderTap = new TapGestureRecognizer();
        genderTap.Tapped += async (_, _) => await OpenGenderPickerAsync();
        gender.GestureRecognizers.Add(genderTap);
        var shinyTap = new TapGestureRecognizer();
        shinyTap.Tapped += (_, _) => _viewModel.EditShiny = !_viewModel.EditShiny;
        shiny.GestureRecognizers.Add(shinyTap);

        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            Children = { gender, shiny },
        };
        Grid.SetColumn(shiny, 1);
        return (row, gender, shiny);
    }

    /// <summary>
    /// One move row: the move's type plate, its name, its PP (the stored PP while the move is
    /// unchanged, the new move's base PP otherwise), and the chevron that opens the picker.
    /// </summary>
    /// <summary>A ROM hack's own move in this slot (no PKHeX id, read as 0), by the engine's name.</summary>
    private string? HackMoveName(int index) =>
        _viewModel.Selected is { IsEmpty: false, MoveNames: { } names } shown && index < names.Count
        && (index switch { 0 => shown.Move1, 1 => shown.Move2, 2 => shown.Move3, _ => shown.Move4 }) == 0
            ? names[index] is { Length: > 0 } name ? name : null
            : null;

    private Border MoveRow(int index, string vmProperty, IGameDataService data, bool dark, Func<Task> open)
    {
        var badge = InfoKit.TypeBadge(null, Design(128));
        var name = EditorValueLabel();
        var pp = new Label
        {
            FontFamily = DsChrome.PixelFont,
            FontSize = EditorText,
            TextColor = EditorPaint.Cyan.ToMauiColor().WithAlpha(0.75f),
            VerticalTextAlignment = TextAlignment.Center,
        };
        var grid = new Grid
        {
            ColumnSpacing = Design(24),
            ColumnDefinitions = [new(new GridLength(Design(128))), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
        };
        grid.Add(badge, 0, 0);
        grid.Add(name, 1, 0);
        grid.Add(pp, 2, 0);
        grid.Add(EditorChevron(), 3, 0);

        void Refresh()
        {
            var id = ParseInt(GetVmString(vmProperty)) ?? 0;
            var session = _sessionsFor();
            name.Text = id == 0 ? HackMoveName(index) ?? "—" : (uint)id < (uint)data.MoveNames.Count ? data.MoveNames[id] : $"#{id}";
            var facts = id == 0 || session is null ? null : InfoPickers.Info?.GetMove(session, id);
            InfoKit.SetType(badge, facts?.Type);
            pp.Text = "";
            // An empty slot has no moves to read (the edit fields still hold the last Pokémon's).
            if (facts is null || session is null || _viewModel.SelectedSlot < 0 || _viewModel.Selected is not { IsEmpty: false }) return;
            MoveSlotDetail? stored = null;
            try
            {
                var moves = session.GetMoveDetails(_viewModel.BoxIndex, _viewModel.SelectedSlot).Moves;
                if (index < moves.Count && moves[index].Move == id) stored = moves[index];
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            pp.Text = stored is { } s ? $"PP {s.PP}/{s.MaxPP}" : $"PP {facts.PP}";
        }
        ViewModelChanged += (_, args) =>
        {
            if (args.PropertyName == vmProperty || args.PropertyName is nameof(BoxBrowserViewModel.Selected))
                Refresh();
        };
        Refresh();

        var row = EditorBand(grid, dark);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await open();
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private static readonly string[] StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    /// <summary>One stat: its value, IV and EV. A opens the IV or EV editor, picked in a small menu.</summary>
    private Border StatLine(int stat, bool dark, Func<string, Task> edit)
    {
        var converter = new StatCellConverter();
        Label Cell(string property)
        {
            var value = EditorValueLabel();
            value.SetBinding(Label.TextProperty, new Binding(property, converter: converter, converterParameter: stat.ToString()));
            return value;
        }
        Label Tag(string text) => new()
        {
            Text = text,
            FontFamily = DsChrome.PixelFont,
            FontSize = EditorText * 0.8,
            TextColor = EditorLabel,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var ivTag = Tag("IV");
        var evTag = Tag("EV");
        // Gen 1/2 keep DVs and stat experience instead.
        var classic = (_sessionsFor()?.GetTrainingCaps().IvMax ?? 31) == 15;
        if (classic) { ivTag.Text = "DV"; evTag.Text = "Exp"; }

        var grid = new Grid
        {
            ColumnSpacing = Design(16),
            ColumnDefinitions =
            [
                new(new GridLength(Design(214))), new(GridLength.Star),
                new(GridLength.Auto), new(new GridLength(Design(72))),
                new(GridLength.Auto), new(new GridLength(Design(104))),
                new(GridLength.Auto),
            ],
        };
        View[] cells = [EditorCaption(StatNames[stat]), Cell(nameof(BoxBrowserViewModel.EditStats)), ivTag,
            Cell(nameof(BoxBrowserViewModel.EditIvs)), evTag, Cell(nameof(BoxBrowserViewModel.EditEvs)), EditorChevron()];
        for (var i = 0; i < cells.Length; i++) grid.Add(cells[i], i, 0);

        var row = EditorBand(grid, dark);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await edit(StatNames[stat]);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    /// <summary>
    /// The legality status: Legal, Illegal (A opens the report), Checking, or Not checked
    /// for ROM hacks PKHeX cannot judge.
    /// </summary>
    private Border LegalityLine()
    {
        var value = EditorValueLabel();
        var report = new Label
        {
            Text = "Report ›",
            FontFamily = DsChrome.PixelFont,
            FontSize = EditorText,
            TextColor = EditorValue,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var grid = new Grid
        {
            ColumnDefinitions = [new(new GridLength(Design(214))), new(GridLength.Star), new(GridLength.Auto)],
            Children = { EditorCaption("Status"), value, report },
        };
        Grid.SetColumn(value, 1);
        Grid.SetColumn(report, 2);

        void Refresh()
        {
            var supported = _sessionsFor()?.SupportsLegalityAnalysis ?? true;
            (value.Text, value.TextColor) = _viewModel.LegalityBadge switch
            {
                "✓" => ("Legal", Pksm.Legal.ToMauiColor()),
                "✗" => ("Illegal", Pksm.Illegal.ToMauiColor()),
                _ when !supported => ("Not checked for this ROM hack", EditorValue),
                _ => ("Checking…", EditorValue),
            };
            report.IsVisible = _viewModel.LegalityBadge == "✗";
        }
        ViewModelChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.LegalityBadge) or nameof(BoxBrowserViewModel.Selected))
                Refresh();
        };
        Refresh();

        var row = EditorBand(grid, dark: true);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenLegalityReportAsync();
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private Task OpenLegalityReportAsync()
    {
        if (_viewModel.LegalityBadge != "✗") return Task.CompletedTask;
        var detail = string.IsNullOrWhiteSpace(_viewModel.LegalityText) ? "No legality details were reported." : _viewModel.LegalityText;
        return ShowLegalityReportAsync(detail);
    }

    // Decoded box wallpapers by asset name (a few dozen at most), with their average colour.
    private static readonly Dictionary<string, (SKImage Art, SKColor Average)?> Wallpapers = new(StringComparer.Ordinal);
    private static readonly HashSet<string> WallpapersLoading = new(StringComparer.Ordinal);
    private (Domain.ISaveEngineSession Session, int Box, long Generation, string Asset)? _wallpaperKey;

    /// <summary>
    /// The current box's wallpaper: the one the game stores for it, or for games that store
    /// none (Gen 1-2, ROM hacks) the Emerald set in turn, so every box still has its own.
    /// Null while it loads; the canvas repaints when it arrives.
    /// </summary>
    private (SKImage Art, SKColor Average)? BoxWallpaper()
    {
        var box = _viewModel.BoxIndex;
        if (box < 0 || _sessionsFor() is not { } session) return null;
        string asset;
        if (_wallpaperKey is { } key && key.Session == session && key.Box == box && key.Generation == _viewModel.MutationGeneration)
            asset = key.Asset;
        else
        {
            string? stored = null;
            try
            {
                if (Engine.BoxLayoutService.SupportsWallpapers(session) && box < _viewModel.BoxCount)
                    stored = Engine.BoxLayoutService.GetBox(session, box).WallpaperAsset;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            asset = stored ?? $"box_wp{box % 16 + 1:00}e";
            _wallpaperKey = (session, box, _viewModel.MutationGeneration, asset);
        }
        if (Wallpapers.TryGetValue(asset, out var art)) return art;
        if (!WallpapersLoading.Add(asset)) return null;
        _ = LoadWallpaperAsync(asset);
        return null;
    }

    private async Task LoadWallpaperAsync(string asset)
    {
        (SKImage, SKColor)? art = null;
        try
        {
            art = await Task.Run(async () =>
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync($"wallpapers/{asset}.png");
                using var bitmap = SKBitmap.Decode(stream);
                return bitmap is null ? ((SKImage, SKColor)?)null : (SKImage.FromBitmap(bitmap), StoragePaint.AverageColor(bitmap));
            });
        }
        catch (Exception error) when (error is IOException or FileNotFoundException) { }
        Wallpapers[asset] = art;
        WallpapersLoading.Remove(asset);
        _canvas.InvalidateSurface();
    }

    /// <summary>The ball icon on the Ball row, pixel-sharp, following the pending ball.</summary>
    private SKCanvasView BallIcon()
    {
        var view = new SKCanvasView
        {
            WidthRequest = Design(40), HeightRequest = Design(40), InputTransparent = true, VerticalOptions = LayoutOptions.Center,
        };
        view.PaintSurface += PaintGuard.Wrap("BoxBrowserPage.EditorPanel view", (_, args) =>
        {
            var c = args.Surface.Canvas;
            c.Clear(SKColors.Transparent);
            if (ParseInt(_viewModel.EditBall) is not { } ball || ball <= 0) return;
            var bitmap = _sprites.GetBall(ball);
            if (bitmap is null)
            {
                _sprites.WarmBall(ball, () => MainThread.BeginInvokeOnMainThread(view.InvalidateSurface));
                return;
            }
            using var image = SKImage.FromBitmap(bitmap);
            c.DrawImage(image, new SKRect(0, 0, args.Info.Width, args.Info.Height), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        });
        ViewModelChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.EditBall) or nameof(BoxBrowserViewModel.Selected))
                view.InvalidateSurface();
        };
        return view;
    }


}
