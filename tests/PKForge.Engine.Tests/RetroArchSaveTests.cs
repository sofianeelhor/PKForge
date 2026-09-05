using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class RetroArchSaveTests
{
    [Theory]
    [InlineData(131072)]
    [InlineData(64)]
    [InlineData(16384)]
    public void CompressionPreservesPayloadAndOriginalBytes(int chunkSize)
    {
        var raw = new byte[524288];
        new Random(42).NextBytes(raw);
        var compressed = RetroArchSaveContainer.Encode(raw, chunkSize);
        Assert.Equal(raw, RetroArchSaveContainer.Decode(compressed));
        Assert.Equal(compressed, RetroArchSaveContainer.Repack(raw, compressed));
        raw[42] ^= 1;
        var written = RetroArchSaveContainer.Repack(raw, compressed);
        Assert.Equal(chunkSize, (int)BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(8)));
        Assert.Equal(raw, RetroArchSaveContainer.Decode(written));
    }

    [Fact]
    public void CompressedGen5EditReopensAndPreservesEnvelope()
    {
        var save = BlankSaveFile.Get(GameVersion.B, "PKForge", LanguageID.English);
        var mon = new PK5 { Species = 25, CurrentLevel = 5 };
        mon.RefreshChecksum();
        save.SetBoxSlotAtIndex(mon, 0, 0, EntityImportSettings.None);
        var bytes = RetroArchSaveContainer.Encode(save.Write().Span);
        var engine = new SaveEngine();
        Assert.Equal("Black", engine.TryDescribe(bytes)!.GameName);
        using var session = engine.OpenSession(bytes);
        Assert.Equal(bytes, session.Snapshot.OriginalBytes.ToArray());
        Assert.Equal(bytes, session.Serialize().ToArray());
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "RZIPTEST"));
        var written = session.Serialize();
        Assert.True(RetroArchSaveContainer.IsCompressed(written.Span));
        Assert.True(engine.Validate(written));
        using var reopened = engine.OpenSession(written);
        Assert.Equal("RZIPTEST", reopened.ReadEntity(0, 0).Nickname);
    }

    [Fact]
    public void MalformedCompressionIsRejectedWithoutUnboundedAllocation()
    {
        var bytes = RetroArchSaveContainer.Encode(new byte[100]);
        Assert.Throws<InvalidDataException>(() => RetroArchSaveContainer.Decode(bytes.AsSpan(0, 12)));
        Assert.Throws<InvalidDataException>(() => RetroArchSaveContainer.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), ulong.MaxValue);
        Assert.False(new SaveEngine().Validate(bytes));
        Assert.Throws<InvalidDataException>(() => new SaveEngine().OpenSession(bytes));
    }

    [Theory]
    [InlineData("Pokemon - Ruby Version (USA, Europe) (Rev 2).srm", GameVersion.R)]
    [InlineData("Pokemon - Sapphire Version (USA, Europe) (Rev 2) (Fix).srm", GameVersion.S)]
    [InlineData("ruby.sav", GameVersion.R)]
    [InlineData("RubySapphire.sav", GameVersion.RS)]
    [InlineData("Ruby Sapphire.sav", GameVersion.RS)]
    [InlineData("main", GameVersion.RS)]
    public void OnlyUnambiguousNameResolvesRubySapphire(string name, GameVersion expected)
    {
        var save = new SAV3RS();
        SaveParser.ApplyVersionHint(save, name);
        Assert.Equal(expected, save.Version);
        var black = BlankSaveFile.Get(GameVersion.B, "PKForge", LanguageID.English);
        SaveParser.ApplyVersionHint(black, name);
        Assert.Equal(GameVersion.B, black.Version);
    }

    [Fact]
    public void SuppliedSapphireFixSaveRoundTripsWhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PKFORGE_SAPPHIRE_FIX_SAVE");
        if (string.IsNullOrEmpty(path)) return;
        var bytes = File.ReadAllBytes(path);
        var engine = new SaveEngine();
        Assert.Equal("Sapphire", engine.TryDescribe(bytes, Path.GetFileName(path))!.GameName);
        using var session = engine.OpenSession(bytes, Path.GetFileName(path));
        Assert.Equal(3, session.Generation);
        Assert.Equal(bytes, session.Snapshot.OriginalBytes.ToArray());
        Assert.Equal(bytes, session.Serialize().ToArray());
        var party = session.ReadEntity(-1, 0);
        Assert.False(party.IsEmpty);
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "PKFORGE"));
        var written = session.Serialize();
        Assert.True(engine.Validate(written));
        using var reopened = engine.OpenSession(written, Path.GetFileName(path));
        Assert.Equal("PKFORGE", reopened.ReadEntity(-1, 0).Nickname);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("Ruby", engine.TryDescribe(bytes, "Pokemon - Ruby Version (USA, Europe) (Rev 2).srm")!.GameName);
    }
}
