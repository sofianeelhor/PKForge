using System.IO.Compression;
using System.Text;
using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class DataArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "old");
    private string New => Path.Combine(_root, "new");

    private static readonly PreferenceValue[] Preferences =
    [
        new("color_theme", "string", "fire"),
        new("hax_mode", "bool", "true"),
        new("motion_level", "int", "1"),
        new("music_library", "string", "doc1\u0001Song"),
        new(DataTransfer.WatchedRootsKey, "string", """[{"Kind":0,"TreeId":"content://tree/ra","DisplayName":"RetroArch saves"}]"""),
    ];

    private static BankEntryInfo Info(int species) => new(species, 0, false, $"Mon{species}", 5, 8, "test", "PK8");

    /// <summary>A device with a bank (two boxes, a named box), a restore point, a photo,
    /// a preset, a save name and a gift marker.</summary>
    private void FillOldDevice()
    {
        var bank = new FileBankService(Path.Combine(Old, "bank"));
        bank.AddMany([new([1, 1, 1], Info(25), 0, 3), new([2, 2, 2], Info(133), 1, 0)]);
        new BankBoxNames(Path.Combine(Old, "bank")).SetMany([(1, "Shinies")]);
        Directory.CreateDirectory(Path.Combine(Old, "backups"));
        File.WriteAllBytes(Path.Combine(Old, "backups", "p1.bin"), [9, 9]);
        File.WriteAllText(Path.Combine(Old, "backups", "p1.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Old, "pokepark-album"));
        File.WriteAllBytes(Path.Combine(Old, "pokepark-album", "20260101-101010-000.jpg"), [0xFF, 0xD8]);
        File.WriteAllText(Path.Combine(Old, "pokepark-album", "20260101-101010-000.txt"), "Pikachu naps");
        new ItemPresetStore(Path.Combine(Old, "item-presets.json")).Save(new ItemPreset("p1", "Balls", 8, [new("Balls", 4, "Poké Ball", 99)]));
        new JsonSaveIdentityStore(Path.Combine(Old, "save-identities.json")).Set(new SaveIdentity("doc:emerald", DisplayName: "Nuzlocke", ColorKey: null, Hidden: true));
        new InjectedGiftHistory(Path.Combine(Old, "injected-gifts.json")).Record("Emerald|3|0001|Aurora Ticket");
    }

    private byte[] ExportOld()
    {
        using var output = new MemoryStream();
        DataArchive.Export(output, Old, Preferences, "3.2.0", new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        return output.ToArray();
    }

    private DataImportResult ImportInto(string root, byte[] archive, out FileBankService bank, out JsonSaveIdentityStore identities)
    {
        bank = new FileBankService(Path.Combine(root, "bank"));
        identities = new JsonSaveIdentityStore(Path.Combine(root, "save-identities.json"));
        using var reader = DataArchive.Open(new MemoryStream(archive));
        return DataArchive.Restore(reader, root, bank, identities, new InjectedGiftHistory(Path.Combine(root, "injected-gifts.json")),
            new BankBoxNames(Path.Combine(root, "bank")), new BankBoxWallpapers(Path.Combine(root, "bank")));
    }

    [Fact]
    public void ManifestSaysWhatIsInside()
    {
        FillOldDevice();
        using var reader = DataArchive.Open(new MemoryStream(ExportOld()));

        var manifest = reader.Manifest;
        Assert.Equal((DataArchive.FormatName, DataArchive.CurrentFormatVersion, "3.2.0"), (manifest.Format, manifest.FormatVersion, manifest.AppVersion));
        Assert.Equal(2, manifest.ItemsIn(DataTransfer.BankSection));
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.RestorePointsSection));
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.AlbumSection));
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.ItemPresetsSection));
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.SaveIdentitiesSection));
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.GiftsSection));
        Assert.Equal(5, manifest.ItemsIn(DataTransfer.PreferencesSection));
        Assert.Equal(Preferences, reader.Preferences);
    }

    [Fact]
    public void RoundTripRestoresEverythingOnAnEmptyDevice()
    {
        FillOldDevice();
        Directory.CreateDirectory(Path.Combine(Old, "items"));
        File.WriteAllBytes(Path.Combine(Old, "items", "potion.png"), [1]); // a cache: stays behind

        var result = ImportInto(New, ExportOld(), out var bank, out var identities);

        Assert.Equal(new DataImportResult(2, 0, 1, 1, 1, 1, 1), result);
        // An empty bank takes the boxes as they were: slots and box names.
        Assert.Equal([(0, 3, 25), (1, 0, 133)], bank.GetAll().OrderBy(e => e.Box).Select(e => (e.Box, e.Slot, e.Info.Species)));
        Assert.Equal([2, 2, 2], bank.GetData(bank.GetAll().Single(e => e.Info.Species == 133).Id));
        Assert.Equal("Shinies", new BankBoxNames(Path.Combine(New, "bank")).Get(1));
        Assert.True(File.Exists(Path.Combine(New, "backups", "p1.bin")));
        Assert.Equal("Pikachu naps", File.ReadAllText(Path.Combine(New, "pokepark-album", "20260101-101010-000.txt")));
        Assert.Equal("Balls", new ItemPresetStore(Path.Combine(New, "item-presets.json")).Read().Single().Name);
        Assert.Equal(new SaveIdentity("doc:emerald", DisplayName: "Nuzlocke", Hidden: true), identities.Get("doc:emerald"));
        Assert.True(new InjectedGiftHistory(Path.Combine(New, "injected-gifts.json")).IsInjected("Emerald|3|0001|Aurora Ticket"));
        Assert.False(Directory.Exists(Path.Combine(New, "items")));
    }

    [Fact]
    public void ImportMergesAndNeverOverwritesWhatIsHere()
    {
        FillOldDevice();
        var archive = ExportOld();
        var bankHere = new FileBankService(Path.Combine(New, "bank"));
        bankHere.Add([2, 2, 2], Info(133)); // the same Eevee, already here
        bankHere.Add([7, 7, 7], Info(1));
        new ItemPresetStore(Path.Combine(New, "item-presets.json")).Save(new ItemPreset("mine", "Balls", 8, []));
        new JsonSaveIdentityStore(Path.Combine(New, "save-identities.json")).Set(new SaveIdentity("doc:emerald", DisplayName: "Mine"));
        Directory.CreateDirectory(Path.Combine(New, "backups"));
        File.WriteAllBytes(Path.Combine(New, "backups", "p1.bin"), [5]);
        File.WriteAllText(Path.Combine(New, "backups", "p1.json"), "{\"mine\":true}");

        using (var reader = DataArchive.Open(new MemoryStream(archive)))
        {
            var preview = DataArchive.Preview(reader, New, bankHere, []);
            Assert.Equal((1, 1, false, 0, 1, 0), (preview.BankNew, preview.BankDuplicates, preview.BankKeepsLayout, preview.RestorePointsNew, preview.ItemPresetsNew, preview.SaveIdentitiesNew));
        }

        var result = ImportInto(New, archive, out var bank, out var identities);

        Assert.Equal((1, 1), (result.BankImported, result.BankDuplicates));
        Assert.Equal(3, bank.GetAll().Count);
        Assert.Equal("Mine", identities.Get("doc:emerald")!.DisplayName);
        Assert.Equal([5], File.ReadAllBytes(Path.Combine(New, "backups", "p1.bin")));
        Assert.Equal(["Balls", "Balls (2)"], new ItemPresetStore(Path.Combine(New, "item-presets.json")).Read().Select(p => p.Name));

        // A second import of the same archive adds nothing.
        var again = ImportInto(New, archive, out bank, out _);
        Assert.Equal(new DataImportResult(0, 2, 0, 0, 0, 0, 0), again);
    }

    [Fact]
    public void DeviceBoundPreferencesAreNotAppliedAndLinkedFoldersWaitToBeRelinked()
    {
        var apply = DataTransfer.ToApply(Preferences);
        Assert.Equal(["color_theme", "hax_mode", "motion_level"], apply.Select(p => p.Key));

        var waiting = new WatchedRoot(EmulatorKind.MelonDS, "content://old", "melonDS");
        var relink = DataTransfer.ToRelink([waiting, waiting], Preferences);
        Assert.Equal([("melonDS", EmulatorKind.MelonDS), ("RetroArch saves", EmulatorKind.RetroArch)], relink.Select(r => (r.DisplayName, r.Kind)));
    }

    [Theory]
    [InlineData("pkforge-folder.tree")]
    [InlineData("pkforge-folder.name")]
    [InlineData("pkforge-folder.report")]
    [InlineData("livingdex_destination")]
    public void ThePkforgeFolderGrantAndLivingDexDestinationStayOnTheirDevice(string key)
    {
        // Another device's SAF tree or document is a grant this device does not hold.
        Assert.True(DataTransfer.IsDeviceBound(key));
        Assert.Empty(DataTransfer.ToApply([new(key, "string", "content://tree/other")]));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("data/bank/../../evil.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("data\\bank\\x.bin")]
    [InlineData("C:/x.bin")]
    [InlineData("data/bank/sub/x.bin")]
    [InlineData("data/shared_prefs/x.xml")]
    [InlineData("readme.txt")]
    public void EntriesOutsideTheArchiveLayoutAreRefused(string name)
    {
        var archive = Zip(("manifest.json", """{"format":"pkforge-data","formatVersion":1,"appVersion":"3.2.0","createdUtc":"2026-11-10T00:00:00Z","sections":[]}"""),
            (name, "x"));

        var error = Assert.Throws<DataArchiveException>(() => DataArchive.Open(new MemoryStream(archive)));
        Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData(null, "no manifest.json")]
    [InlineData("""{"format":"something-else","formatVersion":1,"appVersion":"1","createdUtc":"2026-11-10T00:00:00Z","sections":[]}""", "describes something else")]
    [InlineData("""{"format":"pkforge-data","formatVersion":99,"appVersion":"9.0","createdUtc":"2026-11-10T00:00:00Z","sections":[]}""", "newer PKForge (9.0)")]
    [InlineData("{not json", "manifest.json is damaged")]
    public void MalformedArchivesAreRefusedWithAReason(string? manifest, string reason)
    {
        var archive = manifest is null ? Zip(("data/item-presets.json", "[]")) : Zip(("manifest.json", manifest));

        var error = Assert.Throws<DataArchiveException>(() => DataArchive.Open(new MemoryStream(archive)));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefused()
    {
        var error = Assert.Throws<DataArchiveException>(() => DataArchive.Open(new MemoryStream(Encoding.UTF8.GetBytes("hello"))));
        Assert.Equal("This file is not a zip archive.", error.Message);
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
