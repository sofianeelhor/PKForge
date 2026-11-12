using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using SkiaSharp;

namespace PKForge.App;

/// <summary>
/// What every PKForge widget does to show up on hosts that photograph the widget instead of showing
/// it live. Cocoon (rip.moth.cocoonshell) draws each AppWidgetHostView into a bitmap once per widget
/// and size (View.drawToBitmap), keeps that picture, and shows it in its tiles: whatever the widget
/// shows at that instant is what stays, and an animated ViewFlipper is caught on one frame at best.
/// So a widget is one complete still picture unless its host is known to play animations, the
/// launcher's initial layout is already a painted picture, and each update is pushed again a few times
/// after a bind or a resize, for hosts that lay the widget out late.
/// </summary>
public static class WidgetHost
{
    /// <summary>
    /// Home apps known to show widgets live, ViewFlipper animation included: AOSP Launcher3 and the
    /// launchers built on it or behaving like it. Any other host (Cocoon, iiSU, unknown frontends)
    /// gets still pictures that change on each update.
    /// </summary>
    private static readonly HashSet<string> Animating = new(StringComparer.Ordinal)
    {
        "com.android.launcher3",
        "com.google.android.apps.nexuslauncher",
        "com.sec.android.app.launcher",
        "com.teslacoilsw.launcher",
        "app.lawnchair",
        "ch.deletescape.lawnchair.plah",
        "com.miui.home",
        "com.mi.android.globallauncher",
        "com.oneplus.launcher",
        "net.oneplus.launcher",
        "com.motorola.launcher3",
        "com.huawei.android.launcher",
        "com.hihonor.android.launcher",
        "com.oppo.launcher",
        "com.bbk.launcher2",
        "com.nothing.launcher",
        "bitpit.launcher",
        "com.microsoft.launcher",
    };

    /// <summary>Whether the current home app is known to animate widgets. False when it cannot tell.</summary>
    public static bool Animates(Context context)
    {
        try
        {
            using var home = new Intent(Intent.ActionMain);
            home.AddCategory(Intent.CategoryHome);
            var resolved = context.PackageManager?.ResolveActivity(home, Android.Content.PM.PackageInfoFlags.MatchDefaultOnly);
            var package = resolved?.ActivityInfo?.PackageName;
            return package is not null && Animating.Contains(package);
        }
        catch (Java.Lang.Exception) { return false; }
    }

    /// <summary>The broadcast a provider receives for a delayed repaint (see <see cref="ScheduleRetries"/>).</summary>
    public const string RetryAction = "org.pkforge.app.WIDGET_RETRY";

    /// <summary>
    /// Asks Android to send <paramref name="provider"/> two more repaints, about 5 and 30 seconds from
    /// now, for hosts that lay a fresh widget out late. Non-waking alarms: a sleeping device is never
    /// woken for them, and a repeated request replaces the pending ones instead of adding more.
    /// </summary>
    public static void ScheduleRetries(Context context, Type provider)
    {
        try
        {
            if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
            var now = SystemClock.ElapsedRealtime();
            var slot = 0;
            foreach (var delay in new[] { 5_000L, 30_000L })
            {
                using var intent = new Intent(context, provider);
                intent.SetAction(RetryAction);
                var flags = PendingIntentFlags.UpdateCurrent;
                if (OperatingSystem.IsAndroidVersionAtLeast(23)) flags |= PendingIntentFlags.Immutable;
                // A stable code per provider and delay: a new request replaces the pending alarm.
                var code = (int)(PKForge.Domain.LivePark.Hash(provider.Name + "/retry", slot++) & 0x7fffffff);
                var pending = PendingIntent.GetBroadcast(context, code, intent, flags)!;
                alarms.SetWindow(AlarmType.ElapsedRealtime, now + delay, 2_000, pending);
            }
        }
        catch (Java.Lang.Exception ex) { Android.Util.Log.Warn("Widgets", $"Retry not scheduled: {ex.Message}"); }
    }
}

/// <summary>
/// The pixel face for widget pictures, read straight from the APK's assets: nothing of MAUI's, so it
/// is ready at once in the cold process a launcher's broadcast starts. Falls back to Skia's default face.
/// </summary>
public static class WidgetFont
{
    private static readonly object Gate = new();
    private static SKTypeface? _face;

    public static SKTypeface Face(Context context)
    {
        lock (Gate)
        {
            if (_face is not null) return _face;
            try
            {
                using var stream = context.Assets!.Open("NDS12.ttf");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                using var data = SKData.CreateCopy(bytes.ToArray());
                _face = SKTypeface.FromData(data);
            }
            catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
            {
                Android.Util.Log.Warn("Widgets", $"Pixel face unreadable: {ex.Message}");
            }
            return _face ??= SKTypeface.Default;
        }
    }

    /// <summary>A PNG or other image from the APK's assets, or null.</summary>
    public static SKBitmap? Asset(Context context, string path)
    {
        try
        {
            using var stream = context.Assets!.Open(path);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            bytes.Position = 0;
            return SKBitmap.Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception) { return null; }
    }
}
