using System.Buffers.Binary;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Battery saves as emulators actually write them: the chip plus whatever the emulator
/// appends (RTC state, padding) and whatever container it rides in (RetroArch rzip).
/// Every one must open, round trip byte for byte, and keep its envelope after an edit,
/// or the emulator stops loading it (or silently resets its clock).
/// Sizes come from the emulators' sources: mGBA GB MBC3 = struct GBMBCRTCSaveBuffer
/// (include/mgba/internal/gb/mbc.h, 48 bytes), VBA-M = MBC3_RTC_DATA_SIZE
/// (src/core/gb/gbMemory.h: 10 ints + u64 = 48), SameBoy vba32 = 44 / vba64 = 48
/// (Core/gb.c rtc_save_t), mGBA GBA = struct GBASavedataRTCBuffer (16 bytes).
/// </summary>
public sealed class EmulatorEnvelopeTests
{
    private static readonly SaveEngine Engine = new();

    private static byte[] Footer(int length)
    {
        var footer = new byte[length];
        for (var i = 0; i < length; i++) footer[i] = (byte)(0x11 + i * 7);
        return footer;
    }

    /// <summary>
    /// A recognizer-visible retail save (PKHeX blank saves are not: Gen 3 blanks cannot
    /// Write, and the Gen 2 blank is a 64 KB Japanese-size buffer). Gen 2: the chip-sized
    /// 0x8000 SRAM with both empty Pokémon lists PKHeX keys on (SaveUtil.IsG2CrystalINT /
    /// IsG2GSINT). Gen 3: one full slot of 14 sectors, re-checksummed by PKHeX itself.
    /// </summary>
    private static byte[] Blank(GameVersion version)
    {
        if (version is GameVersion.C or GameVersion.GS)
        {
            var sram = new byte[0x8000];
            int[] lists = version == GameVersion.C ? [0x2865, 0x2D10] : [0x288A, 0x2D6C];
            foreach (var list in lists) { sram[list] = 0; sram[list + 1] = 0xFF; }
            // Through PKHeX once so the checksum is the one the game itself would have written.
            return new SAV2(sram, LanguageID.English, version).Write().ToArray();
        }

        var raw = new byte[0x20000];
        for (var id = 0; id < 14; id++)
        {
            var sector = raw.AsSpan(id * 0x1000);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[0xFF4..], (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFF8..], 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFFC..], 1);
        }
        if (version == GameVersion.E)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xAC), 0x1234_5678); // encryption key
            raw[0x900] = 1; // Emerald's small block extends past RS's 0x890 bytes
        }
        SaveFile save = version == GameVersion.E ? new SAV3E(raw) : new SAV3RS(raw);
        return save.Write().ToArray();
    }

    private static void AssertEnvelopeSurvives(byte[] bytes, int payloadLength)
    {
        Assert.True(Engine.Validate(bytes));
        Assert.NotNull(Engine.TryDescribe(bytes));
        using (var session = Engine.OpenSession(bytes, null))
        {
            Assert.True(session.Serialize().Span.SequenceEqual(bytes), "an unedited save must round trip byte for byte");
            session.SetTrainer(session.GetTrainer() with { Money = 4321 });
            var edited = session.Serialize().ToArray();
            Assert.Equal(RetroArchSaveContainer.IsCompressed(bytes), RetroArchSaveContainer.IsCompressed(edited));
            var decodedOriginal = RetroArchSaveContainer.Decode(bytes);
            var decodedEdited = RetroArchSaveContainer.Decode(edited);
            Assert.Equal(decodedOriginal.Length, decodedEdited.Length);
            Assert.Equal(decodedOriginal[payloadLength..], decodedEdited[payloadLength..]);
            Assert.True(Engine.Validate(edited));
            using var reopened = Engine.OpenSession(edited, null);
            Assert.Equal(4321u, reopened.GetTrainer().Money);
            Assert.True(reopened.Serialize().Span.SequenceEqual(edited));
        }
        Assert.Null(Engine.AssessLayoutRisk(bytes));
    }

    [Theory]
    [InlineData(GameVersion.C, 44)] // SameBoy vba32 / VBA-M loading tolerance
    [InlineData(GameVersion.C, 48)] // mGBA, VBA-M, BGB, SameBoy vba64
    [InlineData(GameVersion.GS, 48)]
    public void Gen2RtcFooterSurvives(GameVersion version, int footer)
    {
        var raw = Blank(version);
        Assert.Equal(0x8000, raw.Length);
        AssertEnvelopeSurvives([.. raw, .. Footer(footer)], raw.Length);
    }

    [Theory]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.R)]
    public void Gen3MgbaRtcFooterSurvivesTheVanillaPath(GameVersion version)
    {
        var raw = Blank(version);
        Assert.Equal(0x20000, raw.Length);
        AssertEnvelopeSurvives([.. raw, .. Footer(16)], raw.Length);
    }

    /// <summary>RetroArch's rzip wraps whatever the core wrote. The padding / footer is
    /// inside the container, so it must be re-applied before re-compressing, not after.</summary>
    [Theory]
    [InlineData(16, 0x00)]
    [InlineData(0x2000, 0xFF)]
    public void Gen3EnvelopeInsideRetroArchContainerSurvives(int extra, byte fill)
    {
        var raw = Blank(GameVersion.E);
        var tail = fill == 0 ? Footer(extra) : Enumerable.Repeat(fill, extra).ToArray();
        AssertEnvelopeSurvives(RetroArchSaveContainer.Encode([.. raw, .. tail]), raw.Length);
    }

    [Fact]
    public void CfruRtcFooterInsideRetroArchContainerSurvives()
    {
        byte[] plain = [.. GsChroniclesSessionTests.SyntheticSave(), .. Footer(16)];
        var bytes = RetroArchSaveContainer.Encode(plain);
        using var session = Engine.OpenSession(bytes, null);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes), "an unedited save must round trip byte for byte");
        Assert.True(session.DuplicateSlot(-1, 0, 0, 0)); // the party Mesprit into box 1
        var written = session.Serialize().ToArray();
        var edited = RetroArchSaveContainer.Decode(written);
        Assert.Equal(plain.Length, edited.Length);
        Assert.Equal(plain[0x20000..], edited[0x20000..]);
        using var reopened = Engine.OpenSession(written, null);
        Assert.Contains(reopened.Snapshot.Slots, slot => slot is { Box: 0, Slot: 0, Species: not null });
    }
}
