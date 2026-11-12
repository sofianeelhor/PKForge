using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Reads what the Origin Dex and the Extended Living Dex need from PKHeX: each Pokémon's origin
/// game and visible traits (gender, Gigantamax, Alcremie sweet), and every species' collectible
/// forms with their category. Read-only.
/// </summary>
public static class DexModeSources
{
    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));
    private static readonly Lazy<IReadOnlyList<DexSpeciesForms>> CachedCatalog = new(BuildCatalog);

    /// <summary>One entity as a <see cref="DexMon"/>. Origin generation is PKHeX's
    /// <see cref="PKM.Generation"/> (VC counts as Gen I/II); Pokémon GO and unknown are 0.</summary>
    public static DexMon FromEntity(PKM pk, DexHolder where)
    {
        var go = pk.Version is GameVersion.GO;
        var generation = go ? 0 : pk.Generation;
        var games = Strings.Value.gamelist;
        var game = go ? "Pokémon GO" : (uint)pk.Version < (uint)games.Length ? games[(int)pk.Version] : "";
        if (pk.Format <= 2) game = pk.Format == 1 ? "Red / Blue / Yellow" : "Gold / Silver / Crystal";
        return new DexMon(pk.Species, pk.Form, pk.IsShiny, where, generation, game,
            Female: pk.Gender == 1,
            Gigantamax: pk is IGigantamaxReadOnly { CanGigantamax: true },
            FormArgument: pk.Species == (ushort)PKHeX.Core.Species.Alcremie && pk is IFormArgument arg ? (int)Math.Min(arg.FormArgument, 6u) : 0,
            IsEgg: pk.IsEgg);
    }

    /// <summary>Every Bank entry (unreadable bytes are skipped).</summary>
    public static List<DexMon> FromBank(IBankService bank)
    {
        var list = new List<DexMon>();
        foreach (var entry in bank.GetAll())
        {
            if (entry.Info.Species <= 0) continue;
            var where = new DexHolder(LivingDexPlanner.BankId, "Bank", entry.Box, entry.Slot, entry.Id);
            PKM? pk;
            try { pk = EntityBytes.Parse(entry, bank.GetData(entry.Id)); }
            catch (Exception) { pk = null; }
            list.Add(pk is not null ? FromEntity(pk, where)
                : new DexMon(entry.Info.Species, entry.Info.Form, entry.Info.Shiny, where,
                    Female: entry.Info.Traits?.Female ?? false, FormArgument: entry.Info.Traits?.FormArgument ?? 0));
        }
        return list;
    }

    /// <summary>Every Pokémon of an open save, party included. ROM-hack sessions have no PKHeX
    /// entity view: their Pokémon count for the Extended dex but have no known origin.</summary>
    public static List<DexMon> FromSession(ISaveEngineSession session, string sourceId, string label)
    {
        var list = new List<DexMon>();
        if (session is SaveEngineSession engine)
        {
            var save = engine.SaveFile;
            for (var slot = 0; slot < save.PartyCount; slot++) Add(save.GetPartySlotAtIndex(slot), -1, slot);
            for (var box = 0; box < save.BoxCount; box++)
                for (var slot = 0; slot < save.BoxSlotCount; slot++)
                    Add(save.GetBoxSlotAtIndex(box, slot), box, slot);
            return list;
        }
        foreach (var slot in session.Snapshot.Slots)
            if (slot.Species is > 0)
                list.Add(new DexMon(slot.Species.Value, slot.Form, slot.IsShiny, new DexHolder(sourceId, label, slot.Box, slot.Slot),
                    Female: slot.Traits.Female, FormArgument: slot.Traits.FormArgument, IsEgg: slot.IsEgg));
        return list;

        void Add(PKM pk, int box, int slot)
        {
            if (pk.Species == 0) return;
            list.Add(FromEntity(pk, new DexHolder(sourceId, label, box, slot)));
        }
    }

    /// <summary>Every species with its collectible forms (the autopilot's catalog), which of them are
    /// regional, its visible gender difference, Gigantamax looks and Alcremie's sweets.</summary>
    public static IReadOnlyList<DexSpeciesForms> Catalog() => CachedCatalog.Value;

    private static readonly string[] Regions = ["Alola", "Galar", "Hisui", "Paldea"];

    private static IReadOnlyList<DexSpeciesForms> BuildCatalog()
    {
        var living = LivingDexCatalogBuilder.Build();
        var list = new List<DexSpeciesForms>(living.Species.Count);
        foreach (var species in living.Species)
        {
            var forms = living.FormsOf(species).Select(form =>
            {
                var name = LivingDexCatalogBuilder.FormName(species, form);
                return new DexFormInfo(form, name, form > 0 && Regions.Any(r => name.Contains(r, StringComparison.OrdinalIgnoreCase)));
            }).ToList();
            var id = (ushort)species;
            // One Gigantamax look per species; Urshifu's two styles each have their own.
            int[]? gmax = !Gigantamax.CanToggle(id, 0) ? null
                : species == (int)PKHeX.Core.Species.Urshifu ? [0, 1] : [0];
            list.Add(new DexSpeciesForms(species, forms, IsGenderDifferent(species), gmax,
                species == (int)PKHeX.Core.Species.Alcremie ? 7 : 0));
        }
        return list;
    }

    /// <summary>Species whose female looks different, PKHeX's QR7 table (complete through Gen VII;
    /// later gender differences - Indeedee, Basculegion, Oinkologne - are forms).</summary>
    public static bool IsGenderDifferent(int species)
    {
        ReadOnlySpan<byte> table =
        [
            0x08, 0x10, 0x18, 0x06, 0x00, 0x36, 0x00, 0x00, 0x03, 0x00,
            0x30, 0x00, 0x02, 0x80, 0xC1, 0x08, 0x06, 0x00, 0x00, 0x04,
            0x60, 0x00, 0x04, 0x46, 0x4C, 0x8C, 0xD1, 0x22, 0x21, 0x01,
            0x00, 0x80, 0x03, 0x28, 0x0D, 0x00, 0x00, 0x00, 0x18, 0x38,
            0x0C, 0x10, 0x00, 0x40, 0x00, 0x00, 0x02, 0x00, 0x00, 0xF0,
            0xBF, 0x80, 0x0E, 0x01, 0x00, 0x38, 0x66, 0x3B, 0x03, 0x02,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x10, 0x40,
        ];
        var index = species >> 3;
        return species > 0 && index < table.Length && (table[index] & (1 << (species & 7))) != 0;
    }
}
