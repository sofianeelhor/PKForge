namespace PKForge.Domain;

/// <summary>A named profile within one save file. Indices are stable even for empty profiles.</summary>
public sealed record SaveProfile(int Index, string Name, bool IsEmpty);

/// <summary>Optional profile selection. All profiles share one whole-file restore point.</summary>
public interface IProfiledSaveSession
{
    IReadOnlyList<SaveProfile> Profiles { get; }
    int CurrentProfile { get; }
    void SelectProfile(int index);
}
