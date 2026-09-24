using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Plain-language verdict on the converted entity's legality.</summary>
public enum TransferLegality
{
    Legal,
    Illegal,
    Unknown,
}

/// <summary>
/// The pre-flight diff for one transfer: what the conversion changes, whether the result is
/// legal, and the risks a backwards (downgrade) conversion takes. Warnings never block a
/// transfer; they are the user's informed-consent text.
/// </summary>
public sealed record TransferPreview(
    IReadOnlyList<string> Changes, TransferLegality Legality, IReadOnlyList<string> LegalityLines,
    IReadOnlyList<string> Warnings, bool Backwards = false)
{
    public TransferPreview(IReadOnlyList<string> changes, TransferLegality legality, IReadOnlyList<string> legalityLines)
        : this(changes, legality, legalityLines, [], false)
    {
    }

    public string Verdict => Legality switch
    {
        TransferLegality.Legal => "Legal ✓",
        TransferLegality.Illegal => "Likely flagged as illegal ✗ (the game still loads it)",
        _ => "Legality unknown",
    };
}

/// <summary>
/// Runs a transfer's conversion as a dry run and reports what it would change. The
/// scratch session the caller hands in is the same throwaway session pattern
/// <c>TransferService</c> uses for real transfers; this service only imports into it
/// and reads the slot back, so no connected save is ever touched. This is the
/// OpenHome-style trust builder: conversions are silent otherwise.
/// </summary>
public sealed class TransferPreviewService
{
    private static readonly string[] StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
    private static readonly string[] GenderNames = ["male", "female", "genderless"];

    private readonly ILegalityService? _legality;
    private readonly GameStrings _strings = GameInfo.GetStrings("en");

    public TransferPreviewService(ILegalityService? legality = null) => _legality = legality;

    /// <summary>
    /// Performs the exact import a transfer would perform into <paramref name="scratch"/>
    /// (parse, convert, land in the slot), then diffs the parsed source entity against
    /// the slot's landed entity and analyzes the result's legality. Returns null when
    /// the entity cannot enter the scratch save's format at all — the same refusal
    /// <see cref="ISaveEngineSession.ImportSlot"/> reports to a real transfer.
    /// </summary>
    /// <remarks><paramref name="format"/>: the bytes' recorded entity format (bank entries,
    /// exports); null reads them by heuristics with the scratch save's context preferred.</remarks>
    public TransferPreview? Preview(ISaveEngineSession scratch, int box, int slot, byte[] entityBytes, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(entityBytes);

        var context = scratch is SaveEngineSession engine ? engine.GetEntity(box, slot).Context : EntityContext.None;
        var before = EntityBytes.Parse(entityBytes, format, context);
        if (before is null || before.Species == 0)
            return null;
        // Romhack engine sessions cannot hand back the landed entity, so the diff is
        // honestly unavailable for them; the transfer itself is unaffected.
        if (scratch is not SaveEngineSession session)
        {
            if (!scratch.ImportSlot(box, slot, entityBytes, format))
                return null;
            return new TransferPreview(
                ["Conversion details are not available for this game's engine; the transfer itself is unchanged."],
                TransferLegality.Unknown,
                ["This game has no offline legality analysis."]);
        }

        var conversion = session.ImportSlotWithReport(box, slot, entityBytes, out _, format);
        if (conversion is null)
            return null;
        var after = session.GetEntity(box, slot);
        var legality = AnalyzeLegality(session, box, slot, out var lines);
        return new TransferPreview([.. Diff(before, after)], legality, lines, conversion.Warnings, conversion.Backwards);
    }

    private TransferLegality AnalyzeLegality(SaveEngineSession session, int box, int slot, out IReadOnlyList<string> lines)
    {
        if (_legality is null)
        {
            lines = ["No legality analyzer is available."];
            return TransferLegality.Unknown;
        }
        try
        {
            var report = _legality.Analyze(session, box, slot);
            lines = report.Lines;
            return report.Valid ? TransferLegality.Legal : TransferLegality.Illegal;
        }
        catch (Exception)
        {
            // Analysis is best-effort by contract: a verdict we cannot compute is
            // reported as unknown, never guessed.
            lines = ["Legality analysis failed for this entity."];
            return TransferLegality.Unknown;
        }
    }

