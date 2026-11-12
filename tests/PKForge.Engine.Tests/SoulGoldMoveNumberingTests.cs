using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.SoulGold;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Soul Gold stores moves in the pokeemerald-expansion numbering, which leaves out the
/// Z-moves: past Hyperspace Fury (621) its ids and PKHeX's diverge (Brutal Swing is 656 in
/// the record, 693 in PKHeX). The editor speaks PKHeX ids, so the session translates at its
/// edge, names hack-only moves read-only and never rewrites what the user did not touch.
/// The sample save (.local-testdata, gitignored) holds no move past 621, so those records
/// are made from its own boxed Pidgey through the .pk3sg import path; a missing sample skips.
/// </summary>
public sealed class SoulGoldMoveNumberingTests
{
    private const int RawBrutalSwing = 656;
    private const int RawShoreUp = 622;
    private const int RawShadowBlast = 848; // Soul Gold's own move (MOVE_DARK_AERO), no PKHeX id

    private static byte[]? Sample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "romhacks", "soulgold.sav");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static EntityEdit Echo(EntityDetail d) => new(
        d.Species, d.Nickname, d.Level, d.Nature, d.Ability, d.HeldItem, d.Move1, d.Move2, d.Move3, d.Move4,
        d.IVs, d.EVs, d.IsShiny, d.Ball, d.OriginalTrainer, d.Gender, d.Friendship);

    /// <summary>The four raw move ids of a .pk3sg record (secure words 3 and 4, 11 bits each).</summary>
    private static int[] RawMoves(byte[] record)
    {
        var low = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x2C));
        var high = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x30));
        return [(int)(low & 0x7FF), (int)((low >> 16) & 0x7FF), (int)(high & 0x7FF), (int)((high >> 16) & 0x7FF)];
    }

    private static byte[] WithRawMoves(byte[] record, int move2, int move3)
    {
        var copy = record.ToArray();
        var low = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(0x2C));
        var high = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(0x30));
        low = (low & ~(0x7FFu << 16)) | ((uint)move2 << 16);
        high = (high & ~0x7FFu) | (uint)move3;
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x2C), low);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x30), high);
        return copy;
    }

    private static (int Box, int Slot) FirstEmptyBoxSlot(SoulGoldEngineSession session)
    {
        for (var box = 0; box < session.BoxCount; box++)
        for (var slot = 0; slot < session.BoxSlotCount; slot++)
            if (session.ReadEntity(box, slot).IsEmpty)
                return (box, slot);
        throw new InvalidOperationException("No empty box slot.");
    }

    /// <summary>The boxed Pidgey (Tackle only) with Brutal Swing and Shadow Blast added, imported
    /// into the first empty box slot.</summary>
    private static (int Box, int Slot) ImportPidgeyWithHackMoves(SoulGoldEngineSession session)
    {
        var record = session.ExportSlot(0, 0).Data;
        Assert.Equal([(int)Move.Tackle, 0, 0, 0], RawMoves(record));
        var target = FirstEmptyBoxSlot(session);
        Assert.True(session.ImportSlot(target.Box, target.Slot, WithRawMoves(record, RawBrutalSwing, RawShadowBlast), "pk3sg"));
        return target;
    }

    [Fact]
    public void The_tables_translate_by_name_past_the_national_order()
    {
        var data = SoulGoldData.Instance;
        var names = GameInfo.GetStrings("en").movelist;
        Assert.Equal("Brutal Swing", data.MoveName(RawBrutalSwing));
        Assert.Equal((int)Move.BrutalSwing, data.MoveToNational(RawBrutalSwing));
        Assert.Equal(RawShoreUp, data.MoveFromNational((int)Move.ShoreUp));
        Assert.Equal(0, data.MoveToNational(RawShadowBlast));
        Assert.Equal((int)Move.HyperspaceFury, data.MoveToNational(621));

        // Every move a Pokémon can know, named as PKHeX names its id (the pack once held Max
        // moves at 0-51 and lost the ids written as MOVES_COUNT_GENn, Sketch to Tera Blast).
        for (var raw = 1; raw <= 847; raw++)
        {
            var national = data.MoveToNational(raw);
            Assert.True(national > 0, $"raw move {raw} {data.MoveName(raw)} has no PKHeX id");
            Assert.Equal(raw, data.MoveFromNational(national));
            Assert.Equal(Normalize(names[national]), Normalize(data.MoveName(raw)));
        }
        Assert.Equal(1, data.MoveBasePp((int)Move.Sketch));
        Assert.Equal(35, data.MoveBasePp((int)Move.Pound));

        static string Normalize(string name) => new(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    [Fact]
    public void The_species_pack_holds_the_expansions_types_stats_and_abilities()
    {
        var data = SoulGoldData.Instance;
        // Types in the engine's 0-17 order (Fire is 9), never the expansion's (Fire 11, Mystery 10).
        Assert.Equal([(int)MoveType.Fire], data.Types((int)Species.Charmander));
        Assert.Equal([(int)MoveType.Grass, (int)MoveType.Poison], data.Types((int)Species.Bulbasaur));
        Assert.Equal([(int)MoveType.Fairy], data.Types((int)Species.Clefairy));
        // Conditional stats resolve to the latest generation instead of 0.
        Assert.Equal([39, 52, 43, 60, 50, 65], data.BaseStats((int)Species.Charmander));
        // Abilities written as ABILITIES_COUNT_GENn exist (Tangled Feet).
        Assert.Equal([(int)Ability.KeenEye, (int)Ability.TangledFeet, (int)Ability.BigPecks], data.Abilities((int)Species.Pidgey));
        Assert.Equal("Tangled Feet", data.AbilityName((int)Ability.TangledFeet));
        // Species defined through a macro are known (Unown, Arceus).
        Assert.True(data.IsKnownSpecies((int)Species.Unown));
        Assert.Equal((int)Species.Arceus, data.NationalIdOf((int)Species.Arceus));
    }

    [Fact]
    public void A_move_past_621_reads_with_its_name_and_a_hack_only_move_by_its_own()
    {
        if (Sample() is not { } sample) return;
        using var session = new SoulGoldEngineSession(sample);
        var (box, slot) = ImportPidgeyWithHackMoves(session);

        var pidgey = session.ReadEntity(box, slot);
        Assert.Equal((int)Species.Pidgey, pidgey.Species);
        Assert.Equal((int)Move.Tackle, pidgey.Move1);
        Assert.Equal((int)Move.BrutalSwing, pidgey.Move2); // raw 656, which PKHeX calls Twinkle Tackle
        Assert.Equal(0, pidgey.Move3);                     // PKHeX has no id for it...
        Assert.Equal([null, null, "Shadow Blast", null], pidgey.MoveNames!); // ...so it is named read-only
        Assert.Equal((int)Move.BrutalSwing, session.GetMoveDetails(box, slot).Moves[1].Move);
        Assert.Contains("- Brutal Swing", session.GetShowdownText(box, slot));
        Assert.True(session.StoresMove((int)Move.BrutalSwing));
        Assert.False(session.StoresMove((int)Move.TwinkleTackleP)); // Z-moves are not in the hack
    }

    [Fact]
    public void A_picked_move_writes_the_expansion_id_and_survives_a_reopen()
    {
        if (Sample() is not { } sample) return;
        byte[] written;
        using (var session = new SoulGoldEngineSession(sample))
        {
            session.ApplyEdit(0, 0, new EntityEdit(Move2: (int)Move.ShoreUp, Move3: (int)Move.BrutalSwing));
            written = session.Serialize().ToArray();
        }
        using var reopened = new SoulGoldEngineSession(written);
        var pidgey = reopened.ReadEntity(0, 0);
        Assert.Equal([(int)Move.Tackle, (int)Move.ShoreUp, (int)Move.BrutalSwing, 0],
            new[] { pidgey.Move1, pidgey.Move2, pidgey.Move3, pidgey.Move4 });
        Assert.Null(pidgey.MoveNames);
        Assert.Equal([(int)Move.Tackle, RawShoreUp, RawBrutalSwing, 0], RawMoves(reopened.ExportSlot(0, 0).Data));
        var details = reopened.GetMoveDetails(0, 0).Moves;
        Assert.Equal((5, 20), (details[1].MaxPP, details[2].MaxPP)); // Shore Up 5, Brutal Swing 20

        // The .pk3sg export carries the hack's ids and imports back to the same moves.
        var export = reopened.ExportSlot(0, 0).Data;
        var target = FirstEmptyBoxSlot(reopened);
        Assert.True(reopened.ImportSlot(target.Box, target.Slot, export, "pk3sg"));
        Assert.Equal((int)Move.ShoreUp, reopened.ReadEntity(target.Box, target.Slot).Move2);
    }

    [Fact]
    public void Saving_without_a_change_keeps_every_byte_and_every_hack_only_move()
    {
        if (Sample() is not { } sample) return;
        using (var session = new SoulGoldEngineSession(sample))
        {
            foreach (var summary in session.Snapshot.Slots.Where(s => s.Species is not null))
                session.ApplyEdit(summary.Box, summary.Slot, Echo(session.ReadEntity(summary.Box, summary.Slot)));
            Assert.True(session.Serialize().Span.SequenceEqual(sample), "echoing every Pokémon changed the save");
        }

        byte[] imported;
        (int Box, int Slot) at;
        using (var session = new SoulGoldEngineSession(sample))
        {
            at = ImportPidgeyWithHackMoves(session);
            imported = session.Serialize().ToArray();
        }
        using (var session = new SoulGoldEngineSession(imported))
        {
            // The editor shows Shadow Blast as 0 and echoes 0: that keeps it.
            session.ApplyEdit(at.Box, at.Slot, Echo(session.ReadEntity(at.Box, at.Slot)));
            Assert.True(session.Serialize().Span.SequenceEqual(imported), "echoing a hack-only move changed the save");

            // Changing another move leaves it in place too.
            session.ApplyEdit(at.Box, at.Slot, Echo(session.ReadEntity(at.Box, at.Slot)) with { Move4 = (int)Move.Growl });
            Assert.Equal([(int)Move.Tackle, RawBrutalSwing, RawShadowBlast, (int)Move.Growl],
                RawMoves(session.ExportSlot(at.Box, at.Slot).Data));
        }
    }
}
