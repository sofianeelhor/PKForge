using System.Security.Cryptography;
using System.Text;

namespace PKForge.App.Services;

/// <summary>
/// The user-facing side of <see cref="Domain.LayoutRiskKind.SuspectedHack"/>: a save that
/// reads as a retail layout but carries species the retail game does not have. The warning
/// is shown once per save; the save is remembered as flagged so its Home menu can offer
/// (and revoke) "Edit at my own risk" without re-analysing the whole file.
/// </summary>
public static class RomHackNotice
{
    private const string FlaggedPrefix = "romhack_flagged_";

    public const string Title = "UNSUPPORTED ROM HACK";

    public const string Warning =
        "This looks like a ROM hack. PKForge detected it but its engine is not built for this game: " +
        "Pokémon, moves or items may show wrong and editing can damage the save. Continue at your own risk.";

    public const string ReadOnlyOption = "Browse read-only";
    public const string EditOption = "Edit at my own risk";

    /// <summary>The status-strip marker while such a save is open, like the Hardcore one.</summary>
    public const string Marker = "ROM HACK · UNSUPPORTED";

    /// <summary>The marker plus how to lift read-only, for a save the user has not accepted.</summary>
    public const string ReadOnlyStatus = Marker + " · READ-ONLY - long-press it on Home to allow editing";

    /// <summary>A corrupting layout is never editable; this explains why nothing is offered.</summary>
    public const string CorruptingTitle = "SAVE IS READ-ONLY";

    /// <summary>True once the warning has been shown for this save.</summary>
    public static bool IsFlagged(string documentId) => Preferences.Default.Get(Key(documentId), false);

    public static void MarkFlagged(string documentId) => Preferences.Default.Set(Key(documentId), true);

    // Document ids are SAF URIs: hashed so the preference key stays short and opaque.
    private static string Key(string documentId) =>
        FlaggedPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)))[..24];
}
