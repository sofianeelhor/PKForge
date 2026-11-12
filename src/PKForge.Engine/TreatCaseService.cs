using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Which contest-treat storage the open save has.</summary>
public enum TreatCaseKind
{
    None,
    /// <summary>Diamond/Pearl/Platinum: 100 poffins (PKHeX PoffinCase4).</summary>
    Poffins4,
    /// <summary>Brilliant Diamond/Shining Pearl: 100 poffins (PKHeX PoffinSaveData8b).</summary>
    Poffins8b,
    /// <summary>Ruby/Sapphire/Emerald: 40 Pokéblocks (PKHeX PokeBlock3Case).</summary>
    PokeBlocks3,
    /// <summary>Omega Ruby/Alpha Sapphire: twelve Pokéblock counters (PKHeX Contest6).</summary>
    PokeBlocks6,
}

/// <summary>One treat in a poffin case or Pokéblock case. Smoothness is the Pokéblock "feel" in Gen 3.</summary>
public sealed record ContestTreat(int Slot, string Name, int Level, int Smoothness,
    int Spicy, int Dry, int Sweet, int Bitter, int Sour);

/// <summary>
/// Poffin and Pokéblock cases, mirroring PKHeX's editors: PoffinCase4Editor (FillCase,
/// DeleteAll), SAV_Poffin8b (B_All: MstID 0x1C, level 60, smoothness and flavors 255;
/// B_None: ToNull), Pokeblock3CaseEditor (MaximizeAll(true), DeleteAll) and
/// SAV_PokeBlockORAS (counts 0-999, "give all" = 999). HeartGold/SoulSilver and
/// Fire Red/Leaf Green have neither case.
/// </summary>
public static class TreatCaseService
{
    /// <summary>The twelve Contest6 counters, in PKHeX's order (pokeblock strings 94-105).</summary>
    public static readonly IReadOnlyList<string> OrasBlockNames =
    [
        "Red Pokéblock", "Blue Pokéblock", "Pink Pokéblock", "Green Pokéblock", "Yellow Pokéblock", "Rainbow Pokéblock",
        "Red Pokéblock +", "Blue Pokéblock +", "Pink Pokéblock +", "Green Pokéblock +", "Yellow Pokéblock +", "Rainbow Pokéblock +",
    ];

    /// <summary>SAV_Poffin8b.B_All values.</summary>
    public const byte BdspFillMstId = 0x1C;
    public const byte BdspFillLevel = 60;

    public static TreatCaseKind GetKind(ISaveEngineSession session) => Save(session) switch
    {
        SAV4Sinnoh => TreatCaseKind.Poffins4,
        SAV8BS => TreatCaseKind.Poffins8b,
        SAV3 { LargeBlock: ISaveBlock3LargeHoenn } => TreatCaseKind.PokeBlocks3,
        SAV6AO => TreatCaseKind.PokeBlocks6,
        _ => TreatCaseKind.None,
    };

    public static bool IsSupported(ISaveEngineSession session) => GetKind(session) != TreatCaseKind.None;

    /// <summary>Case capacity: 100 poffins or 40 Pokéblocks; 0 for ORAS counters and other games.</summary>
    public static int Capacity(ISaveEngineSession session) => GetKind(session) switch
    {
        TreatCaseKind.Poffins4 or TreatCaseKind.Poffins8b => 100,
        TreatCaseKind.PokeBlocks3 => 40,
        _ => 0,
    };

    /// <summary>The treats actually in the case (empty slots left out), in slot order.</summary>
    public static IReadOnlyList<ContestTreat> GetTreats(ISaveEngineSession session)
    {
        switch (Save(session))
        {
            case SAV4Sinnoh sav:
            {
                var names = Util.GetStringList("text_poffin4_en");
                return new PoffinCase4(sav).Poffins
                    .Select((p, i) => (p, i))
                    .Where(x => IsPoffin(x.p))
                    .Select(x => new ContestTreat(x.i, Name(names, (int)x.p.Type, "Poffin"), x.p.Level, x.p.Smoothness,
                        x.p.BoostSpicy, x.p.BoostDry, x.p.BoostSweet, x.p.BoostBitter, x.p.BoostSour))
                    .ToList();
            }
            case SAV8BS sav:
            {
                var names = Util.GetStringList("text_poffin8b_en");
                return sav.Poffins.GetPoffins()
                    .Select((p, i) => (p, i))
                    .Where(x => !x.p.IsNull)
                    .Select(x => new ContestTreat(x.i, Name(names, x.p.MstID + 1, "Poffin"), x.p.Level, x.p.Taste,
                        x.p.FlavorSpicy, x.p.FlavorDry, x.p.FlavorSweet, x.p.FlavorBitter, x.p.FlavorSour))
                    .ToList();
            }
            case SAV3 { LargeBlock: ISaveBlock3LargeHoenn hoenn }:
            {
                var names = Util.GetStringList("text_pokeblock3_en");
                return hoenn.PokeBlocks.Blocks
                    .Select((b, i) => (b, i))
                    .Where(x => x.b.Color != PokeBlock3Color.NoBlock)
                    .Select(x => new ContestTreat(x.i, Name(names, (int)x.b.Color, "Pokéblock"), x.b.Level, x.b.Feel,
                        x.b.Spicy, x.b.Dry, x.b.Sweet, x.b.Bitter, x.b.Sour))
                    .ToList();
            }
            default:
                return [];
        }
    }

