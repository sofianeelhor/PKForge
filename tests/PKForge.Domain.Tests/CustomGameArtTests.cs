using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class CustomGameArtTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-gameart-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Pokémon Emerald Rogue", "emerald-rogue")]
    [InlineData("  Radical Red 4.1!! ", "radical-red-4-1")]
    [InlineData("ポケモン", "game")]
    [InlineData(null, "game")]
    public void Keys_are_readable_slugs(string? name, string key)
    {
        Assert.Equal(key, CustomGameArt.NewKey(name, _ => false));
        Assert.True(CustomGameArt.IsValidKey(key));
    }

    [Fact]
    public void A_taken_key_gets_a_suffix()
    {
        var taken = new HashSet<string> { "unbound", "unbound-2" };
        Assert.Equal("unbound-3", CustomGameArt.NewKey("Pokémon Unbound", taken.Contains));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("A")]
    [InlineData("-a")]
    [InlineData("a--b")]
    [InlineData("")]
    public void Keys_outside_the_alphabet_are_refused(string key) => Assert.False(CustomGameArt.IsValidKey(key));

    [Fact]
    public void File_and_mirror_names_round_trip_and_reject_anything_else()
    {
        Assert.Equal("emerald-rogue--hero.png", CustomGameArt.FileName("emerald-rogue", GameArtKind.Hero));
        Assert.Equal("emerald-rogue/hero.png", CustomGameArt.ToMirrorName("emerald-rogue--hero.png"));
        Assert.Equal("emerald-rogue--logo.png", CustomGameArt.FromMirrorName("emerald-rogue/logo.png"));
        Assert.Null(CustomGameArt.FromMirrorName("emerald-rogue/poster.png"));
        Assert.Null(CustomGameArt.FromMirrorName("../hero.png"));
        Assert.Null(CustomGameArt.FromMirrorName("a/b/hero.png"));
        Assert.Null(CustomGameArt.ToMirrorName("notes.txt"));
    }

    [Fact]
    public void Store_puts_replaces_and_resets_each_kind()
    {
        var store = new FileCustomGameArtStore(Path.Combine(_root, "game-art"));
        var changes = new List<string>();
        store.Changed += changes.Add;

        Assert.Null(store.PathOf("unbound", GameArtKind.Cartridge));
        store.Put("unbound", GameArtKind.Cartridge, Png);
        store.Put("unbound", GameArtKind.Hero, Png);
        store.Put("unbound", GameArtKind.Hero, [.. Png, 9]);

        Assert.Equal(Png, File.ReadAllBytes(store.PathOf("unbound", GameArtKind.Cartridge)!));
        Assert.Equal(12, File.ReadAllBytes(store.PathOf("unbound", GameArtKind.Hero)!).Length);
        Assert.Null(store.PathOf("unbound", GameArtKind.Logo));
        Assert.True(store.HasAny("unbound"));

        store.Remove("unbound", GameArtKind.Cartridge);
        store.Remove("unbound", GameArtKind.Hero);
        Assert.False(store.HasAny("unbound"));
        Assert.Equal(5, changes.Count);
        Assert.Null(store.PathOf("../etc", GameArtKind.Hero));
    }

    [Fact]
    public void Store_refuses_bad_bytes_and_bad_keys()
    {
        var store = new FileCustomGameArtStore(Path.Combine(_root, "game-art"));
        Assert.Throws<InvalidDataException>(() => store.Put("x", GameArtKind.Logo, [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => store.Put("../x", GameArtKind.Logo, Png));
        Assert.False(store.HasAny("x"));
    }

    [Fact]
    public void The_identity_keeps_its_art_key_and_the_shelf_splits_on_it()
    {
        var identities = new JsonSaveIdentityStore(Path.Combine(_root, "ids.json"));
        identities.Set(new SaveIdentity("doc:a", ArtKey: "emerald-rogue"));
        identities.Set(new SaveIdentity("doc:b", ArtKey: "../bad"));

        Assert.Equal("emerald-rogue", new JsonSaveIdentityStore(Path.Combine(_root, "ids.json")).Get("doc:a")?.ArtKey);
        Assert.Null(identities.Get("doc:b")); // an invalid key alone is no customisation

        var guess = SaveIdentityRules.Guess("Emerald", 3, "a.sav", null, "GBA");
        var resolved = SaveIdentityResolver.Resolve(guess, identities.Get("doc:a"));
        Assert.Equal("emerald-rogue", resolved.ArtKey);
        Assert.True(resolved.IsCustomized);
        var a = new DetectedSave("doc:a", "a.sav", "Pokémon Emerald", EmulatorKind.RetroArch, false, null, 3, Guess: guess, Identity: resolved);
        var b = a with { DocumentId = "doc:c", Identity = SaveIdentityResolver.Resolve(guess, null) };
        Assert.Equal("emerald-rogue", a.CustomArtKey);
        Assert.NotEqual(SaveShelf.GroupKey(a), SaveShelf.GroupKey(b));
    }

    [Fact]
    public async Task Art_mirrors_as_Games_key_kind_and_comes_back()
    {
        var removed = new RecentlyRemovedStore(Path.Combine(_root, "removed"));
        var store = new FileCustomGameArtStore(Path.Combine(_root, "a", "game-art"));
        store.Put("emerald-rogue", GameArtKind.Hero, Png);
        var folder = new MemoryFolder();

        var (_, stateA) = await FolderMirrorSync.SyncAsync(folder, "tree",
            [FileCustomGameArtStore.MirrorSource(store.Directory, removed, () => Now)], null, Now);
        Assert.Equal(Png, folder.Files["Games/emerald-rogue/hero.png"].Bytes);

        // Another device takes it, and leaves a stray or non-PNG file in Games/ alone.
        folder.Put("Games/emerald-rogue/poster.png", Png);
        folder.Put("Games/emerald-rogue/logo.png", [1, 2, 3]);
        var other = new FileCustomGameArtStore(Path.Combine(_root, "b", "game-art"));
        var (report, _) = await FolderMirrorSync.SyncAsync(folder, "tree",
            [FileCustomGameArtStore.MirrorSource(other.Directory, removed, () => Now)], null, Now);
        Assert.Equal(Png, File.ReadAllBytes(other.PathOf("emerald-rogue", GameArtKind.Hero)!));
        Assert.Null(other.PathOf("emerald-rogue", GameArtKind.Logo));
        Assert.Equal(["emerald-rogue--hero.png"], Directory.GetFiles(other.Directory).Select(Path.GetFileName));
        Assert.NotNull(stateA);
        Assert.False(report.Failed);
    }

    [Fact]
    public void Art_travels_in_the_data_archive_and_never_replaces_art_here()
    {
        var old = Path.Combine(_root, "old");
        new FileCustomGameArtStore(Path.Combine(old, "game-art")).Put("unbound", GameArtKind.Cartridge, Png);
        new FileCustomGameArtStore(Path.Combine(old, "game-art")).Put("unbound", GameArtKind.Logo, Png);
        new JsonSaveIdentityStore(Path.Combine(old, "save-identities.json")).Set(new SaveIdentity("doc:u", ArtKey: "unbound"));
        using var output = new MemoryStream();
        DataArchive.Export(output, old, [], "3.2.0", Now);

        var root = Path.Combine(_root, "new");
        var mine = new FileCustomGameArtStore(Path.Combine(root, "game-art"));
        mine.Put("unbound", GameArtKind.Logo, [.. Png, 7]);
        var identities = new JsonSaveIdentityStore(Path.Combine(root, "save-identities.json"));
        using (var reader = DataArchive.Open(new MemoryStream(output.ToArray())))
            DataArchive.Restore(reader, root, new FileBankService(Path.Combine(root, "bank")), identities,
                new InjectedGiftHistory(Path.Combine(root, "gifts.json")), new BankBoxNames(Path.Combine(root, "bank")),
                new BankBoxWallpapers(Path.Combine(root, "bank")));

        Assert.Equal(Png, File.ReadAllBytes(mine.PathOf("unbound", GameArtKind.Cartridge)!));
        Assert.Equal(12, File.ReadAllBytes(mine.PathOf("unbound", GameArtKind.Logo)!).Length); // here wins
        Assert.Equal("unbound", identities.Get("doc:u")?.ArtKey);
    }
}
