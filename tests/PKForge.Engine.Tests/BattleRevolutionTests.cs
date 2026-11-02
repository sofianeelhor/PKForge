using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class BattleRevolutionTests
{
    private static readonly SaveEngine Engine = new();

    // SAV4BR @91e9c886: a zero-filled decrypted container has valid zero bit-count
    // checksums. Seed all profiles through its native API, then finalize/encrypt.
    // Fixtures are generated in memory; no player saves or game data are bundled.
    internal static byte[] Fixture(int language = (int)LanguageID.English)
    {
        var save = new SAV4BR(new byte[SaveUtil.SIZE_G4BR], decrypt: false);
        for (var profile = 0; profile < 4; profile++)
        {
            save.CurrentSlot = profile;
            save.Language = language;
            save.CurrentOT = $"P{profile}";
            var pk = new BK4 { Species = 25, PID = 1234, EXP = 1000, Language = language,
                OriginalTrainerName = "TEST", Nickname = "PIKACHU" };
            pk.RefreshChecksum();
            save.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
            save.SetPartySlotAtIndex(pk.Clone(), 0, EntityImportSettings.None);
        }
        return save.Write().ToArray();
    }

    private static byte[] Rename(byte[] original, int profile)
    {
        var save = new SAV4BR(original.ToArray());
        save.CurrentSlot = profile;
        var pk = save.GetBoxSlotAtIndex(0, 0);
        pk.Nickname = "CHANGED";
        pk.RefreshChecksum();
        save.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        return save.Write().ToArray();
    }

    [Fact]
    public void SyntheticSaveRoundTripsThroughTheEngine()
    {
        var original = Fixture();
        Assert.True(Engine.Validate(original));
        using var session = Engine.OpenSession(original);
        Assert.True(session.Serialize().Span.SequenceEqual(original));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ScopeCannotHideChangesInAnotherProfile(int profile)
    {
        var original = Fixture();
        Assert.NotNull(Engine.CheckWriteSafety(original, Rename(original, profile),
            WriteScope.Only(new SlotRef(0, 0))));
    }

    [Fact]
    public void DescriptionUsesTheActualProfileName()
    {
        Assert.Equal("P0", Engine.TryDescribe(Fixture())!.TrainerName);
    }

    [Fact]
    public void SwitchingProfilesAndListingTheirNamesDoesNotChangeTheFile()
    {
        var bytes = Fixture();
        using var session = Engine.OpenSession(bytes);
        var profiles = Assert.IsAssignableFrom<IProfiledSaveSession>(session);
        Assert.Equal(new[] { "P0", "P1", "P2", "P3" }, profiles.Profiles.Select(p => p.Name));
        foreach (var index in new[] { 3, 1, 0, 2 })
        {
            profiles.SelectProfile(index);
            Assert.Equal(index, session.Snapshot.ProfileIndex);
            Assert.Equal($"P{index}", session.GetTrainer().Name);
            Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => profiles.SelectProfile(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => profiles.SelectProfile(4));
        Assert.Equal(2, profiles.CurrentProfile);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExplicitProfileScopeAllowsOnlyItsTargetedSlot(int index)
    {
        var bytes = Fixture();
        var candidate = Rename(bytes, index);
        Assert.Null(Engine.CheckWriteSafety(bytes, candidate,
            WriteScope.Only(new SlotRef(0, 0)) with { ProfileIndex = index }));
        Assert.NotNull(Engine.CheckWriteSafety(bytes, candidate,
            WriteScope.Only(new SlotRef(0, 1)) with { ProfileIndex = index }));
        var reopened = new SAV4BR(candidate.ToArray());
        for (var other = 0; other < 4; other++)
        {
            reopened.CurrentSlot = other;
            Assert.Equal(other == index ? "CHANGED" : "PIKACHU", reopened.GetBoxSlotAtIndex(0, 0).Nickname);
        }
        Assert.True(bytes.AsSpan(SAV4BR.SIZE_HALF).SequenceEqual(candidate.AsSpan(SAV4BR.SIZE_HALF)),
            "The inactive partition must stay byte-identical.");
    }

    [Fact]
    public void OtherProfileMetadataCannotEvadeTheSlotScope()
    {
        var bytes = Fixture();
        var save = new SAV4BR(bytes.ToArray()) { CurrentSlot = 2, CurrentOT = "NEW" };
        Assert.NotNull(Engine.CheckWriteSafety(bytes, save.Write(), WriteScope.Only(new SlotRef(0, 0))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartyScopeCannotAuthorizeTheSamePartySlotInAnotherProfile(int profile)
    {
        var original = Fixture();
        using var session = Engine.OpenSession(original);
        ((IProfiledSaveSession)session).SelectProfile(profile);
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "PARTY"));
        var candidate = session.Serialize();
        var scope = WriteScope.Only(new SlotRef(-1, 0)) with { ProfileIndex = profile };
        Assert.Null(Engine.CheckWriteSafety(original, candidate, scope));
        Assert.NotNull(Engine.CheckWriteSafety(original, candidate,
            scope with { ProfileIndex = (profile + 1) % 4 }));
        using var reopened = Engine.OpenSession(candidate);
        var profiles = (IProfiledSaveSession)reopened;
        for (var other = 0; other < 4; other++)
        {
            profiles.SelectProfile(other);
            Assert.Equal(other == profile ? "PARTY" : "PIKACHU", reopened.ReadEntity(-1, 0).Nickname);
        }
    }

    [Fact]
    public void TrainerRenamePersistsInTheSelectedProfile()
    {
        using var session = Engine.OpenSession(Fixture());
        var profiles = (IProfiledSaveSession)session;
        profiles.SelectProfile(2);
        session.SetTrainer(session.GetTrainer() with { Name = "NEW" });
        var bytes = session.Serialize();
        using var reopened = Engine.OpenSession(bytes);
        ((IProfiledSaveSession)reopened).SelectProfile(2);
        Assert.Equal("NEW", reopened.GetTrainer().Name);
        Assert.Equal("NEW", profiles.Profiles[2].Name);
        profiles.SelectProfile(0);
        Assert.Equal("P0", session.GetTrainer().Name);
    }

    [Fact]
    public void UnrepresentableTrainerRenameLeavesTheSessionUnchanged()
    {
        var bytes = Fixture();
        using var session = Engine.OpenSession(bytes);
        Assert.Throws<InvalidOperationException>(() => session.SetTrainer(session.GetTrainer() with { Name = "TOOLONG" }));
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    [Theory]
    [InlineData(1, "JA", "雪月", "テスト")]
    [InlineData(2, "EN", "Jo", "TEST")]
    [InlineData(3, "FR", "Été", "Été")]
    [InlineData(4, "IT", "Più", "Caffè")]
    [InlineData(5, "DE", "Öde", "MÄX")]
    [InlineData(7, "ES", "Peña", "NIÑO")]
    public void NativeLanguageAndUnicodeNamesSurviveProfileEdits(int language, string tag, string name, string nickname)
    {
        var original = Fixture(language);
        Assert.Equal(tag, Engine.TryDescribe(original)!.Language);
        using var session = Engine.OpenSession(original);
        var profiles = (IProfiledSaveSession)session;
        profiles.SelectProfile(2);
        Assert.True(session.Serialize().Span.SequenceEqual(original));
        session.SetTrainer(session.GetTrainer() with { Name = name });
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: nickname));
        var candidate = session.Serialize();
        Assert.Null(Engine.CheckWriteSafety(original, candidate,
            WriteScope.Only(new SlotRef(0, 0)) with { ProfileIndex = 2 }));
        using var reopened = Engine.OpenSession(candidate);
        var selection = (IProfiledSaveSession)reopened;
        selection.SelectProfile(2);
        Assert.Equal(name, reopened.GetTrainer().Name);
        Assert.Equal(name, selection.Profiles[2].Name);
        Assert.Equal(nickname, reopened.ReadEntity(0, 0).Nickname);
        var export = reopened.ExportSlot(0, 0);
        Assert.Equal("BK4", export.Format);
        Assert.Equal(nickname, EntityBytes.Parse(export.Data, export.Format)!.Nickname);
        Assert.True(reopened.Serialize().Span.SequenceEqual(candidate.Span));
        selection.SelectProfile(0);
        Assert.Equal("P0", reopened.GetTrainer().Name);
        Assert.Equal("PIKACHU", reopened.ReadEntity(0, 0).Nickname);
        Assert.Equal(tag, Engine.TryDescribe(candidate)!.Language);
    }

    [Fact]
    public async Task RevertAndSuccessfulWritesRetainSelectedProfileAndFullBaseline()
    {
        var original = Fixture();
        var service = new SaveSessionService(new MemoryAccess(original), Engine);
        await service.OpenAsync(new PickedDocument("pbr", "PbrSaveData"));
        service.SelectProfile(3);
        service.CurrentSession!.ApplyEdit(0, 0, new EntityEdit(Nickname: "FIRST"));
        var candidate = service.CurrentSession.Serialize();
        service.MarkWritten("pbr", candidate);
        service.CurrentSession.ApplyEdit(0, 0, new EntityEdit(Nickname: "SECOND"));
        service.RevertToBaseline();
        Assert.Equal(3, ((IProfiledSaveSession)service.CurrentSession!).CurrentProfile);
        Assert.Equal(3, service.Current!.Snapshot.ProfileIndex);
        Assert.Equal("FIRST", service.CurrentSession.ReadEntity(0, 0).Nickname);
        Assert.True(service.Current.Snapshot.OriginalBytes.Span.SequenceEqual(candidate.Span));
        service.SelectProfile(0);
        Assert.Equal("PIKACHU", service.CurrentSession.ReadEntity(0, 0).Nickname);
        Assert.True(service.Current.Snapshot.OriginalBytes.Span.SequenceEqual(candidate.Span));
    }

    [Fact]
    public async Task WriterBindsOrdinaryEditsToTheSelectedProfileBeforeBackingUp()
    {
        var original = Fixture();
        using var session = Engine.OpenSession(original);
        ((IProfiledSaveSession)session).SelectProfile(2);
        var access = new MemoryAccess(original);
        var backups = new MemoryBackups(access);
        var writer = new SafeSaveWriter(Engine, backups, access);
        var snapshot = session.Snapshot;
        var noop = await writer.WriteAsync("pbr", snapshot, original);
        Assert.False(noop.Changed);
        Assert.Equal(0, backups.Count);
        Assert.Equal(0, access.Writes);
        // An out-of-profile candidate must fail even on the general write path.
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAsync("pbr", snapshot, Rename(original, 1)).AsTask());
        Assert.Equal(0, backups.Count);
        Assert.Equal(0, access.Writes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync("pbr", snapshot, new byte[20]).AsTask());
        Assert.Equal(0, backups.Count);
        Assert.Equal(0, access.Writes);
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync("pbr", snapshot, Rename(original, 2),
                WriteScope.Only(new SlotRef(0, 0)) with { ProfileIndex = 1 }).AsTask());
        Assert.Equal(0, backups.Count);
        Assert.Equal(0, access.Writes);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "SAVED"));
        var candidate = session.Serialize();
        await writer.WriteScopedAsync("pbr", snapshot, candidate, WriteScope.Only(new SlotRef(0, 0)));
        Assert.Equal(1, backups.Count);
        Assert.Equal(1, access.Writes);
        Assert.True(backups.Bytes!.AsSpan().SequenceEqual(original));
        Assert.True(access.Bytes.AsSpan().SequenceEqual(candidate.Span));
    }

    [Fact]
    public void InvalidSizesAndBothDamagedPartitionsAreRejected()
    {
        Assert.False(Engine.Validate(new byte[0x380000 - 1]));
        Assert.False(Engine.Validate(new byte[3732864])); // Wii data.bin archive size
        var damaged = Fixture();
        damaged[0x1000] ^= 1;
        damaged[SAV4BR.SIZE_HALF + 0x1000] ^= 1;
        Assert.False(Engine.Validate(damaged));
    }

    [Fact]
    public void SecondPartitionFallbackSupportsEveryProfileWithoutRepairingTheDamagedFirst()
    {
        var bytes = Fixture();
        bytes.AsSpan(0, SAV4BR.SIZE_HALF).CopyTo(bytes.AsSpan(SAV4BR.SIZE_HALF));
        bytes[0x1000] ^= 1; // a damaged first partition forces the valid second one
        using var session = Engine.OpenSession(bytes);
        var profiles = (IProfiledSaveSession)session;
        foreach (var index in Enumerable.Range(0, 4))
        {
            profiles.SelectProfile(index);
            Assert.Equal($"P{index}", session.GetTrainer().Name);
            Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        }
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "BACKUP"));
        var candidate = session.Serialize();
        Assert.True(bytes.AsSpan(0, SAV4BR.SIZE_HALF).SequenceEqual(candidate.Span[..SAV4BR.SIZE_HALF]));
        Assert.Null(Engine.CheckWriteSafety(bytes, candidate,
            WriteScope.Only(new SlotRef(0, 0)) with { ProfileIndex = 3 }));
    }

    [Fact]
    public void EmptyProfilesKeepTheirIndicesWhenTheAutomaticDefaultChanges()
    {
        var save = new SAV4BR(Fixture()) { CurrentSlot = 0, CurrentOT = "" };
        var bytes = save.Write();
        using var session = Engine.OpenSession(bytes);
        var profiles = (IProfiledSaveSession)session;
        Assert.Equal(1, profiles.CurrentProfile);
        Assert.True(profiles.Profiles[0].IsEmpty);
        profiles.SelectProfile(0);
        session.SetTrainer(session.GetTrainer() with { Name = "NEW" });
        Assert.Null(Engine.CheckWriteSafety(bytes, session.Serialize(),
            WriteScope.Only() with { ProfileIndex = 0 }));
        Assert.NotNull(Engine.CheckWriteSafety(bytes, session.Serialize(),
            WriteScope.Only() with { ProfileIndex = 1 }));
    }

    private sealed class MemoryAccess(byte[] bytes) : ISaveFileAccess
    {
        public byte[] Bytes = bytes.ToArray();
        public int Writes;
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Bytes);
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Bytes = bytes.ToArray();
            Writes++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryBackups(MemoryAccess access) : IBackupService
    {
        public int Count;
        public byte[]? Bytes;
        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null,
            string? documentId = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, access.Writes);
            Count++;
            Bytes = source.OriginalBytes.ToArray();
            return ValueTask.FromResult(new BackupReceipt("pbr-backup", DateTimeOffset.UtcNow, "hash"));
        }
        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<BackupInfo>>([]);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Bytes ?? []);
    }
}
