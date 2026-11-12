using PKForge.Domain;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// A CFRU record crossing to another game goes through a PK3, and every id in it must be the
/// national twin of the hack's own: v3.0.3 copied Radical Red's internal numbers across, so a
/// Lairon (Radical Red 383) arrived as Groudon (national 383) and a Castform as Jirachi.
/// Every Pokémon of the real and demo saves converts out with the same species, moves, held
/// item, ability and ball names the hack shows, or is refused with the reason; every Pokémon
/// of a vanilla FireRed save converts in the same way. Saves are gitignored (skipped in CI).
/// </summary>
public sealed class CfruPk3ConversionTests(ITestOutputHelper output)
{
    private static readonly GameStrings Strings = GameInfo.GetStrings("en");
    private readonly SaveEngine _engine = new();

    public static TheoryData<string> HackSaves => new()
    {
        "thor/retroarch/mGBA/Radical-red-champ.sav",
        "thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm",
        "romhacks/demo/Radical Red.sav",
        "romhacks/demo/Pokemon Unbound.srm",
        "romhacks/batch-2026-11-12/romhack/romhack saves/Pokemon - GS Chronicles.sav",
    };

    private static byte[]? TestData(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Names compare blind to case, spacing, punctuation and gender symbols
    /// ("BlackGlasses" and "Black Glasses", "Nidoran-F" and "Nidoran♀").</summary>
    private static string Key(string name) => UnboundData.NormalizeName(name);

    /// <summary>What the hack itself shows for a record: species, moves, item, ability, PKHeX ball.</summary>
    private static (string Species, string[] Moves, int[] StoredMoves, int Item, string ItemName, string Ability, int Ball) HackView(byte[] record, string format)
    {
        if (format == CfruEntity.Unbound)
        {
            var mon = new UnboundMon(record, 0, party: false);
            return (UnboundData.SpeciesName(mon.Species), [.. mon.Moves.Select(UnboundData.MoveName)], mon.Moves,
                mon.HeldItem, UnboundData.ItemName(mon.HeldItem), UnboundData.AbilityName(UnboundData.ActiveAbility(mon)), mon.DisplayBall);
        }
        var data = format == CfruEntity.GsChronicles ? (ICfruGameData)GsChronicles.GsChroniclesData.Instance : RadicalRedGameData.Instance;
        var cfru = new RadicalRedMon(record, 0, false, data);
        return (data.SpeciesName(cfru.Species), [.. cfru.Moves.Select(data.MoveName)], cfru.Moves,
            cfru.HeldItem, data.ItemName(cfru.HeldItem), data.AbilityName(data.ActiveAbility(cfru)), cfru.DisplayBall);
    }

    [Theory]
    [MemberData(nameof(HackSaves))]
    public void EveryPokemonLeavesWithItsOwnIdsOrIsRefused(string file)
    {
        if (TestData(file) is not { } bytes) return;
        using var session = _engine.OpenSession(bytes, file);
        var converted = 0;
        var refusals = new Dictionary<string, int>();
        foreach (var slot in session.Snapshot.Slots.Where(s => s.Species is not null))
        {
            var export = session.ExportSlot(slot.Box, slot.Slot);
            var hack = HackView(export.Data, export.Format!);
            var pk3 = EntityBytes.Parse(export.Data, export.Format);
            if (pk3 is null)
            {
                // Refused: the reason names the mon and what Generation 3 lacks, and the
                // transfer refusal the player sees carries it.
                var why = CfruEntity.Pk3Refusal(export.Data, export.Format!);
                Assert.NotNull(why);
                Assert.Contains(why, TransferCompatibility.ExplainRefusal(export.Data, "It", "Gen3", 3, "FireRed", export.Format));
                var kind = why[(why.IndexOf(' ') + 1)..].Split(' ')[0];
                refusals[kind] = refusals.GetValueOrDefault(kind) + 1;
                continue;
            }

            Assert.IsType<PK3>(pk3);
            var where = $"{file} {slot.Box}/{slot.Slot} {hack.Species}";
            // Base species only: a form the hack stores as its own species is refused.
            Assert.True(Key(hack.Species) == Key(Strings.specieslist[pk3.Species]), $"{where}: species became {Strings.specieslist[pk3.Species]}");
            var moves = new[] { pk3.Move1, pk3.Move2, pk3.Move3, pk3.Move4 };
            for (var i = 0; i < 4; i++)
            {
                if (hack.StoredMoves[i] == 0) Assert.Equal(0, moves[i]);
                else Assert.True(Key(hack.Moves[i]) == Key(Strings.movelist[moves[i]]), $"{where}: {hack.Moves[i]} became {Strings.movelist[moves[i]]}");
            }
            if (hack.Item == 0) Assert.Equal(0, pk3.HeldItem);
            else Assert.True(Key(hack.ItemName) == Key(Strings.itemlist[ItemConverter.GetItemFuture3((ushort)pk3.HeldItem)]),
                $"{where}: {hack.ItemName} became {Strings.itemlist[ItemConverter.GetItemFuture3((ushort)pk3.HeldItem)]}");
            // A ball or ability Generation 3 lacks crosses as a Poké Ball or the PID's ability,
            // and the change is reported; everything else keeps its own.
            var adjustments = CfruEntity.Pk3Adjustments(export.Data, export.Format!);
            if (Key(hack.Ability) != Key(Strings.abilitylist[pk3.Ability]))
                Assert.Contains(adjustments, line => line.Contains(Strings.abilitylist[pk3.Ability]));
            if (hack.Ball is > 0 and <= 12) Assert.Equal(hack.Ball, pk3.Ball);
            else
            {
                Assert.Equal((int)Ball.Poke, pk3.Ball);
                Assert.Contains(adjustments, line => line.Contains("Poké Ball"));
            }
            converted++;
        }
        output.WriteLine($"{file}: {converted} converted; refused: {string.Join(", ", refusals.Select(r => $"{r.Key} {r.Value}"))}");
        Assert.True(converted + refusals.Values.Sum() > 0);
    }

    public static TheoryData<string> Destinations => new()
    {
        "romhacks/demo/Radical Red.sav",
        "romhacks/demo/Pokemon Unbound.srm",
        "romhacks/batch-2026-11-12/romhack/romhack saves/Pokemon - GS Chronicles.sav",
    };

    [Theory]
    [MemberData(nameof(Destinations))]
    public void EveryVanillaPokemonArrivesWithItsOwnIdsOrIsRefused(string file)
    {
        if (TestData("firered-vanilla.sav") is not { } vanillaBytes || TestData(file) is not { } bytes) return;
        using var vanilla = _engine.OpenSession(vanillaBytes, "FireRed");
        var arrived = 0;
        var refused = 0;
        foreach (var source in vanilla.Snapshot.Slots.Where(s => s.Species is not null))
        {
            using var hack = _engine.OpenSession(bytes, file);
            var free = hack.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is null);
            var export = vanilla.ExportSlot(source.Box, source.Slot);
            var pk3 = (PK3)EntityBytes.Parse(export.Data, export.Format)!;
            if (!hack.ImportSlot(free.Box, free.Slot, export.Data, export.Format))
            {
                var why = TransferCompatibility.ExplainRefusal(export.Data, "It", hack.Snapshot.Format, 3, file, export.Format);
                Assert.NotNull(why);
                Assert.Contains("It cannot go to", why);
                Assert.True(hack.ReadEntity(free.Box, free.Slot).IsEmpty); // a refusal writes nothing
                output.WriteLine(why);
                refused++;
                continue;
            }

            var landed = hack.ExportSlot(free.Box, free.Slot);
            var view = HackView(landed.Data, landed.Format!);
            var where = $"{Strings.specieslist[pk3.Species]} into {file}";
            Assert.True(Key(view.Species) == Key(Strings.specieslist[pk3.Species]), $"{where}: became {view.Species}");
            var moves = new[] { pk3.Move1, pk3.Move2, pk3.Move3, pk3.Move4 };
            for (var i = 0; i < 4; i++)
            {
                if (moves[i] == 0) Assert.Equal(0, view.StoredMoves[i]);
                else Assert.True(Key(view.Moves[i]) == Key(Strings.movelist[moves[i]]), $"{where}: {Strings.movelist[moves[i]]} became {view.Moves[i]}");
            }
            if (pk3.HeldItem == 0) Assert.Equal(0, view.Item);
            else Assert.True(Key(view.ItemName) == Key(Strings.itemlist[ItemConverter.GetItemFuture3((ushort)pk3.HeldItem)]), $"{where}: {Strings.itemlist[ItemConverter.GetItemFuture3((ushort)pk3.HeldItem)]} became {view.ItemName} (#{view.Item})");
            Assert.True(Key(view.Ability) == Key(Strings.abilitylist[pk3.Ability]), $"{where}: {Strings.abilitylist[pk3.Ability]} became {view.Ability}");
            Assert.Equal(pk3.Ball, view.Ball);
            arrived++;
        }
        output.WriteLine($"{file}: {arrived} arrived, {refused} refused");
        Assert.True(arrived > 0);
    }
}
