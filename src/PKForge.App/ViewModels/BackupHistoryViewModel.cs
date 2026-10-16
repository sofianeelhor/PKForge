using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

public partial class BackupHistoryViewModel : ObservableObject
{
    private readonly IBackupService _backups;
    private readonly ISaveSessionService _sessions;
    private readonly ISafeSaveWriter _writer;
    private readonly BoxBrowserViewModel _boxBrowser;
    private readonly ISaveEngine _engine;
    private readonly IBankService _bank;
    private readonly BankRestorePoints _bankPoints;

    public BackupHistoryViewModel(IBackupService backups, ISaveSessionService sessions, ISafeSaveWriter writer, BoxBrowserViewModel boxBrowser,
        ISaveEngine engine, IBankService bank, BankRestorePoints bankPoints)
    {
        _bankPoints = bankPoints;
        _engine = engine;
        _bank = bank;
        _backups = backups;
        _sessions = sessions;
        _writer = writer;
        _boxBrowser = boxBrowser;
    }

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool CanRestore => _sessions.Current is not null;

    [RelayCommand]
    private async Task LoadAsync()
    {
        Backups.Clear();
        // With a save open, only its own restore points are listed: restoring writes into it.
        var open = _sessions.Current;
        var saves = (await _backups.ListAsync()).Where(info => open is null || info.BelongsTo(open.Document.DocumentId, open.Snapshot));
        // The Bank's own restore points are listed whatever save is open: they restore the Bank, not a save.
        foreach (var info in saves.Concat(_bankPoints.List()).OrderByDescending(info => info.CreatedUtc))
            Backups.Add(info);
        Status = Backups.Count == 0
            ? "No backups yet. One is created automatically before every write."
            : CanRestore
                ? $"{Backups.Count} backup(s) of this save. Restoring writes into it."
                : $"{Backups.Count} backup(s). Open a save to enable restore.";
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

    /// <summary>Whether restoring <paramref name="backup"/> would stop Bank Sync first.</summary>
    public bool RestoreStopsSync(BackupInfo backup) => BankRestorePoints.IsBank(backup) && _bankPoints.SyncIsOn;

    /// <summary>Writes the backup's bytes into the open document; the current state is itself backed up first.</summary>
    public async Task RestoreAsync(BackupInfo backup)
    {
        if (BankRestorePoints.IsBank(backup))
        {
            await RestoreBankAsync(backup);
            return;
        }

        var session = _sessions.Current;
        if (session is null)
        {
            Status = "Open a save first - restore writes into the open save file.";
            return;
        }

        if (!backup.BelongsTo(session.Document.DocumentId, session.Snapshot))
        {
            Status = "This restore point belongs to another save, so it was not restored here.";
            return;
        }

        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = $"Restoring {backup.BackupId[..13]}…";
            var bytes = await _backups.ReadAsync(backup.BackupId);
            // A restore is the recovery path: a deliberate whole-file write, never slot-scoped.
            var receipt = await _writer.WriteScopedAsync(session.Document.DocumentId, session.Snapshot, bytes,
                WriteScope.Everything,
                "Safety copy: the state right before this restore");
            if (receipt.Changed)
                _sessions.MarkWritten(session.Document.DocumentId, bytes);
            await _sessions.OpenAsync(session.Document);
            _boxBrowser.RefreshFromCurrentSession();
            Status = receipt.Changed
                ? $"Restored. Previous state kept as restore point {receipt.BackupId[..13]}…"
                : "This restore point matches the current state - nothing was written.";
            await LoadAsync();
        }
        catch (Exception error)
        {
            Status = $"Restore aborted: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreBankAsync(BackupInfo backup)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Restoring the Bank…";
            await _bankPoints.RestoreAsync(backup);
            await LoadAsync();
            Status = "Bank restored. The Bank as it was just before is kept as a restore point.";
        }
        catch (Exception error)
        {
            Status = $"Restore aborted: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
