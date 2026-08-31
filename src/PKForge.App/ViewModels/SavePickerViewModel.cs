using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

/// <summary>One game on the shelf: every detected save for it (folders for multiple saves of one game).</summary>
public sealed partial class SaveGroup(
    string gameLabel, int generation, EmulatorKind emulator,
    string? trainerName, string? playTime, IReadOnlyList<DetectedSave> saves) : ObservableObject
{
    public string GameLabel { get; } = gameLabel;
    public int Generation { get; } = generation;
    public EmulatorKind Emulator { get; } = emulator;
    public string? TrainerName { get; } = trainerName;
    public string? PlayTime { get; } = playTime;
    public IReadOnlyList<DetectedSave> Saves { get; } = saves;
    public int Count => Saves.Count;
    [ObservableProperty] private bool _isSelected;
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
    private readonly List<string> _scanDiagnostics = [];

    /// <summary>Copyable evidence for folder grants, traversal, candidate bytes and parser outcomes.</summary>
    public string ScanReport => _scanDiagnostics.Count == 0
        ? "No diagnostic scan has run yet. Use Rescan games, then open Scan report again."
        : string.Join("\n", _scanDiagnostics);

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
    private Task AddLinkboyAsync() => AddRootAndScanAsync(EmulatorKind.Linkboy);

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
            _scanDiagnostics.Clear();
            _scanDiagnostics.Add($"PKForge scan report · {DateTimeOffset.Now:O}");
            _scanDiagnostics.Add($"Linked roots: {roots.Count}");
            foreach (var root in roots)
            {
                _scanDiagnostics.Add(string.Empty);
                _scanDiagnostics.Add($"ROOT kind={root.Kind} name={root.DisplayName}");
                _scanDiagnostics.Add($"TREE URI {root.TreeId}");
                Status = $"Scanning {root.Kind} unit · {root.DisplayName}…";
                try
                {
                    var result = await _detection.ScanAsync(root.TreeId, root.Kind);
                    filesSeen += result.FilesSeen;
                    if (result.Diagnostics is { } diagnostics)
                        _scanDiagnostics.AddRange(diagnostics);
                    foreach (var rejected in result.RejectedCandidates)
                        _rejectedCandidates.Add($"{root.Kind}: {rejected}");
                    _scanDiagnostics.Add($"RESULT files={result.FilesSeen} saves={result.Saves.Count} rejected={result.RejectedCandidates.Count}");
                    foreach (var save in result.Saves)
                        if (!Saves.Any(x => x.DocumentId == save.DocumentId))
                            Saves.Add(save);
                }
                catch (Exception error)
                {
                    _scanDiagnostics.Add($"ROOT FAILED {error}");
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
