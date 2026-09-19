using PKForge.Domain;
using Xunit;
namespace PKForge.Domain.Tests;

public class ParkMotionTests
{
    [Theory]
    [InlineData(0, 1, 0)] [InlineData(1, 1, 1)] [InlineData(1, 0, 2)]
    [InlineData(1, -1, 3)] [InlineData(0, -1, 4)] [InlineData(-1, -1, 5)]
    [InlineData(-1, 0, 6)] [InlineData(-1, 1, 7)]
    public void FacingMatchesNativePmdRows(float x, float y, int row) => Assert.Equal(row, ParkMotion.Facing(x, y));

    [Fact]
    public void IdleHoldsPositionAndWalkingFacesTravel()
    {
        for (var i = 0; i < 12; i++)
        for (long t = 0; t < 80000; t += 20)
        {
            var a = ParkMotion.GetState(i, t); var b = ParkMotion.GetState(i, t + 10);
            Assert.InRange(Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y), 0, .1f);
            if (!a.IsWalking && !b.IsWalking) { Assert.Equal(a.X, b.X); Assert.Equal(a.Y, b.Y); }
            if (a.IsWalking && b.IsWalking && (a.X != b.X || a.Y != b.Y))
                Assert.Equal(ParkMotion.Facing(b.X - a.X, b.Y - a.Y), a.Facing);
            Assert.InRange(a.X, 20, 460); Assert.InRange(a.Y, 120, 235);
        }
    }

    [Fact]
    public void AllResidentsSpendTimeBothRestingAndWalking()
    {
        for (var i = 0; i < 12; i++)
        {
            var samples = Enumerable.Range(0, 800).Select(t => ParkMotion.GetState(i, t * 100L)).ToArray();
            Assert.Contains(samples, s => s.IsWalking); Assert.Contains(samples, s => !s.IsWalking);
        }
    }
}
