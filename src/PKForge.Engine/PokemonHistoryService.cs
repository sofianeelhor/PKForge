using System.Diagnostics.CodeAnalysis;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A transfer the history will remember once the save that took it is written: the copy
/// that left, the copy that landed, and every identity the two carry.</summary>
public sealed record HistoryTripPlan(IReadOnlyList<string> Souls, int Species, HistoryCopy Departed, string ArrivedGame, bool Restored)
{
    /// <summary>The copy as it landed (PKHeX stamps a received Pokémon as it places it).</summary>
    public HistoryCopy Arrived(PKM landed, DateTimeOffset at) =>
        HistoryCopy.Of(EntityBytes.FormatOf(landed), ArrivedGame, at, landed.Data.ToArray());

    public IReadOnlyList<string> SoulsWith(PKM landed) =>
        [.. Souls.Append(PokemonHistoryService.Soul(landed)).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Pokémon History, the opt-in that makes a trip to an older format reversible. A downgrade keeps
/// what the older format can hold and drops the rest (hidden ability, met location, ribbons, the
/// PID…); history keeps the exact copy the Pokémon had in every format it left. When it comes back
/// to a format it has been in, that copy is the starting point and only what changed through play
/// since it left is laid on top: level and experience, moves, EVs, nickname, evolution, friendship
/// and newly earned ribbons, always as the plain conversion already put them into the target's
/// format (so every value fits the target), plus the held item it carries now (never a copy of
/// the old one). Nothing new is ever made: it is only the Pokémon's own data, so Hardcore mode keeps it.
/// <para>
/// Gen 1-2 records are left out: they have no PID, so every Pokémon of one trainer shares an
/// identity there. A saved copy is used only when it is the same Pokémon: same evolution family and,
/// from Gen 3 on, the same IVs.
/// </para>
/// </summary>
public static class PokemonHistoryService
{
    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));

    /// <summary>The identity history keys a Pokémon by (the Poképark's, from its own data).</summary>
    public static string Soul(PKM pk) =>
        ParkIdentity.Soul(pk.PID, pk.Format >= 6 ? pk.EncryptionConstant : null, pk.OriginalTrainerName, pk.TID16);

    /// <summary>True for the Pokémon history can follow: hatched, Gen 3 format or later.</summary>
    public static bool Covers(PKM pk) => pk is { Species: > 0, IsEgg: false, Format: >= 3 };

    /// <summary>
    /// The transfer of <paramref name="incoming"/> into <paramref name="target"/> with history: the
    /// conversion as it was when nothing is saved for the target's format, else the saved copy with
    /// play laid on top (its notes replace the downgrade's warnings, which no longer apply). The plan
    /// is what to remember once the transfer is real; null when the format does not change or history
    /// does not cover the Pokémon. Reads <paramref name="store"/> only: nothing is written here.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "PKM subclasses are preserved: PKHeX.Core is consumed whole; ribbons are read by reflection like the transfer preview does.")]
    public static (TransferConversion Conversion, HistoryTripPlan? Trip) Apply(
        IPokemonHistoryStore store, PKM incoming, TransferConversion conversion, SaveFile target)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(conversion);
        ArgumentNullException.ThrowIfNull(target);
        var plain = conversion.Entity;
        if (incoming.GetType() == plain.GetType() || !Covers(incoming) || !Covers(plain))
            return (conversion, null);

        var souls = new[] { Soul(incoming), Soul(plain) }.Distinct(StringComparer.Ordinal).ToArray();
        var record = store.Find(souls);
        var fromFormat = EntityBytes.FormatOf(incoming);
        var fromGame = record?.ArrivalFor(fromFormat)?.Game ?? GameOf(incoming);
        var toGame = GameInfo.GetVersionName(target.Version);
        if (string.IsNullOrEmpty(toGame)) toGame = $"a Gen {target.Generation} game";
        var departed = HistoryCopy.Of(fromFormat, fromGame, default, incoming.Data.ToArray());

        var saved = record?.DepartureFor(EntityBytes.FormatOf(plain));
        var old = saved is null ? null : EntityBytes.Parse(saved.Bytes(), saved.Format);
        if (record is null || saved is null || old is null || old.GetType() != plain.GetType() || !SameMon(old, plain))
            return (conversion, new HistoryTripPlan(souls, plain.Species, departed, toGame, false));

