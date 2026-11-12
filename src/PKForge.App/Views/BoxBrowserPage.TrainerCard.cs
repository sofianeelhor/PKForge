using System.Globalization;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

public sealed partial class BoxBrowserPage
{
    /// <summary>
    /// The trainer card (<see cref="TrainerCardPaint"/>): name, sex, IDs, money, play time and
    /// every currency the game keeps, on one card. ↑↓ pick a line, A changes it (one backed-up
    /// write each), B closes. Read only in Hardcore mode.
    /// </summary>
    private async Task ShowTrainerCardScreenAsync(ISaveEngineSession session)
    {
        var readOnly = Guard.Blocks(SaveAction.EditTrainer);
        var screen = new TrainerCardScreen(this, session, readOnly);
        await screen.RunAsync();
    }

    /// <summary>One line of the card and how to change it.</summary>
    private sealed record CardLine(string Label, string Value, string Hint, Func<Task>? Edit);

    private sealed class TrainerCardScreen : IPadHandler
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly BoxBrowserPage _page;
        private readonly ISaveEngineSession _session;
        private readonly bool _readOnly;
        private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _overlay;
        private List<CardLine> _lines = [];
        private TrainerCardHits? _hits;
        private int _cursor;
        private int _scroll;
        private bool _busy;

        public TrainerCardScreen(BoxBrowserPage page, ISaveEngineSession session, bool readOnly)
        {
            _page = page;
            _session = session;
            _readOnly = readOnly;
            _canvas.PaintSurface += PaintGuard.Wrap("The trainer card", OnPaint);
            _canvas.Touch += OnTouch;
            var footer = DsChrome.Footer(
                ("A", readOnly ? "Read" : "Change", () => OnPadButton(PadButton.A)),
                ("B", "Close", () => OnPadButton(PadButton.B)));
            var screen = new Grid
            {
                Padding = new Thickness(14, 12, 14, 0),
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { _canvas, footer },
            };
            Grid.SetRow(footer, 1);
            _overlay = Kit.AttachOverlay(page._hostGrid, new Grid { Children = { DsChrome.GridBackground(), screen } });
        }

        public async Task RunAsync()
        {
            try { Reload(); }
            catch (Exception error)
            {
                // A save the engine cannot read the card of: say so, rather than an empty card.
                AppLog.Error("trainer", "The trainer card could not be read", error);
                _page._viewModel.Status = $"The trainer card could not be read. {Domain.ErrorText.Plain(error)}";
                _page._hostGrid.Remove(_overlay);
                return;
            }
            var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            router?.Push(this);
            try { await _closed.Task; }
            finally
            {
                router?.Remove(this);
                _page._hostGrid.Remove(_overlay);
            }
        }

