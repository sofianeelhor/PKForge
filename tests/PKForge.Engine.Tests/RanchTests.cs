using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class RanchTests
{
    private static readonly SaveEngine Engine = new();

    // Synthetic original-revision object arrays, not player data. Dimensions and
    // offsets follow the pinned SAV4Ranch/RanchMii/RanchTrainerMii/RK4 readers.
    private static byte[] Save(int level = 25, int last = 0)
    {
        var data = new byte[SaveUtil.SIZE_G4RANCH];
        data[0x5A] = data[0x5B] = (byte)(level - 1);
        int[] offsets = [0x40, 0x22A8, 0x22D8, 0x230C, 0x230C + 8 + (last + 1) * 164];
        int[] sizes = [0x2260, 0x28, 0x2C, 164, 0x28];
        int[] counts = [1, 1, 1, last + 1, 0];
        Set(data, 0x14, 0x00030005);
        for (int i = 0; i < 5; i++)
        {
            Set(data, 0x18 + i * 8, (uint)i);
            Set(data, 0x1C + i * 8, (uint)offsets[i]);
            Set(data, offsets[i], (uint)sizes[i]);
            Set(data, offsets[i] + 4, (uint)counts[i]);
        }
        var mon = new RK4 { Species = 25, PID = 12345, ID32 = 23456, Language = 2,
            Version = GameVersion.D, OwnershipType = RanchOwnershipType.Trainer,
            HandlingTrainerTID = 99, HandlingTrainerSID = 88, HandlingTrainerName = "HANDLER" };
        mon.RefreshChecksum();
        mon.WriteEncryptedDataStored(data.AsSpan(0x2314 + last * 164, 164));
        Hash(data);
        return data;
    }

    private static void Set(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    private static void Hash(byte[] data) => SHA1.HashData(data.AsSpan(20), data.AsSpan(0, 20));

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 25)]
    [InlineData(3, 30)]
    [InlineData(25, 1000)]
    public void CapacityAndNativeExportAreExact(int level, int capacity)
    {
        var source = Save(level, capacity - 1);
        using var session = Engine.OpenSession(source);
        Assert.True(Engine.Validate(source));
        Assert.Equal("My Pokémon Ranch", Engine.TryDescribe(source)!.GameName);
        Assert.Equal(["My Pokémon Ranch"], session.GameNames);
        Assert.False(session.HasParty);
        Assert.NotNull(session.ReadOnlyReason);
        Assert.False(session.SupportsBoxTools);
        Assert.Equal(capacity, session.Snapshot.Slots.Count);
        Assert.Equal(capacity, Engine.Open(source).Slots.Count);
        var last = session.Snapshot.Slots[^1];
        var exported = session.ExportSlot(last.Box, last.Slot);
        Assert.Equal("RK4", exported.Format);
        var native = new RK4(exported.Data);
        Assert.Equal(25, native.Species);
        Assert.Equal(RanchOwnershipType.Trainer, native.OwnershipType);
        Assert.Equal(99, native.HandlingTrainerTID);
        Assert.Equal(88, native.HandlingTrainerSID);
        Assert.Equal("HANDLER", native.HandlingTrainerName);
        Assert.True(native.ConvertToPK4().ChecksumValid);
        Assert.True(session.Serialize().Span.SequenceEqual(source));
        Assert.DoesNotContain(25, session.GetMissingSpecies());
        Assert.NotEmpty(session.ExportBoxShowdown(last.Box));
        session.GetNuzlockeReport();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadEntity(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ExportSlot(capacity / 30, capacity % 30));
    }

    [Theory]
    [InlineData(0x00, 1u)] // file hash
    [InlineData(0x14, 0u)] // unsupported schema
    [InlineData(0x1C, 0xFFFFFFFCu)] // overflow/outside file
    [InlineData(0x24, 0x40u)] // backward/overlapping block
    [InlineData(0x22AC, 21u)] // more Miis than the ranch allows
    [InlineData(0x2310, 1001u)] // count beyond capacity
    [InlineData(0x230C, 136u)] // PK4 is not an RK4 record
    [InlineData(0x3C, 0x2314u)] // end marker inside Pokemon record
    public void CorruptOrUnknownLayoutsDoNotReachTheCoreReader(int offset, uint value)
    {
        var data = Save();
        Set(data, offset, value);
        if (offset != 0) Hash(data); // independently valid file hash must not hide invalid structure
        Assert.False(Engine.Validate(data));
        Assert.Null(Engine.TryDescribe(data));
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(data));
    }

    [Theory]
    [InlineData(0x5A, 25)]
    [InlineData(0x5B, 255)]
    [InlineData(0x2314 + 0x88, 3)]
    public void InvalidLevelsAndOwnershipAreRejected(int offset, byte value)
    {
        var data = Save(); data[offset] = value; Hash(data);
        Assert.False(Engine.Validate(data));
    }

    [Fact]
    public void PortableConversionExplainsLossAndKeepsNativeOwnership()
    {
        using var session = Engine.OpenSession(Save());
        var native = new RK4(session.ExportSlot(0, 0).Data);
        var conversion = CrossFormatConverter.Convert(native, new SAV4DP(), out var refusal);
        Assert.Null(refusal);
        Assert.NotNull(conversion);
        Assert.IsType<PK4>(conversion.Entity);
        Assert.Contains(conversion.Warnings, s => s.Contains("Ranch ownership", StringComparison.Ordinal));
        Assert.Equal(25, conversion.Entity.Species);
        Assert.Equal(RanchOwnershipType.Trainer, native.OwnershipType);
        Assert.Equal("HANDLER", native.HandlingTrainerName);
    }

    [Fact]
    public void UnverifiedPlatinumRevisionIsRefused()
    {
        var data = new byte[SaveUtil.SIZE_G4RANCH_PLAT];
        Assert.False(Engine.Validate(data));
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(data));
        Assert.NotNull(Engine.GetWriteRestriction(data));
    }

    [Fact]
    public void EverySessionMutationRefusesBeforeChangingSource()
    {
        var data = Save();
        using var session = Engine.OpenSession(data);
        var methods = typeof(ISaveEngineSession).GetMethods().Where(m => !m.IsSpecialName &&
            !m.Name.StartsWith("Get", StringComparison.Ordinal) && !m.Name.StartsWith("Is", StringComparison.Ordinal) &&
            !m.Name.StartsWith("Read", StringComparison.Ordinal) && !m.Name.StartsWith("Export", StringComparison.Ordinal) &&
            m.Name is not ("Serialize" or "MakeActive" or "Dispose"));
        foreach (var method in methods)
        {
            var args = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(session, args));
            Assert.IsType<NotSupportedException>(error.InnerException);
            Assert.True(session.Serialize().Span.SequenceEqual(data), method.Name);
        }
    }

    [Fact]
    public void SiblingEditorsCannotBypassTheReadOnlySession()
    {
        var data = Save(); using var session = Engine.OpenSession(data);
        Assert.Throws<NotSupportedException>(() => new LegalizerService().LegalizeSlot(session, 0, 0));
        Assert.Throws<NotSupportedException>(() => MonFieldService.SetPid(session, 0, 0, 0x87654321));
        Assert.Throws<NotSupportedException>(() => BulkBoxActions.Heal(session, [(0, 0)]));
        Assert.Throws<NotSupportedException>(() => new EventDatabaseService().Receive(session, 0, 0, 0));
        Assert.True(session.Serialize().Span.SequenceEqual(data));
        Assert.Equal(12345u, new RK4(session.ExportSlot(0, 0).Data).PID);
    }

    [Fact]
    public async Task SafeWriterRefusesEvenUnrestrictedRestoresBeforeBackupOrWrite()
    {
        var source = Save(); var candidate = source.ToArray(); candidate[0x60] ^= 1; Hash(candidate);
        var storage = new NoWrites();
        var writer = new SafeSaveWriter(Engine, storage, storage);
        writer.ConfirmLayoutRisk("ranch");
        Assert.NotNull(writer.WhyWritesAreRefused("ranch", Engine.Open(source)));
        Assert.NotNull(Engine.CheckWriteSafety(source, candidate, WriteScope.Everything));
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteScopedAsync("ranch", Engine.Open(source), candidate, WriteScope.Everything).AsTask());
        Assert.Equal(0, storage.Writes);
        Assert.Equal(0, storage.Backups);
    }

    [Theory]
    [InlineData("ranch-a.bin", 975)]
    [InlineData("ranch-a-backup.bin", 975)]
    [InlineData("ranch-b.bin", 992)]
    [InlineData("ranch-b-backup.bin", 992)]
    public void OptionalPrivateFixturesKeepAllNativeRecords(string file, int occupied)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln"))) directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "ranch", file);
        if (path is null || !File.Exists(path)) return;
        var source = File.ReadAllBytes(path);
        using var session = Engine.OpenSession(source);
        var records = session.Snapshot.Slots.Where(s => s.Species is not null).ToList();
        Assert.Equal(occupied, records.Count);
        foreach (var slot in records)
        {
            var raw = session.ExportSlot(slot.Box, slot.Slot);
            Assert.Equal(slot.Species, Engine.TryDescribeEntity(raw.Data, "test", raw.Format)!.Species);
            Assert.True(new RK4(raw.Data).ChecksumValid);
        }
        Assert.True(session.Serialize().Span.SequenceEqual(source));
        Assert.Equal(source, File.ReadAllBytes(path));
    }

    private sealed class NoWrites : IBackupService, ISaveFileAccess
    {
        public int Writes { get; private set; }
        public int Backups { get; private set; }
        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, string? documentId = null, CancellationToken cancellationToken = default)
        { Backups++; throw new InvalidOperationException("No backup expected"); }
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException("No write expected"); }
        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
