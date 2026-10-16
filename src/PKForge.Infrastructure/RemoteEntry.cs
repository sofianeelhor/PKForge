using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// A Bank entry as another device stored it (through the sync server). <see cref="Data"/> is null when
/// this Bank already holds the same bytes, as for a move or a facts-only change.
/// </summary>
public sealed record RemoteEntry(BankEntry Entry, byte[]? Data);
