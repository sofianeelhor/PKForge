using Android.App;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.OS;
using Android.Content;
using Android.Views;
using PKForge.Domain;

[assembly: UsesPermission(Android.Manifest.Permission.Vibrate)]
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]

namespace PKForge.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    Icon = "@mipmap/pkforge", RoundIcon = "@mipmap/pkforge_round",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnPause()
    {
        // A Presentation owns a separate window, so Android does not reliably hide it
        // when the launcher backgrounds the main activity (notably on the AYN Thor).
        StopHatRepeat();
        StopKeyRepeat();
        _hatDirection = null;
        SecondaryDisplayHost()?.SuspendForActivityPause();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        SecondaryDisplayHost()?.ResumeAfterActivityPause();
    }

    private static AndroidSecondaryDisplayHost? SecondaryDisplayHost() =>
        IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>() as AndroidSecondaryDisplayHost;

    /// <summary>Console apps are fullscreen: hide status/navigation bars (swipe reveals them transiently).</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus || Window is not { DecorView: { } decorView } window) return;
        if (AndroidX.Core.View.WindowCompat.GetInsetsController(window, decorView) is not { } controller) return;
        controller.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.Action == KeyEventActions.Down && ResolveButton(e.KeyCode) is { } button)
        {
            Android.Util.Log.Info("PKForgeInput", "key down " + e.KeyCode + " rc=" + e.RepeatCount);
            // Direction repeats are owned by the app's own timers (the hat timer for
            // stick-style input, the key timer for clean digital pads); framework
            // repeats would stack on top and double the navigation speed.
            if (e.RepeatCount > 0)
                return IsDirectional(button);
            var router = IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>();
            if (router?.Dispatch(button) == true)
            {
                Haptic();
                if (IsDirectional(button))
                    StartKeyRepeat(button);
                return true;
            }
            // Directional keys must never fall through to Android's native focus search:
            // it draws the grey selection rectangles on the home shelf. Touch stays the
            // only pointer input, so there is nothing useful to hand over.
            if (IsDirectional(button))
                return true;
        }
        if (e?.Action == KeyEventActions.Up && ResolveButton(e.KeyCode) == _keyRepeatButton)
            StopKeyRepeat();
        return base.DispatchKeyEvent(e);
    }

    private Services.PadButton? _keyRepeatButton;
    private Java.Lang.Runnable? _keyRepeatRunnable;

    /// <summary>Hold-repeat for dpad directions that arrive as key events: some pads
    /// report clean digital keys with no framework repeats at all.</summary>
    private void StartKeyRepeat(Services.PadButton button)
    {
        StopKeyRepeat();
        _keyRepeatButton = button;
        _keyRepeatRunnable = new Java.Lang.Runnable(() =>
        {
            if (_keyRepeatButton != button) return;
            var router = IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>();
            if (router?.Dispatch(button) != true) { StopKeyRepeat(); return; }
            _hatRepeatHandler.PostDelayed(_keyRepeatRunnable!, HatRepeatMs);
        });
        _hatRepeatHandler.PostDelayed(_keyRepeatRunnable, HatHoldDelayMs);
    }

    private void StopKeyRepeat()
    {
        _keyRepeatButton = null;
        if (_keyRepeatRunnable is null) return;
        _hatRepeatHandler.RemoveCallbacks(_keyRepeatRunnable);
        _keyRepeatRunnable = null;
    }

    private Services.PadButton? _hatDirection;
    private readonly Android.OS.Handler _hatRepeatHandler = new(Android.OS.Looper.MainLooper!);
    private Java.Lang.Runnable? _hatRepeatRunnable;
    private const long HatHoldDelayMs = 320;
    private const long HatRepeatMs = 80;

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        // D-pad-emulating hats (the Thor's controller) arrive as axis motion, and the
        // device only reports CHANGES: a statically held direction produces no further
        // events. The repeat therefore needs its own timer, exactly like the native
        // joystick navigation this replaces: step once on deflection, then keep
        // stepping after a hold delay at a fixed cadence until release.
        if (e is { Action: MotionEventActions.Move })
        {
            var direction = HatDirection(e);
            Android.Util.Log.Info("PKForgeInput", "motion hat x=" + e.GetAxisValue(Axis.HatX) + " y=" + e.GetAxisValue(Axis.HatY) + " src=" + e.Source);
            if (direction != _hatDirection)
            {
                StopHatRepeat();
                _hatDirection = direction;
                if (direction is { } button)
                {
                    DispatchHat(button, haptic: true);
                    StartHatRepeat(button, HatHoldDelayMs);
                }
            }
            if (direction is not null)
                return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    private void StartHatRepeat(Services.PadButton button, long delayMs)
    {
        _hatRepeatRunnable = new Java.Lang.Runnable(() =>
        {
            if (_hatDirection != button) return; // released or rolled to another direction
            DispatchHat(button, haptic: false);
            StartHatRepeat(button, HatRepeatMs);
        });
        _hatRepeatHandler.PostDelayed(_hatRepeatRunnable, delayMs);
    }

    private void StopHatRepeat()
    {
        if (_hatRepeatRunnable is null) return;
        _hatRepeatHandler.RemoveCallbacks(_hatRepeatRunnable);
        _hatRepeatRunnable = null;
    }

    private static void DispatchHat(Services.PadButton button, bool haptic)
    {
        var router = IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>();
        if (router?.Dispatch(button) == true && haptic)
            Haptic(); // once per press: a tick every 80 ms of repeat would buzz
    }

    private static bool IsDirectional(Services.PadButton button) =>
        button is Services.PadButton.Up or Services.PadButton.Down or Services.PadButton.Left or Services.PadButton.Right;

    private static Services.PadButton? HatDirection(MotionEvent e)
    {
        var x = e.GetAxisValue(Axis.HatX);
        var y = e.GetAxisValue(Axis.HatY);
        if (Math.Abs(x) < 0.5f && Math.Abs(y) < 0.5f) return null;
        if (Math.Abs(y) >= Math.Abs(x)) return y < 0 ? Services.PadButton.Up : Services.PadButton.Down;
        return x < 0 ? Services.PadButton.Left : Services.PadButton.Right;
    }

    private static Services.PadButton? ResolveButton(Keycode code) => code switch
    {
        Keycode.ButtonA or Keycode.Enter or Keycode.DpadCenter => Services.PadButton.A,
        Keycode.ButtonB or Keycode.Back => Services.PadButton.B,
        Keycode.ButtonX => Services.PadButton.X,
        Keycode.ButtonY => Services.PadButton.Y,
        Keycode.ButtonStart or Keycode.Menu => Services.PadButton.Start,
        Keycode.ButtonL1 or Keycode.Button5 => Services.PadButton.L,
        Keycode.ButtonR1 or Keycode.Button6 => Services.PadButton.R,
        Keycode.DpadUp => Services.PadButton.Up,
        Keycode.DpadDown => Services.PadButton.Down,
        Keycode.DpadLeft => Services.PadButton.Left,
        Keycode.DpadRight => Services.PadButton.Right,
        _ => null,
    };

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        var services = IPlatformApplication.Current?.Services;
        var filePicker = services?.GetService<IDocumentPicker>() as AndroidDocumentPicker;
        var folderPicker = services?.GetService<IFolderPicker>() as AndroidFolderPicker;
        if (filePicker?.HandleActivityResult(requestCode, resultCode, data) != true
            && folderPicker?.HandleActivityResult(requestCode, resultCode, data) != true)
            base.OnActivityResult(requestCode, resultCode, data);
    }


    /// <summary>Short tick on handled pad input. Failures (no vibrator, disabled) are ignored.</summary>
    private static void Haptic()
    {
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); }
        catch { }
    }
}

