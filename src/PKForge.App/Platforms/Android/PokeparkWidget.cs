using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.IO.Compression;

namespace PKForge.App;

/// <summary>Publishes a small, persistent animation to Android's launcher widget host.</summary>
public static class PokeparkWidgetPublisher
{
    // Render the source frame at 2x the old widget resolution so launcher
    // scaling has enough detail on large and high-density home screens.
    public const int FrameWidth = 640;
    public const int FrameHeight = 360;
    public const int FrameCount = 8;
    public const int FrameIntervalMilliseconds = 400;

    internal static readonly object SnapshotLock = new();
    internal static string SnapshotPath(Context context) =>
        System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "pokepark-widget.zip");

    /// <summary>Accepts up to eight PNG frames. An empty list clears the park.</summary>
    public static void Publish(IReadOnlyList<byte[]> pngFrames)
    {
        try { PublishCore(pngFrames); }
        catch (Java.Lang.Exception ex)
        {
            // Surface native decoder / launcher failures through the page's recoverable
            // widget-save error path, while keeping the previous snapshot intact.
            throw new IOException("Android could not publish the Poképark widget.", ex);
        }
    }

    private static void PublishCore(IReadOnlyList<byte[]> pngFrames)
    {
        var context = Android.App.Application.Context;
        lock (SnapshotLock)
        {
            var path = SnapshotPath(context);
            var temporary = path + ".tmp";
            try
            {
                // A killed publisher may leave an incomplete temporary archive behind.
                if (File.Exists(temporary)) File.Delete(temporary);
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    for (var i = 0; i < Math.Min(pngFrames.Count, FrameCount); i++)
                    {
                        using var source = BitmapFactory.DecodeByteArray(pngFrames[i], 0, pngFrames[i].Length)
                            ?? throw new InvalidDataException("Invalid Poképark widget frame.");
                        // Android limits RemoteViews bitmap memory to 1.5 screenfuls.
                        // Keep all frames together below one screenful, even on small devices.
                        var metrics = context.Resources!.DisplayMetrics!;
                        var pixelBudget = Math.Max(1d, (double)metrics.WidthPixels * metrics.HeightPixels);
                        var count = Math.Max(1, Math.Min(pngFrames.Count, FrameCount));
                        var memoryScale = Math.Sqrt(pixelBudget / (count * (double)source.Width * source.Height));
                        var scale = Math.Min(1d, Math.Min(memoryScale,
                            Math.Min((double)FrameWidth / source.Width, (double)FrameHeight / source.Height)));
                        using var frame = Bitmap.CreateScaledBitmap(source,
                            Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), true);
                        using var output = archive.CreateEntry($"{i}.png", CompressionLevel.NoCompression).Open();
                        if (!frame.Compress(Bitmap.CompressFormat.Png!, 100, output))
                            throw new IOException("Could not encode Poképark widget frame.");
                    }
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        PokeparkWidgetProvider.UpdateAll(context);
    }
}

[BroadcastReceiver(Label = "Poképark", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/pokepark_widget_info")]
public sealed class PokeparkWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null && appWidgetManager is not null && appWidgetIds is not null)
            Update(context, appWidgetManager, appWidgetIds);
    }

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager,
        int appWidgetId, Bundle? newOptions)
    {
        if (context is not null && appWidgetManager is not null)
            Update(context, appWidgetManager, [appWidgetId]);
    }

    internal static void UpdateAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        using var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(PokeparkWidgetProvider)));
        var ids = manager.GetAppWidgetIds(component);
        if (ids is { Length: > 0 }) Update(context, manager, ids);
    }

    private static void Update(Context context, AppWidgetManager manager, int[] ids)
    {
        var frames = new List<Bitmap>();
        try
        {
            lock (PokeparkWidgetPublisher.SnapshotLock)
            {
                var path = PokeparkWidgetPublisher.SnapshotPath(context);
                if (File.Exists(path))
                {
                    try
                    {
                        using var archive = ZipFile.OpenRead(path);
                        foreach (var entry in archive.Entries.Take(PokeparkWidgetPublisher.FrameCount))
                        {
                            using var stream = entry.Open();
                            if (BitmapFactory.DecodeStream(stream) is { } bitmap) frames.Add(bitmap);
                        }
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Poképark", $"Widget snapshot unavailable: {ex.Message}");
                    }
                }
            }
            using var views = new RemoteViews(context.PackageName, Resource.Layout.pokepark_widget);
            views.RemoveAllViews(Resource.Id.pokepark_frames);
            views.SetInt(Resource.Id.pokepark_frames, "setFlipInterval", PokeparkWidgetPublisher.FrameIntervalMilliseconds);
            foreach (var bitmap in frames)
            {
                // The launcher's allocated cell rectangle is not necessarily 16:9.
                // The frame ImageView uses centerCrop: one uniform scale fills that
                // rectangle without stretching sprites or adding an inner border.
                using var child = new RemoteViews(context.PackageName, Resource.Layout.pokepark_widget_frame);
                child.SetImageViewBitmap(Resource.Id.pokepark_frame, bitmap);
                views.AddView(Resource.Id.pokepark_frames, child);
            }
            views.SetViewVisibility(Resource.Id.pokepark_empty, frames.Count == 0 ? ViewStates.Visible : ViewStates.Gone);
            views.SetViewVisibility(Resource.Id.pokepark_frames, frames.Count == 0 ? ViewStates.Gone : ViewStates.Visible);
            using var intent = new Intent(context, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            intent.PutExtra("pokepark", true);
            var pendingFlags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(23)) pendingFlags |= PendingIntentFlags.Immutable;
            using var pending = PendingIntent.GetActivity(context, 7041, intent, pendingFlags);
            views.SetOnClickPendingIntent(Resource.Id.pokepark_root, pending);
            manager.UpdateAppWidget(ids, views);
        }
        catch (Exception ex)
        {
            // A launcher's binder/reconstruction failure must never crash the main app.
            Android.Util.Log.Warn("Poképark", $"Widget update failed: {ex.Message}");
        }
        finally
        {
            foreach (var bitmap in frames) bitmap.Dispose();
        }
    }
}
