using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The restore point list is filtered by what each write was doing, read from the sentence
/// the write recorded, and dated in the player's own time.
/// </summary>
public sealed class RestorePointsTests
{
    [Theory]
    [InlineData("Move GIBLE: Box 2, Slot 18 -> Box 2, Slot 17", RestorePointKind.Move)]
    [InlineData("Moved 3 Pokémon from box 2 slot 13 on.", RestorePointKind.Move)]
    [InlineData("Edit CHARMELEON (Box 3, Slot 16)", RestorePointKind.Edit)]
    [InlineData("Met / origin updated", RestorePointKind.Edit)]
    [InlineData("Generated - legal.", RestorePointKind.New)]
    [InlineData("Random team of 6 placed in the boxes.", RestorePointKind.New)]
    [InlineData("Here's your gift. Take good care of it!", RestorePointKind.New)]
    [InlineData("Released 4 Pokémon. Bye-bye!", RestorePointKind.Release)]
    [InlineData("Pikachu arrived from the bank", RestorePointKind.Transfer)]
    [InlineData("3 Pokémon went back to the bank.", RestorePointKind.Transfer)]
    [InlineData("Every Apricorn set to 99.", RestorePointKind.Items)]
    [InlineData("Safety copy: the state right before this restore", RestorePointKind.Restore)]
    [InlineData("Snapshot: before the League", RestorePointKind.Snapshot)]
    [InlineData("Snapshot: restore test", RestorePointKind.Snapshot)]
    [InlineData(null, RestorePointKind.Other)]
    [InlineData("", RestorePointKind.Other)]
    public void TheSentenceAWriteRecordsNamesItsKind(string? description, RestorePointKind expected) =>
        Assert.Equal(expected, RestorePoints.KindOf(description));

    [Fact]
    public void EveryKindHasAFilterName()
    {
        foreach (var kind in Enum.GetValues<RestorePointKind>())
            Assert.False(string.IsNullOrWhiteSpace(RestorePoints.Label(kind)));
    }

    [Theory]
    [InlineData("2026-11-10T00:29:40+00:00", "just now")]
    [InlineData("2026-11-10T00:07:36+00:00", "22 min ago")]
    [InlineData("2026-11-09T22:15:00+00:00", "yesterday 23:15")]  // 23:15 on the 9th in +01:00
    [InlineData("2026-11-09T23:10:00+00:00", "today 00:10")]      // already the 10th for the player
    [InlineData("2026-11-06T13:00:00+00:00", "Friday 14:00")]
    [InlineData("2026-10-01T08:05:00+00:00", "Oct 1, 09:05")]
    [InlineData("2025-12-24T18:00:00+00:00", "Dec 24 2025, 19:00")]
    public void APointIsDatedInThePlayersOwnTime(string createdUtc, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 10, 1, 30, 0, TimeSpan.FromHours(1));
        Assert.Equal(expected, RestorePoints.When(DateTimeOffset.Parse(createdUtc, System.Globalization.CultureInfo.InvariantCulture), now));
    }
}
