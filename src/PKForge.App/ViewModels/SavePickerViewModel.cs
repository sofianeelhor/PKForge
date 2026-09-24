using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace PKForge.App.ViewModels;

/// <summary>
/// One cartridge on the shelf: every save of one game identity (see <see cref="SaveShelf"/>).
/// The same game in another language, region or emulator shares the tile; a save the
/// player renamed, recolored or re-identified gets its own, so hacks never hide under a
/// retail label.
/// </summary>
public sealed partial class SaveCard(IReadOnlyList<DetectedSave> saves) : ObservableObject
{
    public SaveCard(DetectedSave save) : this([save]) { }

    /// <summary>The tile's saves, most recently played first.</summary>
    public IReadOnlyList<DetectedSave> Saves { get; } = saves;
    /// <summary>The representative save: art, color and name come from it (all saves share them).</summary>
    public DetectedSave Save => Saves[0];
    public int SaveCount => Saves.Count;
    public bool HasSeveralSaves => Saves.Count > 1;
    public string CountBadge => Saves.Count > 1 ? Saves.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string DisplayName => Save.GameLabel;

    /// <summary>The shelf title: the cartridge art already says Pokémon, so a retail
    /// label drops the prefix ("HeartGold"); a name the user typed is shown as typed.</summary>
    public string ShelfTitle
    {
        get
        {
            // Labels arrive both precomposed and decomposed ("é" vs "e" + accent).
            var label = DisplayName.Normalize(System.Text.NormalizationForm.FormC);
            var typedName = Save.Identity is { } identity && identity.DisplayName != identity.GameLabel;
            return !typedName && label.StartsWith("Pokémon ", StringComparison.Ordinal)
                ? label["Pokémon ".Length..]
                : label;
        }
    }
    /// <summary>The game (chosen or detected), for filters and release order - never the custom name.</summary>
    public string GameLabel => Save.Identity?.GameLabel ?? Save.GameLabel;
    public int Generation => Save.Generation;
    public string? TrainerName => Save.TrainerName;
    public string? PlayTime => Save.PlayTime;
    public string FileName => Save.FileName;

    /// <summary>"Ash · 12:34", the file name when the trainer block is unreadable, or the trainers of a shared tile.</summary>
    public string TrainerLine
    {
        get
        {
            if (Saves.Count > 1)
            {
                var trainers = Saves.Select(s => s.TrainerName).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToArray();
                return trainers.Length switch
                {
                    0 => $"{Saves.Count} saves",
                    1 => $"{trainers[0]} · {Saves.Count} saves",
                    2 => $"{trainers[0]}, {trainers[1]}",
                    _ => $"{trainers[0]}, {trainers[1]} +{trainers.Length - 2}",
                };
            }
            return string.IsNullOrEmpty(Save.TrainerName)
                ? Save.FileName
                : string.IsNullOrEmpty(Save.PlayTime) ? Save.TrainerName! : $"{Save.TrainerName} · {Save.PlayTime}";
        }
    }

    /// <summary>The tile's last line: emulator and date only; folders live in the chooser and the save menu.</summary>
    public string DetailLine => SaveDescriptions.GroupDetail(Saves.Count > 0 ? Saves : [Save]);

    public string? ColorKey => Save.Identity?.ColorKey;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Shared one-line descriptions of a save for sheets and pickers.</summary>
public static class SaveDescriptions
{
    public static string Detail(DetectedSave save)
    {
        var parts = new List<string> { EmulatorName(save.Emulator) };
        if (!string.IsNullOrEmpty(save.FolderHint)) parts.Add(save.FolderHint!);
        if (save.LastModified is { } modified) parts.Add(modified.ToLocalTime().ToString("MMM d, yyyy"));
        return string.Join(" · ", parts);
    }

    /// <summary>A tile: its emulators and the latest play date ("melonDS, DraStic · Sep 3, 2026").</summary>
    public static string GroupDetail(IReadOnlyList<DetectedSave> saves)
    {
        var emulators = saves.Select(s => EmulatorName(s.Emulator)).Distinct().ToArray();
        var parts = new List<string> { emulators.Length <= 2 ? string.Join(", ", emulators) : $"{emulators.Length} emulators" };
        if (saves.Max(s => s.LastModified) is { } latest) parts.Add(latest.ToLocalTime().ToString("MMM d, yyyy"));
        return string.Join(" · ", parts);
    }

