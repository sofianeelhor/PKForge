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
    // Resize, not pan: the keyboard inset reaches SafeAreaListener, which lifts the content above it.
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const string MovedToMainScreen = "pkforge.moved-to-main-screen";

    /// <summary>
    /// The UI scale setting: a percentage scales Android's density for this activity, so
    /// every MAUI layout grows or shrinks together. An override configuration survives the
    /// configuration changes the activity keeps (a pad paired, a fold opened). Auto and 100%
    /// leave the density alone, so the Thor is untouched.
    /// </summary>
    protected override void AttachBaseContext(Context? @base)
    {
        base.AttachBaseContext(@base);
        var factor = Services.UiScaleSetting.LayoutFactor;
        if (factor == 1f || @base?.Resources?.Configuration is not { DensityDpi: > 0 } current) return;
        var scaled = new Android.Content.Res.Configuration { DensityDpi = (int)Math.Round(current.DensityDpi * factor) };
        ApplyOverrideConfiguration(scaled);
        Services.UiScaleSetting.AppliedLayoutFactor = factor;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        KeepContentInSafeArea();
        AppShortcuts.Publish();
        // Rebuilt after Android reclaimed the process: the intent that started it was a link
        // already followed then, not a new tap.
        if (savedInstanceState is not null)
        {
            Intent?.RemoveExtra(HomeWidgets.OpenExtra);
            Intent?.RemoveExtra("pokepark");
        }
        if (savedInstanceState is null) MoveToMainScreenIfLaunchedBelow();
    }

    /// <summary>
    /// A dual-screen handheld can start PKForge on its lower panel, which then took the top
    /// panel for the second screen: the whole layout upside down. Relaunch once on the main
    /// display; if Android keeps it below, say so (the lower screen stays off, see
    /// <see cref="AndroidSecondaryDisplayHost.LaunchedOnLowerScreen"/>). The main display is
    /// always the default one: Android only lets a Presentation onto the other panels, so
    /// this holds even where the default panel is the lower one (Gamma OS on the RG DS).
    /// </summary>
    private void MoveToMainScreenIfLaunchedBelow()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || !AndroidSecondaryDisplayHost.LaunchedOnLowerScreen(this)) return;
        if (Intent?.GetBooleanExtra(MovedToMainScreen, false) == true)
        {
            Services.AppLog.Warn("second", $"Still on display {Display?.DisplayId} after moving to the main screen");
            // "Lower" only where the panels say so (the Thor's small one); equal panels, or a
            // firmware that made the lower one the default, leave it unsaid.
            var where = AndroidSecondaryDisplayHost.ArrangementFrom(this) == ScreenArrangement.MainOnTop ? "the lower screen" : "the second screen";
            Android.Widget.Toast.MakeText(this,
                $"PKForge opened on {where}. For the two-screen layout, launch it from the main screen.",
                Android.Widget.ToastLength.Long)?.Show();
            return;
        }
        try
        {
            var relaunch = new Intent(this, typeof(MainActivity))
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.MultipleTask);
            // A widget's or a shortcut's link goes along to the main screen.
            if (Intent?.Extras is { } extras) relaunch.PutExtras(extras);
            relaunch.PutExtra(MovedToMainScreen, true);
            if (ActivityOptions.MakeBasic() is not { } options) return;
            options.SetLaunchDisplayId(Display.DefaultDisplay);
            Services.AppLog.Info("second", $"Launched on display {Display?.DisplayId}; moving to the main screen");
            StartActivity(relaunch, options.ToBundle());
            FinishAndRemoveTask();
        }
        catch (Exception error) when (error is Java.Lang.SecurityException or ActivityNotFoundException)
        {
            Services.AppLog.Error("second", "Could not move to the main screen", error);
        }
    }

    protected override void OnPause()
    {
        // A Presentation owns a separate window, so Android does not reliably hide it
        // when the launcher backgrounds the main activity (notably on the AYN Thor).
        StopHatRepeat();
        StopKeyRepeat();
        _hatDirection = null;
        SecondaryDisplayHost()?.SuspendForActivityPause();
        // Back on the home screen: the walk, the partner and the egg as PKForge left them.
        HomeWidgets.RefreshAll();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ClaimFocusIfFree();
        OpenFromIntent();
        SecondaryDisplayHost()?.ResumeAfterActivityPause();
    }

    /// <summary>
    /// Pairing or unplugging a controller changes the keyboard and navigation configuration.
    /// The activity is kept (see ConfigurationChanges): recreating it tore down the MAUI window
    /// under pages still loading images, and dropped the lower screen. A pad that goes away
    /// mid-hold never sends its key-up, so its repeat stops here.
    /// </summary>
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ReturnAfterLink();
        StopHatRepeat();
        StopKeyRepeat();
        _hatDirection = null;
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        // A launcher on the lower panel (Cocoon on the Thor) can bring the running task over to
        // its own display with the tap: PKForge goes back to the main screen, link and all, and
        // follows the link there.
        if (BackToMainScreen(intent)) return;
        var link = intent?.HasExtra(HomeWidgets.OpenExtra) == true || intent?.HasExtra("pokepark") == true;
        OpenFromIntent();
        // Android may move the task to the launcher's panel only after this call: checked again
        // once it settled, and on the configuration change the move brings.
        if (link && !(intent?.GetBooleanExtra(MovedToMainScreen, false) ?? false))
        {
            _linkAt = DateTime.UtcNow;
            Window?.DecorView?.PostDelayed(ReturnAfterLink, 700);
        }
    }

    private DateTime _linkAt;

    /// <summary>A link just moved PKForge to a lower panel: back to the main screen, once; the link goes on there.</summary>
    private void ReturnAfterLink()
    {
        if (DateTime.UtcNow - _linkAt > TimeSpan.FromSeconds(5)) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || !AndroidSecondaryDisplayHost.LaunchedOnLowerScreen(this)) return;
        _linkAt = default;
        BackToMainScreen(new Intent());
    }

    /// <summary>
    /// The running activity was brought to a lower panel by a launcher's tap: asks Android to put
    /// the task back on the default display, carrying the intent's link. True when it asked; the
    /// link then arrives again, on the main screen. Asked once per tap: if Android keeps PKForge
    /// below, the link is followed where it is.
    /// </summary>
    private bool BackToMainScreen(Intent? intent)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || intent is null || !AndroidSecondaryDisplayHost.LaunchedOnLowerScreen(this)
            || intent.GetBooleanExtra(MovedToMainScreen, false)) return false;
        try
        {
            if (ActivityOptions.MakeBasic() is not { } options) return false;
            options.SetLaunchDisplayId(Display.DefaultDisplay);
            var back = new Intent(this, typeof(MainActivity))
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            if (intent.Extras is { } extras) back.PutExtras(extras);
            back.PutExtra(MovedToMainScreen, true);
            Services.AppLog.Info("second", $"Brought to display {Display?.DisplayId} by a tap; back to the main screen");
            StartActivity(back, options.ToBundle());
            return true;
        }
        catch (Exception error) when (error is Java.Lang.SecurityException or ActivityNotFoundException or Java.Lang.IllegalArgumentException)
        {
            Services.AppLog.Error("second", "Could not go back to the main screen", error);
            return false;
        }
    }

    /// <summary>
    /// A widget or a launcher shortcut asked for a screen (<see cref="HomeWidgets.OpenExtra"/>):
    /// the park and the Pokéwalker go through the park; the rest through <see cref="Views.WidgetLinks"/>.
    /// The Poképark widget's older "pokepark" flag still works.
    /// </summary>
    private void OpenFromIntent()
    {
        if (Intent is not { } intent) return;
        var target = intent.GetStringExtra(HomeWidgets.OpenExtra);
        if (target is null && intent.GetBooleanExtra("pokepark", false)) target = "pokepark";
        if (target is null) return;
        // Read once: the same intent comes back on every resume, and with the activity rebuilt.
        intent.RemoveExtra(HomeWidgets.OpenExtra);
        intent.RemoveExtra("pokepark");
        // Reopened from Recents, Android hands back the intent that first started the task: that
        // tap was answered long ago.
        if ((intent.Flags & ActivityFlags.LaunchedFromHistory) != 0) return;
        Services.AppLog.Info("widgets", $"Link {target} ({(intent.Data?.ToString() ?? "shortcut")})");
        // A launcher widget click can arrive while the secondary Presentation still owns focus:
        // the main activity takes it back, so the tap never strands the player on the lower display.
        Window?.DecorView?.Post(() =>
        {
            Window?.DecorView?.RequestFocus();
            Window?.DecorView?.ClearFocus();
        });
        Views.WidgetLinks.Request(target);
    }

    /// <summary>
    /// Notches, punch holes and visible system bars: the window is padded clear of them, so
    /// no page root or painted canvas runs under a camera or a gesture bar. Hidden bars give
    /// no inset (the Thor, fullscreen with no cutout, gets none: nothing moves there); bars
    /// swiped in for a moment float over the app as before.
    /// </summary>
    private void KeepContentInSafeArea()
    {
        if (Window is not { } window) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && window.Attributes is { } attributes)
        {
            // Draw edge to edge (behind the cutout's strip too) and pad the window, instead of
            // Android's black letterbox on one side only.
            attributes.LayoutInDisplayCutoutMode = OperatingSystem.IsAndroidVersionAtLeast(30)
                ? LayoutInDisplayCutoutMode.Always
                : LayoutInDisplayCutoutMode.ShortEdges;
            window.Attributes = attributes;
        }
        if (window.DecorView is not { } decor) return;
        // The strip left by the padding reads as the screen's own black edge, not a white band.
        decor.SetBackgroundColor(Android.Graphics.Color.Black);
        AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(decor, new SafeAreaListener());
    }

    /// <summary>Pads the whole window (every page, every overlay, every painted canvas) clear of the cutout and visible bars.</summary>
    private sealed class SafeAreaListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? view, AndroidX.Core.View.WindowInsetsCompat? insets)
        {
            if (view is null || insets is null) return insets!;
            var types = AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout() | AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars();
            // The soft keyboard is an inset too. Consuming everything below used to swallow it,
            // so the keyboard covered a text popup and its Set button (QA 2026-11-12): the
            // window's bottom padding now grows by the keyboard's height while it is up, and
            // every centred popup moves above it. A hardware pad (the Thor) shows no keyboard
            // and gets no IME inset: nothing changes there.
            var ime = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.Ime());
            if (insets.GetInsets(types) is { } safe)
            {
                var bottom = Math.Max(safe.Bottom, ime?.Bottom ?? 0);
                if (view.PaddingLeft != safe.Left || view.PaddingTop != safe.Top || view.PaddingRight != safe.Right || view.PaddingBottom != bottom)
                {
                    Services.AppLog.Info("insets", $"Safe area {safe.Left},{safe.Top},{safe.Right},{bottom}");
                    view.SetPadding(safe.Left, safe.Top, safe.Right, bottom);
                }
            }
            // The pages below are inside the padding already: they get no inset of their own.
            return AndroidX.Core.View.WindowInsetsCompat.Consumed!;
        }
    }

    private static AndroidSecondaryDisplayHost? SecondaryDisplayHost() =>
        IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>() as AndroidSecondaryDisplayHost;

    /// <summary>Console apps are fullscreen: hide status/navigation bars (swipe reveals them transiently).</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) ClaimFocusIfFree();
        if (!hasFocus || Window is not { DecorView: { } decorView } window) return;
        if (AndroidX.Core.View.WindowCompat.GetInsetsController(window, decorView) is not { } controller) return;
        controller.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
    }

    /// <summary>
    /// After a touch Android is in touch mode, and the first directional key only takes it
    /// out of it: when no view holds focus, Android hands focus to one and swallows the key
    /// before <see cref="DispatchKeyEvent"/> ever sees it. So the first press after a tap did
    /// nothing, on every screen. A one-pixel view that may hold focus in touch mode keeps it
    /// whenever nothing else does; a text field that is tapped still takes it as usual.
    /// </summary>
    private Android.Views.View? _focusKeeper;

    private void ClaimFocusIfFree()
    {
        if (Window?.DecorView is not ViewGroup decor) return;
        if (_focusKeeper is null || _focusKeeper.Parent is null)
        {
            _focusKeeper ??= new Android.Views.View(this)
            {
                Focusable = true,
                FocusableInTouchMode = true,
                ImportantForAccessibility = ImportantForAccessibility.No,
            };
            (_focusKeeper.Parent as ViewGroup)?.RemoveView(_focusKeeper);
            decor.AddView(_focusKeeper, new ViewGroup.LayoutParams(1, 1));
        }
        if (CurrentFocus is null or ViewGroup) _focusKeeper.RequestFocus();
    }

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        var handled = base.DispatchTouchEvent(e);
        if (e?.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel) ClaimFocusIfFree();
        return handled;
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        // Enter typed into a text field belongs to the field (it finishes the text), not to the
        // page as A: in the editor's Nickname field it opened "Unsaved changes" and then picked
        // the Pokémon up (QA 2026-11-12). The pad's own A button still acts as A everywhere.
        if (e is { KeyCode: Keycode.Enter or Keycode.NumpadEnter } && CurrentFocus is Android.Widget.EditText)
            return base.DispatchKeyEvent(e);
        if (e?.Action == KeyEventActions.Down && ResolveButton(e.KeyCode) is { } button)
        {
            // Direction and L/R repeats are owned by the app's own timers (the hat timer
            // for stick-style input, the key timer for clean digital pads); framework
            // repeats would stack on top and double the navigation speed.
            if (e.RepeatCount > 0)
                return IsRepeatable(button);
            var router = IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>();
            // Asked before dispatching: the press itself may open another screen.
            var repeats = IsDirectional(button) || (IsRepeatable(button) && router?.TopPagesWithShoulders == true);
            if (router?.Dispatch(button) == true)
            {
                Haptic();
                if (repeats)
                    StartKeyRepeat(button);
                return true;
            }
            // Directional keys must never fall through to Android's native focus search:
            // it draws the grey selection rectangles on the home shelf. Touch stays the
            // only pointer input, so there is nothing useful to hand over.
            if (IsDirectional(button))
                return true;
        }
        if (e?.Action == KeyEventActions.Up && ResolveButton(e.KeyCode) is { } released)
        {
            if (released == _keyRepeatButton) StopKeyRepeat();
            IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>()?.DispatchRelease(released);
        }
        return base.DispatchKeyEvent(e);
    }

    private Services.PadButton? _keyRepeatButton;
    private Java.Lang.Runnable? _keyRepeatRunnable;

    /// <summary>Hold-repeat for dpad directions that arrive as key events (some pads report
    /// clean digital keys with no framework repeats at all), and for L/R: holding a shoulder
    /// flips through boxes and pages without tapping it again and again.</summary>
    private void StartKeyRepeat(Services.PadButton button)
    {
        StopKeyRepeat();
        _keyRepeatButton = button;
        _keyRepeatRunnable = new Java.Lang.Runnable(() =>
        {
            if (_keyRepeatButton != button) return;
            var router = IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>();
            if (!IsDirectional(button) && router?.TopPagesWithShoulders != true) { StopKeyRepeat(); return; }
            if (router?.Dispatch(button) != true) { StopKeyRepeat(); return; }
            _hatRepeatHandler.PostDelayed(_keyRepeatRunnable!, IsDirectional(button) ? HatRepeatMs : ShoulderRepeatMs);
        });
        _hatRepeatHandler.PostDelayed(_keyRepeatRunnable, IsDirectional(button) ? HatHoldDelayMs : ShoulderHoldDelayMs);
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
    // A whole box or page changes per step: a slower cadence keeps each one readable.
    private const long ShoulderHoldDelayMs = 380;
    private const long ShoulderRepeatMs = 140;

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        // D-pad-emulating hats (the Thor's controller) arrive as axis motion, and the
        // device only reports CHANGES: a statically held direction produces no further
        // events. The repeat therefore needs its own timer, exactly like the native
        // joystick navigation this replaces: step once on deflection, then keep
        // stepping after a hold delay at a fixed cadence until release.
        if (e is { Action: MotionEventActions.Move })
        {
            // The analog sticks, for a screen that reads them (the Poképark's camera). The hat
            // below still sees every event: its release is what stops a held direction repeating.
            var stickRead = e.IsFromSource(InputSourceType.Joystick)
                && IPlatformApplication.Current?.Services.GetService<Services.GamepadRouter>() is { } sticks
                && sticks.DispatchStick(StickAxis(e, Axis.X), StickAxis(e, Axis.Y), StickAxis(e, Axis.Z), StickAxis(e, Axis.Rz));
            var direction = HatDirection(e);
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
            if (direction is not null || stickRead)
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

    private static bool IsRepeatable(Services.PadButton button) =>
        IsDirectional(button) || button is Services.PadButton.L or Services.PadButton.R;

    // A stick's axis past its dead zone (the device's own flat, at least 0.25: worn sticks drift), rescaled to start at 0.
    private static float StickAxis(MotionEvent e, Axis axis)
    {
        var value = e.GetAxisValue(axis);
        var flat = Math.Max(.25f, e.Device?.GetMotionRange(axis, e.Source)?.Flat ?? 0);
        var size = Math.Abs(value);
        return size <= flat ? 0 : Math.Sign(value) * Math.Min(1, (size - flat) / (1 - flat));
    }

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
        Keycode.ButtonSelect => Services.PadButton.Select,
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
/// The second screen goes on the panel PKForge does not run on. Android refuses a
/// Presentation on the default display, so PKForge itself must be there: on Gamma OS
/// (Anbernic RG DS) that is the lower panel and the second screen is the top one, which
/// only the hints' wording has to know (<see cref="Arrangement"/>).
/// </summary>
public sealed class AndroidSecondaryDisplayHost(IServiceProvider services) : ISecondaryDisplayHost
{
    private readonly object _showGate = new();
    private PagePresentation? _presentation;
    private ContentPage? _page;
    private bool _resumeAfterActivityPause;
    private ScreenArrangement _arrangement = ScreenArrangement.MainOnTop;

    public bool IsAvailable => Services.SecondScreenMode.Allowed && ResolveDisplay() is not null;

    /// <summary>Measured when the second screen is shown; the Thor's wording until then.</summary>
    public ScreenArrangement Arrangement => _arrangement;

    /// <summary>True on a dual-screen handheld when PKForge runs on a panel other than the main one.</summary>
    internal static bool LaunchedOnLowerScreen(Activity activity) =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
        && activity.Display is { } display && display.DisplayId != Display.DefaultDisplay
        && IsDualScreenHandheld();

    /// <summary>
    /// How the default display sits against the panel <paramref name="activity"/> runs on when
    /// that is another one: the Thor's large top panel over its small lower one, or unknown.
    /// </summary>
    internal static ScreenArrangement ArrangementFrom(Activity activity)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || activity.Display is not { } own
            || activity.GetSystemService(Android.Content.Context.DisplayService) is not DisplayManager manager
            || manager.GetDisplay(Display.DefaultDisplay) is not { } main)
            return ScreenArrangement.MainOnTop;
        return Panel(main) is { } top && Panel(own) is { } below ? DualScreenLayout.Arrange(top, below) : ScreenArrangement.MainOnTop;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static ScreenPanel? Panel(Display display) =>
        display.GetMode() is { PhysicalWidth: > 0, PhysicalHeight: > 0 } mode
            ? new ScreenPanel(display.DisplayId, mode.PhysicalWidth, mode.PhysicalHeight)
            : null;

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var display = ResolveDisplay() ?? throw new InvalidOperationException("No secondary display is available.");
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No foreground Android activity is available.");

        lock (_showGate)
        {
            if (_presentation is { IsShowing: true })
            {
                if (_page is Views.PokeparkJournalPage journalPage)
                    _ = journalPage.RefreshAsync(cancellationToken);
                return ValueTask.CompletedTask;
            }

            // A dismissed presentation (SAF picker, sleep) cannot be reshown;
            // rebuild the page and presentation as one serialized operation.
            _presentation?.Dismiss();
            if (_page is Views.SecondScreenBoxPage boxPage)
                boxPage.Cleanup();
            else if (_page is Views.PokeparkJournalPage journalPage)
                journalPage.Cleanup();
            _presentation = null;
            _page = null;
            if (!Services.SecondScreenMode.Allowed) return ValueTask.CompletedTask;

            // Read by the page's hints ("summary on the top screen"), so set before it is built.
            if (OperatingSystem.IsAndroidVersionAtLeast(30) && activity.Display is { } own
                && Panel(own) is { } main && Panel(display) is { } second)
                _arrangement = DualScreenLayout.Arrange(main, second);

            // Built and shown as locals: a display Android refuses (a virtual or removed one
            // raises BadToken/InvalidDisplay) leaves nothing half-built, and PKForge carries
            // on with one screen instead of crashing.
            var page = services.GetRequiredService<Views.SecondScreenBoxPage>();
            var presentation = new PagePresentation(activity, display, page, services);
            try
            {
                presentation.Show();
            }
            catch (Exception error) when (error is WindowManagerBadTokenException or WindowManagerInvalidDisplayException or Java.Lang.Exception)
            {
                page.Cleanup();
                Services.SecondScreenMode.DisableForSession($"display {display.DisplayId} ({display.Name}) refused the lower screen", error);
                return ValueTask.CompletedTask;
            }
            _page = page;
            _presentation = presentation;
            Services.AppLog.Info("second", $"Second screen on display {display.DisplayId} ({display.Name}, flags 0x{(int)display.Flags:X}), main on {(OperatingSystem.IsAndroidVersionAtLeast(30) ? activity.Display?.DisplayId : Display.DefaultDisplay)}, {_arrangement}");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowPokeparkJournalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The lower display is created by HomePage. Never construct a cold Android
        // Presentation while navigating into Poképark: Presentation/MAUI inflation
        // is synchronous on the UI thread and can stall the primary window.
        lock (_showGate)
        {
            if (_presentation is { IsShowing: true })
            {
                if (_page is Views.SecondScreenBoxPage boxPage)
                    _ = boxPage.RefreshPokeparkJournalAsync(cancellationToken);
                else if (_page is Views.PokeparkJournalPage journalPage)
                    _ = journalPage.RefreshAsync(cancellationToken);
                return ValueTask.CompletedTask;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshPokeparkJournalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_showGate)
        {
            if (_presentation is not { IsShowing: true } || _page is not Views.PokeparkJournalPage)
                return ValueTask.CompletedTask;
            return ((Views.PokeparkJournalPage)_page).RefreshAsync(cancellationToken);
        }
    }

    public ValueTask DismissAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dismiss();
        _resumeAfterActivityPause = false;
        StopWaitingForDisplay();
        return ValueTask.CompletedTask;
    }

    internal void SuspendForActivityPause()
    {
        // Not IsShowing: going to sleep turns the lower display off, and Android may already
        // have dismissed the Presentation by the time the activity pauses.
        _resumeAfterActivityPause |= _presentation is not null;
        StopWaitingForDisplay();
        Dismiss();
    }

    internal void ResumeAfterActivityPause()
    {
        if (!_resumeAfterActivityPause)
            return;
        if (ResolveDisplay() is null)
        {
            // Waking from sleep resumes the activity before the lower display is back on:
            // wait for it instead of giving the lower screen up.
            WaitForDisplay();
            return;
        }

        _resumeAfterActivityPause = false;
        StopWaitingForDisplay();
        try { _ = ShowAsync(); }
        catch { /* A removed or unavailable secondary display must not break resume. */ }
    }

    private DisplayWaiter? _displayWaiter;

    private void WaitForDisplay()
    {
        if (_displayWaiter is not null) return;
        if (Platform.AppContext.GetSystemService(Android.Content.Context.DisplayService) is not DisplayManager manager) return;
        _displayWaiter = new DisplayWaiter(manager, ResumeAfterActivityPause);
        manager.RegisterDisplayListener(_displayWaiter, new Handler(Looper.MainLooper!));
    }

    private void StopWaitingForDisplay()
    {
        if (_displayWaiter is null) return;
        _displayWaiter.Manager.UnregisterDisplayListener(_displayWaiter);
        _displayWaiter = null;
    }

    private sealed class DisplayWaiter(DisplayManager manager, Action retry) : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public DisplayManager Manager => manager;
        public void OnDisplayAdded(int displayId) => retry();
        public void OnDisplayChanged(int displayId) => retry();
        public void OnDisplayRemoved(int displayId) { }
    }

    private void Dismiss()
    {
        lock (_showGate)
        {
            _presentation?.Dismiss();
            _presentation = null;
            if (_page is Views.SecondScreenBoxPage boxPage)
                boxPage.Cleanup();
            else if (_page is Views.PokeparkJournalPage journalPage)
                journalPage.Cleanup();
            _page = null;
        }
    }

    // Dual-screen handhelds whose built-in lower panel is not tagged as a presentation
    // display (the AYN Thor's is not): only on these may an untagged display be used.
    private static readonly string[] DualScreenMakers = ["AYN", "AYANEO", "Retroid", "Anbernic"];

    // Names Android gives displays that are not a second panel of this device: casting,
    // screen recording, desktop modes (Motorola Ready For, Samsung DeX), developer overlays.
    private static readonly string[] VirtualHints = ["virtual", "overlay", "cast", "wifi", "wireless", "ready for", "dex", "mirror", "record"];

    // Debug builds on the Android emulator take a developer overlay display
    // (settings put global overlay_display_devices) for a handheld's second panel.
    private static readonly bool EmulatedPanels =
#if DEBUG
        Android.OS.Build.Hardware is "ranchu" or "goldfish";
#else
        false;
#endif

    private static bool IsDualScreenHandheld() =>
        EmulatedPanels || DualScreenMakers.Any(m => (Android.OS.Build.Manufacturer ?? "").Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A panel the second screen may use: not <paramref name="own"/>, on, public, and not a
    /// virtual display. Never the default display either: Android refuses a Presentation
    /// there, so PKForge running on another panel keeps a single screen instead of failing.
    /// </summary>
    private static bool Usable(Display d, int own) =>
        d.DisplayId != own && d.DisplayId != Display.DefaultDisplay && d.IsValid && d.State != DisplayState.Off
        && (d.Flags & DisplayFlags.Private) == 0
        && !VirtualHints.Any(hint => (d.Name ?? "").Contains(hint, StringComparison.OrdinalIgnoreCase)
            && !(EmulatedPanels && hint == "overlay"));

    /// <summary>
    /// The display for the lower screen, or null. Never the display PKForge itself is on,
    /// never an invalid, off or private one, and never one whose name says it is virtual:
    /// phones expose casting and desktop-mode displays that a Presentation cannot live on.
    /// </summary>
    private static Display? ResolveDisplay()
    {
        var manager = (DisplayManager?)Platform.AppContext.GetSystemService(Android.Content.Context.DisplayService);
        if (manager is null) return null;
        // Before Android 11 an activity cannot say its display; it is then the default one.
        var own = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? Platform.CurrentActivity?.Display?.DisplayId ?? Display.DefaultDisplay
            : Display.DefaultDisplay;
        // Running on the lower panel: the main one must never become the "second screen".
        if (Platform.CurrentActivity is { } current && LaunchedOnLowerScreen(current)) return null;

        var presentation = manager.GetDisplays(DisplayManager.DisplayCategoryPresentation)?.FirstOrDefault(d => Usable(d, own));
        if (presentation is not null)
            return presentation;

        // Thor fallback: its built-in bottom screen is not presentation-tagged.
        if (!IsDualScreenHandheld())
            return null;
        return manager.GetDisplays()?.FirstOrDefault(d => Usable(d, own));
    }

    private sealed class PagePresentation(Activity activity, Display display, ContentPage page, IServiceProvider services)
        : Presentation(activity, display)
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window?.AddFlags(WindowManagerFlags.Fullscreen);
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            // Touch on the lower screen must not steal key focus from the main window: a focused
            // Presentation took the pad's B/back itself and dismissed as a dialog, leaving the
            // launcher on the lower screen. It still receives touch while unfocusable.
            Window?.AddFlags(WindowManagerFlags.NotFocusable);

            // Inflate with the Activity as context (not the Presentation's dialog context)
            // so MAUI handlers resolve fonts/drawables registered against the Activity.
            var mauiContext = new Microsoft.Maui.MauiContext(services, activity);
            SetContentView(Microsoft.Maui.Platform.ElementExtensions.ToPlatform(page, mauiContext));
        }

        // Belt and braces for keys that still land here: they belong to the main activity.
        public override bool DispatchKeyEvent(KeyEvent e) => activity.DispatchKeyEvent(e);
    }
}
