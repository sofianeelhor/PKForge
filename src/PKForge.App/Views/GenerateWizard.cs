using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Domain;

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

        int? nature = null, ability = null, ball = null;
        var moves = new int?[4];

        var level = new Entry
        {
            Placeholder = "auto",
            Keyboard = Keyboard.Numeric,
            FontSize = 14,
            TextColor = UiTokens.Ink0,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
            WidthRequest = 90,
        };
        var shiny = new Switch { OnColor = UiTokens.Gold };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(GenerationRequest? request)
        {
            host.Remove(overlay);
            pad?.Dispose();
            result.TrySetResult(request);
        }

        // A chooser row: caption, current value ("auto" until picked), opens a picker.
        (View Row, Action Refresh) Chooser(string caption, Func<List<PickItem>> items, Func<int?> get, Action<int?> set,
            Func<int?, Task<PickItem?>>? open = null)
        {
            var value = new Label { TextColor = UiTokens.Ink0, FontSize = UiTokens.TextBody, FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center, Text = "auto" };
            void Refresh()
            {
                var current = get();
                value.Text = current is { } id ? items().FirstOrDefault(x => x.Id == id)?.Name ?? "auto" : "auto";
            }
            // A picker row (flat stripe), not a bordered card.
            var chip = new Border
            {
                BackgroundColor = UiTokens.RowStripe,
                Stroke = Colors.Transparent,
                StrokeThickness = 1.2,
                StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
                Padding = new Thickness(10, 6),
                Content = new Grid
                {
                    ColumnDefinitions = [new(new GridLength(80)), new(GridLength.Star), new(GridLength.Auto)],
                    Children =
                    {
                        new Label { Text = Kit.Tidy(caption), FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                        value,
                        PksmIcons.Icon("search", 16),
                    },
                },
            };
            var inner = (Grid)chip.Content!;
            Grid.SetColumn(inner.Children[1] as View, 1);
            Grid.SetColumn(inner.Children[2] as View, 2);
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                var choices = items();
                if (choices.Count == 0) return;
                var picked = open is not null ? await open(get()) : await PickerMenu.ShowAsync(host, caption, choices, get());
                if (picked is not null) set(picked.Id);
                Refresh();
            };
            chip.GestureRecognizers.Add(tap);
            return (chip, Refresh);
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

        var (natureRow, _) = Chooser("Nature", NatureItems, () => nature, v => nature = v, OpenNature);
        natureRow.IsVisible = session.Generation >= 3; // Gen 1/2 have no natures
        var (abilityRow, _) = Chooser("Ability", AbilityItems, () => ability, v => ability = v);
        var (ballRow, _) = Chooser("BALL", BallItems, () => ball, v => ball = v);
        var moveRows = new View[4];
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            (moveRows[i], _) = Chooser($"MOVE {i + 1}", MoveItems, () => moves[index], v => moves[index] = v);
        }

        void Generate()
        {
            int? parsedLevel = int.TryParse(level.Text?.Trim(), out var lv) ? lv : null;
            var pickedMoves = moves.Where(m => m is > 0).Select(m => m!.Value).ToList();
            Close(new GenerationRequest(species.Id, parsedLevel, shiny.IsToggled, nature, ability, ball,
                pickedMoves.Count > 0 ? pickedMoves : null, form, Services.HaXMode.IsOn));
        }

        var generate = Kit.Capsule("Generate", UiTokens.Green, primary: true, icon: "create");
        generate.Clicked += (_, _) => Generate();
        var cancel = Kit.Capsule("Cancel", UiTokens.Ink1, icon: "close");
        cancel.Clicked += (_, _) => Close(null);

        var levelRow = new HorizontalStackLayout
        {
            Spacing = 14,
            Children =
            {
                new Label { Text = "Level", FontSize = UiTokens.TextSmall, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                level,
                new Label { Text = "Shiny", FontSize = UiTokens.TextSmall, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                shiny,
            },
        };

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar($"Step 2 · {species.Name}"),
                new Label { Text = "Everything left on \"auto\" is chosen by the legalizer to guarantee a legal Pokémon.", TextColor = UiTokens.InkSoft, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.WordWrap },
                levelRow, natureRow, abilityRow, ballRow,
                moveRows[0], moveRows[1], moveRows[2], moveRows[3],
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, generate } },
            },
        };

        // Who this is, before anything is created: typing, base stats, abilities, gender.
        if (InfoPickers.SpeciesCard(data, session, species.Id, form, species.Name) is { } card)
            content.Children.Insert(1, card);

        // Host-capped + scrolls, so the GENERATE / CANCEL buttons are never pushed off-screen.
        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 480);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(cancel: () => Close(null), confirm: () => Generate());
        return result.Task;
    }
}
