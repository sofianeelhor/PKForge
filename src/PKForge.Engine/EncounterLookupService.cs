using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Answers "how do I get this?" for every mainline game, with no open save involved:
/// each game's own blank save supplies the format, personal table, and trainer context
/// the pinned encounter generator needs. Results are cached per species line because
/// enumerating ~20 games is real work and the answer never changes.
/// </summary>
public sealed class EncounterLookupService : IEncounterLookup
{
    /// <summary>Mainline games, oldest first, one row per playable game. Side games
    /// (Colosseum/XD/GO) and the unreleased placeholders are deliberately absent.</summary>
    private static readonly GameVersion[] MainlineGames =
    [
        GameVersion.RD, GameVersion.BU, GameVersion.YW,
        GameVersion.GD, GameVersion.SI, GameVersion.C,
        GameVersion.R, GameVersion.S, GameVersion.E,
        GameVersion.FR, GameVersion.LG,
        GameVersion.D, GameVersion.P, GameVersion.Pt,
        GameVersion.HG, GameVersion.SS,
        GameVersion.B, GameVersion.W, GameVersion.B2, GameVersion.W2,
        GameVersion.X, GameVersion.Y, GameVersion.OR, GameVersion.AS,
        GameVersion.SN, GameVersion.MN, GameVersion.US, GameVersion.UM,
        GameVersion.GP, GameVersion.GE,
        GameVersion.SW, GameVersion.SH,
        GameVersion.BD, GameVersion.SP,
        GameVersion.PLA,
        GameVersion.SL, GameVersion.VL,
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<(int Species, int Form), IReadOnlyList<GameEncounterListing>> _cache = [];
    private readonly Dictionary<GameVersion, SaveFile> _blankSaves = [];

    public IReadOnlyList<GameEncounterListing> Describe(int species, int form)
    {
        if (species < 1)
            return [];
        lock (_gate)
        {
            if (_cache.TryGetValue((species, form), out var cached)) return cached;
        }

        var listings = new List<GameEncounterListing>();
        foreach (var version in MainlineGames)
        {
            var save = GetBlankSave(version);
            if (save is null) continue;
            if (species > save.MaxSpeciesID) continue;

            var cards = EncounterDatabase.Describe(save, species, form, version);
            listings.Add(new GameEncounterListing(GameInfo.GetVersionName(version), version.Generation, cards.Count > 0, cards));
        }

        lock (_gate)
        {
            if (_cache.Count > 64) _cache.Clear();
            _cache[(species, form)] = listings;
            return listings;
        }
    }

    /// <summary>Blank saves are reused: building one per game is the expensive part,
    /// and the generator only reads format, personal data, and trainer identity.</summary>
    private SaveFile? GetBlankSave(GameVersion version)
    {
        lock (_gate)
        {
            if (_blankSaves.TryGetValue(version, out var cached)) return cached;
        }

        SaveFile? blank;
        try
        {
            blank = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        }
        catch
        {
            return null; // no template for this version: skip the game entirely
        }

        lock (_gate)
        {
            _blankSaves[version] = blank;
            return blank;
        }
    }
}
