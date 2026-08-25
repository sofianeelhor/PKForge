namespace PKForge.App.Services;

public enum PadButton { A, B, X, Y, Start, L, R, Up, Down, Left, Right }

/// <summary>A screen that owns the gamepad while visible. Return false to let the system handle the press.</summary>
public interface IPadHandler
{
    bool OnPadButton(PadButton button);
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
}