    private IEnumerable<string> Diff(PKM before, PKM after)
    {
        var crossGen = before.Format != after.Format;
        if (before.IsEgg && !after.IsEgg)
            yield return "Egg hatches during the transfer: the target format cannot store eggs from the old game.";

        foreach (var line in DiffMoves(before, after))
            yield return line;

        if (before.Ability != after.Ability)
        {
            var reason = crossGen ? " (re-derived from the ability slot in the target game's species data)" : "";
            yield return $"Ability: {AbilityName(before.Ability)} → {AbilityName(after.Ability)}{reason}.";
        }
        if (before.Nature != after.Nature)
            yield return $"Nature: {NatureName(before.Nature)} → {NatureName(after.Nature)}.";
        if (before.Gender != after.Gender)
            yield return $"Gender: {GenderName(before.Gender)} → {GenderName(after.Gender)}.";
        if (before.IsShiny != after.IsShiny)
            yield return $"Shiny state: {(before.IsShiny ? "shiny" : "not shiny")} → {(after.IsShiny ? "shiny" : "not shiny")}.";
        if (before.Form != after.Form)
            yield return $"Form: {before.Form} → {after.Form}.";

        if (before.PID != after.PID)
        {
            yield return before.Format <= 2
                ? $"PID created: {after.PID:X8} (Gen 1-2 Pokémon have no PID; the modern format needs a new identity)."
                : $"PID regenerated: {before.PID:X8} → {after.PID:X8} (old games track nature/gender/ability in the PID).";
        }
        else if (before.EncryptionConstant != after.EncryptionConstant)
        {
            yield return $"Encryption constant set to {after.EncryptionConstant:X8}.";
        }

        var ivs = DiffIvs(before, after);
        if (ivs.Length > 0)
        {
            var reason = before.Format <= 2 ? " (Gen 1-2 stat DNA does not map 1:1 to modern IVs)" : "";
            yield return $"IVs rewritten: {string.Join(", ", ivs)}{reason}.";
        }

        var beforeBall = BallName(before.Ball, before.Format);
        var afterBall = BallName(after.Ball, after.Format);
        if (!string.Equals(beforeBall, afterBall, StringComparison.Ordinal))
            yield return $"Ball: {beforeBall} → {afterBall}.";
        foreach (var line in DiffHeldItem(before, after, crossGen))
            yield return line;

        if (ChangedLocation(before, after, egg: false))
            yield return $"Met location: {LocationName(before, egg: false)} → {LocationName(after, egg: false)}.";
        if (ChangedLocation(before, after, egg: true))
            yield return $"Egg location: {LocationName(before, egg: true)} → {LocationName(after, egg: true)}.";
        if (before.MetLevel != after.MetLevel)
            yield return $"Met level: {before.MetLevel} → {after.MetLevel}.";
        if (!Nullable.Equals(before.MetDate, after.MetDate))
            yield return $"Met date: {DateName(before.MetDate)} → {DateName(after.MetDate)}.";
        if (before.Version != after.Version)
            yield return $"Origin game: {GameInfo.GetVersionName(before.Version)} → {GameInfo.GetVersionName(after.Version)}.";

        if (!string.Equals(before.Nickname, after.Nickname, StringComparison.Ordinal))
            yield return $"Nickname: '{before.Nickname}' → '{after.Nickname}'.";
        else if (before.IsNicknamed != after.IsNicknamed)
            yield return $"Nickname flag: {(before.IsNicknamed ? "nicknamed" : "species name")} → {(after.IsNicknamed ? "nicknamed" : "species name")}.";

        if (!string.Equals(before.OriginalTrainerName, after.OriginalTrainerName, StringComparison.Ordinal))
            yield return $"Original trainer: '{before.OriginalTrainerName}' → '{after.OriginalTrainerName}'.";
        if (before.OriginalTrainerGender != after.OriginalTrainerGender)
            yield return $"Original trainer gender: {GenderName(before.OriginalTrainerGender)} → {GenderName(after.OriginalTrainerGender)}.";
        if (before.TID16 != after.TID16 || before.SID16 != after.SID16)
            yield return $"Original trainer ID: {before.TID16}/{before.SID16} → {after.TID16}/{after.SID16}.";

        foreach (var line in DiffRibbons(before, after))
            yield return line;
    }

    private IEnumerable<string> DiffMoves(PKM before, PKM after)
    {
        var b = new[] { before.Move1, before.Move2, before.Move3, before.Move4 };
        var a = new[] { after.Move1, after.Move2, after.Move3, after.Move4 };
        for (var i = 0; i < 4; i++)
        {
            if (b[i] == a[i])
                continue;
            if (a[i] == 0)
                yield return $"Moves: {MoveName(b[i])} will be lost (the target format cannot hold it).";
            else if (b[i] == 0)
                yield return $"Moves: slot {i + 1} gains {MoveName(a[i])}.";
            else
                yield return $"Moves: {MoveName(b[i])} → {MoveName(a[i])}.";
        }
    }

