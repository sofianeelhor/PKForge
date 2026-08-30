using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class FashionTests
{
    [Fact]
    public void SwordShieldLegalFashionUnlockUsesUpstreamLegalFilter()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(8);
        var save = Assert.IsType<SAV8SWSH>(session.SaveFile);
        Assert.True(session.SupportsLegalFashionUnlock);

        session.UnlockAllLegalFashion();

        // At least a standard eyewear entry is now owned; the upstream routine also
        // removes version-incompatible and unobtainable clothing before returning.
        Assert.NotEmpty(save.Fashion.GetIndexesOwnedFlag(FashionUnlock8.REGION_EYEWEAR));
    }

    [Fact]
    public void OtherFormatsRejectLegalFashionUnlock()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(7);
        Assert.False(session.SupportsLegalFashionUnlock);
        Assert.Throws<NotSupportedException>(session.UnlockAllLegalFashion);
    }
}
