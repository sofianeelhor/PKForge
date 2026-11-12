using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Widget;
using PKForge.App.Services;
using PKForge.App.Views;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App;

/// <summary>
/// The living Poképark widget: the residents walking, looking about, sitting and sleeping in their
/// PMD frames, under the sky of the hour. A launcher cannot run PKForge's animation loop, so each
/// update paints a short film (<see cref="LivePark"/>) that the launcher's ViewFlipper plays by itself;
/// PKForge does not run meanwhile. The film is painted again every 30 minutes (updatePeriodMillis, no
/// service, no alarm of PKForge's own) and when the park changes. It reads the Poképark widget's
/// snapshot and never writes a save.
/// </summary>
public static class LiveParkWidget
{
    /// <summary>The share of the launcher's widget bitmap allowance (1.5 screens of pixels) one film may take.</summary>
    private const double BudgetShare = 0.5;

    private static LiveParkArt? _art;

    /// <summary>
    /// The bitmap memory one widget update may carry: Android refuses an update past 1.5 screens of
    /// 32-bit pixels (AppWidgetServiceImpl's maxWidgetBitmapMemory, 6 bytes per screen pixel).
    /// </summary>
    public static long MaxWidgetBitmapMemory(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.DisplayService) is DisplayManager displays && displays.GetDisplay(Android.Views.Display.DefaultDisplay) is { } display)
            {
                using var size = new Android.Graphics.Point();
#pragma warning disable CA1422, CS0618 // the size Android itself measures the allowance with
                display.GetRealSize(size);
#pragma warning restore CA1422, CS0618
                if (size.X > 0 && size.Y > 0) return 6L * size.X * size.Y;
            }
        }
        catch (Java.Lang.Exception) { }
        var metrics = context.Resources!.DisplayMetrics!;
        return 6L * Math.Max(1, metrics.WidthPixels) * Math.Max(1, metrics.HeightPixels);
    }

    /// <summary>The park's tree and grass tuft, read once from the APK's assets (nothing of MAUI's: a cold process has them at once).</summary>
    private static LiveParkArt Art(Context context) =>
        _art ??= new LiveParkArt(WidgetFont.Asset(context, "pokepark/widget/tree.png"), WidgetFont.Asset(context, "pokepark/widget/bush.png"));

    /// <summary>
    /// The film for a widget of <paramref name="widthPx"/> x <paramref name="heightPx"/>: as many frames
    /// (12 at best) and as large a whole step as <paramref name="budgetBytes"/> allows. <paramref name="still"/>:
    /// its first frame only, one complete picture.
    /// </summary>
    /// <param name="quick">The first pass after a bind or a resize: an empty park is not said yet.</param>
    public static WidgetPicture Render(Context context, ParkWidgetSize size, int widthPx, int heightPx, long budgetBytes, DateTime local, bool still = false, bool quick = false)
    {
        var snapshot = PokeparkWidgetPublisher.Read(context);
        var (sceneW, sceneH) = LivePark.SceneSize(size, (double)widthPx / Math.Max(1, heightPx));
        // Before anything was read, or an empty park on the quick first pass: the neutral face, since a
        // host that photographs the widget once keeps that picture.
        var view = snapshot is null || quick && snapshot.Residents.Length == 0
            ? LivePark.Neutral(size, sceneW, sceneH)
            : LivePark.Compose(snapshot, size, sceneW, sceneH, local);
        var maxStep = Math.Max(1, Math.Min(widthPx / sceneW, heightPx / sceneH));
        var (frames, step) = LivePark.Budget(sceneW, sceneH, maxStep, budgetBytes);
        // An empty park, or one where nobody moves, needs one picture, not a film.
        if (view.Actors.Count == 0 || still) frames = 1;
        var sheets = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        try
        {
            var bitmaps = LiveParkPaint.Render(view, frames, step, Art(context), r => Load(r.Live), r => Load(r.Sprite), WidgetFont.Face(context), local);
            var names = view.Actors.Select(a => a.Resident.Name).ToList();
            var words = names.Count == 0 ? view.Lines.Contains(ParkWidget.Mark) ? "Living Poképark. Tap to open PKForge." : ParkWidget.EmptyLine
                : view.Phase == ParkDayPhase.Night ? $"Poképark at night. {string.Join(", ", names)} asleep."
                : $"Poképark. {string.Join(", ", names)} out on the grass.";
            return new WidgetPicture(bitmaps, LivePark.IntervalMs, words);
        }
        finally
        {
            foreach (var bitmap in sheets.Values) bitmap?.Dispose();
        }

        SKBitmap? Load(string? name)
        {
            if (name is null) return null;
            if (sheets.TryGetValue(name, out var known)) return known;
            var path = System.IO.Path.Combine(PokeparkWidgetPublisher.Folder(context), System.IO.Path.GetFileName(name));
            SKBitmap? loaded = null;
            lock (PokeparkWidgetPublisher.FileLock)
                if (File.Exists(path)) loaded = SKBitmap.Decode(path);
            return sheets[name] = loaded;
        }
    }

    internal static RemoteViews Views(Context context, AppWidgetManager manager, int id, bool quick = false)
    {
        var metrics = context.Resources!.DisplayMetrics!;
        var layout = HomeWidgetStore.Settings.LiveParkLayout;
        var (hintW, hintH) = HomeWidgetSizes.Dp(layout ?? HomeWidgetSizeHint.Wide);
        var (widthDp, heightDp) = HomeWidgets.CellDp(context, manager.GetAppWidgetOptions(id), hintW, hintH);
        int tileW = Math.Max(64, (int)(widthDp * metrics.Density)), tileH = Math.Max(64, (int)(heightDp * metrics.Density));
        var budget = (long)(MaxWidgetBitmapMemory(context) * BudgetShare);
        // The layout chosen in the Widgets menu, whatever tile the launcher gives; Auto fits the tile.
        var chosen = WidgetLayout.Pick(layout, widthDp, heightDp);
        var (dpW, dpH) = HomeWidgetSizes.Dp(chosen);
        var (naturalW, naturalH) = LivePark.SceneSize(WidgetLayout.ParkSize(chosen), (double)dpW / dpH);
        var fit = WidgetLayout.Fit(layout, widthDp, heightDp, tileW, tileH, naturalW, naturalH);
        var size = WidgetLayout.ParkSize(fit.Size);
        var (widthPx, heightPx) = fit.Fills ? (tileW, tileH) : (naturalW * fit.Step, naturalH * fit.Step);
        // The tile around a smaller layout takes its share of the allowance too.
        if (!fit.Fills) budget = (long)(budget * ((double)widthPx * heightPx / ((double)tileW * tileH)));
        // Hosts that photograph the widget (Cocoon) keep whatever it shows: one complete still picture
        // there, the film only on launchers known to play it.
        var still = !WidgetHost.Animates(context);
        var picture = Render(context, size, widthPx, heightPx, budget, DateTime.Now, still, quick);
        if (!fit.Fills) picture = HomeWidgets.InTile(picture, tileW, tileH);
        using var _ = picture;

        if (picture.Frames.Count == 1)
        {
            var single = new RemoteViews(context.PackageName, Resource.Layout.pokepark_widget);
            single.SetImageViewBitmap(Resource.Id.pokepark_frame, ToBitmap(picture.Frames[0]));
            single.SetContentDescription(Resource.Id.pokepark_frame, picture.Description);
            single.SetOnClickPendingIntent(Resource.Id.pokepark_root, HomeWidgets.OpenIntent(context, "pokepark", id));
            Android.Util.Log.Info("Poképark", $"Living park {id}: {size} {widthDp}x{heightDp}dp, one still picture of " +
                $"{picture.Frames[0].Width}x{picture.Frames[0].Height}");
            return single;
        }

        var views = new RemoteViews(context.PackageName, Resource.Layout.live_park_widget);
        views.RemoveAllViews(Resource.Id.live_park_flipper);
        long bytes = 0;
        foreach (var frame in picture.Frames)
        {
            var bitmap = ToBitmap(frame);
            bytes += bitmap.ByteCount;
            var one = new RemoteViews(context.PackageName, Resource.Layout.live_park_frame);
            one.SetImageViewBitmap(Resource.Id.live_park_frame, bitmap);
            views.AddView(Resource.Id.live_park_flipper, one);
        }
        views.SetInt(Resource.Id.live_park_flipper, "setFlipInterval", picture.IntervalMs);
        views.SetContentDescription(Resource.Id.live_park_root, picture.Description);
        views.SetOnClickPendingIntent(Resource.Id.live_park_root, HomeWidgets.OpenIntent(context, "pokepark", id));
        Android.Util.Log.Info("Poképark", $"Living park {id}: {size} {widthDp}x{heightDp}dp, {picture.Frames.Count} frames of " +
            $"{picture.Frames[0].Width}x{picture.Frames[0].Height}, {bytes / 1024} KB of {MaxWidgetBitmapMemory(context) / 1024} KB allowed");
        return views;
    }

    private static Bitmap ToBitmap(SKBitmap source)
    {
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length)!;
    }
}

