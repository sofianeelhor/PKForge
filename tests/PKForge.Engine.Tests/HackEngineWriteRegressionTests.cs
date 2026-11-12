using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.Expansion;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.RechargedYellow;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Write regressions from the 3.2 engine review and its audit of the other hack engines,
/// each on a real gitignored save (skipped when absent). Every write is checked the same
/// way: the untouched round trip stays byte-identical, the edit survives a reopen, every
/// live sector checksum holds over the game's window, and the backup copy is untouched.
/// </summary>
public sealed class HackEngineWriteRegressionTests
{
    private static byte[]? Sample(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", relative);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private const string RechargedYellowFile = "romhacks/recharged-yellow.sav";
    private const string TeamRocketFile = "romhacks/batch-2026-11-12/mgba/mGBA Romhack Saves/Pokemon - Team Rocket Edition.srm";
    private const string PerfectEmeraldFile = "romhacks/perfect-emerald.sav";
    private const string RadicalRedFile = "thor/retroarch/mGBA/Radical-red-champ.sav";
    private const string UnboundFile = "thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm";

    // ── Recharged Yellow #12: the bag editor sees the list the bag view shows ──

    [Fact]
    public void RechargedYellowMedicineEditsNeverTouchTheHiddenEntry58()
    {
        if (Sample(RechargedYellowFile) is not { } bytes) return;
        var payload = SramPadding.Trim(bytes);
        Assert.True(RechargedYellowFormat.TryGetLayout(payload, out _, out var sections, out var profile));
        var medicine = RechargedYellowFormat.Pockets.Single(pocket => pocket.Name == "Medicine");
        var hidden = medicine.Offset + 58 * 4;
        var sb1 = RechargedYellowFormat.ReadBlock(payload, sections, 1, profile.Sb1Chunks);
        var entry58 = sb1.AsSpan(hidden, 4).ToArray();
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(entry58)); // item 13 x6 behind an empty list

        byte[] added;
        using (var session = new RechargedYellowEngineSession(bytes))
        {
            Assert.Equal(bytes, session.Serialize().ToArray());
            Assert.Empty(session.GetBag().Single(pouch => pouch.Name == "Medicine").Items);
            // Before the fix this wrote into the hidden entry 58 and the view still showed nothing.
            Assert.Equal(1, session.SetItemCount("Medicine", 13, 1));
            Assert.Equal([new BagItem(13, 1)], session.GetBag().Single(pouch => pouch.Name == "Medicine").Items);
            added = session.Serialize().ToArray();
        }
        AssertRechargedYellowWrite(bytes, added, entry58, hidden);
        using (var reopened = new RechargedYellowEngineSession(added))
            Assert.Equal([new BagItem(13, 1)], reopened.GetBag().Single(pouch => pouch.Name == "Medicine").Items);

        // Removing it clears only the list's own entry: before the fix this cleared
        // entry 58 and compacted the whole array.
        byte[] removed;
        using (var session = new RechargedYellowEngineSession(added))
        {
            Assert.Equal(0, session.SetItemCount("Medicine", 13, 0));
            removed = session.Serialize().ToArray();
        }
        AssertRechargedYellowWrite(bytes, removed, entry58, hidden);
        using (var reopened = new RechargedYellowEngineSession(removed))
            Assert.Empty(reopened.GetBag().Single(pouch => pouch.Name == "Medicine").Items);
    }

