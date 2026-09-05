using System.Buffers.Binary;
using System.Text;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class EmulatorContainerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RetroArchChunkRejectsMissingZlibChecksum(int removed)
    {
        var encoded = RetroArchSaveContainer.Encode(new byte[100]);
        var truncated = encoded[..^removed];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(20), (uint)(truncated.Length - 24));
        Assert.Throws<InvalidDataException>(() => RetroArchSaveContainer.Decode(truncated));
    }

    [Theory]
    [InlineData(false, "GC6E")]
    [InlineData(false, "GC6P")]
    [InlineData(false, "GC6J")]
    [InlineData(true, "GXXE")]
    [InlineData(true, "GXXP")]
    [InlineData(true, "GXXJ")]
    public void GameCubeGciPreservesHeaderAndEditsAcrossReopen(bool xd, string gameCode)
    {
        var data = CreateGameCubeGci(xd, gameCode);
        var engine = new SaveEngine();
        Assert.True(engine.Validate(data));
        var description = engine.TryDescribe(data);
        Assert.NotNull(description);
        Assert.Contains(xd ? "XD" : "Colosseum", description.GameName);
        using var session = engine.OpenSession(data, "Dolphin.gci");
        Assert.Equal(xd ? 8 : 3, session.Snapshot.Slots.Select(slot => slot.Box).Distinct().Count());
        var trainer = session.GetTrainer();
        session.SetTrainer(trainer with { Money = 12345 });
        var edited = session.Serialize().ToArray();
        Assert.Equal(data.Length, edited.Length);
        Assert.Equal(data[..0x40], edited[..0x40]);
        Assert.True(engine.Validate(edited));
        using var reopened = engine.OpenSession(edited, "Dolphin.gci");
        Assert.Equal(12345u, reopened.GetTrainer().Money);
        Assert.Equal(edited, reopened.Serialize().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DsDsvPreservesRawOrDesmumeContainer(bool footer)
    {
        // DraStic battery .dsv files can be raw DS saves; DeSmuME adds a footer.
        // Detection must use the bytes, never the shared filename extension.
        var save = BlankSaveFile.Get(GameVersion.B, "Tester", LanguageID.English);
        var raw = save.Write().ToArray();
        var data = new byte[raw.Length + (footer ? 0x7A : 0)];
        raw.CopyTo(data, 0);
        if (footer)
        {
            data.AsSpan(raw.Length).Fill(0xA5);
            var marker = "|-DESMUME SAVE-|"u8;
            marker.CopyTo(data.AsSpan(data.Length - marker.Length));
        }
        var engine = new SaveEngine();
        Assert.True(engine.Validate(data));
        using var session = engine.OpenSession(data, "Pokemon Black.dsv");
        session.SetTrainer(session.GetTrainer() with { Money = 9999 });
        var edited = session.Serialize().ToArray();
        Assert.Equal(data.Length, edited.Length);
        if (footer) Assert.Equal(data[raw.Length..], edited[raw.Length..]);
        using var reopened = engine.OpenSession(edited, "Pokemon Black.dsv");
        Assert.Equal(9999u, reopened.GetTrainer().Money);
    }

    private static byte[] CreateGameCubeGci(bool xd, string code)
    {
        // Blank GC constructors have no backing card container and cannot Write().
        // Build minimal full-size slot containers, then use the pinned game crypto.
        var raw = new byte[xd ? XDCrypto.SAVE_SIZE : ColoCrypto.SAVE_SIZE];
        for (var index = 0; index < (xd ? 2 : 3); index++)
        {
            var slot = (xd ? XDCrypto.GetSlot(raw, index) : ColoCrypto.GetSlot(raw, index)).Span;
            BinaryPrimitives.WriteUInt32LittleEndian(slot, 0x101);
            BinaryPrimitives.WriteInt32BigEndian(slot[4..], index + 1);
            if (xd)
            {
                // Offsets from SAV3XD's representative US layout, stored relative to 0xA8.
                int[] offsets = [0xA8, 0xCCD8, 0x10E08, 0xA8, 0x1CA68, 0xF678, 0xA8, 0x1CB48];
                for (var i = 0; i < offsets.Length; i++)
                {
                    var relative = offsets[i] - 0xA8;
                    BinaryPrimitives.WriteUInt16BigEndian(slot[(0x40 + i * 4)..], (ushort)relative);
                    BinaryPrimitives.WriteUInt16BigEndian(slot[(0x42 + i * 4)..], (ushort)(relative >> 16));
                }
                BinaryPrimitives.WriteUInt16BigEndian(slot[(0x20 + 5 * 2)..], 0x1774);
                BinaryPrimitives.WriteUInt16BigEndian(slot[(0x20 + 7 * 2)..], 0x2400);
                slot[0xA9] = slot[0xAA] = (byte)GCRegion.NTSC_U;
                XDCrypto.SetChecksums(slot, 0);
                XDCrypto.EncryptSlot(slot);
            }
            else
            {
                ColoCrypto.SetChecksums(slot);
                ColoCrypto.Encrypt(slot);
            }
        }
        var result = new byte[raw.Length + 0x40];
        result.AsSpan(0, 0x40).Fill(0x5A);
        Encoding.ASCII.GetBytes(code).CopyTo(result, 0);
        "01"u8.CopyTo(result.AsSpan(4));
        raw.CopyTo(result, 0x40);
        return result;
    }
}
