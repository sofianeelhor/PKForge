using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class AppUpdateRulesTests
{
    [Theory]
    [InlineData("1.3.0", "v1.3.1", true)]
    [InlineData("1.3.0", "v1.10.0", true)]
    [InlineData("1.3.0", "v1.3.0", false)]
    [InlineData("1.3.0", "v1.2.9", false)]
    [InlineData("1.4.0", "v1.4.0-beta+build.1", false)]
    [InlineData("garbage", "v1.4.0", false)]
    public void ComparesStableReleaseTags(string installed, string candidate, bool expected) =>
        Assert.Equal(expected, AppUpdateRules.IsNewerVersion(installed, candidate));

    [Fact]
    public void SkippedReleaseDoesNotPromptButNewerReleaseDoes()
    {
        Assert.False(AppUpdateRules.ShouldPromptAutomatically("1.3.0", "1.3.1", "1.3.1"));
        Assert.True(AppUpdateRules.ShouldPromptAutomatically("1.3.0", "1.4.0", "1.3.1"));
        Assert.False(AppUpdateRules.ShouldPromptAutomatically("1.3.0", "1.3.0", ""));
    }
}
