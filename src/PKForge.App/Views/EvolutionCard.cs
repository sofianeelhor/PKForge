using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>What the player chose on the evolution menu: the request to commit, and the plan and
/// branch it came from (for the evolution scene once the write has landed).</summary>
public sealed record EvolutionChoice(EvolutionRequest Request, EvolutionPlan Plan, EvolutionOption Option)
{
    /// <summary>The line the scene ends on, in the games' words: "Charmeleon evolved into Charizard!".</summary>
    public string Line
    {
        get
        {
            var nicknamed = Plan.Nickname.Length > 0 && !string.Equals(Plan.Nickname, Plan.SpeciesName, StringComparison.OrdinalIgnoreCase);
            return $"{(nicknamed ? Plan.Nickname : Plan.SpeciesName)} evolved into {Option.SpeciesName}!";
        }
    }
}

/// <summary>
/// The evolution menu, painted in the DA by <see cref="EvolutionMenuPaint"/>: the Pokémon now and
/// after on a stage, the requirement and how it stands, and what changes. When it can evolve more
/// than one way (Eevee, Kirlia...) the branches sit on a tab strip: Left/Right (or L/R) or a tap
/// picks one, a second tap on it (or A) evolves, B backs out. Nothing is written here:
/// <see cref="RunAsync"/> returns the choice and the caller commits it through its own guarded
/// write path.
/// </summary>
public sealed class EvolutionCard : IPadHandler
{
    private readonly TaskCompletionSource<EvolutionOption?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly EvolutionPlan _plan;
    private readonly ISpriteService? _sprites;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly FrameInvalidator _frame;
    // Box icons cut from the icon sheet, by branch: ours to dispose.
    private readonly SKImage?[] _icons;
    private EvolutionMenuHits? _hits;
    private int _index;
    private bool _closed;

