using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ParkInteractionTests
{
    [Fact]
    public void WalkClockStopsDuringActivityAndResumesContinuously()
    {
        var activity = new ParkInteraction();
        activity.Begin(6500, "Play");
        Assert.Equal(6500, activity.MotionTime(6500));
        Assert.Equal(6500, activity.MotionTime(9000));
        Assert.Equal(6500, activity.MotionTime(12500));
        Assert.Equal(6501, activity.MotionTime(12501));
        Assert.False(activity.IsActive(12500));
    }

    [Fact]
    public void ReplacingActivityPreservesCurrentWalkPosition()
    {
        var activity = new ParkInteraction();
        activity.Begin(6500, "Snack");
        activity.Begin(8000, "relax");
        Assert.Equal("Relax", activity.Action);
        Assert.Equal(6500, activity.MotionTime(8000));
        Assert.Equal(6500, activity.MotionTime(15999));
        Assert.True(activity.IsActive(15999));
        Assert.Equal(6500, activity.MotionTime(16000));
        Assert.False(activity.IsActive(16000));
        activity.Begin(17000, "Talk");
        Assert.Equal(7500, activity.MotionTime(17000));
        Assert.Equal(7501, activity.MotionTime(23001));
    }
}
