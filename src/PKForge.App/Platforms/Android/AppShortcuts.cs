using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

namespace PKForge.App;

/// <summary>
/// PKForge's launcher shortcuts: Open last save, Bank, Pokéwalker, Poképark. Published as dynamic
/// shortcuts (the long-press menu of Launcher3, Pixel, Nova and most launchers, and what Cocoon can
/// import), and offered to launchers that ask apps for a shortcut the old way
/// (<see cref="ShortcutPickerActivity"/>, Cocoon's "Add Shortcut"). Each opens MainActivity with
/// <see cref="HomeWidgets.OpenExtra"/>, the same door the widgets use.
/// </summary>
public static class AppShortcuts
{
    public sealed record Entry(string Id, string Target, string Short, string Long);

    public static IReadOnlyList<Entry> Entries()
    {
        var last = HomeWidgetStore.Last;
        var list = new List<Entry>();
        if (last is not null) list.Add(new("last-save", "lastsave", "Last save", $"Open {last.Name}"));
        list.Add(new("bank", "bank", "Bank", "Open the Bank"));
        list.Add(new("walker", "walker", "Pokéwalker", "Open the Pokéwalker"));
        list.Add(new("pokepark", "pokepark", "Poképark", "Visit the Poképark"));
        return list;
    }

    public static Intent IntentFor(Context context, string target)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(Intent.ActionView);
        // Its own data per target: launchers that keep shortcuts as PendingIntents tell them apart.
        intent.SetData(Android.Net.Uri.Parse($"pkforge-widget://{target}/shortcut"));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra(HomeWidgets.OpenExtra, target);
        return intent;
    }

    private static ShortcutInfo? Build(Context context, Entry entry)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(25)) return null;
        var builder = new ShortcutInfo.Builder(context, entry.Id)
            .SetShortLabel(entry.Short)
            .SetLongLabel(entry.Long)
            .SetIntent(IntentFor(context, entry.Target));
        if (context.ApplicationInfo?.Icon is > 0 and var icon) builder.SetIcon(Icon.CreateWithResource(context, icon));
        return builder.Build();
    }

    /// <summary>Publishes the shortcuts (again, when the last save changed). Never throws.</summary>
    public static void Publish()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(25)) return;
        try
        {
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Java.Lang.Class.FromType(typeof(ShortcutManager))) is not ShortcutManager manager) return;
            var max = Math.Max(1, manager.MaxShortcutCountPerActivity);
            var shortcuts = Entries().Take(max).Select(e => Build(context, e)).OfType<ShortcutInfo>().ToList();
            manager.SetDynamicShortcuts(shortcuts);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            Android.Util.Log.Warn("Shortcuts", $"Shortcuts not published: {ex.Message}");
        }
    }

    /// <summary>The result a launcher expects from an "Add Shortcut" request.</summary>
    public static Intent? ResultFor(Context context, Entry entry)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26)
            && context.GetSystemService(Java.Lang.Class.FromType(typeof(ShortcutManager))) is ShortcutManager manager
            && Build(context, entry) is { } info)
            return manager.CreateShortcutResultIntent(info);
        // Launchers before Android 8 read the legacy extras.
#pragma warning disable CS0618, CA1422 // the only way those launchers understand
        var result = new Intent();
        result.PutExtra(Intent.ExtraShortcutIntent, IntentFor(context, entry.Target));
        result.PutExtra(Intent.ExtraShortcutName, entry.Short);
        if (context.ApplicationInfo?.Icon is > 0 and var icon)
            result.PutExtra(Intent.ExtraShortcutIconResource, Intent.ShortcutIconResource.FromContext(context, icon));
#pragma warning restore CS0618, CA1422
        return result;
    }
}

/// <summary>
/// Answers a launcher's "Add Shortcut" request (<c>ACTION_CREATE_SHORTCUT</c>): a short native list
/// of PKForge's shortcuts; the one picked goes back to the launcher. Cocoon imports shortcuts this way.
/// </summary>
[Activity(Label = "PKForge", Exported = true, Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true,
    Icon = "@mipmap/pkforge")]
[IntentFilter(new[] { Intent.ActionCreateShortcut }, Categories = new[] { Intent.CategoryDefault })]
public sealed class ShortcutPickerActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var entries = AppShortcuts.Entries();
        var theme = OperatingSystem.IsAndroidVersionAtLeast(22) ? Android.Resource.Style.ThemeDeviceDefaultDialogAlert : Android.Resource.Style.ThemeDeviceDefaultDialog;
        var dialog = new AlertDialog.Builder(this, theme)
            .SetTitle("Add a PKForge shortcut")!
            .SetItems(entries.Select(e => e.Long).ToArray(), (_, args) =>
            {
                if (AppShortcuts.ResultFor(this, entries[args.Which]) is { } result) SetResult(Result.Ok, result);
                Finish();
            })!
            .SetOnCancelListener(new Cancelled(this))!
            .Create()!;
        dialog.Show();
    }

    private sealed class Cancelled(Activity activity) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog)
        {
            activity.SetResult(Result.Canceled);
            activity.Finish();
        }
    }
}
