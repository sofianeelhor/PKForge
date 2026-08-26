using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

/// <summary>One game on the shelf: every detected save for it (folders for multiple saves of one game).</summary>
public sealed record SaveGroup(
    string GameLabel, int Generation, EmulatorKind Emulator,
    string? TrainerName, string? PlayTime, IReadOnlyList<DetectedSave> Saves)
{
    public int Count => Saves.Count;
}

public partial class SavePickerViewModel : ObservableObject
{
    private readonly IFolderPicker _folderPicker;
    private readonly IDocumentPicker _filePicker;
    private readonly IEmulatorDetectionService _detection;
    private readonly IWatchedRootStore _roots;
    private readonly ISaveSessionService _sessions;
    private readonly BoxBrowserViewModel _boxBrowser;

    public SavePickerViewModel(
        IFolderPicker folderPicker,
        IDocumentPicker filePicker,
        IEmulatorDetectionService detection,
        IWatchedRootStore roots,
        ISaveSessionService sessions,
        BoxBrowserViewModel boxBrowser)
    {
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _detection = detection;
        _roots = roots;
        _sessions = sessions;
        _boxBrowser = boxBrowser;
    }

    private const string SetupDoneKey = "setup_complete";

    public ObservableCollection<DetectedSave> Saves { get; } = [];

    /// <summary>The shelf view: saves grouped by game. Rebuilt on every scan.</summary>
    public ObservableCollection<SaveGroup> Groups { get; } = [];

    private readonly List<string> _rejectedCandidates = [];

    /// <summary>Evidence for "why wasn't my save found?" - candidate files that failed to parse.</summary>
    public string ScanReport => _rejectedCandidates.Count == 0
        ? "Every save-like file parsed, or none were found. Check that the linked folder actually contains the emulator's save files (not ROMs or states)."
        : string.Join("\n", _rejectedCandidates.Take(14)) + (_rejectedCandidates.Count > 14 ? $"\n… and {_rejectedCandidates.Count - 14} more" : "");

    [ObservableProperty] private string _status = "Link an emulator's storage to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showWizard = !Preferences.Default.Get(SetupDoneKey, false);

    /// <summary>First-run setup is done once the user links anything (or skips).</summary>
    [RelayCommand]
    public void CompleteSetup()
    {
        Preferences.Default.Set(SetupDoneKey, true);
        ShowWizard = false;
    }

    /// <summary>Set true by the page when a save was opened, so it can pop back.</summary>
    public bool OpenedSave { get; private set; }

    [RelayCommand]
    private Task AddRetroArchAsync() => AddRootAndScanAsync(EmulatorKind.RetroArch);

    [RelayCommand]
    private Task AddMelonDsAsync() => AddRootAndScanAsync(EmulatorKind.MelonDS);

    [RelayCommand]
    private Task AddAzaharAsync() => AddRootAndScanAsync(EmulatorKind.Azahar);

    [RelayCommand]
    private Task AddEdenAsync() => AddRootAndScanAsync(EmulatorKind.Eden);

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsBusy) return;
        var roots = _roots.GetRoots();
        if (roots.Count == 0)
        {
            Status = "No storage units linked yet. Link one below (RetroArch/melonDS: the saves folder; Azahar/Eden: the emulator's files root).";
            return;
        }

        try
        {
            IsBusy = true;
            Saves.Clear();
            var filesSeen = 0;
            _rejectedCandidates.Clear();
            foreach (var root in roots)
            {
                Status = $"Scanning {root.Kind} unit · {root.DisplayName}…";
                try
                {
                    var result = await _detection.ScanAsync(root.TreeId, root.Kind);
                    filesSeen += result.FilesSeen;
                    foreach (var rejected in result.RejectedCandidates)
                        _rejectedCandidates.Add($"{root.Kind}: {rejected}");
                    foreach (var save in result.Saves)
                        if (!Saves.Any(x => x.DocumentId == save.DocumentId))
                            Saves.Add(save);
                }
                catch (Exception error)
                {
                    Status = $"Scan of {root.Kind} failed: {error.Message}";
                }
            }
            Groups.Clear();
            foreach (var group in Saves.GroupBy(s => s.GameLabel).OrderBy(g => g.Key))
                Groups.Add(new SaveGroup(group.Key, group.First().Generation, group.First().Emulator,
                    group.First().TrainerName, group.First().PlayTime, group.ToArray()));
            Status = Saves.Count == 0
                ? $"No games found. Scanned {filesSeen} file(s), {_rejectedCandidates.Count} looked like saves but did not parse."
                : $"{Saves.Count} game(s) on the shelf. Scanned {filesSeen} file(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Direct link to a single save file (the escape hatch when detection can't find it).</summary>
    [RelayCommand]
    private async Task LinkFileAsync()
    {
        if (IsBusy) return;
        OpenedSave = false;
        try
        {
            IsBusy = true;
            Status = "Select the save file to link…";
            var document = await _filePicker.PickSaveAsync();
            if (document is null) { Status = "Link cancelled."; return; }
            Status = $"Linking {document.DisplayName}…";
            await _sessions.OpenAsync(document);
            _boxBrowser.RefreshFromCurrentSession();
            OpenedSave = true;
            CompleteSetup();
            Status = "Storage linked.";
        }
        catch (Exception error)
        {
            Status = $"Could not link save: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddRootAndScanAsync(EmulatorKind kind)
    {
        if (IsBusy) return;
        var folder = await _folderPicker.PickFolderAsync();
        if (folder is null) return;
        _roots.AddRoot(new WatchedRoot(kind, folder.TreeId, folder.DisplayName));
        CompleteSetup();
        await RescanAsync();
    }

    public async Task OpenAsync(DetectedSave save)
    {
        if (IsBusy) return;
        OpenedSave = false;
        try
        {
            IsBusy = true;
            Status = $"Connecting to {save.GameLabel}…";
            await _sessions.OpenAsync(new PickedDocument(save.DocumentId, $"{save.GameLabel} ({save.FileName})"));
            _boxBrowser.RefreshFromCurrentSession();
            OpenedSave = true;
            Status = "Connected.";
        }
        catch (Exception error)
        {
            Status = $"Could not connect: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