        /// <summary>Reads the card again from the save (after every change). Painting draws only
        /// what this kept: it never reads the session, which a refused write replaces.</summary>
        private void Reload()
        {
            var trainer = _session.GetTrainer();
            var stats = _session.GetTrainerStats();
            _trainer = trainer;
            _card = _page.SaveDataCardOf(_session);
            _partyLooks = [.. _session.Snapshot.Slots.Where(slot => slot.Box == -1 && slot.Species is not null).OrderBy(slot => slot.Slot).Select(slot => slot.Look)];
            _bigParty = null;
            var lines = new List<CardLine>
            {
                new("Name", trainer.Name, "A types a new name.", () => EditNameAsync(trainer)),
                new("Trainer", trainer.Gender == 1 ? "♀ Girl" : "♂ Boy", "A switches between boy and girl.",
                    () => WriteTrainerAsync(trainer with { Gender = trainer.Gender == 1 ? 0 : 1 }, "Trainer switched.")),
                new("ID No.", trainer.TID.ToString("00000", Inv), "A types a new ID, 0 to 65535.",
                    () => EditNumberAsync("ID No.", trainer.TID, 65535, v => WriteTrainerAsync(trainer with { TID = v }, $"ID No. set to {v:00000}."))),
                new("Secret ID", trainer.SID.ToString("00000", Inv), "A types a new secret ID, 0 to 65535. It decides which Pokémon are shiny for this trainer.",
                    () => EditNumberAsync("Secret ID", trainer.SID, 65535, v => WriteTrainerAsync(trainer with { SID = v }, $"Secret ID set to {v:00000}."))),
                new("Money", trainer.Money.ToString("N0", Inv), "A types an amount, up to 9,999,999.",
                    () => EditNumberAsync("Money", (int)Math.Min(trainer.Money, int.MaxValue), 9_999_999, v => WriteTrainerAsync(trainer with { Money = (uint)v }, $"Money set to {v.ToString("N0", Inv)}."))),
            };
            if (stats.SupportsPlayTime)
                lines.Add(new("Play time", $"{stats.PlayTimeHours}:{stats.PlayTimeMinutes:00}", "A types hours and minutes, like 12:30.", () => EditPlayTimeAsync(stats)));
            // The card's extras (badges, rival, dates) are read from PKHeX's own save; a ROM hack's
            // session has none of that, and its card keeps the lines above.
            _details = _session is Engine.SaveEngineSession ? Engine.TrainerCardService.Get(_session) : null;
            if (_details is { } details)
            {
                if (details.SupportsBadges && details.Badges.Count > 0)
                {
                    var earned = details.Badges.Where(b => b.Obtained).Select(b => b.Name).ToList();
                    lines.Add(new("Badges", $"{details.BadgeCount} of {details.Badges.Count}",
                        earned.Count == 0 ? "No badge yet. A picks which ones the trainer has." : $"{string.Join(", ", earned)}. A picks which ones the trainer has.",
                        () => EditBadgesAsync(details)));
                }
                if (details.SupportsRivalName)
                    lines.Add(new("Rival", details.RivalName ?? "", "A types the rival's name.", () => EditRivalAsync(details)));
                foreach (var (label, field) in new[] { ("Adventure started", details.StartDate), ("Hall of Fame", details.HallOfFameDate), ("Last saved", details.LastSaved) })
                    if (field.Supported && field.Value is { } when)
                        lines.Add(new(label, when.ToString(field.HasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", Inv), "As the game keeps it.", null));
            }
            foreach (var currency in CurrenciesOf(stats))
                lines.Add(new(currency.Name, currency.Value.ToString("N0", Inv), $"A types an amount, up to {currency.Max.ToString("N0", Inv)}.",
                    () => EditNumberAsync(currency.Name, currency.Value, currency.Max, v => WriteStatsAsync(currency.Edit(v), $"{currency.Name} set to {v.ToString("N0", Inv)}."))));
            _lines = lines;
            _cursor = Math.Clamp(_cursor, 0, _lines.Count - 1);
            _canvas.InvalidateSurface();
        }

        private TrainerCardDetails? _details;
        private TrainerInfo? _trainer;
        private SaveDataCard? _card;
        private List<SpriteLook> _partyLooks = [];
        private List<(SKImage, SKRect)>? _bigParty;

        /// <summary>The badge case as a list: each badge on or off, all, or none.</summary>
        private async Task EditBadgesAsync(TrainerCardDetails details)
        {
            var owned = details.Badges.Select(b => b.Obtained).ToArray();
            while (true)
            {
                var options = new List<PadOption>
                {
                    new("All badges", IconPath: "check", Detail: "Every badge of the game."),
                    new("No badges", IconPath: "clear"),
                };
                options.AddRange(details.Badges.Select((b, i) => new PadOption($"{(owned[i] ? "✓" : "·")} {b.Name}", IconPath: "ribbons", Detail: b.Region)));
                var choice = await PadMenu.ShowAsync(_page._hostGrid, "Badges", $"{owned.Count(o => o)} of {owned.Length}. Pick one to give or take it.", options.ToArray());
                if (choice is null) return;
                if (choice == "All badges") Array.Fill(owned, true);
                else if (choice == "No badges") Array.Fill(owned, false);
                else
                {
                    var index = options.FindIndex(o => o.Label == choice) - 2;
                    if (index < 0) return;
                    owned[index] = !owned[index];
                }
                // A refused write closed the card: the badge list goes with it.
                if (!await MutateAsync(s => Engine.TrainerCardService.Apply(s, new TrainerCardEdit(Badges: owned)), $"Badges: {owned.Count(o => o)} of {owned.Length}."))
                    return;
            }
        }

        private async Task EditRivalAsync(TrainerCardDetails details)
        {
            var typed = await TextPopup.ShowLineAsync(_page._hostGrid, "Rival", $"Up to {details.RivalNameMaxLength} characters", details.RivalName ?? "", confirm: "Set");
            if (string.IsNullOrWhiteSpace(typed) || typed.Trim() == details.RivalName) return;
            var name = typed.Trim();
            if (name.Length > details.RivalNameMaxLength) name = name[..details.RivalNameMaxLength];
            await MutateAsync(s => Engine.TrainerCardService.Apply(s, new TrainerCardEdit(RivalName: name)), $"Rival named {name}.");
        }

        private async Task EditNameAsync(TrainerInfo trainer)
        {
            var typed = await TextPopup.ShowLineAsync(_page._hostGrid, "Name", "The trainer's name", trainer.Name, confirm: "Set");
            if (string.IsNullOrWhiteSpace(typed) || typed == trainer.Name) return;
            await WriteTrainerAsync(trainer with { Name = typed.Trim() }, $"Name set to {typed.Trim()}.");
        }

        private async Task EditNumberAsync(string label, int current, int max, Func<int, Task> write)
        {
            var typed = await TextPopup.ShowLineAsync(_page._hostGrid, label, $"0 to {max.ToString("N0", Inv)}", current.ToString(Inv), confirm: "Set", numeric: true);
            if (typed is null) return;
            if (!long.TryParse(typed.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, Inv, out var number))
            {
                _page._viewModel.Status = $"{label}: \"{typed.Trim()}\" is not a number.";
                return;
            }
            var value = (int)Math.Clamp(number, 0, max);
            if (value != current) await write(value);
        }

        private async Task EditPlayTimeAsync(TrainerStats stats)
        {
            var current = $"{stats.PlayTimeHours}:{stats.PlayTimeMinutes:00}";
            var typed = await TextPopup.ShowLineAsync(_page._hostGrid, "Play time", $"Hours:minutes, hours up to {stats.PlayTimeHoursMax}", current, confirm: "Set");
            if (typed is null || typed.Trim() == current) return;
            var parts = typed.Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, Inv, out var hours) || !int.TryParse(parts[1], NumberStyles.Integer, Inv, out var minutes))
            {
                _page._viewModel.Status = $"Play time: \"{typed.Trim()}\" is not hours:minutes.";
                return;
            }
            hours = Math.Clamp(hours, 0, stats.PlayTimeHoursMax);
            minutes = Math.Clamp(minutes, 0, 59);
            await WriteStatsAsync(new TrainerStatsEdit(PlayTimeHours: hours, PlayTimeMinutes: minutes), $"Play time set to {hours}:{minutes:00}.");
        }

        private Task WriteTrainerAsync(TrainerInfo updated, string done) => MutateAsync(s => s.SetTrainer(updated), done);

        private Task WriteStatsAsync(TrainerStatsEdit edit, string done) => MutateAsync(s => s.SetTrainerStats(edit), done);

        /// <summary>One backed-up write, then a fresh read; false when it did not happen (the card closed).</summary>
        private async Task<bool> MutateAsync(Action<ISaveEngineSession> change, string done)
        {
            var saved = await _page._viewModel.RunMutationAsync(s =>
            {
                change(s);
                return new GenerationOutcome(true, done);
            }, Math.Max(0, _page._viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditTrainer);
            if (!saved)
            {
                // As the Day Care does: a refused or failed write reopened the save from its
                // last written bytes, so this card's session is gone. The reason is in the status.
                _closed.TrySetResult();
                return false;
            }
            Reload();
            return true;
        }

        /// <summary>The party as full sprites, trimmed, so they stand large on the card; empty until
        /// they load. Kept once complete, so a repaint does not trim them again.</summary>
        private List<(SKImage, SKRect)> BigParty()
        {
            if (_bigParty is not null) return _bigParty;
            var party = new List<(SKImage, SKRect)>();
            foreach (var look in _partyLooks)
            {
                if (_page._sprites.GetSprite(look) is not { } bitmap)
                {
                    _page._sprites.Warm(look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
                    return [];
                }
                var r = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
                party.Add((SpriteImages.Of(bitmap), new SKRect(r.Left, r.Top, r.Right, r.Bottom)));
            }
            return _bigParty = party;
        }

        private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            if (_trainer is not { } trainer || _card is not { } card) return;
            var scene = new TrainerCardScene(card.Game, trainer.Name, trainer.Gender, $"ID No. {trainer.TID:00000}",
                [.. _lines.Select(l => new TrainerCardLine(l.Label, l.Value, l.Edit is not null, l.Hint))],
                _cursor, _scroll, BigParty() is { Count: > 0 } big ? big : card.Party, _readOnly,
                _details is { SupportsBadges: true } d ? [.. d.Badges.Select(b => b.Obtained)] : null,
                _details is { SupportsBadges: true } k ? [.. k.Badges.Select(b => TrainerCardPaint.BadgeKey(b.Region, b.Name))] : null);
            _hits = TrainerCardPaint.Paint(args.Surface.Canvas, args.Info, scene, PixelTypeface());
        }

        private int Visible => TrainerCardPaint.VisibleLines(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));

        private void Move(int index)
        {
            if (_lines.Count == 0) return;
            _cursor = Math.Clamp(index, 0, _lines.Count - 1);
            var visible = Visible;
            if (_cursor < _scroll) _scroll = _cursor;
            else if (_cursor >= _scroll + visible) _scroll = _cursor - visible + 1;
            _canvas.InvalidateSurface();
        }

        private async void Change()
        {
            if (_busy || _cursor >= _lines.Count || _lines[_cursor].Edit is not { } edit) return;
            if (_readOnly) { _page.Denied(SaveAction.EditTrainer); return; }
            _busy = true;
            try { await edit(); }
            catch (Exception error)
            {
                // async void: an escaping exception would close the app.
                AppLog.Error("trainer", "A trainer card change failed", error);
                _page._viewModel.Status = $"Not changed. {Domain.ErrorText.Plain(error)}";
                _closed.TrySetResult();
            }
            finally { _busy = false; }
        }

        private void OnTouch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
            foreach (var (index, rect) in hits.Lines)
            {
                if (!rect.Contains(args.Location)) continue;
                // A tap picks the line; a second tap on it changes it.
                if (index == _cursor) Change();
                else Move(index);
                return;
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Move(_cursor - 1); return true;
                case PadButton.Down: Move(_cursor + 1); return true;
                case PadButton.A: Change(); return true;
                case PadButton.B: _closed.TrySetResult(); return true;
                default: return true; // modal while open
            }
        }
    }
}
