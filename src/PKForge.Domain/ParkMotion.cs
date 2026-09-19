namespace PKForge.Domain;

public readonly record struct ParkResidentState(float X, float Y, bool IsWalking, int Facing, string Mood, string Activity);

/// <summary>Deterministic, continuous walking and resting cycles in a 480 by 270 meadow.</summary>
public static class ParkMotion
{
    public static ParkResidentState GetState(int index, long elapsedMs)
    {
        var phase = index % 3;
        var rest = 5000 + phase * 2500;
        const int walk = 6500;
        var leg = rest + walk;
        var t = (Math.Max(0, elapsedMs) + index * 1177L) % (leg * 4);
        var segment = (int)(t / leg);
        var local = t % leg;
        var x = 53 + index % 4 * 112;
        var y = 133 + index / 4 * 45;
        // A small rectangular stroll is continuous at every state transition.
        (float X, float Y)[] corners = [(x - 22, y - 7), (x + 22, y - 7), (x + 22, y + 7), (x - 22, y + 7)];
        var from = corners[segment];
        var to = corners[(segment + 1) % 4];
        var moving = local >= rest;
        var progress = moving ? (local - rest) / (float)walk : 0;
        var facing = moving ? Facing(to.X - from.X, to.Y - from.Y) : segment switch { 0 => 4, 1 => 2, 2 => 0, _ => 6 };
        var mood = phase switch { 0 => "Curious", 1 => "Content", _ => "Dreamy" };
        var activity = moving ? phase switch { 0 => "Exploring the meadow", 1 => "Taking a gentle stroll", _ => "Wandering in the fresh air" }
            : phase switch { 0 => "Watching the world go by", 1 => "Enjoying a peaceful break", _ => "Daydreaming in the grass" };
        return new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress, moving, facing, mood, activity);
    }

    /// <summary>PMDCollab's actual row order: S, SE, E, NE, N, NW, W, SW.</summary>
    public static int Facing(float dx, float dy)
    {
        if (dx == 0 && dy == 0) return 0;
        return ((int)Math.Round(Math.Atan2(dx, dy) / (Math.PI / 4)) + 8) % 8;
    }
}