    private static void AssertRechargedYellowWrite(byte[] before, byte[] after, byte[] entry58, int hidden)
    {
        var payload = SramPadding.Trim(after);
        Assert.True(RechargedYellowFormat.LooksLike(payload)); // every live section checksum holds
        Assert.True(RechargedYellowFormat.TryGetLayout(payload, out _, out var sections, out var profile));
        Assert.True(RechargedYellowFormat.TryGetLayout(SramPadding.Trim(before), out _, out var liveBefore, out _));
        Assert.Equal(liveBefore, sections);
        var sb1 = RechargedYellowFormat.ReadBlock(payload, sections, 1, profile.Sb1Chunks);
        Assert.Equal(entry58, sb1.AsSpan(hidden, 4).ToArray());
        var live = sections.ToHashSet();
        for (var offset = 0; offset < RechargedYellowFormat.FileSize; offset += 0x1000)
            if (!live.Contains(offset))
                Assert.True(before.AsSpan(offset, 0x1000).SequenceEqual(after.AsSpan(offset, 0x1000)), $"backup sector 0x{offset:X} changed");
    }

    // ── Team Rocket Edition #17, and the Expansion engine's empty source ──

    private static void AssertExpansionBackupUntouched(byte[] before, byte[] after)
    {
        var live = ExpansionFormat.SectionOffsets(before).ToHashSet();
        for (var offset = 0; offset < ExpansionFormat.FileSize; offset += ExpansionFormat.SectorSize)
            if (!live.Contains(offset))
                Assert.True(before.AsSpan(offset, 0x1000).SequenceEqual(after.AsSpan(offset, 0x1000)), $"sector 0x{offset:X} changed");
    }

    [Fact]
    public void TeamRocketPartyPokemonDroppedOnAnOccupiedBoxSlotIsRefused()
    {
        if (Sample(TeamRocketFile) is not { } bytes) return;
        byte[] written;
        using (var session = new ExpansionEngineSession(bytes, "test"))
        {
            Assert.Equal(bytes, session.Serialize().ToArray());
            Assert.Equal(139, session.ReadEntity(0, 0).Species); // Omastar
            // Before the fix Omastar entered the party with stats rebuilt from base stats
            // this hack changed: the exact corruption the withdraw guard exists to prevent.
            Assert.Throws<NotSupportedException>(() => session.MoveSlot(-1, 1, 0, 0));
            Assert.Equal(bytes, session.Serialize().ToArray());
            // Dropping onto an empty box slot still works: no stats are computed.
            session.MoveSlot(-1, 5, 1, 0);
            written = session.Serialize().ToArray();
        }
        AssertExpansionBackupUntouched(bytes, written);
        Assert.Equal(ExpansionGame.TeamRocketEdition, ExpansionFormat.LooksLike(written)?.Game); // checksums hold
        using var reopened = new ExpansionEngineSession(written, "test");
        Assert.Equal(137, reopened.ReadEntity(1, 0).Species);
        Assert.Equal(229, reopened.ReadEntity(-1, 1).Species); // Houndoom stayed in the party
        Assert.Equal(139, reopened.ReadEntity(0, 0).Species);  // Omastar stayed in its box
    }

    [Fact]
    public void ExpansionEmptyBoxSlotCannotJoinTheParty()
    {
        if (Sample(PerfectEmeraldFile) is not { } bytes) return;
        using var session = new ExpansionEngineSession(bytes, "test");
        var empty = Enumerable.Range(0, session.BoxSlotCount).Select(slot => (Box: 0, Slot: slot))
            .First(where => session.ReadEntity(where.Box, where.Slot).IsEmpty);
        var party = session.Snapshot.Slots.Count(slot => slot.Box == -1 && slot.Species is not null);
        if (party >= 6) session.ReleaseSlot(-1, 5);
        var before = session.Serialize().ToArray();
        // Before the fix this appended an empty record and raised the party count.
        Assert.Throws<InvalidOperationException>(() => session.MoveSlot(empty.Box, empty.Slot, -1, 0));
        Assert.Equal(before, session.Serialize().ToArray());
    }

    // ── CFRU (Radical Red, GS Chronicles) ──

