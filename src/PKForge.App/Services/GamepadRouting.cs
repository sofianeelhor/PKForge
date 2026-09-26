namespace PKForge.App.Services;

public enum PadButton { A, B, X, Y, Start, L, R, Up, Down, Left, Right, Select }

/// <summary>A screen that owns the gamepad while visible. Return false to let the system handle the press.</summary>
public interface IPadHandler
{
    bool OnPadButton(PadButton button);
}

/// <summary>A screen that also needs to know when a button is let go (hold gestures).</summary>
public interface IPadReleaseHandler
{
    void OnPadReleased(PadButton button);
}

/// <summary>
/// A screen where L/R step through boxes or pages: holding a shoulder keeps stepping.
/// Screens where L/R open something stay one press, one action.
/// </summary>
public interface IPadPagingHandler : IPadHandler
{
    /// <summary>False while L/R would write something (moving a held save box), so a hold never repeats a write.</summary>
    bool ShouldersRepeat => true;
}

/// <summary>
/// Routes physical buttons to the top-most visible screen. Pages push themselves in
/// OnAppearing and remove themselves in OnDisappearing, so mapping is always per-screen
/// and never falls through to stale handlers.
/// </summary>
public sealed class GamepadRouter
{
    private readonly List<IPadHandler> _stack = [];

    public void Push(IPadHandler handler)
    {
        _stack.Remove(handler);
        _stack.Add(handler);
    }

    public void Remove(IPadHandler handler) => _stack.Remove(handler);

    public bool Dispatch(PadButton button) => _stack.Count > 0 && _stack[^1].OnPadButton(button);

    /// <summary>True when the top-most screen pages with L/R (see <see cref="IPadPagingHandler"/>).</summary>
    public bool TopPagesWithShoulders => _stack.Count > 0 && _stack[^1] is IPadPagingHandler { ShouldersRepeat: true };

    /// <summary>Tells the top-most screen a button was released, when it listens for that.</summary>
    public void DispatchRelease(PadButton button)
    {
        if (_stack.Count > 0 && _stack[^1] is IPadReleaseHandler handler)
            handler.OnPadReleased(button);
    }
}
