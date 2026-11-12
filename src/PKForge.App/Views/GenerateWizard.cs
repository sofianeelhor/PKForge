using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using static PKForge.App.Views.EditorRows;

namespace PKForge.App.Views;

/// <summary>
/// The step-by-step "create a Pokémon" flow:
/// 1. WHO - searchable species picker with sprites.
/// 2. HOW - the features form (level, shiny, nature, ability, ball, moves), everything optional.
/// 3. The legalizer does the rest offline; the result lands in the slot, legal.
/// </summary>
public static class GenerateWizard
{
    /// <summary>Runs the flow and returns the request, or null if the user backed out.</summary>
    public static async Task<GenerationRequest?> RunAsync(Grid host, IGameDataService data, ISaveEngineSession session)
    {
        // Step 1 - the Pokémon, picked in the floating Pokédex.
        var species = await PokedexPicker.ShowAsync(host, data, session);
        if (species is null) return null;

        // Step 1b - the form, when this game's species has more than one (Rotom-Wash,
        // Deoxys-Speed, regional forms...). Skipped silently for single-form species.
        var forms = session.GetFormChoices(species.Id);
        int form = 0;
        var formOptions = new List<PadOption>();
        for (var index = 0; index < forms.Count; index++)
        {
            if (index != 0 && forms[index].Length == 0) continue;
            var label = index == 0 || forms[index].Length == 0 ? "Standard" : forms[index];
            formOptions.Add(new PadOption(label, IconPath: await FormSpritePathAsync(species.Id, index)));
        }
        if (formOptions.Count > 1)
        {
            var chosen = await PadMenu.ShowAsync(host, $"Form of {species.Name}",
                "This species has multiple forms in this game.", [.. formOptions]);
            if (chosen is null) return null;
            var index = formOptions.FindIndex(o => o.Label == chosen);
            form = Math.Max(0, index);
        }

        // Step 2 - the features.
        return await ShowFeaturesFormAsync(host, data, session, species, form);
    }

    /// <summary>
    /// Caches the bundled sprite of exactly this form (SpriteCatalog naming: b_479-5.png,
    /// b_25-8p.png, b_869-1-0.png, Gen 9 artwork) without blocking the UI thread. A form with
    /// no art of its own gets no icon rather than its base form's: the label carries it.
    /// </summary>
    private static async Task<string?> FormSpritePathAsync(int species, int form)
    {
        var target = System.IO.Path.Combine(FileSystem.CacheDirectory, $"form-v2-{species}-{form}.png");
        if (File.Exists(target)) return target;
        try
        {
            foreach (var candidate in SpriteCatalog.BundledCandidates(new SpriteLook(species, form, false)))
            {
                if (candidate.Fidelity is SpriteFidelity.BaseForm or SpriteFidelity.Unknown) break;
                var asset = await TryOpenAsync(candidate.Path);
                if (asset is null) continue;
                await using (asset)
                await using (var output = File.Create(target))
                    await asset.CopyToAsync(output).ConfigureAwait(false);
                return target;
            }
            return null;
        }
        catch
        {
            return null; // sprite not bundled for this form; the label still carries it
        }
    }

    private static async Task<Stream?> TryOpenAsync(string source)
    {
        try { return await FileSystem.OpenAppPackageFileAsync(source).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
    }

    private static Task<GenerationRequest?> ShowFeaturesFormAsync(Grid host, IGameDataService data, ISaveEngineSession session, PickItem species, int form = 0)
    {
        var result = new TaskCompletionSource<GenerationRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();

        int? nature = null, ability = null, ball = null;
        var moves = new int?[4];
        var shiny = false;

        var level = new EditorEntry
        {
            Placeholder = "auto",
            Keyboard = Keyboard.Numeric,
            FontSize = EditorText,
            FontFamily = DsChrome.PixelFont,
            TextColor = EditorValue,
            PlaceholderColor = EditorPaint.ExpInk.ToMauiColor(),
            BackgroundColor = Colors.Transparent,
            HeightRequest = EditorRowHeight,
            VerticalTextAlignment = TextAlignment.Center,
        };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(GenerationRequest? request)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(request);
        }

        // The species card on the left: who this is, before anything is created.
        var card = new SpeciesCard(sprites, data, session, species, form);

        // A chooser row: caption, the current value ("auto" until picked), opens a picker.
        View Chooser(string caption, bool dark, Func<List<PickItem>> items, Func<int?> get, Action<int?> set,
            Func<int?, Task<PickItem?>>? open = null, Func<int?, View?>? leading = null)
        {
            var value = EditorValueLabel();
            value.Text = "auto";
            // The picked value's art (ball icon, move type) goes before its name.
            var content = new HorizontalStackLayout { Spacing = Design(14), Children = { value } };
            View? art = null;
            void Refresh()
            {
                var current = get();
                value.Text = current is { } id ? items().FirstOrDefault(x => x.Id == id)?.Name ?? "auto" : "auto";
                if (art is not null) content.Children.Remove(art);
                art = leading?.Invoke(current);
                if (art is not null) content.Children.Insert(0, art);
            }
            var grid = new Grid
            {
                ColumnSpacing = Design(16),
                ColumnDefinitions = [new(new GridLength(Design(214))), new(GridLength.Star), new(GridLength.Auto)],
            };
            grid.Add(EditorCaption(caption), 0, 0);
            grid.Add(content, 1, 0);
            grid.Add(EditorChevron(), 2, 0);
            var row = EditorBand(grid, dark);
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                var choices = items();
                if (choices.Count == 0) return;
                var picked = open is not null ? await open(get()) : await PickerMenu.ShowAsync(host, caption, choices, get());
                if (picked is not null) set(picked.Id);
                Refresh();
            };
            row.GestureRecognizers.Add(tap);
            return row;
        }