    public static string EmulatorName(EmulatorKind kind) => kind switch
    {
        EmulatorKind.MelonDS => "melonDS",
        EmulatorKind.DraStic => "DraStic",
        EmulatorKind.PizzaBoyGba => "Pizza Boy GBA",
        EmulatorKind.PizzaBoyGbc => "Pizza Boy GBC",
        _ => kind.ToString(),
    };
}

public partial class SavePickerViewModel : ObservableObject
{
    private readonly IFolderPicker _folderPicker;
    private readonly IDocumentPicker _filePicker;
    private readonly IEmulatorDetectionService _detection;
    private readonly IWatchedRootStore _roots;
    private readonly ISaveSessionService _sessions;
    private readonly BoxBrowserViewModel _boxBrowser;
    private readonly ISaveIdentityStore _identities;

    public SavePickerViewModel(
        IFolderPicker folderPicker,
        IDocumentPicker filePicker,
        IEmulatorDetectionService detection,
        IWatchedRootStore roots,
        ISaveSessionService sessions,
        BoxBrowserViewModel boxBrowser,
        ISaveIdentityStore identities)
    {
        _identities = identities;
        _identities.Changed += OnIdentityChanged;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _detection = detection;
        _roots = roots;
        _sessions = sessions;
        _boxBrowser = boxBrowser;
    }

    private const string SetupDoneKey = "setup_complete";

    /// <summary>
    /// Every visible detected save, with the player's identity applied: <see cref="DetectedSave.GameLabel"/>
    /// is the display name, <see cref="DetectedSave.Identity"/> carries color, game and route.
    /// Hidden saves are left out here, so Home and every picker skip them.
    /// </summary>
    public ObservableCollection<DetectedSave> Saves { get; } = [];

    /// <summary>Saves the player hid, for "Show hidden saves".</summary>
    public IReadOnlyList<DetectedSave> HiddenSaves => [.. _detected.Select(Resolve).Where(s => s.Identity?.IsHidden == true)];

    /// <summary>The shelf view: one card per game identity (see <see cref="SaveShelf"/>). Rebuilt on every scan.</summary>
    public ObservableCollection<SaveCard> Groups { get; } = [];

    public ISaveIdentityStore Identities => _identities;

    private readonly HashSet<string> _saveIds = new(StringComparer.Ordinal);
    /// <summary>Scanner output as detected (no identity applied), in discovery order.</summary>
    private readonly List<DetectedSave> _detected = [];
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
    private Task AddDolphinAsync() => AddRootAndScanAsync(EmulatorKind.Dolphin);

    [RelayCommand]
    private Task AddDraSticAsync() => AddRootAndScanAsync(EmulatorKind.DraStic);

    [RelayCommand]
    private Task AddPizzaBoyGbaAsync() => AddRootAndScanAsync(EmulatorKind.PizzaBoyGba);

    [RelayCommand]
    private Task AddPizzaBoyGbcAsync() => AddRootAndScanAsync(EmulatorKind.PizzaBoyGbc);

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsBusy) return;
        var roots = _roots.GetRoots();
        if (roots.Count == 0)
        {
            Status = "No storage units linked yet. Open Link and choose your platform and emulator.";
            return;
        }

