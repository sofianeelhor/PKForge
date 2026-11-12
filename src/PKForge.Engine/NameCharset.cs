using System.Text;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Which kind of name a <see cref="NameCharset"/> check is about (it only words the message).</summary>
public enum NameField
{
    Nickname,
    TrainerName,
    BoxName,
}

/// <summary>A name the destination game cannot store. Thrown before anything is written.</summary>
public sealed class NameNotStorableException(string message) : InvalidOperationException(message);

/// <summary>
/// Checks a typed name against the character set of the game it goes into, before anything is
/// written. PKHeX's Gen 1-3 encoders stop at the first character the game cannot store, so a
/// Latin nickname typed on a Japanese Gen 3 save used to become an empty one (QA 2026-11-12).
/// What can be mapped is mapped: half-width Latin becomes full-width on a Japanese save (its
/// charset has Ａ-Ｚ, ａ-ｚ, ０-９), full-width becomes half-width on an international one.
/// Anything else is refused with a message that names the characters, and nothing is written.
/// </summary>
public static class NameCharset
{
    /// <summary>The text to write: <paramref name="wanted"/> itself or its width-mapped form.</summary>
    /// <param name="roundTrip">Writes a candidate into a scratch copy of the destination and returns what reads back.</param>
    /// <exception cref="NameNotStorableException">The name cannot be stored; the message says why.</exception>
    public static string Fit(string wanted, Func<string, string> roundTrip, int maxLength, NameField field, bool japanese)
    {
        if (TryFit(wanted, roundTrip, maxLength, field, japanese, out var fitted, out var problem))
            return fitted;
        throw new NameNotStorableException(problem);
    }

    public static bool TryFit(string wanted, Func<string, string> roundTrip, int maxLength, NameField field, bool japanese,
        out string fitted, out string problem)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(roundTrip);
        fitted = wanted;
        problem = "";
        if (wanted.Length == 0) return true; // an empty name clears it: nothing to encode
        if (maxLength > 0 && wanted.Length > maxLength)
        {
            problem = $"{Label(field, capital: true)}s hold at most {maxLength} characters in this game. Nothing was changed.";
            return false;
        }

        string? best = null;
        var bestBad = new List<char>();
        foreach (var candidate in Candidates(wanted, japanese))
        {
            if (Stores(candidate, roundTrip))
            {
                fitted = candidate;
                return true;
            }
            var bad = candidate.Distinct().Where(c => !Stores(c.ToString(), roundTrip)).ToList();
            if (best is null || (bad.Count > 0 && (bestBad.Count == 0 || bad.Count < bestBad.Count)))
            {
                best = candidate;
                bestBad = bad;
            }
        }

