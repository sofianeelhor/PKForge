using System.Globalization;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

public sealed partial class BoxBrowserPage
{
    /// <summary>
    /// The Hall of Fame (<see cref="HallOfFamePaint"/>): every inducted team, the one under the
    /// cursor shown large. Y writes the current party in as a new entry, A removes the team
    /// under the cursor, X empties the Hall of Fame, B closes; each write is confirmed and
    /// backed up. Read only in Hardcore mode.
    /// </summary>
    private async Task ShowHallOfFameAsync(ISaveEngineSession session)
    {
        var screen = new HallOfFameScreen(this, session, Guard.Blocks(SaveAction.EditTrainer));
        await screen.RunAsync();
    }

    private sealed class HallOfFameScreen : IPadHandler
    {
        private readonly BoxBrowserPage _page;
        private readonly ISaveEngineSession _session;
        private readonly bool _readOnly;
        private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _overlay;
        private HallOfFameInfo _info;
        private HallOfFameHits? _hits;
        private int _cursor;
        private int _scroll;
        private bool _busy;

        public HallOfFameScreen(BoxBrowserPage page, ISaveEngineSession session, bool readOnly)
        {
            _page = page;
            _session = session;
            _readOnly = readOnly;
            _info = HallOfFameService.Get(session);
            _canvas.PaintSurface += PaintGuard.Wrap("The Hall of Fame", OnPaint);
            _canvas.Touch += OnTouch;
            var caps = _info.Capabilities;
            var hints = new List<(string, string, Action?)>();
            if (!readOnly && caps.CanRecordParty) hints.Add(("Y", "Add the party", () => OnPadButton(PadButton.Y)));
            if (!readOnly && caps.CanDeleteTeams) hints.Add(("A", "Remove team", () => OnPadButton(PadButton.A)));
            if (!readOnly) hints.Add(("X", "Empty it", () => OnPadButton(PadButton.X)));
            hints.Add(("B", "Close", () => OnPadButton(PadButton.B)));
            var footer = DsChrome.Footer([.. hints]);
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
            var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            router?.Push(this);
            try { await _closed.Task; }
            finally
            {
                router?.Remove(this);
                _page._hostGrid.Remove(_overlay);
            }
        }

        private (SKImage, SKRect)? Icon(HallOfFameMember member)
        {
            var look = new SpriteLook(member.Species, member.Form ?? 0, member.IsShiny ?? false);
            if (!_page._sprites.TryGetShowdownIcon(look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface), out var sheet, out var cell)) return null;
            return sheet is null ? null : (SpriteImages.Of(sheet), new SKRect(cell.Left, cell.Top, cell.Right, cell.Bottom));
        }

        private SKImage? Sprite(HallOfFameMember member)
        {
            var look = new SpriteLook(member.Species, member.Form ?? 0, member.IsShiny ?? false);
            if (_page._sprites.GetSprite(look) is { } bitmap) return SpriteImages.Of(bitmap);
            _page._sprites.Warm(look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
            return null;
        }

        private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            var teams = _info.Teams.Select(team => new HallOfFameLine(
                team.Label,
                team.Date is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : team.ClearNumber is { } n ? $"Clear #{n}" : "",
                [.. team.Members.Select(Icon).OfType<(SKImage, SKRect)>()])).ToList();
            var members = _cursor < _info.Teams.Count
                ? _info.Teams[_cursor].Members.Select(m => new HallOfFameCard(
                    m.Nickname is { Length: > 0 } nick ? nick : m.SpeciesName,
                    m.Level is { } level ? $"{m.SpeciesName} · Lv. {level}" : m.SpeciesName,
                    Sprite(m), m.IsShiny ?? false)).ToList()
                : [];
            var hint = _readOnly ? "Hardcore mode: the Hall of Fame can be read, not changed."
                : _info.ClearCount is { } count ? $"Champion {count} time{(count == 1 ? "" : "s")}."
                : "";
            var name = string.IsNullOrEmpty(_page._viewModel.ConnectedName) ? "" : _page._viewModel.ConnectedName;
            var scene = new HallOfFameScene(name, teams, _cursor, _scroll, members, hint,
                "No team has entered the Hall of Fame yet.");
            _hits = HallOfFamePaint.Paint(args.Surface.Canvas, args.Info, scene, PixelTypeface());
        }

        private void Move(int index)
        {
            if (_info.Teams.Count == 0) return;
            _cursor = Math.Clamp(index, 0, _info.Teams.Count - 1);
            var visible = HallOfFamePaint.VisibleTeams(new SKSizeI((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height));
            if (_cursor < _scroll) _scroll = _cursor;
            else if (_cursor >= _scroll + visible) _scroll = _cursor - visible + 1;
            _canvas.InvalidateSurface();
        }

        /// <summary>One confirmed, backed-up change, then a fresh read.</summary>
        private async Task ChangeAsync(string question, string detail, string verb, Func<ISaveEngineSession, HallOfFameInfo> change, string done)
        {
            if (_busy || _readOnly) return;
            _busy = true;
            try
            {
                if (!await PadMenu.ConfirmAsync(_page._hostGrid, question, detail, verb)) return;
                var saved = await _page._viewModel.RunMutationAsync(s =>
                {
                    change(s);
                    return new GenerationOutcome(true, done);
                }, Math.Max(0, _page._viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditTrainer);
                if (!saved)
                {
                    // As the Day Care does: a refused or failed write reopened the save, so this
                    // screen's session is gone. The reason is in the status.
                    _closed.TrySetResult();
                    return;
                }
                _info = HallOfFameService.Get(_session);
                Move(_cursor);
            }
            catch (Exception error)
            {
                AppLog.Error("hall-of-fame", "A Hall of Fame change failed", error);
                _page._viewModel.Status = $"Not changed. {Domain.ErrorText.Plain(error)}";
                _closed.TrySetResult();
            }
            finally { _busy = false; }
        }

        private void OnTouch(object? sender, SKTouchEventArgs args)
        {
            args.Handled = true;
            if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
            foreach (var (index, rect) in hits.Teams)
                if (rect.Contains(args.Location)) { Move(index); return; }
        }

        public bool OnPadButton(PadButton button)
        {
            var caps = _info.Capabilities;
            switch (button)
            {
                case PadButton.Up: Move(_cursor - 1); return true;
                case PadButton.Down: Move(_cursor + 1); return true;
                case PadButton.Y when caps.CanRecordParty:
                    _ = ChangeAsync("Add the party?", "The party as it is now enters the Hall of Fame, dated today. Restore points can undo it.", "Add",
                        s => HallOfFameService.RecordParty(s, DateOnly.FromDateTime(DateTime.Today)), "Party added to the Hall of Fame.");
                    return true;
                case PadButton.A when caps.CanDeleteTeams && _cursor < _info.Teams.Count:
                    var team = _info.Teams[_cursor];
                    _ = ChangeAsync($"Remove {team.Label}?", "This team leaves the Hall of Fame. Restore points can undo it.", "Remove",
                        s => HallOfFameService.DeleteTeam(s, team.Index), $"{team.Label} removed.");
                    return true;
                case PadButton.X when _info.Teams.Count > 0:
                    _ = ChangeAsync("Empty the Hall of Fame?", "Every team leaves it. Restore points can undo it.", "Empty",
                        HallOfFameService.ClearAll, "Hall of Fame emptied.");
                    return true;
                case PadButton.B: _closed.TrySetResult(); return true;
                default: return true; // modal while open
            }
        }
    }
}