    /// <summary>ORAS's twelve Pokéblock counters; empty for every other game.</summary>
    public static IReadOnlyList<CountedEntry> GetBlockCounts(ISaveEngineSession session)
    {
        if (Save(session) is not SAV6AO sav) return [];
        var contest = sav.Contest;
        return OrasBlockNames.Select((name, i) => new CountedEntry(i, name, (int)contest.GetBlockCount(i), (int)Contest6.MaxBlock)).ToList();
    }

    /// <summary>Sets one ORAS counter, clamped to 0-999 as Contest6 does. Returns the stored count.</summary>
    public static int SetBlockCount(ISaveEngineSession session, int index, int count)
    {
        if (Save(session) is not SAV6AO sav)
            throw new NotSupportedException("Only Omega Ruby and Alpha Sapphire count Pokéblocks.");
        sav.Contest.SetBlockCount(index, (uint)Math.Clamp(count, 0, (int)Contest6.MaxBlock));
        return (int)sav.Contest.GetBlockCount(index);
    }

    /// <summary>Every slot becomes the best treat PKHeX's "give all" writes. Returns how many treats the case holds.</summary>
    public static int Fill(ISaveEngineSession session)
    {
        switch (Save(session))
        {
            case SAV4Sinnoh sav:
            {
                var box = new PoffinCase4(sav);
                box.FillCase();
                box.Save();
                break;
            }
            case SAV8BS sav:
            {
                var poffins = sav.Poffins.GetPoffins();
                foreach (var poffin in poffins)
                {
                    poffin.MstID = BdspFillMstId;
                    poffin.Level = BdspFillLevel;
                    poffin.Taste = byte.MaxValue;
                    poffin.FlavorSpicy = poffin.FlavorDry = poffin.FlavorSweet = poffin.FlavorBitter = poffin.FlavorSour = byte.MaxValue;
                }
                sav.Poffins.SetPoffins(poffins);
                break;
            }
            case SAV3 { LargeBlock: ISaveBlock3LargeHoenn hoenn }:
            {
                var blocks = hoenn.PokeBlocks;
                blocks.MaximizeAll(true);
                hoenn.PokeBlocks = blocks;
                break;
            }
            case SAV6AO sav:
                for (var i = 0; i < Contest6.CountBlock; i++)
                    sav.Contest.SetBlockCount(i, Contest6.MaxBlock);
                return Contest6.CountBlock;
            default:
                throw Unsupported();
        }
        return GetTreats(session).Count;
    }

    /// <summary>Empties the case (ORAS: every counter to 0).</summary>
    public static void DeleteAll(ISaveEngineSession session)
    {
        switch (Save(session))
        {
            case SAV4Sinnoh sav:
            {
                var box = new PoffinCase4(sav);
                box.DeleteAll();
                box.Save();
                break;
            }
            case SAV8BS sav:
            {
                var poffins = sav.Poffins.GetPoffins();
                foreach (var poffin in poffins)
                    poffin.ToNull();
                sav.Poffins.SetPoffins(poffins);
                break;
            }
            case SAV3 { LargeBlock: ISaveBlock3LargeHoenn hoenn }:
            {
                var blocks = hoenn.PokeBlocks;
                blocks.DeleteAll();
                hoenn.PokeBlocks = blocks;
                break;
            }
            case SAV6AO sav:
                for (var i = 0; i < Contest6.CountBlock; i++)
                    sav.Contest.SetBlockCount(i, 0);
                break;
            default:
                throw Unsupported();
        }
    }

    /// <summary>Deletes one treat, the way the games' "throw away" leaves an empty slot.</summary>
    public static void Delete(ISaveEngineSession session, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Math.Max(1, Capacity(session)));
        switch (Save(session))
        {
            case SAV4Sinnoh sav:
            {
                var box = new PoffinCase4(sav);
                box.Poffins[slot].Delete();
                box.Save();
                break;
            }
            case SAV8BS sav:
            {
                var poffins = sav.Poffins.GetPoffins();
                poffins[slot].ToNull();
                sav.Poffins.SetPoffins(poffins);
                break;
            }
            case SAV3 { LargeBlock: ISaveBlock3LargeHoenn hoenn }:
            {
                var blocks = hoenn.PokeBlocks;
                blocks.Blocks[slot].Delete();
                hoenn.PokeBlocks = blocks;
                break;
            }
            default:
                throw Unsupported();
        }
    }

    /// <summary>PKHeX deletes a poffin as type None; a zero-filled slot (level 0) is empty too, no cooked poffin has every stat at 0.</summary>
    private static bool IsPoffin(Poffin4 poffin) => poffin.Type < PoffinFlavor4.FLAVOR_MAX && (poffin.Level != 0 || poffin.Smoothness != 0);

    private static string Name(string[] names, int index, string fallback) =>
        (uint)index < (uint)names.Length && !string.IsNullOrWhiteSpace(names[index]) ? names[index] : fallback;

    private static NotSupportedException Unsupported() =>
        new("This game has no poffin case or Pokéblock case.");

    private static SaveFile? Save(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile : null;
}
