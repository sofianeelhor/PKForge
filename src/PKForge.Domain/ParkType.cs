namespace PKForge.Domain;

/// <summary>
/// Canonical elemental type IDs (1–18) used across the park's habitats, encounters
/// and traversal rules. Values are the National Pokédex type order, deliberately
/// distinct from PKHeX's <c>MoveType</c> enum which is zero-based (Fire = 9).
/// 0 means "unknown/none" so unmatched or invalid forms never select a habitat.
/// </summary>
public static class ParkType
{
    public const int Unknown = 0;
    public const int Normal = 1;
    public const int Fighting = 2;
    public const int Flying = 3;
    public const int Poison = 4;
    public const int Ground = 5;
    public const int Rock = 6;
    public const int Bug = 7;
    public const int Ghost = 8;
    public const int Steel = 9;
    public const int Fire = 10;
    public const int Water = 11;
    public const int Grass = 12;
    public const int Electric = 13;
    public const int Psychic = 14;
    public const int Ice = 15;
    public const int Dragon = 16;
    public const int Dark = 17;
    public const int Fairy = 18;
    public const int Max = Fairy;

    /// <summary>Lift a zero-based PKHeX <c>MoveType</c> value into the canonical 1–18 range.</summary>
    public static int FromPkhex(int pkhexType) =>
        pkhexType < 0 || pkhexType > Max - 1 ? Unknown : pkhexType + 1;
}
