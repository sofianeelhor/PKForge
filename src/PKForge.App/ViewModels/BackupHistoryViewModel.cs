using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

public partial class BackupHistoryViewModel : ObservableObject
{
    private readonly IBackupService _backups;
    private readonly ISaveSessionService _sessions;
    private readonly ISafeSaveWriter _writer;
    private readonly BoxBrowserViewModel _boxBrowser;

    public BackupHistoryViewModel(IBackupService backups, ISaveSessionService sessions, ISafeSaveWriter writer, BoxBrowserViewModel boxBrowser)
    {
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
        foreach (var info in await _backups.ListAsync())
            Backups.Add(info);
        Status = Backups.Count == 0
            ? "No backups yet. One is created automatically before every write."
            : CanRestore
                ? $"{Backups.Count} backup(s). Restoring writes into the currently open save file."
                : $"{Backups.Count} backup(s). Open a save to enable restore.";
    }

    /// <summary>Writes the backup's bytes into the open document; the current state is itself backed up first.</summary>
    public async Task RestoreAsync(BackupInfo backup)
    {
        var session = _sessions.Current;
        if (session is null)
        {
            Status = "Open a save first - restore writes into the open save file.";
            return;
        }

        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = $"Restoring {backup.BackupId[..13]}…";
            var bytes = await _backups.ReadAsync(backup.BackupId);
            var receipt = await _writer.WriteAsync(session.Document.DocumentId, session.Snapshot, bytes,
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
}