/// <summary>The launcher's living Poképark widget. Tapping it opens the Poképark.</summary>
[BroadcastReceiver(Label = "Living Poképark", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/live_park_widget_info")]
public sealed class LiveParkWidgetProvider : AppWidgetProvider
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(25);

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent?.Action == WidgetHost.RetryAction)
        {
            // A delayed repaint for hosts that lay a fresh widget out late (WidgetHost.ScheduleRetries).
            var pending = GoAsync();
            _ = Task.Run(() =>
            {
                try { UpdateAll(context); }
                finally { pending?.Finish(); }
            });
            return;
        }
        base.OnReceive(context, intent);
    }

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        WidgetHost.ScheduleRetries(context, typeof(LiveParkWidgetProvider));
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                Update(context, appWidgetManager, appWidgetIds, quick: true);
                var snapshot = PokeparkWidgetPublisher.Read(context);
                if (snapshot is null || DateTimeOffset.Now - snapshot.WrittenAt > Stale)
                    await PokeparkWidgetPublisher.RefreshAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                else
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                Update(context, appWidgetManager, appWidgetIds);
            }
            finally { pending?.Finish(); }
        });
    }

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        // Cocoon sets a widget's size as it creates its view, and photographs it right after.
        WidgetHost.ScheduleRetries(context, typeof(LiveParkWidgetProvider));
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                Update(context, appWidgetManager, [appWidgetId], quick: true);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                Update(context, appWidgetManager, [appWidgetId]);
            }
            finally { pending?.Finish(); }
        });
    }

    internal static void UpdateAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        using var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(LiveParkWidgetProvider)));
        var ids = manager.GetAppWidgetIds(component);
        if (ids is { Length: > 0 }) Update(context, manager, ids);
    }

    private static void Update(Context context, AppWidgetManager manager, int[] ids, bool quick = false)
    {
        foreach (var id in ids)
        {
            try { manager.UpdateAppWidget(id, LiveParkWidget.Views(context, manager, id, quick)); }
            catch (Exception ex)
            {
                // A launcher's binder failure, or a film past the allowance, must never crash PKForge.
                Android.Util.Log.Warn("Poképark", $"Living park update failed: {ex}");
                HomeWidgets.Fallback(context, manager, id, "pokepark");
            }
        }
    }
}
