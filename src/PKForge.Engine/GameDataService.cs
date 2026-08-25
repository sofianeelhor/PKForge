using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Exposes the pinned engine's English display-name tables to the UI.</summary>
public sealed class GameDataService : IGameDataService
{
    private readonly GameStrings _strings = GameInfo.GetStrings("en");

    public IReadOnlyList<string> SpeciesNames => _strings.specieslist;
    public IReadOnlyList<string> MoveNames => _strings.movelist;
    public IReadOnlyList<string> ItemNames => _strings.itemlist;
    public IReadOnlyList<string> AbilityNames => _strings.abilitylist;
    public IReadOnlyList<string> NatureNames => _strings.natures;
    public IReadOnlyList<string> BallNames => _strings.balllist;
}
