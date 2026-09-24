using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// Offline proof that every sprite surface resolves the right art for form / shiny / gender
/// edge cases. Bundled assets are checked against the real PKHeX submodule files the app
/// packs (PKForge.App.csproj MauiAsset rules, mirrored in <see cref="BundledAssets"/>);
/// HOME / Showdown against the generated PokeAPI table.
/// </summary>
public sealed class SpriteCatalogTests
{
    private static readonly Lazy<HashSet<string>> Bundled = new(BundledAssets);

    /// <summary>The app's bundled logical asset names (see the MauiAsset items in PKForge.App.csproj).</summary>
    private static HashSet<string> BundledAssets()
    {
        var img = Path.Combine(RepoRoot(), "external", "PKHeX", "PKHeX.Drawing.PokeSprite", "Resources", "img");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { "Big Pokemon Sprites", "Big Shiny Sprites" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(img, dir), "*.png"))
                set.Add("sprites/" + Path.GetFileName(file));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(img, "Artwork Pokemon Sprites"), "a_*.png"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("a_9") || name.StartsWith("a_10") || name.Contains('-'))
                set.Add("artwork/" + name);
        }
        return set;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PKForge.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private static SpriteCandidate ResolveBundled(SpriteLook look) =>
        SpriteCatalog.BundledCandidates(look).First(c => Bundled.Value.Contains(c.Path));

    private static SpriteLook L(int species, int form = 0, bool shiny = false, bool female = false, int arg = 0, bool cosplay = false, bool gmax = false) =>
        new(species, form, shiny, new SpriteTraits(female, arg, cosplay, gmax));

    // ── bundled pixel sprites / artwork ──

    public static TheoryData<string, SpriteLook, string, SpriteFidelity> BundledCases => new()
    {
        { "Charizard", L(6), "sprites/b_6.png", SpriteFidelity.Exact },
        { "Charizard shiny", L(6, shiny: true), "sprites/b_6s.png", SpriteFidelity.Exact },
        { "Mega Charizard X", L(6, 1), "sprites/b_6-1.png", SpriteFidelity.Exact },
        { "Mega Charizard Y", L(6, 2), "sprites/b_6-2.png", SpriteFidelity.Exact },
        { "Mega Charizard Y shiny", L(6, 2, true), "sprites/b_6-2s.png", SpriteFidelity.Exact },
        { "G-Max Charizard", L(6, gmax: true), "sprites/b_6-gmax.png", SpriteFidelity.Exact },
        { "G-Max Charizard shiny", L(6, shiny: true, gmax: true), "sprites/b_6-gmaxs.png", SpriteFidelity.Exact },
        { "Primal Kyogre", L(382, 1), "sprites/b_382-1.png", SpriteFidelity.Exact },
        { "Pikachu female (no pixel f-art)", L(25, female: true), "sprites/b_25.png", SpriteFidelity.Exact },
        { "Pikachu Original Cap", L(25, 1), "sprites/b_25-1.png", SpriteFidelity.Exact },
        { "Cosplay Pikachu Rock Star (Gen 6)", L(25, 1, cosplay: true), "sprites/b_25-1c.png", SpriteFidelity.Exact },
        { "Cosplay Pikachu Libre shiny", L(25, 5, true, cosplay: true), "sprites/b_25-5cs.png", SpriteFidelity.Exact },
        { "Let's Go Pikachu", L(25, 8), "sprites/b_25-8p.png", SpriteFidelity.Exact },
        { "Let's Go Eevee", L(133, 1), "sprites/b_133-1p.png", SpriteFidelity.Exact },
        { "World Cap Pikachu", L(25, 9), "sprites/b_25-9.png", SpriteFidelity.Exact },
        { "Hippowdon female", L(450, female: true), "sprites/b_450f.png", SpriteFidelity.Exact },
        { "Hippowdon female shiny", L(450, shiny: true, female: true), "sprites/b_450fs.png", SpriteFidelity.Exact },
        { "Pyroar female", L(668, female: true), "sprites/b_668f.png", SpriteFidelity.Exact },
        { "Jellicent female", L(593, female: true), "sprites/b_593f.png", SpriteFidelity.Exact },
        { "Unown B", L(201, 1), "sprites/b_201-1.png", SpriteFidelity.Exact },
        { "Unown ?", L(201, 27), "sprites/b_201-27.png", SpriteFidelity.Exact },
        { "Vivillon Poké Ball", L(666, 19), "sprites/b_666-19.png", SpriteFidelity.Exact },
        { "Vivillon Fancy shiny", L(666, 18, true), "sprites/b_666-18s.png", SpriteFidelity.Exact },
        { "Furfrou Pharaoh", L(676, 9), "sprites/b_676-9.png", SpriteFidelity.Exact },
        { "Minior Red Core", L(774, 7), "sprites/b_774-7.png", SpriteFidelity.Exact },
        { "Minior Violet Core shiny", L(774, 13, true), "sprites/b_774-13s.png", SpriteFidelity.Exact },
        { "Alcremie Vanilla Strawberry", L(869, 0, arg: 0), "sprites/b_869-0-0.png", SpriteFidelity.Exact },
        { "Alcremie Rainbow Swirl Ribbon", L(869, 8, arg: 6), "sprites/b_869-8-6.png", SpriteFidelity.Exact },
        { "Alcremie Ruby Cream Star shiny", L(869, 1, true, arg: 3), "sprites/b_869-1-3s.png", SpriteFidelity.Exact },
        { "Meowstic female form", L(678, 1), "sprites/b_678-1.png", SpriteFidelity.Exact },
        { "Indeedee female form", L(876, 1), "sprites/b_876-1.png", SpriteFidelity.Exact },
        { "Alolan Vulpix", L(37, 1), "sprites/b_37-1.png", SpriteFidelity.Exact },
        { "Galarian Ponyta", L(77, 1), "sprites/b_77-1.png", SpriteFidelity.Exact },
        { "Hisuian Zorua", L(570, 1), "sprites/b_570-1.png", SpriteFidelity.Exact },
        { "Paldean Wooper", L(194, 1), "artwork/a_194-1.png", SpriteFidelity.Exact },
        { "Paldean Tauros Combat", L(128, 1), "artwork/a_128-1.png", SpriteFidelity.Exact },
        { "Paldean Tauros Aqua", L(128, 3), "artwork/a_128-3.png", SpriteFidelity.Exact },
        { "Paldean Tauros Blaze shiny (no shiny artwork)", L(128, 2, true), "artwork/a_128-2.png", SpriteFidelity.ShinyMissing },
        { "Urshifu Rapid Strike (no bundled art; HOME has it)", L(892, 1), "sprites/b_892.png", SpriteFidelity.BaseForm },
        { "G-Max Urshifu Rapid Strike", L(892, 1, gmax: true), "sprites/b_892-1-gmax.png", SpriteFidelity.Exact },
        { "Zacian Crowned", L(888, 1), "sprites/b_888-1.png", SpriteFidelity.Exact },
        { "Calyrex Shadow Rider", L(898, 2), "sprites/b_898-2.png", SpriteFidelity.Exact },
        { "Totem Mimikyu (PKHeX: base form + glow)", L(778, 2), "sprites/b_778.png", SpriteFidelity.Exact },
        { "Totem Alolan Raticate", L(20, 2), "sprites/b_20-1.png", SpriteFidelity.Exact },
        { "Mimikyu Busted (PKHeX folds)", L(778, 1), "sprites/b_778.png", SpriteFidelity.BaseForm },
        { "Ash-Greninja", L(658, 2), "sprites/b_658-2.png", SpriteFidelity.Exact },
        { "Ogerpon Wellspring", L(1017, 1), "artwork/a_1017-1.png", SpriteFidelity.Exact },
        { "Ogerpon Cornerstone Tera", L(1017, 7), "artwork/a_1017-7.png", SpriteFidelity.Exact },
        { "Terapagos Stellar", L(1024, 2), "artwork/a_1024-2.png", SpriteFidelity.Exact },
        { "Sprigatito shiny (Gen 9 artwork)", L(906, shiny: true), "artwork/a_906.png", SpriteFidelity.ShinyMissing },
        { "Mega Raichu X (Z-A)", L(26, 2), "artwork/a_26-2.png", SpriteFidelity.Exact },
        { "Mega Absol Z (Z-A)", L(359, 2), "artwork/a_359-2.png", SpriteFidelity.Exact },
        { "Scatterbug Fancy (PKHeX draws base)", L(664, 18), "sprites/b_664.png", SpriteFidelity.Exact },
        { "Spinda (single sprite)", L(327), "sprites/b_327.png", SpriteFidelity.Exact },
        { "Arceus Fairy", L(493, 17), "sprites/b_493-17.png", SpriteFidelity.Exact },
        { "Rotom Wash", L(479, 2), "sprites/b_479-2.png", SpriteFidelity.Exact },
        { "Kyurem Black shiny", L(646, 2, true), "sprites/b_646-2s.png", SpriteFidelity.Exact },
        { "Necrozma Ultra", L(800, 3), "sprites/b_800-3.png", SpriteFidelity.Exact },
        { "Eternamax Eternatus", L(890, 1), "sprites/b_890-1.png", SpriteFidelity.Exact },
        { "Xerneas Active", L(716, 1), "sprites/b_716-1.png", SpriteFidelity.Exact },
        { "Zygarde Complete", L(718, 4), "sprites/b_718-4.png", SpriteFidelity.Exact },
        { "Hoopa Unbound", L(720, 1), "sprites/b_720-1.png", SpriteFidelity.Exact },
        { "Koraidon Gliding Build", L(1007, 4), "artwork/a_1007-4.png", SpriteFidelity.Exact },
    };

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void Bundled_sprite_is_the_exact_asset(string label, SpriteLook look, string expected, SpriteFidelity fidelity)
    {
        var resolved = ResolveBundled(look);
        Assert.True(Bundled.Value.Contains(expected), $"{label}: expected asset {expected} is not bundled");
        Assert.Equal((expected, fidelity), (resolved.Path, resolved.Fidelity));
    }

    [Fact]
    public void Mega_Charizard_Y_is_never_Mega_X_or_base()
    {
        Assert.Equal("sprites/b_6-2.png", ResolveBundled(L(6, 2)).Path);
        Assert.NotEqual(ResolveBundled(L(6, 1)).Path, ResolveBundled(L(6, 2)).Path);
    }

    [Fact]
    public void Pkhex_names_match_PKHeX_SpriteName()
    {
        // Straight from PKHeX.Drawing.PokeSprite SpriteName.GetResourceStringSprite.
        Assert.Equal("_6_2s", SpriteCatalog.PkhexName(6, 2, false, 0, false, true));
        Assert.Equal("_25_3c", SpriteCatalog.PkhexName(25, 3, false, 0, true, false));
        Assert.Equal("_25_8p", SpriteCatalog.PkhexName(25, 8, false, 0, false, false));
        Assert.Equal("_133_1p", SpriteCatalog.PkhexName(133, 1, false, 0, false, false));
        Assert.Equal("_450f", SpriteCatalog.PkhexName(450, 0, true, 0, false, false));
        Assert.Equal("_25", SpriteCatalog.PkhexName(25, 0, true, 0, false, false)); // no gendered pixel art
        Assert.Equal("_869_0_4", SpriteCatalog.PkhexName(869, 0, false, 4, false, false));
        Assert.Equal("_869_3_2s", SpriteCatalog.PkhexName(869, 3, false, 2, false, true));
        Assert.Equal("_892", SpriteCatalog.PkhexName(892, 1, false, 0, false, false)); // default-form sprite
    }

    [Fact]
    public void Every_form_PKHeX_draws_has_a_bundled_asset_at_exact_or_documented_fidelity()
    {
        // Every regular table row is a real PKHeX (species, form). None may silently land on
        // the "?" placeholder; base-form fallbacks are allowed only where no asset exists,
        // and the list of those is pinned so a regression (or a newly bundled fix) shows up.
        var baseForm = new List<string>();
        foreach (var key in SpriteCatalog.Entries.Keys)
        {
            if (SpriteCatalog.ParseKey(key) is not { } look || look.Traits.Gigantamax) continue;
            var resolved = ResolveBundled(look);
            Assert.NotEqual(SpriteFidelity.Unknown, resolved.Fidelity);
            if (resolved.Fidelity == SpriteFidelity.BaseForm) baseForm.Add(key);
        }
        Assert.Equal(KnownBundledGaps, baseForm.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// PKHeX forms with neither a pixel sprite nor artwork in the pinned submodule (checked
    /// by hand against the Resources/img folders): their base form is drawn and the fidelity
    /// says so. Mostly battle-only / event-only looks.
    /// </summary>
    private static readonly string[] KnownBundledGaps = [.. KnownGapsList().Order(StringComparer.Ordinal)];

    private static IEnumerable<string> KnownGapsList()
    {
        var path = Path.Combine(RepoRoot(), "tests", "PKForge.Domain.Tests", "SpriteGaps.txt");
        return File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#');
    }

    // ── HOME / Showdown (PokeAPI) ──

    public static TheoryData<string, SpriteLook, string?, string?> RemoteCases => new()
    {
        // label, look, HOME path, Showdown path (null = no exact art: caller must fall back)
        { "Charizard", L(6), "6.png", "6.gif" },
        { "Charizard shiny", L(6, shiny: true), "shiny/6.png", "shiny/6.gif" },
        { "Mega Charizard X", L(6, 1), "10034.png", "10034.gif" },
        { "Mega Charizard Y", L(6, 2), "10035.png", "10035.gif" },
        { "Mega Charizard Y shiny", L(6, 2, true), "shiny/10035.png", "shiny/10035.gif" },
        { "G-Max Charizard", L(6, gmax: true), "10196.png", "10196.gif" },
        { "Pikachu female", L(25, female: true), "female/25.png", "female/25.gif" },
        { "Pikachu female shiny", L(25, shiny: true, female: true), "shiny/female/25.png", "shiny/female/25.gif" },
        { "Hippowdon female", L(450, female: true), "female/450.png", "female/450.gif" },
        { "Meowstic female form", L(678, 1), "10025.png", "10025.gif" },
        { "Indeedee female form", L(876, 1), "10186.png", "10186.gif" },
        { "Pikachu Original Cap (no shiny)", L(25, 1, true), "10094.png", "shiny/10094.gif" },
        { "Cosplay Pikachu Belle", L(25, 2, cosplay: true), null, "10081.gif" },
        { "Unown A (default form)", L(201), "201-a.png", "201.gif" },
        { "Unown !", L(201, 26), "201-exclamation.png", "201-exclamation.gif" },
        { "Vivillon Icy Snow (form 0)", L(666), "666-icy-snow.png", null },
        { "Vivillon Poké Ball", L(666, 19), "666-poke-ball.png", "666-pokeball.gif" },
        { "Xerneas Neutral (PokeAPI default is Active)", L(716), "716-neutral.png", "716-neutral.gif" },
        { "Xerneas Active", L(716, 1), "716-active.png", "716-active.gif" },
        { "Alcremie Ruby Cream Star", L(869, 1, arg: 3), "869-ruby-cream-star-sweet.png", null },
        { "Alcremie Vanilla Strawberry shiny", L(869, 0, true, arg: 0), "shiny/869-vanilla-cream-strawberry-sweet.png", "shiny/869.gif" },
        { "Paldean Tauros Combat", L(128, 1), "10250.png", "10250.gif" },
        { "Paldean Tauros Blaze", L(128, 2), "10251.png", "10251.gif" },
        { "Paldean Tauros Aqua", L(128, 3), "10252.png", "10252.gif" },
        { "Urshifu Rapid Strike", L(892, 1), "10191.png", "10191.gif" },
        { "G-Max Urshifu Rapid Strike", L(892, 1, gmax: true), "10227.png", "10227.gif" },
        { "Zacian Crowned", L(888, 1), "10188.png", "10188.gif" },
        { "Ogerpon Wellspring", L(1017, 1), "10273.png", null },
        { "Ogerpon Wellspring Tera", L(1017, 5), "10273.png", null },
        { "Totem Mimikyu", L(778, 2), "10144.png", "10144.gif" },
        { "Minior Red Meteor (form 0)", L(774), "774.png", "774.gif" },
        { "Minior Red Core", L(774, 7), "10136.png", "10136.gif" },
        { "Ash-Greninja", L(658, 2), "10117.png", "10117.gif" },
        { "Pyroar female", L(668, female: true), "female/668.png", "female/668.gif" },
        { "Alolan Raichu", L(26, 1), "10100.png", "10100.gif" },
        { "Mega Raichu X (Z-A)", L(26, 2), "10304.png", null },
    };

    [Theory]
    [MemberData(nameof(RemoteCases))]
    public void Remote_sprite_is_the_exact_file(string label, SpriteLook look, string? home, string? showdown)
    {
        Assert.True(home == SpriteCatalog.Home(look)?.Path, $"{label}: HOME {SpriteCatalog.Home(look)?.Path ?? "none"} != {home ?? "none"}");
        Assert.True(showdown == SpriteCatalog.Showdown(look)?.Path, $"{label}: Showdown {SpriteCatalog.Showdown(look)?.Path ?? "none"} != {showdown ?? "none"}");
    }

    [Fact]
    public void Base_species_keep_the_legacy_cache_file_names()
    {
        // Existing installs cached "home/6.png" and "home/6-s.png": those stay valid.
        Assert.Equal("6.png", SpriteCatalog.Home(L(6))!.CacheName);
        Assert.Equal("6-s.png", SpriteCatalog.Home(L(6, shiny: true))!.CacheName);
        Assert.Equal("25-f-s.gif", SpriteCatalog.Showdown(L(25, shiny: true, female: true))!.CacheName);
        // A form never shares a file name with its base species.
        Assert.NotEqual("716.png", SpriteCatalog.Home(L(716))!.CacheName);
    }

    [Fact]
    public void Every_PKHeX_form_has_a_table_row()
    {
        // The generator fails on unmapped forms; this guards the shipped file itself.
        foreach (var (species, forms) in new[] { (6, 3), (25, 10), (128, 4), (201, 28), (493, 19), (666, 20), (774, 14), (869, 9), (1017, 8), (1024, 3) })
            for (var form = 0; form < forms; form++)
                Assert.NotNull(SpriteCatalog.ParseKey(SpriteCatalog.TableKey(L(species, form))));
        Assert.True(SpriteCatalog.Entries.Count > 1500);
    }

    [Fact]
    public void Of_reads_entity_fields_like_PKHeX()
    {
        Assert.True(SpriteLook.Of(25, 3, false, gender: 1, generation: 6).Traits is { Female: true, Cosplay: true });
        Assert.False(SpriteLook.Of(25, 3, false, generation: 7).Traits.Cosplay);
        Assert.Equal(4, SpriteLook.Of(869, 2, false, formArgument: 4).Traits.FormArgument);
        Assert.Equal(0, SpriteLook.Of(6, 0, false, formArgument: 4).Traits.FormArgument);
    }
}
