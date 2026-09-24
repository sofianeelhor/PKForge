using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

file static class DpScale
{
    public static float Value => (float)DeviceDisplay.MainDisplayInfo.Density;
}

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
            var pick = await CardShelf.ShowAsync(host, listing, sprites, species, form, canCatch: true);
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
            var pick = await CardShelf.ShowAsync(host, listing, sprites, species, form, canCatch);
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

    /// <summary>Both screens live on the same night wallpaper so the flow feels like one place.</summary>
    private static SKCanvasView Backdrop()
    {
        var canvasView = new SKCanvasView { InputTransparent = true };
        canvasView.PaintSurface += (_, args) =>
            PksmPaint.Wallpaper(args.Surface.Canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), Pksm.Housing);
        return canvasView;
    }

    // ── Screen 1: the games, grouped by generation ──────────────────────────────

    private sealed class GameShelf : IPadHandler
    {
        private const float Pad = 10f, Gap = 6f, RowH = 44f, HeaderH = 22f;
        private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly IReadOnlyList<GameEncounterListing> _listings;
        private readonly IReadOnlyList<int> _order;      // display order over _listings
        private readonly IReadOnlyList<int> _catchable;
        private readonly SKCanvasView _wall;
        private int _index;
        private int _scrollRow;
        private int _rowsPerScreen = 4;

        public static Task<int?> ShowAsync(Grid host, IReadOnlyList<GameEncounterListing> listings, ISpriteService sprites, int species, int form, IReadOnlyList<int> catchable) =>
            new GameShelf(host, listings, sprites, species, form, catchable)._result.Task;

        /// <summary>Rows the open save can actually catch into, by listing index.</summary>
        private bool IsCurrent(int display) => _catchable.Contains(_order[Math.Clamp(display, 0, _order.Count - 1)]);

        private GameShelf(Grid host, IReadOnlyList<GameEncounterListing> listings, ISpriteService sprites, int species, int form, IReadOnlyList<int> catchable)
        {
            _host = host;
            _listings = listings;
            _catchable = catchable;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            // A curated order: the generation the player is in first when a save is open,
            // then oldest to newest, so "where can I get this?" reads chronologically.
            var order = Enumerable.Range(0, listings.Count).ToList();
            order.Sort((a, b) =>
            {
                var byGen = listings[a].Generation.CompareTo(listings[b].Generation);
                return byGen != 0 ? byGen : string.CompareOrdinal(listings[a].GameName, listings[b].GameName);
            });
            // Rows the save owns come first: its own answer is what the player acts on.
            var owned = order.Where(i => catchable.Contains(i)).ToList();
            foreach (var index in owned)
            {
                order.Remove(index);
                order.Insert(0, index);
            }
            _order = order;

            var maxW = host.Width > 0 ? host.Width - 24 : 616;
            var maxH = host.Height > 0 ? host.Height - 16 : 344;
            _wall = new SKCanvasView { EnableTouchEvents = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            _wall.PaintSurface += Paint;
            _wall.Touch += Touch;

            var hero = new SKCanvasView { WidthRequest = 64, HeightRequest = 46, HorizontalOptions = LayoutOptions.Start };
            hero.PaintSurface += (_, args) =>
            {
                var bitmap = sprites.GetSprite(species, form, false);
                if (bitmap is null) sprites.Warm(species, form, false, () => MainThread.BeginInvokeOnMainThread(hero.InvalidateSurface));
                EventGallery.PaintMon(args.Surface.Canvas, args.Info, bitmap, Math.Min(args.Info.Width, args.Info.Height) * 0.95f);
            };
            var heroRow = new Grid { ColumnSpacing = 10, ColumnDefinitions = [new(new GridLength(64)), new(GridLength.Star)] };
            heroRow.Children.Add(hero);
            var header = Kit.HeaderBar($"How to get · {listings.Count(l => l.Obtainable)} of {listings.Count} games");
            heroRow.Children.Add(header);
            Grid.SetColumn(header, 1);

            var content = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children =
                {
                    heroRow,
                    _wall,
                    Kit.WindowHints(("A", "WAYS", null), ("B", "BACK", () => Close(null))),
                },
            };
            content.SetRow(_wall, 1);
            content.SetRow((View)content.Children[2], 2);

            var window = new Border
            {
                BackgroundColor = UiTokens.Shell,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Shadow = Kit.HardShadow(),
                Padding = 14,
                Content = new Grid { Children = { Backdrop(), content } },
            };
            window.MaximumWidthRequest = maxW;
            window.MaximumHeightRequest = maxH;
            window.HorizontalOptions = LayoutOptions.Center;
            window.VerticalOptions = LayoutOptions.Center;

            var scrim = new BoxView { Color = UiTokens.Scrim };
            var scrimTap = new TapGestureRecognizer();
            scrimTap.Tapped += (_, _) => Close(null);
            scrim.GestureRecognizers.Add(scrimTap);
            _overlay = new Grid { Children = { scrim, window } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

            _router?.Push(this);
            Kit.AnimateIn(window);
        }

        /// <summary>Row height per display slot; generation headers take a header row.</summary>
        private List<(bool IsHeader, string Text, int Display, int Generation)> BuildRows()
        {
            var rows = new List<(bool, string, int, int)>();
            var generation = -1;
            for (var i = 0; i < _order.Count; i++)
            {
                var listing = _listings[_order[i]];
                if (listing.Generation != generation)
                {
                    generation = listing.Generation;
                    rows.Add((true, $"Generation {Roman(generation)}", -1, generation));
                }
                rows.Add((false, listing.GameName, i, generation));
            }
            return rows;
        }

        private static string Roman(int gen) => gen switch
        {
            1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => $"{gen}",
        };

        private void Paint(object? sender, SKPaintSurfaceEventArgs args)
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            // Laid out in dp (row heights, card widths): scale the pixel canvas to match.
            var density = (float)DeviceDisplay.MainDisplayInfo.Density;
            canvas.Scale(density);
            var info = new SKImageInfo((int)(args.Info.Width / density), (int)(args.Info.Height / density));
            _rowsPerScreen = Math.Max(2, (int)((info.Height - 8 + Gap) / (RowH + Gap)));

            using var headerFont = new SKFont(PixelFont.Face, 14f) { Edging = SKFontEdging.Antialias };
            using var nameFont = new SKFont(PixelFont.Face, 15f) { Edging = SKFontEdging.Antialias };
            using var detailFont = new SKFont(PixelFont.Face, 12.5f) { Edging = SKFontEdging.Antialias };
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var inkSoft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };

            var rows = BuildRows();
            // Scroll by whole rows so the selected game is always on screen; the
            // generation headers ride along with the first visible entry.
            var position = rows.FindIndex(r => !r.IsHeader && r.Display == _index);
            var firstRow = Math.Max(0, position < 0 ? 0 : position - (_rowsPerScreen - 1));
            var y = 4f;
            for (var i = firstRow; i < rows.Count; i++)
            {
                var row = rows[i];
                var height = row.IsHeader ? HeaderH : RowH;
                if (y > info.Height) break;
                var rect = new SKRect(Pad, y, info.Width - Pad, y + height);
                if (row.IsHeader)
                {
                    PksmPaint.HeaderStrip(canvas, rect, row.Text, headerFont);
                }
                else
                {
                    var listing = _listings[_order[row.Display]];
                    var selected = row.Display == _index;
                    PksmPaint.Panel(canvas, rect);
                    using var dim = new SKPaint { Color = Pksm.Ink.WithAlpha(0x60), IsAntialias = true };
                    canvas.DrawText(EventGallery.Fit(nameFont, listing.GameName, rect.Width * 0.5f), rect.Left + 12, rect.MidY + 6, SKTextAlign.Left, nameFont, listing.Obtainable ? ink : dim);
                    var summary = listing.Obtainable ? Summarize(listing) : "Not obtainable here";
                    canvas.DrawText(EventGallery.Fit(detailFont, summary, rect.Width * 0.48f), rect.Right - 12, rect.MidY + 6, SKTextAlign.Right, detailFont, listing.Obtainable ? inkSoft : dim);
                    if (IsCurrent(row.Display))
                        canvas.DrawText("This game", rect.Left + 12, rect.Top + 15, SKTextAlign.Left, detailFont, inkSoft);
                    if (selected) PksmPaint.Selection(canvas, rect);
                }
                y += height + Gap;
            }
        }

        /// <summary>"3 ways · Wild, Egg, Events" - the honest one-line answer per game.</summary>
        private static string Summarize(GameEncounterListing listing)
        {
            var kinds = listing.Cards.Select(c => c.Kind).Distinct().ToList();
            var parts = kinds.Select(KindLabel);
            return $"{listing.Cards.Count} {(listing.Cards.Count == 1 ? "way" : "ways")} · {string.Join(", ", parts)}";
        }

        private static string KindLabel(string kind) => kind switch
        {
            "Egg" => "Eggs",
            "Wild" => "Wild",
            "Static" => "Static",
            "Trade" => "Trades",
            "Event" => "Events",
            _ => "Special",
        };

        private void Touch(object? sender, SKTouchEventArgs args)
        {
            if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
            if (args.ActionType != SKTouchAction.Released) return;
            args.Handled = true;
            var rows = BuildRows();
            var y = 4f;
            foreach (var row in rows)
            {
                var height = row.IsHeader ? HeaderH : RowH;
                if (!row.IsHeader && args.Location.Y / DpScale.Value >= y && args.Location.Y / DpScale.Value <= y + height)
                {
                    _index = row.Display;
                    _wall.InvalidateSurface();
                    Close(_order[row.Display]); // display position -> listing index
                    return;
                }
                y += height + Gap;
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Move(_index - 1); return true;
                case PadButton.Down: Move(_index + 1); return true;
                case PadButton.A: Close(_order[Math.Clamp(_index, 0, _order.Count - 1)]); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private void Move(int index)
        {
            _index = Math.Clamp(index, 0, Math.Max(0, _order.Count - 1));
            var rows = BuildRows();
            var position = rows.FindIndex(r => !r.IsHeader && r.Display == _index);
            var firstVisible = rows.FindIndex(r => !r.IsHeader && r.Display == _scrollRow);
            if (firstVisible < 0) firstVisible = 0;
            if (position < firstVisible)
                _scrollRow = _index;
            _wall.InvalidateSurface();
        }

        private void Close(int? result)
        {
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }
    }

    // ── Screen 2: one game's cards, grouped by kind, repeats collapsed ──────────

    private sealed class CardShelf : IPadHandler
    {
        private const float Pad = 10f, Gap = 8f, HeaderH = 22f, HeaderGap = 6f, MinCardW = 270f;
        private readonly float _cardW;
        private readonly float _cardH;

        private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _host;
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly GameEncounterListing _listing;
        private readonly IReadOnlyList<CardGroup> _groups;
        private readonly bool _canCatch;
        private readonly SKCanvasView _wall;
        private int _index;
        private int _scrollRow;
        private int _cols = 2;
        private bool _busy;

        /// <summary>One displayed card: the representative encounter plus every source
        /// card index it stands for, so CATCH still places exactly what was shown.</summary>
        private sealed record CardGroup(EncounterCard Card, IReadOnlyList<int> Indices, bool SameMethod);

        private readonly record struct LayoutRow(bool IsHeader, string? Header, int FirstCard, int CardCount);

        public static Task<int?> ShowAsync(Grid host, GameEncounterListing listing, ISpriteService sprites, int species, int form, bool canCatch) =>
            new CardShelf(host, listing, sprites, species, form, canCatch)._result.Task;

        private CardShelf(Grid host, GameEncounterListing listing, ISpriteService sprites, int species, int form, bool canCatch)
        {
            _host = host;
            _listing = listing;
            _groups = Collapse(listing.Cards);
            _canCatch = canCatch;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

            var maxW = host.Width > 0 ? host.Width - 24 : 616;
            var maxH = host.Height > 0 ? host.Height - 16 : 344;
            var wallW = maxW - 28;
            _cols = Math.Max(2, (int)((wallW - Pad * 2 + Gap) / (MinCardW + Gap)));
            _cardW = (float)((wallW - Pad * 2 - (_cols - 1) * Gap) / _cols);
            var bodyH = maxH - 28 - 46 - 10 - 34;
            _cardH = (float)Math.Clamp((bodyH - HeaderH - HeaderGap - Gap) / 2, 92, 118);

            _wall = new SKCanvasView { EnableTouchEvents = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            _wall.PaintSurface += Paint;
            _wall.Touch += Touch;

            var hero = new SKCanvasView { WidthRequest = 64, HeightRequest = 46, HorizontalOptions = LayoutOptions.Start };
            hero.PaintSurface += (_, args) =>
            {
                var bitmap = sprites.GetSprite(species, form, false);
                if (bitmap is null) sprites.Warm(species, form, false, () => MainThread.BeginInvokeOnMainThread(hero.InvalidateSurface));
                EventGallery.PaintMon(args.Surface.Canvas, args.Info, bitmap, Math.Min(args.Info.Width, args.Info.Height) * 0.95f);
            };
            var heroRow = new Grid { ColumnSpacing = 10, ColumnDefinitions = [new(new GridLength(64)), new(GridLength.Star)] };
            heroRow.Children.Add(hero);
            var header = Kit.HeaderBar($"{listing.GameName} · {listing.Cards.Count} ways");
            heroRow.Children.Add(header);
            Grid.SetColumn(header, 1);

            var content = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                Children =
                {
                    heroRow,
                    _wall,
                    Kit.WindowHints(("A", canCatch ? "Catch" : "INFO", null), ("B", "Games", () => Close(null))),
                },
            };
            content.SetRow(_wall, 1);
            content.SetRow((View)content.Children[2], 2);

            var window = new Border
            {
                BackgroundColor = UiTokens.Shell,
                Stroke = UiTokens.ShellEdge,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Shadow = Kit.HardShadow(),
                Padding = 14,
                Content = new Grid { Children = { Backdrop(), content } },
            };
            window.MaximumWidthRequest = maxW;
            window.MaximumHeightRequest = maxH;
            window.HorizontalOptions = LayoutOptions.Center;
            window.VerticalOptions = LayoutOptions.Center;

            var scrim = new BoxView { Color = UiTokens.Scrim };
            var scrimTap = new TapGestureRecognizer();
            scrimTap.Tapped += (_, _) => Close(null);
            scrim.GestureRecognizers.Add(scrimTap);
            _overlay = new Grid { Children = { scrim, window } };
            host.Add(_overlay);
            Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

            _router?.Push(this);
            Kit.AnimateIn(window);
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

        private static string KindHeader(string kind) => kind switch
        {
            "Egg" => "EGGS",
            "Wild" => "WILD",
            "Static" => "Static",
            "Trade" => "In-game trades",
            "Event" => "Events",
            _ => "Special",
        };

        private List<LayoutRow> BuildLayout()
        {
            var rows = new List<LayoutRow>();
            var ordered = _groups
                .Select((group, index) => (group, index))
                .OrderBy(x => EncounterDatabaseRank(x.group.Card.Kind))
                .ThenBy(x => x.group.Card.LevelMin)
                .ThenBy(x => x.group.Card.Location, StringComparer.Ordinal)
                .ToList();
            _ordered.Clear();
            _ordered.AddRange(ordered.Select(x => x.index));
            var i = 0;
            while (i < _ordered.Count)
            {
                var kind = _groups[_ordered[i]].Card.Kind;
                var count = 0;
                while (i + count < _ordered.Count && _groups[_ordered[i + count]].Card.Kind == kind) count++;
                rows.Add(new LayoutRow(true, $"{KindHeader(kind)} · {count}", i, count));
                for (var c = 0; c < count; c += _cols)
                    rows.Add(new LayoutRow(false, null, i + c, Math.Min(_cols, count - c)));
                i += count;
            }
            return rows;
        }

        private static int EncounterDatabaseRank(string kind) => kind switch
        {
            "Egg" => 0, "Wild" => 1, "Static" => 2, "Trade" => 3, "Event" => 4, _ => 5,
        };

        private readonly List<int> _ordered = [];

        private void Paint(object? sender, SKPaintSurfaceEventArgs args)
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            // Laid out in dp (row heights, card widths): scale the pixel canvas to match.
            var density = (float)DeviceDisplay.MainDisplayInfo.Density;
            canvas.Scale(density);
            var info = new SKImageInfo((int)(args.Info.Width / density), (int)(args.Info.Height / density));

            using var headerFont = new SKFont(PixelFont.Face, 14f) { Edging = SKFontEdging.Antialias };
            using var nameFont = new SKFont(PixelFont.Face, 15f) { Edging = SKFontEdging.Antialias };
            using var lvFont = new SKFont(PixelFont.Face, 12.5f) { Edging = SKFontEdging.Antialias };
            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            using var inkSoft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };

            var y = 4f;
            var cardRow = 0;
            foreach (var row in BuildLayout())
            {
                if (y > info.Height) break;
                if (row.IsHeader)
                {
                    if (cardRow >= _scrollRow && y > -HeaderH)
                    {
                        var strip = new SKRect(Pad, y, info.Width - Pad, y + HeaderH);
                        PksmPaint.HeaderStrip(canvas, strip, row.Header!, headerFont);
                    }
                    y += HeaderH + HeaderGap;
                    continue;
                }
                if (cardRow >= _scrollRow)
                {
                    for (var c = 0; c < row.CardCount; c++)
                    {
                        var slot = row.FirstCard + c;
                        var group = _groups[_ordered[slot]];
                        var card = group.Card;
                        var x = Pad + c * (_cardW + Gap);
                        var rect = new SKRect(x, y, x + _cardW, y + _cardH);
                        PksmPaint.Panel(canvas, rect);

                        var line1 = string.IsNullOrEmpty(card.Location) ? card.Kind : card.Location;
                        canvas.DrawText(EventGallery.Fit(nameFont, line1, _cardW - 14), x + _cardW / 2, y + _cardH - 38, SKTextAlign.Center, nameFont, ink);
                        var levels = card.LevelMin == card.LevelMax ? $"Lv. {card.LevelMin}" : $"Lv. {card.LevelMin}-{card.LevelMax}";
                        var method = group.SameMethod ? MethodTag(card.Detail) : string.Empty;
                        var line2 = string.IsNullOrEmpty(method) ? levels : $"{levels} · {method}";
                        canvas.DrawText(EventGallery.Fit(lvFont, line2, _cardW - 14), x + _cardW / 2, y + _cardH - 14, SKTextAlign.Center, lvFont, inkSoft);

                        // Repeats carry a count so it is honest that one card stands for many.
                        if (group.Indices.Count > 1)
                            canvas.DrawText($"{group.Indices.Count} spots", x + _cardW / 2, y + 18, SKTextAlign.Center, lvFont, inkSoft);

                        if (card.ShinyGuaranteed)
                            PksmPaint.Sparkle(canvas, new SKPoint(x + _cardW - 14, y + 14), 6f);

                        if (slot == _index)
                            PksmPaint.Selection(canvas, rect);
                    }
                }
                cardRow++;
                y += _cardH + Gap;
            }
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
            if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
            if (args.ActionType != SKTouchAction.Released) return;
            args.Handled = true;

            var index = HitTest(args.Location.X / DpScale.Value, args.Location.Y / DpScale.Value);
            if (index < 0) return;
            _index = index;
            EnsureVisible();
            _wall.InvalidateSurface();
            ConfirmCatch();
        }

        private int HitTest(float px, float py)
        {
            if (_wall.Width <= 0) return -1;
            var y = 4f;
            var cardRow = 0;
            foreach (var row in BuildLayout())
            {
                if (row.IsHeader) { y += HeaderH + HeaderGap; continue; }
                if (cardRow >= _scrollRow && py >= y && py <= y + _cardH)
                {
                    var col = (int)((px - Pad) / (_cardW + Gap));
                    return col >= 0 && col < row.CardCount ? row.FirstCard + col : -1;
                }
                cardRow++;
                y += _cardH + Gap;
                if (y > py) break;
            }
            return -1;
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Left: Move(_index - 1); return true;
                case PadButton.Right: Move(_index + 1); return true;
                case PadButton.Up: Move(_index - _cols); return true;
                case PadButton.Down: Move(_index + _cols); return true;
                case PadButton.A: ConfirmCatch(); return true;
                case PadButton.B: Close(null); return true;
                default: return true;
            }
        }

        private void Move(int index)
        {
            _index = Math.Clamp(index, 0, Math.Max(0, _groups.Count - 1));
            EnsureVisible();
            _wall.InvalidateSurface();
        }

        private void EnsureVisible()
        {
            var target = CardRowOf(_index);
            if (target < _scrollRow)
            {
                _scrollRow = target;
                return;
            }
            var height = _wall.Height > 0 ? (float)_wall.Height : 300f;
            var y = 4f;
            var cardRow = 0;
            foreach (var row in BuildLayout())
            {
                if (row.IsHeader) { y += HeaderH + HeaderGap; continue; }
                if (cardRow == target)
                {
                    if (y + _cardH > height) _scrollRow = Math.Max(0, target - 1);
                    return;
                }
                y += _cardH + Gap;
                cardRow++;
            }
        }

        private int CardRowOf(int slot)
        {
            var seen = 0;
            foreach (var row in BuildLayout())
            {
                if (row.IsHeader) continue;
                if (slot < row.FirstCard + row.CardCount) return seen;
                seen++;
            }
            return Math.Max(0, seen - 1);
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
                var confirmed = await PadMenu.ConfirmAsync(_host, "Catch this encounter?",
                    $"A legal Pokémon from {_listing.GameName} ({card.Kind.ToLowerInvariant()}, {where}) is placed in this box's first empty slot. One backed-up write.", "Catch");
                if (confirmed) Close(group.Indices[0]);
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
