using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App.Views;

/// <summary>
/// The Collection Center: browse community event collections (RoC's PC) folder by
/// folder and deposit any box of Pokémon straight into your Bank. Wondercards for
/// the connected game stay in the in-save Event Database; this is the online shelf.
/// </summary>
public static class CollectionCenter
{
    public static async Task ShowAsync(Grid host)
    {
        var services = IPlatformApplication.Current?.Services;
        var bank = services?.GetService<IBankService>();
        var engine = services?.GetService<ISaveEngine>();
        if (bank is null || engine is null) return;

        var service = Service ??= new CommunityBoxService();
        var trail = new Stack<(string Path, string Title)>();
        trail.Push(("", CommunityBoxService.RepoTitle));

        while (trail.Count > 0)
        {
            var (path, title) = trail.Peek();
            IReadOnlyList<CommunityNode> nodes;
            var loading = LoadingOverlay.Show(host, "COLLECTION CENTER",
                "Fetching the community shelf from RoC's PC. Visited folders are kept offline.");
            try
            {
                nodes = await service.ListAsync(path, loading.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                loading.Close();
                await PadMenu.ShowAsync(host, "COLLECTION CENTER", error.Message, "OK");
                return;
            }
            finally
            {
                loading.Close();
            }

            var files = nodes.Where(n => !n.IsDirectory).ToList();
            var dirs = nodes.Where(n => n.IsDirectory).ToList();

            var rows = new List<PickItem>();
            if (files.Count > 0)
                rows.Add(new PickItem(-1, $"Deposit this box in the Bank ({files.Count} Pokémon)"));
            rows.AddRange(dirs.Select((d, i) => new PickItem(i, d.Name)));

            if (rows.Count == 0)
            {
                await PadMenu.ShowAsync(host, title.ToUpperInvariant(), "This folder holds nothing usable.", "OK");
                trail.Pop();
                continue;
            }

            var picked = await PickerMenu.ShowAsync(host, title.ToUpperInvariant(), rows);
            if (picked is null)
            {
                trail.Pop(); // B climbs back one shelf; leaving the root closes the center
                continue;
            }

            if (picked.Id == -1)
            {
                if (await DepositBoxAsync(host, service, bank, engine, title, files))
                    return; // done: land back wherever the user came from, box is in the Bank
                continue;
            }

            var dir = dirs[picked.Id];
            trail.Push((dir.Path, dir.Name));
        }
    }

    private static CommunityBoxService? Service;

    /// <summary>Downloads every entity in the folder and deposits them into fresh Bank boxes.</summary>
    private static async Task<bool> DepositBoxAsync(Grid host, CommunityBoxService service,
        IBankService bank, ISaveEngine engine, string boxName, IReadOnlyList<CommunityNode> files)
    {
        var boxes = (files.Count + FileBankService.SlotsPerBox - 1) / FileBankService.SlotsPerBox;
        var confirmed = await PadMenu.ConfirmAsync(host, "DEPOSIT IN THE BANK",
            $"\"{boxName}\" holds {files.Count} Pokémon. They will arrive in {(boxes == 1 ? "a fresh Bank box" : $"{boxes} fresh Bank boxes")}.",
            "Deposit");
        if (!confirmed) return false;

        var overlay = LoadingOverlay.Show(host, "DEPOSITING…",
            $"Downloading {boxName} into your Bank. Cancelling keeps what already arrived.");
        var deposited = 0;
        var skipped = 0;
        try
        {
            var box = bank.BoxCount;
            bank.AddBox();
            var slot = 0;
            for (var i = 0; i < files.Count; i++)
            {
                overlay.Cancellation.Token.ThrowIfCancellationRequested();
                overlay.Report(i, files.Count);
                byte[] data;
                try
                {
                    data = await service.DownloadAsync(files[i], overlay.Cancellation.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch { skipped++; continue; }

                var info = engine.TryDescribeEntity(data, boxName);
                if (info is null) { skipped++; continue; }

                if (slot == FileBankService.SlotsPerBox)
                {
                    box = bank.BoxCount;
                    bank.AddBox();
                    slot = 0;
                }
                var entry = bank.Add(data, info);
                bank.Move(entry.Id, box, slot++);
                deposited++;
            }
            overlay.Report(files.Count, files.Count);
        }
        catch (OperationCanceledException)
        {
            // Partial deposits stay: the Bank never loses what it was handed.
        }
        finally
        {
            overlay.Close();
        }

        var summary = deposited == 0
            ? "Nothing could be deposited - no file in that folder was a readable Pokémon."
            : $"{deposited} Pokémon arrived in the Bank{(skipped > 0 ? $" ({skipped} could not be read and were skipped)" : "")}.";
        await PadMenu.ShowAsync(host, "COLLECTION CENTER", summary, "OK");
        return deposited > 0;
    }
}