        try
        {
            IsBusy = true;
            Saves.Clear();
            Groups.Clear();
            _saveIds.Clear();
            _detected.Clear();
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
                    if (_detection is IIncrementalEmulatorDetectionService incremental)
                    {
                        await foreach (var update in incremental.ScanIncrementalAsync(root.TreeId, root.Kind))
                        {
                            if (!update.IsComplete)
                            {
                                if (update.Save is { } save)
                                    AddSave(save);
                                continue;
                            }

                            filesSeen += update.FilesSeen;
                            if (update.Diagnostics is { } diagnostics)
                                _scanDiagnostics.AddRange(diagnostics);
                            if (update.RejectedCandidates is { } rejectedCandidates)
                            {
                                foreach (var rejected in rejectedCandidates)
                                    _rejectedCandidates.Add($"{root.Kind}: {rejected}");
                            }
                            _scanDiagnostics.Add($"RESULT files={update.FilesSeen} saves={update.SavesFound} rejected={update.RejectedCandidates?.Count ?? 0}");
                        }
                    }
                    else
                    {
                        var result = await _detection.ScanAsync(root.TreeId, root.Kind);
                        filesSeen += result.FilesSeen;
                        if (result.Diagnostics is { } diagnostics)
                            _scanDiagnostics.AddRange(diagnostics);
                        foreach (var rejected in result.RejectedCandidates)
                            _rejectedCandidates.Add($"{root.Kind}: {rejected}");
                        _scanDiagnostics.Add($"RESULT files={result.FilesSeen} saves={result.Saves.Count} rejected={result.RejectedCandidates.Count}");
                        foreach (var save in result.Saves)
                            AddSave(save);
                    }
                }
                catch (Exception error)
                {
                    _scanDiagnostics.Add($"ROOT FAILED {error}");
                    Status = $"Scan of {root.Kind} failed: {error.Message}";
                }
            }
            RebuildGroups();
            Status = Saves.Count == 0
                ? $"No games found. Scanned {filesSeen} file(s), {_rejectedCandidates.Count} looked like saves but did not parse."
                : $"{Saves.Count} save(s) on the shelf. Scanned {filesSeen} file(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddSave(DetectedSave save)
    {
        if (!_saveIds.Add(save.DocumentId))
            return;

        _detected.Add(save);
        var resolved = Resolve(save);
        if (resolved.Identity?.IsHidden != true) Saves.Add(resolved);
    }

    /// <summary>Saves cached by an older build carry no guess: rebuild one from the label.</summary>
    private static SaveIdentityGuess GuessOf(DetectedSave save) => save.Guess ?? SaveIdentityRules.Guess(
        save.GameLabel.StartsWith("Pokémon ", StringComparison.Ordinal) ? save.GameLabel["Pokémon ".Length..] : null,
        save.Generation, save.FileName, save.RomFileName, save.GameLabel);

    private DetectedSave Resolve(DetectedSave detected)
    {
        var guess = GuessOf(detected);
        var identity = SaveIdentityResolver.Resolve(guess, _identities.Get(detected.DocumentId));
        return detected with { GameLabel = identity.DisplayName, Guess = guess, Identity = identity };
    }

    /// <summary>The save as detected, before the player's identity (for the identity sheet).</summary>
    public DetectedSave? DetectedFor(string documentId) => _detected.FirstOrDefault(s => s.DocumentId == documentId);

    private void OnIdentityChanged(string documentId)
    {
        void Apply()
        {
            var index = _detected.FindIndex(s => s.DocumentId == documentId);
            if (index < 0) return;
            // Rebuild the visible list in discovery order so hiding and showing keep the shelf stable.
            var visible = _detected.Select(Resolve).Where(s => s.Identity?.IsHidden != true).ToList();
            Saves.Clear();
            foreach (var save in visible) Saves.Add(save);
            OnPropertyChanged(nameof(HiddenSaves));
            // Keep the highlight on the tile that now holds the edited save (it may have
            // moved to its own tile after a rename), else on whatever was selected.
            var selected = Groups.FirstOrDefault(g => g.IsSelected)?.Saves.Select(s => s.DocumentId).ToHashSet();
            RebuildGroups();
            var target = Groups.FirstOrDefault(g => selected?.Contains(documentId) == true && g.Saves.Any(s => s.DocumentId == documentId))
                ?? Groups.FirstOrDefault(g => selected is not null && g.Saves.Any(s => selected.Contains(s.DocumentId)));
            foreach (var card in Groups) card.IsSelected = ReferenceEquals(card, target);
        }
        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    /// <summary>Caption of the active shelf filter, for the home screen chip.</summary>
    public partial class FilterState : ObservableObject
    {
        [ObservableProperty] private string _caption = "ALL";
    }

    public FilterState Filter { get; } = new();
    private string _filterKey = "all";

    /// <summary>
    /// Applies a shelf filter: "all", "az", "gen1".."gen9", or a console bucket
    /// ("gb", "gba", "ds", "3ds", "switch"). The unfiltered list is always kept in
    /// Saves; only the shelf view narrows.
    /// </summary>
    public void ApplyFilter(string key, string caption)
    {
        _filterKey = key;
        Filter.Caption = caption;
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        IEnumerable<SaveCard> groups = SaveShelf.Group(Saves).Select(saves => new SaveCard(saves));

        groups = _filterKey switch
        {
            "release" => groups.OrderBy(ReleaseRank).ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            "az" => groups.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            "gen1" or "gen2" or "gen3" or "gen4" or "gen5" or "gen6" or "gen7" or "gen8" or "gen9"
                => groups.Where(g => g.Generation == int.Parse(_filterKey[3..])),
            "gb" => groups.Where(g => ConsoleOf(g) == "Game Boy"),
            "gba" => groups.Where(g => ConsoleOf(g) == "GBA"),
            "ds" => groups.Where(g => ConsoleOf(g) == "DS"),
            "3ds" => groups.Where(g => ConsoleOf(g) == "3DS"),
            "switch" => groups.Where(g => ConsoleOf(g) == "Switch"),
            _ => groups,
        };
        foreach (var group in groups)
            Groups.Add(group);
    }

    /// <summary>Console a game belongs to. Let's Go is a Switch game despite being
    /// generation 7 with the 3DS pair.</summary>
    private static string ConsoleOf(SaveCard group) => group.GameLabel.StartsWith("Pokémon Let's Go", StringComparison.Ordinal)
        ? "Switch"
        : group.Generation switch
        {
            1 or 2 => "Game Boy",
            3 => "GBA",
            4 or 5 => "DS",
            6 or 7 => "3DS",
            _ => "Switch",
        };

    /// <summary>
    /// Chronological shelf order: Red/Blue (1996) through Legends: Z-A, with the
    /// GameCube side games and romhacks slotted next to the era they belong to.
    /// Unknown labels fall back to generation order.
    /// </summary>
    private static int ReleaseRank(SaveCard group)
    {
        var label = group.GameLabel;
        return label switch
        {
            // Substring traps first: FireRed contains "Red", HeartGold contains "Gold",
            // Omega Ruby contains "Ruby", Brilliant Diamond contains "Diamond"...
            _ when label.Contains("FireRed", StringComparison.Ordinal) || label.Contains("LeafGreen", StringComparison.Ordinal) ||
                    label.Contains("Unbound", StringComparison.Ordinal) || label.Contains("Radical Red", StringComparison.Ordinal) ||
                    label.Contains("GS Chronicles", StringComparison.Ordinal) => 2004,
            _ when label.Contains("HeartGold", StringComparison.Ordinal) || label.Contains("SoulSilver", StringComparison.Ordinal) => 2009,
            _ when label.Contains("Black 2", StringComparison.Ordinal) || label.Contains("White 2", StringComparison.Ordinal) => 2012,
            _ when label.Contains("Omega", StringComparison.Ordinal) || label.Contains("Alpha", StringComparison.Ordinal) => 2014,
            _ when label.Contains("Ultra", StringComparison.Ordinal) => 2017,
            _ when label.Contains("Let's Go", StringComparison.Ordinal) => 2018,
            _ when label.Contains("Brilliant", StringComparison.Ordinal) || label.Contains("Shining", StringComparison.Ordinal) ||
                    label.Contains("Luminescent", StringComparison.Ordinal) => 2021,
            _ when label.Contains("Legends: Arceus", StringComparison.Ordinal) => 2022,
            _ when label.Contains("Legends: Z-A", StringComparison.Ordinal) => 2025,
            _ when label.Contains("Colosseum", StringComparison.Ordinal) => 2003,
            _ when label.Contains("XD", StringComparison.Ordinal) => 2005,
            _ when label.Contains("Box", StringComparison.Ordinal) => 2002,
            _ when label.Contains("Red", StringComparison.Ordinal) || label.Contains("Blue", StringComparison.Ordinal) ||
                    label.Contains("Green", StringComparison.Ordinal) => 1996,
            _ when label.Contains("Yellow", StringComparison.Ordinal) => 1998,
            _ when label.Contains("Gold", StringComparison.Ordinal) || label.Contains("Silver", StringComparison.Ordinal) => 1999,
            _ when label.Contains("Crystal", StringComparison.Ordinal) => 2000,
            _ when label.Contains("Ruby", StringComparison.Ordinal) || label.Contains("Sapphire", StringComparison.Ordinal) => 2002,
            _ when label.Contains("Emerald", StringComparison.Ordinal) => 2004,
            _ when label.Contains("Diamond", StringComparison.Ordinal) || label.Contains("Pearl", StringComparison.Ordinal) => 2006,
            _ when label.Contains("Platinum", StringComparison.Ordinal) => 2008,
            _ when label.Contains("Black", StringComparison.Ordinal) || label.Contains("White", StringComparison.Ordinal) => 2010,
            _ when label.Contains("X", StringComparison.Ordinal) || label.Contains("Y", StringComparison.Ordinal) => 2013,
            _ when label.Contains("Sun", StringComparison.Ordinal) || label.Contains("Moon", StringComparison.Ordinal) => 2016,
            _ when label.Contains("Scarlet", StringComparison.Ordinal) || label.Contains("Violet", StringComparison.Ordinal) ||
                    label.Contains("Compass", StringComparison.Ordinal) => 2022,
            _ => group.Generation * 1000,
        };
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
            // EngineHint carries the chosen game (FireRed vs LeafGreen), never the custom
            // name; the session service reads the chosen engine route from the store.
            await _sessions.OpenAsync(new PickedDocument(save.DocumentId, save.EngineHint));
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
