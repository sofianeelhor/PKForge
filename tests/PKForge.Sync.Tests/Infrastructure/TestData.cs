using PKForge.Domain;

namespace PKForge.Sync.Tests.Infrastructure;

public static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Stand-in Pokémon bytes, unique per seed.</summary>
    public static byte[] Bytes(int seed)
    {
        var bytes = new byte[344];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static BankEntryInfo Info(int species, string nickname) => new(species, 0, false, nickname, 50, 8, "Test", "PK8");
}
