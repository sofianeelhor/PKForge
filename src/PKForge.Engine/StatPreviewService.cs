using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Nature picker previews. Engine saves run PKHeX's own per-format stat calculation
/// (hyper training, Let's Go AVs and friendship, Legends: Arceus GVs, Gen 8+ mints) on a
/// clone with each nature applied the way <see cref="SaveEngineSession.ApplyEdit"/> would
/// apply it. Sessions without a PKHeX entity (romhacks) fall back to the plain Gen 3
/// formula over their own base stats.
/// </summary>
public sealed class StatPreviewService : IStatPreviewService
{
    public NatureStatPreview? PreviewSlot(ISaveEngineSession session, int box, int slot, StatPreviewOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Generation <= 2) return null;
        return session is SaveEngineSession engine
            ? PreviewEntity(engine.GetEntity(box, slot), overrides)
            : PreviewDetail(session, session.ReadEntity(box, slot), overrides);
    }

    public NatureStatPreview? PreviewSpecies(ISaveEngineSession session, int species, int form, int level)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Generation <= 2 || species <= 0) return null;
        var baseStats = session is SaveEngineSession engine
            ? ToBaseStats(engine.SaveFile.Personal.GetFormEntry((ushort)species, (byte)Math.Max(0, form)))
            : session.GetBaseStats(species);
        level = Math.Clamp(level, 1, 100);
        return NatureStatMath.Preview(baseStats, level, [31, 31, 31, 31, 31, 31], [0, 0, 0, 0, 0, 0], -1,
            $"Lv.{level} · 31 IVs · 0 EVs \u00b7 vs neutral");
    }

    /// <summary>The engine path, public to the assembly's tests through the session.</summary>
    internal static NatureStatPreview? PreviewEntity(PKM stored, StatPreviewOverrides? overrides)
    {
        if (stored.Species == 0 || stored.Format <= 2) return null;
        var work = stored.Clone();
        if (overrides?.Species is { } species && species > 0 && species != work.Species)
        {
            work.Species = (ushort)species;
            work.Form = 0;
        }
        if (overrides?.Level is { } level) work.CurrentLevel = (byte)Math.Clamp(level, 1, 100);
        var caps = SaveEngineSession.TrainingCapsOf(work);
        if (overrides?.IVs is { Count: 6 } ivs) SaveEngineSession.SetIVsFromAppOrder(work, SaveEngineSession.ClampAll(ivs.ToArray(), caps.IvMax));
        if (overrides?.EVs is { Count: 6 } evs) SaveEngineSession.SetEVsFromAppOrder(work, SaveEngineSession.ClampAll(evs.ToArray(), caps.EvMax));

        var byNature = new IReadOnlyList<int>[NatureFacts.Count];
        for (var n = 0; n < NatureFacts.Count; n++)
            byNature[n] = StatsWithNature(work, (Nature)n);

        var minted = work.Format >= 8 && work.StatAlignment != work.Nature;
        var basis = $"Lv.{work.CurrentLevel} · {(overrides is null ? "its" : "edited")} IVs/{TrainingLabel(work)}";
        return new NatureStatPreview((int)work.Nature, minted ? (int)work.StatAlignment : null, basis,
            AppOrder(Compute(work.Clone())), byNature);
    }

    /// <summary>Stats after a nature edit, as the session would apply it.</summary>
    private static int[] StatsWithNature(PKM work, Nature nature)
    {
        var clone = work.Clone();
        SaveEngineSession.NatureEditTarget(clone, nature, out var statNature);
        clone.StatAlignment = statNature;
        if (clone.StatAlignment != statNature)
        {
            // Gen 3/4 derive the nature from the PID (empty setter). Only stats are read
            // from this throwaway clone, so any PID with the wanted remainder will do.
            var baseline = clone.PID % 0x7FFF_FFFFu / 25 * 25;
            clone.PID = baseline + (uint)statNature;
        }
        return AppOrder(Compute(clone));
    }

    private static ushort[] Compute(PKM entity)
    {
        var stats = new ushort[6];
        entity.LoadStats(entity.PersonalInfo, stats);
        return stats;
    }

    // PKHeX: H/A/B/S/C/D -> display HP/Atk/Def/SpA/SpD/Spe.
    private static int[] AppOrder(ushort[] s) => [s[0], s[1], s[2], s[4], s[5], s[3]];

    private static string TrainingLabel(PKM entity) => entity switch
    {
        PB7 => "AVs",
        PA8 => "GVs",
        _ => "EVs",
    };

    private static BaseStats ToBaseStats(IBaseStat p) => new(p.HP, p.ATK, p.DEF, p.SPA, p.SPD, p.SPE);

    private static NatureStatPreview? PreviewDetail(ISaveEngineSession session, EntityDetail detail, StatPreviewOverrides? overrides)
    {
        if (detail.IsEmpty) return null;
        var species = overrides?.Species is > 0 ? overrides.Species.Value : detail.Species;
        var level = overrides?.Level ?? detail.Level;
        var ivs = overrides?.IVs is { Count: 6 } i ? i : detail.IVs;
        var evs = overrides?.EVs is { Count: 6 } e ? e : detail.EVs;
        return NatureStatMath.Preview(session.GetBaseStats(species), level, ivs, evs, detail.Nature,
            $"Lv.{Math.Clamp(level, 1, 100)} · {(overrides is null ? "its" : "edited")} IVs/EVs");
    }
}
