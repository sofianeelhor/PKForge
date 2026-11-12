using System.Diagnostics;

namespace PKForge.App.Services;

/// <summary>
/// Diagnostic builds only, and only once asked for with
/// <c>adb shell setprop log.tag.PKF-FRAME DEBUG</c> (then restart the app): how long each
/// painted area takes to draw a frame and how much managed memory it allocates doing so, one
/// logcat line per frame, "PKF-FRAME area ms bytes". Other builds compile it down to nothing.
/// </summary>
public static class PaintClock
{
#if DIAGNOSTIC
    private static readonly bool On = Android.Util.Log.IsLoggable("PKF-FRAME", Android.Util.LogPriority.Debug);
#endif

    public readonly record struct Probe(long Ticks, long Bytes);

    public static Probe Start()
    {
#if DIAGNOSTIC
        return On ? new Probe(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;
#else
        return default;
#endif
    }

    [Conditional("DIAGNOSTIC")]
    public static void Stop(string area, Probe probe)
    {
#if DIAGNOSTIC
        if (!On) return;
        var ms = Stopwatch.GetElapsedTime(probe.Ticks).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - probe.Bytes;
        Console.WriteLine($"PKF-FRAME {area.Replace(' ', '_')} {ms:0.000} {bytes}");
#endif
    }
}
