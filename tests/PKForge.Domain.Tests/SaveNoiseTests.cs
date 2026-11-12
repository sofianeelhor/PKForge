using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// "My menu shows ten copies of each game": Syncthing's hidden versions folder and the
/// backups emulators keep stood on the shelf like any other save. They are recognized and
/// start in Hidden saves instead.
/// </summary>
public sealed class SaveNoiseTests
{
    [Theory]
    [InlineData(".stversions")]
    [InlineData(".trash")]
    [InlineData("backup")]
    [InlineData("Backups")]
    [InlineData("save_backup_2026-10-12")]
    [InlineData("lost+found")]
    public void HiddenAndBackupFoldersHoldCopies(string folder) => Assert.True(SaveNoise.IsNoiseFolder(folder));

    [Theory]
    [InlineData("saves")]
    [InlineData("mGBA")]
    [InlineData("0100ABF008968000")]
    [InlineData("Card A")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryFoldersHoldSaves(string? folder) => Assert.False(SaveNoise.IsNoiseFolder(folder));

    [Theory]
    [InlineData("Pokemon Black (backup).sav")]
    [InlineData("Pokemon - Emerald Version.srm.bak")]
    [InlineData("main.backup")]
    [InlineData("Pokemon Crystal.sync-conflict-20261012-183000-ABCDEFG.sav")]
    [InlineData("Pokemon Red.sav~")]
    [InlineData("Pokemon - Version Noire (France) (copy).sav")]
    [InlineData("main (copy 2)")]
    [InlineData(".Pokemon Red.sav")]
    public void AFileCanSayItIsACopy(string file) => Assert.True(SaveNoise.IsBackupCopy(file));

    [Theory]
    [InlineData("main")]
    [InlineData("Pokemon - Emerald Version (USA, Europe).srm")]
    [InlineData("Pokemon Black.dsv")]
    [InlineData("01-GPXE-pokemon_xd.gci")]
    [InlineData("")]
    [InlineData(null)]
    public void RealSavesAreNotCopies(string? file) => Assert.False(SaveNoise.IsBackupCopy(file));

    [Theory]
    [InlineData(".stversions/Pokemon - Emerald Version~20261012-183000.srm")]
    [InlineData("mGBA/.stversions/Pokemon - Emerald Version.srm")]
    [InlineData("0100ABF008968000/backup/main")]
    [InlineData("Pokemon Black (backup).sav")]
    public void ACopyIsKnownByItsNameOrByAFolderAboveIt(string path) => Assert.True(SaveNoise.IsCopyPath(path));

    [Theory]
    [InlineData("Pokemon - Emerald Version.srm")]
    [InlineData("mGBA/Pokemon - Emerald Version.srm")]
    [InlineData("nand/user/save/0000000000000000/ABCDEF/0100ABF008968000/main")]
    [InlineData("")]
    [InlineData(null)]
    public void ASaveInOrdinaryFoldersIsNotACopy(string? path) => Assert.False(SaveNoise.IsCopyPath(path));

    [Fact]
    public void DraSticsOwnSaveFolderIsNamedBackup()
    {
        Assert.False(SaveNoise.IsCopyPath("backup/Pokemon Black.dsv", savesFolder: "backup"));
        Assert.True(SaveNoise.IsCopyPath("backup/Pokemon Black.dsv"));
        Assert.True(SaveNoise.IsCopyPath("backup/.stversions/Pokemon Black.dsv", savesFolder: "backup"));
    }
}
