using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;

// Build-time living-dex bundle generator. Runs ONCE per release on a desktop: for every
// game family, legalize one of each species through the same GetLegalFromSet path the
// in-app creator uses, and pack the resulting mons into a compressed bundle the app
// copies onto the save instantly - zero on-device computation.
//
// Families are named by entity context, never by generation alone: Sword/Shield (8),
// Legends: Arceus (8a) and Brilliant Diamond/Shining Pearl (8b) are all generation 8 but
// store PK8, PA8 and PB8. Usage: dexgen <output-directory> [1,2,...,7b,8a,8b,9a]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dexgen <output-directory> [families, e.g. 1,8,8a,8b,7b,9a]");
    return 1;
}

var output = args[0];
var requested = args.Length > 1
    ? args[1].Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet()
    : null;

Directory.CreateDirectory(output);
var engine = new SaveEngine();
var legalizer = new LegalizerService();

foreach (var (context, version, fileName) in LivingDexBundles.Families)
{
    var token = context.ToString()[3..].ToLowerInvariant(); // Gen8a -> "8a"
    if (requested is not null && !requested.Contains(token))
        continue;
    var bundle = Path.Combine(output, fileName);
    var failed = new List<int>();
    var mons = new List<PKM>();
    try
    {
        using var session = (SaveEngineSession)engine.OpenBlankSession(version);
        var save = session.SaveFile;
        var maxSpecies = save.MaxSpeciesID;

        for (var species = 1; species <= maxSpecies; species++)
        {
            // Only species this game can hold at all (Legends: Arceus has 242, BDSP 493).
            if (!save.Personal.IsSpeciesInGame((ushort)species))
                continue;
            try
            {
                // The base form first; when the game only has a regional form (Legends:
                // Arceus has Hisuian Growlithe, Typhlosion, Zorua... and no Kantonian or
                // Johtonian one), the first form it does have.
                GeneratedEntity? entity = null;
                var formCount = save.Personal[(ushort)species].FormCount;
                for (byte form = 0; form < formCount && entity is null; form++)
                {
                    if (!save.Personal.IsPresentInGame((ushort)species, form))
                        continue;
                    var request = new GenerationRequest(Species: species, Level: 50, Shiny: false,
                        Nature: null, Ability: null, Ball: null, Moves: null, Form: form);
                    entity = legalizer.GenerateData(session, request);
                }
                if (entity is null || entity.Info.Species != species)
                {
                    // The legalizer occasionally answers a species request with a cousin
                    // (Nidoran-M -> Nidoran-F). Never bundle a lie: verify, then skip.
                    failed.Add(species);
                    continue;
                }
                var mon = EntityFormat.GetFromBytes(entity.Data, context);
                if (mon is null || mon.GetType() != save.PKMType || mon.Species != species
                    || !new LegalityAnalysis(mon).Valid)
                {
                    failed.Add(species);
                    continue;
                }
                mons.Add(mon);
                if (mons.Count % 100 == 0)
                    Console.WriteLine($"{token}: {mons.Count}");
            }
            catch
            {
                failed.Add(species);
            }
        }
        await File.WriteAllBytesAsync(bundle, LivingDexBundles.Write(context, mons));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{token} FAILED: {ex.Message}");
        if (File.Exists(bundle)) File.Delete(bundle);
        continue;
    }
    Console.WriteLine($"{token}: bundle {bundle} - {mons.Count} mons, {failed.Count} failed" +
        (failed.Count > 0 ? $" ({string.Join(',', failed)})" : ""));
}
return 0;
