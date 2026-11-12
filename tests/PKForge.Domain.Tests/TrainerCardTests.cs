using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class TrainerCardTests
{
    [Fact]
    public void BadgeMaskRoundTripsAndFirstNFillsInOrder()
    {
        var badges = TrainerBadgeBits.FromMask(0b1000_0101, 16);
        Assert.Equal(0b1000_0101, TrainerBadgeBits.ToMask(badges));
        Assert.Equal([true, true, true, false], TrainerBadgeBits.FirstN(3, 4));
    }

    [Fact]
    public void DateFieldOnlyAcceptsSupportedInRangeValues()
    {
        Assert.False(TrainerDateField.None.Accepts(new DateTime(2010, 1, 1)));
        var field = new TrainerDateField(true, null, true, TrainerDateField.DefaultMin, TrainerDateField.DefaultMax);
        Assert.True(field.Accepts(new DateTime(2010, 1, 1)));
        Assert.False(field.Accepts(new DateTime(1999, 12, 31)));
    }
}
