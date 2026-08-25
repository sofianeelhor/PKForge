using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Downloads the complete offline sprite pack: animated Showdown sprites and HOME renders,
/// normal + shiny, for every species. Everything lands in the same permanent caches the
/// app already reads, so it is purely additive and resumable (existing files are skipped).
/// </summary>
public sealed class SpritePackDownloader(ISpriteService sprites, IGameDataService data)
{
    /// <summary>Roughly 150 MB for the full pack; shown to the user before starting.</summary>
    public const string SizeHint = "~150 MB";

    public async Task RunAsync(Action<int, int> onProgress, CancellationToken cancellationToken)
    {
        var speciesIds = Enumerable.Range(1, data.SpeciesNames.Count - 1)
            .Where(id => data.SpeciesNames[id].Length > 0)
            .ToList();

        // 4 units per species (showdown/home × normal/shiny) + one per item icon.
        var units = new List<Func<Task>>(speciesIds.Count * 4 + data.ItemNames.Count);
        foreach (var id in speciesIds)
        {
            foreach (var shiny in new[] { false, true })
            {
                var species = id;
                var isShiny = shiny;
                units.Add(() => WarmAsync(done => sprites.WarmShowdown(species, isShiny, done)));
                units.Add(() => WarmAsync(done => sprites.WarmHome(species, isShiny, done)));
            }
        }
        foreach (var itemName in data.ItemNames.Where(n => n.Length > 0))
        {
            var name = itemName;
            units.Add(() => ItemArt.GetAsync(name));
        }

        var total = units.Count;
        var done = 0;
        var gate = new SemaphoreSlim(6);
        var tasks = units.Select(async unit =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await unit().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref done);
                if (current % 20 == 0 || current == total)
                    onProgress(current, total);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task WarmAsync(Action<Action> warm)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warm(() => loaded.TrySetResult());
        // Warm() early-returns without a callback when another caller owns the load;
        // the timeout keeps the pack moving instead of hanging on that unit.
        await Task.WhenAny(loaded.Task, Task.Delay(8000)).ConfigureAwait(false);
    }
}