        List<PickItem> NatureItems() => NaturePicker.Items(data.NatureNames);
        // The mon does not exist yet: preview a perfect-IV, untrained one at the typed
        // level (50 while the level is still "auto").
        Task<PickItem?> OpenNature(int? current)
        {
            var previewLevel = int.TryParse(level.Text?.Trim(), out var typed) ? typed : 50;
            var preview = NaturePicker.Service?.PreviewSpecies(session, species.Id, form, previewLevel);
            return NaturePicker.ShowAsync(host, data.NatureNames, current, preview);
        }
        List<PickItem> AbilityItems() => InfoPickers.AbilityItems(data, session, species.Id, form);
        List<PickItem> BallItems() =>
            Enumerable.Range(1, data.BallNames.Count - 1).Where(i => data.BallNames[i].Length > 0)
                .Select(i => new PickItem(i, data.BallNames[i])).ToList();
        List<PickItem>? moveChoices = null;
        List<PickItem> MoveItems() => moveChoices ??= InfoPickers.MoveRows(data, session);

        // Level and shiny share a band; the star toggles shiny.
        var star = EditorValueLabel();
        void RefreshStar()
        {
            star.Text = shiny ? "★" : "☆";
            star.TextColor = shiny ? UiTokens.Gold : EditorValue;
            card.SetShiny(shiny);
        }
        RefreshStar();
        var shinyHalf = EditorBand(new HorizontalStackLayout { Spacing = Design(20), Children = { EditorCaption("Shiny", Design(128)), star } }, dark: true);
        var shinyTap = new TapGestureRecognizer();
        shinyTap.Tapped += (_, _) => { shiny = !shiny; RefreshStar(); };
        shinyHalf.GestureRecognizers.Add(shinyTap);
        var levelHalf = EditorBand(new HorizontalStackLayout { Spacing = Design(20), Children = { EditorCaption("Level", Design(128)), level } }, dark: true);
        level.WidthRequest = Design(160);
        var levelShiny = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], Children = { levelHalf, shinyHalf } };
        Grid.SetColumn(shinyHalf, 1);

        var natureRow = Chooser("Nature", false, NatureItems, () => nature, v => nature = v, OpenNature);
        natureRow.IsVisible = session.Generation >= 3; // Gen 1/2 have no natures
        var abilityRow = Chooser("Ability", true, AbilityItems, () => ability, v => ability = v);
        var ballRow = Chooser("Ball", false, BallItems, () => ball, v => ball = v,
            leading: id => id is { } b && sprites is not null ? BallArt(sprites, b) : null);
        var moveRows = new View[4];
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            // The species' learnable moves first, with the Legal (Y) and type (X) filters.
            Task<PickItem?> OpenMove(int? current) => InfoPickers.ShowMovesAsync(host, $"Move {index + 1}", data, session,
                IMonInfoService.NoSlot, 0, current, species.Id, form);
            moveRows[i] = Chooser($"Move {i + 1}", i % 2 == 0, MoveItems, () => moves[index], v => moves[index] = v, OpenMove,
                leading: id => id is { } m && InfoPickers.Info?.GetMove(session, m) is { } facts ? InfoKit.TypeBadge(facts.Type, Design(128)) : null);
        }

        void Generate()
        {
            int? parsedLevel = int.TryParse(level.Text?.Trim(), out var lv) ? lv : null;
            var pickedMoves = moves.Where(m => m is > 0).Select(m => m!.Value).ToList();
            Close(new GenerationRequest(species.Id, parsedLevel, shiny, nature, ability, ball,
                pickedMoves.Count > 0 ? pickedMoves : null, form, Services.HaXMode.IsOn));
        }

        var generate = EditorTool("Generate", primary: true);
        generate.Clicked += (_, _) => Generate();
        var cancel = EditorTool("Cancel");
        cancel.Clicked += (_, _) => Close(null);
        var buttons = new Grid
        {
            ColumnSpacing = Design(20),
            Margin = new Thickness(Design(28), Design(16), Design(28), Design(8)),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            Children = { cancel, generate },
        };
        Grid.SetColumn(generate, 1);

        var note = new Label
        {
            Text = "Everything left on \"auto\" is chosen by the legalizer, so the Pokémon comes out legal.",
            FontFamily = DsChrome.PixelFont,
            FontSize = EditorText * 0.8,
            TextColor = EditorPaint.ExpInk.ToMauiColor(),
            Margin = new Thickness(Design(34), Design(8)),
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var rows = new VerticalStackLayout
        {
            Children =
            {
                EditorSection("Features"), note, levelShiny, natureRow, abilityRow, ballRow,
                EditorSection("Moves"), moveRows[0], moveRows[1], moveRows[2], moveRows[3],
                buttons,
            },
        };

        var body = new Grid
        {
            ColumnSpacing = Design(24),
            ColumnDefinitions = [new(new GridLength(Design(560))), new(GridLength.Star)],
            Children = { card, new ScrollView { Content = rows } },
        };
        Grid.SetColumn(body.Children[1] as View, 1);
        var backdrop = new SKCanvasView { InputTransparent = true };
        backdrop.PaintSurface += PaintGuard.Wrap("GenerateWizard backdrop", (_, args) =>
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            StoragePaint.WellPanel(args.Surface.Canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height),
                args.Info.Width / 1720f);
        });
        var content = new Grid
        {
            WidthRequest = Design(1720),
            HeightRequest = Design(900),
            Padding = new Thickness(Design(24)),
            Children = { backdrop, body },
        };
        backdrop.Margin = new Thickness(-Design(24));

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: Design(1780));
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(cancel: () => Close(null), confirm: () => Generate());
        return result.Task;
    }

    /// <summary>A ball icon, pixel-sharp.</summary>
    private static SKCanvasView BallArt(ISpriteService sprites, int ball)
    {
        var view = new SKCanvasView { WidthRequest = Design(40), HeightRequest = Design(40), InputTransparent = true, VerticalOptions = LayoutOptions.Center };
        view.PaintSurface += PaintGuard.Wrap("GenerateWizard view", (_, args) =>
        {
            var c = args.Surface.Canvas;
            c.Clear(SKColors.Transparent);
            var bitmap = sprites.GetBall(ball);
            if (bitmap is null) { sprites.WarmBall(ball, () => MainThread.BeginInvokeOnMainThread(view.InvalidateSurface)); return; }
            var image = Services.SpriteImages.Of(bitmap);
            c.DrawImage(image, new SKRect(0, 0, args.Info.Width, args.Info.Height), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        });
        return view;
    }

    /// <summary>
    /// Who is being created, drawn: the name banner, Showdown's front sprite (shiny once the
    /// star is on), the type plates, the base stats, the abilities by slot and the gender ratio.
    /// </summary>
    private sealed class SpeciesCard : SKCanvasView
    {
        private const float DesignWidth = 560;
        private readonly ISpriteService? _sprites;
        private readonly string _name;
        private readonly int _species, _form;
        private readonly Domain.SpeciesCard? _card;
        private readonly IGameDataService _data;
        private bool _shiny;

        public SpeciesCard(ISpriteService? sprites, IGameDataService data, ISaveEngineSession session, PickItem species, int form)
        {
            _sprites = sprites;
            _data = data;
            _species = species.Id;
            _form = form;
            var forms = session.GetFormChoices(species.Id);
            var formName = form > 0 && form < forms.Count ? forms[form] : "";
            _name = formName.Length > 0 ? $"{species.Name} ({formName})" : species.Name;
            _card = InfoPickers.Info?.GetSpeciesCard(session, species.Id, form);
            InputTransparent = true;
            PaintSurface += PaintGuard.Wrap("GenerateWizard.OnPaint", OnPaint);
        }

        public void SetShiny(bool shiny)
        {
            if (_shiny == shiny) return;
            _shiny = shiny;
            InvalidateSurface();
        }

        private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            c.Clear(SKColors.Transparent);
            var scale = args.Info.Width / DesignWidth;
            c.Scale(scale);
            var height = args.Info.Height / scale;

            // The name banner.
            using (var banner = new SKPath())
            {
                var r = new SKRect(0, 8, DesignWidth - 20, 84);
                banner.MoveTo(r.Left + 18, r.Top);
                banner.LineTo(r.Right - 30, r.Top);
                banner.LineTo(r.Right, r.Bottom);
                banner.LineTo(r.Left + 18, r.Bottom);
                banner.ArcTo(new SKRect(r.Left, r.Bottom - 36, r.Left + 36, r.Bottom), 90, 90, false);
                banner.LineTo(r.Left, r.Top + 18);
                banner.ArcTo(new SKRect(r.Left, r.Top, r.Left + 36, r.Top + 36), 180, 90, false);
                banner.Close();
                using var fill = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        [StoragePaint.BannerTop, StoragePaint.BannerBottom], SKShaderTileMode.Clamp),
                };
                c.DrawPath(banner, fill);
                using var rim = new SKPaint { Color = Pksm.Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
                c.DrawPath(banner, rim);
            }
            SummaryInk.Draw(c, SummaryInk.Fit($"#{_species:000} {_name}", 36, DesignWidth - 90), 28, SummaryInk.Center(46, 36), 36, Pksm.Ink);

            // The Pokémon, at a whole-number scale.
            if (_sprites is not null)
            {
                void Redraw() => MainThread.BeginInvokeOnMainThread(InvalidateSurface);
                var look = new SpriteLook(_species, _form, _shiny);
                if (_sprites.TryGetShowdownFront(look, Redraw, out var sprite))
                {
                    sprite ??= _sprites.GetSprite(look);
                    if (sprite is null) _sprites.Warm(look, Redraw);
                    else
                    {
                        var k = Math.Clamp(MathF.Floor(Math.Min(300f / sprite.Width, 260f / sprite.Height)), 1, 5);
                        var w = sprite.Width * k;
                        var h = sprite.Height * k;
                        var image = Services.SpriteImages.Of(sprite);
                        c.DrawImage(image, new SKRect(DesignWidth / 2 - w / 2, 250 - h / 2, DesignWidth / 2 + w / 2, 250 + h / 2),
                            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                    }
                }
            }
            if (_card is not { } card) return;

            var types = card.Types.Where(TypeFacts.IsValid).ToList();
            var x = DesignWidth / 2 - (types.Count * 150 + Math.Max(0, types.Count - 1) * 16) / 2f;
            foreach (var type in types)
            {
                TypePlates.Paint(c, new SKRect(x, 400, x + 150, 448), type);
                x += 166;
            }

            // Base stats, two columns, and the total.
            var labelInk = EditorPaint.Label;
            var valueInk = EditorPaint.Value;
            string[] names = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
            int[] values = [card.BaseStats.Hp, card.BaseStats.Atk, card.BaseStats.Def, card.BaseStats.SpA, card.BaseStats.SpD, card.BaseStats.Spe];
            for (var i = 0; i < 6; i++)
            {
                var col = i / 3;
                var cy = 500 + (i % 3) * 44;
                var left = 40 + col * 250;
                SummaryInk.Draw(c, names[i], left, SummaryInk.Center(cy, 30), 30, labelInk);
                SummaryInk.Draw(c, values[i].ToString(), left + 180, SummaryInk.Center(cy, 30), 30, valueInk, align: SKTextAlign.Right);
            }
            SummaryInk.Draw(c, $"Total {card.Total}", DesignWidth / 2, SummaryInk.Center(640, 30), 30, valueInk, align: SKTextAlign.Center);

            // Abilities by slot, then the gender ratio.
            var y = 700f;
            foreach (var choice in card.Abilities)
            {
                if (y > height - 40) break;
                var name = (uint)choice.Id < (uint)_data.AbilityNames.Count ? _data.AbilityNames[choice.Id] : $"#{choice.Id}";
                var slot = choice.Slot == "Hidden" ? "Hidden" : $"Slot {choice.Slot}";
                SummaryInk.Draw(c, SummaryInk.Fit(name, 30, 330), 40, SummaryInk.Center(y, 30), 30, valueInk);
                SummaryInk.Draw(c, slot, DesignWidth - 40, SummaryInk.Center(y, 26), 26, choice.Slot == "Hidden" ? Pksm.ShinyGold : EditorPaint.ExpInk, align: SKTextAlign.Right);
                y += 44;
            }
            if (card.Gender is { } gender && y <= height - 40)
                SummaryInk.Draw(c, SummaryInk.Fit(gender.Label, 28, DesignWidth - 80), 40, SummaryInk.Center(y + 8, 28), 28, EditorPaint.ExpInk);
        }
    }
}