        var baseline = record.ArrivalFor(fromFormat) is { } arrival ? EntityBytes.Parse(arrival.Bytes(), arrival.Format) : null;
        if (baseline is not null && baseline.GetType() != incoming.GetType()) baseline = null;
        var restored = Restore(old, plain, incoming, baseline, target, saved.Game, fromGame, out var notes);
        var landed = new TransferConversion(restored, conversion.Backwards, notes);
        return (landed, new HistoryTripPlan([.. souls, .. record.Souls], restored.Species, departed, toGame, true));
    }

    /// <summary>Writes a trip to the store: the record gains the copy that left, the copy that landed
    /// and the trip itself (a new record when this is the Pokémon's first).</summary>
    public static PokemonHistoryRecord Commit(IPokemonHistoryStore store, HistoryTripPlan trip, PKM landed, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(landed);
        var souls = trip.SoulsWith(landed);
        return store.Update(souls, record => (record ?? PokemonHistoryRecord.Start(souls, trip.Species))
            .WithTrip(souls, landed.Species, trip.Departed with { At = at }, trip.Arrived(landed, at), trip.Restored));
    }

    /// <summary>The game a copy comes from when no trip names it: its origin game when it is still in
    /// that game's format, else its generation.</summary>
    public static string GameOf(PKM pk)
    {
        var name = pk.Generation == pk.Format ? GameInfo.GetVersionName(pk.Version) : null;
        return string.IsNullOrEmpty(name) ? $"a Gen {pk.Format} game" : name;
    }

    /// <summary>The saved copy and the arrival are one Pokémon: same family and, from Gen 3, same IVs.</summary>
    private static bool SameMon(PKM old, PKM plain)
    {
        var tree = EvolutionTree.GetEvolutionTree(plain.Context);
        if (tree.GetBaseSpeciesForm(old.Species, old.Form).Species != tree.GetBaseSpeciesForm(plain.Species, plain.Form).Species)
            return false;
        Span<int> a = stackalloc int[6];
        Span<int> b = stackalloc int[6];
        old.GetIVs(a);
        plain.GetIVs(b);
        return a.SequenceEqual(b);
    }

    [RequiresUnreferencedCode("Reads and writes ribbons by reflection.")]
    private static PKM Restore(PKM old, PKM plain, PKM now, PKM? baseline, ITrainerInfo receiver, string oldGame, string nowGame, out IReadOnlyList<string> notes)
    {
        var restored = old.Clone();
        var kept = new List<string>();

        // Evolution: the species follows play, the ability stays in its slot (a hidden one stays hidden).
        var evolved = baseline is null ? plain.Species != old.Species : now.Species != baseline.Species || now.Form != baseline.Form;
        if (evolved && (plain.Species != old.Species || plain.Form != old.Form))
        {
            var slot = old.AbilityNumber switch { 4 => 2, 2 => 1, _ => 0 };
            restored.Species = plain.Species;
            restored.Form = plain.Form;
            restored.RefreshAbility(slot);
            if (!restored.IsNicknamed) restored.ClearNickname();
            kept.Add($"evolved into {Name(plain.Species)}");
        }

        // Level and experience never go down.
        if (plain.CurrentLevel > restored.CurrentLevel)
        {
            restored.CurrentLevel = plain.CurrentLevel;
            kept.Add($"level {plain.CurrentLevel}");
        }
        if (plain.CurrentLevel == restored.CurrentLevel && plain.EXP > restored.EXP)
            restored.EXP = plain.EXP;

        var movesChanged = baseline is not null && !Moves(now).SequenceEqual(Moves(baseline));
        if (movesChanged && Moves(plain).Any(m => m != 0))
        {
            SetMoves(restored, plain);
            kept.Add("new moves");
        }
        if (baseline is not null && !Evs(now).SequenceEqual(Evs(baseline)))
        {
            restored.EV_HP = plain.EV_HP; restored.EV_ATK = plain.EV_ATK; restored.EV_DEF = plain.EV_DEF;
            restored.EV_SPA = plain.EV_SPA; restored.EV_SPD = plain.EV_SPD; restored.EV_SPE = plain.EV_SPE;
            kept.Add("EVs");
        }
        if (baseline is not null && (now.Nickname != baseline.Nickname || now.IsNicknamed != baseline.IsNicknamed))
        {
            restored.IsNicknamed = plain.IsNicknamed;
            restored.Nickname = plain.Nickname;
            if (!restored.IsNicknamed) restored.ClearNickname();
            kept.Add(plain.IsNicknamed ? $"nickname {plain.Nickname}" : "nickname cleared");
        }
        if (plain.OriginalTrainerFriendship > restored.OriginalTrainerFriendship)
            restored.OriginalTrainerFriendship = plain.OriginalTrainerFriendship;

        // The item it holds now travels with it; the one it held back then was given away or kept in the other game.
        if (restored.HeldItem != plain.HeldItem)
        {
            restored.HeldItem = plain.HeldItem;
            kept.Add(plain.HeldItem == 0 ? "no held item" : "its held item");
        }

        var gained = GainRibbons(restored, plain);
        if (gained > 0) kept.Add(gained == 1 ? "1 new ribbon" : $"{gained} new ribbons");

        // The copy saved back then names the handler of that save; the one taking it in now holds it.
        CrossFormatConverter.ReceivedBy(restored, receiver);
        restored.ResetPartyStats();
        restored.HealPP();
        restored.RefreshChecksum();

        // A move learned over there may not exist on this side of the line: the old moves stay then.
        if (movesChanged && !new LegalityAnalysis(restored).Valid && new LegalityAnalysis(old).Valid)
        {
            var oldMoves = restored.Clone();
            SetMoves(oldMoves, old);
            oldMoves.HealPP();
            oldMoves.RefreshChecksum();
            if (new LegalityAnalysis(oldMoves).Valid)
            {
                restored = oldMoves;
                kept.Remove("new moves");
                kept.Add("its old moves (the new ones would not be legal here)");
            }
        }

        var lines = new List<string> { $"Restored from your {oldGame} data: {string.Join(", ", WhatCameBack(old, plain))}." };
        if (kept.Count > 0) lines.Add($"Kept from {nowGame}: {string.Join(", ", kept)}.");
        lines.Add(new LegalityAnalysis(restored).Valid
            ? "Legality re-checked: legal."
            : "Legality re-checked: it would be flagged (the copy saved back then may already have been).");
        notes = lines;
        return restored;
    }

    /// <summary>What the saved copy brings back that the plain conversion would have rewritten.</summary>
    private static List<string> WhatCameBack(PKM old, PKM plain)
    {
        var back = new List<string>();
        if (old.Ability != plain.Ability)
            back.Add($"{(old.AbilityNumber == 4 ? "Hidden Ability " : "ability ")}{Label(Strings.Value.abilitylist, old.Ability)}");
        if (old.PID != plain.PID) back.Add("its original PID");
        if (old.IsShiny && !plain.IsShiny) back.Add("its shininess");
        if (old.MetLocation != plain.MetLocation || old.Version != plain.Version)
        {
            var met = LocationNames.Of(old, old.MetLocation, false);
            back.Add(string.IsNullOrEmpty(met) ? "where it was met" : $"met in {met}");
        }
        if (old.Ball != plain.Ball) back.Add($"{Label(Strings.Value.balllist, old.Ball)}");
        var ribbons = RibbonNames(old).Except(RibbonNames(plain)).Count();
        if (ribbons > 0) back.Add(ribbons == 1 ? "1 ribbon" : $"{ribbons} ribbons");
        back.Add("everything else it had there");
        return back;
    }

    [RequiresUnreferencedCode("Reads and writes ribbons by reflection.")]
    private static int GainRibbons(PKM restored, PKM plain)
    {
        var gained = 0;
        var mine = RibbonInfo.GetRibbonInfo(restored).ToDictionary(r => r.Name);
        foreach (var ribbon in RibbonInfo.GetRibbonInfo(plain))
        {
            if (!mine.TryGetValue(ribbon.Name, out var have)) continue;
            if (ribbon.Type == RibbonValueType.Boolean && ribbon.HasRibbon && !have.HasRibbon)
            {
                if (ReflectUtil.SetValue(restored, ribbon.Name, true)) gained++;
            }
            else if (ribbon.Type == RibbonValueType.Byte && ribbon.RibbonCount > have.RibbonCount)
            {
                if (ReflectUtil.SetValue(restored, ribbon.Name, ribbon.RibbonCount)) gained++;
            }
        }
        return gained;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "PKHeX.Core is consumed whole.")]
    private static IEnumerable<string> RibbonNames(PKM pk) =>
        RibbonInfo.GetRibbonInfo(pk).Where(r => r.HasRibbon || r.RibbonCount > 0).Select(r => r.Name);

    private static void SetMoves(PKM into, PKM from)
    {
        into.Move1 = from.Move1; into.Move2 = from.Move2; into.Move3 = from.Move3; into.Move4 = from.Move4;
        into.Move1_PPUps = from.Move1_PPUps; into.Move2_PPUps = from.Move2_PPUps;
        into.Move3_PPUps = from.Move3_PPUps; into.Move4_PPUps = from.Move4_PPUps;
    }

    private static ushort[] Moves(PKM pk) => [pk.Move1, pk.Move2, pk.Move3, pk.Move4];

    private static int[] Evs(PKM pk) => [pk.EV_HP, pk.EV_ATK, pk.EV_DEF, pk.EV_SPA, pk.EV_SPD, pk.EV_SPE];

    private static string Name(ushort species) => Label(Strings.Value.specieslist, species);

    private static string Label(string[] list, int index) => (uint)index < (uint)list.Length ? list[index] : $"#{index}";
}
