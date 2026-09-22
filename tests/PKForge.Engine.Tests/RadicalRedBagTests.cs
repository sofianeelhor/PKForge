using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Radical Red bag against the owner's real champion save: the five CFRU pockets
/// (Items 450 / Key 75 / Balls 50 / TM 128 / Berries 75 capacity, 4-byte zero-terminated
/// slots) start in section 13's parasite tail and spill into the raw sector-30/31
/// region, quantities XORed with the security key. Every count below was pinned by
/// the triage script cases/radical-red/10-triage/05_bag_layout.py.
/// </summary>
public sealed class RadicalRedBagTests
{
    private static string? GroundTruth()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "radicalred-champ.sav");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static RadicalRedEngineSession? OpenGroundTruth()
    {
        var path = GroundTruth();
        if (path is null) return null; // gitignored ground truth: dev-only, skipped on CI
        return new RadicalRedEngineSession(File.ReadAllBytes(path), "Radical Red");
    }

    private static BagPouch Pouch(ISaveEngineSession session, string name) =>
        session.GetBag().Single(pouch => pouch.Name == name);

    [Fact]
    public void BagDecodesTheFiveChampionPouches()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var bag = session.GetBag();
        Assert.Equal(["Items", "Key Items", "Balls", "TMs", "Berries"], bag.Select(pouch => pouch.Name));

        var items = bag[0].Items;
        Assert.Equal(143, items.Count);
        Assert.Contains(new BagItem(68, 305), items); // Rare Candy
        Assert.Contains(new BagItem(72, 295), items); // Ability Pill
        Assert.Contains(new BagItem(711, 18), items); // Assault Vest — CFRU extension id

        var key = bag[1].Items;
        Assert.Equal(19, key.Count);
        Assert.All(key, entry => Assert.Equal(1, entry.Count)); // one of each, like the game
        Assert.Contains(key, entry => entry.Id == 182); // Exp. Share is a KEY item in this hack
        Assert.Contains(key, entry => entry.Id == 360); // Bicycle

        var balls = bag[2].Items;
        Assert.Equal(8, balls.Count);
        Assert.Contains(balls, entry => entry.Id == 1 && entry.Count >= 1); // Master Ball
        Assert.Contains(balls, entry => entry.Id == 251); // Beast Ball — CFRU ball block

        var machines = bag[3].Items;
        Assert.Equal(94, machines.Count);
        Assert.All(machines, entry => Assert.Equal(1, entry.Count)); // reusable discs
        Assert.Contains(machines, entry => entry.Id == 339); // HM01 shares the TM pocket
        Assert.Contains(machines, entry => entry.Id == 445); // TM120

        var berries = bag[4].Items;
        Assert.Equal(30, berries.Count);
        Assert.Contains(new BagItem(153, 300), berries); // Pomeg Berry — stacks past 99
        Assert.Contains(new BagItem(446, 7), berries); // Occa Berry — CFRU berry block
    }

    [Fact]
    public void SetItemCountUpdatesAppendsAndRemovesThroughReopen()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Equal(500, session.SetItemCount("Items", 68, 500)); // Rare Candy 305 -> 500
        Assert.Equal(30, session.SetItemCount("Balls", 6, 30)); // Net Ball appended
        Assert.Equal(7, session.SetItemCount("TMs", 445, 7)); // TM120 count grows
        Assert.Equal(0, session.SetItemCount("No Such Pouch", 1, 5)); // unknown pouch refuses

        using var reopened = new RadicalRedEngineSession(session.Serialize());
        Assert.Contains(new BagItem(68, 500), Pouch(reopened, "Items").Items);
        Assert.Contains(new BagItem(6, 30), Pouch(reopened, "Balls").Items);
        Assert.Contains(new BagItem(445, 7), Pouch(reopened, "TMs").Items);
        Assert.Equal(143, Pouch(reopened, "Items").Items.Count); // edits did not disturb neighbours

        Assert.Equal(0, reopened.SetItemCount("TMs", 445, 0)); // removal
        using var removed = new RadicalRedEngineSession(reopened.Serialize());
        Assert.DoesNotContain(Pouch(removed, "TMs").Items, entry => entry.Id == 445);
        Assert.DoesNotContain(Pouch(removed, "Items").Items, entry => entry.Id == 445);
    }

    [Fact]
    public void SerializeStaysByteIdenticalWhenTheBagIsOnlyRead()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Equal(5, session.GetBag().Count);
        Assert.NotEmpty(session.GetPouchLegalItems("Items"));
        Assert.Equal(File.ReadAllBytes(GroundTruth()!), session.Serialize().ToArray());
    }

    [Fact]
    public void BagEditsKeepEveryChecksumAndSectionValid()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        session.SetItemCount("Items", 68, 999); // touches the section-13 parasite path
        session.SetItemCount("Berries", 446, 99); // touches the raw-region path

        var bytes = session.Serialize().ToArray();
        for (var sector = 0; sector < RadicalRedFormat.SectorCount; sector++)
        {
            var off = sector * RadicalRedFormat.SectorSize;
            // The raw sectors 30/31 carry zeroed footers (id 0, no signature); only
            // sectors with a retail 0x0801 20xx footer hold a checksum to verify.
            if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 0xFF8)) & 0xFFFF_FF00u) != 0x0801_2000u)
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF4));
            if (id >= RadicalRedFormat.SectionCount) continue; // Hall of Fame sector
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF6));
            var window = RadicalRedFormat.CfruWindows[id];
            Assert.Equal(RadicalRedFormat.Checksum(bytes.AsSpan(off, window), window), stored);
        }
        Assert.True(SaveParser.IsPokemonRadicalRed(bytes));

        // The bag rides the parasite: every rotating copy of section 13 must agree,
        // byte for byte, past the checksum window too.
        var copies = RadicalRedFormat.AllSectionOffsets(bytes, RadicalRedFormat.BagSection);
        Assert.True(copies.Count >= 2);
        var live = RadicalRedFormat.SectionOffsets(bytes)[RadicalRedFormat.BagSection];
        foreach (var copy in copies)
            Assert.Equal(
                bytes.AsSpan(live + 0x450, 0xFF4 - 0x450).ToArray(),
                bytes.AsSpan(copy + 0x450, 0xFF4 - 0x450).ToArray());

        // The raw region also carries the PC's overflow boxes further in: the bag
        // write must not have eaten them, and the champion's mons must all survive.
        using var reopened = new RadicalRedEngineSession(bytes);
        var mons = 0;
        for (var box = 0; box < reopened.BoxCount; box++)
        for (var slot = 0; slot < reopened.BoxSlotCount; slot++)
            if (!reopened.ReadEntity(box, slot).IsEmpty)
                mons++;
        Assert.Equal(151, mons);
        Assert.False(reopened.ReadEntity(-1, 0).IsEmpty); // party Terapagos untouched
    }

    [Fact]
    public void ItemsPocketFillsAcrossTheSectionThirteenBoundary()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        // 143 entries sit in section 13; capacity 450 means slots 326+ live in the
        // raw sector-30/31 region. Fill the pocket to force writes across the split.
        var legal = session.GetPouchLegalItems("Items");
        var existing = Pouch(session, "Items").Items.Select(entry => entry.Id).ToHashSet();
        var added = 0;
        foreach (var id in legal.Take(600))
        {
            if (existing.Contains(id)) continue;
            if (session.SetItemCount("Items", id, 1) > 0)
                added++;
        }
        Assert.Equal(450, Pouch(session, "Items").Items.Count); // filled to capacity
        Assert.Equal(450 - 143, added);
        Assert.Equal(0, session.SetItemCount("Items", 999, 1)); // full pouch refuses a new item

        using var reopened = new RadicalRedEngineSession(session.Serialize());
        var items = Pouch(reopened, "Items").Items;
        Assert.Equal(450, items.Count);
        Assert.Contains(new BagItem(68, 305), items); // the original Rare Candy survived
        Assert.True(SaveParser.IsPokemonRadicalRed(reopened.Serialize().ToArray()));
    }

    [Fact]
    public void LegalItemsMatchThePocketZonesObservedInTheSave()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;

        var items = session.GetPouchLegalItems("Items");
        Assert.Contains(13, items); // Potion
        Assert.Contains(202, items); // Light Ball — a held item despite the name
        Assert.DoesNotContain(1, items); // Master Ball belongs to the ball pocket
        Assert.DoesNotContain(375, items); // "-DONT USE- -" filler never surfaces
        Assert.DoesNotContain(749, items); // "Free Space22" filler never surfaces

        var balls = session.GetPouchLegalItems("Balls");
        Assert.Equal([.. Enumerable.Range(1, 12), .. Enumerable.Range(239, 15)], balls);
        Assert.DoesNotContain(686, balls); // Iron Ball is a held item

        Assert.Equal(128, session.GetPouchLegalItems("TMs").Count);
        Assert.All(session.GetPouchLegalItems("TMs"), id => Assert.True(id is >= 289 and <= 445));

        var berries = session.GetPouchLegalItems("Berries");
        Assert.Contains(133, berries); // Cheri
        Assert.Contains(446, berries); // Occa — CFRU berry block
        Assert.DoesNotContain(44, berries); // Berry Juice is a drink

        var key = session.GetPouchLegalItems("Key Items");
        Assert.Contains(182, key); // Exp. Share
        Assert.Contains(262, key); // Old Rod
        Assert.Contains(360, key); // Bicycle
        Assert.DoesNotContain(276, key); // Red Orb is a held item in this hack
    }
}
