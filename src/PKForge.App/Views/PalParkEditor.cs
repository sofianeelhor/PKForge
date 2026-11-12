using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Gen 4 Pal Park, as one painted screen (<see cref="PalParkPaint"/>): the six Pokémon a GBA
/// migration leaves for the Catching Show, and the transfer lock. Left/right pick a place, A puts
/// a Pokémon in (a copy from the save's boxes or the Bank, converted like a real Gen 3 → 4
/// migration), X clears the place, Y lifts the lock, + fills the empty places from one box.
/// Every write goes through <see cref="BoxBrowserViewModel.RunMutationAsync"/>.
/// </summary>
public sealed class PalParkEditor : IPadHandler
{
    private readonly Grid _host;
    private readonly ISaveEngineSession _session;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService? _sprites;
    private readonly IBankService? _bank;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true };
    private readonly Dictionary<(int, int, bool), SKImage?> _art = [];
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly Grid _overlay;
    private PalParkState _state;
    private PalParkHits? _hits;
    private int _cursor;
    private string _status = "";
    private bool _busy;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!PalParkService.IsSupported(session))
        {
            await PadMenu.ShowAsync(host, "PAL PARK", "Only Diamond, Pearl, Platinum, HeartGold and SoulSilver have a Pal Park.", "OK");
            return;
        }
        await new PalParkEditor(host, session, viewModel).RunAsync();
    }

    private PalParkEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        _host = host;
        _session = session;
        _viewModel = viewModel;
        _state = PalParkService.GetState(session);
        var services = IPlatformApplication.Current?.Services;
        _sprites = services?.GetService<ISpriteService>();
        _bank = services?.GetService<IBankService>();
        _canvas.PaintSurface += PaintGuard.Wrap("The Pal Park", OnPaint);
        _canvas.Touch += OnTouch;
        var footer = DsChrome.Footer(
            ("A", "Put in", () => OnPadButton(PadButton.A)),
            ("X", "Clear", () => OnPadButton(PadButton.X)),
            ("Y", "Lock", () => OnPadButton(PadButton.Y)),
            ("+", "Fill", () => OnPadButton(PadButton.Start)),
            ("B", "Back", () => OnPadButton(PadButton.B)));
        var screen = new Grid
        {
            Padding = new Thickness(14, 12, 14, 0),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { _canvas, footer },
        };
        Grid.SetRow(footer, 1);
        _overlay = Kit.AttachOverlay(host, new Grid { Children = { DsChrome.GridBackground(), screen } });
    }

    private async Task RunAsync()
    {
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        try { await _closed.Task; }
        finally
        {
            router?.Remove(this);
            _host.Remove(_overlay);
        }
    }

    private void Invalidate() => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var slots = _state.Slots.Select(slot => new PalParkSceneSlot(slot.Index, slot.Occupied, slot.Nickname, slot.SpeciesName,
            slot.Level, slot.Origin, slot.Legal, slot.Occupied ? Art(slot) : null)).ToList();
        var scene = new PalParkScene(slots, _cursor, _state.Lock.Armed, _state.Lock.TrainersRecorded,
            HardcoreMode.Guard.Allows(SaveAction.Duplicate), _status);
        _hits = PalParkPaint.Paint(c, args.Info, scene, _face);
    }

    private SKImage? Art(PalParkSlot slot)
    {
        if (_sprites is null || slot.Species == 0) return null;
        var key = ((int)slot.Species, (int)slot.Form, slot.Shiny);
        if (_art.TryGetValue(key, out var known)) return known;
        if (_sprites.GetSprite(slot.Species, slot.Form, slot.Shiny) is not { } bitmap)
        {
            _sprites.Warm(slot.Species, slot.Form, slot.Shiny, Invalidate);
            return null;
        }
        // Cropped to the Pokémon itself, so it stands on the floor line.
        var bounds = StoragePaint.OpaqueBounds(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        using var whole = SKImage.FromBitmap(bitmap);
        var image = bounds.Width > 0 && bounds.Height > 0 ? Kit.SubsetOf(whole, bounds) : SKImage.FromBitmap(bitmap);
        _art[key] = image;
        return image;
    }

    private void MoveCursor(int delta)
    {
        _cursor = Math.Clamp(_cursor + delta, 0, PalParkService.SlotCount - 1);
        _status = "";
        Invalidate();
    }

    private void Say(string status)
    {
        _status = status;
        Invalidate();
    }

    /// <summary>Runs one write through the view model (restore point, Hardcore guard), then re-reads the Pal Park.</summary>
    private async Task<bool> WriteAsync(Func<ISaveEngineSession, GenerationOutcome> write, SaveAction action)
    {
        var message = "";
        var saved = await _viewModel.RunMutationAsync(s =>
        {
            var outcome = write(s);
            message = outcome.Message;
            return outcome;
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: action);
        if (!saved)
        {
            // As the Day Care does: a refused or failed write reopened the save from its last
            // written bytes, so this screen's session is gone. The reason is in the status.
            _closed.TrySetResult();
            return false;
        }
        _state = PalParkService.GetState(_session);
        Say(message);
        return true;
    }

    private async Task Guarded(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        try { await work(); }
        catch (Exception error)
        {
            // Run as a discarded task: without this the failure would vanish and leave the screen dead.
            AppLog.Error("pal-park", "A Pal Park action failed", error);
            _viewModel.Status = $"The Pal Park could not open. {Domain.ErrorText.Plain(error)}";
            _closed.TrySetResult();
        }
        finally { _busy = false; }
    }

    private Task PutInAsync() => Guarded(async () =>
    {
        var index = _cursor;
        var current = _state.Slots[index];
        if (current.Occupied && !await PadMenu.ConfirmAsync(_host, "REPLACE?",
                $"{current.Nickname} is in place {index + 1}. The new Pokémon takes its place.", "Replace"))
            return;
        var source = await PickSourceAsync();
        if (source is null) return;
        var hax = HaXMode.IsOn;
        var preview = PalParkService.Preview(_session, source, hax);
        if (!preview.Allowed)
        {
            await PadMenu.ShowAsync(_host, "CANNOT GO IN", preview.Refusal, "OK");
            return;
        }
        var lines = new List<string>
        {
            $"{preview.SpeciesName} Lv. {preview.Level} goes to place {index + 1} as a copy; the original stays.",
            preview.FromGba ? "Converted like a real migration: met at the Pal Park, origin game kept." : "",
            $"Legality: {preview.Verdict}",
        };
        lines.AddRange(preview.Warnings);
        if (!await PadMenu.ConfirmAsync(_host, preview.FromGba ? "PUT IN THE PAL PARK?" : "HAX: NOT FROM A GBA GAME",
                string.Join("\n", lines.Where(l => l.Length > 0)), "Put in"))
            return;
        await WriteAsync(s =>
        {
            var done = PalParkService.Put(s, index, source, hax);
            return new GenerationOutcome(done.Allowed, done.Allowed
                ? $"{done.SpeciesName} waits in place {index + 1}. {done.Verdict}."
                : done.Refusal ?? "Refused.");
        }, SaveAction.Duplicate);
    });

    private Task ClearAsync() => Guarded(async () =>
    {
        var index = _cursor;
        var slot = _state.Slots[index];
        if (!slot.Occupied) { Say("This place is already empty."); return; }
        if (!await PadMenu.ConfirmAsync(_host, "CLEAR THIS PLACE?",
                $"{slot.Nickname} leaves the Pal Park; the place is left empty as the game leaves it. Restore points can undo it.", "Clear"))
            return;
        await WriteAsync(s =>
        {
            PalParkService.Clear(s, index);
            return new GenerationOutcome(true, $"Place {index + 1} is empty.");
        }, SaveAction.EditMon);
    });

    private Task ClearLockAsync() => Guarded(async () =>
    {
        const string Lock = "Clear the lock";
        const string All = "Clear the lock and the history";
        var choice = await PadMenu.ShowAsync(_host, "TRANSFER LOCK",
            (_state.Lock.Armed
                ? "The game allows one migration a day from each GBA game. Clearing the stored console address skips that check."
                : "There is no lock now: the next migration is allowed at once.") +
            " The history is the list of GBA trainers that migrated already.",
            Lock, All, "Cancel");
        if (choice is not (Lock or All)) return;
        var history = choice == All;
        // Nothing to clear is not a failure: the write finds no change and makes no restore point,
        // and the screen stays (a failed outcome would reopen the save and close it).
        await WriteAsync(s => new GenerationOutcome(true, PalParkService.ClearLock(s, history)
            ? history ? "Lock and history cleared." : "Lock cleared: the next migration is allowed at once."
            : "There was nothing to clear."), SaveAction.EditTrainer);
    });

    private Task FillAsync() => Guarded(async () =>
    {
        var free = _state.Slots.Count(slot => !slot.Occupied);
        if (free == 0) { Say("All six places are full. Clear one first."); return; }
        var sources = await PickBoxAsync();
        if (sources is null) return;
        var hax = HaXMode.IsOn;
        if (!await PadMenu.ConfirmAsync(_host, "FILL THE PAL PARK?",
                $"Copies of the box's Pokémon go into the {free} empty places, in order." +
                (hax ? " HaX mode: Pokémon not from a GBA game go in too, and will not be legal." : " Only Pokémon from a GBA game go in."),
                "Fill"))
            return;
        await WriteAsync(s =>
        {
            var (placed, skipped) = PalParkService.Fill(s, sources, hax);
            var note = skipped.Count > 0 ? $" Skipped: {skipped[0]}" + (skipped.Count > 1 ? $" (+{skipped.Count - 1})" : "") : "";
            // Nothing placed changes nothing: no write, and the screen stays with the reason.
            return new GenerationOutcome(true, placed > 0 ? $"{placed} Pokémon put in.{note}" : $"Nothing could go in.{note}");
        }, SaveAction.Duplicate);
    });

    private async Task<string?> PickOriginAsync(string title)
    {
        const string Save = "The save's boxes";
        const string Bank = "The Bank";
        var hint = HaXMode.IsOn
            ? "HaX mode is on: any Pokémon can go in, but only one from a GBA game is legal."
            : "Only Pokémon from a GBA game (Ruby, Sapphire, Emerald, FireRed, LeafGreen, Colosseum, XD) can go in.";
        return _bank is null
            ? Save
            : await PadMenu.ShowAsync(_host, title, hint, Save, Bank, "Cancel") is { } pick and (Save or Bank) ? pick : null;
    }

    private async Task<PalParkSource?> PickSourceAsync()
    {
        var origin = await PickOriginAsync("PUT IN FROM");
        if (origin is null) return null;
        if (origin == "The Bank" && _bank is not null)
        {
            var entries = _bank.GetAll().Where(e => e.Info.Species is > 0 and <= PalParkService.MaxSpecies)
                .OrderBy(e => e.Info.Generation == 3 ? 0 : 1).ThenBy(e => e.Box).ThenBy(e => e.Slot).ToList();
            if (entries.Count == 0) { Say("The Bank has no Pokémon the Pal Park knows."); return null; }
            var items = entries.Select((e, i) => new PickItem(i, $"{Label(e.Info.Nickname, e.Info.Species)} Lv. {e.Info.Level}",
                Detail: $"Bank box {e.Box + 1}, slot {e.Slot + 1} · {e.Info.SourceName}")
            { Tag = e.Info.Format, Muted = e.Info.Generation != 3 }).ToList();
            var picked = await PickerMenu.ShowAsync(_host, "From the Bank", items);
            if (picked is null) return null;
            var entry = entries[picked.Id];
            return PalParkSource.FromBytes(_bank.GetData(entry.Id), entry.Info.Format);
        }

        var slots = _session.Snapshot.Slots.Where(s => s.Species is > 0 and <= PalParkService.MaxSpecies && !s.IsEgg)
            .OrderBy(s => s.Box).ThenBy(s => s.Slot).ToList();
        if (slots.Count == 0) { Say("The save has no Pokémon the Pal Park knows."); return null; }
        var choices = slots.Select((s, i) => new PickItem(i, Label(s.Nickname, s.Species ?? 0),
            Detail: s.Box < 0 ? $"Party, slot {s.Slot + 1}" : $"Box {s.Box + 1}, slot {s.Slot + 1}")).ToList();
        var chosen = await PickerMenu.ShowAsync(_host, "From the save", choices);
        return chosen is null ? null : PalParkSource.FromSave(slots[chosen.Id].Box, slots[chosen.Id].Slot);
    }

    private async Task<IReadOnlyList<PalParkSource>?> PickBoxAsync()
    {
        var origin = await PickOriginAsync("FILL FROM");
        if (origin is null) return null;
        if (origin == "The Bank" && _bank is not null)
        {
            var boxes = _bank.GetAll().Where(e => e.Info.Species is > 0 and <= PalParkService.MaxSpecies)
                .GroupBy(e => e.Box).OrderBy(g => g.Key).ToList();
            if (boxes.Count == 0) { Say("The Bank has no Pokémon the Pal Park knows."); return null; }
            var items = boxes.Select((g, i) => new PickItem(i, $"Bank box {g.Key + 1}",
                Detail: $"{g.Count()} Pokémon, {g.Count(e => e.Info.Generation == 3)} from a GBA game")).ToList();
            var picked = await PickerMenu.ShowAsync(_host, "Fill from a Bank box", items);
            if (picked is null) return null;
            return [.. boxes[picked.Id].OrderBy(e => e.Slot).Select(e => PalParkSource.FromBytes(_bank.GetData(e.Id), e.Info.Format))];
        }

        var groups = _session.Snapshot.Slots.Where(s => s.Box >= 0 && s.Species is > 0 && !s.IsEgg)
            .GroupBy(s => s.Box).OrderBy(g => g.Key).ToList();
        if (groups.Count == 0) { Say("The save's boxes are empty."); return null; }
        var choices = groups.Select((g, i) => new PickItem(i, $"Box {g.Key + 1}", Detail: $"{g.Count()} Pokémon")).ToList();
        var chosen = await PickerMenu.ShowAsync(_host, "Fill from a box", choices);
        return chosen is null ? null : [.. groups[chosen.Id].OrderBy(s => s.Slot).Select(s => PalParkSource.FromSave(s.Box, s.Slot))];
    }

    private static string Label(string? nickname, int species) =>
        string.IsNullOrWhiteSpace(nickname) ? $"No. {species}" : nickname;

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released || _hits is not { } hits) return;
        var at = args.Location;
        for (var i = 0; i < hits.Actions.Count; i++)
        {
            if (!hits.Actions[i].Contains(at)) continue;
            OnPadButton(i switch { 0 => PadButton.A, 1 => PadButton.X, 2 => PadButton.Y, _ => PadButton.Start });
            return;
        }
        for (var i = 0; i < hits.Slots.Count; i++)
        {
            if (!hits.Slots[i].Contains(at)) continue;
            // A tap picks the place; a second tap puts a Pokémon in.
            if (i == _cursor) _ = PutInAsync();
            else { _cursor = i; _status = ""; Invalidate(); }
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left: MoveCursor(-1); return true;
            case PadButton.Right: MoveCursor(1); return true;
            case PadButton.A: _ = PutInAsync(); return true;
            case PadButton.X: _ = ClearAsync(); return true;
            case PadButton.Y: _ = ClearLockAsync(); return true;
            case PadButton.Start: _ = FillAsync(); return true;
            case PadButton.B: _closed.TrySetResult(); return true;
            default: return true;
        }
    }
}
