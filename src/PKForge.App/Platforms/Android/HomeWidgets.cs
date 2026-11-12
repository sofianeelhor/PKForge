using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.App.Views;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using System.Text.Json;

namespace PKForge.App;

/// <summary>A painted widget: its frames (the launcher flips through them), how long each stays, and what it says.</summary>
public sealed class WidgetPicture(IReadOnlyList<SKBitmap> frames, int intervalMs, string description) : IDisposable
{
    public IReadOnlyList<SKBitmap> Frames { get; } = frames;
    public int IntervalMs { get; } = intervalMs;
    public string Description { get; } = description;

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
}

/// <summary>The last save opened in PKForge: what "Open last save" and the egg widget follow.</summary>
public sealed record LastSave(string DocumentId, string Name);

/// <summary>
/// The three new home-screen widgets' files, in the app's own folder (never in a save): the
/// choices made in the Widgets menu, each save's eggs as last read, the last save opened, the
/// day's first pedometer reading, and the sprites the widgets draw (written from the app, so a
/// widget can paint itself while PKForge is closed).
/// </summary>
public static class HomeWidgetStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    internal static readonly object FileLock = new();

    public static string Folder => System.IO.Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "home-widgets");
    private static string PathOf(string name) => System.IO.Path.Combine(Folder, name);

    private static T? Read<T>(string name) where T : class
    {
        lock (FileLock)
        {
            try
            {
                var path = PathOf(name);
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Android.Util.Log.Warn("Widgets", $"{name} unreadable: {ex.Message}");
                return null;
            }
        }
    }

    private static void Write<T>(string name, T value)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            var temporary = PathOf(name) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, PathOf(name), true);
        }
    }

    /// <summary>The Widgets menu's choices, read fresh each time (a widget update may run in a cold process).</summary>
    public static HomeWidgetSettings Settings
    {
        get
        {
            lock (FileLock)
            {
                try
                {
                    var path = PathOf("settings.json");
                    return File.Exists(path) ? HomeWidgetSettings.FromJson(File.ReadAllText(path)) : new();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Android.Util.Log.Warn("Widgets", $"settings.json unreadable: {ex.Message}");
                    return new();
                }
            }
        }
    }

    public static void Save(HomeWidgetSettings settings)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            var temporary = PathOf("settings.json") + ".tmp";
            File.WriteAllText(temporary, settings.ToJson());
            File.Move(temporary, PathOf("settings.json"), true);
        }
    }

    public static Dictionary<string, EggWidgetSnapshot> Eggs => Read<Dictionary<string, EggWidgetSnapshot>>("eggs.json") ?? [];

    /// <summary>Keeps one save's eggs (the twelve saves read most recently).</summary>
    public static void Save(EggWidgetSnapshot snapshot)
    {
        var all = Eggs;
        all[snapshot.DocumentId] = snapshot;
        Write("eggs.json", all.Values.OrderByDescending(s => s.WrittenAt).Take(12).ToDictionary(s => s.DocumentId));
    }

    public static LastSave? Last => Read<LastSave>("last-save.json");
    public static void Save(LastSave last) => Write("last-save.json", last);

    public static WalkerDayCount? WalkerDay => Read<WalkerDayCount>("walker-day.json");
    public static void Save(WalkerDayCount count) => Write("walker-day.json", count);

    /// <summary>The egg widget's save: the one picked in the menu, or the last one opened.</summary>
    public static EggWidgetSnapshot? EggSnapshot(HomeWidgetSettings settings)
    {
        var id = settings.EggSave ?? Last?.DocumentId;
        return id is not null && Eggs.TryGetValue(id, out var snapshot) ? snapshot : null;
    }

    public static string SpriteFile(int species, int form, bool shiny, string kind) =>
        PathOf($"{kind}-{species}-{form}-{(shiny ? "s" : "n")}.png");

    public static SKBitmap? LoadSprite(string path)
    {
        lock (FileLock)
            return File.Exists(path) ? SKBitmap.Decode(path) : null;
    }

    public static void SaveSprite(string path, SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(path, data.ToArray());
        }
    }
}

/// <summary>
/// Paints and refreshes the Pocket Pokéwalker, Partner and egg widgets. Battery: no service and no
/// alarm of PKForge's own; the launcher asks every 30 minutes (updatePeriodMillis) and PKForge
/// pushes a picture when it changes the data. The step counter is read once per update, only when
/// the player allowed it. Nothing here writes a save, or the Pokéwalker's walk file.
/// </summary>
public static class HomeWidgets
{
    public const string OpenExtra = "pkforge.open";

