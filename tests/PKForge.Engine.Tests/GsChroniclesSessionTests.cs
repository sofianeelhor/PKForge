using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.GsChronicles;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon GS Chronicles (CFRU, sector signature 0x66290096): detection, the 25-box
/// layout including the save-block boxes 23-25, the generated tables and their national
/// bridges. Synthetic saves always run; the real device save (gitignored) adds ground
/// truth when present.
/// </summary>
public sealed class GsChroniclesSessionTests
{
    private static readonly SaveEngine Engine = new();

    private static string? GroundTruth()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null
            : Path.Combine(directory.FullName, ".local-testdata", "romhacks", "Pokemon - GS Chronicles.sav");
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>A CFRU save as GS Chronicles writes it: both rotating slots, every
    /// footer stamped with <paramref name="signature"/>, checksums over the CFRU windows,
    /// and one plaintext party Mesprit (species 534) in section 1.</summary>
    internal static byte[] SyntheticSave(uint signature = SaveParser.GsChroniclesSectorSignature)
    {
        var data = new byte[0x20_000];
        for (var slot = 0; slot < 2; slot++)
        for (var id = 0; id < 14; id++)
        {
            var off = (slot * 14 + id) * 0x1000;
            if (id == 0)
            {
                StringConverter3.SetString(data.AsSpan(off, 7), "Mia", 7, jp: false);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xA), 4572);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xC), 10025);
            }
            if (id == 1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0x34), 1u);
                var mon = off + 0x38;
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(mon), 0x1234_5678u);
                StringConverter3.SetString(data.AsSpan(mon + 8, 10), "Mesprit", 10, jp: false);
                data[mon + 0x13] = 2; // hasSpecies
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(mon + 0x20), 534);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(mon + 0x24), 156);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(mon + 0x2C), 15); // Cut
                data[mon + 0x54] = 5;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF4), (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFF8), signature);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFFC), slot == 0 ? 4u : 3u);
            RadicalRedFormat.WriteChecksum(data, off, RadicalRedFormat.CfruWindows[id]);
        }
        return data;
    }

    private static void AssertAllChecksumsValid(byte[] bytes)
    {
        var sections = RadicalRedFormat.SectionOffsets(bytes);
        for (var id = 0; id < 14; id++)
        {
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sections[id] + 0xFF6));
            var window = RadicalRedFormat.CfruWindows[id];
            Assert.Equal(RadicalRedFormat.Checksum(bytes.AsSpan(sections[id], window), window), stored);
        }
    }

    // ── Detection (synthetic, always runs) ──

    [Fact]
    public void SignatureRoutesToGsChroniclesAndNeverToUnboundOrRadicalRed()
    {
        var bytes = SyntheticSave();
        Assert.True(SaveParser.IsPokemonGsChronicles(bytes));
        Assert.False(SaveParser.IsPokemonUnbound(bytes));
        Assert.False(SaveParser.IsPokemonRadicalRed(bytes));
        Assert.True(Engine.Validate(bytes));

        using var session = Engine.OpenSession(bytes, "GSC");
        Assert.IsType<GsChroniclesEngineSession>(session);
        Assert.Equal(["GS Chronicles"], session.GameNames);
        Assert.Equal(25, Assert.IsType<GsChroniclesEngineSession>(session).BoxCount);
        Assert.Equal("GSCHRONICLES", session.Snapshot.Format);
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(bytes, "GSC", SaveFormat.RadicalRed));
        using var routed = Engine.OpenSession(bytes, "GSC", SaveFormat.GsChronicles);
        Assert.IsType<GsChroniclesEngineSession>(routed);
    }

    [Fact]
    public void TheSameLayoutWithTheRetailSignatureIsNotGsChronicles()
    {
        var retail = SyntheticSave(0x0801_2025u);
        Assert.False(SaveParser.IsPokemonGsChronicles(retail));
        Assert.Throws<InvalidDataException>(() => new GsChroniclesEngineSession(retail));
    }

    [Fact]
    public void SyntheticPartyDecodesThroughTheGscTables()
    {
        using var session = new GsChroniclesEngineSession(SyntheticSave());
        var mesprit = session.ReadEntity(-1, 0);
        Assert.Equal(481, mesprit.Species); // GSC 534 -> national Mesprit
        Assert.Equal("Mesprit", mesprit.SpeciesName);
        Assert.Equal(5, mesprit.Level);
        Assert.Equal(15, mesprit.Move1);
        Assert.Equal("Mia", session.GetTrainer().Name);
    }

    [Fact]
    public void SaveBlockBoxesRoundTripInsideTheirChecksummedSections()
    {
        var original = SyntheticSave();
        using var session = new GsChroniclesEngineSession(original);
        // Box 23 (index 22) straddles sections 2/3 at slot 3 (SaveBlock1 0x1F08 + 3*58
        // = 0x1FB6 runs past the section boundary at 0x1FE0); box 25 (index 24) lives
        // in SaveBlock2.
        Assert.True(session.GenerateFromShowdownText(22, 3, "Pikachu\nLevel: 30\n- Thunderbolt").Success);
        Assert.True(session.GenerateFromShowdownText(24, 29, "Eevee\nLevel: 12").Success);
        Assert.True(session.GenerateFromShowdownText(23, 0, "Mewtwo\nLevel: 70").Success);

        var bytes = session.Serialize().ToArray();
        AssertAllChecksumsValid(bytes);
        var sections = RadicalRedFormat.SectionOffsets(bytes);
        // Slot 3 of box 23 begins at section 2 + 0xFC6 and spills 0x10 bytes (its
        // packed moves onward) into section 3; its species (compact +0x1C) is still in
        // section 2.
        Assert.Equal(25, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sections[2] + 0xFC6 + 0x1C)));
        Assert.Equal(133, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sections[0] + 0xB0 + 29 * 58 + 0x1C)));

        using var reopened = new GsChroniclesEngineSession(bytes);
        Assert.Equal(25, reopened.ReadEntity(22, 3).Species);
        Assert.Equal(30, reopened.ReadEntity(22, 3).Level);
        Assert.Equal(85, reopened.ReadEntity(22, 3).Move1); // Thunderbolt
        Assert.Equal(133, reopened.ReadEntity(24, 29).Species);
        Assert.Equal(150, reopened.ReadEntity(23, 0).Species);
        Assert.Equal(481, reopened.ReadEntity(-1, 0).Species);
        Assert.Equal(3, reopened.Snapshot.Slots.Count(s => s.Box >= 0 && s.Species is not null));
        Assert.Equal("BOX 25", reopened.GetBoxName(24));
    }

    [Fact]
    public void SyntheticSingleSlotEditPassesTheStructuralDiff()
    {
        var original = SyntheticSave();
        using var session = new GsChroniclesEngineSession(original);
        Assert.True(session.GenerateFromShowdownText(24, 0, "Eevee\nLevel: 12").Success);
        var candidate = session.Serialize().ToArray();
        Assert.Null(Engine.CheckWriteSafety(original, candidate, WriteScope.Only(new SlotRef(24, 0))));
        Assert.NotNull(Engine.CheckWriteSafety(original, candidate, WriteScope.Only(new SlotRef(24, 1))));
    }

    // ── Tables and bridges (always run) ──

    [Fact]
    public void SpeciesBridgeAgreesWithPkhexNamesAndRoundTrips()
    {
        var data = GsChroniclesData.Instance;
        var names = GameInfo.GetStrings("en").specieslist;
        var bridged = 0;
        for (var id = 1; id <= data.MaxSpeciesId; id++)
        {
            var national = data.NationalIdOf(id);
            if (national == 0) continue;
            bridged++;
            // The display name starts with the national name (forms append "-Form").
            var baseName = data.SpeciesName(id).Split('-')[0];
            if (names[national].Contains('-')) baseName = data.SpeciesName(id)[..names[national].Length];
            Assert.Equal(UnboundData.NormalizeName(names[national]), UnboundData.NormalizeName(baseName));
            var back = data.SpeciesFromNational(national);
            Assert.Equal(national, data.NationalIdOf(back));
            Assert.True(back <= id); // the base form is the lowest id
        }
        Assert.True(bridged > 1250, $"only {bridged} species bridged");
        Assert.Equal(481, data.NationalIdOf(534));
        Assert.Equal(0, data.NationalIdOf(706)); // Shadow Warrior, a GSC original
        // pokedex.h gives Annihilape GSC dex 906 (national 979): the bridge goes by name
        // and the dex bitmap keeps GSC's own number.
        var annihilape = data.SpeciesIdByName("Annihilape");
        Assert.Equal(979, data.NationalIdOf(annihilape));
        Assert.Equal(906, data.DexNumberOf(979));
        Assert.Equal(481, data.DexNumberOf(481));
        Assert.Equal("Vulpix-Alola", data.SpeciesName(data.SpeciesIdByName("Vulpix-Alola")));
        Assert.Equal(37, data.NationalIdOf(data.SpeciesIdByName("Vulpix-Alola")));
    }

    [Fact]
    public void MoveItemAndAbilityBridgesRoundTrip()
    {
        var data = GsChroniclesData.Instance;
        var strings = GameInfo.GetStrings("en");

        var moves = 0;
        for (var move = 1; move < 782; move++) // every non-Z move id
        {
            var national = data.MoveToNational(move);
            if (national == 0) continue;
            moves++;
            Assert.Equal(move, data.MoveFromNational(national));
        }
        Assert.True(moves > 700, $"only {moves} moves bridged");
        Assert.Equal(15, data.MoveToNational(15)); // Cut
        Assert.Equal("Dual Wingbeat", data.MoveName(730));
        Assert.Equal("Dual Wingbeat", strings.movelist[data.MoveToNational(730)]);
        Assert.Equal(10, data.MoveBasePp(730));

        Assert.Equal("Oran Berry", strings.itemlist[data.ItemToNational(139)]);
        Assert.Equal(139, data.ItemFromNational(data.ItemToNational(139)));
        Assert.Equal("Potion", data.ItemName(13));
        // items.h names 0xE2 twice; the ambiguous id is shown as such and never bridged.
        Assert.Equal("Rocky Helmet / Choice Specs", data.ItemName(226));
        Assert.Equal(0, data.ItemToNational(226));
        Assert.Contains(4, data.PocketIds("ball"));
        Assert.Contains(139, data.PocketIds("berry"));
        Assert.Contains(289, data.PocketIds("tm"));

        Assert.Equal("Levitate", data.AbilityName(26));
        Assert.Equal(26, data.AbilityIds(534).A1); // Mesprit: Levitate
    }

    [Fact]
    public void DisplayNameAndLayoutFamilyResolve()
    {
        var guess = SaveIdentityRules.Guess("GS Chronicles", 3, "Pokemon - GS Chronicles.sav", null, "Generation 3");
        Assert.Equal("Pokémon GS Chronicles", guess.Label);
        Assert.Equal(SaveFormat.GsChronicles, guess.Format);
        Assert.Equal(SaveLayoutFamily.GsChroniclesCfru, guess.Family);
        Assert.Equal(SaveFormat.GsChronicles, SaveIdentityRules.FormatOfChoice("gschronicles"));
    }

    // ── Ground truth (skipped without the local save) ──

    [Fact]
    public void RealSaveReadsAndSerializesByteIdentically()
    {
        var path = GroundTruth();
        if (path is null) return;
        var original = File.ReadAllBytes(path);

        Assert.True(SaveParser.IsPokemonGsChronicles(original));
        Assert.Equal("GS Chronicles", Engine.TryDescribe(original)?.GameName);
        using var session = Engine.OpenSession(original, "GSC");
        Assert.IsType<GsChroniclesEngineSession>(session);
        Assert.Equal(original, session.Serialize().ToArray());

        var mesprit = session.ReadEntity(-1, 0);
        Assert.Equal(481, mesprit.Species);
        Assert.Equal("Mesprit", mesprit.SpeciesName);
        Assert.Equal(5, mesprit.Level);
        Assert.Equal(23, mesprit.CurrentHp);
        Assert.Equal(15, mesprit.Move1); // Cut
        Assert.Equal(814, mesprit.Move2); // GSC 730 Dual Wingbeat -> national 814
        Assert.Equal(155, mesprit.HeldItem); // GSC 139 Oran Berry -> national 155
        Assert.Equal("Mia", mesprit.OriginalTrainer);
        Assert.True(session.ReadEntity(-1, 1).IsEmpty);

        var trainer = session.GetTrainer();
        Assert.Equal(("Mia", 4572, 10025, 10000u), (trainer.Name, trainer.TID, trainer.SID, trainer.Money));
        Assert.Equal(new DexEntryState(true, true), session.GetDexEntry(481));
        Assert.Equal(1, session.GetDexProgress().Caught);

        var items = session.GetBag().Single(pouch => pouch.Name == "Items").Items;
        Assert.Equal([new BagItem(13, 2)], items); // 2 Potions
        Assert.All(session.GetBag().Where(pouch => pouch.Name != "Items"), pouch => Assert.Empty(pouch.Items));
        Assert.Equal(0, session.Snapshot.Slots.Count(s => s.Box >= 0 && s.Species is not null)); // empty PC
    }

    [Fact]
    public void RealSaveSingleSlotEditPassesTheStructuralDiffAndReopens()
    {
        var path = GroundTruth();
        if (path is null) return;
        var original = File.ReadAllBytes(path);

        using var session = new GsChroniclesEngineSession(original);
        session.ApplyEdit(-1, 0, new EntityEdit(Level: 20));
        var candidate = session.Serialize().ToArray();
        AssertAllChecksumsValid(candidate);
        Assert.Null(Engine.CheckWriteSafety(original, candidate, WriteScope.Only(new SlotRef(-1, 0))));
        Assert.NotNull(Engine.CheckWriteSafety(original, candidate, WriteScope.Only(new SlotRef(0, 0))));

        using var reopened = new GsChroniclesEngineSession(candidate);
        Assert.Equal(20, reopened.ReadEntity(-1, 0).Level);
        Assert.Equal(481, reopened.ReadEntity(-1, 0).Species);

        // A PC write into box 25 (SaveBlock2) keeps the rest of the save intact.
        Assert.True(reopened.DuplicateSlot(-1, 0, 24, 0));
        using var boxed = new GsChroniclesEngineSession(reopened.Serialize());
        Assert.Equal(481, boxed.ReadEntity(24, 0).Species);
        Assert.Equal("Mia", boxed.GetTrainer().Name);
    }
}
