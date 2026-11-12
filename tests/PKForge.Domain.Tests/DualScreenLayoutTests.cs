using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class DualScreenLayoutTests
{
    // The AYN Thor: a 1920×1080 top panel over a 1240×1080 lower one.
    private static readonly ScreenPanel ThorTop = new(0, 1920, 1080);
    private static readonly ScreenPanel ThorLower = new(2, 1240, 1080);
    // The Anbernic RG DS: two 640×480 panels; Gamma OS makes the lower one the default display.
    private static readonly ScreenPanel DsLower = new(0, 640, 480);
    private static readonly ScreenPanel DsTop = new(1, 640, 480);

    [Fact]
    public void TheThorKeepsItsTopAndLowerWording()
    {
        var arrangement = DualScreenLayout.Arrange(ThorTop, ThorLower);
        Assert.Equal(ScreenArrangement.MainOnTop, arrangement);
        Assert.Equal("THE TOP SCREEN", DualScreenLayout.MainScreenCaps(arrangement));
        Assert.Equal("the lower screen", DualScreenLayout.SecondScreenName(arrangement));
    }

    [Fact]
    public void EqualPanelsNeverClaimWhichIsOnTop()
    {
        var arrangement = DualScreenLayout.Arrange(DsLower, DsTop);
        Assert.Equal(ScreenArrangement.Unknown, arrangement);
        Assert.Equal("THE MAIN SCREEN", DualScreenLayout.MainScreenCaps(arrangement));
        Assert.Equal("the second screen", DualScreenLayout.SecondScreenName(arrangement));
    }

    [Fact]
    public void MainOnTheSmallerPanelIsNotCalledTheTop() =>
        Assert.Equal(ScreenArrangement.Unknown, DualScreenLayout.Arrange(ThorLower, ThorTop));
}
