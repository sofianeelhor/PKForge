using PKForge.App.Views;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>"Save .pk files" with a PKForge folder set: its Exports/ is offered first, another
/// folder stays one choice away.</summary>
public static class PkforgeFolderExports
{
    /// <summary>The status line when the files went to Exports/ (empty when the player backed
    /// out); null when no PKForge folder is set or another folder was asked for, and the usual
    /// folder picker should run.</summary>
    public static async Task<string?> TryAsync(Grid host, string label, Func<IReadOnlyList<PkFile>> files)
    {
        var mirror = IPlatformApplication.Current?.Services.GetService<PkforgeFolder>();
        if (mirror?.Folder is not { } folder) return null;
        var here = $"{folder.DisplayName} › Exports";
        var choice = await PadMenu.ShowAsync(host, "Save .pk files", $"Where should the files from {label} go?", here, "Another folder…");
        if (choice != here) return choice is null ? "" : null;
        var overlay = LoadingOverlay.Show(host, "Saving .pk files…", $"Writing to {here}.");
        try
        {
            var batch = await Task.Run(files);
            var written = await Task.Run(() => mirror.ExportAsync(batch, $"{DateTime.Now:yyyy-MM-dd HHmm} - {label}"));
            return $"{written} .pk file{(written == 1 ? "" : "s")} from {label} saved to {here}.";
        }
        catch (Exception error)
        {
            AppLog.Error("folder", "Saving .pk files to the PKForge folder failed", error);
            return $"Saving failed. {Domain.ErrorText.Plain(error)}";
        }
        finally
        {
            overlay.Close();
        }
    }
}
