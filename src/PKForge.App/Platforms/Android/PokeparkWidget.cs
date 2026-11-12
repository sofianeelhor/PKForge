using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using PKForge.App.Services;
using PKForge.App.Views;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using System.Text.Json;

namespace PKForge.App;

/// <summary>
/// Keeps the Poképark widget's snapshot: the roster, the newest journal lines and one standing
/// sprite per resident, in the app's files. The widget used to show a camera crop of the live park
/// written only while the park page was open; most of the time that crop held no Pokémon at all,
/// and a widget added before the park was opened stayed empty for good (#52). The snapshot is
/// written from the park's own stores, so the widget can fill itself without the park on screen.
/// </summary>
public static class PokeparkWidgetPublisher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    internal static readonly object FileLock = new();

    internal static string Folder(Context context) => System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "pokepark-widget");
    internal static string SnapshotPath(Context context) => System.IO.Path.Combine(Folder(context), "snapshot.json");

    /// <summary>Whether a Poképark widget, still or living, is on a home screen.</summary>
    public static bool HasActiveWidgets() => HasActive(typeof(PokeparkWidgetProvider)) || HasActive(typeof(LiveParkWidgetProvider));

    public static bool HasActive(Type provider)
    {
        var context = Android.App.Application.Context;
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return false;
        using var component = new ComponentName(context, Java.Lang.Class.FromType(provider));
        return manager.GetAppWidgetIds(component) is { Length: > 0 };
    }

    /// <summary>Rewrites the snapshot from the park's stores and redraws every widget. Never throws.</summary>
    /// <param name="always">Also without a widget on a home screen (the Widgets menu's preview).</param>
    public static async Task RefreshAsync(TimeSpan spriteWait, bool always = false)
    {
        var context = Android.App.Application.Context;
        try
        {
            if (!always && !HasActiveWidgets()) return;
            if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var snapshot = await BuildAsync(context, spriteWait).ConfigureAwait(false);
                if (snapshot is not null) Write(context, snapshot.Value.Snapshot, snapshot.Value.Sprites);
            }
            finally { Gate.Release(); }
            PokeparkWidgetProvider.UpdateAll(context);
            LiveParkWidgetProvider.UpdateAll(context);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Poképark", $"Widget refresh failed: {ex.Message}");
        }
    }

    private static async Task<(ParkWidgetSnapshot Snapshot, Dictionary<string, byte[]> Sprites)?> BuildAsync(Context context, TimeSpan spriteWait)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services?.GetService<PokeparkService>() is not { } park || services.GetService<PokeparkSpriteService>() is not { } walking) return null;
        var roster = await Task.Run(park.LoadRoster).ConfigureAwait(false);
        var moments = services.GetService<PokeparkSocialService>() is { } social
            ? await Task.Run(() => social.Journal(6).Select(e => e.Text).ToArray()).ConfigureAwait(false)
            : [];

        // The sprites come from the park's own sheets (cached on disk once the park has shown them).
        var waits = new List<Task>();
        foreach (var mon in roster)
        {
            if (Frame(walking, mon) is not null) continue;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            walking.Warm(mon.Species, mon.Form, mon.Shiny, () => loaded.TrySetResult());
            waits.Add(loaded.Task);
        }
        if (waits.Count > 0) await Task.WhenAny(Task.WhenAll(waits), Task.Delay(spriteWait)).ConfigureAwait(false);

        // The living park's poses: asking for them starts the loads of the ones not on disk yet
        // (a sit, a sleep); they get the rest of the wait, and a pose still loading stands idle.
        var deadline = DateTime.UtcNow + spriteWait;
        bool Pending() => roster.Any(mon => LivePark.Clips.Any(c => walking.GetClip(mon.Species, mon.Form, mon.Shiny, c.Pose, c.Direction)?.Pending == true));
        while (Pending() && DateTime.UtcNow < deadline)
            await Task.Delay(150).ConfigureAwait(false);

        var sprites = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var lives = new Dictionary<string, ParkLiveClip[]?>(StringComparer.Ordinal);
        var residents = new List<ParkWidgetResident>(roster.Count);
        foreach (var mon in roster)
        {
            string? file = null;
            var key = $"{mon.Species}-{mon.Form}-{(mon.Shiny ? "s" : "n")}";
            if (Frame(walking, mon) is { } frame && Standing(frame) is { } png)
            {
                file = key + ".png";
                sprites[file] = png;
            }
            // Two residents of one species share their sheet.
            var live = key + "-live.png";
            if (!lives.TryGetValue(live, out var clips)) lives[live] = clips = Live(walking, mon, live, sprites);
            residents.Add(new(mon.Id, mon.Name, mon.Species, mon.Form, mon.Shiny, file, clips is null ? null : live, clips,
                clips is null ? 1 : walking.GetScale(mon.Species, mon.Form, mon.Shiny)));
        }
        return (new ParkWidgetSnapshot([.. residents], moments, DateTimeOffset.Now), sprites);
    }

    /// <summary>The resident's PMD walk, idle, sit and sleep frames cut into its living-park sheet (PNG, into <paramref name="sprites"/>).</summary>
    private static ParkLiveClip[]? Live(PokeparkSpriteService walking, ParkPokemon mon, string file, Dictionary<string, byte[]> sprites)
    {
        try
        {
            var sources = new List<LiveParkSource>();
            foreach (var (clip, pose, direction) in LivePark.Clips)
                if (walking.GetClip(mon.Species, mon.Form, mon.Shiny, pose, direction) is { } found)
                    sources.Add(new(clip, found.Bitmap, found.Sources, found.Feet, found.Durations));
            if (sources.Count == 0 || LiveParkSheet.Build(sources) is not { } built) return null;
            using (built.Sheet)
            {
                using var image = SKImage.FromBitmap(built.Sheet);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null) return null;
                sprites[file] = data.ToArray();
            }
            return built.Clips;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Android.Util.Log.Warn("Poképark", $"Living sheet for {mon.Name} failed: {ex.Message}");
            return null;
        }
    }

    private static ParkSpriteFrame? Frame(PokeparkSpriteService walking, ParkPokemon mon) =>
        walking.GetFrame(mon.Species, mon.Form, mon.Shiny, ParkPose.Idle, 0, 0);

    /// <summary>The first idle frame facing the viewer, trimmed to its opaque pixels, as PNG.</summary>
    private static byte[]? Standing(ParkSpriteFrame frame)
    {
        var s = frame.Source;
        int left = (int)s.Left, top = (int)s.Top, right = (int)s.Right, bottom = (int)s.Bottom;
        int minX = right, minY = bottom, maxX = -1, maxY = -1;
        lock (frame.Bitmap)
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                    if (frame.Bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
        if (maxX < 0) return null;
        using var cut = new SKBitmap();
        if (!frame.Bitmap.ExtractSubset(cut, SKRectI.Create(minX, minY, maxX - minX + 1, maxY - minY + 1))) return null;
        using var image = SKImage.FromBitmap(cut);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    private static void Write(Context context, ParkWidgetSnapshot snapshot, Dictionary<string, byte[]> sprites)
    {
        lock (FileLock)
        {
            var folder = Folder(context);
            Directory.CreateDirectory(folder);
            foreach (var (name, png) in sprites) File.WriteAllBytes(System.IO.Path.Combine(folder, name), png);
            var temporary = SnapshotPath(context) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temporary, SnapshotPath(context), true);
            // Sprites of residents who left go with them.
            foreach (var old in Directory.GetFiles(folder, "*.png"))
                if (!sprites.ContainsKey(System.IO.Path.GetFileName(old))) File.Delete(old);
        }
    }

    internal static ParkWidgetSnapshot? Read(Context context)
    {
        lock (FileLock)
        {
            try
            {
                var path = SnapshotPath(context);
                return File.Exists(path) ? JsonSerializer.Deserialize<ParkWidgetSnapshot>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Android.Util.Log.Warn("Poképark", $"Widget snapshot unreadable: {ex.Message}");
                return null;
            }
        }
    }
}

