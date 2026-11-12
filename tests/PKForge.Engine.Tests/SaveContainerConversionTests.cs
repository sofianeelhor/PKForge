using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// A save moves between emulator file shapes (.sav, RetroArch's .srm, DraStic's .dsv) only
/// when converting back gives the very bytes the player had; everything else is refused.
/// </summary>
public sealed class SaveContainerConversionTests
{
    // The real copies stay outside the repository; these tests are skipped without them.
    private static byte[]? Local(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, ".local-testdata", "thor", file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        return null;
    }

    private static byte[] Random(int size, int seed = 7)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] RoundTrip(byte[] bytes, string name, int generation, SaveContainer target)
    {
        var there = SaveContainerConversion.Convert(bytes, name, generation, target);
        Assert.True(there.Succeeded, there.Refusal);
        var from = SaveContainerConversion.Detect(bytes, name);
        var back = SaveContainerConversion.Convert(there.Bytes!, there.FileName!, generation, from);
        Assert.True(back.Succeeded, back.Refusal);
        return back.Bytes!;
    }

    [Fact]
    public void ADsSaveGoesToDsvAndBack_ByteForByte()
    {
        var sav = Random(0x80000);
        var dsv = SaveContainerConversion.Convert(sav, "Platinum.sav", 4, SaveContainer.DeSmuME);

        Assert.True(dsv.Succeeded, dsv.Refusal);
        Assert.Equal("Platinum.dsv", dsv.FileName);
        Assert.Equal(sav, dsv.Bytes![..sav.Length]);
        Assert.Equal(SaveContainer.DeSmuME, SaveContainerConversion.Detect(dsv.Bytes, "Platinum.dsv"));
        Assert.Equal(sav, RoundTrip(sav, "Platinum.sav", 4, SaveContainer.DeSmuME));
        Assert.Equal(dsv.Bytes, RoundTrip(dsv.Bytes, "Platinum.dsv", 4, SaveContainer.Raw));
    }

    [Fact]
    public void ASavIsAnSrmByTheSameBytes()
    {
        var sav = Random(0x20000);
        var srm = SaveContainerConversion.Convert(sav, "Emerald.sav", 3, SaveContainer.RetroArch);

        Assert.True(srm.Succeeded, srm.Refusal);
        Assert.Equal("Emerald.srm", srm.FileName);
        Assert.Equal(sav, srm.Bytes);
        Assert.Equal(sav, RoundTrip(sav, "Emerald.sav", 3, SaveContainer.RetroArch));
    }

    [Fact]
    public void ACompressedSrmIsUnpackedToItsMemory()
    {
        var memory = Random(0x80000);
        var srm = RetroArchSaveContainer.Encode(memory);

        var sav = SaveContainerConversion.Convert(srm, "Black.srm", 5, SaveContainer.Raw);

        Assert.True(sav.Succeeded, sav.Refusal);
        Assert.Equal(memory, sav.Bytes);
    }

    [Fact]
    public void WhatCannotBeProvedLosslessIsRefusedWithAReason()
    {
        // Not a cartridge save.
        Assert.False(SaveContainerConversion.Convert(Random(0x100), "main", 8, SaveContainer.Raw).Succeeded);
        // A Game Boy Advance save has no .dsv.
        Assert.Contains("DS games only", SaveContainerConversion.Convert(Random(0x20000), "Ruby.sav", 3, SaveContainer.DeSmuME).Refusal);
        // A size DeSmuME has no footer for.
        Assert.NotNull(SaveContainerConversion.Convert(Random(0x12345), "odd.sav", 4, SaveContainer.DeSmuME).Refusal);
        // A .dsv whose footer is not the one PKForge would rebuild cannot go and come back.
        var dsv = SaveContainerConversion.Convert(Random(0x80000), "Pt.sav", 4, SaveContainer.DeSmuME).Bytes!;
        dsv[0x80000 + 82 + 8] = 9; // the memory type word
        var refused = SaveContainerConversion.Convert(dsv, "Pt.dsv", 4, SaveContainer.Raw);
        Assert.False(refused.Succeeded);
        Assert.Contains(".dsv footer", refused.Refusal);
        // Already in that shape.
        Assert.False(SaveContainerConversion.Convert(Random(0x20000), "x.sav", 3, SaveContainer.Raw).Succeeded);
    }

    [Theory]
    [InlineData("nds/Pokemon - Platinum Version (Europe).sav", 4, SaveContainer.DeSmuME)]
    [InlineData("nds/Pokemon - Version Noire (France) (NDSi Enhanced).sav", 5, SaveContainer.DeSmuME)]
    [InlineData("retroarch/POKEMON_EMER_BPEJ_00.srm", 3, SaveContainer.Raw)]
    [InlineData("retroarch/mGBA/Pokemon Fire Red.sav", 3, SaveContainer.RetroArch)]
    public void RealSavesConvertAndComeBackUnchanged(string file, int generation, SaveContainer target)
    {
        if (Local(file) is not { } bytes) return;
        var name = Path.GetFileName(file);
        var converted = SaveContainerConversion.Convert(bytes, name, generation, target);
        Assert.True(converted.Succeeded, converted.Refusal);
        Assert.Equal(bytes, RoundTrip(bytes, name, generation, target));
        // The converted file is still the same save to PKForge.
        var engine = new SaveEngine();
        Assert.Equal(engine.TryDescribe(bytes)!.TrainerName, engine.TryDescribe(converted.Bytes!)!.TrainerName);
    }
}