    private static string[] DiffIvs(PKM before, PKM after)
    {
        var b = new[] { before.IV_HP, before.IV_ATK, before.IV_DEF, before.IV_SPA, before.IV_SPD, before.IV_SPE };
        var a = new[] { after.IV_HP, after.IV_ATK, after.IV_DEF, after.IV_SPA, after.IV_SPD, after.IV_SPE };
        var lines = new List<string>(6);
        for (var i = 0; i < 6; i++)
            if (b[i] != a[i])
                lines.Add($"{StatNames[i]} {b[i]}→{a[i]}");
        return [.. lines];
    }

    private IEnumerable<string> DiffHeldItem(PKM before, PKM after, bool crossGen)
    {
        if (before.HeldItem != after.HeldItem)
        {
            if (after.HeldItem == 0)
                yield return $"Held item lost: {ItemName(before)}.";
            else if (before.HeldItem == 0)
                yield return $"Held item gained: {ItemName(after)}.";
            else
                yield return $"Held item: {ItemName(before)} → {ItemName(after)}.";
        }
        else if (before.HeldItem != 0 && crossGen)
        {
            // The index rides along raw, but every generation numbers its item table
            // differently; only flag it when the two tables disagree about this index.
            var oldName = ItemName(before);
            var newName = ItemName(after);
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
                yield return $"Held item reinterpreted: {oldName} → {newName} (the index now points into the target game's item table).";
        }
    }

    private static IEnumerable<string> DiffRibbons(PKM before, PKM after)
    {
        var b = RibbonsOf(before);
        var a = RibbonsOf(after);
        var lost = b.Where(r => !a.TryGetValue(r.Key, out var count) || count < r.Value)
            .Select(r => a.TryGetValue(r.Key, out var count) ? $"{r.Key} (count {r.Value}→{count})" : r.Key).ToList();
        var gained = a.Where(r => !b.TryGetValue(r.Key, out var count) || count > r.Value)
            .Select(r => b.TryGetValue(r.Key, out var count) ? $"{r.Key} (count {count}→{r.Value})" : r.Key).ToList();
        if (lost.Count > 0)
            yield return $"Ribbons lost: {string.Join(", ", lost)}.";
        if (gained.Count > 0)
            yield return $"Ribbons gained: {string.Join(", ", gained)}.";
    }

    /// <summary>Ribbon name → count (boolean ribbons count 1).</summary>
    private static Dictionary<string, int> RibbonsOf(PKM entity) =>
        RibbonInfo.GetRibbonInfo(entity)
            .Where(r => r.HasRibbon || r.RibbonCount > 0)
            .ToDictionary(r => r.Name, r => r.HasRibbon ? 1 : r.RibbonCount);

    private string MoveName(int move) => move > 0 && move < _strings.movelist.Length ? _strings.movelist[move] : $"#{move}";
    private string AbilityName(int ability) => ability >= 0 && ability < _strings.abilitylist.Length ? _strings.abilitylist[ability] : "none";
    private string NatureName(Nature nature) => (int)nature < _strings.natures.Length ? _strings.natures[(int)nature] : nature.ToString();
    private string BallName(int ball, int format) => ball > 0 && ball < _strings.balllist.Length ? _strings.balllist[ball] : (format <= 2 ? "Poké Ball" : $"#{ball}");
    private static string GenderName(int gender) => (uint)gender < GenderNames.Length ? GenderNames[gender] : $"#{gender}";
    private string ItemName(PKM entity)
    {
        var items = _strings.GetItemStrings(entity.Context, entity.Version);
        var id = entity.HeldItem;
        return id > 0 && id < items.Length ? items[id] : $"#{id}";
    }

    private static string DateName(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "—";

    private string LocationName(PKM entity, bool egg)
    {
        var id = egg ? entity.EggLocation : entity.MetLocation;
        return GameInfo.GetLocationName(egg, id, entity.Format, entity.Generation, entity.Version) is { Length: > 0 } name
            ? name
            : $"#{id}";
    }

    /// <summary>Location ids are renumbered between generations, so an unchanged id can
    /// still mean a different place; "no location" (0) on both sides is not a change.</summary>
    private bool ChangedLocation(PKM before, PKM after, bool egg)
    {
        var b = egg ? before.EggLocation : before.MetLocation;
        var a = egg ? after.EggLocation : after.MetLocation;
        if (b != a)
            return true;
        if (b == 0)
            return false;
        return before.Format != after.Format
            && !string.Equals(LocationName(before, egg), LocationName(after, egg), StringComparison.Ordinal);
    }
}
