using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One regimen's two best-time records in the save (time 0 = no record).</summary>
public sealed record SuperTrainingRecord(int Index, string Stage, bool Unlocked,
    float Time1, ushort Species1, string Holder1, float Time2, ushort Species2, string Holder2)
{
    public bool HasRecord => Time1 > 0 || Time2 > 0 || Species1 != 0 || Species2 != 0;
}

/// <summary>The training bags held in the save's 12 bag slots.</summary>
public sealed record SuperTrainingBag(int Slot, int Bag, string Name);

public sealed record SuperTrainingInfo(int StagesUnlocked, int StageCount, IReadOnlyList<SuperTrainingBag> Bags,
    IReadOnlyList<SuperTrainingRecord> Records);

/// <summary>A Pokémon's super training medals.</summary>
/// <param name="CanTrain">False for eggs and Pokémon that never visited a Gen 6 game: any medal would be illegal.</param>
/// <param name="HasSecretFlags">Only a Gen 6 Pokémon stores the Secret Super Training flags (a Gen 7 transfer wipes them).</param>
public sealed record MonSuperTraining(bool CanTrain, int Regular, int Secret, bool SecretUnlocked, bool SupremelyTrained,
    bool HasSecretFlags)
{
    public const int RegularCount = 18;
    public const int SecretCount = 12;
    public int Medals => Regular + Secret;
}

/// <summary>
/// Super Training for X/Y and Omega Ruby/Alpha Sapphire, after PKHeX's WinForms
/// <c>SAV_SuperTrain</c> (save: training bags, regimen unlocks, best-time records in
/// <c>SuperTrainBlock</c>) and <c>SuperTrainingEditor</c> (per Pokémon: the 30 medals of
/// <c>ISuperTrainRegimen</c>, and on PK6 the Secret flags). Medal writes follow PKHeX's
/// <c>MedalVerifier</c>: distribution medals are never set, nothing is given to an egg or a
/// Pokémon that never visited Gen 6, and "Supremely trained" goes with every medal, as
/// <c>RibbonRules.IsSuperTrainSupremelyTrained</c> requires.
/// </summary>
public static class SuperTrainingService
{
    public const int BagSlots = 12;

