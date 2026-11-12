using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

/// <summary>One restore point as the list shows it: what it undoes first, then when and where.</summary>
public sealed record RestorePointRow(BackupInfo Info, string Title, string Detail, RestorePointKind Kind)
{
    /// <summary>The word on the row's right: the kind of change, or that the player keeps this point.</summary>
    public string Tag => Info.Pinned ? "Pinned" : RestorePoints.Label(Kind);
    public bool Pinned => Info.Pinned;
}

/// <summary>A filter the list offers, with how many points it would show.</summary>
public sealed record RestorePointFilter(RestorePointKind? Kind, string Label, int Count, bool PinnedOnly = false);

public partial class BackupHistoryViewModel : ObservableObject
{
    private readonly IBackupService _backups;
    private readonly ISaveSessionService _sessions;
    private readonly ISafeSaveWriter _writer;
    private readonly BoxBrowserViewModel _boxBrowser;
    private readonly ISaveEngine _engine;
    private readonly IBankService _bank;

    public BackupHistoryViewModel(IBackupService backups, ISaveSessionService sessions, ISafeSaveWriter writer, BoxBrowserViewModel boxBrowser,
        ISaveEngine engine, IBankService bank)
    {
        _engine = engine;
        _bank = bank;
        _backups = backups;
        _sessions = sessions;
        _writer = writer;
        _boxBrowser = boxBrowser;
    }

    private readonly List<RestorePointRow> _all = [];

    /// <summary>The restore points the current filter lets through, newest first.</summary>
    public ObservableCollection<RestorePointRow> Backups { get; } = [];

    /// <summary>All, Pinned, then each kind that has at least one point.</summary>
    public IReadOnlyList<RestorePointFilter> Filters { get; private set; } = [];

    /// <summary>Position of the active filter in <see cref="Filters"/>.</summary>
    public int FilterIndex { get; private set; }

    /// <summary>Raised when <see cref="Filters"/> or the active one changed, for the strip of chips.</summary>
    public event Action? FiltersChanged;

    /// <summary>Raised with what a restore or a pin just did, to be said as a notice.</summary>
    public event Action<string>? ActionReported;

    private void Report(string outcome)
    {
        Status = outcome;
        ActionReported?.Invoke(outcome);
    }

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool CanRestore => _sessions.Current is not null;

    [RelayCommand]
    private Task LoadAsync() => ReloadAsync(keepStatus: false);

    /// <summary>Reads the list again; <paramref name="keepStatus"/> leaves the last action's report on screen.</summary>
    private async Task ReloadAsync(bool keepStatus)
    {
        var active = FilterIndex < Filters.Count ? Filters[FilterIndex] : null;
        _all.Clear();
        // With a save open, only its own restore points are listed: restoring writes into it.
        var open = _sessions.Current;
        var now = DateTimeOffset.Now;
        foreach (var info in await _backups.ListAsync())
        {
            if (open is not null && !info.BelongsTo(open.Document.DocumentId, open.Snapshot)) continue;
            var title = string.IsNullOrWhiteSpace(info.ChangeDescription) ? "Restore point" : info.ChangeDescription!;
            var detail = $"{RestorePoints.When(info.CreatedUtc, now)} · {info.DisplayName ?? info.Format}";
            _all.Add(new RestorePointRow(info, title, detail, RestorePoints.KindOf(info.ChangeDescription)));
        }

        var filters = new List<RestorePointFilter> { new(null, "All", _all.Count) };
        var pinned = _all.Count(row => row.Pinned);
        if (pinned > 0) filters.Add(new RestorePointFilter(null, "Pinned", pinned, PinnedOnly: true));
        filters.AddRange(_all.GroupBy(row => row.Kind).OrderByDescending(group => group.Count())
            .Select(group => new RestorePointFilter(group.Key, RestorePoints.Label(group.Key), group.Count())));
        Filters = filters;
        // The filter in use stays in use when it still has something to show.
        FilterIndex = active is null ? 0
            : Math.Max(0, filters.FindIndex(f => f.Kind == active.Kind && f.PinnedOnly == active.PinnedOnly));
        ApplyFilter();
        if (!keepStatus) Status = Summary();
    }

