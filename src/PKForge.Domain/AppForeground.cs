namespace PKForge.Domain;

/// <summary>
/// Pairs the app leaving the screen with coming back. Android does not always follow OnSleep with
/// OnResume: when it recreates the activity (a configuration change the activity doesn't handle,
/// or the activity reclaimed while the process lives on) the new window is only activated. Each
/// transition is reported once, however many callbacks announce it. Main thread only.
/// </summary>
public sealed class AppForeground
{
    private bool _background;

    /// <summary>The app left the screen. True when it was in front: run the going-away work.</summary>
    public bool EnterBackground()
    {
        if (_background) return false;
        _background = true;
        return true;
    }

    /// <summary>The app is in front (OnResume, or a window activated). True only when it had left.</summary>
    public bool EnterForeground()
    {
        if (!_background) return false;
        _background = false;
        return true;
    }
}
