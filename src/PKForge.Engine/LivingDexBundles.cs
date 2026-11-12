using System.IO.Compression;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The pre-generated living dex bundles (tools/DexGen, shipped under Resources/UI/dex):
/// which file belongs to which game family, and a strict reader that refuses any bundle
/// not made for the save's exact format.
/// </summary>
/// <remarks>
/// Two header layouts exist. Version 1 (dex-g1..dex-g9) starts with the generation number
/// only, so it is accepted solely by the generation's main family (the one DexGen built it
/// from: Sword/Shield for 8, Sun/Moon for 7, Scarlet/Violet for 9). Version 2 starts with
/// <see cref="Magic"/>, a format version and the exact <see cref="EntityContext"/>.
/// </remarks>
public static class LivingDexBundles
{
    /// <summary>"PKDX", little-endian.</summary>
    public const int Magic = 0x58444B50;
    public const int FormatVersion = 2;

    /// <summary>The game DexGen builds each family's bundle from, and the file it writes.</summary>
    public static IReadOnlyList<(EntityContext Context, GameVersion Version, string File)> Families { get; } =
    [
        (EntityContext.Gen1, GameVersion.RD, "dex-g1.bin.gz"),
        (EntityContext.Gen2, GameVersion.C, "dex-g2.bin.gz"),
        (EntityContext.Gen3, GameVersion.E, "dex-g3.bin.gz"),
        (EntityContext.Gen4, GameVersion.Pt, "dex-g4.bin.gz"),
        (EntityContext.Gen5, GameVersion.B2, "dex-g5.bin.gz"),
        (EntityContext.Gen6, GameVersion.AS, "dex-g6.bin.gz"),
        (EntityContext.Gen7, GameVersion.UM, "dex-g7.bin.gz"),
        (EntityContext.Gen7b, GameVersion.GP, "dex-gen7b.bin.gz"),
        (EntityContext.Gen8, GameVersion.SW, "dex-g8.bin.gz"),
        (EntityContext.Gen8a, GameVersion.PLA, "dex-gen8a.bin.gz"),
        (EntityContext.Gen8b, GameVersion.BD, "dex-gen8b.bin.gz"),
        (EntityContext.Gen9, GameVersion.VL, "dex-g9.bin.gz"),
        (EntityContext.Gen9a, GameVersion.ZA, "dex-gen9a.bin.gz"),
    ];

    /// <summary>The bundle file for this context, or null when no family matches.</summary>
    public static string? FileFor(EntityContext context) =>
        Families.FirstOrDefault(f => f.Context == context).File;

    /// <summary>Main family of a generation: the only context a version 1 header may fill.</summary>
    private static EntityContext MainContext(int generation) => generation switch
    {
        1 => EntityContext.Gen1, 2 => EntityContext.Gen2, 3 => EntityContext.Gen3,
        4 => EntityContext.Gen4, 5 => EntityContext.Gen5, 6 => EntityContext.Gen6,
        7 => EntityContext.Gen7, 8 => EntityContext.Gen8, 9 => EntityContext.Gen9,
        _ => EntityContext.None,
    };

    /// <summary>Writes a version 2 bundle (used by tools/DexGen and tests).</summary>
    public static byte[] Write(EntityContext context, IEnumerable<PKM> mons)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var list = mons.ToList();
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write((int)context);
            writer.Write(list.Count);
            foreach (var mon in list)
            {
                writer.Write(mon.Species);
                writer.Write(mon.Data.Length);
                writer.Write(mon.Data);
            }
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Every Pokémon of the bundle, already in <paramref name="pkmType"/>. Throws
    /// <see cref="InvalidOperationException"/> with a reason the player can read when the
    /// bundle was made for another game or holds anything but that format; nothing is
    /// returned partially.
    /// </summary>
    public static IReadOnlyList<PKM> Read(byte[] compressedBundle, EntityContext context, Type pkmType)
    {
        using var input = new MemoryStream(compressedBundle);
        using var inflate = new GZipStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        raw.Position = 0;
        using var reader = new BinaryReader(raw);

        var first = reader.ReadInt32();
        EntityContext bundleContext;
        if (first == Magic)
        {
            var version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidOperationException($"This living dex file uses format {version}, which this version of PKForge cannot read.");
            bundleContext = (EntityContext)reader.ReadInt32();
        }
        else
        {
            bundleContext = MainContext(first);
        }
        if (bundleContext != context)
            throw new InvalidOperationException(
                $"This living dex was made for {Describe(bundleContext)}, not {Describe(context)}. Nothing was written.");

        var count = reader.ReadInt32();
        if (count is < 0 or > 10_000)
            throw new InvalidDataException("The living dex file is damaged. Nothing was written.");
        var mons = new List<PKM>(count);
        for (var i = 0; i < count; i++)
        {
            var species = reader.ReadUInt16();
            var length = reader.ReadInt32();
            var data = reader.ReadBytes(length);
            if (data.Length != length)
                throw new InvalidDataException("The living dex file is damaged. Nothing was written.");
            var mon = EntityFormat.GetFromBytes(data, context);
            if (mon is not null && mon.GetType() != pkmType && pkmType == typeof(PB8LUMI) && mon is PB8)
                mon = ToLuminescent(mon);
            if (mon is null || mon.GetType() != pkmType)
                throw new InvalidOperationException(
                    $"The living dex holds a {mon?.GetType().Name ?? "unreadable"} Pokémon where this save stores {pkmType.Name}. Nothing was written.");
            if (mon.Species == 0 || mon.Species != species || !mon.ChecksumValid)
                throw new InvalidDataException("The living dex file is damaged. Nothing was written.");
            mons.Add(mon);
        }
        return mons;
    }

    private static PB8LUMI ToLuminescent(PKM mon)
    {
        var data = new byte[mon.SIZE_PARTY];
        mon.WriteDecryptedDataParty(data);
        return new PB8LUMI(data);
    }

    private static string Describe(EntityContext context) => context switch
    {
        EntityContext.Gen7b => "Let's Go Pikachu/Eevee",
        EntityContext.Gen8 => "Sword/Shield",
        EntityContext.Gen8a => "Legends: Arceus",
        EntityContext.Gen8b => "Brilliant Diamond/Shining Pearl",
        EntityContext.Gen9 => "Scarlet/Violet",
        EntityContext.Gen9a => "Legends: Z-A",
        EntityContext.None => "an unknown game",
        _ => $"generation {context.Generation}",
    };
}
