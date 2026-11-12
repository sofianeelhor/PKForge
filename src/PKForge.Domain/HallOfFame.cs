namespace PKForge.Domain;

// Hall of Fame records as the save keeps them. What each generation stores differs a lot:
// Gen 1 keeps species, level and nickname for up to 50 teams plus a clear counter; Gen 3
// adds PID and trainer IDs; Gen 6 stores full cards (OT, held item, moves, form, shiny) with a
// date and clear number for the first clear and the 15 most recent; Gen 7 keeps species only
// for the first and the latest team. Capabilities say which fields a screen should show.

/// <summary>What a game's Hall of Fame stores and lets PKForge edit.</summary>
/// <param name="TeamSlots">Storage slots (Gen 1/3: 50, Gen 6: 16, Gen 7: 2).</param>
/// <param name="CanDeleteTeams">Deleting a team shifts the later ones up, as the game's own list does.</param>
/// <param name="CanRecordParty">The current party can be written in as a new entry.</param>
public sealed record HallOfFameCapabilities(
    int TeamSlots, int MembersPerTeam, int NicknameMaxLength,
    bool StoresLevel, bool StoresNickname, bool StoresTrainer, bool StoresPid,
    bool StoresMovesAndItem, bool StoresFormGenderShiny,
    bool StoresDate, bool StoresClearNumber, bool StoresClearCount,
    bool CanDeleteTeams, bool CanRecordParty);

/// <summary>One Pokémon in a Hall of Fame team. Fields the game does not store are null.</summary>
public sealed record HallOfFameMember(
    int Slot, ushort Species, string SpeciesName,
    int? Level, string? Nickname, bool? IsNicknamed,
    string? OriginalTrainer, int? OriginalTrainerGender, ushort? TrainerId, ushort? SecretId,
    uint? Pid, int? Form, int? Gender, bool? IsShiny,
    int? HeldItem, IReadOnlyList<ushort> Moves);

/// <summary>A team as stored in slot <paramref name="Index"/>; empty slots are not listed.</summary>
/// <param name="Label">"Clear #12", "First clear", "Latest"...</param>
public sealed record HallOfFameTeam(int Index, string Label, IReadOnlyList<HallOfFameMember> Members,
    DateOnly? Date, int? ClearNumber);

/// <param name="ClearCount">Times the game counts the champion as crowned (Gen 1 counter); null when not stored.</param>
public sealed record HallOfFameInfo(int Generation, HallOfFameCapabilities Capabilities,
    IReadOnlyList<HallOfFameTeam> Teams, int? ClearCount);

/// <summary>Replacement for one team member. Null keeps the stored value (or a sensible default
/// for an empty slot); fields the game does not store are ignored.</summary>
/// <param name="Nickname">Null on a Gen 6 write stores the species name, not nicknamed.</param>
public sealed record HallOfFameMemberEdit(
    ushort Species,
    int? Level = null,
    string? Nickname = null,
    string? OriginalTrainer = null,
    int? OriginalTrainerGender = null,
    ushort? TrainerId = null,
    ushort? SecretId = null,
    uint? Pid = null,
    int? Form = null,
    int? Gender = null,
    bool? IsShiny = null,
    int? HeldItem = null,
    IReadOnlyList<ushort>? Moves = null);
