namespace PKForge.Domain;

[Flags]
public enum ParkTraversal { Land = 1, Water = 2, Lava = 4, Air = 8 }
public enum ParkTerrain { Blocked, Land, Water, Lava, Air }
public readonly record struct ParkPoint(float X, float Y);
public sealed record ParkRegion(ParkTerrain Terrain, ParkPoint[] Points)
{
    public bool Contains(float x, float y)
    {
        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var a = Points[i]; var b = Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

/// <summary>Collision tracing of the approved paintings in the same 480×270 coordinates as rendering.
/// Regions are layered; later shapes override earlier ones. Unmapped pixels are never walkable.</summary>
public sealed class ParkMap(string asset, ParkRegion[] regions)
{
    public const int Width = 480, Height = 270;
    public string Asset { get; } = asset;
    public IReadOnlyList<ParkRegion> Regions { get; } = regions;
    private readonly ParkTerrain[] _terrain = Rasterize(regions);
    private static ParkTerrain[] Rasterize(ParkRegion[] regions)
    {
        var pixels = new ParkTerrain[Width * Height];
        for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++)
            foreach (var region in regions)
                if (region.Contains(x + .5f, y + .5f)) pixels[y * Width + x] = region.Terrain;
        return pixels;
    }
    public ParkTerrain TerrainAt(float x, float y) => x < 0 || y < 0 || x >= Width || y >= Height
        ? ParkTerrain.Blocked : _terrain[(int)y * Width + (int)x];
    public static ParkTraversal TraversalFor(ParkTerrain terrain) => terrain switch
    {
        ParkTerrain.Land => ParkTraversal.Land, ParkTerrain.Water => ParkTraversal.Water,
        ParkTerrain.Lava => ParkTraversal.Lava, ParkTerrain.Air => ParkTraversal.Air, _ => 0
    };

    // Test the whole sprite envelope, not just its feet. This deliberately keeps
    // residents in front of painted props instead of drawing them over tree crowns.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ParkTraversal, int[]> _clearance = new();
    public bool CanOccupy(float x, float y, ParkTraversal allowed, float radius = 8)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(radius) || radius < 0) return false;
        var left = (int)MathF.Floor(x - radius); var right = (int)MathF.Ceiling(x + radius);
        var top = (int)MathF.Floor(y - radius * 2.5f); var bottom = (int)MathF.Ceiling(y + radius * .5f);
        if (left < 0 || top < 0 || right >= Width || bottom >= Height) return false;
        // Summed-area collision table: full-body clearance is constant-time even
        // during route planning; no per-frame pixel scan or managed allocations.
        var sums = _clearance.GetOrAdd(allowed, BuildClearance);
        const int stride = Width + 1;
        return sums[(bottom + 1) * stride + right + 1] - sums[top * stride + right + 1]
            - sums[(bottom + 1) * stride + left] + sums[top * stride + left] == 0;
    }
    private int[] BuildClearance(ParkTraversal allowed)
    {
        const int stride = Width + 1;
        var sums = new int[stride * (Height + 1)];
        for (var y = 0; y < Height; y++)
        {
            var row = 0;
            for (var x = 0; x < Width; x++)
            {
                if ((TraversalFor(_terrain[y * Width + x]) & allowed) == 0) row++;
                sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + row;
            }
        }
        return sums;
    }
    public static ParkMap For(int environmentIndex) => Maps[Math.Clamp(environmentIndex, 0, 3)];
    private static ParkRegion Poly(ParkTerrain terrain, params float[] xy) => new(terrain,
        Enumerable.Range(0, xy.Length / 2).Select(i => new ParkPoint(xy[i * 2], xy[i * 2 + 1])).ToArray());
    private static ParkRegion Obstacle(float x, float y, float rx, float ry) => new(ParkTerrain.Blocked,
        Enumerable.Range(0, 24).Select(i => new ParkPoint(x + rx * MathF.Cos(i * MathF.PI / 12), y + ry * MathF.Sin(i * MathF.PI / 12))).ToArray());
    private static readonly ParkMap[] Maps =
    [
        new("commons.png", [
            Poly(ParkTerrain.Land, 30,48, 216,48, 222,0, 257,0, 258,46, 310,46, 310,102, 451,102, 451,164, 434,177, 425,187, 445,213, 435,238, 279,235, 265,233, 265,270, 222,270, 222,229, 168,217, 137,209, 126,186, 92,175, 31,175),
            // Southwest sun-warmed clearing and southeast secluded grass.
            Poly(ParkTerrain.Land, 30,172, 81,172, 92,190, 126,209, 138,238, 101,251, 69,251, 36,221, 28,200),
            Poly(ParkTerrain.Land, 369,204, 418,204, 445,223, 441,250, 373,250, 359,229),
            Poly(ParkTerrain.Water, 334,35, 360,35, 363,25, 395,25, 407,35, 425,35, 430,45, 450,48, 450,78, 439,79, 439,92, 337,92, 337,79, 329,77, 329,47),
            Obstacle(326,121,19,24), Obstacle(344,143,16,19),
            Obstacle(386,185,25,21), Obstacle(419,183,23,20), Obstacle(363,213,21,26),
            Obstacle(438,238,22,21), Obstacle(381,258,25,20),
            Obstacle(166,102,8,8), Obstacle(177,107,4,4), Obstacle(78,212,10,7),
            Obstacle(98,185,20,23), Obstacle(119,200,20,26)
        ]),
        new("water.png", [
            Poly(ParkTerrain.Water, 128,27, 480,27, 480,270, 0,270, 0,176, 28,163, 47,145, 46,124, 57,117, 61,85, 69,70, 99,61, 102,44, 125,40),
            Poly(ParkTerrain.Land, 0,38, 78,38, 91,28, 121,26, 120,38, 99,44, 98,59, 65,67, 57,84, 54,116, 44,124, 44,141, 31,159, 0,174),
            Obstacle(7,67,17,25), Obstacle(74,17,30,22), Obstacle(18,19,35,22),
            Obstacle(69,147,9,8), Obstacle(85,155,5,5),
            // Cliff toes and waterfall splash face remain scenery, not swimming cells.
            Poly(ParkTerrain.Blocked, 112,0, 480,0, 480,44, 467,38, 458,25, 439,25, 426,30, 410,24, 400,31, 388,31, 384,24, 360,25, 353,31, 342,28, 325,27, 319,32, 304,32, 300,26, 276,25, 270,35, 223,35, 216,26, 200,27, 197,32, 183,32, 181,25, 128,25)
        ]),
        new("fire.png", [
            Poly(ParkTerrain.Land, 33,32, 108,32, 108,0, 145,0, 147,34, 249,34, 248,60, 268,77, 305,80, 304,98, 290,110, 275,119, 268,148, 296,167, 322,176, 346,197, 369,204, 384,224, 385,243, 400,257, 400,270, 137,270, 133,239, 102,235, 99,224, 27,224, 25,177, 22,151, 27,125, 18,96, 25,72),
            Poly(ParkTerrain.Land, 23,173, 74,173, 93,193, 108,229, 96,254, 81,270, 0,270, 0,191),
            Poly(ParkTerrain.Lava, 370,57, 394,58, 403,47, 426,49, 433,63, 459,79, 467,91, 450,115, 451,166, 430,194, 430,224, 415,233, 399,226, 383,205, 360,199, 339,180, 332,173, 302,164, 277,147, 282,125, 309,110, 319,85, 347,79, 350,64),
            Obstacle(77,69,13,15), Obstacle(298,215,12,13),
            Obstacle(57,190,19,22), Obstacle(10,204,18,27),
            Poly(ParkTerrain.Blocked, 235,17, 311,14, 345,39, 327,52, 327,65, 279,70, 248,58)
        ]),
        new("sky.png", [
            Poly(ParkTerrain.Air, 0,0, 480,0, 480,270, 0,270),
            // The painted cliff silhouette is blocked even to fliers; air routes go around it.
            Poly(ParkTerrain.Blocked, 82,76, 108,48, 139,29, 151,0, 192,0, 214,38, 264,27, 280,8, 317,14, 326,23, 373,22, 397,49, 408,79, 408,98, 395,120, 389,161, 365,187, 327,213, 280,234, 267,251, 276,270, 202,270, 215,246, 213,238, 174,233, 137,218, 114,195, 97,181, 93,153, 78,141, 72,115),
            Poly(ParkTerrain.Land, 93,75, 119,52, 148,40, 198,48, 218,41, 262,39, 292,29, 315,33, 367,30, 388,51, 392,79, 379,100, 388,112, 381,140, 358,165, 334,184, 301,200, 269,213, 251,230, 253,250, 266,270, 211,270, 230,247, 225,229, 218,217, 178,213, 143,200, 124,180, 111,163, 101,146, 90,128, 89,96),
            Poly(ParkTerrain.Blocked, 131,49, 151,4, 176,5, 204,43, 197,57, 176,65, 148,58),
            Poly(ParkTerrain.Blocked, 261,38, 282,13, 303,17, 318,43, 311,57, 280,58),
            Obstacle(121,109,18,19), Obstacle(135,147,18,21),
            Obstacle(177,128,10,7), Obstacle(352,67,10,8)
        ])
    ];
}
