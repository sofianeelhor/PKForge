using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Downloads the complete offline sprite pack: animated Showdown sprites and HOME renders,
/// normal + shiny, for every species. Everything lands in the same permanent caches the
/// app already reads, so it is purely additive and resumable (existing files are skipped).
/// </summary>
public sealed class SpritePackDownloader(ISpriteService sprites, IGameDataService data)
{
    /// <summary>Rough size of the full pack (every form, shiny and female variant); shown before starting.</summary>
    public const string SizeHint = "~250 MB";

    public async Task RunAsync(Action<int, int> onProgress, CancellationToken cancellationToken)
    {
        // Every form, shiny and female variant the PokeAPI tree really has (SpriteCatalog's
        // generated table), deduplicated by file: forms sharing art download once.
        var looks = SpriteCatalog.AllRemoteLooks().ToList();
        var units = new List<Func<Task>>(looks.Count * 2 + data.ItemNames.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var look in looks)
        {
            if (SpriteCatalog.Showdown(look) is { } sd && seen.Add("sd/" + sd.CacheName))
                units.Add(() => WarmAsync(done => sprites.WarmShowdown(look, done)));
            if (SpriteCatalog.Home(look) is { } home && seen.Add("home/" + home.CacheName))
                units.Add(() => WarmAsync(done => sprites.WarmHome(look, done)));
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
