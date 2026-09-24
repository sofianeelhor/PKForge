namespace PKForge.Domain;

/// <summary>Who the second screen currently belongs to. The lower display is a pure
/// function of the top claim: nothing else decides what it shows.</summary>
public enum SecondScreenOwner
{
    /// <summary>Nobody claims it (a settings page, a cold start): the idle branding.</summary>
    Idle,
    /// <summary>The home shelf: hero art of the highlighted game, idle branding otherwise.</summary>
    Home,
    /// <summary>The box browser: the inspector follows the box cursor.</summary>
    Box,
    /// <summary>The Bank: the inspector follows the Bank's cursor.</summary>
    Bank,
    /// <summary>The Pokédex picker overlay: its highlighted species.</summary>
    Pokedex,
    /// <summary>Poképark: the field journal.</summary>
    Pokepark,
    /// <summary>The full-screen summary on the top screen: a box overview, never its details twice.</summary>
    Summary,
    /// <summary>The Living Dex Autopilot: the route map of cartridges and travelling Pokémon.</summary>
    Autopilot,
}

/// <summary>
/// The ownership stack of the second screen. Pages <see cref="SecondScreenClaim.Activate"/>
/// their claim when they appear and <see cref="SecondScreenClaim.Release"/> it when they
/// disappear; overlays open a child claim of whatever is on top and release it when they
/// close. Releasing a claim releases its children too, so a page popped under an open
/// overlay (back gesture, save close) can never leave that overlay on the lower screen.
/// Activation order between two pages does not matter: the most recent activation wins
/// and a release only ever removes its own claim.
/// </summary>
public sealed class SecondScreenRoutes
{
    private readonly List<SecondScreenClaim> _stack = [];

    /// <summary>Raised whenever <see cref="Current"/> may have changed.</summary>
    public event Action? Changed;

    public SecondScreenOwner Current => _stack.Count == 0 ? SecondScreenOwner.Idle : _stack[^1].Owner;

    public IReadOnlyList<SecondScreenOwner> Owners => _stack.Select(c => c.Owner).ToArray();

    /// <summary>A page's claim; inactive until <see cref="SecondScreenClaim.Activate"/>.</summary>
    public SecondScreenClaim CreateClaim(SecondScreenOwner owner) => new(this, owner, null);

    /// <summary>An overlay's claim, a child of the current top claim; active immediately.</summary>
    public SecondScreenClaim OpenOverlay(SecondScreenOwner owner)
    {
        var claim = new SecondScreenClaim(this, owner, _stack.Count == 0 ? null : _stack[^1]);
        claim.Activate();
        return claim;
    }

    internal void Activate(SecondScreenClaim claim)
    {
        // Move the claim (and the overlays it opened) to the top, keeping their order.
        var family = _stack.Where(c => c.DescendsFrom(claim)).ToList();
        _stack.RemoveAll(family.Contains);
        if (!family.Contains(claim)) family.Insert(0, claim);
        _stack.AddRange(family);
        Changed?.Invoke();
    }

    internal void Release(SecondScreenClaim claim)
    {
        if (_stack.RemoveAll(c => c.DescendsFrom(claim)) > 0) Changed?.Invoke();
    }

    internal bool IsActive(SecondScreenClaim claim) => _stack.Contains(claim);
}

/// <summary>One surface's hold on the second screen. <see cref="Dispose"/> releases it.</summary>
public sealed class SecondScreenClaim : IDisposable
{
    private readonly SecondScreenRoutes _routes;
    private readonly SecondScreenClaim? _parent;

    internal SecondScreenClaim(SecondScreenRoutes routes, SecondScreenOwner owner, SecondScreenClaim? parent)
    {
        _routes = routes;
        Owner = owner;
        _parent = parent;
    }

    public SecondScreenOwner Owner { get; }

    public bool IsActive => _routes.IsActive(this);

    /// <summary>Brings this claim (with its open overlays) to the top of the stack.</summary>
    public void Activate() => _routes.Activate(this);

    /// <summary>Removes this claim and every overlay opened on top of it. Idempotent.</summary>
    public void Release() => _routes.Release(this);

    public void Dispose() => Release();

    internal bool DescendsFrom(SecondScreenClaim ancestor)
    {
        for (var claim = this; claim is not null; claim = claim._parent)
            if (ReferenceEquals(claim, ancestor)) return true;
        return false;
    }
}
