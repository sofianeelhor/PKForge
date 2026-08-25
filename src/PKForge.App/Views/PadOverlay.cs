using PKForge.App.Services;

namespace PKForge.App.Views;

/// <summary>
/// Makes any modal overlay gamepad-safe. While it is alive it owns the top of the
/// <see cref="GamepadRouter"/>, so buttons can never leak to the page underneath (which
/// was popping the page or moving the hidden box cursor). By default B cancels, A
/// confirms, and every other button is swallowed; a handler can intercept first (e.g. a
/// numeric spinner using Up/Down). Dispose on close to hand the pad back.
/// </summary>
public sealed class PadOverlay : IPadHandler, IDisposable
{
    private readonly GamepadRouter? _router;
    private readonly Action _cancel;
    private readonly Action _confirm;
    private readonly Func<PadButton, bool>? _intercept;
    private bool _released;

    public PadOverlay(Action cancel, Action confirm, Func<PadButton, bool>? intercept = null)
    {
        _cancel = cancel;
        _confirm = confirm;
        _intercept = intercept;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _router?.Push(this);
    }

    public bool OnPadButton(PadButton button)
    {
        if (_intercept is not null && _intercept(button)) return true;
        switch (button)
        {
            case PadButton.B: _cancel(); return true;
            case PadButton.A: _confirm(); return true;
            default: return true; // swallow everything so nothing reaches the page beneath
        }
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _router?.Remove(this);
    }
}
