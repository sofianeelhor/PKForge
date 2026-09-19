namespace PKForge.Domain;

/// <summary>A precomputed, closed stroll through reachable terrain. Sampling is allocation-free
/// and independent of frame rate or the order in which timestamps are requested.</summary>
public sealed class ParkWander
{
    private const int Cell = 8;
    private readonly record struct Leg(ParkPoint From, ParkPoint To, long Start, long Duration, int Facing);
    private readonly Leg[] _legs;
    private readonly long _duration;
    private readonly long _offset;
    private readonly ParkMap _map;

    public ParkWander(ParkMap map, ParkTraversal allowed, ParkTerrain preferredTerrain, int seed, float radius = 8)
    {
        _map = map;
        var random = new Random(seed);
        var columns = (int)ParkMap.Width / Cell;
        var rows = (int)ParkMap.Height / Cell;
        var points = new ParkPoint[columns * rows];
        var valid = new bool[points.Length];
        var candidates = new List<int>();
        var preferred = new List<int>();
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var i = y * columns + x;
            points[i] = new(x * Cell + Cell / 2f, y * Cell + Cell / 2f);
            var p = points[i];
            valid[i] = map.CanOccupy(p.X, p.Y, allowed, radius);
            if (!valid[i]) continue;
            candidates.Add(i);
            if (map.TerrainAt(p.X, p.Y) == preferredTerrain) preferred.Add(i);
        }
        if (candidates.Count == 0)
            throw new ArgumentException("This map has no traversable space for the resident.", nameof(allowed));

        // Check the entire edge, including diagonal corners, rather than just its endpoints.
        var edges = new List<int>[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            edges[i] = [];
            if (!valid[i]) continue;
            var x = i % columns;
            var y = i / columns;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0 || x + dx < 0 || x + dx >= columns || y + dy < 0 || y + dy >= rows) continue;
                var next = (y + dy) * columns + x + dx;
                if (!valid[next]) continue;
                if (dx != 0 && dy != 0 && (!valid[y * columns + x + dx] || !valid[(y + dy) * columns + x])) continue;
                var safe = true;
                for (var sample = 1; sample < 16; sample++)
                {
                    var t = sample / 16f;
                    var a = points[i]; var b = points[next];
                    if (map.CanOccupy(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, allowed, radius)) continue;
                    safe = false;
                    break;
                }
                if (safe) edges[i].Add(next);
            }
        }

        var spawnPool = preferred.Count > 0 ? preferred : candidates;
        var start = spawnPool[random.Next(spawnPool.Count)];
        var parents = new int[points.Length];
        var queue = new Queue<int>();
        void Search(int origin)
        {
            Array.Fill(parents, -1);
            parents[origin] = origin;
            queue.Clear(); queue.Enqueue(origin);
            while (queue.TryDequeue(out var i))
                foreach (var next in edges[i])
                    if (parents[next] == -1) { parents[next] = i; queue.Enqueue(next); }
        }
        Search(start);
        candidates.RemoveAll(i => parents[i] == -1);
        preferred.RemoveAll(i => parents[i] == -1);
        var legs = new List<Leg>();
        long clock = 0;
        var current = start;
        var facing = random.Next(8);
        var speed = 7f + (float)random.NextDouble() * 2;
        for (var stop = 0; stop < 9; stop++)
        {
            var p = points[current];
            var rest = random.Next(3000, 10001);
            legs.Add(new(p, p, clock, rest, facing)); clock += rest;
            var pool = preferred.Count > 1 && random.Next(5) != 0 ? preferred : candidates;
            var target = stop == 8 ? start : pool[random.Next(pool.Count)];
            // Encourage meaningful trips rather than frequently picking adjacent cells.
            for (var attempt = 0; stop < 8 && attempt < 12 && Distance(points[current], points[target]) < 48; attempt++)
                target = pool[random.Next(pool.Count)];
            Search(current);
            var route = new List<int>();
            for (var step = target; step != current; step = parents[step]) route.Add(step);
            route.Reverse();
            foreach (var next in route)
            {
                var from = points[current]; var to = points[next];
                facing = ParkMotion.Facing(to.X - from.X, to.Y - from.Y);
                var duration = (long)Math.Ceiling(Distance(from, to) / speed * 1000);
                legs.Add(new(from, to, clock, duration, facing)); clock += duration;
                current = next;
            }
        }
        _legs = legs.ToArray(); _duration = clock;
        _offset = (long)(random.NextDouble() * clock);
    }

    public ParkResidentState GetState(long time)
    {
        var t = ((time % _duration + _duration) % _duration + _offset) % _duration;
        var low = 0; var high = _legs.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_legs[mid].Start <= t) low = mid; else high = mid - 1;
        }
        var leg = _legs[low];
        var progress = (t - leg.Start) / (float)leg.Duration;
        var x = leg.From.X + (leg.To.X - leg.From.X) * progress;
        var y = leg.From.Y + (leg.To.Y - leg.From.Y) * progress;
        var walking = leg.From != leg.To;
        var terrain = _map.TerrainAt(x, y);
        var activity = (terrain, walking) switch
        {
            (ParkTerrain.Water, true) => "Exploring the water",
            (ParkTerrain.Water, false) => "Relaxing in the shallows",
            (ParkTerrain.Lava, true) => "Exploring the warm pools",
            (ParkTerrain.Lava, false) => "Basking in volcanic warmth",
            (ParkTerrain.Air, true) => "Gliding on the mountain breeze",
            (ParkTerrain.Air, false) => "Drifting on a gentle updraft",
            (_, true) => "Taking a gentle stroll",
            _ => "Enjoying a peaceful break"
        };
        return new(x, y, walking, leg.Facing, walking ? "Curious" : "Content", activity);
    }

    private static float Distance(ParkPoint a, ParkPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
