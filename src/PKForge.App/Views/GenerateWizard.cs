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
        var formOptions = forms
            .Select((name, index) => new PadOption(
                index == 0 || name.Length == 0 ? "Standard" : name,
                IconPath: index == 0 || name.Length == 0 ? null : "editor"))
            .Where((option, index) => index == 0 || forms[index].Length > 0)
            .ToList();
        if (formOptions.Count > 1)
        {
            var chosen = await PadMenu.ShowAsync(host, $"FORM OF {species.Name.ToUpperInvariant()}",
                "This species has multiple forms in this game.", [.. formOptions]);
            if (chosen is null) return null;
            var index = formOptions.FindIndex(o => o.Label == chosen);
            form = Math.Max(0, index);
        }

        // Step 2 - the features.
        return await ShowFeaturesFormAsync(host, data, session, species, form);
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
        (View Row, Action Refresh) Chooser(string caption, Func<List<PickItem>> items, Func<int?> get, Action<int?> set)
        {
            var value = new Label { TextColor = UiTokens.Ink0, FontSize = 13, FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center, Text = "auto" };
            void Refresh()
            {
                var current = get();
                value.Text = current is { } id ? items().FirstOrDefault(x => x.Id == id)?.Name ?? "auto" : "auto";
            }
            var chip = new Border
            {
                BackgroundColor = UiTokens.ShellPress,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 1.2,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(10, 6),
                Content = new Grid
                {
                    ColumnDefinitions = [new(new GridLength(80)), new(GridLength.Star), new(GridLength.Auto)],
                    Children =
                    {
                        new Label { Text = caption, FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
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
                var picked = await PickerMenu.ShowAsync(host, caption, choices, get());
                if (picked is not null) set(picked.Id);
                Refresh();
            };
            chip.GestureRecognizers.Add(tap);
            return (chip, Refresh);
        }

        List<PickItem> NatureItems() =>
            Enumerable.Range(0, data.NatureNames.Count).Where(i => data.NatureNames[i].Length > 0)
                .Select(i => new PickItem(i, data.NatureNames[i])).ToList();
        List<PickItem> AbilityItems() =>
            session.GetAbilityChoices(species.Id, form)
                .Select(id => new PickItem(id, id < data.AbilityNames.Count ? data.AbilityNames[id] : $"#{id}")).ToList();
        List<PickItem> BallItems() =>
            Enumerable.Range(1, data.BallNames.Count - 1).Where(i => data.BallNames[i].Length > 0)
                .Select(i => new PickItem(i, data.BallNames[i])).ToList();
        List<PickItem> MoveItems() =>
            Enumerable.Range(1, data.MoveNames.Count - 1).Where(i => data.MoveNames[i].Length > 0)
                .Select(i => new PickItem(i, data.MoveNames[i])).ToList();

        var (natureRow, _) = Chooser("NATURE", NatureItems, () => nature, v => nature = v);
        var (abilityRow, _) = Chooser("ABILITY", AbilityItems, () => ability, v => ability = v);
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
                pickedMoves.Count > 0 ? pickedMoves : null, form));
        }

        var generate = Kit.Capsule("GENERATE", UiTokens.Green);
        generate.Clicked += (_, _) => Generate();
        var cancel = Kit.Capsule("CANCEL", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var levelRow = new HorizontalStackLayout
        {
            Spacing = 14,
            Children =
            {
                new Label { Text = "LEVEL", FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                level,
                new Label { Text = "SHINY", FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                shiny,
            },
        };

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar($"STEP 2 · {species.Name.ToUpperInvariant()}"),
                new Label { Text = "Everything left on \"auto\" is chosen by the legalizer to guarantee a legal Pokémon.", TextColor = UiTokens.InkSoft, FontSize = 11, LineBreakMode = LineBreakMode.WordWrap },
                levelRow, natureRow, abilityRow, ballRow,
                moveRows[0], moveRows[1], moveRows[2], moveRows[3],
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, generate } },
            },
        };

        // Host-capped + scrolls, so the GENERATE / CANCEL buttons are never pushed off-screen.
        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 480);
        overlay = Kit.AttachOverlay(host, window, () => Close(null));
        pad = new PadOverlay(cancel: () => Close(null), confirm: () => Generate());
        return result.Task;
    }
}
