using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class AppForegroundTests
{
    [Fact]
    public void ARecreatedActivityThatOnlyActivatesStillBringsTheAppBack()
    {
        // Android recreated the activity: the old one stopped (OnSleep), the new window only
        // activated; OnResume never came. Bank Sync stayed paused until the app restarted.
        var app = new AppForeground();

        Assert.True(app.EnterBackground());
        Assert.True(app.EnterForeground());
    }

    [Fact]
    public void TheUsualReturnIsReportedOnce()
    {
        var app = new AppForeground();

        app.EnterBackground();

        Assert.True(app.EnterForeground());   // OnResume
        Assert.False(app.EnterForeground());  // then the window activates
    }

    [Fact]
    public void ActivatingWithoutHavingLeftIsNotAReturn()
    {
        var app = new AppForeground();

        Assert.False(app.EnterForeground());  // first launch, or a dialog closing
    }

    [Fact]
    public void LeavingTwiceIsReportedOnce()
    {
        var app = new AppForeground();

        Assert.True(app.EnterBackground());
        Assert.False(app.EnterBackground());
        Assert.True(app.EnterForeground());
        Assert.True(app.EnterBackground());
    }
}