    /// <summary>
    /// Plans, shows the menu and, when a branch is confirmed, asks about the moves it can learn.
    /// Returns the choice to commit, or null when the user backed out or nothing can evolve.
    /// </summary>
    public static async Task<EvolutionChoice?> RunAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var service = IPlatformApplication.Current?.Services.GetService<IEvolutionService>();
        if (service is null) return null;
        var hax = HaXMode.IsOn;
        var plan = await Task.Run(() => service.Plan(session, box, slot, hax));
        if (plan.Options.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Evolution", plan.Unavailable ?? "It can't evolve.", "OK");
            return null;
        }
        var card = new EvolutionCard(host, plan);
        var option = await card._result.Task;
        if (option is null) return null;
        var moves = await AskMovesAsync(host, plan, option);
        if (moves is null) return null;
        var meet = !option.Available && option.MeetCondition is not null;
        return new EvolutionChoice(new EvolutionRequest(option.Id, moves, hax, MeetCondition: meet), plan, option);
    }

    /// <summary>The games' "trying to learn" dialogue: a free slot just asks; a full moveset
    /// asks which move to forget. Returns null when the user cancels the whole evolution.</summary>
    private static async Task<IReadOnlyList<EvolutionMoveChoice>?> AskMovesAsync(Grid host, EvolutionPlan plan, EvolutionOption option)
    {
        var known = plan.CurrentMoves.ToList();
        var choices = new List<EvolutionMoveChoice>();
        foreach (var offer in option.Moves)
        {
            var why = offer.EvolutionMove ? "an evolution move" : $"learned at Lv.{plan.Level}";
            if (known.Count < 4)
            {
                var pick = await PadMenu.ShowAsync(host, $"Learn {offer.Name}?",
                    $"{option.SpeciesName} can learn {offer.Name} ({why}).",
                    new PadOption($"Learn {offer.Name}", IconPath: "moves"), new PadOption("Don't learn", IconPath: "close"));
                if (pick is null) return null;
                if (pick != "Don't learn")
                {
                    choices.Add(new EvolutionMoveChoice(offer.Move, known.Count));
                    known.Add(offer.Name);
                }
                continue;
            }
            var options = known.Select(m => new PadOption($"Forget {m}", IconPath: "delete")).ToList();
            options.Add(new PadOption($"Don't learn {offer.Name}", IconPath: "close"));
            var forget = await PadMenu.ShowAsync(host, $"Learn {offer.Name}?",
                $"{option.SpeciesName} wants to learn {offer.Name} ({why}), but already knows four moves. Forget one?",
                options.ToArray());
            if (forget is null) return null;
            var slot = known.FindIndex(m => forget == $"Forget {m}");
            if (slot < 0) continue;
            choices.Add(new EvolutionMoveChoice(offer.Move, slot));
            known[slot] = offer.Name;
        }
        return choices;
    }

    private EvolutionCard(Grid host, EvolutionPlan plan)
    {
        _host = host;
        _plan = plan;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _sprites = IPlatformApplication.Current?.Services.GetService<ISpriteService>();
        _index = Math.Max(0, plan.Options.ToList().FindIndex(o => EvolutionMenuPaint.CanGo(o)));
        _icons = new SKImage?[plan.Options.Count];
        _frame = new FrameInvalidator(_canvas);
        _canvas.PaintSurface += PaintGuard.Wrap("EvolutionCard.OnPaint", OnPaint);
        _canvas.Touch += OnTouch;
        _overlay = Kit.AttachOverlay(host, _canvas);
        _overlay.Unloaded += (_, _) => Close(null);
        _router?.Push(this);
    }

    private SpriteLook Look(int species, int form) => SpriteLook.Of(species, form, _plan.Shiny);

    /// <summary>The Gen 5 front as the summary shows it, or PKHeX's art when Showdown has none; null while it loads.</summary>
    private SKImage? Front(int species, int form)
    {
        if (_sprites is null) return null;
        var look = Look(species, form);
        if (!_sprites.TryGetShowdownFront(look, _frame.Request, out var sprite)) return null;
        sprite ??= _sprites.GetSprite(look);
        if (sprite is null)
        {
            _sprites.Warm(look, _frame.Request);
            return null;
        }
        return SpriteImages.Of(sprite);
    }

    private SKImage? Icon(int index)
    {
        if (_icons[index] is { } made || _sprites is null) return _icons[index];
        var option = _plan.Options[index];
        if (!_sprites.TryGetShowdownIcon(Look(option.Species, option.Form), _frame.Request, out var sheet, out var cell) || sheet is null)
            return null;
        return _icons[index] = Kit.SubsetOf(SpriteImages.Of(sheet), cell);
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Transparent);
        if (_closed) return;
        var scene = new EvolutionMenuScene(_plan, _index, Front(_plan.Species, _plan.Form),
            [.. _plan.Options.Select(o => Front(o.Species, o.Form))],
            [.. Enumerable.Range(0, _plan.Options.Count).Select(Icon)],
            HaXMode.IsOn);
        _hits = EvolutionMenuPaint.Paint(args.Surface.Canvas, args.Info, scene, BoxBrowserPage.PixelTypeface());
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var at = args.Location;
        if (hits.Evolve.Contains(at)) { Confirm(); return; }
        if (hits.Cancel.Contains(at) || !hits.Panel.Contains(at)) { Close(null); return; }
        // A tap picks a branch; a second tap on the same one evolves.
        for (var i = 0; i < hits.Branches.Count; i++)
        {
            if (!hits.Branches[i].Contains(at)) continue;
            if (i == _index) Confirm();
            else Select(i);
            return;
        }
    }

    private void Select(int index)
    {
        var count = _plan.Options.Count;
        _index = (index % count + count) % count;
        _canvas.InvalidateSurface();
    }

    private void Confirm()
    {
        var option = _plan.Options[_index];
        if (!EvolutionMenuPaint.CanGo(option)) return;
        Close(option);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left or PadButton.Up or PadButton.L: Select(_index - 1); break;
            case PadButton.Right or PadButton.Down or PadButton.R: Select(_index + 1); break;
            case PadButton.A: Confirm(); break;
            case PadButton.B: Close(null); break;
        }
        return true; // the menu owns the pad while open
    }

    private void Close(EvolutionOption? option)
    {
        if (_closed) return;
        _closed = true;
        _router?.Remove(this);
        _host.Remove(_overlay);
        // The cut icons are ours; whatever may still be drawing them is gone with the overlay.
        _host.Dispatcher.Dispatch(() =>
        {
            foreach (var icon in _icons) icon?.Dispose();
        });
        _result.TrySetResult(option);
    }
}
