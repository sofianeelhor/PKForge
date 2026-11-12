using System.Diagnostics;

namespace PKForge.App.Services;

/// <summary>
/// Opt-in timing lines for the summary/inspector hot paths: "PKF-PERF name ms" in logcat
/// (Console on Android lands under the DOTNET tag). Compiled out of Release builds unless
/// DIAGNOSTIC is defined, so it costs nothing in production.
/// </summary>
public static class PerfTrace
{
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Log(string name, Stopwatch watch) =>
        Console.WriteLine($"PKF-PERF {name} {watch.Elapsed.TotalMilliseconds:0.0}ms");

    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Log(string name, double ms) =>
        Console.WriteLine($"PKF-PERF {name} {ms:0.0}ms");

    /// <summary>A line about the Poképark: "PKF-PARK …" in logcat.</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Park(string line) => Console.WriteLine("PKF-PARK " + line);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Marks = new(StringComparer.Ordinal);

    /// <summary>Starts a named span that a later <see cref="Since"/> (often on another screen) closes.</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Mark(string mark) => Marks[mark] = Stopwatch.GetTimestamp();

    /// <summary>Logs the time since <see cref="Mark"/>; <paramref name="close"/> ends the span (first paint after a tap).</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Since(string mark, string name, bool close = true)
    {
        var found = close ? Marks.TryRemove(mark, out var start) : Marks.TryGetValue(mark, out start);
        if (found) Log(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>Managed heap and collection counts: "PKF-GC name heap=… gen0=… gen1=… gen2=…".</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Gc(string name)
    {
        // Leak checks only: a "force-gc" file in the cache folder collects first, so what is
        // still alive is really held.
        if (File.Exists(Path.Combine(FileSystem.CacheDirectory, "force-gc")))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        string live;
        lock (LivePages) live = string.Join(',', LivePages.GroupBy(p => p.Name).Select(g => $"{g.Key}:{g.Count(p => p.Page.IsAlive)}"));
        Console.WriteLine($"PKF-GC {name} heap={GC.GetTotalMemory(false) / 1024 / 1024}MB gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
            $"livePages={live}");
    }

    private static readonly List<(string Name, WeakReference Page)> LivePages = [];

    /// <summary>Remembers a page weakly, so <see cref="Gc"/> can say how many are still alive.</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void Track(object page)
    {
        lock (LivePages) LivePages.Add((page.GetType().Name, new WeakReference(page)));
    }

    /// <summary>Logs the time since Android started this process (cold start milestones).</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void SinceProcessStart(string name)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            Log(name, Android.OS.SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime);
#endif
    }

    /// <summary>Logs the time from now until the dispatcher next runs (≈ the layout/draw pass that follows).</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void UntilIdle(string name, IDispatcher dispatcher)
    {
        var watch = Stopwatch.StartNew();
        dispatcher.Dispatch(() => Log(name + ".idle", watch));
    }
}
