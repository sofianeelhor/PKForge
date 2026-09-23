using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The info-rich choosers every editor opens for moves, abilities, held items and types.
/// Rows wear the shared <see cref="InfoKit"/> pieces; legal choices come first with how
/// they are obtained, and a filter chip (Y) flips to the full list. HaX mode starts on
/// the full list. Picking only returns an id: callers apply it through their usual
/// guarded write paths, so Hardcore mode still decides whether anything is written.
/// </summary>
public static class InfoPickers
{
    /// <summary>The facts service registered by the app (null outside the MAUI host).</summary>
    public static IMonInfoService? Info => IPlatformApplication.Current?.Services.GetService<IMonInfoService>();

    private static string NameOf(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : $"#{id}";

    // ---------- Moves ----------

    /// <summary>
    /// Move chooser for one mon: type badge, category, power/accuracy/PP per row, legal
    /// moves first tagged "Lv 32 / TM / Egg / Tutor", and a live card with the effect.
    /// </summary>
    /// <param name="species">A pending (unsaved) species edit to learn for, if any.</param>
    public static async Task<PickItem?> ShowMovesAsync(Grid host, string title, IGameDataService data, ISaveEngineSession? session,
        int box, int slot, int? current, int? species = null, int? form = null, bool includeNone = true)
    {
        IReadOnlyList<MoveChoice> choices = [];
        if (session is not null && Info is { } info)
        {
            try { choices = await Task.Run(() => info.GetMoveChoices(session, box, slot, species, form)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        }
        if (choices.Count == 0)
        {
            // No facts for this session (none open, or a romhack's own move table): the plain list.
            var plain = Enumerable.Range(includeNone ? 0 : 1, Math.Max(0, data.MoveNames.Count - (includeNone ? 0 : 1)))
                .Where(i => i == 0 || data.MoveNames[i].Length > 0)
                .Select(i => new PickItem(i, i == 0 ? "(none)" : data.MoveNames[i])).ToList();
            return await PickerMenu.ShowAsync(host, title, plain, current);
        }

        var hasLearnData = choices.Any(c => c.Learn.IsLegal);
        var byId = choices.ToDictionary(c => c.Id);
        var items = new List<PickItem>(choices.Count + 1);
        if (includeNone) items.Add(new PickItem(0, "(none)"));
        foreach (var move in MoveChoiceOrder.Sort(choices.Where(c => (uint)c.Id < (uint)data.MoveNames.Count && data.MoveNames[c.Id].Length > 0),
                     id => NameOf(data.MoveNames, id)))
            items.Add(MoveRow(move, NameOf(data.MoveNames, move.Id), hasLearnData));

        var filter = hasLearnData
            ? new PickerFilter("LEGAL", "SHOW ALL", item => item.Id == 0 || !item.Muted, StartOn: !Services.HaXMode.IsOn)
            : null;
        return await PickerMenu.ShowAsync(host, title, items, current, MovePreview(byId, hasLearnData), filter);
    }

    private static PickItem MoveRow(MoveChoice move, string name, bool hasLearnData) =>
        new(move.Id, name, Detail: InfoKit.MoveNumbers(move))
        {
            TypeId = move.Type,
            Category = move.Category,
            Tag = move.Learn.IsLegal ? move.Learn.Label : null,
            TagColor = InfoKit.LearnColor(move.Learn.Kind),
            Muted = hasLearnData && !move.Learn.IsLegal,
            Keywords = $"{TypeFacts.Name(move.Type)} {TypeFacts.CategoryName(move.Category)} {move.Learn.Label}",
        };

    /// <summary>The card under the move list: name, type + category, numbers, effect, how it is learned.</summary>
    private static PickerPreview MovePreview(IReadOnlyDictionary<int, MoveChoice> moves, bool hasLearnData)
    {
        var heading = InfoKit.Heading();
        var badge = InfoKit.TypeBadge();
        var category = new InfoKit.CategoryIcon();
        var numbers = InfoKit.DetailLine();
        var effect = InfoKit.Body();
        effect.MaxLines = 3;
        effect.LineBreakMode = LineBreakMode.TailTruncation;
        var learn = InfoKit.Note();
        var badges = new HorizontalStackLayout { Spacing = 4, Children = { badge, category } };
        var card = InfoKit.Card(InfoKit.HeaderRow(heading, badges), numbers, effect, learn);
        card.MinimumHeightRequest = 96;

        void Show(PickItem? item)
        {
            if (item is null || !moves.TryGetValue(item.Id, out var move))
            {
                heading.Text = item?.Name.ToUpperInvariant() ?? "";
                badges.IsVisible = numbers.IsVisible = learn.IsVisible = false;
                effect.Text = item?.Id == 0 ? "Leaves the move slot empty." : "";
                return;
            }
            badges.IsVisible = true;
            heading.Text = item.Name.ToUpperInvariant();
            InfoKit.SetType(badge, move.Type);
            category.Category = move.Category;
            numbers.Text = $"{TypeFacts.CategoryName(move.Category)} · Power {InfoKit.Power(move.Power)} · Accuracy {InfoKit.Accuracy(move.Accuracy)} · PP {move.PP}";
            numbers.IsVisible = true;
            effect.Text = move.Effect.Length > 0 ? move.Effect : "No description available.";
            learn.IsVisible = hasLearnData;
            learn.Text = move.Learn.Sentence;
            learn.TextColor = InfoKit.ToneColor(move.Learn.IsLegal ? InfoKit.NoteTone.Good : InfoKit.NoteTone.Bad);
        }

        return new PickerPreview(card, Show);
    }

    /// <summary>Move rows for a Pokémon that does not exist yet: types, categories and numbers, no learn tags.</summary>
    public static List<PickItem> MoveRows(IGameDataService data, ISaveEngineSession session)
    {
        var rows = new List<PickItem>();
        for (var id = 1; id < data.MoveNames.Count; id++)
        {
            if (data.MoveNames[id].Length == 0) continue;
            rows.Add(Info?.GetMove(session, id) is { } move
                ? MoveRow(move, data.MoveNames[id], hasLearnData: false)
                : new PickItem(id, data.MoveNames[id]));
        }
        return rows;
    }

    // ---------- Species card ----------

    /// <summary>
    /// The identity card shown before a Pokémon is created: typing, base stats with the
    /// total, abilities with their slots and effects, and the gender ratio.
    /// </summary>
    public static View? SpeciesCard(IGameDataService data, ISaveEngineSession session, int species, int form, string name)
    {
        if (Info?.GetSpeciesCard(session, species, form) is not { } card) return null;
        var children = new List<View>
        {
            InfoKit.HeaderRow(InfoKit.Heading($"#{species:000} {name.ToUpperInvariant()}"), InfoKit.TypeRow(card.Types)),
            InfoKit.BaseStatBars(card.BaseStats, card.Total),
        };
        foreach (var ability in card.Abilities)
        {
            var row = InfoKit.HeaderRow(
                new Label { Text = NameOf(data.AbilityNames, ability.Id), FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink0 },
                InfoKit.Tag(ability.Slot == "Hidden" ? "HIDDEN" : $"SLOT {ability.Slot}",
                    ability.Slot == "Hidden" ? Color.FromArgb("#B8860B") : UiTokens.Blueprint));
            children.Add(row);
            if (ability.Effect.Length > 0) children.Add(InfoKit.DetailLine(ability.Effect, maxLines: 2));
        }
        if (card.Gender is { } gender)
            children.Add(InfoKit.HeaderRow(
                new Label { Text = "GENDER", FontFamily = DsChrome.PixelFont, FontSize = 10, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center },
                new Label { Text = gender.Label, FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink0 }));
        return InfoKit.Card([.. children]);
    }

    // ---------- Abilities ----------

    /// <summary>
    /// Ability rows: slot tag (1 / 2 / HIDDEN) and the short effect. Outside HaX mode only
    /// the species' own abilities are offered; HaX lists them first, then every other one.
    /// </summary>
    public static List<PickItem> AbilityItems(IGameDataService data, ISaveEngineSession? session, int species, int form)
    {
        var own = session is not null && species > 0
            ? Info?.GetAbilityChoices(session, species, form) ?? session.GetAbilityChoices(species, form)
                .Select((id, i) => new AbilityChoice(id, i == 2 ? "Hidden" : $"{i + 1}", DexFacts.Ability(id) ?? "")).ToList()
            : [];
        var items = own.Select(a => new PickItem(a.Id, NameOf(data.AbilityNames, a.Id), Detail: Blank(a.Effect))
        {
            Tag = a.Slot == "Hidden" ? "HIDDEN" : $"SLOT {a.Slot}",
            TagColor = a.Slot == "Hidden" ? Color.FromArgb("#B8860B") : UiTokens.Blueprint,
            Keywords = a.Slot,
        }).ToList();
        if (!Services.HaXMode.IsOn) return items;

        var seen = own.Select(a => a.Id).ToHashSet();
        var rest = Enumerable.Range(0, data.AbilityNames.Count)
            .Where(id => !seen.Contains(id) && (id == 0 || data.AbilityNames[id].Length > 0))
            .Select(id => new PickItem(id, id == 0 ? "(none)" : data.AbilityNames[id], Detail: Blank(DexFacts.Ability(id)))
            {
                Muted = own.Count > 0,
            })
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        items.AddRange(rest);
        return items;
    }

    // ---------- Held items ----------

    /// <summary>
    /// Held-item rows with sprites and short effects. Items the open game lets a Pokémon
    /// hold come first; a filter chip flips to every item (HaX starts there).
    /// </summary>
    public static Task<PickItem?> ShowHeldItemsAsync(Grid host, string title, IGameDataService data, ISaveEngineSession? session,
        int? current, Func<int, string?> iconFor)
    {
        var names = data.ItemNames;
        // The open game's own item table names ids correctly in Gen 1-4 (Rare Candy et al).
        var gameNames = session?.GetItemNames() is { Count: > 0 } n ? n : names;
        var legal = session is not null ? Info?.GetHeldItems(session).ToHashSet() ?? [] : [];

        var items = new List<PickItem>(names.Count) { new(0, "(none)", Detail: "Holds nothing.") };
        var rows = new List<PickItem>();
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            var description = DexFacts.Item(id < gameNames.Count ? gameNames[id] : names[id]) ?? DexFacts.Item(names[id]);
            rows.Add(new PickItem(id, names[id], iconFor(id), Blank(description)) { Muted = legal.Count > 0 && !legal.Contains(id) });
        }
        // Holdable first, in the game's id order (it groups balls, berries, plates…).
        items.AddRange(rows.OrderBy(r => r.Muted ? 1 : 0));
        var filter = legal.Count > 0
            ? new PickerFilter("HOLDABLE", "SHOW ALL", item => item.Id == 0 || !item.Muted, StartOn: !Services.HaXMode.IsOn)
            : null;
        return PickerMenu.ShowAsync(host, title, items, current, filter: filter);
    }

    // ---------- Types (Tera, Hidden Power) ----------

    /// <summary>
    /// Tera type rows: badge per type and a note when the Tera type matches the Pokémon's
    /// own typing (its STAB on that type rises from 1.5× to 2×).
    /// </summary>
    public static Task<PickItem?> ShowTeraAsync(Grid host, IReadOnlyList<NamedChoice> choices, int current, IReadOnlyList<int> ownTypes)
    {
        var items = choices.Select(c =>
        {
            var own = ownTypes.Contains(c.Id);
            return new PickItem(c.Id, c.Name, Detail: TeraDetail(c.Id, own))
            {
                TypeId = c.Id,
                Tag = own ? "OWN TYPE" : c.Id == current ? "NOW" : null,
                TagColor = own ? UiTokens.Green : UiTokens.Blueprint,
            };
        }).ToList();
        return PickerMenu.ShowAsync(host, "TERA TYPE", items, current);
    }

    /// <summary>What the Tera type does for this Pokémon, in one line.</summary>
    public static string TeraDetail(int type, bool matchesOwnType) => type == TypeFacts.Stellar
        ? "One boost per move type: 1.2×, or 2× on its own types"
        : matchesOwnType
            ? $"Matches its own type: {TypeFacts.Name(type)} STAB rises to 2×"
            : $"{TypeFacts.Name(type)} moves gain 1.5× STAB; keeps its own STAB";

    /// <summary>
    /// Hidden Power type chooser (types Fighting-Dark). Each row previews the IVs the
    /// type needs from the current spread; null rows (impossible spreads) are muted.
    /// </summary>
    public static Task<PickItem?> ShowHiddenPowerAsync(Grid host, int? current, Func<int, IReadOnlyList<int>?> ivsFor)
    {
        var items = Enumerable.Range(1, 16).Select(type =>
        {
            var ivs = ivsFor(type);
            return new PickItem(type, TypeFacts.Name(type), Detail: ivs is null ? "Not reachable from these IVs" : $"IVs {string.Join("/", ivs)}")
            {
                TypeId = type,
                Tag = type == current ? "NOW" : null,
                Muted = ivs is null,
            };
        }).ToList();
        return PickerMenu.ShowAsync(host, "HIDDEN POWER", items, current);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
