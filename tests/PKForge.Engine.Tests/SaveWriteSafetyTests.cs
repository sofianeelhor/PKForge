using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Regression net for the data-loss report "raised one of my Pokémon to level 100, made
/// all my party Pokémon appear to come from eggs, and lost one of my Pokémon boxes":
/// a CFRU ROM-hack save that misses the Radical Red / Unbound detectors is parsed by
/// stock PKHeX as vanilla FireRed (garbage party, levels up to 100, every checksum bad)
/// and any write re-checksums its sectors over the vanilla windows, which the game then
/// rejects. SafeSaveWriter now refuses both the ambiguous layout and any diff that
/// corrupts or touches slots the mutation did not target.
/// </summary>
public sealed class SaveWriteSafetyTests
{
    private static readonly SaveEngine Engine = new();

    private static byte[]? Local(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null; // gitignored: dev-only
    }

    /// <summary>A portable vanilla Ruby/Sapphire save: 14 live sections, retail signature,
    /// finalized by PKHeX itself so every checksum is the vanilla one. Two box mons.</summary>
    private static byte[] SyntheticVanillaGen3()
    {
        var data = new byte[0x20000];
        for (var sector = 0; sector < 14; sector++)
        {
            var off = sector * 0x1000;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF4), (ushort)sector);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFF8), 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFFC), 1);
        }
        data.AsSpan(0xE000).Fill(0xFF); // second slot erased
        Assert.True(SaveUtil.TryGetSaveFile(data, out var save) && save is SAV3, "synthetic Gen 3 save did not parse");
        foreach (var (slot, species) in new[] { (0, (ushort)25), (1, (ushort)150) })
        {
            var pk = new PK3 { Species = species, PID = 0x1234_5678u + (uint)slot, TID16 = 1, EXP = 1000, Language = 2 };
            pk.OriginalTrainerName = "PKF";
            pk.Nickname = "MON";
            pk.RefreshChecksum();
            save!.SetBoxSlotAtIndex(pk, 0, slot, EntityImportSettings.None);
        }
        return save!.Write().ToArray();
    }

    private static (SafeSaveWriter Writer, MemoryAccess Access) Writer()
    {
        var access = new MemoryAccess();
        return (new SafeSaveWriter(Engine, new NullBackups(), access), access);
    }

    private static SaveSnapshot Snap(byte[] bytes) => new("test", 3, bytes, [], null);

    // ── Round trips: open -> serialize with no edit must be byte-identical ──

    [Theory]
    [InlineData("unbound-v2111.srm")]
    [InlineData("radicalred-champ.sav")]
    [InlineData("firered-vanilla.sav")]
    public void EverySessionTypeRoundTripsLocalSamplesByteIdentical(string file)
    {
        if (Local(file) is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, file);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes), $"{session.GetType().Name} changed bytes on an untouched round trip");
        Assert.Null(Engine.DescribeLayoutRisk(bytes)); // recognized layouts are not ambiguous
    }

    [Fact]
    public void SyntheticVanillaGen3RoundTripsAndIsNotFlagged()
    {
        var bytes = SyntheticVanillaGen3();
        using var session = Engine.OpenSession(bytes);
        Assert.IsType<SaveEngineSession>(session);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        Assert.Null(Engine.DescribeLayoutRisk(bytes));
    }

    // ── Root cause reproduction ──

    [Fact]
    public async Task MisdetectedRadicalRedFallsToVanillaAndReproducesTheIncident()
    {
        if (Local("radicalred-champ.sav") is not { } rr) return;

        // One live section with a stale CFRU checksum (a variant or mid-save state) is
        // enough to miss IsRadicalRed: the save silently routes to stock FireRed.
        var broken = rr.ToArray();
        var live13 = 14 * 0x1000; // sector 14 holds section 13 at save index 295
        broken[live13 + 0xFF6] ^= 0x01;
        using var session = Engine.OpenSession(broken, "hack.sav");
        Assert.IsType<SaveEngineSession>(session);

        // Symptoms: every party mon is unreadable ("from eggs") and levels are garbage (100).
        var party = Enumerable.Range(0, 6).Select(i => ((SaveEngineSession)session).GetEntity(-1, i)).ToList();
        Assert.All(party, pk => Assert.False(pk.ChecksumValid));
        Assert.Contains(party, pk => pk.CurrentLevel == 100);

        // Damage: an untouched serialize already re-checksums live CFRU sectors over the
        // vanilla windows, which the game rejects on load.
        var written = session.Serialize().ToArray();
        var rewritten = Enumerable.Range(0, 32).Count(s => written[s * 0x1000 + 0xFF6] != broken[s * 0x1000 + 0xFF6]
                                                          || written[s * 0x1000 + 0xFF7] != broken[s * 0x1000 + 0xFF7]);
        Assert.True(rewritten >= 5, $"expected the vanilla write to rewrite CFRU checksums, got {rewritten}");

        // The guard: flagged, and the writer refuses until the user confirms.
        Assert.Equal(LayoutRiskKind.CorruptingLayout, Engine.AssessLayoutRisk(broken)?.Kind);
        var (writer, access) = Writer();
        var refusal = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAsync("doc", Snap(broken), written).AsTask());
        Assert.False(refusal.RequiresConfirmation); // a provably corrupting write has no own-risk bypass
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task SaveWhoseChecksumsFollowANonVanillaWindowIsFlaggedAndRefused()
    {
        // Portable model of the CFRU mechanism: data past the vanilla 0xF80 window,
        // covered by a checksum over the CFRU 0xFF0 window.
        var hack = SyntheticVanillaGen3();
        // Outside every vanilla window but inside section 1's CFRU window, unchecksummed:
        // keeps the file from passing the structural Radical Red test (all 14 CFRU windows).
        hack[1 * 0x1000 + 0xF90] = 0xA5;
        var pcSector = 5 * 0x1000;
        hack[pcSector + 0xF90] = 0x5A;
        uint sum = 0;
        for (var i = 0; i < 0xFF0; i += 4) sum += BinaryPrimitives.ReadUInt32LittleEndian(hack.AsSpan(pcSector + i));
        BinaryPrimitives.WriteUInt16LittleEndian(hack.AsSpan(pcSector + 0xFF6), (ushort)(sum + (sum >> 16)));

        Assert.Equal(LayoutRiskKind.CorruptingLayout, Engine.AssessLayoutRisk(hack)?.Kind);
        using var session = Engine.OpenSession(hack);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "EDITED"));
        var candidate = session.Serialize();

        var (writer, access) = Writer();
        var refusal = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync("doc", Snap(hack), candidate, WriteScope.Only(new SlotRef(0, 0))).AsTask());
        Assert.False(refusal.RequiresConfirmation);
        Assert.Equal(0, access.Writes);

        // Not even an explicit "at my own risk" lifts it: the vanilla write would break the file.
        writer.ConfirmLayoutRisk("doc");
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync("doc", Snap(hack), candidate, WriteScope.Only(new SlotRef(0, 0))).AsTask());
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task OtherCfruHackChoicesAreRefused()
    {
        var bytes = SyntheticVanillaGen3();
        using var session = Engine.OpenSession(bytes);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "EDITED"));
        var candidate = session.Serialize();
        var identities = new FakeIdentities();
        var access = new MemoryAccess();
        var writer = new SafeSaveWriter(Engine, new NullBackups(), access, identities);
        var scope = WriteScope.Only(new SlotRef(0, 0));

        identities.Choice = "hack-cfru";
        var readOnly = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteScopedAsync("doc", Snap(bytes), candidate, scope).AsTask());
        Assert.False(readOnly.RequiresConfirmation);
        Assert.Equal(0, access.Writes);

        identities.Choice = "emerald";
        await writer.WriteScopedAsync("doc", Snap(bytes), candidate, scope);
        Assert.Equal(1, access.Writes);
    }

    [Fact]
    public void MovesAskWhetherTheSourceAcceptsWritesBeforeTheDestinationIsWritten()
    {
        var bytes = SyntheticVanillaGen3();
        var identities = new FakeIdentities();
        var writer = new SafeSaveWriter(Engine, new NullBackups(), new MemoryAccess(), identities);

        identities.Choice = "emerald";
        Assert.Null(writer.WhyWritesAreRefused("doc", Snap(bytes)));
        identities.Choice = "hack-cfru";
        Assert.NotNull(writer.WhyWritesAreRefused("doc", Snap(bytes)));
    }

    [Fact]
    public async Task TheLayoutVerdictCarriesOverToTheWrittenBytes()
    {
        var bytes = SyntheticVanillaGen3();
        using var session = Engine.OpenSession(bytes);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "FIRST"));
        var first = session.Serialize().ToArray();
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "SECOND"));
        var second = session.Serialize();
        var (writer, access) = Writer();
        var scope = WriteScope.Only(new SlotRef(0, 0));

        await writer.WriteScopedAsync("doc", Snap(bytes), first, scope);
        await writer.WriteScopedAsync("doc", Snap(first), second, scope);
        Assert.Equal(2, access.Writes);
        Assert.Null(writer.WhyWritesAreRefused("doc", Snap(second.ToArray())));
    }

    // ── Suspected ROM hacks: vanilla layout, foreign species ids ──

    /// <summary>A vanilla Emerald save (PKHeX-finalized) with one PC mon whose RAW species
    /// id is out of the Gen 3 range but whose checksum is valid: the pokeemerald-expansion
    /// shape (Emerald Enhanced stores 875 for an Alolan Vulpix).</summary>
    private static byte[] SyntheticEmerald(ushort? rawSpecies)
    {
        // The portable Gen 3 image with Emerald's security key at section 0 +0xAC...
        var data = new byte[0x20000];
        for (var sector = 0; sector < 14; sector++)
        {
            var off = sector * 0x1000;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF4), (ushort)sector);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFF8), 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFFC), 1);
        }
        // ...plus SaveBlock2 data past RS's 0x890 bytes, which is how PKHeX tells it from RS.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xAC), 0x0FCA0FCC);
        data[0x900] = 1;
        uint sum = 0;
        for (var i = 0; i < 0xF80; i += 4) sum += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0xFF6), (ushort)(sum + (sum >> 16)));
        data.AsSpan(0xE000).Fill(0xFF);
        Assert.True(SaveUtil.TryGetSaveFile(data, out var parsed) && parsed is SAV3E, "synthetic Emerald save did not parse");
        var save = (SAV3E)parsed!;

        // Slot 0 carries the species under test, slot 1 an ordinary (editable) Pikachu.
        foreach (var (slot, raw) in new[] { (0, rawSpecies), (1, null) })
        {
            var pk = new PK3 { Species = 25, PID = 0x1234_5678u + (uint)slot, TID16 = 1, EXP = 1000, Language = 2 };
            pk.OriginalTrainerName = "PKF";
            pk.Nickname = "MON";
            if (raw is { } id) pk.SpeciesInternal = id;
            pk.RefreshChecksum();
            Assert.True(pk.ChecksumValid);
            save.SetBoxSlotAtIndex(pk, 0, slot, EntityImportSettings.None);
        }
        return save.Write().ToArray();
    }

    [Fact]
    public void VanillaEmeraldIsNotFlagged()
    {
        var bytes = SyntheticEmerald(rawSpecies: null);
        Assert.True(SaveUtil.TryGetSaveFile(bytes.ToArray(), out var save) && save is SAV3E);
        Assert.Null(Engine.AssessLayoutRisk(bytes));
    }

    [Theory]
    [InlineData((ushort)875)]     // Emerald Enhanced: plain expansion id
    [InlineData((ushort)26878)]   // Emerald Ex: 0x7FF-masked Sceptile with high bits set
    [InlineData((ushort)260)]     // retail filler id 252-276
    public void ValidMonWithAForeignSpeciesIdIsASuspectedHack(ushort raw)
    {
        var bytes = SyntheticEmerald(raw);
        var risk = Engine.AssessLayoutRisk(bytes);
        Assert.Equal(LayoutRiskKind.SuspectedHack, risk?.Kind);
        Assert.True(risk!.UserMayProceed);
    }

    [Fact]
    public void VanillaFireRedSampleIsNotFlagged()
    {
        if (Local("firered-vanilla.sav") is not { } bytes) return;
        Assert.Null(Engine.AssessLayoutRisk(bytes));
    }

    [Theory]
    [InlineData("Pokemon - Black Pearl Emerald.sav")]
    [InlineData("Pokemon - Emerald Enhanced.sav")]
    [InlineData("Pokemon - Emerald Ex.sav")]
    public void ExpansionHacksOnTheEmeraldLayoutAreSuspectedHacks(string file)
    {
        if (Local(Path.Combine("romhacks", file)) is not { } bytes) return;
        Assert.Equal(LayoutRiskKind.SuspectedHack, Engine.AssessLayoutRisk(bytes)?.Kind);
    }

    [Fact]
    public async Task SuspectedHackIsReadOnlyUntilAcceptedAndTheAcceptancePersists()
    {
        var bytes = SyntheticEmerald(875);
        using var session = Engine.OpenSession(bytes);
        session.ApplyEdit(0, 1, new EntityEdit(Nickname: "EDITED"));
        var candidate = session.Serialize();
        var path = Path.Combine(Path.GetTempPath(), "pkforge-hackrisk-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var identities = new JsonSaveIdentityStore(path);
            var access = new MemoryAccess();
            var writer = new SafeSaveWriter(Engine, new NullBackups(), access, identities);

            var refusal = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteAsync("doc", Snap(bytes), candidate).AsTask());
            Assert.True(refusal.RequiresConfirmation);
            Assert.Contains("Edit at my own risk", refusal.Message); // the refusal says how to lift it
            Assert.Equal(0, access.Writes);

            writer.ConfirmLayoutRisk("doc");
            Assert.True(identities.Get("doc")?.AcceptedHackRisk);

            // A fresh writer over the reloaded store (an app restart) honours the choice.
            var restarted = new SafeSaveWriter(Engine, new NullBackups(), access, new JsonSaveIdentityStore(path));
            Assert.Null(restarted.WhyWritesAreRefused("doc", Snap(bytes)));
            await restarted.WriteAsync("doc", Snap(bytes), candidate);
            Assert.Equal(1, access.Writes);

            // Revoking makes it read-only again, for good.
            restarted.RevokeLayoutRisk("doc");
            Assert.NotNull(new SafeSaveWriter(Engine, new NullBackups(), access, new JsonSaveIdentityStore(path))
                .WhyWritesAreRefused("doc", Snap(bytes)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpansionMonsAreNotMistakenForEmptySlots()
    {
        // PKHeX maps the foreign id to species 0; the diff must still see a Pokémon there,
        // or a write that overwrites it would pass as touching an "empty" slot.
        var bytes = SyntheticEmerald(875);
        Assert.True(SaveUtil.TryGetSaveFile(bytes.ToArray(), out var save));
        save!.SetBoxSlotAtIndex(new PK3(), 0, 0, EntityImportSettings.None);
        var cleared = save.Write().ToArray();
        Assert.NotNull(Engine.CheckWriteSafety(bytes, cleared, WriteScope.Only(new SlotRef(0, 1))));
        Assert.Null(Engine.CheckWriteSafety(bytes, cleared, WriteScope.Only(new SlotRef(0, 0))));
    }

    // ── Structural diff ──

    [Fact]
    public async Task ScopedWriteIsRefusedWhenAnUntargetedSlotChanges()
    {
        var bytes = SyntheticVanillaGen3();
        using var session = Engine.OpenSession(bytes);
        session.ApplyEdit(0, 1, new EntityEdit(Nickname: "SNEAKY"));
        var candidate = session.Serialize();

        Assert.NotNull(Engine.CheckWriteSafety(bytes, candidate, WriteScope.Only(new SlotRef(0, 0))));
        Assert.Null(Engine.CheckWriteSafety(bytes, candidate, WriteScope.Only(new SlotRef(0, 1))));
        Assert.Null(Engine.CheckWriteSafety(bytes, candidate, null));

        var (writer, access) = Writer();
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync("doc", Snap(bytes), candidate, WriteScope.Only(new SlotRef(0, 0))).AsTask());
        Assert.Equal(0, access.Writes);
        await writer.WriteScopedAsync("doc", Snap(bytes), candidate, WriteScope.Only(new SlotRef(0, 1)));
        Assert.Equal(1, access.Writes);
    }

    [Fact]
    public async Task WriteThatCorruptsAReadablePokemonIsRefusedEvenUnscoped()
    {
        var bytes = SyntheticVanillaGen3();
        // Scramble box 1 slot 2's stored ciphertext in place (RS PC = section 5, u32
        // current box then 80-byte mons) without fixing its checksum.
        var candidate = bytes.ToArray();
        candidate[5 * 0x1000 + 4 + 80 + 0x30] ^= 0xFF;
        Assert.True(SaveUtil.TryGetSaveFile(candidate.ToArray(), out var check) && !check!.GetBoxSlotAtIndex(0, 1).ChecksumValid);

        Assert.NotNull(Engine.CheckWriteSafety(bytes, candidate, null));
        var (writer, access) = Writer();
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAsync("doc", Snap(bytes), candidate).AsTask());
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task RestoreIsNeverBlockedBecauseItIsTheRecoveryPath()
    {
        if (Local("radicalred-champ.sav") is not { } rr) return;
        var broken = rr.ToArray();
        broken[14 * 0x1000 + 0xFF6] ^= 0x01; // ambiguous current state
        var (writer, access) = Writer();
        await writer.WriteScopedAsync("doc", Snap(broken), rr, WriteScope.Everything);
        Assert.Equal(1, access.Writes);
    }

    [Fact]
    public void RouteChangeIsRefused()
    {
        if (Local("radicalred-champ.sav") is not { } rr) return;
        var broken = rr.ToArray();
        broken[14 * 0x1000 + 0xFF6] ^= 0x01;
        Assert.Contains("Radical Red", Engine.CheckWriteSafety(rr, broken, null));
    }

    // ── Hack sessions: no-op edits must not rewrite anything ──

    [Theory]
    [InlineData("unbound-v2111.srm")]
    [InlineData("radicalred-champ.sav")]
    public void UnchangedLevelInAnEditDoesNotRewriteExperience(string file)
    {
        if (Local(file) is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, file);
        var slot = FirstBoxMon(session);
        var detail = session.ReadEntity(slot.Box, slot.Slot);
        session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Level: detail.Level));
        var party = session.ReadEntity(-1, 0);
        session.ApplyEdit(-1, 0, new EntityEdit(Level: party.Level));
        Assert.True(session.Serialize().Span.SequenceEqual(bytes), "re-saving the same level rewrote the save");
    }

    /// <summary>The editor echoes the ability it showed; for a species whose two slots hold
    /// the same ability that echo used to reroll the PID on a save with no changes.</summary>
    [Theory]
    [InlineData("unbound-v2111.srm")]
    [InlineData("radicalred-champ.sav")]
    public void EchoedAbilityIsANoOp(string file)
    {
        if (Local(file) is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, file);
        foreach (var slot in session.Snapshot.Slots.Where(s => s.Species is not null))
        {
            var detail = session.ReadEntity(slot.Box, slot.Slot);
            session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Ability: detail.Ability));
        }
        Assert.True(session.Serialize().Span.SequenceEqual(bytes), "re-saving the shown ability rewrote the save");
    }

    [Theory]
    [InlineData("unbound-v2111.srm")]
    [InlineData("radicalred-champ.sav")]
    public void HackSessionSingleSlotEditPassesTheStructuralDiff(string file)
    {
        if (Local(file) is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, file);
        var slot = FirstBoxMon(session);
        session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Nickname: "Probe"));
        var candidate = session.Serialize();
        Assert.Null(Engine.CheckWriteSafety(bytes, candidate, WriteScope.Only(new SlotRef(slot.Box, slot.Slot))));
        var other = slot.Box == -1 ? new SlotSummary(0, 0, null, null, false, true)
            : session.Snapshot.Slots.First(s => s.Box >= 0 && (s.Box, s.Slot) != (slot.Box, slot.Slot));
        Assert.NotNull(Engine.CheckWriteSafety(bytes, candidate, WriteScope.Only(new SlotRef(other.Box, other.Slot))));
    }

    private static SlotRef FirstBoxMon(ISaveEngineSession session)
    {
        foreach (var s in session.Snapshot.Slots.Where(s => s.Box >= 0))
            if (!session.ReadEntity(s.Box, s.Slot).IsEmpty)
                return new SlotRef(s.Box, s.Slot);
        return new SlotRef(-1, 0); // the Unbound sample keeps its PC empty: fall back to the party lead
    }

    private sealed class FakeIdentities : ISaveIdentityStore
    {
        public string? Choice { get; set; }
        public SaveIdentity? Get(string documentId) => new(documentId, GameChoiceId: Choice);
        public void Set(SaveIdentity identity) { }
        public void Reset(string documentId) { }
        public event Action<string>? Changed { add { } remove { } }
    }

    private sealed class NullBackups : IBackupService
    {
        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new BackupReceipt("backup-0000000000", DateTimeOffset.UtcNow, "sha"));
        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemoryAccess : ISaveFileAccess
    {
        public int Writes { get; private set; }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }
}