        var where = Label(field, capital: false);
        var save = japanese ? "This Japanese save" : "This save";
        problem = bestBad.Count == 0
            ? $"{save} cannot store that {where} as typed. Nothing was changed."
            : $"{save} has no {Quote(bestBad)} in its character set, so it cannot go in a {where}. Nothing was changed.";
        return false;
    }

    /// <summary>A name check for a PKHeX entity: writes the candidate into a clone.</summary>
    public static string FitNickname(PKM entity, string wanted) =>
        Fit(wanted, c => { var probe = entity.Clone(); probe.Nickname = c; return probe.Nickname; },
            entity.MaxStringLengthNickname, NameField.Nickname, IsJapanese(entity));

    public static string FitTrainerName(PKM entity, string wanted) =>
        Fit(wanted, c => { var probe = entity.Clone(); probe.OriginalTrainerName = c; return probe.OriginalTrainerName; },
            entity.MaxStringLengthTrainer, NameField.TrainerName, IsJapanese(entity));

    /// <summary>A name check against a save's own charset (trainer name, box names, Hall of Fame).
    /// Every PKHeX format writes its save strings with the same encoder as its entities.</summary>
    public static string FitForSave(SaveFile save, string wanted, NameField field, int maxLength)
    {
        var probe = ProbeFor(save);
        // The probe's nickname field is the save's widest string; longer names (Gen 2 box
        // names) are checked in pieces, the encoders being per character.
        var piece = Math.Max(1, probe.MaxStringLengthNickname);
        string RoundTrip(string c)
        {
            var sb = new StringBuilder(c.Length);
            for (var i = 0; i < c.Length; i += piece)
            {
                var p = probe.Clone();
                p.Nickname = c.Substring(i, Math.Min(piece, c.Length - i));
                sb.Append(p.Nickname);
            }
            return sb.ToString();
        }
        return Fit(wanted, RoundTrip, maxLength, field, IsJapanese(probe));
    }

    /// <summary>A check for the Gen 3 hacks, which all write names through PKHeX's Gen 3 encoder.</summary>
    public static string FitGen3(string wanted, int maxLength, NameField field, bool japanese = false) =>
        Fit(wanted, c =>
        {
            Span<byte> buffer = stackalloc byte[Math.Max(maxLength, c.Length) + 1];
            StringConverter3.SetString(buffer, c, c.Length, japanese);
            return StringConverter3.GetString(buffer, japanese);
        }, maxLength, field, japanese);

    /// <summary>A check for Gen 4 hacks that write names through PKHeX's Gen 4 encoder.</summary>
    public static string FitGen4(string wanted, int maxLength, NameField field, int language) =>
        Fit(wanted, c =>
        {
            var buffer = new byte[(c.Length + 1) * 2];
            StringConverter4.SetString(buffer, c, c.Length, language);
            return StringConverter4.GetString(buffer);
        }, maxLength, field, language == (int)LanguageID.Japanese);

    /// <summary>The Gen 3 hack editors' names, fitted (or refused) before any field is written.
    /// Lengths stay each hack's own check (0 = not checked here): their records differ.</summary>
    public static Domain.EntityEdit FitGen3Edit(Domain.EntityEdit edit, int nicknameMax = 0, int trainerMax = 0)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Nickname is { Length: > 0 } nick)
            edit = edit with { Nickname = FitGen3(nick, nicknameMax, NameField.Nickname) };
        if (edit.OriginalTrainer is { Length: > 0 } ot)
            edit = edit with { OriginalTrainer = FitGen3(ot, trainerMax, NameField.TrainerName) };
        return edit;
    }

    internal static PKM ProbeFor(SaveFile save)
    {
        var probe = save.BlankPKM;
        if (probe.Format >= 3 && save.Language > 0)
            probe.Language = save.Language;
        return probe;
    }

    private static bool IsJapanese(PKM entity) => entity.Japanese;

    private static bool Stores(string candidate, Func<string, string> roundTrip)
    {
        string back;
        try { back = roundTrip(candidate); }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException) { return false; }
        return string.Equals(Fold(back), Fold(candidate), StringComparison.Ordinal);
    }

    /// <summary>The typed text first, then its width-mapped forms, the save's own width first.</summary>
    internal static IEnumerable<string> Candidates(string wanted, bool japanese)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { wanted };
        yield return wanted;
        var full = ToFullWidth(wanted);
        var half = ToHalfWidth(wanted);
        foreach (var other in japanese ? new[] { full, half } : new[] { half, full })
            if (seen.Add(other)) yield return other;
    }

    /// <summary>ASCII → full-width forms (U+FF01-U+FF5E), space → ideographic space.</summary>
    public static string ToFullWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c switch
            {
                ' ' => '　',
                >= '!' and <= '~' => (char)(c + 0xFEE0),
                _ => c,
            });
        return sb.ToString();
    }

    /// <summary>Full-width forms → ASCII, ideographic space → space.</summary>
    public static string ToHalfWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c switch
            {
                '　' => ' ',
                >= '！' and <= '～' => (char)(c - 0xFEE0),
                _ => c,
            });
        return sb.ToString();
    }

    /// <summary>What a reader sees: width, curly quotes and the games' private gender glyphs folded.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in ToHalfWidth(text))
        {
            var c = StringConverter4Util.NormalizeGenderSymbol(raw);
            sb.Append(c switch
            {
                '' => '♂',
                '' => '♀',
                '’' or '‘' => '\'',
                '“' or '”' or '«' or '»' or '„' or '『' or '』' => '"',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static string Label(NameField field, bool capital) => (field, capital) switch
    {
        (NameField.Nickname, true) => "Nickname",
        (NameField.Nickname, false) => "nickname",
        (NameField.TrainerName, true) => "Trainer name",
        (NameField.TrainerName, false) => "trainer name",
        (NameField.BoxName, true) => "Box name",
        _ => "box name",
    };

    private static string Quote(IReadOnlyList<char> chars) =>
        string.Join(", ", chars.Take(6).Select(c => c == ' ' ? "space" : $"“{c}”")) + (chars.Count > 6 ? "…" : "");
}