    private string Summary()
    {
        if (_all.Count == 0) return "No restore points yet. One is made automatically before every write.";
        var scope = CanRestore ? "of this save" : "in all. Open a save to restore one";
        var filter = Filters[FilterIndex];
        var shown = filter.Kind is null && !filter.PinnedOnly ? "" : $" Showing {filter.Label}: {Backups.Count}.";
        return $"{_all.Count} restore {(_all.Count == 1 ? "point" : "points")} {scope}.{shown}";
    }

    private void ApplyFilter()
    {
        var filter = Filters[FilterIndex];
        Backups.Clear();
        foreach (var row in _all)
            if (filter.PinnedOnly ? row.Pinned : filter.Kind is null || row.Kind == filter.Kind)
                Backups.Add(row);
        FiltersChanged?.Invoke();
    }

    /// <summary>Shows only one kind of restore point (or all of them again).</summary>
    public void SelectFilter(int index)
    {
        if (Filters.Count == 0) return;
        FilterIndex = ((index % Filters.Count) + Filters.Count) % Filters.Count;
        ApplyFilter();
        Status = Summary();
    }

    /// <summary>Pins a point so it is never dropped to make room, or lets it go again.</summary>
    public async Task TogglePinAsync(RestorePointRow row)
    {
        try
        {
            await _backups.SetPinnedAsync(row.Info.BackupId, !row.Pinned);
            await ReloadAsync(keepStatus: true);
            Report(row.Pinned
                ? "Unpinned."
                : "Pinned: kept until you unpin it.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report($"Could not pin this restore point. {Domain.ErrorText.Plain(error)}");
        }
    }

    /// <summary>
    /// Hardcore mode's duplication check before a restore: the Pokémon the restore point
    /// holds that the open save no longer does (moved to the Bank, sent to another game or
    /// released since). Null when Hardcore is off, no save is open, or the restore point
    /// cannot be read - the caller then falls back to the generic warning.
    /// </summary>
    public async Task<RestoreResurrection?> CheckResurrectionAsync(BackupInfo backup)
    {
        if (!HardcoreMode.IsOn || _sessions.Current is not { } session) return null;
        try
        {
            var bytes = await _backups.ReadAsync(backup.BackupId);
            var then = await Task.Run(() => _engine.Open(bytes).Slots);
            var bank = _bank.GetAll().Select(entry => entry.Info).ToList();
            return RestoreResurrection.Detect(then, session.Snapshot.Slots, bank);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the backup's bytes into the open document; the current state is itself backed up first.</summary>
    public async Task RestoreAsync(BackupInfo backup)
    {
        var session = _sessions.Current;
        if (session is null)
        {
            Status = "Open the save first, then restore it here.";
            return;
        }

        if (!backup.BelongsTo(session.Document.DocumentId, session.Snapshot))
        {
            Status = "Not restored: this point belongs to another save.";
            return;
        }

        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Restoring…";
            var bytes = await _backups.ReadAsync(backup.BackupId);
            // A restore is the recovery path: a deliberate whole-file write, never slot-scoped.
            var receipt = await _writer.WriteScopedAsync(session.Document.DocumentId, session.Snapshot, bytes,
                WriteScope.Everything,
                "Safety copy: the state right before this restore");
            if (receipt.Changed)
                _sessions.MarkWritten(session.Document.DocumentId, bytes);
            await _sessions.OpenAsync(session.Document);
            _boxBrowser.RefreshFromCurrentSession();
            await ReloadAsync(keepStatus: true);
            Report(receipt.Changed
                ? "Restored."
                : "No changes: the save already matches this point.");
        }
        catch (Exception error)
        {
            Report($"Not restored. {Domain.ErrorText.Plain(error)}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
