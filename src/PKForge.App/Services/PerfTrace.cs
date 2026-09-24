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

    /// <summary>Logs the time from now until the dispatcher next runs (≈ the layout/draw pass that follows).</summary>
    [Conditional("DEBUG"), Conditional("DIAGNOSTIC")]
    public static void UntilIdle(string name, IDispatcher dispatcher)
    {
        var watch = Stopwatch.StartNew();
        dispatcher.Dispatch(() => Log(name + ".idle", watch));
    }
}
