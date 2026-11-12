using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The size chosen in the Widgets menu: kept for every widget (the Poképark and the living park
/// included), read back after the menu closes, and the layout it gives in any launcher tile.
/// </summary>
public sealed class WidgetLayoutTests
{
    [Fact]
    public void Every_widget_keeps_its_own_size_through_the_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pkforge-widgets-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new HomeWidgetSettings()
                .WithLayout(WidgetSlot.Walker, HomeWidgetSizeHint.Wide)
                .WithLayout(WidgetSlot.Partner, HomeWidgetSizeHint.Large)
                .WithLayout(WidgetSlot.Egg, HomeWidgetSizeHint.Small)
                .WithLayout(WidgetSlot.Park, HomeWidgetSizeHint.Large)
                .WithLayout(WidgetSlot.LivePark, HomeWidgetSizeHint.Small);
            File.WriteAllText(path, settings.ToJson());

            // The menu closes; the next one (or a widget update) reads the file afresh.
            var read = HomeWidgetSettings.FromJson(File.ReadAllText(path));
            Assert.Equal(HomeWidgetSizeHint.Wide, read.LayoutOf(WidgetSlot.Walker));
            Assert.Equal(HomeWidgetSizeHint.Large, read.LayoutOf(WidgetSlot.Partner));
            Assert.Equal(HomeWidgetSizeHint.Small, read.LayoutOf(WidgetSlot.Egg));
            Assert.Equal(HomeWidgetSizeHint.Large, read.LayoutOf(WidgetSlot.Park));
            Assert.Equal(HomeWidgetSizeHint.Small, read.LayoutOf(WidgetSlot.LivePark));
            Assert.Equal(settings, read);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Changing_one_size_keeps_the_other_choices()
    {
        var partner = new PartnerChoice("Pikachu", 25, 0, false, 12, "Bank");
        var settings = new HomeWidgetSettings(Partner: partner, WalkerAlert: false)
            .WithLayout(WidgetSlot.Park, HomeWidgetSizeHint.Large)
            .WithLayout(WidgetSlot.Walker, HomeWidgetSizeHint.Wide);
        var read = HomeWidgetSettings.FromJson(settings.ToJson());
        Assert.Equal(partner, read.Partner);
        Assert.False(read.WalkerAlert);
        Assert.Equal(HomeWidgetSizeHint.Large, read.ParkLayout);
        Assert.Null(read.LiveParkLayout);
        Assert.Null(read.WithLayout(WidgetSlot.Park, null).ParkLayout);
    }

    [Fact]
    public void Old_preview_sizes_and_new_installs_stay_on_auto()
    {
        // What the Thor's file held: the old preview-only sizes, which never sized a widget.
        var old = HomeWidgetSettings.FromJson("""{"Partner":null,"EggSave":null,"WalkerBigNumber":true,"WalkerAlert":true,"WalkerSize":0,"PartnerSize":0,"EggSize":0}""");
        foreach (var slot in Enum.GetValues<WidgetSlot>())
        {
            Assert.Null(old.LayoutOf(slot));
            Assert.Null(new HomeWidgetSettings().LayoutOf(slot));
        }
        Assert.Equal(new HomeWidgetSettings(), HomeWidgetSettings.FromJson(null));
        Assert.Equal(new HomeWidgetSettings(), HomeWidgetSettings.FromJson("{ not json"));
    }

    [Fact]
    public void The_size_row_cycles_auto_then_each_size()
    {
        HomeWidgetSizeHint? size = null;
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            size = HomeWidgetSizes.Next(size);
            seen.Add(HomeWidgetSizes.Label(size));
        }
        Assert.Equal(["2x2", "4x2", "4x4", "Auto"], seen);
    }

    [Theory]
    [InlineData(110, 110, HomeWidgetSizeHint.Small)]
    [InlineData(250, 110, HomeWidgetSizeHint.Wide)]
    [InlineData(250, 250, HomeWidgetSizeHint.Large)]
    public void Auto_follows_the_launcher_tile(int widthDp, int heightDp, HomeWidgetSizeHint expected)
    {
        Assert.Equal(expected, WidgetLayout.Pick(null, widthDp, heightDp));
        var fit = WidgetLayout.Fit(null, widthDp, heightDp, widthDp * 3, heightDp * 3, 100, 100);
        Assert.True(fit.Fills);
        Assert.Equal(expected, fit.Size);
    }

    [Fact]
    public void A_chosen_size_wins_over_the_tile_cocoon_gives()
    {
        // Cocoon starts every widget at its minimum, 2x2 (266 px square on the Thor).
        foreach (var chosen in new[] { HomeWidgetSizeHint.Wide, HomeWidgetSizeHint.Large })
            Assert.Equal(chosen, WidgetLayout.Pick(chosen, 110, 110));
        var wide = WidgetLayout.Fit(HomeWidgetSizeHint.Wide, 110, 110, 266, 266, 209, 92);
        Assert.False(wide.Fills);
        Assert.Equal(HomeWidgetSizeHint.Wide, wide.Size);
        Assert.Equal(1, wide.Step);
    }

    [Fact]
    public void A_chosen_size_matching_the_tile_fills_it()
    {
        var fit = WidgetLayout.Fit(HomeWidgetSizeHint.Wide, 250, 110, 750, 330, 209, 92);
        Assert.True(fit.Fills);
        Assert.Equal(HomeWidgetSizeHint.Wide, fit.Size);
    }

    [Fact]
    public void A_smaller_layout_in_a_big_tile_scales_by_whole_steps()
    {
        // A 2x2 layout (76 px natural) in a 4x4 tile of 750 px: the largest whole step that fits.
        var fit = WidgetLayout.Fit(HomeWidgetSizeHint.Small, 250, 250, 750, 750, 76, 76);
        Assert.False(fit.Fills);
        Assert.Equal(9, fit.Step);
        // A 4x2 layout in a 2x2 tile: limited by the width.
        Assert.Equal(2, WidgetLayout.Fit(HomeWidgetSizeHint.Wide, 110, 110, 440, 440, 209, 92).Step);
    }

    [Fact]
    public void The_layout_sits_centred_in_a_canvas_of_the_tile_proportions_without_stretching()
    {
        // A 4x2 picture in a square tile: bands above and below, the picture untouched.
        var (w, h, x, y) = WidgetLayout.Canvas(418, 184, 266, 266);
        Assert.Equal((418, 418), (w, h));
        Assert.Equal(0, x);
        Assert.Equal((418 - 184) / 2, y);

        // A square picture in a wide tile: bands left and right.
        (w, h, x, y) = WidgetLayout.Canvas(152, 152, 750, 330);
        Assert.Equal(152, h);
        Assert.Equal(345, w);
        Assert.Equal((345 - 152) / 2, x);
        Assert.Equal(0, y);

        // Already the tile's shape: nothing added.
        Assert.Equal((300, 132, 0, 0), WidgetLayout.Canvas(300, 132, 750, 330));
    }

    [Fact]
    public void The_neutral_park_says_nothing_about_residents()
    {
        var still = ParkWidget.Neutral(ParkWidgetSize.Wide, 120, 60);
        Assert.Empty(still.Placements);
        Assert.Equal([ParkWidget.Mark], still.Lines);
        Assert.DoesNotContain(still.Lines, l => l.Contains("empty", StringComparison.OrdinalIgnoreCase));
        var live = LivePark.Neutral(ParkWidgetSize.Small, 84, 84);
        Assert.Empty(live.Actors);
        Assert.Equal([ParkWidget.Mark], live.Lines);
        Assert.Equal(ParkDayPhase.Day, live.Phase);
    }
}
