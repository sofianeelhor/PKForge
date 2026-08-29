namespace PKForge.App;

public sealed class App : Application
{
    /// <summary>Raised when Android resumes the app (including after the install-permission screen).</summary>
    public static event Action? Resumed;

    /// <summary>The user's optional default background music starts with the app, once.</summary>
    protected override void OnStart()
    {
        base.OnStart();
        var music = IPlatformApplication.Current?.Services.GetService<Domain.IMusicPlayer>() as Platforms.Android.MusicPlayer;
        music?.MaybeAutostart();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Resumed?.Invoke();
    }

    public App()
    {
        Trace("App ctor");

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

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Trace("CreateWindow enter");
        // Never let a startup failure become a silent blank screen: show the error.
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("MAUI services are unavailable.");
            Trace("resolving HomePage");
            var page = services.GetRequiredService<Views.HomePage>();
            Trace("HomePage resolved");
            return new Window(new NavigationPage(page)
            {
                BarBackgroundColor = Theme.UiTokens.Navy1,
                BarTextColor = Colors.White,
            });
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
