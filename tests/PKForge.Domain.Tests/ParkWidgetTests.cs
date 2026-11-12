using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The home-screen widget (#52): its shape from the launcher cell, who stands on the grass
/// (taking turns every half hour), where, and which journal lines fit under them.
/// </summary>
public sealed class ParkWidgetTests
{
    private static ParkWidgetResident Mon(int i, bool sprite = true) =>
        new($"id{i}", $"Mon{i}", i + 1, 0, false, sprite ? $"{i}.png" : null);

    private static ParkWidgetSnapshot Park(int residents, params string[] moments) =>
        new(Enumerable.Range(0, residents).Select(i => Mon(i)).ToArray(), moments, DateTimeOffset.UnixEpoch);

    private static readonly DateTime Noon = new(2026, 11, 11, 12, 0, 0);

    [Theory]
    [InlineData(110, 110, ParkWidgetSize.Small)]
    [InlineData(180, 300, ParkWidgetSize.Small)]
    [InlineData(250, 110, ParkWidgetSize.Wide)]
    [InlineData(320, 180, ParkWidgetSize.Wide)]
    [InlineData(250, 250, ParkWidgetSize.Large)]
    public void Launcher_cells_pick_the_shape(int width, int height, ParkWidgetSize size) =>
        Assert.Equal(size, ParkWidget.SizeFor(width, height));

    [Theory]
    [InlineData(ParkWidgetSize.Small, 2)]
    [InlineData(ParkWidgetSize.Wide, 4)]
    [InlineData(ParkWidgetSize.Large, 6)]
    public void Each_shape_shows_its_share_of_residents(ParkWidgetSize size, int count)
    {
        var view = ParkWidget.Compose(Park(9), size, 160, 120, Noon);
        Assert.Equal(count, view.Placements.Count);
        Assert.Equal(count, view.Placements.Select(p => p.Resident.Id).Distinct().Count());
    }

    [Fact]
    public void Residents_take_turns_every_half_hour_and_everyone_gets_one()
    {
        var park = Park(5);
        var first = ParkWidget.Shown(park.Residents, ParkWidgetSize.Small, Noon);
        var same = ParkWidget.Shown(park.Residents, ParkWidgetSize.Small, Noon.AddMinutes(29));
        var next = ParkWidget.Shown(park.Residents, ParkWidgetSize.Small, Noon.AddMinutes(30));
        Assert.Equal(first, same);
        Assert.NotEqual(first, next);
        var seen = Enumerable.Range(0, 5).SelectMany(t => ParkWidget.Shown(park.Residents, ParkWidgetSize.Small, Noon.AddMinutes(30 * t))).Select(r => r.Id).ToHashSet();
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void Residents_without_a_sprite_are_never_placed()
    {
        var park = new ParkWidgetSnapshot([Mon(0), Mon(1, sprite: false), Mon(2)], [], DateTimeOffset.UnixEpoch);
        var view = ParkWidget.Compose(park, ParkWidgetSize.Wide, 200, 100, Noon);
        Assert.DoesNotContain(view.Placements, p => p.Resident.Id == "id1");
        Assert.Equal(2, view.Placements.Count);
    }

    [Fact]
    public void Residents_stand_on_the_grass_inside_the_scene_and_face_each_other()
    {
        var view = ParkWidget.Compose(Park(6), ParkWidgetSize.Large, 160, 160, Noon);
        Assert.All(view.Placements, p =>
        {
            Assert.InRange(p.FeetX, 1, view.Width - 1);
            Assert.InRange(p.FeetY, view.HorizonY + 1, view.Height - 1);
        });
        Assert.Equal(2, view.Placements.Select(p => p.FeetY).Distinct().Count());
        Assert.Contains(view.Placements, p => p.FacingLeft);
        Assert.Contains(view.Placements, p => !p.FacingLeft);
    }

    [Fact]
    public void The_newest_moments_fit_under_the_scene()
    {
        var view = ParkWidget.Compose(Park(3, "Pikachu and Eevee played tag by the pond all afternoon long", "b", "c", "d"), ParkWidgetSize.Large, 120, 120, Noon);
        Assert.Equal(3, view.Lines.Count);
        Assert.All(view.Lines, l => Assert.True(l.Length <= ParkWidget.LineCharacters(120)));
        Assert.EndsWith("…", view.Lines[0]);
        Assert.Single(ParkWidget.Compose(Park(3, "a", "b"), ParkWidgetSize.Wide, 200, 100, Noon).Lines);
    }

    [Fact]
    public void An_empty_park_invites_and_a_quiet_one_counts_its_residents()
    {
        Assert.Equal(ParkWidget.Fit(ParkWidget.EmptyLine, ParkWidget.LineCharacters(200)),
            Assert.Single(ParkWidget.Compose(ParkWidgetSnapshot.Empty, ParkWidgetSize.Wide, 200, 100, Noon).Lines));
        Assert.Equal("3 Pokémon live in the park", Assert.Single(ParkWidget.Compose(Park(3), ParkWidgetSize.Wide, 200, 100, Noon).Lines));
    }

    [Fact]
    public void The_sky_follows_the_clock()
    {
        Assert.Equal(ParkDayPhase.Day, ParkWidget.Compose(Park(1), ParkWidgetSize.Small, 80, 80, Noon).Phase);
        Assert.Equal(ParkDayPhase.Night, ParkWidget.Compose(Park(1), ParkWidgetSize.Small, 80, 80, Noon.AddHours(11)).Phase);
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("one two three four", 12, "one two…")]
    [InlineData("  spaced   out  ", 20, "spaced out")]
    public void Lines_are_shortened_on_a_word(string text, int characters, string expected) =>
        Assert.Equal(expected, ParkWidget.Fit(text, characters));
}
