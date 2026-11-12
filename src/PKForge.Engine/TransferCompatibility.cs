using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Explains why an entity cannot enter a save's format, for the refusals
/// <see cref="PKForge.Domain.ISaveEngineSession.ImportSlot"/> reports as a bare false.
/// Transfers go either direction (<see cref="CrossFormatConverter"/> downgrades newer
/// formats with warnings), so the only refusals left are physical: the file is not a
/// Pokémon, or the species does not exist in the target game's data at all.
/// </summary>
public static class TransferCompatibility
{
    /// <summary>
    /// Plain-language reason <paramref name="entityBytes"/> cannot go to a save whose
    /// snapshot reports <paramref name="targetFormat"/> / <paramref name="targetGeneration"/>,
    /// or null when no specific reason is known (the caller keeps its generic message).
    /// Bytes are parsed with the target's context, exactly like the import itself.
    /// </summary>
    public static string? ExplainRefusal(byte[] entityBytes, string nickname, string targetFormat, int targetGeneration, string targetLabel, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(entityBytes);
        var context = Enum.TryParse<EntityContext>(targetFormat, out var parsed) ? parsed : EntityContext.None;
        var entity = EntityBytes.Parse(entityBytes, format, context);
        if ((entity is null || entity.Species == 0) && RadicalRed.CfruEntity.Recognize(format) is { } cfru)
            return $"{nickname} cannot go to {targetLabel}. " + (RadicalRed.CfruEntity.Pk3Refusal(entityBytes, cfru) is { } why
                ? $"{why} Only {RadicalRed.CfruEntity.GameName(cfru)} can take it."
                : $"It is a {RadicalRed.CfruEntity.GameName(cfru)} Pokémon with no Generation 3 counterpart, so only {RadicalRed.CfruEntity.GameName(cfru)} can take it.");
        if (entity is null || entity.Species == 0)
            return $"{nickname} is not a readable Pokémon file.";
        // A CFRU game takes a Pokémon only when every id has a twin in its own tables.
        if (RadicalRed.CfruEntity.ImportRefusal(entity, targetFormat) is { } hackRefusal)
            return $"{nickname} cannot go to {targetLabel}. {hackRefusal}";

        var target = TargetFor(context, targetGeneration);
        if (target is null)
            return null;
        return CrossFormatConverter.ExplainImpossible(entity, target) is { } reason
            ? $"{nickname} cannot go to {targetLabel}. {reason}"
            : null;
    }

    /// <summary>A blank save standing in for the target's species data, or null when the context is unknown.</summary>
    private static SaveFile? TargetFor(EntityContext context, int generation)
    {
        try
        {
            if (context.IsValid)
                return BlankSaveFile.Get(context);
            if (generation is >= 1 and <= 9)
                return BlankSaveFile.Get((EntityContext)generation);
        }
        catch (Exception)
        {
            // No blank for this context: the caller keeps its generic message.
        }
        return null;
    }
}
