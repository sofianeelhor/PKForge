using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

public partial class BoxBrowserViewModel : ObservableObject, IBoxPager
{
    private readonly IDocumentPicker _picker;
    private readonly ISaveSessionService _sessions;
    private readonly ILegalityService _legality;
    private readonly ISafeSaveWriter _writer;
    private readonly Theme.ThemeService _theme;
    private readonly ILegalizerService? _legalizer;

    private SlotSummary[] _slots = [];

    public BoxBrowserViewModel(IDocumentPicker picker, ISaveSessionService sessions, ILegalityService legality, ISafeSaveWriter writer, Theme.ThemeService theme, ILegalizerService? legalizer = null)
    {
        _picker = picker;
        _sessions = sessions;
        _legality = legality;
        _writer = writer;
        _theme = theme;
        _legalizer = legalizer;
    }

    [ObservableProperty] private SaveSnapshot? _save;
    [ObservableProperty] private int _boxIndex;
    [ObservableProperty] private int _selectedSlot = -1;
    [ObservableProperty] private string _status = "NO STORAGE CONNECTED - PRESS ACCESS STORAGE";
    [ObservableProperty] private bool _isBusy;

    // Structured connection facts for the blueprint chips (never merged into one status blob).
    [ObservableProperty] private string _connectedName = "";
    [ObservableProperty] private int _connectedGeneration;
    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private EntityDetail? _selected;
    [ObservableProperty] private string _legalityBadge = string.Empty;
    [ObservableProperty] private string _legalityText = string.Empty;

    // Editor fields (strings for binding; parsed on save).
    [ObservableProperty] private string _editNickname = string.Empty;
    [ObservableProperty] private string _editLevel = string.Empty;
    [ObservableProperty] private string _editSpecies = string.Empty;
    [ObservableProperty] private string _editNature = string.Empty;
    [ObservableProperty] private string _editAbility = string.Empty;
    [ObservableProperty] private string _editHeldItem = string.Empty;
    [ObservableProperty] private string _editMove1 = string.Empty;
    [ObservableProperty] private string _editMove2 = string.Empty;
    [ObservableProperty] private string _editMove3 = string.Empty;
    [ObservableProperty] private string _editMove4 = string.Empty;
    [ObservableProperty] private string _editIvs = string.Empty;
    [ObservableProperty] private string _editStats = string.Empty;
    [ObservableProperty] private string _editEvs = string.Empty;
    [ObservableProperty] private string _editBall = string.Empty;
    [ObservableProperty] private string _editOt = string.Empty;
    [ObservableProperty] private bool _editShiny;

    public IReadOnlyList<SlotSummary> VisibleSlots =>
        BoxIndex == -1
            ? Enumerable.Range(0, 6).Select(i =>
                _slots.FirstOrDefault(x => x.Box == -1 && x.Slot == i) is { Box: -1 } found
                    ? found
                    : new SlotSummary(-1, i, null, null, false, true)).ToArray()
            : _slots.Where(x => x.Box == BoxIndex).OrderBy(x => x.Slot).ToArray();

    public int BoxCount => _slots.Length == 0 ? 0 : _slots.Max(x => x.Box) + 1;

    /// <summary>LCD readout above the grid; the party rides before box 1.</summary>
    public string BoxLabel => Save is null ? "NO DATA" : BoxIndex == -1 ? "PARTY" : $"{BoxIndex + 1:00} / {BoxCount:00}";

    partial void OnBoxIndexChanged(int value) => OnPropertyChanged(nameof(BoxLabel));
    partial void OnSaveChanged(SaveSnapshot? value) => OnPropertyChanged(nameof(BoxLabel));

