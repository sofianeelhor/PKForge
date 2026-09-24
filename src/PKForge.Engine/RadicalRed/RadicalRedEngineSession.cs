namespace PKForge.Engine.RadicalRed;

/// <summary>
/// A Pokémon Radical Red save: the CFRU session with Radical Red's tables. Radical Red
/// keeps the retail 0x080120xx signature, so it is told apart structurally
/// (<see cref="RadicalRedFormat.IsRadicalRed"/>), and its build backs 22 boxes (the
/// stream's 19 plus the raw region's 3).
/// </summary>
internal sealed class RadicalRedEngineSession : CfruEngineSession
{
    public static readonly CfruGameProfile Profile = new(
        "Radical Red", "RADICALRED", RadicalRedGameData.Instance, [],
        bytes => RadicalRedFormat.IsRadicalRed(bytes.Span));

    public RadicalRedEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
        : base(bytes, displayName, Profile)
    {
    }
}
