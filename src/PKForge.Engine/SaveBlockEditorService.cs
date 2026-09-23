using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Switch-era save block (SCBlock) as the block editor lists it.</summary>
/// <param name="Name">PKHeX's accessor name for the key, when it has one.</param>
/// <param name="Value">Display value; null for objects and arrays.</param>
/// <param name="Editable">Bool and single numeric blocks only.</param>
public sealed record SaveBlockEntry(uint Key, string? Name, string Type, int Size, string? Value, bool Editable);

/// <summary>
/// Gen 8/9 block editor (SW/SH, Legends: Arceus, S/V, Legends: Z-A) over PKHeX's
/// <see cref="ISCBlockArray"/>, mirroring PKHeX.WinForms SAV_BlockDump8 but restricted to the
/// safe subset: flipping a boolean block (Bool1 = false, Bool2 = true, see
/// <see cref="SCBlock.ChangeBooleanType"/>) and replacing a single primitive in place with a
/// value of the same type (<see cref="SCBlock.SetValue"/>). Nothing here resizes a block,
/// changes its stored type, or touches object/array payloads.
/// </summary>
public static class SaveBlockEditorService
{
    public static bool IsSupported(ISaveEngineSession session) => Blocks(session) is not null;

    public static IReadOnlyList<SaveBlockEntry> GetBlocks(ISaveEngineSession session)
    {
        if (Blocks(session) is not { } array)
            return [];
        var names = TryLoadNames(array);
        return array.AllBlocks.Select(block => Describe(block, names?.GetBlockName(block, out _))).ToList();
    }

    public static GenerationOutcome SetBool(ISaveEngineSession session, uint key, bool value)
    {
        if (!TryGet(session, key, out var block))
            return new GenerationOutcome(false, $"No block {key:X8} in this save.");
        if (block.Type is not (SCTypeCode.Bool1 or SCTypeCode.Bool2))
            return new GenerationOutcome(false, $"Block {key:X8} is {block.Type}, not a boolean.");
        block.ChangeBooleanType(value ? SCTypeCode.Bool2 : SCTypeCode.Bool1);
        return new GenerationOutcome(true, $"Block {key:X8} = {value}");
    }

    /// <summary>Parses <paramref name="text"/> (invariant culture) as the block's own type and
    /// stores it; out-of-range or non-finite input is refused rather than wrapped.</summary>
    public static GenerationOutcome SetNumber(ISaveEngineSession session, uint key, string text)
    {
        if (!TryGet(session, key, out var block))
            return new GenerationOutcome(false, $"No block {key:X8} in this save.");
        if (!IsNumeric(block.Type))
            return new GenerationOutcome(false, $"Block {key:X8} is {block.Type}, not a single number.");
        if (!TryParse(block.Type, text.Trim(), out var value))
            return new GenerationOutcome(false, $"\"{text}\" is not a valid {block.Type}.");
        block.SetValue(value);
        return new GenerationOutcome(true, $"Block {key:X8} = {FormatValue(block)}");
    }

    private static SaveBlockEntry Describe(SCBlock block, string? name)
    {
        if (block.Type.IsBoolean())
        {
            // Bool3 is the element type of boolean arrays, never a stored single value.
            var editable = block.Type is SCTypeCode.Bool1 or SCTypeCode.Bool2;
            var shown = block.Type switch { SCTypeCode.Bool1 => "false", SCTypeCode.Bool2 => "true", _ => null };
            return new SaveBlockEntry(block.Key, name, "Bool", 0, shown, editable);
        }
        if (IsNumeric(block.Type))
            return new SaveBlockEntry(block.Key, name, block.Type.ToString(), block.Data.Length, FormatValue(block), true);
        var type = block.Type is SCTypeCode.Array ? $"{block.SubType}[]" : block.Type.ToString();
        return new SaveBlockEntry(block.Key, name, type, block.Data.Length, null, false);
    }

    private static bool IsNumeric(SCTypeCode type) => type is >= SCTypeCode.Byte and <= SCTypeCode.Double;

    private static string FormatValue(SCBlock block) => block.GetValue() switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "",
    };

    private static bool TryParse(SCTypeCode type, string text, [NotNullWhen(true)] out object? value)
    {
        const NumberStyles integer = NumberStyles.Integer;
        const NumberStyles real = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        value = type switch
        {
            SCTypeCode.Byte => byte.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.UInt16 => ushort.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.UInt32 => uint.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.UInt64 => ulong.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.SByte => sbyte.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.Int16 => short.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.Int32 => int.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.Int64 => long.TryParse(text, integer, culture, out var v) ? v : null,
            SCTypeCode.Single => float.TryParse(text, real, culture, out var v) && float.IsFinite(v) ? v : null,
            SCTypeCode.Double => double.TryParse(text, real, culture, out var v) && double.IsFinite(v) ? v : null,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryGet(ISaveEngineSession session, uint key, [NotNullWhen(true)] out SCBlock? block)
    {
        block = Blocks(session)?.AllBlocks.FirstOrDefault(candidate => candidate.Key == key);
        return block is not null;
    }

    private static ISCBlockArray? Blocks(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as ISCBlockArray : null;

    /// <summary>Block names come from reflection over PKHeX's accessor constants; they are a
    /// convenience only, so a trimmed build that lost them still lists every block by key.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Names are optional; keys, types and values do not depend on reflection.")]
    private static SCBlockMetadata? TryLoadNames(ISCBlockArray array)
    {
        try
        {
            return new SCBlockMetadata(array.Accessor, []);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
