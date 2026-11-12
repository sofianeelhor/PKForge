using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// Errors used to reach the player as raw exception messages ("Could not find file
/// '/storage/emulated/0/…'", "Object reference not set…"). They now say what happened and
/// what to do, and PKForge's own refusals, already written for the player, pass through.
/// </summary>
public sealed class ErrorTextTests
{
    [Fact]
    public void AMissingFileSaysSoWithoutItsPath()
    {
        var text = ErrorText.Plain(new FileNotFoundException("Could not find file '/storage/emulated/0/ROMs/nds/Platinum.sav'."));
        Assert.Equal(ErrorText.Missing, text);
        Assert.DoesNotContain("/storage", text);
    }

    [Fact]
    public void ALostPermissionTellsWhatToDo() =>
        Assert.Equal(ErrorText.NoAccess, ErrorText.Plain(new UnauthorizedAccessException("Permission Denial: content://com.android.externalstorage")));

    [Fact]
    public void ABugIsNeverShownAsATypeName()
    {
        Assert.Equal(ErrorText.Unexpected, ErrorText.Plain(new NullReferenceException()));
        Assert.Equal(ErrorText.Unexpected, ErrorText.Plain(new InvalidOperationException("System.InvalidOperationException: Sequence contains no elements")));
    }

    [Fact]
    public void PKForgesOwnRefusalsPassThrough() =>
        Assert.Equal("The save was closed before the move was written.",
            ErrorText.Plain(new InvalidOperationException("The save was closed before the move was written.")));

    [Fact]
    public void APathInsideAnAuthoredMessageIsLeftOut()
    {
        var text = ErrorText.Plain(new InvalidDataException("Unexpected size for /storage/emulated/0/ROMs/x.sav"));
        Assert.DoesNotContain("/storage", text);
        Assert.EndsWith(".", text);
    }

    [Fact]
    public void ACancelIsJustACancel() =>
        Assert.Equal("Cancelled.", ErrorText.Plain(new OperationCanceledException()));
}