/// <summary>
/// The launcher's Poképark widget. Android asks for an update every 30 minutes (updatePeriodMillis,
/// which never wakes a sleeping device) and on resize; the park asks when it changes. Each update
/// refreshes the snapshot when it is stale, then paints one bitmap per widget at its own size.
/// </summary>
[BroadcastReceiver(Label = "Poképark", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/pokepark_widget_info")]
public sealed class PokeparkWidgetProvider : AppWidgetProvider
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
        WidgetHost.ScheduleRetries(context, typeof(PokeparkWidgetProvider));
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

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager,
        int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        // Cocoon sets a widget's size as it creates its view, and photographs it right after.
        WidgetHost.ScheduleRetries(context, typeof(PokeparkWidgetProvider));
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
        using var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(PokeparkWidgetProvider)));
        var ids = manager.GetAppWidgetIds(component);
        if (ids is { Length: > 0 }) Update(context, manager, ids);
    }

    /// <param name="quick">The first pass after a bind or a resize: an empty park is not said yet.</param>
    private static void Update(Context context, AppWidgetManager manager, int[] ids, bool quick = false)
    {
        var snapshot = PokeparkWidgetPublisher.Read(context);
        var layout = HomeWidgetStore.Settings.ParkLayout;
        var sprites = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        try
        {
            foreach (var id in ids)
            {
                try { manager.UpdateAppWidget(id, Views(context, manager, id, snapshot, layout, quick, sprites)); }
                catch (Exception ex)
                {
                    // A launcher's binder/reconstruction failure must never crash the main app.
                    Android.Util.Log.Warn("Poképark", $"Widget update failed: {ex}");
                    HomeWidgets.Fallback(context, manager, id, "pokepark");
                }
            }
        }
        catch (Exception ex) { Android.Util.Log.Warn("Poképark", $"Widget update failed: {ex.Message}"); }
        finally
        {
            foreach (var bitmap in sprites.Values) bitmap?.Dispose();
        }
    }

    private static RemoteViews Views(Context context, AppWidgetManager manager, int id, ParkWidgetSnapshot? snapshot,
        HomeWidgetSizeHint? layout, bool quick, Dictionary<string, SKBitmap?> sprites)
    {
        var metrics = context.Resources!.DisplayMetrics!;
        var options = manager.GetAppWidgetOptions(id);
        // Portrait launchers lay it out at the min width by the max height, landscape ones at the
        // max width by the min height; a fresh widget may report none.
        var (hintW, hintH) = HomeWidgetSizes.Dp(layout ?? HomeWidgetSizeHint.Wide);
        var (widthDp, heightDp) = HomeWidgets.CellDp(context, options, hintW, hintH);
        // Kept small: RemoteViews bitmaps cross a binder and count against the launcher's memory.
        var scale = Math.Min(metrics.Density, 720f / Math.Max(widthDp, heightDp));
        int tileW = Math.Max(64, (int)(widthDp * scale)), tileH = Math.Max(64, (int)(heightDp * scale));
        // The layout chosen in the Widgets menu, whatever tile the launcher gives; Auto fits the tile.
        var (naturalW, naturalH) = HomeWidgetPaint.NaturalSize(null, WidgetLayout.Pick(layout, widthDp, heightDp));
        var fit = WidgetLayout.Fit(layout, widthDp, heightDp, tileW, tileH, naturalW, naturalH);
        var (widthPx, heightPx) = fit.Fills ? (tileW, tileH) : (naturalW * fit.Step, naturalH * fit.Step);
        var size = WidgetLayout.ParkSize(fit.Size);
        var step = ParkWidgetPaint.StepFor(size, widthPx, heightPx);
        int sceneW = (widthPx + step - 1) / step, sceneH = (heightPx + step - 1) / step;
        // Before anything was read, or an empty park on the quick first pass: the neutral face, since a
        // host that photographs the widget once keeps that picture.
        var view = snapshot is null || quick && snapshot.Residents.Length == 0
            ? ParkWidget.Neutral(size, sceneW, sceneH)
            : ParkWidget.Compose(snapshot, size, sceneW, sceneH, DateTime.Now);

        using var painted = ParkWidgetPaint.Render(view, step, Sprite, WidgetFont.Face(context));
        using var placed = fit.Fills ? null : HomeWidgetPaint.InTile(painted, tileW, tileH);
        var picture = placed ?? painted;
        using var image = SKImage.FromBitmap(picture);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        using var bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);

        var views = new RemoteViews(context.PackageName, Resource.Layout.pokepark_widget);
        views.SetImageViewBitmap(Resource.Id.pokepark_frame, bitmap);
        views.SetContentDescription(Resource.Id.pokepark_frame, string.Join(". ", view.Placements.Select(p => p.Resident.Name).Concat(view.Lines)));
        using var pending = HomeWidgets.OpenIntent(context, "pokepark", id);
        views.SetOnClickPendingIntent(Resource.Id.pokepark_root, pending);
        return views;

        SKBitmap? Sprite(ParkWidgetResident resident)
        {
            if (resident.Sprite is not { } name) return null;
            if (sprites.TryGetValue(name, out var known)) return known;
            var path = System.IO.Path.Combine(PokeparkWidgetPublisher.Folder(context), System.IO.Path.GetFileName(name));
            SKBitmap? loaded = null;
            lock (PokeparkWidgetPublisher.FileLock)
                if (File.Exists(path)) loaded = SKBitmap.Decode(path);
            return sprites[name] = loaded;
        }
    }
}
