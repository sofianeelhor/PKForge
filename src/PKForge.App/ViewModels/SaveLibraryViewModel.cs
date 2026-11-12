using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.App.Services;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.ViewModels;

/// <summary>One save in the library, with how many snapshots the player keeps of it.</summary>
public sealed record SaveLibraryRow(SaveLibraryEntry Entry, int Snapshots)
{
    /// <summary>The other files of the same playthrough (its copies), newest first.</summary>
    public IReadOnlyList<SaveLibraryRow> Versions { get; init; } = [];

    public string Title => Entry.Title;
    public string? ArtLabel => Entry.Save.ArtLabel;
    public int Generation => Entry.Save.Generation;
    public string Who => Entry.Who;
    public string Where => Entry.Where;

    /// <summary>The row's right side: why it is off the shelf, and its snapshots.</summary>
    public string Tag
    {
        get
        {
            var parts = new List<string>(3);
            if (Entry.Tag.Length > 0) parts.Add(Entry.Tag);
            if (Snapshots > 0) parts.Add(Snapshots == 1 ? "1 snapshot" : $"{Snapshots} snapshots");
            if (Versions.Count > 0) parts.Add(Versions.Count == 1 ? "1 other version" : $"{Versions.Count} other versions");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A filter of the library with how many saves it shows.</summary>
public sealed record SaveLibraryChip(SaveLibraryFilter Filter, string Label, int Count);

/// <summary>
/// The Saves section: every save file PKForge knows, on the shelf or not, and what the
/// player can do with one without opening it (snapshot it, hide it, put it back).
/// </summary>
public partial class SaveLibraryViewModel(
    SavePickerViewModel picker, IBackupService backups, ISaveFileAccess files, ISaveEngine engine,
    IGameDataService data, IFolderPicker folders, IFolderFileAccess folderFiles,
    ISafeSaveWriter writer, ISaveSessionService sessions) : ObservableObject
{
    private IReadOnlyList<SaveLibraryRow> _all = [];

    public ObservableCollection<SaveLibraryRow> Rows { get; } = [];
    public IReadOnlyList<SaveLibraryChip> Chips { get; private set; } = [];
    public int ChipIndex { get; private set; }
    public SavePickerViewModel Picker => picker;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Raised when the chips or the active one changed.</summary>
    public event Action? ChipsChanged;

    /// <summary>Raised with what an action just did, to be said as a notice.</summary>
    public event Action<string>? ActionReported;

    private void Report(string outcome)
    {
        Status = outcome;
        ActionReported?.Invoke(outcome);
    }

    public async Task LoadAsync(bool keepStatus = false)
    {
        var active = ChipIndex < Chips.Count ? Chips[ChipIndex].Filter : SaveLibraryFilter.All;
        var snapshots = (await backups.ListAsync())
            .Where(point => point.DocumentId is not null && SaveLibrary.IsSnapshot(point.ChangeDescription))
            .GroupBy(point => point.DocumentId!)
            .ToDictionary(group => group.Key, group => group.Count());
        var entries = SaveLibrary.Build([.. picker.Saves, .. picker.HiddenSaves], DateTimeOffset.Now, SaveDescriptions.EmulatorName);
        _all = [.. entries.Select(entry => new SaveLibraryRow(entry, snapshots.GetValueOrDefault(entry.Save.DocumentId)))];

        // Only the filters that have something to show, so no chip leads to an empty list.
        Chips =
        [
            .. Enum.GetValues<SaveLibraryFilter>()
                .Select(filter => new SaveLibraryChip(filter, SaveLibrary.Label(filter),
                    SaveLibrary.Apply(entries, filter).Count()))
                .Where(chip => chip.Filter == SaveLibraryFilter.All || chip.Count > 0),
        ];
        ChipIndex = Math.Max(0, Chips.ToList().FindIndex(chip => chip.Filter == active));
        ApplyFilter();
        if (!keepStatus) Status = Summary();
    }

    private string Summary()
    {
        if (_all.Count == 0) return "No saves yet. Link an emulator folder or open a save file from Home.";
        var offShelf = _all.Count(row => !row.Entry.OnShelf);
        var total = $"{_all.Count} save {(_all.Count == 1 ? "file" : "files")}";
        return offShelf == 0 ? $"{total}, all on Home." : $"{total}: {_all.Count - offShelf} on Home, {offShelf} hidden or set aside as copies.";
    }

    private void ApplyFilter()
    {
        var filter = Chips[ChipIndex].Filter;
        Rows.Clear();
        // One row per playthrough: its copies are reached as its versions.
        var shown = _all.Where(row => SaveLibrary.Apply([row.Entry], filter).Any()).ToList();
        var byEntry = shown.ToDictionary(row => (object)row.Entry, row => row, ReferenceEqualityComparer.Instance);
        foreach (var group in SaveLibrary.Group(shown.Select(row => row.Entry)))
            Rows.Add(byEntry[group.Lead] with { Versions = [.. group.Versions.Select(version => byEntry[version])] });
        ChipsChanged?.Invoke();
    }

    public void SelectChip(int index)
    {
        if (Chips.Count == 0) return;
        ChipIndex = ((index % Chips.Count) + Chips.Count) % Chips.Count;
        ApplyFilter();
        Status = Summary();
    }

    /// <summary>
    /// Keeps the save file as it is right now under a name of the player's choosing: a pinned
    /// restore point, so it never leaves to make room and can be put back from Restore points.
    /// The file is only read.
    /// </summary>
    public async Task SnapshotAsync(SaveLibraryRow row, string name)
    {
        var save = row.Entry.Save;
        try
        {
            var bytes = await files.ReadAsync(save.DocumentId);
            var snapshot = await Task.Run(() => engine.Open(bytes, save.EngineHint));
            var receipt = await backups.CreateAsync(snapshot, SaveLibrary.SnapshotDescription(name), save.DocumentId);
            await backups.SetPinnedAsync(receipt.BackupId, pinned: true);
            await LoadAsync(keepStatus: true);
            Report($"Snapshot \"{SaveLibrary.SnapshotName(SaveLibrary.SnapshotDescription(name))}\" kept in Restore points.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Report($"The snapshot could not be made. {Domain.ErrorText.Plain(error)}");
        }
    }

    /// <summary>The other saves of the same game, to compare <paramref name="row"/> with.</summary>
    public IReadOnlyList<SaveLibraryRow> SameGame(SaveLibraryRow row) =>
        [.. _all.Where(other => !ReferenceEquals(other.Entry, row.Entry)
            && other.Entry.Save.Generation == row.Entry.Save.Generation
            && string.Equals(other.Entry.Save.Identity?.GameLabel ?? other.Title, row.Entry.Save.Identity?.GameLabel ?? row.Title, StringComparison.OrdinalIgnoreCase))];

    public string SpeciesName(int species) =>
        (uint)species < (uint)data.SpeciesNames.Count && data.SpeciesNames[species].Length > 0 ? data.SpeciesNames[species] : $"#{species}";

    /// <summary>
    /// What the save holds, read from the file without connecting to it: trainer, party,
    /// boxes, Pokédex. Null when the file cannot be read.
    /// </summary>
    public async Task<SaveInsight?> InsightAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        // Kept per file and date: moving the cursor over the list reads each save once.
        var key = (save.DocumentId, save.LastModified);
        if (_insights.TryGetValue(key, out var known)) return known;
        var insight = await ReadInsightAsync(save);
        _insights[key] = insight;
        return insight;
    }

    private readonly Dictionary<(string, DateTimeOffset?), SaveInsight?> _insights = [];

    private async Task<SaveInsight?> ReadInsightAsync(DetectedSave save)
    {
        try
        {
            var bytes = await files.ReadAsync(save.DocumentId);
            return await Task.Run(() =>
            {
                using var session = engine.OpenSession(bytes, save.EngineHint);
                var trainer = session.GetTrainer();
                int seen = 0, caught = 0, total = 0;
                for (var species = 1; species <= session.MaxSpeciesId; species++)
                {
                    if (!session.IsDexSpecies(species)) continue;
                    total++;
                    var entry = session.GetDexEntry(species);
                    if (entry.Seen) seen++;
                    if (entry.Caught) caught++;
                }
                return SaveInsights.From(session.Snapshot, trainer.Name, trainer.TID, trainer.Money, save.PlayTime, seen, caught, total)
                    with { Badges = session.GetBadges() };
            });
        }
        catch (Exception error)
        {
            // Any failure, PKHeX's own on a damaged or exotic save included: a details read is never fatal.
            AppLog.Warn("saves", $"Reading {save.FileName} for its details failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// The Hall of Fame museum: one hall per playthrough on the Home shelf (its newest file),
    /// each read from a copy of its bytes in a session of its own that is never written.
    /// A save that cannot be read is left out.
    /// </summary>
    public async Task<IReadOnlyList<(SaveLibraryRow Row, MuseumHall Hall)>> MuseumAsync()
    {
        if (_all.Count == 0) await LoadAsync(keepStatus: true);
        var shelf = _all.Where(row => row.Entry.OnShelf).ToList();
        var byEntry = shelf.ToDictionary(row => (object)row.Entry, row => row, ReferenceEqualityComparer.Instance);
        var halls = new List<(SaveLibraryRow, MuseumHall)>();
        foreach (var group in SaveLibrary.Group(shelf.Select(row => row.Entry)))
        {
            var row = byEntry[group.Lead];
            if (await ReadHallAsync(row) is { } hall) halls.Add((row, hall));
        }
        var order = Museum.Timeline(halls.Select(h => h.Item2));
        return [.. order.Select(hall => halls.First(h => ReferenceEquals(h.Item2, hall)))];
    }

    private async Task<MuseumHall?> ReadHallAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        try
        {
            var bytes = await files.ReadAsync(save.DocumentId);
            return await Task.Run(() =>
            {
                using var session = engine.OpenSession(bytes, save.EngineHint);
                var trainer = session.GetTrainer();
                int seen = 0, caught = 0, total = 0;
                for (var species = 1; species <= session.MaxSpeciesId; species++)
                {
                    if (!session.IsDexSpecies(species)) continue;
                    total++;
                    var entry = session.GetDexEntry(species);
                    if (entry.Seen) seen++;
                    if (entry.Caught) caught++;
                }
                var held = session.Snapshot.Slots.Count(slot => slot.Species is not null && !slot.IsEgg);
                return new MuseumHall(save.DocumentId, row.Title, save.Generation, trainer.Name, trainer.TID, trainer.Gender,
                    MuseumService.Read(session), held, caught, seen, total, save.LastModified);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            AppLog.Warn("saves", $"Reading {save.FileName} for the museum failed: {error.Message}");
            return null;
        }
    }

    /// <summary>What each of two saves holds that the other does not. Null when one cannot be read.</summary>
    public async Task<SaveComparison?> CompareAsync(SaveLibraryRow first, SaveLibraryRow second)
    {
        try
        {
            var a = await files.ReadAsync(first.Entry.Save.DocumentId);
            var b = await files.ReadAsync(second.Entry.Save.DocumentId);
            return await Task.Run(() =>
            {
                // Through a session, whose picture counts the party: the engine's plain
                // Open lists the boxes only.
                using var left = engine.OpenSession(a, first.Entry.Save.EngineHint);
                using var right = engine.OpenSession(b, second.Entry.Save.EngineHint);
                return SaveInsights.Compare(left.Snapshot, right.Snapshot);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            AppLog.Warn("saves", $"Comparing two saves failed: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a copy of the save file into a folder the player picks: another emulator's save
    /// folder, a sync folder, anywhere. A file already there with that name is left alone:
    /// the copy takes a free name.
    /// </summary>
    public async Task ExportAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        try
        {
            if (await folders.PickFolderAsync() is not { } folder) return;
            var bytes = await files.ReadAsync(save.DocumentId);
            var existing = (await folderFiles.ListFilesAsync(folder.TreeId)).Select(file => file.DisplayName);
            var name = SaveInsights.FreeFileName(save.FileName, existing);
            await folderFiles.WriteFileAsync(folder.TreeId, name, bytes);
            // A copy written into a linked folder is a save PKForge can now see.
            await picker.RescanAsync();
            await LoadAsync(keepStatus: true);
            var place = folder.DisplayName[(folder.DisplayName.IndexOf(':') + 1)..];
            Report(name == save.FileName
                ? $"Copied to {place}."
                : $"Copied to {place} as \"{name}\". The file already there was left alone.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report($"The copy could not be written. {Domain.ErrorText.Plain(error)}");
        }
    }

    /// <summary>
    /// The game journal: what changed in the save since its last restore point (the file as
    /// it was before PKForge last wrote it, or a snapshot). Null when either cannot be read;
    /// Since is null when the save has no restore point to compare with.
    /// </summary>
    public async Task<(BackupInfo? Since, IReadOnlyList<JournalFact> Facts)?> JournalAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        try
        {
            var bytes = await files.ReadAsync(save.DocumentId);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.Span));
            if (SaveJournal.Baseline(await backups.ListAsync(), save.DocumentId, sha) is not { } since)
                return (null, []);
            var earlier = await backups.ReadAsync(since.BackupId);
            return await Task.Run(() =>
            {
                using var then = engine.OpenSession(earlier, save.EngineHint);
                using var now = engine.OpenSession(bytes, save.EngineHint);
                return ((BackupInfo?)since, SaveJournal.Diff(SaveJournal.Capture(then), SaveJournal.Capture(now), SpeciesName));
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            AppLog.Warn("saves", $"Reading what changed in {save.FileName} failed: {error.Message}");
            return null;
        }
    }

    /// <summary>The snapshots kept of this save, newest first.</summary>
    public async Task<IReadOnlyList<BackupInfo>> SnapshotsAsync(SaveLibraryRow row) =>
        SaveLibrary.SnapshotsOf(await backups.ListAsync(), row.Entry.Save.DocumentId);

    /// <summary>
    /// Puts the save back as it was in <paramref name="snapshot"/>, through the safe writer:
    /// the file as it is now is kept as a restore point first, and nothing is written when
    /// the two are the same. A save open for editing is reopened on what was written.
    /// </summary>
    public async Task RestoreSnapshotAsync(SaveLibraryRow row, BackupInfo snapshot)
    {
        var save = row.Entry.Save;
        try
        {
            var current = await files.ReadAsync(save.DocumentId);
            var original = await Task.Run(() => engine.Open(current, save.EngineHint));
            if (!snapshot.BelongsTo(save.DocumentId, original))
            {
                Report("This snapshot belongs to another save, so it was not restored here.");
                return;
            }
            var bytes = await backups.ReadAsync(snapshot.BackupId);
            // A restore is the recovery path: a deliberate whole-file write, never slot-scoped.
            var receipt = await writer.WriteScopedAsync(save.DocumentId, original, bytes, WriteScope.Everything,
                "Safety copy: the state right before this restore");
            if (receipt.Changed && sessions.Current is { } open && open.Document.DocumentId == save.DocumentId)
            {
                sessions.MarkWritten(save.DocumentId, bytes);
                await sessions.OpenAsync(open.Document);
            }
            _insights.Clear();
            await LoadAsync(keepStatus: true);
            var name = SaveLibrary.SnapshotName(snapshot.ChangeDescription);
            Report(receipt.Changed
                ? $"Restored \"{name}\"."
                : $"The save already matches \"{name}\": nothing was written.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or UnsafeSaveWriteException)
        {
            Report($"Not restored. {Domain.ErrorText.Plain(error)}");
        }
    }

    /// <summary>The other emulator file shapes this save can be written as, each converted or refused with a reason.</summary>
    public async Task<IReadOnlyList<(SaveContainer Target, SaveConversion Result)>> ConversionsAsync(SaveLibraryRow row)
    {
        var save = row.Entry.Save;
        try
        {
            var bytes = await files.ReadAsync(save.DocumentId);
            return await Task.Run(() => SaveContainerConversion.Options(bytes.Span, save.FileName, save.Generation));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report($"The save could not be read. {Domain.ErrorText.Plain(error)}");
            return [];
        }
    }

    /// <summary>Writes a conversion as a NEW file next to the original, which is never touched.</summary>
    public async Task WriteConversionAsync(SaveLibraryRow row, SaveConversion conversion)
    {
        if (conversion is not { Succeeded: true, FileName: { } fileName, Bytes: { } bytes }) return;
        try
        {
            var written = await folderFiles.WriteBesideAsync(row.Entry.Save.DocumentId, fileName, bytes);
            await picker.RescanAsync();
            await LoadAsync(keepStatus: true);
            Report($"Written as \"{written}\" next to {row.Entry.Save.FileName}, which was left as it was.");
        }
        catch (NotSupportedException error)
        {
            Report($"{Domain.ErrorText.Plain(error)} Use Copy to a folder on the original instead.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report($"The new file could not be written. {Domain.ErrorText.Plain(error)}");
        }
    }

    /// <summary>Hides a save from Home, or puts it back. A backup copy put back stays there.</summary>
    public async Task SetOnShelfAsync(SaveLibraryRow row, bool onShelf)
    {
        var save = row.Entry.Save;
        var current = picker.Identities.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
        picker.Identities.Set(onShelf
            ? current with { Hidden = false, KeepShown = save.IsCopy }
            : current with { Hidden = true });
        await LoadAsync(keepStatus: true);
        Report(onShelf ? $"{save.GameLabel} is back on Home." : $"{save.GameLabel} is hidden from Home. It stays in this list.");
    }
}
