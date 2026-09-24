using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// Recovers the PKHeX form behind a CFRU form species. CFRU hacks store megas, regional
/// forms and cosmetic forms as separate species ids whose names carry the form as a
/// Showdown-style suffix ("Charizard-Mega-Y", "Tauros-Paldea-Aqua", "Unown-!"); the national
/// bridge keeps only the base species, which is why every such mon drew as its base form.
/// The suffix is matched against PKHeX's own form names (FormConverter.GetFormList), so a
/// form resolves only when PKHeX names the very same form; anything else stays form 0.
/// </summary>
internal static class RomhackForms
{
    private static readonly EntityContext[] Contexts =
        [EntityContext.Gen9, EntityContext.Gen9a, EntityContext.Gen8, EntityContext.Gen8a, EntityContext.Gen7];

    private static readonly Dictionary<(int, string), (int Form, SpriteTraits Traits)> Cache = [];
    private static readonly Lock Gate = new();

    /// <param name="national">The national id <paramref name="romName"/> bridged to.</param>
    /// <param name="romName">The hack's species name, e.g. "Charizard-Mega-Y".</param>
    public static (int Form, SpriteTraits Traits) Resolve(int national, string romName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((national, romName), out var hit)) return hit;
            var result = ResolveCore(national, romName);
            Cache[(national, romName)] = result;
            return result;
        }
    }

    private static (int, SpriteTraits) ResolveCore(int national, string romName)
    {
        if (national <= 0) return (0, default);
        var strings = GameInfo.GetStrings("en");
        var baseName = strings.specieslist[national];
        if (!romName.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase)) return (0, default);
        var suffix = Normalize(romName[(baseName.Length + 1)..]);
        if (suffix.Length == 0) return (0, default);
        if (suffix == "gmax") return (0, new SpriteTraits(Gigantamax: true));

        foreach (var context in Contexts)
        {
            var forms = FormConverter.GetFormList((ushort)national, strings.types, strings.forms, GameInfo.GenderSymbolASCII, context);
            for (var form = 1; form < forms.Length; form++)
                if (Normalize(forms[form]) == suffix)
                    return (form, default);
        }
        // ORAS cosplay Pikachu: PKHeX names these only in the Gen 6 context.
        if (national == SpriteCatalog.Pikachu)
        {
            var forms = FormConverter.GetFormList((ushort)national, strings.types, strings.forms, GameInfo.GenderSymbolASCII, EntityContext.Gen6);
            for (var form = 1; form < forms.Length; form++)
                if (Normalize(forms[form]) == suffix)
                    return (form, new SpriteTraits(Cosplay: true));
        }
        return (0, default);
    }

    /// <summary>Case- and punctuation-blind, keeping Unown's '!' and '?'.</summary>
    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var n = 0;
        foreach (var ch in text)
            if (char.IsAsciiLetterOrDigit(ch) || ch is '!' or '?')
                buffer[n++] = char.ToLowerInvariant(ch);
        return new string(buffer[..n]);
    }
}
