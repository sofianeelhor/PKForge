using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The evolution card: the Pokémon now and after (sprites either side of the evolution
/// arrow), the requirement in the games' words, and everything that changes - stats, ability,
/// name, the item used up, the trade record, moves it can learn. One card lists every
/// branch (Eevee, Kirlia...) as chips; Left/Right or a tap switches branch, A evolves, B backs out.
/// Nothing is written here: <see cref="RunAsync"/> returns the request and the caller commits
/// it through its own guarded write path.
/// </summary>
public sealed class EvolutionCard : IPadHandler
{
    private readonly TaskCompletionSource<EvolutionOption?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly EvolutionPlan _plan;
    private readonly ISpriteService? _sprites;
    private readonly List<Border> _chips = [];
    private readonly SKCanvasView _stage;
    private readonly Label _toName, _requirement, _status;
    private readonly Border _requirementChip;
    private readonly VerticalStackLayout _changes;
    private readonly Button _evolve;
    private int _index;
    private bool _closed;

    /// <summary>
    /// Plans, shows the card and, when a branch is confirmed, asks about the moves it can learn.
    /// Returns the request to commit, or null when the user backed out or nothing can evolve.
    /// </summary>
    public static async Task<EvolutionRequest?> RunAsync(Grid host, ISaveEngineSession session, int box, int slot)
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
        return new EvolutionRequest(option.Id, moves, hax);
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
        _index = Math.Max(0, plan.Options.ToList().FindIndex(o => o.Available));

        // Branch chips: one per evolution, only when there is a choice to make.
        var chipRow = new HorizontalStackLayout { Spacing = 6 };
        for (var i = 0; i < plan.Options.Count; i++)
        {
            var index = i;
            var chip = Kit.Tab(plan.Options[i].SpeciesName);
            Tap(chip, () => Select(index));
            _chips.Add(chip);
            chipRow.Children.Add(chip);
        }

        _stage = new SKCanvasView { HeightRequest = 118 };
        _stage.PaintSurface += PaintStage;
        if (_sprites is not null)
        {
            void Repaint() => MainThread.BeginInvokeOnMainThread(() => { if (!_closed) _stage.InvalidateSurface(); });
            _sprites.Warm(plan.Species, plan.Form, plan.Shiny, Repaint);
            foreach (var o in plan.Options) _sprites.Warm(o.Species, o.Form, plan.Shiny, Repaint);
        }

        var fromName = new Label
        {
            Text = plan.Nickname.Length > 0 && !string.Equals(plan.Nickname, plan.SpeciesName, StringComparison.OrdinalIgnoreCase)
                ? $"{plan.Nickname} ({plan.SpeciesName})" : plan.SpeciesName,
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextTitle, TextColor = UiTokens.Ink0,
            HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation,
        };
        _toName = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextTitle, TextColor = UiTokens.IndigoInk,
            HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation,
        };
        var names = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(36)), new(GridLength.Star)] };
        names.Add(fromName, 0);
        names.Add(new Label { Text = $"Lv.{plan.Level}", FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center }, 1);
        names.Add(_toName, 2);

        // The requirement reads as coloured text (met = green, trade = blue), not a pill.
        _requirement = new Label { FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextLabel, TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.Center };
        _requirementChip = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(0, 2),
            HorizontalOptions = LayoutOptions.Center,
            Content = _requirement,
        };
        _status = new Label { FontSize = UiTokens.TextSmall, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap };
        _changes = new VerticalStackLayout { Spacing = 6 };

        var cancel = Kit.Capsule("Cancel", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);
        _evolve = Kit.Capsule("Evolve", UiTokens.Green, primary: true);
        _evolve.Clicked += (_, _) => Confirm();
        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, _evolve } };

        var content = new VerticalStackLayout { Spacing = 8 };
        content.Children.Add(Kit.HeaderBar(HaXMode.IsOn ? "Evolution · hax" : "Evolution"));
        if (plan.Options.Count > 1) content.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = chipRow });
        content.Children.Add(_stage);
        content.Children.Add(names);
        content.Children.Add(_requirementChip);
        content.Children.Add(_status);
        content.Children.Add(_changes);
        content.Children.Add(buttons);
        content.Children.Add(Kit.WindowHints(
            ("◀▶", "Branch", null),
            ("A", "Evolve", Confirm),
            ("B", "Cancel", () => Close(null))));

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 560);
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));
        _overlay.Unloaded += (_, _) => Close(null);
        Select(_index);
        _router?.Push(this);
    }

    private EvolutionOption Current => _plan.Options[_index];

    private void Select(int index)
    {
        _index = (index + _plan.Options.Count) % _plan.Options.Count;
        var option = Current;
        for (var i = 0; i < _chips.Count; i++)
            Kit.SetTab(_chips[i], i == _index);

        _toName.Text = option.SpeciesName;
        _requirement.Text = option.Requirement;
        _requirement.TextColor = UiTokens.TextTone(option.IsTrade ? UiTokens.Cyan
            : option.ConditionMet ? UiTokens.Green
            : UiTokens.Ink1);
        (_status.Text, _status.TextColor) = option switch
        {
            { Available: false } => (option.BlockedReason ?? "Not possible right now.", UiTokens.GiftRed),
            { IsTrade: true } => ("No second console needed: PKForge performs the link trade for you.", UiTokens.Ink1),
            { ConditionMet: false } => ("HaX: the game's requirement is not met; evolving anyway.", UiTokens.RedOrange),
            _ => ("The requirement is met.", UiTokens.Green),
        };
        _evolve.IsEnabled = option.Available;
        _evolve.Opacity = option.Available ? 1 : 0.45;

        _changes.Children.Clear();
        _changes.Children.Add(StatGrid(option));
        var lines = new List<(string Caption, string Text, Color Tone)>();
        if (option.AbilityBefore != "—")
            lines.Add(("ABILITY", option.AbilityBefore == option.AbilityAfter ? option.AbilityAfter : $"{option.AbilityBefore} → {option.AbilityAfter}", UiTokens.Ink0));
        lines.Add(("NAME", option.NewNickname is { } nick ? $"Becomes {nick}" : $"Keeps the nickname {_plan.Nickname}", UiTokens.Ink0));
        if (option.ConsumedItem is { } item) lines.Add(("ITEM", $"{item} is used up", UiTokens.RedOrange));
        if (option.HandlerNote is { } handler) lines.Add(("Trade", handler, UiTokens.Ink1));
        if (option.Moves.Count > 0) lines.Add(("Moves", $"Can learn {string.Join(", ", option.Moves.Select(m => m.Name))} (you choose next)", UiTokens.IndigoInk));
        lines.Add(("DEX", $"{option.SpeciesName} is registered as caught", UiTokens.Ink1));
        foreach (var (caption, text, tone) in lines) _changes.Children.Add(Line(caption, text, tone));

        _stage.InvalidateSurface();
    }

    private static View StatGrid(EvolutionOption option)
    {
        var grid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 4,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
        };
        for (var i = 0; i < 6 && i < option.StatsAfter.Count; i++)
        {
            var before = option.StatsBefore[i];
            var after = option.StatsAfter[i];
            var d = after - before;
            var tone = d > 0 ? UiTokens.Green : d < 0 ? UiTokens.RedOrange : UiTokens.Ink0;
            var text = new FormattedString();
            if (d != 0) text.Spans.Add(new Span { Text = $"{before}→", TextColor = UiTokens.InkSoft });
            text.Spans.Add(new Span { Text = $"{after}", TextColor = tone, FontAttributes = FontAttributes.Bold });
            if (d != 0) text.Spans.Add(new Span { Text = d > 0 ? $" ▲{d}" : $" ▼{-d}", TextColor = tone, FontSize = UiTokens.TextSmall });
            var cell = new Border
            {
                BackgroundColor = Colors.Transparent,
                StrokeThickness = 0,
                Padding = new Thickness(2, 1),
                Content = new VerticalStackLayout
                {
                    Spacing = 0,
                    Children =
                    {
                        new Label { FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft, Text = NatureFacts.StatNames[i] },
                        new Label { FontSize = UiTokens.TextBody, LineBreakMode = LineBreakMode.NoWrap, FormattedText = text },
                    },
                },
            };
            grid.Add(cell, i % 3, i / 3);
        }
        return grid;
    }

    private static View Line(string caption, string text, Color tone)
    {
        var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new(new GridLength(64)), new(GridLength.Star)] };
        grid.Add(new Label { Text = caption, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.IndigoInk, VerticalTextAlignment = TextAlignment.Start }, 0);
        grid.Add(new Label { Text = text, FontSize = UiTokens.TextSmall, TextColor = tone, LineBreakMode = LineBreakMode.WordWrap }, 1);
        return grid;
    }

    /// <summary>Two shadow pedestals and the evolution arrow; a soft cobalt light under the destination.</summary>
    private void PaintStage(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        canvas.Clear(SKColors.Transparent);
        var option = Current;
        var left = new SKPoint(w * 0.24f, h * 0.5f);
        var right = new SKPoint(w * 0.76f, h * 0.5f);
        var radius = Math.Min(w * 0.2f, h * 0.46f);

        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = PKForge.Chrome.Pksm.LogoVoid.WithAlpha(0xA0);
        canvas.DrawOval(left.X, h * 0.86f, radius * 0.9f, radius * 0.22f, paint);
        using (var glow = SKShader.CreateRadialGradient(right, radius * 1.1f,
                   [PKForge.Chrome.Pksm.LogoGrid.WithAlpha(option.Available ? (byte)0x90 : (byte)0x40), PKForge.Chrome.Pksm.LogoGrid.WithAlpha(0)],
                   SKShaderTileMode.Clamp))
        {
            paint.Shader = glow;
            canvas.DrawCircle(right, radius * 1.1f, paint);
            paint.Shader = null;
        }
        paint.Color = PKForge.Chrome.Pksm.LogoVoid.WithAlpha(0xA0);
        canvas.DrawOval(right.X, h * 0.86f, radius * 0.9f, radius * 0.22f, paint);

        // The arrow: three chevrons, the evolution beat of the games.
        var accent = option.IsTrade ? PKForge.Chrome.Pksm.LogoCyan : option.Available ? PKForge.Chrome.Pksm.Legal : PKForge.Chrome.Pksm.InkSoft;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, h * 0.035f), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        var cx = w * 0.5f;
        var cy = h * 0.5f;
        var size = h * 0.09f;
        for (var i = 0; i < 3; i++)
        {
            stroke.Color = accent.WithAlpha((byte)(110 + i * 70));
            var x = cx - size * 1.6f + i * size * 1.2f;
            using var path = new SKPath();
            path.MoveTo(x, cy - size);
            path.LineTo(x + size, cy);
            path.LineTo(x, cy + size);
            canvas.DrawPath(path, stroke);
        }

        DrawSprite(canvas, _plan.Species, _plan.Form, left, radius, dim: false);
        DrawSprite(canvas, option.Species, option.Form, right, radius, dim: !option.Available);
    }

    private void DrawSprite(SKCanvas canvas, int species, int form, SKPoint center, float radius, bool dim)
    {
        var bitmap = _sprites?.GetSprite(species, form, _plan.Shiny);
        if (bitmap is null) return;
        var scale = radius * 2 / Math.Max(bitmap.Width, bitmap.Height);
        var dw = bitmap.Width * scale;
        var dh = bitmap.Height * scale;
        using var paint = new SKPaint { IsAntialias = false };
        if (dim) paint.ColorFilter = SKColorFilter.CreateBlendMode(SKColor.Parse("#B0404850"), SKBlendMode.SrcATop);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, SKRect.Create(center.X - dw / 2, center.Y - dh / 2, dw, dh),
            new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }

    private void Confirm()
    {
        if (!Current.Available) return;
        Close(Current);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left or PadButton.Up: Select(_index - 1); break;
            case PadButton.Right or PadButton.Down: Select(_index + 1); break;
            case PadButton.A: Confirm(); break;
            case PadButton.B: Close(null); break;
        }
        return true;
    }

    private static void Tap(View view, Action action)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => action();
        view.GestureRecognizers.Add(tap);
    }

    private void Close(EvolutionOption? option)
    {
        if (_closed) return;
        _closed = true;
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(option);
    }
}
