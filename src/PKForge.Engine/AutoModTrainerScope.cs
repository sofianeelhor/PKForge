using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>
/// When Auto-Legality builds a Pokémon from an encounter of another game than the open save
/// (a transfer-only species, or a repair that keeps a foreign origin), it asks its trainer
/// database for a trainer of that game. PKForge never fills that database, so the answer is
/// Auto-Legality's built-in stand-in, "ALM" with TID 54321, which then ends up as the
/// Pokémon's trainer and, once it moves on to a Gen 8+ game, shows as a handler nobody knows.
/// For as long as a generation runs, the stand-in is the open save's own trainer.
/// Callers hold the legalizer's generation lock: these settings are process-wide.
/// </summary>
internal readonly struct AutoModTrainerScope : IDisposable
{
    private readonly string _ot;
    private readonly ushort _tid;
    private readonly ushort _sid;
    private readonly bool _active; // a default instance changes nothing and restores nothing

    public AutoModTrainerScope(ITrainerInfo save)
    {
        _ot = TrainerSettings.DefaultOT;
        _tid = TrainerSettings.DefaultTID16;
        _sid = TrainerSettings.DefaultSID16;
        _active = true;
        if (save.OT.Length == 0) return; // a blank name would be worse than the stand-in
        TrainerSettings.DefaultOT = save.OT;
        TrainerSettings.DefaultTID16 = save.TID16;
        TrainerSettings.DefaultSID16 = save.SID16;
    }

    public void Dispose()
    {
        if (!_active) return;
        TrainerSettings.DefaultOT = _ot;
        TrainerSettings.DefaultTID16 = _tid;
        TrainerSettings.DefaultSID16 = _sid;
    }
}
