using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Exposes the pinned engine's English display-name tables to the UI.</summary>
public sealed class GameDataService : IGameDataService
{
    private readonly GameStrings _strings = GameInfo.GetStrings("en");
    private IReadOnlyList<Domain.SpeciesFormFlags>? _formFlags;

    public IReadOnlyList<string> SpeciesNames => _strings.specieslist;
    public IReadOnlyList<string> MoveNames => _strings.movelist;
    public IReadOnlyList<string> ItemNames => _strings.itemlist;
    public IReadOnlyList<string> AbilityNames => _strings.abilitylist;
    public IReadOnlyList<string> NatureNames => _strings.natures;
    public IReadOnlyList<string> BallNames => _strings.balllist;

    public IReadOnlyList<Domain.SpeciesFormFlags> FormFlags => _formFlags ??= BuildFormFlags();

    /// <summary>
    /// Authoritative form facts: megas from PKHeX's battle-mega table, G-Max from the
    /// SwSh personal table's per-form bit, regional from the Gen-9 form name lists
    /// (which name regions but not megas), and any-forms as the union of all of it.
    /// </summary>
    private IReadOnlyList<Domain.SpeciesFormFlags> BuildFormFlags()
    {
        var flags = new Domain.SpeciesFormFlags[_strings.specieslist.Length];
        Array.Fill(flags, new Domain.SpeciesFormFlags(false, false, false, false));
        var swsh = PersonalTable.SWSH;
        for (var id = 1; id < flags.Length; id++)
        {
            if (_strings.specieslist[id].Length == 0)
                continue;
            var forms = FormConverter.GetFormList((ushort)id, _strings.Types, _strings.forms, EntityContext.Gen9);
            var mega = FormInfo.HasMegaForm((ushort)id);
            var regional = false;
            foreach (var form in forms)
            {
                if (form.Length == 0) continue;
                if (form.Contains("Alola", StringComparison.Ordinal) || form.Contains("Galar", StringComparison.Ordinal)
                    || form.Contains("Hisui", StringComparison.Ordinal) || form.Contains("Paldea", StringComparison.Ordinal)) regional = true;
            }
            var gmax = Domain.SpeciesCategories.GigantamaxCapable.Contains(id);
            var hasForms = forms.Any(form => form.Length > 0) || mega || gmax;
            if (!hasForms)
                continue;
            flags[id] = new Domain.SpeciesFormFlags(hasForms, mega, gmax, regional);
        }
        return flags;
    }
}
