using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The second screen's ownership stack: the lower display always belongs to the most
/// recently activated live claim, and every exit path (page pop in either event order,
/// overlay close, a page popped under its overlay, resume re-activation) hands it back.
/// </summary>
public sealed class SecondScreenRoutesTests
{
    [Fact]
    public void Nobody_claims_it_at_start()
    {
        var routes = new SecondScreenRoutes();
        Assert.Equal(SecondScreenOwner.Idle, routes.Current);
    }

    [Fact]
    public void Leaving_the_box_hands_the_screen_back_to_home()
    {
        var routes = new SecondScreenRoutes();
        var home = routes.CreateClaim(SecondScreenOwner.Home);
        var box = routes.CreateClaim(SecondScreenOwner.Box);
        home.Activate();
        home.Release();   // Home.OnDisappearing as the box is pushed
        box.Activate();
        Assert.Equal(SecondScreenOwner.Box, routes.Current);

        box.Release();    // B: the box pops
        home.Activate();
        Assert.Equal(SecondScreenOwner.Home, routes.Current);
    }

    [Fact]
    public void Pop_order_between_pages_does_not_matter()
    {
        var routes = new SecondScreenRoutes();
        var box = routes.CreateClaim(SecondScreenOwner.Box);
        var bank = routes.CreateClaim(SecondScreenOwner.Bank);
        box.Activate();
        bank.Activate();

        box.Activate();   // the revealed page appears before the popped one disappears
        bank.Release();
        Assert.Equal(SecondScreenOwner.Box, routes.Current);
        Assert.Equal([SecondScreenOwner.Box], routes.Owners);
    }

    [Fact]
    public void A_page_nobody_claims_shows_idle()
    {
        var routes = new SecondScreenRoutes();
        var home = routes.CreateClaim(SecondScreenOwner.Home);
        home.Activate();
        home.Release();   // Settings pushed: it does not claim the lower screen
        Assert.Equal(SecondScreenOwner.Idle, routes.Current);
    }

    [Fact]
    public void Overlays_stack_on_their_page_and_close_back_to_it()
    {
        var routes = new SecondScreenRoutes();
        var box = routes.CreateClaim(SecondScreenOwner.Box);
        box.Activate();
        var summary = routes.OpenOverlay(SecondScreenOwner.Summary);
        Assert.Equal(SecondScreenOwner.Summary, routes.Current);
        var dex = routes.OpenOverlay(SecondScreenOwner.Pokedex);
        Assert.Equal(SecondScreenOwner.Pokedex, routes.Current);

        dex.Dispose();
        Assert.Equal(SecondScreenOwner.Summary, routes.Current);
        summary.Dispose();
        Assert.Equal(SecondScreenOwner.Box, routes.Current);
    }

    [Fact]
    public void Popping_a_page_under_its_overlay_takes_the_overlay_with_it()
    {
        var routes = new SecondScreenRoutes();
        var home = routes.CreateClaim(SecondScreenOwner.Home);
        var bank = routes.CreateClaim(SecondScreenOwner.Bank);
        home.Activate();
        bank.Activate();
        var summary = routes.OpenOverlay(SecondScreenOwner.Summary);

        bank.Release();   // back gesture / save close while the summary is still up
        home.Activate();
        Assert.Equal(SecondScreenOwner.Home, routes.Current);
        Assert.False(summary.IsActive);
        summary.Dispose(); // the late close is harmless
        Assert.Equal(SecondScreenOwner.Home, routes.Current);
    }

    [Fact]
    public void Reactivating_a_page_keeps_its_open_overlay_on_top()
    {
        var routes = new SecondScreenRoutes();
        var box = routes.CreateClaim(SecondScreenOwner.Box);
        box.Activate();
        var summary = routes.OpenOverlay(SecondScreenOwner.Summary);

        box.Activate();   // app resume re-fires OnAppearing under the overlay
        Assert.Equal(SecondScreenOwner.Summary, routes.Current);
        summary.Dispose();
        Assert.Equal(SecondScreenOwner.Box, routes.Current);
    }

    [Fact]
    public void Release_is_idempotent_and_only_touches_its_own_claim()
    {
        var routes = new SecondScreenRoutes();
        var home = routes.CreateClaim(SecondScreenOwner.Home);
        var park = routes.CreateClaim(SecondScreenOwner.Pokepark);
        home.Activate();
        park.Activate();
        var changes = 0;
        routes.Changed += () => changes++;

        park.Release();
        park.Release();
        Assert.Equal(1, changes);
        Assert.Equal(SecondScreenOwner.Home, routes.Current);
    }
}