    private static void AssertCfruWrite(byte[] before, byte[] after)
    {
        var sections = RadicalRedFormat.SectionOffsets(after);
        Assert.Equal(RadicalRedFormat.SectionOffsets(before), sections);
        for (var id = 0; id < RadicalRedFormat.SectionCount; id++)
            Assert.Equal(
                RadicalRedFormat.Checksum(after.AsSpan(sections[id]), RadicalRedFormat.CfruWindows[id]),
                BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(sections[id] + 0xFF6)));
        // The backup copies of the PC stream are never written (the party and bag
        // sections are mirrored into every copy by design).
        for (var id = 2; id <= 12; id++)
            foreach (var copy in RadicalRedFormat.AllSectionOffsets(before, id).Where(copy => copy != sections[id]))
                Assert.True(before.AsSpan(copy, 0x1000).SequenceEqual(after.AsSpan(copy, 0x1000)), $"backup of section {id} changed");
    }

    private static (int Box, int Slot) FirstOccupied(ISaveEngineSession session) =>
        Enumerable.Range(0, 19).SelectMany(box => Enumerable.Range(0, 30).Select(slot => (box, slot)))
            .First(where => !session.ReadEntity(where.box, where.slot).IsEmpty);

    private static (int Box, int Slot) FirstEmpty(ISaveEngineSession session, int box = 0) =>
        Enumerable.Range(0, 30).Select(slot => (box, slot)).First(where => session.ReadEntity(where.box, where.slot).IsEmpty);

    private static int PartyCount(ISaveEngineSession session) =>
        Enumerable.Range(0, 6).Count(slot => !session.ReadEntity(-1, slot).IsEmpty);

    [Fact]
    public void RadicalRedBoxPokemonMovedIntoThePartyLeavesTheBoxInTheFile()
    {
        if (Sample(RadicalRedFile) is not { } bytes) return;
        byte[] written;
        (int Box, int Slot) from;
        int species;
        using (var session = new RadicalRedEngineSession(bytes))
        {
            Assert.Equal(bytes, session.Serialize().ToArray());
            if (PartyCount(session) == 6) session.ReleaseSlot(-1, 5);
            from = FirstOccupied(session);
            species = session.ReadEntity(from.Box, from.Slot).Species;
            session.MoveSlot(from.Box, from.Slot, -1, 5);
            written = session.Serialize().ToArray();
        }
        AssertCfruWrite(bytes, written);
        // Before the fix only the in-memory box was cleared: the file kept the Pokémon in
        // its box and in the party.
        using var reopened = new RadicalRedEngineSession(written);
        Assert.True(reopened.ReadEntity(from.Box, from.Slot).IsEmpty);
        Assert.Equal(species, reopened.ReadEntity(-1, 5).Species);
        Assert.True(reopened.ReadEntity(-1, 5).Stats![0] > 0);
    }

    [Fact]
    public void RadicalRedPartyCountStaysInBounds()
    {
        if (Sample(RadicalRedFile) is not { } bytes) return;
        using var session = new RadicalRedEngineSession(bytes);
        Assert.Equal(6, PartyCount(session));
        var money = session.GetTrainer().Money;

        // A full party refuses a generated Pokémon: before the fix its record landed on
        // money, coins and what follows, and the count became 7.
        Assert.False(session.GenerateFromShowdownText(-1, 0, "Pikachu").Success);
        // An empty box slot cannot join the party.
        var empty = FirstEmpty(session, 1);
        session.ReleaseSlot(-1, 5);
        var five = session.Serialize().ToArray();
        Assert.Throws<InvalidOperationException>(() => session.MoveSlot(empty.Box, empty.Slot, -1, 0));
        // Releasing an empty slot changes nothing: before the fix it removed member 5.
        session.ReleaseSlot(-1, 5);
        Assert.Equal(five, session.Serialize().ToArray());
        AssertCfruWrite(bytes, five);

        using var reopened = new RadicalRedEngineSession(five);
        Assert.Equal(5, PartyCount(reopened));
        Assert.Equal(money, reopened.GetTrainer().Money);
        Assert.Equal(bytes.Length, five.Length);

        // An emptied party stays at count 0 (before the fix: 0xFFFFFFFF, and the 100 bytes
        // before the party, the previous sector's footer included, were cleared).
        for (var i = 0; i < 5; i++) reopened.ReleaseSlot(-1, 0);
        var zero = reopened.Serialize().ToArray();
        reopened.ReleaseSlot(-1, 0);
        Assert.Equal(zero, reopened.Serialize().ToArray());
        AssertCfruWrite(bytes, zero);
        Assert.True(RadicalRedFormat.IsRadicalRed(zero));
    }

    [Fact]
    public void RadicalRedPokemonImportedIntoThePartyHasItsStats()
    {
        if (Sample(RadicalRedFile) is not { } bytes) return;
        var pk = new PK3 { Species = (ushort)Species.Pikachu, Language = (int)LanguageID.English };
        pk.Nickname = "PIKACHU";
        pk.OriginalTrainerName = "Sof";
        pk.TID16 = 12345;
        pk.SID16 = 54321;
        pk.PID = 0x0BAD_F00D;
        pk.EXP = Experience.GetEXP(20, pk.PersonalInfo.EXPGrowth);
        pk.Ball = (byte)Ball.Poke;
        pk.SetMoves([(ushort)Move.ThunderShock, 0, 0, 0]);
        pk.RefreshChecksum();

        byte[] written;
        using (var session = new RadicalRedEngineSession(bytes))
        {
            session.ReleaseSlot(-1, 5);
            Assert.True(session.ImportSlot(-1, 0, pk.Data.ToArray(), "PK3"));
            written = session.Serialize().ToArray();
        }
        AssertCfruWrite(bytes, written);
        using var reopened = new RadicalRedEngineSession(written);
        var landed = reopened.ReadEntity(-1, 5);
        Assert.Equal(25, landed.Species);
        Assert.Equal(20, landed.Level);
        // Before the fix: 0 HP and every stat 0.
        Assert.All(landed.Stats!, stat => Assert.True(stat > 0));
        Assert.Equal(landed.Stats![0], landed.CurrentHp);
        Assert.True(reopened.GetMoveDetails(-1, 5).Moves[0].PP > 0);
    }

    [Fact]
    public void RadicalRedLevelOneOccupantSwapsInsteadOfBeingOverwritten()
    {
        if (Sample(RadicalRedFile) is not { } bytes) return;
        byte[] written;
        (int Box, int Slot) boxed;
        int boxedSpecies, partySpecies;
        using (var session = new RadicalRedEngineSession(bytes))
        {
            boxed = FirstOccupied(session);
            session.ApplyEdit(boxed.Box, boxed.Slot, new EntityEdit(Level: 1)); // stores EXP 0
            boxedSpecies = session.ReadEntity(boxed.Box, boxed.Slot).Species;
            partySpecies = session.ReadEntity(-1, 0).Species;
            // Before the fix an EXP-0 occupant counted as empty: it was overwritten and lost.
            session.MoveSlot(-1, 0, boxed.Box, boxed.Slot);
            written = session.Serialize().ToArray();
        }
        AssertCfruWrite(bytes, written);
        using var reopened = new RadicalRedEngineSession(written);
        Assert.Equal(partySpecies, reopened.ReadEntity(boxed.Box, boxed.Slot).Species);
        Assert.Equal(boxedSpecies, reopened.ReadEntity(-1, 0).Species);
        Assert.Equal(1, reopened.ReadEntity(-1, 0).Level);
        Assert.Equal(6, PartyCount(reopened));
    }

    [Fact]
    public void RadicalRedBagEditRefusesWhenAnUnreadableEntryWouldBeErased()
    {
        if (Sample(RadicalRedFile) is not { } bytes) return;
        var image = bytes.ToArray();
        var sections = RadicalRedFormat.SectionOffsets(image);
        var key = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sections[0] + 0xF20));
        int count;
        using (var session = new RadicalRedEngineSession(image))
            count = session.GetBag().Single(pouch => pouch.Name == "Balls").Items.Count;
        Assert.True(count > 0 && count + 2 <= 50);
        // The Balls pocket sits in the raw sector-30/31 region (image 0x834, past the 0x518
        // bytes section 13 holds): an unknown id at the end of the list, a real ball behind it.
        var entry = RadicalRedFormat.RawFileOffset(0x834 - RadicalRedFormat.BagImageInSector + count * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry), 0x7FFF);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry + 2), (ushort)(1 ^ key));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry + 4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry + 6), (ushort)(5 ^ key));

        using var edit = new RadicalRedEngineSession(image);
        Assert.Equal(image, edit.Serialize().ToArray());
        var first = edit.GetBag().Single(pouch => pouch.Name == "Balls").Items[0];
        Assert.Equal(count, edit.GetBag().Single(pouch => pouch.Name == "Balls").Items.Count);
        // Before the fix the pocket was rebuilt from the short list and the ball behind cleared.
        Assert.Throws<InvalidOperationException>(() => edit.SetItemCount("Balls", first.Id, first.Count + 1));
        Assert.Equal(image, edit.Serialize().ToArray());
    }

    // ── Unbound ──

    private static void AssertUnboundWrite(byte[] before, byte[] after)
    {
        var sections = UnboundFormat.SectionOffsets(after);
        Assert.Equal(UnboundFormat.SectionOffsets(before), sections);
        Assert.True(SaveParser.IsPokemonUnbound(after));
        int[] windows = [0xF24, 0xFF0, 0xFF0, 0xFF0, 0xD98, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0x450];
        for (var id = 0; id < UnboundFormat.SectionCount; id++)
        {
            Assert.Equal(
                UnboundFormat.Checksum(after.AsSpan(sections[id]), windows[id]),
                BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(sections[id] + 0xFF6)));
            // Footer (id, signature, save index) and the padding before it are never written.
            Assert.True(before.AsSpan(sections[id] + 0xFF0, 4).SequenceEqual(after.AsSpan(sections[id] + 0xFF0, 4)));
            Assert.True(before.AsSpan(sections[id] + 0xFF8, 8).SequenceEqual(after.AsSpan(sections[id] + 0xFF8, 8)));
            Assert.Equal(id, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(sections[id] + 0xFF4)));
        }
        for (var id = 2; id <= 12; id++)
            foreach (var copy in UnboundFormat.AllSectionOffsets(before, id).Where(copy => copy != sections[id]))
                Assert.True(before.AsSpan(copy, 0x1000).SequenceEqual(after.AsSpan(copy, 0x1000)), $"backup of section {id} changed");
    }

    [Fact]
    public void UnboundSaveBlockBoxesAndStreamBoundariesKeepTheirSectors()
    {
        if (Sample(UnboundFile) is not { } bytes) return;
        // Box 23 slot 4 straddles sections 2 and 3; box 3 slot 11 and box 5 slot 21 straddle
        // PC stream sections (1-based names; 0-based here).
        (int Box, int Slot)[] targets = [(22, 3), (22, 4), (23, 0), (2, 10), (4, 20)];
        byte[] written;
        int species;
        using (var session = new UnboundEngineSession(bytes))
        {
            Assert.Equal(bytes, session.Serialize().ToArray());
            species = session.ReadEntity(-1, 0).Species;
            foreach (var (box, slot) in targets)
            {
                Assert.True(session.ReadEntity(box, slot).IsEmpty);
                // Before the fix (22, 3) wrote over section 2's footer, boxes 22-23 were never
                // checksummed, and (2, 10)/(4, 20) wrote the padding before a footer.
                Assert.True(session.DuplicateSlot(-1, 0, box, slot));
            }
            written = session.Serialize().ToArray();
        }
        AssertUnboundWrite(bytes, written);
        using var reopened = new UnboundEngineSession(written);
        foreach (var (box, slot) in targets)
            Assert.Equal(species, reopened.ReadEntity(box, slot).Species);
        Assert.True(reopened.ReadEntity(22, 5).IsEmpty);
    }

    [Fact]
    public void UnboundBerryEditsKeepTheStateAfterThePocket()
    {
        if (Sample(UnboundFile) is not { } bytes) return;
        var data = SramPadding.Trim(bytes);
        var after75 = data.AsSpan(0x1E710, 0x64).ToArray();
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E712)));
        byte[] written;
        using (var session = new UnboundEngineSession(bytes))
        {
            var berry = session.GetPouchLegalItems("Berries")[0];
            Assert.True(session.SetItemCount("Berries", berry, 3) > 0);
            written = session.Serialize().ToArray();
        }
        AssertUnboundWrite(bytes, written);
        // Before the fix the pocket ran to 100 slots and every berry edit zeroed these bytes.
        Assert.Equal(after75, SramPadding.Trim(written).AsSpan(0x1E710, 0x64).ToArray());
        using var reopened = new UnboundEngineSession(written);
        Assert.Single(reopened.GetBag().Single(pouch => pouch.Name == "Berries").Items);
    }

    [Fact]
    public void UnboundPartyMovesReleasesAndImportsStayConsistent()
    {
        if (Sample(UnboundFile) is not { } bytes) return;
        byte[] written;
        int count;
        int movedSpecies;
        (int Box, int Slot) from;
        using (var session = new UnboundEngineSession(bytes))
        {
            count = PartyCount(session);
            Assert.InRange(count, 1, 4);
            // Releasing an empty slot changes nothing (before: the last member was removed).
            session.ReleaseSlot(-1, 5);
            Assert.Equal(bytes, session.Serialize().ToArray());
            // An empty box slot cannot join the party.
            var empty = FirstEmpty(session, 1);
            Assert.Throws<InvalidOperationException>(() => session.MoveSlot(empty.Box, empty.Slot, -1, 0));
            Assert.Equal(bytes, session.Serialize().ToArray());

            // A boxed Pokémon joins the party and leaves its box in the file too.
            Assert.True(session.DuplicateSlot(-1, 0, 0, 29));
            from = (0, 29);
            movedSpecies = session.ReadEntity(0, 29).Species;
            session.MoveSlot(0, 29, -1, 5);

            var pk = new PK3 { Species = (ushort)Species.Pikachu, Language = (int)LanguageID.English };
            pk.Nickname = "PIKACHU";
            pk.OriginalTrainerName = "Sof";
            pk.PID = 0x0BAD_F00D;
            pk.EXP = Experience.GetEXP(20, pk.PersonalInfo.EXPGrowth);
            pk.Ball = (byte)Ball.Poke;
            pk.SetMoves([(ushort)Move.ThunderShock, 0, 0, 0]);
            pk.RefreshChecksum();
            Assert.True(session.ImportSlot(-1, 0, pk.Data.ToArray(), "PK3"));
            written = session.Serialize().ToArray();
        }
        AssertUnboundWrite(bytes, written);
        using var reopened = new UnboundEngineSession(written);
        Assert.Equal(count + 2, PartyCount(reopened));
        Assert.True(reopened.ReadEntity(from.Box, from.Slot).IsEmpty);
        Assert.Equal(movedSpecies, reopened.ReadEntity(-1, count).Species);
        var pikachu = reopened.ReadEntity(-1, count + 1);
        Assert.Equal((25, 20), (pikachu.Species, pikachu.Level));
        // Before the fix a foreign import landed with 0 HP and every stat 0.
        Assert.All(pikachu.Stats!, stat => Assert.True(stat > 0));
        Assert.Equal(pikachu.Stats![0], pikachu.CurrentHp);
    }
}
