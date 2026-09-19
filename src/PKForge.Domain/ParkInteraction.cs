namespace PKForge.Domain;

/// <summary>Pauses a resident's walk clock without jumping when an activity ends or is replaced.</summary>
public sealed class ParkInteraction
{
    private long _pausedBefore;
    public long Start { get; private set; }
    public string Action { get; private set; } = "Talk";
    public int Duration => Action == "Relax" ? 8000 : 6000;
    private bool _started;
    public bool IsActive(long time) => _started && time >= Start && time - Start < Duration;
    public long Elapsed(long time) => Math.Clamp(time - Start, 0, Duration);
    public long MotionTime(long time) => time - _pausedBefore - (_started ? Elapsed(time) : 0);
    public void Begin(long time, string action)
    {
        if (_started) _pausedBefore += Elapsed(time);
        Start = time;
        Action = action.ToLowerInvariant() switch { "snack" => "Snack", "play" => "Play", "relax" => "Relax", _ => "Talk" };
        _started = true;
    }
}
