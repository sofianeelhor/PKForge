using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// "How do I get this?" - the whole answer, no save required.
///
/// Pick a species, then a game: the first screen lists every mainline game grouped by
/// generation, each with how many ways it offers (or that it cannot be obtained there).
/// Choosing a game opens its cards, grouped by kind, with repeated sightings of the
/// same place collapsed into one card so a 250-entry game stays readable. When a save
/// is open, its own game is flagged and CATCH materializes a legal Pokémon from the
/// highlighted card into the first empty slot through the usual backed-up write; for
/// any other game the cards stay informational.
/// </summary>
public static class EncounterGallery
{
    /// <summary>
    /// Game-scoped "how to get": the open save's own game only, straight to its cards.
    /// This is the storage-tools answer ("how do I get this in the game I am editing?").
    /// </summary>
    public static async Task ShowGameScopedAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession session, Action repaint)
    {
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        var sprites = services?.GetService<ISpriteService>();
        if (data is null || sprites is null) return;

        var species = await PokedexPicker.ShowAsync(host, data, session);
        if (species is null) return;
        var (form, cancelled) = await PickFormAsync(host, session, species.Id);
        if (cancelled) return;
        await ShowGameCardsAsync(host, viewModel, session, sprites, species.Id, form, repaint);
    }

    /// <summary>Game-scoped cards for a species already chosen (the dex editor's action).</summary>
    public static Task ShowForSpeciesAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession session, int species, int form, Action repaint)
    {
        var sprites = IPlatformApplication.Current?.Services?.GetService<ISpriteService>();
        return sprites is null
            ? Task.CompletedTask
            : ShowGameCardsAsync(host, viewModel, session, sprites, species, form, repaint);
    }

    /// <summary>One game's cards and its catch loop: no game list, no other games.</summary>
    private static async Task ShowGameCardsAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession session,
        ISpriteService sprites, int species, int form, Action repaint)
    {
        // The save's own list, so a picked card index matches what PlaceEncounter rebuilds.
        var listing = new GameEncounterListing(
            string.Join(" / ", session.GameNames), session.Generation, true, session.GetEncounterCards(species, form));
        while (true)
        {
            var pick = await CardShelf.ShowAsync(host, listing, sprites, species, form, canCatch: true, back: "Close", progress: session.GetBadges());
            if (pick is null || viewModel is null) return;

            var target = viewModel.VisibleSlots.FirstOrDefault(s => s.Species is null)?.Slot ?? -1;
            if (target < 0)
            {
                viewModel.Status = "No empty slot in this box for the encounter.";
                return;
            }
            var card = listing.Cards[pick.Value];
            await viewModel.RunMutationAsync(s => s.PlaceEncounter(species, form, pick.Value, viewModel.BoxIndex, target), target, action: SaveAction.CreateMon);
            viewModel.Status = $"Caught · {(string.IsNullOrEmpty(card.Location) ? card.Kind : card.Location)}";
            repaint();
            return;
        }
    }

    private static async Task<(int Form, bool Cancelled)> PickFormAsync(Grid host, ISaveEngineSession session, int species)
    {
        var forms = session.GetFormChoices(species);
        if (forms.Count <= 1) return (0, false);
        var options = new List<PadOption>();
        for (var index = 0; index < forms.Count; index++)
            options.Add(new PadOption(forms[index]));
        var picked = await PadMenu.ShowAsync(host, "Form", null, options.ToArray());
        if (picked is null) return (0, true);
        var form = options.FindIndex(o => o.Label == picked);
        return (form < 0 ? 0 : form, false);
    }

    // ── Screen 0: the whole answer, every game ─────────────────────────────────

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession? session, Action repaint)
    {
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        var sprites = services?.GetService<ISpriteService>();
        var lookup = services?.GetService<IEncounterLookup>();
        if (data is null || sprites is null || lookup is null) return;

        // The species picker needs a save-shaped context for its type/stat filters.
        // Without an open save we browse with a throwaway blank of the newest format:
        // it is never written and never shown as the user's game.
        var browseSession = session ?? services?.GetService<ISaveEngine>()?.OpenBlankSession(9);
        if (browseSession is null) return;
        var ownsSession = session is null;

        try
        {
            var species = await PokedexPicker.ShowAsync(host, data, browseSession);
            if (species is null) return;
            var (form, cancelled) = await PickFormAsync(host, browseSession, species.Id);
            if (cancelled) return;
            await ShowAllGamesAsync(host, viewModel, session, data, sprites, lookup, species.Id, form, repaint);
        }
        finally
        {
            if (ownsSession) browseSession.Dispose();
        }
    }

    /// <summary>
    /// All-games answer for a species already chosen - the living dex action. No picker:
    /// the player pointed at this Pokémon, so it answers for exactly that one.
    /// </summary>
    public static async Task ShowAllGamesForSpeciesAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession? session, int species, Action repaint)
    {
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        var sprites = services?.GetService<ISpriteService>();
        var lookup = services?.GetService<IEncounterLookup>();
        if (data is null || sprites is null || lookup is null) return;

        var form = 0;
        if (session is not null)
        {
            var (picked, cancelled) = await PickFormAsync(host, session, species);
            if (cancelled) return;
            form = picked;
        }
        await ShowAllGamesAsync(host, viewModel, session, data, sprites, lookup, species, form, repaint);
    }

    /// <summary>The whole answer: every game that gives this species out, and how.</summary>
    private static async Task ShowAllGamesAsync(Grid host, BoxBrowserViewModel? viewModel, ISaveEngineSession? session,
        IGameDataService data, ISpriteService sprites, IEncounterLookup lookup, int species, int form, Action repaint)
    {
        var speciesName = (uint)species < (uint)data.SpeciesNames.Count ? data.SpeciesNames[species] : $"#{species}";
        var title = speciesName;

        // Two sources, one answer: with a save open the games that save can be are
        // collapsed into a single "this save" row carrying the save's own union list
        // (the only list CATCH indices line up with); every other game comes from the
        // save-free lookup and stays informational.
        var names = session?.GameNames;
        var catchable = new HashSet<int>();
        var listings = await Task.Run(() =>
        {
            var all = lookup.Describe(species, form).ToList();
            if (session is null || names is null || names.Count == 0) return all;
            var matching = Enumerable.Range(0, all.Count).Where(i => names.Contains(all[i].GameName)).ToList();
            if (matching.Count == 0) return all;

            var union = session.GetEncounterCards(species, form);
            var label = string.Join(" / ", matching.Select(i => all[i].GameName));
            foreach (var index in matching.OrderByDescending(i => i))
                all.RemoveAt(index);
            all.Insert(0, new GameEncounterListing(label, session.Generation, union.Count > 0, union));
            catchable.Add(0);
            return all;
        });
        if (listings.Count == 0)
        {
            await PadMenu.ShowAsync(host, "How to get", $"{title} is not in the encounter database.", "OK");
            return;
        }

        while (true)
        {
            var choice = await GameShelf.ShowAsync(host, listings, sprites, species, form, [.. catchable]);
            if (choice is null) return;

            var listing = listings[choice.Value];
            var canCatch = catchable.Contains(choice.Value);
            var pick = await CardShelf.ShowAsync(host, listing, sprites, species, form, canCatch, back: "Games",
                progress: canCatch ? session?.GetBadges() : null);
            if (pick is null) continue; // back to the game list

            if (viewModel is null || session is null || !canCatch)
            {
                await PadMenu.ShowAsync(host, "Keep browsing",
                    $"Open {listing.GameName} to catch this one - the database answers for every game, but a Pokémon needs that save open to land in it.", "OK");
                continue;
            }
            var target = viewModel.VisibleSlots.FirstOrDefault(s => s.Species is null)?.Slot ?? -1;
            if (target < 0)
            {
                viewModel.Status = "No empty slot in this box for the encounter.";
                return;
            }
            var card = listing.Cards[pick.Value];
            await viewModel.RunMutationAsync(s => s.PlaceEncounter(species, form, pick.Value, viewModel.BoxIndex, target), target, action: SaveAction.CreateMon);
            viewModel.Status = $"Caught · {title} · {(string.IsNullOrEmpty(card.Location) ? card.Kind : card.Location)}";
            repaint();
            return;
        }
    }

    /// <summary>The Pokémon trimmed to its visible pixels, so it can be drawn large; null while it loads.</summary>
    private static SKImage? TrimmedSprite(ISpriteService sprites, int species, int form, SKCanvasView repaint)
    {
        var bitmap = sprites.GetSprite(species, form, false);
        if (bitmap is null)
        {
            sprites.Warm(species, form, false, () => MainThread.BeginInvokeOnMainThread(repaint.InvalidateSurface));
            return null;
        }
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var image = SKImage.FromBitmap(bitmap);
        return bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(image, bounds) : SKImage.FromBitmap(bitmap);
    }

    /// <summary>A bundled tool icon (ui/tools), read once per screen.</summary>
    private static SKImage? ToolIcon(Dictionary<string, SKImage?> cache, string name)
    {
        if (cache.TryGetValue(name, out var known)) return known;
        SKImage? image = null;
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync($"ui/tools/{name}.png").GetAwaiter().GetResult();
            using var bitmap = SKBitmap.Decode(stream);
            image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception error) when (error is IOException or FileNotFoundException) { }
        cache[name] = image;
        return image;
    }

    // ── Screen 1: the games, grouped by generation ──────────────────────────────

    /// <summary>
    /// Every game, in the same painted screen as one game's ways (<see cref="EncounterPaint"/>):
    /// the Pokémon at the left, one row per game with its generation, how it gives the Pokémon
    /// out, and the open save's own game first. ↑↓ pick, A shows the ways, B closes. A tap
    /// picks a game; a second tap opens it.
    /// </summary>
    private sealed class GameShelf : IPadHandler
    {
        private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly IReadOnlyList<GameEncounterListing> _listings;
        private readonly IReadOnlyList<int> _order;      // display order over _listings
        private readonly IReadOnlyList<int> _catchable;
        private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
        private readonly ISpriteService _sprites;
        private readonly int _species;
        private readonly int _form;
        private readonly Dictionary<string, SKImage?> _icons = new(StringComparer.Ordinal);
        private SKImage? _sprite;
        private EncounterHits? _hits;
        private int _index;
        private int _scroll;
        private SKPoint? _pressedAt;
        private bool _dragged;
        private float _dragRows;

        public static Task<int?> ShowAsync(Grid host, IReadOnlyList<GameEncounterListing> listings, ISpriteService sprites, int species, int form, IReadOnlyList<int> catchable) =>
            new GameShelf(host, listings, sprites, species, form, catchable)._result.Task;

        private GameShelf(Grid host, IReadOnlyList<GameEncounterListing> listings, ISpriteService sprites, int species, int form, IReadOnlyList<int> catchable)
        {
            _host = host;
            _listings = listings;
            _catchable = catchable;
            _sprites = sprites;
            _species = species;
            _form = form;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            // Oldest to newest, so "where can I get this?" reads chronologically; the open
            // save's own game first, since that is the answer the player acts on.
            var order = Enumerable.Range(0, listings.Count).ToList();
            order.Sort((a, b) =>
            {
                var byGen = listings[a].Generation.CompareTo(listings[b].Generation);
                return byGen != 0 ? byGen : string.CompareOrdinal(listings[a].GameName, listings[b].GameName);
            });
            foreach (var index in order.Where(i => catchable.Contains(i)).ToList())
            {
                order.Remove(index);
                order.Insert(0, index);
            }
            _order = order;

            _canvas.PaintSurface += PaintGuard.Wrap("EncounterGallery.Paint", Paint);
            _canvas.Touch += Touch;
            var footer = DsChrome.Footer(
                ("A", "Ways", () => OnPadButton(PadButton.A)),
                ("B", "Close", () => OnPadButton(PadButton.B)));
            var screen = new Grid
            {
                Padding = new Thickness(14, 12, 14, 0),
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { _canvas, footer },
            };
            Grid.SetRow(footer, 1);
            _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
            _router?.Push(this);
        }

        private static string Roman(int gen) => gen switch
        {
            1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => $"{gen}",
        };

        /// <summary>"3 ways · Wild, Eggs": the honest one-line answer per game.</summary>
        private static string Summarize(GameEncounterListing listing)
        {
            var kinds = listing.Cards.Select(c => c.Kind).Distinct().Select(KindLabel);
            return $"{listing.Cards.Count} {(listing.Cards.Count == 1 ? "way" : "ways")} · {string.Join(", ", kinds)}";
        }

        private static string KindLabel(string kind) => kind switch
        {
            "Egg" => "Eggs",
            "Wild" => "Wild",
            "Static" => "Gifts",
            "Trade" => "Trades",
            "Event" => "Events",
            _ => "Special",
        };

        private void Paint(object? sender, SKPaintSurfaceEventArgs args)
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            var rows = _order.Select(i =>
            {
                var listing = _listings[i];
                var current = _catchable.Contains(i);
                var detail = listing.Obtainable
                    ? $"{listing.GameName}: A shows each way to get it{(current ? ", and can catch it into this save." : ".")}"
                    : $"{listing.GameName} does not give this Pokémon out.";
                return new EncounterRow($"Generation {Roman(listing.Generation)}", listing.GameName,
                    listing.Obtainable ? Summarize(listing) : "Not in this game", "", 1, false, detail,
                    Badge: Roman(listing.Generation), Dim: !listing.Obtainable, Tag: current ? "This game" : null);
            }).ToList();
            var names = IPlatformApplication.Current?.Services.GetService<IGameDataService>()?.SpeciesNames;
            var species = names is not null && (uint)_species < (uint)names.Count ? names[_species] : $"#{_species}";
            var subtitle = $"Obtainable in {_listings.Count(l => l.Obtainable)} of {_listings.Count} games";
            _sprite ??= TrimmedSprite(_sprites, _species, _form, _canvas);
            var scene = new EncounterScene(species, _sprite, "", rows, _index, _scroll, name => ToolIcon(_icons, name), Games: true, Subtitle: subtitle);
            _hits = EncounterPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
        }

        private void Touch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (_hits is not { } hits) return;
            var at = args.Location;
            switch (args.ActionType)
            {
                case SKTouchAction.Pressed:
                    _pressedAt = at;
                    _dragged = false;
                    _dragRows = 0;
                    return;
                case SKTouchAction.Moved when _pressedAt is { } start && hits.List.Contains(start):
                    if (!_dragged && Math.Abs(start.Y - at.Y) < hits.RowHeight / 3) return;
                    _dragged = true;
                    var step = (int)((start.Y - at.Y) / hits.RowHeight - _dragRows);
                    if (step == 0) return;
                    _dragRows += step;
                    _scroll = Math.Clamp(_scroll + step, 0, Math.Max(0, _order.Count - hits.VisibleRows));
                    _canvas.InvalidateSurface();
                    return;
                case SKTouchAction.Released:
                    var tapped = !_dragged;
                    _pressedAt = null;
                    if (!tapped) return;
                    foreach (var (index, rect) in hits.Rows)
                    {
                        if (!rect.Contains(at)) continue;
                        if (index == _index) Close(_order[index]);
                        else Move(index);
                        return;
                    }
                    return;
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Move(_index - 1); return true;
                case PadButton.Down: Move(_index + 1); return true;
                case PadButton.L: Move(_index - Visible); return true;
                case PadButton.R: Move(_index + Visible); return true;
                case PadButton.A: if (_order.Count > 0) Close(_order[Math.Clamp(_index, 0, _order.Count - 1)]); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private int Visible => EncounterPaint.VisibleRows(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));

        private void Move(int index)
        {
            if (_order.Count == 0) return;
            _index = Math.Clamp(index, 0, _order.Count - 1);
            var visible = Visible;
            if (_index < _scroll) _scroll = _index;
            else if (_index >= _scroll + visible) _scroll = _index - visible + 1;
            _canvas.InvalidateSurface();
        }

        private void Close(int? result)
        {
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }

    // ── Screen 2: one game's cards, grouped by kind, repeats collapsed ──────────

    /// <summary>
    /// One game's ways to get the species, painted in the app's art direction
    /// (<see cref="EncounterPaint"/>): the Pokémon at the left, every way as a row at the
    /// right. ↑↓ pick a way, A catches it (after a confirm), B goes back. A tap picks a row;
    /// a second tap on the picked row asks to catch it.
    /// </summary>
    private sealed class CardShelf : IPadHandler
    {
        private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly GameEncounterListing _listing;
        private readonly SaveBadges? _progress;
        private readonly IReadOnlyList<CardGroup> _groups;
        private readonly List<int> _ordered;
        private readonly bool _canCatch;
        private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
        private readonly ISpriteService _sprites;
        private readonly int _species;
        private readonly int _form;
        private readonly Dictionary<string, SKImage?> _icons = new(StringComparer.Ordinal);
        private SKImage? _sprite;
        private EncounterHits? _hits;
        private int _index;
        private int _scroll;
        private bool _busy;
        private SKPoint? _pressedAt;
        private bool _dragged;
        private float _dragRows;

        /// <summary>One displayed card: the representative encounter plus every source
        /// card index it stands for, so CATCH still places exactly what was shown.</summary>
        private sealed record CardGroup(EncounterCard Card, IReadOnlyList<int> Indices, bool SameMethod);

        /// <param name="progress">The open save's badges: cards beyond them are marked "later",
        /// an estimate (<see cref="EncounterReach"/>). Null for another game's cards.</param>
        public static Task<int?> ShowAsync(Grid host, GameEncounterListing listing, ISpriteService sprites, int species, int form, bool canCatch, string back,
            SaveBadges? progress = null) =>
            new CardShelf(host, listing, sprites, species, form, canCatch, back, progress)._result.Task;

        private CardShelf(Grid host, GameEncounterListing listing, ISpriteService sprites, int species, int form, bool canCatch, string back,
            SaveBadges? progress)
        {
            _host = host;
            _progress = progress;
            _listing = listing;
            _groups = Collapse(listing.Cards);
            _ordered = [.. _groups.Select((group, index) => (group, index))
                .OrderBy(x => EncounterDatabaseRank(x.group.Card.Kind))
                .ThenBy(x => x.group.Card.LevelMin)
                .ThenBy(x => x.group.Card.Location, StringComparer.Ordinal)
                .Select(x => x.index)];
            _canCatch = canCatch;
            _sprites = sprites;
            _species = species;
            _form = form;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            _canvas.PaintSurface += PaintGuard.Wrap("EncounterGallery.Paint", Paint);
            _canvas.Touch += Touch;
            var footer = DsChrome.Footer(
                ("A", canCatch ? "Catch" : "Choose", () => OnPadButton(PadButton.A)),
                ("B", back, () => OnPadButton(PadButton.B)));
            var screen = new Grid
            {
                Padding = new Thickness(14, 12, 14, 0),
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { _canvas, footer },
            };
            Grid.SetRow(footer, 1);
            _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
            _router?.Push(this);
        }

        /// <summary>
        /// Collapses repeated sightings: one card per (kind, place) with the full level
        /// span. Scarlet/Violet list the same area once per spawn band, which turned a
        /// single species into 250 cards; this keeps the wall readable while remembering
        /// the underlying indices so CATCH places the first real entry of the group.
        /// </summary>
        private static List<CardGroup> Collapse(IReadOnlyList<EncounterCard> cards)
        {
            var groups = new List<CardGroup>();
            var byKey = new Dictionary<(string Kind, string Location), (int Index, List<int> Indices)>();
            foreach (var (card, index) in cards.Select((c, i) => (c, i)))
            {
                var key = (card.Kind, card.Location);
                if (byKey.TryGetValue(key, out var found))
                {
                    found.Indices.Add(index);
                    var merged = found.Index >= 0 ? groups[found.Index] : null;
                    if (merged is not null)
                    {
                        groups[found.Index] = merged with
                        {
                            Card = merged.Card with
                            {
                                LevelMin = Math.Min(merged.Card.LevelMin, card.LevelMin),
                                LevelMax = Math.Max(merged.Card.LevelMax, card.LevelMax),
                                ShinyGuaranteed = merged.Card.ShinyGuaranteed || card.ShinyGuaranteed,
                                ShinyLocked = merged.Card.ShinyLocked && card.ShinyLocked,
                            },
                            Indices = found.Indices,
                            SameMethod = merged.SameMethod && merged.Card.Detail == card.Detail,
                        };
                    }
                    continue;
                }
                var list = new List<int> { index };
                byKey[key] = (groups.Count, list);
                groups.Add(new CardGroup(card, list, SameMethod: true));
            }
            return groups;
        }

        private static int EncounterDatabaseRank(string kind) => kind switch
        {
            "Egg" => 0, "Wild" => 1, "Static" => 2, "Trade" => 3, "Event" => 4, _ => 5,
        };

        private SKImage? Sprite() => _sprite ??= TrimmedSprite(_sprites, _species, _form, _canvas);

        private SKImage? Icon(string name) => ToolIcon(_icons, name);

        private void Paint(object? sender, SKPaintSurfaceEventArgs args)
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            var rows = _ordered.Select(i =>
            {
                var group = _groups[i];
                var card = group.Card;
                var levels = card.LevelMin == card.LevelMax ? $"Lv. {card.LevelMin}" : $"Lv. {card.LevelMin}-{card.LevelMax}";
                if (EncounterReach.Judge(card, _progress).Reach == EncounterReachKind.Later) levels += " · later?";
                var place = string.IsNullOrEmpty(card.Location) ? EncounterPaint.KindTitle(card.Kind) : card.Location;
                return new EncounterRow(card.Kind, place, levels, group.SameMethod ? MethodTag(card.Detail) : "",
                    group.Indices.Count, card.ShinyGuaranteed, card.Detail);
            }).ToList();
            var names = IPlatformApplication.Current?.Services.GetService<IGameDataService>()?.SpeciesNames;
            var species = names is not null && (uint)_species < (uint)names.Count ? names[_species] : $"#{_species}";
            var scene = new EncounterScene(species, Sprite(), _listing.GameName, rows, _index, _scroll, Icon);
            _hits = EncounterPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
        }

        /// <summary>LongName ("Wild Encounter (Pearl) Walking") minus the shared "(Game)" part.</summary>
        private static string MethodTag(string detail)
        {
            var paren = detail.IndexOf('(');
            if (paren < 0) return string.Empty;
            var close = detail.IndexOf(')', paren);
            if (close < 0 || close + 1 >= detail.Length) return string.Empty;
            return detail[(close + 1)..].Trim();
        }

        private void Touch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (_hits is not { } hits) return;
            var at = args.Location;
            switch (args.ActionType)
            {
                case SKTouchAction.Pressed:
                    _pressedAt = at;
                    _dragged = false;
                    _dragRows = 0;
                    return;
                case SKTouchAction.Moved when _pressedAt is { } start && hits.List.Contains(start):
                    if (!_dragged && Math.Abs(start.Y - at.Y) < hits.RowHeight / 3) return;
                    _dragged = true;
                    var step = (int)((start.Y - at.Y) / hits.RowHeight - _dragRows);
                    if (step == 0) return;
                    _dragRows += step;
                    _scroll = Math.Clamp(_scroll + step, 0, Math.Max(0, _ordered.Count - hits.VisibleRows));
                    _canvas.InvalidateSurface();
                    return;
                case SKTouchAction.Released:
                    var tapped = !_dragged;
                    _pressedAt = null;
                    if (!tapped) return;
                    foreach (var (index, rect) in hits.Rows)
                    {
                        if (!rect.Contains(at)) continue;
                        // A tap picks the way; a second tap on it asks to catch it.
                        if (index == _index) ConfirmCatch();
                        else Move(index);
                        return;
                    }
                    return;
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Move(_index - 1); return true;
                case PadButton.Down: Move(_index + 1); return true;
                case PadButton.L: Move(_index - Visible); return true;
                case PadButton.R: Move(_index + Visible); return true;
                case PadButton.A: ConfirmCatch(); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private int Visible => EncounterPaint.VisibleRows(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));

        private void Move(int index)
        {
            if (_ordered.Count == 0) return;
            _index = Math.Clamp(index, 0, _ordered.Count - 1);
            var visible = Visible;
            if (_index < _scroll) _scroll = _index;
            else if (_index >= _scroll + visible) _scroll = _index - visible + 1;
            _canvas.InvalidateSurface();
        }

        private async void ConfirmCatch()
        {
            if (_busy || _groups.Count == 0) return;
            _busy = true;
            try
            {
                var group = _groups[_ordered[Math.Clamp(_index, 0, _ordered.Count - 1)]];
                var card = group.Card;
                var where = string.IsNullOrEmpty(card.Location) ? card.Kind.ToLowerInvariant() : card.Location;
                if (!_canCatch)
                {
                    await PadMenu.ShowAsync(_host, "Not your game",
                        $"This is how {_listing.GameName} gives out this Pokémon. Open that save to catch one into it.", "OK");
                    return;
                }
                var reach = EncounterReach.Judge(card, _progress);
                var later = reach.Reach == EncounterReachKind.Later
                    ? $"\nProbably not reachable yet: {reach.Note}. Judged from the badges only."
                    : "";
                var confirmed = await PadMenu.ConfirmAsync(_host, "Catch this encounter?",
                    $"A legal Pokémon from {_listing.GameName} ({card.Kind.ToLowerInvariant()}, {where}) is placed in this box's first empty slot. One backed-up write.{later}", "Catch");
                if (confirmed) Close(group.Indices[0]);
            }
            catch (Exception error)
            {
                // async void: a failed question is logged, the gallery stays open.
                AppLog.Error("encounters", "The catch could not be confirmed", error);
            }
            finally
            {
                _busy = false;
            }
        }

        private void Close(int? result)
        {
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }
}
