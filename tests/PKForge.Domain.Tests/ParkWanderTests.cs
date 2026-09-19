using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public class ParkWanderTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void StrollsStayOnAllowedTerrainIncludingBetweenWaypoints(int environment)
    {
        var map = ParkMap.For(environment);
        foreach (var allowed in new[] { ParkTraversal.Land, ParkTraversal.Land | ParkTraversal.Water,
                     ParkTraversal.Land | ParkTraversal.Lava, ParkTraversal.Land | ParkTraversal.Air })
        for (var seed = 0; seed < 8; seed++)
        {
            var preferred = allowed.HasFlag(ParkTraversal.Water) ? ParkTerrain.Water
                : allowed.HasFlag(ParkTraversal.Lava) ? ParkTerrain.Lava
                : allowed.HasFlag(ParkTraversal.Air) ? ParkTerrain.Air : ParkTerrain.Land;
            var wander = new ParkWander(map, allowed, preferred, seed, 12);
            for (long t = 0; t < 600000; t += 233)
            {
                var state = wander.GetState(t);
                Assert.True(map.CanOccupy(state.X, state.Y, allowed, 12), $"Map {environment}, {allowed}, seed {seed}, t={t}: {state}");
            }
        }
    }

    [Theory]
    [InlineData(0, 166, 102)]
    [InlineData(1, 7, 67)]
    [InlineData(2, 77, 69)]
    [InlineData(3, 121, 109)]
    public void PaintedObstaclesAndMapEdgesBlockEveryTraversal(int environment, float x, float y)
    {
        var map = ParkMap.For(environment);
        const ParkTraversal all = ParkTraversal.Land | ParkTraversal.Water | ParkTraversal.Lava | ParkTraversal.Air;
        Assert.Equal(ParkTerrain.Blocked, map.TerrainAt(x, y));
        Assert.False(map.CanOccupy(x, y, all, 12));
        foreach (var point in new[] { new ParkPoint(-1, 100), new ParkPoint(481, 100),
                     new ParkPoint(100, -1), new ParkPoint(100, 271), new ParkPoint(3, 100), new ParkPoint(100, 8) })
            Assert.False(map.CanOccupy(point.X, point.Y, all, 12));
    }

    [Fact]
    public void SamplingIsRepeatableAndDoesNotDependOnTimeOrder()
    {
        var a = new ParkWander(ParkMap.For(0), ParkTraversal.Land, ParkTerrain.Land, 123, 12);
        var b = new ParkWander(ParkMap.For(0), ParkTraversal.Land, ParkTerrain.Land, 123, 12);
        foreach (var time in new long[] { 0, 100, 9321398, -100, long.MaxValue, long.MinValue, 0, 100 })
            Assert.Equal(a.GetState(time), b.GetState(time));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MovementIsContinuousAndIncludesMeaningfulStrollsAndRests(int environment)
    {
        var allowed = environment == 1 ? ParkTraversal.Land | ParkTraversal.Water : ParkTraversal.Land;
        var preferred = environment == 1 ? ParkTerrain.Water : ParkTerrain.Land;
        var wander = new ParkWander(ParkMap.For(environment), allowed, preferred, 92, 12);
        var moving = 0; var resting = 0;
        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;
        for (long time = 0; time < 900000; time += 17)
        {
            var a = wander.GetState(time); var b = wander.GetState(time + 17);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            Assert.InRange(MathF.Sqrt(dx * dx + dy * dy), 0, .155f);
            if (a.IsWalking) moving++; else resting++;
            if (!a.IsWalking && !b.IsWalking) { Assert.Equal(a.X, b.X); Assert.Equal(a.Y, b.Y); }
            // Only compare directions away from a waypoint/turn boundary.
            var c = wander.GetState(time + 8);
            if (a.IsWalking && b.IsWalking && c.Facing == a.Facing && a.Facing == b.Facing && (dx != 0 || dy != 0))
                Assert.Equal(a.Facing, ParkMotion.Facing(dx, dy));
            minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, a.X);
            minY = Math.Min(minY, a.Y); maxY = Math.Max(maxY, a.Y);
        }
        Assert.True(moving > 0 && resting > 0);
        Assert.True(maxX - minX > 48 || maxY - minY > 48);
    }
}
