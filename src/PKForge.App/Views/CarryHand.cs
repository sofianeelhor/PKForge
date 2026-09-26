using System.Diagnostics;
using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The Pokémon in the player's hand while it moves between slots, shared by the save boxes
/// and the Bank: picked up with a little hop, it glides under the PKSM pointer to whichever
/// slot the cursor is on, a small shadow marking where it will land, and settles into its
/// new slot when dropped. Each frame only advances while something still moves.
/// </summary>
public sealed class CarryHand
{
    private readonly Spring _x = new(0);
    private readonly Spring _y = new(0);
    private readonly Spring _lift = new(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _last;
    private bool _holding;
    private Action<SKCanvas, SKRect>? _landing;
    private int _landingSlot = -1;

    /// <summary>
    /// Follows the page's carry state: starts a pick-up from <paramref name="origin"/> when a
    /// carry begins; when it ends, lands the Pokémon on slot <paramref name="cursorSlot"/>
    /// (<paramref name="cursor"/>), drawn by <paramref name="drawDropped"/>. The landing spot
    /// and the Pokémon are fixed at that moment: moving the cursor afterwards changes neither.
    /// </summary>
    public void Sync(bool carrying, SKRect origin, SKRect cursor, int cursorSlot, Action<SKCanvas, SKRect> drawDropped)
    {
        if (carrying && !_holding)
        {
            _holding = true;
            _landing = null;
            _landingSlot = -1;
            _x.Snap(origin.MidX);
            _y.Snap(origin.MidY);
            _lift.Snap(0);
            _last = _clock.ElapsedTicks;
        }
        else if (!carrying && _holding)
        {
            _holding = false;
            _landing = drawDropped;
            _landingSlot = cursorSlot;
            _x.Target = cursor.MidX;
            _y.Target = cursor.MidY;
            _last = _clock.ElapsedTicks;
        }
        if (_holding)
        {
            _x.Target = cursor.MidX;
            _y.Target = cursor.MidY;
        }
        _lift.Target = _holding ? 1 : 0;
    }

    /// <summary>True while the hand is drawn: holding, or still settling a dropped Pokémon.</summary>
    public bool Visible => _holding || _landing is not null;

    /// <summary>True while a dropped Pokémon is still settling onto <paramref name="slot"/>: that slot skips its own sprite meanwhile.</summary>
    public bool IsLandingOn(int slot) => !_holding && _landing is not null && slot == _landingSlot;

    /// <summary>
    /// Advances and draws the hand over the slot grid. Returns true while it still moves, so
    /// the caller asks for another frame.
    /// </summary>
    public bool Draw(SKCanvas canvas, float cell, Action<SKCanvas, SKRect> drawHeld)
    {
        if (!Visible) return false;
        var now = _clock.ElapsedTicks;
        var dt = Math.Clamp((float)((now - _last) / (double)Stopwatch.Frequency), 0, 0.05f);
        _last = now;
        _x.Step(dt, 700f, 52.9f);
        _y.Step(dt, 700f, 52.9f);
        _lift.Step(dt, 520f, _holding ? 30f : 45.6f); // a small hop up, a clean landing

        var lift = Math.Max(0, _lift.Value);
        var size = cell * 0.94f;
        var raise = cell * 0.34f * lift;
        var slot = SKRect.Create(_x.Value - size / 2, _y.Value - size / 2, size, size);

        // Where it will land: a flat shadow on the slot, smaller the higher it is.
        if (lift > 0.02f)
        {
            using var shadow = new SKPaint { Color = Pksm.LogoVoid.WithAlpha((byte)(90 * lift)), IsAntialias = true };
            var width = size * (0.62f - 0.12f * lift);
            canvas.DrawOval(SKRect.Create(slot.MidX - width / 2, slot.Bottom - size * 0.2f, width, size * 0.12f), shadow);
        }

        var held = new SKRect(slot.Left, slot.Top - raise, slot.Right, slot.Bottom - raise);
        (_holding ? drawHeld : _landing!)(canvas, held);

        if (_holding && AutopilotArt.Icon("ui:pointer_arrow.png") is { } pointer)
        {
            // The PKSM move pointer (its tip is the image's top-left corner) holds the Pokémon
            // from below-right, as the storage cursor does.
            var height = cell * 0.42f;
            var width = height * pointer.Width / pointer.Height;
            using var image = SKImage.FromBitmap(pointer);
            canvas.DrawImage(image, SKRect.Create(held.MidX + size * 0.12f, held.MidY + size * 0.08f, width, height),
                BoxGridRenderer.SpriteSampling);
        }

        var moving = !_x.Settled || !_y.Settled || !_lift.Settled;
        if (!_holding && !moving)
        {
            _landing = null;
            _landingSlot = -1;
        }
        return moving;
    }
}