    [RelayCommand]
    private async Task OpenSaveAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Choose a save file…";
            var document = await _picker.PickSaveAsync();
            if (document is null) { Status = "Open cancelled."; return; }
            var session = await _sessions.OpenAsync(document);
            _slots = session.Snapshot.Slots.ToArray();
            Save = session.Snapshot;
            BoxIndex = 0;
            SelectedSlot = -1;
            Selected = null;
            OnPropertyChanged(nameof(VisibleSlots));
            OnPropertyChanged(nameof(BoxCount));
            OnPropertyChanged(nameof(BoxLabel));
            ConnectedName = document.DisplayName;
            ConnectedGeneration = Save.Generation;
            IsConnected = true;
            Status = "READY";
        }
        catch (Exception error)
        {
            Status = $"Could not open save: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectSlot(int slot)
    {
        if (Save is null) return;
        var engineSession = _sessions.CurrentSession;
        if (engineSession is null) return;
        var count = BoxIndex == -1 ? 6 : _slots.Count(x => x.Box == BoxIndex);
        if (slot < 0 || slot >= count) return;

        SelectedSlot = slot;
        var detail = engineSession.ReadEntity(BoxIndex, slot);
        Selected = detail;
        _theme.ApplyTypes(detail.Types);
        if (detail.IsEmpty)
        {
            LegalityBadge = string.Empty;
            LegalityText = "Empty slot.";
            return;
        }

        EditNickname = detail.Nickname;
        EditLevel = detail.Level.ToString();
        EditSpecies = detail.Species.ToString();
        EditNature = detail.Nature.ToString();
        EditAbility = detail.Ability.ToString();
        EditHeldItem = detail.HeldItem.ToString();
        EditMove1 = detail.Move1.ToString();
        EditMove2 = detail.Move2.ToString();
        EditMove3 = detail.Move3.ToString();
        EditMove4 = detail.Move4.ToString();
        EditIvs = string.Join(' ', detail.IVs);
        EditEvs = string.Join(' ', detail.EVs);
        EditStats = detail.Stats is { Count: 6 } ? string.Join(' ', detail.Stats) : string.Empty;
        EditBall = detail.Ball.ToString();
        EditOt = detail.OriginalTrainer;
        EditShiny = detail.IsShiny;

        // No transient "analyzing" flash: stay blank until the verdict is ready.
        LegalityBadge = string.Empty;
        LegalityText = string.Empty;
        Task.Run(() =>
        {
            var report = _legality.Analyze(engineSession, BoxIndex, slot);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (SelectedSlot != slot) return;
                LegalityBadge = report.Valid ? "✓" : "✗";
                LegalityText = string.Join('\n', report.Lines);
            });
        });
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (IsBusy) return;
        var engineSession = _sessions.CurrentSession;
        var session = _sessions.Current;
        var detail = Selected;
        if (engineSession is null || session is null || detail is null || detail.IsEmpty)
        {
            Status = "Nothing to save.";
            return;
        }

        try
        {
            IsBusy = true;
            var edit = new EntityEdit(
                Species: ParseInt(EditSpecies),
                Nickname: EditNickname.Length == 0 ? null : EditNickname,
                Level: ParseInt(EditLevel),
                Nature: ParseInt(EditNature),
                Ability: ParseInt(EditAbility),
                HeldItem: ParseInt(EditHeldItem),
                Move1: ParseInt(EditMove1),
                Move2: ParseInt(EditMove2),
                Move3: ParseInt(EditMove3),
                Move4: ParseInt(EditMove4),
                IVs: ParseIntList(EditIvs),
                EVs: ParseIntList(EditEvs),
                IsShiny: EditShiny != detail.IsShiny ? EditShiny : null,
                Ball: ParseInt(EditBall),
                OriginalTrainer: EditOt.Length == 0 ? null : EditOt);

            Status = "Applying edit…";
            engineSession.ApplyEdit(detail.Box, detail.Slot, edit);
            var candidate = engineSession.Serialize();

            Status = "Validating, backing up, writing…";
            var receipt = await _writer.WriteAsync(session.Document.DocumentId, session.Snapshot, candidate);

            var updated = engineSession.ReadEntity(detail.Box, detail.Slot);
            var askedAbility = ParseInt(EditAbility);
            if (askedAbility is { } wanted && updated.Ability != wanted)
                Status = $"Ability did not stick: asked for {wanted}, the mon holds {updated.Ability}. This is the diagnostic the developer needs.";
            var index = Array.FindIndex(_slots, x => x.Box == detail.Box && x.Slot == detail.Slot);
            if (index >= 0)
                _slots[index] = _slots[index] with { Species = updated.Species, Nickname = updated.Nickname, IsShiny = updated.IsShiny, Form = updated.Form };
            OnPropertyChanged(nameof(VisibleSlots));
            SelectSlot(detail.Slot);

            Status = $"Saved. Backup {receipt.BackupId[..Math.Min(13, receipt.BackupId.Length)]}… · {receipt.WrittenSha256[..8]}…";
        }
        catch (Exception error)
        {
            Status = $"Write aborted: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-reads the grid from the session service's current save (after a restore or detected-save open).</summary>
    public void RefreshFromCurrentSession()
    {
        var session = _sessions.Current;
        if (session is null) return;
        _slots = session.Snapshot.Slots.ToArray();
        Save = session.Snapshot;
        BoxIndex = Math.Min(BoxIndex, Math.Max(0, BoxCount - 1));
        SelectedSlot = -1;
        Selected = null;
        OnPropertyChanged(nameof(VisibleSlots));
        OnPropertyChanged(nameof(BoxCount));
        OnPropertyChanged(nameof(BoxLabel));
        ConnectedName = session.Document.DisplayName;
        ConnectedGeneration = session.Snapshot.Generation;
        IsConnected = true;
        Status = "READY";
    }

    /// <summary>Runs a legalizer mutation (generate/legalize) then commits it through the safe write path.</summary>
    public Task<bool> RunLegalizerAsync(Func<ILegalizerService, ISaveEngineSession, GenerationOutcome> operation, int slot)
    {
        var legalizer = _legalizer;
        if (legalizer is null)
        {
            Status = "Legalizer unavailable.";
            return Task.FromResult(false);
        }
        return RunMutationAsync(session => operation(legalizer, session), slot);
    }

    /// <summary>Runs any slot mutation then commits it through the safe write path (validate → backup → atomic write).</summary>
    public async Task<bool> RunMutationAsync(Func<ISaveEngineSession, GenerationOutcome> operation, int slot)
    {
        var engineSession = _sessions.CurrentSession;
        var session = _sessions.Current;
        if (engineSession is null || session is null)
        {
            Status = "No save connected.";
            return false;
        }

        try
        {
            IsBusy = true;
            var outcome = await Task.Run(() => operation(engineSession));
            if (!outcome.Success)
            {
                Status = outcome.Message;
                return false;
            }

            var candidate = engineSession.Serialize();
            var receipt = await _writer.WriteAsync(session.Document.DocumentId, session.Snapshot, candidate);

            var updated = engineSession.ReadEntity(BoxIndex, slot);
            var index = Array.FindIndex(_slots, x => x.Box == BoxIndex && x.Slot == slot);
            if (index >= 0)
                _slots[index] = _slots[index] with
                {
                    Species = updated.IsEmpty ? null : updated.Species,
                    Nickname = updated.IsEmpty ? null : updated.Nickname,
                    IsShiny = updated.IsShiny,
                    Form = updated.Form,
                };
            OnPropertyChanged(nameof(VisibleSlots));
            SelectSlot(slot);
            Status = $"{outcome.Message} · backup {receipt.BackupId[..Math.Min(13, receipt.BackupId.Length)]}";
            return true;
        }
        catch (Exception error)
        {
            Status = $"Aborted: {error.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void PreviousBox() => ChangeBox(-1);
    public void NextBox() => ChangeBox(1);

    private const int GridColumns = 6;
    private const int GridRows = 5;

    // ── Organizer: multi-select for bulk operations ──

    [ObservableProperty] private bool _selectMode;
    private readonly HashSet<(int Box, int Slot)> _marked = [];

    public int MarkedCount => _marked.Count;
    public IReadOnlyList<(int Box, int Slot)> MarkedSlots => _marked.OrderBy(m => m.Box).ThenBy(m => m.Slot).ToList();
    public bool IsMarked(int box, int slot) => _marked.Contains((box, slot));

    public void EnterSelectMode()
    {
        SelectMode = true;
        _marked.Clear();
        Status = "ORGANIZER - mark Pokémon with A, then open the menu";
        OnPropertyChanged(nameof(MarkedCount));
    }

    public void ExitSelectMode()
    {
        SelectMode = false;
        _marked.Clear();
        Status = "READY";
        OnPropertyChanged(nameof(MarkedCount));
    }

    /// <summary>Marks/unmarks an occupied slot; false when the slot is empty.</summary>
    public bool ToggleMark(int slot)
    {
        var slots = VisibleSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot].Species is null) return false;
        var key = (BoxIndex, slot);
        if (!_marked.Remove(key)) _marked.Add(key);
        Status = $"ORGANIZER - {_marked.Count} marked";
        OnPropertyChanged(nameof(MarkedCount));
        return true;
    }

    /// <summary>Moves every marked mon into the target box's empty slots. One backup, one write.</summary>
    public Task<bool> BulkMoveAsync(int targetBox) => RunMutationAsync(session =>
    {
        var targets = new Queue<int>();
        foreach (var summary in _slots.Where(x => x.Box == targetBox && x.Species is null).OrderBy(x => x.Slot))
            targets.Enqueue(summary.Slot);

        var moved = 0;
        foreach (var (box, slot) in _marked.OrderBy(m => m.Box).ThenBy(m => m.Slot))
        {
            if (box == targetBox) continue; // already home
            if (targets.Count == 0) break;
            session.MoveSlot(box, slot, targetBox, targets.Dequeue());
            moved++;
        }
        var leftBehind = _marked.Count(m => m.Box != targetBox) - moved;
        return new GenerationOutcome(moved > 0,
            moved == 0 ? "No room in that box." : $"Moved {moved} Pokémon to box {targetBox + 1}." + (leftBehind > 0 ? $" {leftBehind} left (box full)." : ""));
    }, Math.Max(0, SelectedSlot)).ContinueWith(t =>
    {
        RefreshAllSlots();
        ExitSelectMode();
        return t.Result;
    }, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Releases every marked mon. One backup, one write.</summary>
    public Task<bool> BulkReleaseAsync() => BulkReleaseAsync(null);

    /// <summary>Releases only the given slots (or every marked mon when null). One backup, one write.</summary>
    public Task<bool> BulkReleaseAsync(IReadOnlyList<(int Box, int Slot)>? only) => RunMutationAsync(session =>
    {
        var targets = only ?? _marked.Select(m => (m.Box, m.Slot)).ToList();
        foreach (var (box, slot) in targets)
            session.ReleaseSlot(box, slot);
        return new GenerationOutcome(true, $"Released {targets.Count} Pokémon. Bye-bye!");
    }, Math.Max(0, SelectedSlot)).ContinueWith(t =>
    {
        RefreshAllSlots();
        ExitSelectMode();
        return t.Result;
    }, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Exports every marked mon; returns the written file paths for sharing.</summary>
    public IReadOnlyList<string> BulkExport(string directory)
    {
        var session = _sessions.CurrentSession;
        if (session is null) return [];
        var paths = new List<string>();
        foreach (var (box, slot) in _marked.OrderBy(m => m.Box).ThenBy(m => m.Slot))
        {
            var export = session.ExportSlot(box, slot);
            var path = System.IO.Path.Combine(directory, export.FileName);
            File.WriteAllBytes(path, export.Data);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Re-reads every slot of the save into the grid model (after bulk mutations).</summary>
    public void RefreshAllSlots()
    {
        var session = _sessions.CurrentSession;
        if (session is null) return;
        _slots = session.Snapshot.Slots
            .Select(s =>
            {
                var updated = session.ReadEntity(s.Box, s.Slot);
                return s with
                {
                    Species = updated.IsEmpty ? null : updated.Species,
                    Nickname = updated.IsEmpty ? null : updated.Nickname,
                    IsShiny = updated.IsShiny,
                    Form = updated.Form,
                };
            })
            .ToArray();
        OnPropertyChanged(nameof(VisibleSlots));
    }

    /// <summary>Origin of a mon currently being carried by the cursor; null when nothing is held.</summary>
    [ObservableProperty] private (int Box, int Slot)? _carrySource;

    /// <summary>Grid summary of the carried mon (for drawing it on the cursor).</summary>
    public SlotSummary? CarriedSummary { get; private set; }

    /// <summary>Picks up the mon under the cursor. False if the slot is empty or nothing is open.</summary>
    public bool BeginCarry()
    {
        if (Save is null || SelectedSlot < 0) return false;
        var slots = VisibleSlots;
        if (SelectedSlot >= slots.Count || slots[SelectedSlot].Species is null) return false;
        CarriedSummary = slots[SelectedSlot];
        CarrySource = (BoxIndex, SelectedSlot);
        Status = "CARRYING - place with A, cancel with B";
        return true;
    }

    public void CancelCarry()
    {
        CarrySource = null;
        CarriedSummary = null;
        Status = "READY";
    }

    /// <summary>Drops the carried mon on the cursor slot (move or swap), writing safely at once.</summary>
    public async Task DropAsync()
    {
        var engineSession = _sessions.CurrentSession;
        var session = _sessions.Current;
        if (CarrySource is not { } source || engineSession is null || session is null) return;
        var target = (Box: BoxIndex, Slot: SelectedSlot < 0 ? 0 : SelectedSlot);
        CarrySource = null;
        CarriedSummary = null;
        if (source.Box == target.Box && source.Slot == target.Slot)
        {
            Status = "READY";
            return;
        }

        try
        {
            IsBusy = true;
            engineSession.MoveSlot(source.Box, source.Slot, target.Box, target.Slot);
            var candidate = engineSession.Serialize();
            var receipt = await _writer.WriteAsync(session.Document.DocumentId, session.Snapshot, candidate);

            // Refresh both touched slots in the grid model.
            foreach (var (box, slot) in new[] { (source.Box, source.Slot), (target.Box, target.Slot) })
            {
                var updated = engineSession.ReadEntity(box, slot);
                var index = Array.FindIndex(_slots, x => x.Box == box && x.Slot == slot);
                if (index >= 0)
                    _slots[index] = _slots[index] with
                    {
                        Species = updated.IsEmpty ? null : updated.Species,
                        Nickname = updated.IsEmpty ? null : updated.Nickname,
                        IsShiny = updated.IsShiny,
                        Form = updated.Form,
                    };
            }
            OnPropertyChanged(nameof(VisibleSlots));
            SelectSlot(target.Slot);
            Status = $"MOVED · backup {receipt.BackupId[..Math.Min(13, receipt.BackupId.Length)]}";
        }
        catch (Exception error)
        {
            Status = $"Move aborted: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deterministic 6×5 cursor: the selection IS the cursor, so it can never desync.</summary>
    public bool MoveCursor(FocusDirection direction)
    {
        if (Save is null) return false;
        var cols = BoxIndex == -1 ? 2 : GridColumns;
        var rows = BoxIndex == -1 ? 3 : GridRows;
        var slot = SelectedSlot < 0 ? 0 : SelectedSlot;
        var col = slot % cols;
        var row = slot / cols;
        (col, row) = direction switch
        {
            FocusDirection.Left => (Math.Max(0, col - 1), row),
            FocusDirection.Right => (Math.Min(cols - 1, col + 1), row),
            FocusDirection.Up => (col, Math.Max(0, row - 1)),
            FocusDirection.Down => (col, Math.Min(rows - 1, row + 1)),
            _ => (col, row),
        };
        var next = row * cols + col;
        if (next == SelectedSlot) return true; // edge of the grid still consumes the input
        SelectSlot(next);
        return true;
    }

    public void ChangeBox(int delta)
    {
        if (Save is null || BoxCount == 0) return;
        // The party wraps around the boxes: last box, then PARTY, then box 1 again.
        var next = BoxIndex + delta;
        if (next > BoxCount - 1) next = -1;
        else if (next < -1) next = BoxCount - 1;
        BoxIndex = next;
        SelectedSlot = -1;
        Selected = null;
        OnPropertyChanged(nameof(VisibleSlots));
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), out var value) ? value : null;

    private static int[]? ParseIntList(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 6) return null;
        var values = new int[6];
        for (var i = 0; i < 6; i++)
            if (!int.TryParse(parts[i], out values[i])) return null;
        return values;
    }
}
