using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

// Settings › Games and folders › PKForge folder: the folder that mirrors PKForge's data.
public sealed partial class HomePage
{
    private const string FolderTitle = "PKForge folder";

    private static PkforgeFolder? Mirror => IPlatformApplication.Current?.Services.GetService<PkforgeFolder>();

    private (PadOption, Func<Task<bool>>) PkforgeFolderRow()
    {
        var folder = Mirror?.Folder;
        return folder is null
            ? (new PadOption("PKForge folder: Choose…", IconPath: "sdcard",
                Detail: "Keeps a copy of your Bank, photos and saves, synced both ways."), Stay(ChoosePkforgeFolderAsync))
            : (new PadOption($"PKForge folder: {folder.DisplayName}", IconPath: "sdcard", Detail: LastSyncLine()), Stay(ShowPkforgeFolderAsync));
    }

    private static string LastSyncLine()
    {
        if (Mirror is not { } mirror) return "";
        if (mirror.IsSyncing) return "Syncing…";
        return mirror.LastReport is { } last
            ? $"{(last.Error is null ? "Synced" : "Tried")} {last.FinishedUtc.ToLocalTime():MMM d, HH:mm}: {last.Summary}"
            : "Not synced yet";
    }

    private async Task ChoosePkforgeFolderAsync()
    {
        if (Mirror is not { } mirror) return;
        var go = await PadMenu.ShowAsync(_hostGrid, FolderTitle,
            "Pick or make a folder (for example \"PKForge\" on your storage). PKForge puts a copy of its data there and keeps it in sync both ways:\n"
            + "• Bank: a folder per box, a .pk file per Pokémon (PKHeX reads them). Drop a .pk file in a box to deposit it.\n"
            + "• Album: your Poképark photos.\n• Saves: your pinned restore points as .sav files.\n• Items: your item presets.\n• Exports: where .pk exports and data archives go.\n\n"
            + "PKForge keeps working on its own copy, so nothing is lost if the folder goes away. You can back the folder up or sync it with Syncthing.",
            "Choose a folder", "Not now");
        if (go != "Choose a folder") return;
        await PickPkforgeFolderAsync(mirror);
    }

    private async Task PickPkforgeFolderAsync(PkforgeFolder mirror)
    {
        var picker = IPlatformApplication.Current?.Services.GetService<IFolderPicker>();
        if (picker is null || await picker.PickFolderAsync() is not { } picked) return;
        mirror.Use(picked);
        AppLog.Info("folder", $"Player set the PKForge folder to {picked.DisplayName}");
        await SyncPkforgeFolderAsync(mirror);
    }

    private Task<bool> ShowPkforgeFolderAsync() => ShowSectionAsync(FolderTitle, () =>
    {
        var mirror = Mirror!;
        var folder = mirror.Folder;
        if (folder is null) return [];
        var held = PkforgeFolder.RecentlyRemoved.List().Count;
        var last = mirror.LastReport;
        var rows = new List<(PadOption, Func<Task<bool>>)>
        {
            (new("Sync now", IconPath: "refresh", Detail: LastSyncLine()), Stay(() => SyncPkforgeFolderAsync(mirror))),
        };
        if (last is { } report && (report.Conflicts.Count > 0 || report.LeftAlone.Count > 0 || report.Error is not null))
            rows.Add((new("Last sync details", IconPath: "report", Detail: $"{report.Conflicts.Count} conflict(s), {report.LeftAlone.Count} file(s) left alone"),
                Stay(() => ShowSyncDetailsAsync(report))));
        if (held > 0)
            rows.Add((new($"Recently removed ({held})", IconPath: "history", Detail: "Deleted from the folder, kept 30 days."),
                Stay(ShowRecentlyRemovedAsync)));
        rows.Add((new("Change folder", IconPath: "sdcard", Detail: $"Now: {folder.DisplayName}. The new folder gets everything; the old one keeps its copy."),
            Leave(() => PickPkforgeFolderAsync(mirror))));
        rows.Add((new("Stop syncing", IconPath: "unlink", Detail: "PKForge and the folder both keep their copy."), Leave(async () =>
        {
            if (await PadMenu.ConfirmAsync(_hostGrid, "Stop syncing?",
                    $"PKForge stops updating {folder.DisplayName}. Your data here and the files there both stay as they are.", "Stop syncing"))
                mirror.Stop();
        })));
        return [.. rows];
    });

    private async Task SyncPkforgeFolderAsync(PkforgeFolder mirror)
    {
        var overlay = LoadingOverlay.Show(_hostGrid, "Syncing the PKForge folder…", mirror.Folder?.DisplayName ?? "");
        PkforgeFolder.Stored? report;
        try { report = await mirror.SyncNowAsync(); }
        finally { overlay.Close(); }
        if (report is null) return;
        _viewModel.Status = report.Summary;
        if (report.Error is not null || report.Conflicts.Count > 0 || report.LeftAlone.Count > 0)
            await ShowSyncDetailsAsync(report);
    }

    private async Task ShowSyncDetailsAsync(PkforgeFolder.Stored report)
    {
        var lines = new List<string> { report.Summary };
        if (report.Error is not null)
            lines.Add("Your data in PKForge is untouched. Check the folder is still there, or choose it again from Change folder.");
        if (report.Conflicts.Count > 0)
        {
            lines.Add("\nChanged in both places (the folder's version was kept as \"(conflict …)\"):");
            lines.AddRange(report.Conflicts.Take(8).Select(c => "• " + c));
        }
        if (report.LeftAlone.Count > 0)
        {
            lines.Add("\nLeft as they are in the folder:");
            lines.AddRange(report.LeftAlone.Take(8).Select(c => "• " + c));
            if (report.LeftAlone.Count > 8) lines.Add($"…and {report.LeftAlone.Count - 8} more (in Share logs).");
        }
        await PadMenu.ShowAsync(_hostGrid, "PKForge folder sync", string.Join("\n", lines), "OK");
    }

    private async Task ShowRecentlyRemovedAsync()
    {
        var bank = IPlatformApplication.Current?.Services.GetService<IBankService>();
        if (bank is null) return;
        while (true)
        {
            var items = PkforgeFolder.RecentlyRemoved.List();
            if (items.Count == 0) return;
            var options = items.Take(40).Select(item => new PadOption(
                Path.GetFileName(item.FolderPath), IconPath: item.Info is null ? "file" : "box",
                Detail: $"Removed {item.RemovedUtc.ToLocalTime():MMM d, HH:mm} from {Path.GetDirectoryName(item.FolderPath)?.Replace('\\', '/')}")).ToList();
            options.Insert(0, new PadOption($"Put all {items.Count} back", IconPath: "import"));
            var choice = await PadMenu.ShowAsync(_hostGrid, "Recently removed", "Deleted from the PKForge folder. Each is kept 30 days.", [.. options]);
            if (choice is null) return;
            var picked = choice.StartsWith("Put all ") ? items : items.Where(i => Path.GetFileName(i.FolderPath) == choice).Take(1).ToList();
            var restored = 0;
            foreach (var item in picked)
            {
                try
                {
                    PkforgeFolder.RecentlyRemoved.Restore(item, bank);
                    restored++;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    AppLog.Error("folder", $"Restoring {item.FolderPath} failed", error);
                }
            }
            _viewModel.Status = $"{restored} put back.";
            Mirror?.RequestSync(TimeSpan.FromSeconds(1));
        }
    }
}