    /// <summary>SAV_SuperTrain lists the 32 released regimens (the block has room for 48).</summary>
    public const int StageCount = 32;

    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));

    // ── Save: bags, unlocks, records ──

    public static bool IsSupported(ISaveEngineSession session) => TryGetBlock(session) is not null;

    public static SuperTrainingInfo Get(ISaveEngineSession session)
    {
        var block = RequireBlock(session);
        var strings = Strings.Value;
        var bags = Enumerable.Range(0, BagSlots).Select(i => new SuperTrainingBag(i, block.GetBag(i), BagName(block.GetBag(i)))).ToList();
        var records = Enumerable.Range(0, StageCount).Select(i =>
        {
            var h1 = block.GetHolder1(i);
            var h2 = block.GetHolder2(i);
            return new SuperTrainingRecord(i, i < strings.trainingstage.Length ? strings.trainingstage[i] : $"Regimen {i + 1}",
                block.GetIsRegimenUnlocked(i), block.GetTime1(i), h1.Species, SpeciesLabel(h1.Species),
                block.GetTime2(i), h2.Species, SpeciesLabel(h2.Species));
        }).ToList();
        return new SuperTrainingInfo(records.Count(r => r.Unlocked), StageCount, bags, records);
    }

    /// <summary>Bag names by id (index 0 is no bag), for a picker.</summary>
    public static IReadOnlyList<string> BagNames =>
        [.. Enumerable.Range(0, Strings.Value.trainingbags.Length).Select(BagName)];

    /// <summary>Puts a bag in the first empty slot; false when all twelve are full.</summary>
    public static bool AddBag(ISaveEngineSession session, int bag)
    {
        var block = RequireBlock(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(bag, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bag, Strings.Value.trainingbags.Length);
        // SuperTrainBlock.GetOpenBagIndex returns the first *filled* slot, so look for the empty one here.
        for (var i = 0; i < BagSlots; i++)
        {
            if (block.GetBag(i) != 0) continue;
            block.SetBag(i, (byte)bag);
            return true;
        }
        return false;
    }

    /// <summary>Removes one bag; the ones after it move up, as SAV_SuperTrain compacts on save.</summary>
    public static void RemoveBag(ISaveEngineSession session, int slot)
    {
        var block = RequireBlock(session);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, BagSlots);
        block.RemoveBag(slot);
    }

    /// <summary>SuperTrainBlock.UnlockAllStages(false): every released regimen, never the distribution ones.</summary>
    public static void UnlockAllStages(ISaveEngineSession session) => RequireBlock(session).UnlockAllStages(false);

    /// <summary>Clears both best-time records (time and holder) of one regimen.</summary>
    public static void ClearRecord(ISaveEngineSession session, int index)
    {
        var block = RequireBlock(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, StageCount);
        block.ClearRecord1(index);
        block.ClearRecord2(index);
    }

    // ── Pokémon: medals ──

    /// <summary>Null when this Pokémon format has no super training data (anything but PK6/PK7).</summary>
    public static MonSuperTraining? GetMedals(ISaveEngineSession session, int box, int slot)
    {
        if (session is not SaveEngineSession engine) return null;
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0 || pk is not ISuperTrainRegimen regimen) return null;
        var regular = Enumerable.Range(0, MonSuperTraining.RegularCount).Count(regimen.GetRegimenState);
        var secret = Enumerable.Range(MonSuperTraining.RegularCount, MonSuperTraining.SecretCount).Count(regimen.GetRegimenState);
        return new MonSuperTraining(CanTrain(pk), regular, secret, regimen.SecretSuperTrainingUnlocked,
            regimen.SuperTrainSupremelyTrained, pk is PK6);
    }

    /// <summary>The 18 regular medals; on a Gen 6 Pokémon also unlocks Secret Super Training.</summary>
    public static MonSuperTraining CompleteRegular(ISaveEngineSession session, int box, int slot) =>
        Edit(session, box, slot, (pk, regimen) =>
        {
            RequireTrainable(pk);
            for (var i = 0; i < MonSuperTraining.RegularCount; i++)
                regimen.SetRegimenState(i, true);
            SyncSecretFlags(pk, regimen, unlock: true);
        });

    /// <summary>Every medal, secret ones included; on a Gen 6 Pokémon this is "Supremely trained".</summary>
    public static MonSuperTraining CompleteAll(ISaveEngineSession session, int box, int slot) =>
        Edit(session, box, slot, (pk, regimen) =>
        {
            RequireTrainable(pk);
            for (var i = 0; i < SuperTrainRegimenExtensions.CountRegimen; i++)
                regimen.SetRegimenState(i, true);
            SyncSecretFlags(pk, regimen, unlock: true);
        });

    /// <summary>No medals, no distribution medals, no Secret flags.</summary>
    public static MonSuperTraining ClearAll(ISaveEngineSession session, int box, int slot) =>
        Edit(session, box, slot, (_, regimen) =>
        {
            for (var i = 0; i < SuperTrainRegimenExtensions.CountRegimen; i++)
                regimen.SetRegimenState(i, false);
            for (var i = 0; i < SuperTrainRegimenExtensions.CountRegimenDistribution; i++)
                regimen.SetRegimenStateDistribution(i, false);
            regimen.SecretSuperTrainingUnlocked = false;
            regimen.SuperTrainSupremelyTrained = false;
        });

    /// <summary>MedalVerifier: no medals on an egg, nor on a Pokémon from Gen 1-2 or Gen 7+ games.</summary>
    private static bool CanTrain(PKM pk) => !pk.IsEgg && pk.Generation is >= 3 and <= 6;

    private static void RequireTrainable(PKM pk)
    {
        if (!CanTrain(pk))
            throw new InvalidOperationException(pk.IsEgg
                ? "An egg can't have super training medals."
                : "This Pokémon never visited X, Y, Omega Ruby or Alpha Sapphire, so it can't have super training medals.");
    }

    /// <summary>A Gen 7 transfer wipes the Secret flags; on PK6 "Supremely trained" means every medal.</summary>
    private static void SyncSecretFlags(PKM pk, ISuperTrainRegimen regimen, bool unlock)
    {
        if (pk is not PK6)
        {
            regimen.SecretSuperTrainingUnlocked = false;
            regimen.SuperTrainSupremelyTrained = false;
            return;
        }
        if (unlock) regimen.SecretSuperTrainingUnlocked = true;
        regimen.SuperTrainSupremelyTrained = RibbonRules.IsSuperTrainSupremelyTrained(regimen.SuperTrainBitFlags);
    }

    private static MonSuperTraining Edit(ISaveEngineSession session, int box, int slot, Action<PKM, ISuperTrainRegimen> edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine)
            throw new NotSupportedException("Super training is only editable on PKHeX-backed saves.");
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0) throw new InvalidOperationException("Cannot edit an empty slot.");
        if (pk is not ISuperTrainRegimen regimen)
            throw new NotSupportedException("Only Gen 6 and Gen 7 Pokémon store super training medals.");
        edit(pk, regimen);
        // The session's surgical slot write: no dex, record or handler side effects.
        pk.RefreshChecksum();
        var save = engine.SaveFile;
        if (box == -1) save.SetPartySlotAtIndex(pk, slot, EntityImportSettings.None);
        else save.SetBoxSlotAtIndex(pk, box, slot, EntityImportSettings.None);
        return GetMedals(session, box, slot)!;
    }

    private static string BagName(int bag)
    {
        var names = Strings.Value.trainingbags;
        if (bag == 0) return "—";
        return bag < names.Length && names[bag].Length != 0 ? names[bag] : $"Bag {bag}";
    }

    private static string SpeciesLabel(ushort species) =>
        species == 0 ? "—" : SpeciesName.GetSpeciesName(species, (int)LanguageID.English);

    private static SuperTrainBlock? TryGetBlock(ISaveEngineSession session) =>
        session is SaveEngineSession engine && engine.SaveFile is ISaveBlock6Main main ? main.SuperTrain : null;

    private static SuperTrainBlock RequireBlock(ISaveEngineSession session) =>
        TryGetBlock(session) ?? throw new NotSupportedException("Super Training exists only in X, Y, Omega Ruby and Alpha Sapphire saves.");
}
