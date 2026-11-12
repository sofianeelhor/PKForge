using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The sprite sizes (issue #48).</summary>
public sealed class ParkSpriteScaleTests
{

    [Theory]
    [InlineData(20, 1f)]
    [InlineData(34, 1f)]
    [InlineData(48, .8125f)]
    [InlineData(64, .6875f)]
    public void Big_sprites_are_drawn_smaller_but_still_bigger(float height, float scale)
    {
        Assert.Equal(scale, ParkSpriteScale.For(height));
        // Never a giant, never smaller than a middling one.
        Assert.InRange(height * ParkSpriteScale.For(height), Math.Min(height, ParkSpriteScale.Full), 48);
    }

    [Fact]
    public void Drawn_height_grows_with_the_sprite()
    {
        // Within the rounding (1/16 of the height) of a smooth curve that only goes up.
        for (var h = 10; h <= 120; h++)
        {
            var smooth = h <= ParkSpriteScale.Full ? h : ParkSpriteScale.Full + (h - ParkSpriteScale.Full) * ParkSpriteScale.Kept;
            Assert.InRange(h * ParkSpriteScale.For(h), smooth - h / 32f - .01f, smooth + h / 32f + .01f);
        }
    }
}