    public static Type ProviderOf(HomeWidgetKind kind) => kind switch
    {
        HomeWidgetKind.Walker => typeof(WalkerWidgetProvider),
        HomeWidgetKind.Partner => typeof(PartnerWidgetProvider),
        _ => typeof(EggWidgetProvider),
    };

    public static int[] IdsOf(Context context, Type provider)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return [];
        using var component = new ComponentName(context, Java.Lang.Class.FromType(provider));
        return manager.GetAppWidgetIds(component) ?? [];
    }

    public static bool HasAny(HomeWidgetKind kind) => IdsOf(Android.App.Application.Context, ProviderOf(kind)).Length > 0;

    /// <summary>Repaints every widget of that kind, off the UI thread. Never throws.</summary>
    public static void Refresh(HomeWidgetKind kind) => _ = Task.Run(async () =>
    {
        try
        {
            var context = Android.App.Application.Context;
            var ids = IdsOf(context, ProviderOf(kind));
            if (ids.Length > 0) await UpdateAsync(context, AppWidgetManager.GetInstance(context)!, ids, kind).ConfigureAwait(false);
        }
        catch (Exception ex) { Android.Util.Log.Warn("Widgets", $"Refresh failed: {ex.Message}"); }
    });

    /// <summary>
    /// Repaints every widget of that slot now, and again a few seconds later for hosts that photograph
    /// a widget only once it has settled (<see cref="WidgetHost.ScheduleRetries"/>). Never throws.
    /// </summary>
    public static void Refresh(WidgetSlot slot)
    {
        try
        {
            var context = Android.App.Application.Context;
            var provider = ProviderOf(slot);
            if (IdsOf(context, provider).Length == 0) return;
            WidgetHost.ScheduleRetries(context, provider);
            switch (slot)
            {
                case WidgetSlot.Park: _ = Task.Run(() => Guard(() => PokeparkWidgetProvider.UpdateAll(context))); break;
                case WidgetSlot.LivePark: _ = Task.Run(() => Guard(() => LiveParkWidgetProvider.UpdateAll(context))); break;
                default: Refresh(slot switch { WidgetSlot.Walker => HomeWidgetKind.Walker, WidgetSlot.Partner => HomeWidgetKind.Partner, _ => HomeWidgetKind.Egg }); break;
            }
        }
        catch (Java.Lang.Exception ex) { Android.Util.Log.Warn("Widgets", $"Refresh failed: {ex.Message}"); }

        static void Guard(Action paint)
        {
            try { paint(); }
            catch (Exception ex) { Android.Util.Log.Warn("Widgets", $"Refresh failed: {ex.Message}"); }
        }
    }

    public static Type ProviderOf(WidgetSlot slot) => slot switch
    {
        WidgetSlot.Park => typeof(PokeparkWidgetProvider),
        WidgetSlot.LivePark => typeof(LiveParkWidgetProvider),
        WidgetSlot.Walker => typeof(WalkerWidgetProvider),
        WidgetSlot.Partner => typeof(PartnerWidgetProvider),
        _ => typeof(EggWidgetProvider),
    };

    /// <summary>The layout the launcher's tile gives the first widget of that slot on a home screen, or null.</summary>
    public static HomeWidgetSizeHint? PlacedLayout(WidgetSlot slot)
    {
        try
        {
            var context = Android.App.Application.Context;
            var ids = IdsOf(context, ProviderOf(slot));
            if (ids.Length == 0 || AppWidgetManager.GetInstance(context) is not { } manager) return null;
            var (w, h) = CellDp(context, manager.GetAppWidgetOptions(ids[0]), 0, 0);
            return w > 0 && h > 0 ? HomeWidgetSizes.For(w, h) : null;
        }
        catch (Java.Lang.Exception) { return null; }
    }

    /// <summary>
    /// The picture placed in a tile of <paramref name="tileWidth"/> x <paramref name="tileHeight"/> (any
    /// unit): each frame centred on the widget's own background, at its own pixel scale, never stretched.
    /// </summary>
    public static WidgetPicture InTile(WidgetPicture picture, int tileWidth, int tileHeight)
    {
        if (picture.Frames.Count == 0) return picture;
        var first = picture.Frames[0];
        var (w, h, _, _) = WidgetLayout.Canvas(first.Width, first.Height, tileWidth, tileHeight);
        if (w == first.Width && h == first.Height) return picture;
        var placed = picture.Frames.Select(f => HomeWidgetPaint.InTile(f, tileWidth, tileHeight)).ToArray();
        var fitted = new WidgetPicture(placed, picture.IntervalMs, picture.Description);
        picture.Dispose();
        return fitted;
    }

    public static void RefreshAll()
    {
        Refresh(HomeWidgetKind.Walker);
        Refresh(HomeWidgetKind.Partner);
        Refresh(HomeWidgetKind.Egg);
    }

    private static readonly SemaphoreSlim[] Painting = [new(1, 1), new(1, 1), new(1, 1)];
    private static readonly int[] Turns = new int[3];

    /// <summary>
    /// Paints the widgets <paramref name="ids"/> of that kind. <paramref name="quick"/> (the first
    /// picture after a bind or a resize) uses only what is already on the device: the sprites PKForge
    /// saved, no step-counter wait, no sprite download, so it lands within a moment even in the cold
    /// process a launcher's broadcast starts. The later passes read and fetch everything.
    /// </summary>
    internal static async Task UpdateAsync(Context context, AppWidgetManager manager, int[] ids, HomeWidgetKind kind, bool quick = false)
    {
        var gate = Painting[(int)kind % Painting.Length];
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var metrics = context.Resources!.DisplayMetrics!;
            // Hosts that photograph the widget (Cocoon) keep whatever it shows: one complete still
            // picture, the next frame on each update, unless the host is known to play the frames.
            var animate = WidgetHost.Animates(context);
            var still = animate ? (int?)null : Turns[(int)kind % Turns.Length]++;
            foreach (var id in ids)
            {
                try
                {
                    var options = manager.GetAppWidgetOptions(id);
                    var settings = HomeWidgetStore.Settings;
                    var chosen = settings.LayoutOf(HomeWidgetSettings.SlotOf(kind));
                    var (hintW, hintH) = HomeWidgetSizes.Dp(chosen ?? HomeWidgetSizeHint.Small);
                    var (widthDp, heightDp) = CellDp(context, options, hintW, hintH);
                    // Every frame crosses a binder and counts against the launcher's bitmap memory (about
                    // 1.5 screens): all frames together stay under two million pixels (8 MB).
                    var frames = still is not null ? 1 : kind == HomeWidgetKind.Egg ? 4 : 2;
                    var scale = (float)Math.Min(metrics.Density, Math.Sqrt(2_000_000.0 / frames / Math.Max(1, widthDp * heightDp)));
                    int tileW = Math.Max(64, (int)(widthDp * scale)), tileH = Math.Max(64, (int)(heightDp * scale));
                    // The layout chosen in the Widgets menu, whatever tile the launcher gives; Auto fits the tile.
                    var (naturalW, naturalH) = HomeWidgetPaint.NaturalSize(kind, WidgetLayout.Pick(chosen, widthDp, heightDp));
                    var fit = WidgetLayout.Fit(chosen, widthDp, heightDp, tileW, tileH, naturalW, naturalH);
                    var (widthPx, heightPx) = fit.Fills ? (tileW, tileH) : (naturalW * fit.Step, naturalH * fit.Step);
                    var picture = await RenderAsync(context, kind, fit.Size, widthPx, heightPx, settings, live: true, still, fetch: !quick, neutralIfEmpty: quick).ConfigureAwait(false);
                    if (!fit.Fills) picture = InTile(picture, tileW, tileH);
                    using (picture) manager.UpdateAppWidget(id, Views(context, kind, picture, id));
                }
                catch (Exception ex)
                {
                    // A launcher's binder or a missing sprite must never crash PKForge, nor leave a
                    // stale picture: the widget says to tap it, and the tap still leads to its screen.
                    Android.Util.Log.Warn("Widgets", $"{kind} widget update failed: {ex}");
                    Fallback(context, manager, id, kind switch { HomeWidgetKind.Walker => "walker", HomeWidgetKind.Partner => "partner", _ => "egg" });
                }
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The widget's size in dp as the launcher lays it out now. Launchers report a range: in portrait
    /// the cell is the min width by the max height, in landscape the max width by the min height (the
    /// Thor's main screen, and Launcher3 on a landscape phone). A fresh widget may report none.
    /// </summary>
    public static (int WidthDp, int HeightDp) CellDp(Context context, Bundle? options, int fallbackW, int fallbackH)
    {
        var landscape = context.Resources?.Configuration?.Orientation == Android.Content.Res.Orientation.Landscape;
        var w = options?.GetInt(landscape ? AppWidgetManager.OptionAppwidgetMaxWidth : AppWidgetManager.OptionAppwidgetMinWidth) ?? 0;
        var h = options?.GetInt(landscape ? AppWidgetManager.OptionAppwidgetMinHeight : AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0;
        return (w > 0 ? w : fallbackW, h > 0 ? h : fallbackH);
    }

    private static RemoteViews Views(Context context, HomeWidgetKind kind, WidgetPicture picture, int id)
    {
        RemoteViews views;
        if (picture.Frames.Count == 1)
        {
            // One complete picture in a plain ImageView: what a photographing host keeps is the real state.
            views = new RemoteViews(context.PackageName, Resource.Layout.home_widget_1);
            views.SetImageViewBitmap(Resource.Id.home_widget_frame0, ToBitmap(picture.Frames[0]));
        }
        else
        {
            var four = picture.Frames.Count > 2;
            views = new RemoteViews(context.PackageName, four ? Resource.Layout.home_widget_4 : Resource.Layout.home_widget_2);
            int[] frames = [Resource.Id.home_widget_frame0, Resource.Id.home_widget_frame1, Resource.Id.home_widget_frame2, Resource.Id.home_widget_frame3];
            var made = new Dictionary<SKBitmap, Bitmap>();
            for (var i = 0; i < (four ? 4 : 2); i++)
            {
                var source = picture.Frames[Math.Min(i, picture.Frames.Count - 1)];
                // The same frame twice is the same Bitmap: RemoteViews sends it once.
                if (!made.TryGetValue(source, out var bitmap)) made[source] = bitmap = ToBitmap(source);
                views.SetImageViewBitmap(frames[i], bitmap);
            }
            views.SetInt(Resource.Id.home_widget_flipper, "setFlipInterval", picture.IntervalMs);
        }
        views.SetContentDescription(Resource.Id.home_widget_root, picture.Description);
        views.SetOnClickPendingIntent(Resource.Id.home_widget_root, OpenIntent(context, kind switch
        {
            HomeWidgetKind.Walker => "walker",
            HomeWidgetKind.Partner => "partner",
            _ => "egg",
        }, id));
        return views;
    }

    /// <summary>The plain "tap to open" face of a widget whose picture could not be painted. Never throws.</summary>
    public static void Fallback(Context context, AppWidgetManager manager, int id, string target)
    {
        try
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.home_widget_loading);
            views.SetTextViewText(Resource.Id.home_widget_loading_text, context.GetString(Resource.String.home_widget_failed));
            views.SetOnClickPendingIntent(Resource.Id.home_widget_loading_root, OpenIntent(context, target, id));
            manager.UpdateAppWidget(id, views);
        }
        catch (Exception ex) { Android.Util.Log.Warn("Widgets", $"Fallback failed: {ex.Message}"); }
    }

    /// <summary>
    /// The tap of one widget: opens PKForge at <paramref name="target"/>. Android tells PendingIntents
    /// apart by request code and intent data, never by extras, so each widget id and target gets
    /// its own data URI and code: two widgets can never swap their links, nor an update keep a stale one.
    /// The existing task comes to the front (no second PKForge), and MainActivity hears it in OnNewIntent.
    /// </summary>
    public static PendingIntent OpenIntent(Context context, string target, int widgetId)
    {
        using var intent = new Intent(context, typeof(MainActivity));
        intent.SetData(Android.Net.Uri.Parse($"pkforge-widget://{target}/{widgetId}"));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra(OpenExtra, target);
        var flags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) flags |= PendingIntentFlags.Immutable;
        var code = (int)(LivePark.Hash(target, widgetId) & 0x7fffffff);
        // PKForge's own screen is the default display (the Thor's top panel), wherever the launcher
        // showing the widget sits: the lower panel is its second screen. MainActivity puts it back
        // there too when a launcher's own options win.
        Bundle? options = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && ActivityOptions.MakeBasic() is { } basic)
        {
            basic.SetLaunchDisplayId(Android.Views.Display.DefaultDisplay);
            options = basic.ToBundle();
        }
        return PendingIntent.GetActivity(context, code, intent, flags, options)!;
    }

    private static Bitmap ToBitmap(SKBitmap source)
    {
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length)!;
    }

    // ── Painting ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The widget's frames at that pixel size. <paramref name="live"/> (a launcher update) reads the
    /// step counter once and keeps the day's count; the Widgets menu's preview does neither.
    /// <paramref name="still"/>: only that frame (modulo the widget's frame count), one picture.
    /// Without <paramref name="fetch"/>, nothing is waited for: sprites come from PKForge's saved
    /// files only, and the step counter is not read. <paramref name="neutralIfEmpty"/> (the quick first
    /// pass): an empty state ("no walk", "no partner", "no egg") is not said yet, only the widget's
    /// neutral face, since a host that photographs the widget once keeps it after it stops being true.
    /// </summary>
    public static async Task<WidgetPicture> RenderAsync(Context context, HomeWidgetKind kind, HomeWidgetSizeHint size,
        int widthPx, int heightPx, HomeWidgetSettings settings, bool live, int? still = null, bool fetch = true, bool neutralIfEmpty = false)
    {
        var face = WidgetFont.Face(context);
        IEnumerable<int> Frames(int count) => still is { } s ? [s % count] : Enumerable.Range(0, count);
        WidgetPicture Neutral()
        {
            using var shell = kind == HomeWidgetKind.Walker ? WalkerShell(context) : null;
            var name = kind switch { HomeWidgetKind.Walker => "Pokéwalker", HomeWidgetKind.Partner => "Partner Pokémon", _ => "Day Care egg" };
            return new WidgetPicture([HomeWidgetPaint.RenderNeutral(kind, size, widthPx, heightPx, shell, face)], 4000, $"{name}. Tap to open PKForge.");
        }
        switch (kind)
        {
            case HomeWidgetKind.Walker:
            {
                var view = await WalkerViewAsync(live && fetch).ConfigureAwait(false);
                if (neutralIfEmpty && !view.Walking) return Neutral();
                var sprite = view.Walking ? await WalkerSpriteAsync(view, live, fetch).ConfigureAwait(false) : null;
                using var shell = WalkerShell(context);
                var frames = Frames(2).Select(f =>
                    HomeWidgetPaint.RenderWalker(view, size, widthPx, heightPx, f, sprite, shell, settings.WalkerBigNumber, settings.WalkerAlert, face)).ToArray();
                var words = view.Walking
                    ? $"Pokéwalker. {view.Name} on {view.CourseName}. {EggWidget.Num(view.TodaySteps)} steps today, {EggWidget.Num(view.Watts)} watts."
                    : $"Pokéwalker. Nobody on a walk. {EggWidget.Num(view.Watts)} watts.";
                return new WidgetPicture(frames, 800, words + (settings.WalkerAlert && view.Alert ? " A search is ready." : ""));
            }
            case HomeWidgetKind.Partner:
            {
                var partner = settings.Partner;
                if (neutralIfEmpty && partner is null) return Neutral();
                var (battery, charging) = Battery(context);
                var mood = PartnerWidget.MoodAt(DateTime.Now, battery, charging);
                using var sprite = partner is null ? null : await PartnerSpriteAsync(partner, live, fetch).ConfigureAwait(false);
                var frames = Frames(2).Select(f =>
                    HomeWidgetPaint.RenderPartner(partner, mood, battery, sprite, DateTime.Now, size, widthPx, heightPx, f, face)).ToArray();
                var interval = mood switch { PartnerMood.Asleep => 1600, PartnerMood.Happy => 600, _ => 1000 };
                return new WidgetPicture(frames, interval, partner is null ? PartnerWidget.EmptyLine : PartnerWidget.Line(partner, mood, battery));
            }
            default:
            {
                var view = EggWidget.View(HomeWidgetStore.EggSnapshot(settings));
                if (neutralIfEmpty && view.Stage == EggStage.None) return Neutral();
                var (names, interval) = EggWidget.Motion(view.Stage);
                // A still egg needs one frame; a wobbling one, its four.
                var count = names.Distinct().Count() == 1 ? 1 : 4;
                var frames = Frames(count).Select(f => HomeWidgetPaint.RenderEgg(view, DateTime.Now, size, widthPx, heightPx, f, face)).ToArray();
                return new WidgetPicture(frames, interval, $"{view.Title}. {view.Line} {view.Detail}");
            }
        }
    }

    /// <summary>The Poképark widget, painted the way its own provider does (the Widgets menu's preview).</summary>
    public static WidgetPicture RenderPark(Context context, HomeWidgetSizeHint size, int widthPx, int heightPx)
    {
        var snapshot = PokeparkWidgetPublisher.Read(context) ?? ParkWidgetSnapshot.Empty;
        var parkSize = size switch { HomeWidgetSizeHint.Small => ParkWidgetSize.Small, HomeWidgetSizeHint.Wide => ParkWidgetSize.Wide, _ => ParkWidgetSize.Large };
        var step = ParkWidgetPaint.StepFor(parkSize, widthPx, heightPx);
        var view = ParkWidget.Compose(snapshot, parkSize, (widthPx + step - 1) / step, (heightPx + step - 1) / step, DateTime.Now);
        var sprites = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        try
        {
            var bitmap = ParkWidgetPaint.Render(view, step, resident =>
            {
                if (resident.Sprite is not { } name) return null;
                if (sprites.TryGetValue(name, out var known)) return known;
                var path = System.IO.Path.Combine(PokeparkWidgetPublisher.Folder(context), System.IO.Path.GetFileName(name));
                SKBitmap? loaded = null;
                lock (PokeparkWidgetPublisher.FileLock)
                    if (File.Exists(path)) loaded = SKBitmap.Decode(path);
                return sprites[name] = loaded;
            }, WidgetFont.Face(context));
            return new WidgetPicture([bitmap], 4000, "Poképark");
        }
        finally
        {
            foreach (var bitmap in sprites.Values) bitmap?.Dispose();
        }
    }

    // ── The Pokéwalker's data ────────────────────────────────────────────────────────────────

    private static async Task<WalkerWidgetView> WalkerViewAsync(bool live)
    {
        WalkerState state;
        try
        {
            state = File.Exists(PokewalkerStrolls.FilePath)
                ? JsonSerializer.Deserialize<WalkerState>(await File.ReadAllTextAsync(PokewalkerStrolls.FilePath).ConfigureAwait(false)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            state = new();
        }
        long? reading = null;
        if (live && WalkerSteps.HasSensor && StepPermission())
            reading = await WalkerSteps.ReadAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(DateTime.Now);
        long? pedometerToday = null;
        if (reading is { } now)
        {
            var (count, steps) = WalkerWidget.Today(HomeWidgetStore.WalkerDay, now, today);
            HomeWidgetStore.Save(count);
            pedometerToday = steps;
        }
        else if (state.SensorLast is not null && HomeWidgetStore.WalkerDay is { } day && day.Day == today)
            pedometerToday = day.Carried + day.Last - day.Anchor;
        return WalkerWidget.View(WalkerWidget.Project(state, reading, DateTimeOffset.Now), pedometerToday, today);
    }

    private static bool StepPermission() =>
        !OperatingSystem.IsAndroidVersionAtLeast(29) ||
        AndroidX.Core.Content.ContextCompat.CheckSelfPermission(Android.App.Application.Context, Android.Manifest.Permission.ActivityRecognition)
            == Android.Content.PM.Permission.Granted;

    private static SKImage? WalkerShell(Context context)
    {
        using var bitmap = WidgetFont.Asset(context, $"{WalkerDevice.Folder}/{WalkerDevice.ShellFile}");
        return bitmap is null || bitmap.Width != WalkerDevice.Width ? null : SKImage.FromBitmap(bitmap);
    }

    private static async Task<WalkerSprite?> WalkerSpriteAsync(WalkerWidgetView view, bool live, bool fetch)
    {
        var path = HomeWidgetStore.SpriteFile(view.Species, view.Form, view.Shiny, "walker");
        var bitmap = HomeWidgetStore.LoadSprite(path);
        if (bitmap is null && fetch)
        {
            bitmap = await FetchSpriteAsync(new SpriteLook(view.Species, view.Form, view.Shiny), front: false, TimeSpan.FromSeconds(live ? 5 : 1)).ConfigureAwait(false);
            if (bitmap is not null) HomeWidgetStore.SaveSprite(path, bitmap);
        }
        if (bitmap is null) return null;
        using (bitmap) return WalkerLcd.Convert(bitmap);
    }

    // ── The partner's data ───────────────────────────────────────────────────────────────────

    private static async Task<SKBitmap?> PartnerSpriteAsync(PartnerChoice partner, bool live, bool fetch)
    {
        var path = HomeWidgetStore.SpriteFile(partner.Species, partner.Form, partner.Shiny, "partner");
        if (HomeWidgetStore.LoadSprite(path) is { } saved) return saved;
        if (!fetch) return null;
        var fetched = await FetchSpriteAsync(new SpriteLook(partner.Species, partner.Form, partner.Shiny), front: true, TimeSpan.FromSeconds(live ? 5 : 2)).ConfigureAwait(false);
        if (fetched is not null) HomeWidgetStore.SaveSprite(path, fetched);
        return fetched;
    }

    /// <summary>Battery level (percent) and whether it charges; nulls when the phone does not say.</summary>
    private static (int?, bool) Battery(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.BatteryService) is not BatteryManager battery) return (null, false);
            var level = battery.GetIntProperty((int)BatteryProperty.Capacity);
            var charging = OperatingSystem.IsAndroidVersionAtLeast(23) && battery.IsCharging;
            return (level is > 0 and <= 100 ? level : null, charging);
        }
        catch (Java.Lang.Exception) { return (null, false); }
    }

    /// <summary>
    /// A sprite trimmed to its opaque pixels, copied out of the sprite cache (which may let its own
    /// bitmap go): Showdown's front when asked and available, PKHeX's pixel art otherwise. Null when
    /// nothing came within <paramref name="wait"/>.
    /// </summary>
    public static async Task<SKBitmap?> FetchSpriteAsync(SpriteLook look, bool front, TimeSpan wait)
    {
        if (IPlatformApplication.Current?.Services.GetService<ISpriteService>() is not { } sprites) return null;
        var deadline = DateTime.UtcNow + wait;
        var useFront = front;
        while (true)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (done, copy) = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                SKBitmap? found = null;
                if (useFront)
                {
                    if (!sprites.TryGetShowdownFront(look, () => signal.TrySetResult(), out found)) return (false, (SKBitmap?)null);
                    if (found is null) useFront = false;
                }
                if (found is null)
                {
                    found = sprites.GetSprite(look);
                    if (found is null)
                    {
                        sprites.Warm(look, () => signal.TrySetResult());
                        return (false, null);
                    }
                }
                return (true, Trim(found));
            }).ConfigureAwait(false);
            if (done) return copy;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            await Task.WhenAny(signal.Task, Task.Delay(left)).ConfigureAwait(false);
            if (!signal.Task.IsCompleted) return null;
        }
    }

    private static SKBitmap? Trim(SKBitmap source)
    {
        var bounds = StoragePaint.OpaqueBounds(source, new SKRectI(0, 0, source.Width, source.Height));
        if (bounds.Width <= 0 || bounds.Height <= 0) return source.Copy();
        using var crop = new SKBitmap();
        return source.ExtractSubset(crop, bounds) ? crop.Copy() : source.Copy();
    }

    // ── What the app tells the widgets ───────────────────────────────────────────────────────

    /// <summary>
    /// Called when a save opens or is left: remembers it as the last save, reads its Day Care and
    /// its party's eggs for the egg widget (read only), updates the "Open last save" shortcut, and
    /// repaints the egg widgets. Never throws, never writes the save.
    /// </summary>
    public static async Task CaptureOpenSaveAsync()
    {
        try
        {
            var services = IPlatformApplication.Current?.Services;
            if (services?.GetService<ISaveSessionService>() is not { Current: { } current, CurrentSession: { } session }) return;
            var id = current.Document.DocumentId;
            var detected = services.GetService<SavePickerViewModel>()?.Saves.FirstOrDefault(s => s.DocumentId == id);
            var name = detected is null ? current.Document.DisplayName
                : string.IsNullOrWhiteSpace(detected.TrainerName) ? detected.GameLabel : $"{detected.GameLabel} · {detected.TrainerName}";
            HomeWidgetStore.Save(new LastSave(id, name));
            AppShortcuts.Publish();
            var snapshot = await Task.Run(() => ReadEggs(session, id, name)).ConfigureAwait(false);
            if (snapshot is not null) HomeWidgetStore.Save(snapshot);
            Refresh(HomeWidgetKind.Egg);
        }
        catch (Exception ex) { Android.Util.Log.Warn("Widgets", $"Egg read failed: {ex.Message}"); }
    }

    private static EggWidgetSnapshot? ReadEggs(ISaveEngineSession session, string id, string name)
    {
        var eggs = new List<EggWidgetEgg>();
        if (EggService.IsSupported(session))
            foreach (var slot in session.Snapshot.Slots.Where(s => s.Box == -1 && s.IsEgg && s.Species is > 0))
            {
                try
                {
                    var egg = EggService.Get(session, -1, slot.Slot);
                    if (egg.IsEgg) eggs.Add(new EggWidgetEgg(egg.Species, slot.Species ?? 0, egg.HatchCounter, egg.MinHatchCounter, egg.MaxHatchCounter, egg.StepsLeft));
                }
                catch (InvalidOperationException) { }
            }
        DaycareInfo daycare;
        try { daycare = session.GetDaycare(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { daycare = new(false, []); }
        var facility = daycare.Facilities.FirstOrDefault(f => f.EggAvailable) ?? daycare.Facilities.FirstOrDefault(f => f.Slots.Any(s => s.Occupied));
        var parents = facility?.Slots.Where(s => s.Occupied).Select(s => s.Nickname.Length > 0 ? s.Nickname : s.SpeciesName).Take(2).ToArray() ?? [];
        return new EggWidgetSnapshot(id, name, daycare.Supported && daycare.Facilities.Count > 0, facility?.EggAvailable == true, parents, [.. eggs], DateTimeOffset.Now);
    }

    /// <summary>Makes <paramref name="partner"/> the partner: its sprite is fetched now, while PKForge is open.</summary>
    public static async Task SetPartnerAsync(PartnerChoice? partner)
    {
        HomeWidgetStore.Save(HomeWidgetStore.Settings with { Partner = partner });
        if (partner is not null)
        {
            var path = HomeWidgetStore.SpriteFile(partner.Species, partner.Form, partner.Shiny, "partner");
            if (!File.Exists(path)
                && await FetchSpriteAsync(new SpriteLook(partner.Species, partner.Form, partner.Shiny), front: true, TimeSpan.FromSeconds(6)).ConfigureAwait(false) is { } sprite)
                using (sprite) HomeWidgetStore.SaveSprite(path, sprite);
        }
        Refresh(HomeWidgetKind.Partner);
    }

    /// <summary>A partner from loose bytes (a save slot's export, a Bank entry), described by the engine.</summary>
    public static PartnerChoice? PartnerFrom(byte[] data, string? format, string origin, Guid? bankId, string? documentId)
    {
        var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
        if (engine?.TryDescribeEntity(data, origin, format) is not { } info) return null;
        return new PartnerChoice(info.Nickname, info.Species, info.Form, info.Shiny, info.Level, origin, bankId, documentId,
            info.Format ?? format, Convert.ToBase64String(data));
    }

    /// <summary>Asks the launcher to pin a widget; false when it cannot (the menu then explains how).</summary>
    /// <remarks>
    /// Android lets an app ask for a widget, not for its cells: the launcher takes those from the
    /// provider's info. The chosen layout goes along as the widget's size options (for launchers that
    /// read them), and the widget draws that layout in whatever tile it gets anyway.
    /// </remarks>
    public static bool RequestPin(WidgetSlot slot, HomeWidgetSizeHint? layout)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return false;
        var context = Android.App.Application.Context;
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null || !manager.IsRequestPinAppWidgetSupported) return false;
        using var component = new ComponentName(context, Java.Lang.Class.FromType(ProviderOf(slot)));
        using var extras = new Bundle();
        if (layout is { } size)
        {
            var (w, h) = HomeWidgetSizes.Dp(size);
            extras.PutInt(AppWidgetManager.OptionAppwidgetMinWidth, w);
            extras.PutInt(AppWidgetManager.OptionAppwidgetMaxWidth, w);
            extras.PutInt(AppWidgetManager.OptionAppwidgetMinHeight, h);
            extras.PutInt(AppWidgetManager.OptionAppwidgetMaxHeight, h);
        }
        try { return manager.RequestPinAppWidget(component, layout is null ? null : extras, null); }
        catch (Java.Lang.Exception ex)
        {
            Android.Util.Log.Warn("Widgets", $"Pin refused: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// The shared part of the three providers: paint on update and on resize, off the main thread. A
/// quick picture first, the full one a second later, and two more passes at about 5 and 30 seconds
/// (<see cref="WidgetHost.ScheduleRetries"/>), for hosts that lay a fresh widget out late.
/// </summary>
public abstract class HomeWidgetProvider : AppWidgetProvider
{
    protected abstract HomeWidgetKind Kind { get; }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent?.Action == WidgetHost.RetryAction)
        {
            var manager = AppWidgetManager.GetInstance(context);
            var ids = HomeWidgets.IdsOf(context, GetType());
            if (manager is not null && ids.Length > 0) Paint(context, manager, ids, fresh: false);
            return;
        }
        base.OnReceive(context, intent);
    }

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        Paint(context, appWidgetManager, appWidgetIds, fresh: true);
    }

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        Paint(context, appWidgetManager, [appWidgetId], fresh: true);
    }

    private void Paint(Context context, AppWidgetManager manager, int[] ids, bool fresh)
    {
        var pending = GoAsync();
        var kind = Kind;
        if (fresh) WidgetHost.ScheduleRetries(context, GetType());
        _ = Task.Run(async () =>
        {
            try
            {
                if (fresh)
                {
                    await HomeWidgets.UpdateAsync(context, manager, ids, kind, quick: true).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                await HomeWidgets.UpdateAsync(context, manager, ids, kind).ConfigureAwait(false);
            }
            catch (Exception ex) { Android.Util.Log.Warn("Widgets", $"{kind} update failed: {ex.Message}"); }
            finally { pending?.Finish(); }
        });
    }
}

/// <summary>The Pocket Pokéwalker widget.</summary>
[BroadcastReceiver(Label = "Pocket Pokéwalker", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/walker_widget_info")]
public sealed class WalkerWidgetProvider : HomeWidgetProvider
{
    protected override HomeWidgetKind Kind => HomeWidgetKind.Walker;
}

/// <summary>The Partner Pokémon widget.</summary>
[BroadcastReceiver(Label = "Partner Pokémon", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/partner_widget_info")]
public sealed class PartnerWidgetProvider : HomeWidgetProvider
{
    protected override HomeWidgetKind Kind => HomeWidgetKind.Partner;
}

/// <summary>The Day Care egg widget.</summary>
[BroadcastReceiver(Label = "Day Care egg", Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/egg_widget_info")]
public sealed class EggWidgetProvider : HomeWidgetProvider
{
    protected override HomeWidgetKind Kind => HomeWidgetKind.Egg;
}
