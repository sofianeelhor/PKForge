using System.IO.Compression;
using System.Text;
using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;

// Build-time living-dex bundle generator. Runs ONCE per release on a desktop: for every
// generation, legalize one of each species through the same GetLegalFromSet path the
// in-app creator uses, and pack the resulting mons into a compressed bundle the app
// copies onto the save instantly - zero on-device computation.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dexgen <output-directory> [generations]");
    return 1;
}

var output = args[0];
var generations = args.Length > 1
    ? args[1].Split(',').Select(int.Parse).ToArray()
    : [1, 2, 3, 4, 5, 6, 7, 8, 9];

Directory.CreateDirectory(output);
var engine = new SaveEngine();
var legalizer = new LegalizerService();

foreach (var generation in generations)
{
    var bundle = Path.Combine(output, $"dex-g{generation}.bin.gz");
    var built = 0;
    var failed = new List<int>();
    try
    {
        using var session = engine.OpenBlankSession(generation);
        var maxSpecies = ((SaveEngineSession)session).MaxSpeciesID;

        using var raw = new MemoryStream();
        using var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true);
        writer.Write(generation);
        writer.Write(0); // count placeholder

        for (var species = 1; species <= maxSpecies; species++)
        {
            try
            {
                var request = new GenerationRequest(Species: species, Level: 50, Shiny: false,
                    Nature: null, Ability: null, Ball: null, Moves: null);
                var entity = legalizer.GenerateData(session, request);
                if (entity is null || entity.Info.Species != species)
                {
                    // The legalizer occasionally answers a species request with a cousin
                    // (Nidoran-M -> Nidoran-F). Never bundle a lie: verify, then skip.
                    failed.Add(species);
                    continue;
                }
                writer.Write((ushort)species);
                writer.Write(entity.Data.Length);
                writer.Write(entity.Data);
                built++;
                if (built % 100 == 0)
                    Console.WriteLine($"g{generation}: {built}/{maxSpecies}");
            }
            catch
            {
                failed.Add(species);
            }
        }

        writer.Flush();
        raw.Position = 0;
        // patch the count
        using var patched = new MemoryStream();
        // header is 8 bytes: generation + count
        var header = new byte[8];
        raw.Read(header, 0, 8);
        BitConverter.GetBytes(built).CopyTo(header, 4);
        patched.Write(header);
        patched.Write(raw.ToArray(), 8, (int)raw.Length - 8);

        await using var file = File.Create(bundle);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        patched.Position = 0;
        await patched.CopyToAsync(gzip);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"g{generation} FAILED: {ex.Message}");
        if (File.Exists(bundle)) File.Delete(bundle);
        continue;
    }
    Console.WriteLine($"g{generation}: bundle {bundle} - {built} mons, {failed.Count} failed" +
        (failed.Count > 0 ? $" (first: {string.Join(',', failed.Take(10))})" : ""));
}
return 0;
