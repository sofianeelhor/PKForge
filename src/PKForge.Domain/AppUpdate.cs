namespace PKForge.Domain;

/// <summary>The latest installable release discovered by the app.</summary>
public sealed record AvailableAppUpdate(
    string Version,
    string ReleaseUrl,
    string DownloadUrl,
    long SizeBytes);

/// <summary>Result of comparing the installed app with GitHub's latest release.</summary>
public sealed record AppUpdateCheck(
    bool IsAvailable,
    AvailableAppUpdate? Update,
    string Message);

public enum AppUpdateInstallResult
{
    InstallerOpened,
    InstallPermissionRequired,
    ReleasePageOpened,
}

/// <summary>Pure release-channel rules. Network and platform services stay in the app layer.</summary>
public static class AppUpdateRules
{
    /// <summary>Standard release tags are vMAJOR.MINOR.PATCH; build metadata is ignored.</summary>
    public static bool IsNewerVersion(string installed, string candidate)
    {
        if (!TryParse(installed, out var current) || !TryParse(candidate, out var next))
            return false;
        var core = next.Core.CompareTo(current.Core);
        if (core != 0)
            return core > 0;
        if (next.Prerelease.Length == 0 || current.Prerelease.Length == 0)
            return next.Prerelease.Length == 0 && current.Prerelease.Length != 0;
        return string.CompareOrdinal(next.Prerelease, current.Prerelease) > 0;
    }

    /// <summary>Don't-remind-me applies to one release. A newer release always asks again.</summary>
    public static bool ShouldPromptAutomatically(string installed, string candidate, string skipped) =>
        IsNewerVersion(installed, candidate) && !string.Equals(candidate, skipped, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string value, out ReleaseVersion version)
    {
        var prerelease = "";
        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];
        // PKForge release tags are stable SemVer. Strip prerelease metadata if a future
        // tag carries it rather than treating the whole tag as incomparable.
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }
        if (!Version.TryParse(text, out var core))
        {
            version = default;
            return false;
        }
        version = new ReleaseVersion(core, prerelease);
        return true;
    }

    private readonly record struct ReleaseVersion(Version Core, string Prerelease);
}