/// <summary>
/// Renders the second-screen box mirror via DisplayManager + Presentation.
/// The AYN Thor does NOT tag its bottom screen as a presentation-category display,
/// so detection falls back to GetDisplays()[1] when the category query is empty.
/// </summary>
public sealed class AndroidSecondaryDisplayHost(IServiceProvider services) : ISecondaryDisplayHost
{
    private PagePresentation? _presentation;
    private Views.SecondScreenBoxPage? _page;
    private bool _resumeAfterActivityPause;

    public bool IsAvailable => ResolveDisplay() is not null;

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var display = ResolveDisplay() ?? throw new InvalidOperationException("No secondary display is available.");
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No foreground Android activity is available.");

        if (_presentation is { IsShowing: true })
            return ValueTask.CompletedTask;

        // A dismissed presentation (SAF picker, sleep) cannot be reshown; rebuild page + presentation.
        _presentation?.Dismiss();
        _page?.Cleanup();
        _page = services.GetRequiredService<Views.SecondScreenBoxPage>();
        _presentation = new PagePresentation(activity, display, _page, services);
        _presentation.Show();
        return ValueTask.CompletedTask;
    }

    public ValueTask DismissAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dismiss();
        _resumeAfterActivityPause = false;
        return ValueTask.CompletedTask;
    }

    internal void SuspendForActivityPause()
    {
        _resumeAfterActivityPause |= _presentation?.IsShowing == true;
        Dismiss();
    }

    internal void ResumeAfterActivityPause()
    {
        if (!_resumeAfterActivityPause)
            return;

        _resumeAfterActivityPause = false;
        try { _ = ShowAsync(); }
        catch { /* A removed or unavailable secondary display must not break resume. */ }
    }

    private void Dismiss()
    {
        _presentation?.Dismiss();
        _presentation = null;
        _page?.Cleanup();
        _page = null;
    }

    private static Display? ResolveDisplay()
    {
        var manager = (DisplayManager?)Platform.AppContext.GetSystemService(Android.Content.Context.DisplayService);
        if (manager is null) return null;

        var presentation = manager.GetDisplays(DisplayManager.DisplayCategoryPresentation);
        if (presentation is { Length: > 0 })
            return presentation[0];

        // Thor fallback: its built-in bottom screen is not presentation-tagged.
        var all = manager.GetDisplays();
        return all is { Length: > 1 } ? all[1] : null;
    }

    private sealed class PagePresentation(Activity activity, Display display, ContentPage page, IServiceProvider services)
        : Presentation(activity, display)
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window?.AddFlags(WindowManagerFlags.Fullscreen);
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            // Inflate with the Activity as context (not the Presentation's dialog context)
            // so MAUI handlers resolve fonts/drawables registered against the Activity.
            var mauiContext = new Microsoft.Maui.MauiContext(services, activity);
            SetContentView(Microsoft.Maui.Platform.ElementExtensions.ToPlatform(page, mauiContext));
        }
    }
}
