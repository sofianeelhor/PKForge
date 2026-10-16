namespace PKForge.App;

public sealed class App : Application
{
    /// <summary>Raised when Android resumes the app (including after the install-permission screen).</summary>
    public static event Action? Resumed;
    public static event Action? Suspended;

    /// <summary>Android can stop the activity and recreate it without an OnResume; see <see cref="Domain.AppForeground"/>.</summary>
    private static readonly Domain.AppForeground Foreground = new();

    protected override void OnSleep()
    {
        if (Foreground.EnterBackground())
        {
            Suspended?.Invoke();
            Music?.PauseForBackground();
            Sync?.Suspend();
        }
        base.OnSleep();
    }

    private static PKForge.Sync.SyncCoordinator? Sync =>
        IPlatformApplication.Current?.Services.GetService<PKForge.Sync.SyncCoordinator>();

    private static Platforms.Android.MusicPlayer? Music =>
        IPlatformApplication.Current?.Services.GetService<Domain.IMusicPlayer>() as Platforms.Android.MusicPlayer;

    /// <summary>The user's optional default background music starts with the app, once.</summary>
    protected override void OnStart()
    {
        base.OnStart();
        Music?.MaybeAutostart();
        // Bank Sync: a first sync now, and another whenever the network comes back.
        Connectivity.Current.ConnectivityChanged += (_, args) =>
        {
            if (args.NetworkAccess != NetworkAccess.None) Sync?.NetworkAvailable();
        };
        Sync?.RequestSync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        BackInFront();
    }

    /// <summary>
    /// A recreated activity (a configuration change, or Android reclaiming it while the process
    /// lives) only activates its new window and never calls OnResume, which left Bank Sync paused
    /// until the app restarted. Activation finishes the return in that case.
    /// </summary>
    private static void OnWindowActivated(object? sender, EventArgs args)
    {
        if (BackInFront()) Services.AppLog.Info("app", "Back in front without OnResume (the activity was recreated)");
    }

    /// <summary>Undoes OnSleep, once per return. False when the app never left.</summary>
    private static bool BackInFront()
    {
        if (!Foreground.EnterForeground()) return false;
        Music?.ResumeFromBackground();
        Sync?.Resume();
        Resumed?.Invoke();
        return true;
    }

    public App()
    {
        Trace("App ctor");

        // Every page reads its chrome colors when it is built, so the scheme comes first.
        Services.ColorThemeSetting.ApplyStored();

        // Bank Sync: the living dex start box now lives with the Bank; move it before anything touches boxes.
        Services.LivingDexAutopilot.MoveLegacyStartBox();

        // Warm both Skia faces off the ctor: the party view paints before the first
        // save opens, and its cached nickname font must never pin the placeholder.
        _ = Views.PixelFont.WarmAsync();
        _ = Views.PixelFont.WarmFallbackAsync();

        // The DS system font (NDS12/"PixelUI") is the app's voice everywhere. Symbol glyphs
        // that NDS12 lacks (Ⓐ, ♂, ▼, ◓ ...) are pinned to "Rounded" at their few call sites.
        Style FontStyle(Type target, BindableProperty property) =>
            new(target) { Setters = { new Setter { Property = property, Value = "PixelUI" } } };
        Resources.Add(FontStyle(typeof(Label), Label.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Button), Button.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Entry), Entry.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Editor), Editor.FontFamilyProperty));
    }

    internal static void Trace(string message)
    {
#if ANDROID
        Android.Util.Log.Info("PKForgeBoot", message);
#endif
    }

    private static NavigationPage CreateRoot(IServiceProvider services)
    {
        Trace("resolving HomePage");
        var page = services.GetRequiredService<Views.HomePage>();
        Trace("HomePage resolved");
        return new NavigationPage(page)
        {
            BarBackgroundColor = Theme.UiTokens.Navy1,
            BarTextColor = Colors.White,
        };
    }

    /// <summary>
    /// Builds Home and the lower screen again after a color scheme change: pages take their
    /// colors when they are built. Called from Home's settings, so Home is the only page open.
    /// </summary>
    internal async Task ReloadForThemeAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null || Windows.Count == 0) return;
        Windows[0].Page = CreateRoot(services);
        var host = services.GetService<PKForge.Domain.ISecondaryDisplayHost>();
        if (host is null || Services.SecondScreenMode.UserOff) return;
        try
        {
            await host.DismissAsync();
            if (host.IsAvailable) await host.ShowAsync();
        }
        catch (InvalidOperationException error)
        {
            Services.AppLog.Warn("theme", $"Rebuilding the lower screen: {error.Message}");
        }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Trace("CreateWindow enter");
        // Never let a startup failure become a silent blank screen: show the error.
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("MAUI services are unavailable.");
            var window = new Window(CreateRoot(services));
            window.Activated += OnWindowActivated;
            return window;
        }
        catch (Exception error)
        {
            Trace($"CreateWindow FAILED: {error}");
            return new Window(new ContentPage
            {
                BackgroundColor = Theme.UiTokens.Navy0,
                Content = new ScrollView
                {
                    Content = new Label
                    {
                        Text = $"PKForge failed to start:\n\n{error}",
                        TextColor = Colors.White,
                        Margin = new Thickness(20),
                    },
                },
            });
        }
    }
}
