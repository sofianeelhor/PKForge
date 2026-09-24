using PKHeX.Core;

// Dumps PKHeX's English form names per (species, form, context) as TSV for
// tools/SpriteForms/build.py, which maps them onto PokeAPI form identifiers.
// Source of truth: PKHeX.Core FormConverter.GetFormList - the same names PKHeX shows.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: formdump <output.tsv>");
    return 1;
}

var strings = GameInfo.GetStrings("en");
EntityContext[] contexts =
[
    EntityContext.Gen9, EntityContext.Gen9a, EntityContext.Gen8, EntityContext.Gen8a,
    EntityContext.Gen8b, EntityContext.Gen7, EntityContext.Gen7b, EntityContext.Gen6,
];
using var writer = new StreamWriter(args[0]);
foreach (var context in contexts)
{
    for (ushort species = 1; species <= (ushort)Species.Pecharunt; species++)
    {
        var names = FormConverter.GetFormList(species, strings.types, strings.forms, GameInfo.GenderSymbolASCII, context);
        for (var form = 0; form < names.Length; form++)
            writer.WriteLine($"{species}\t{form}\t{context}\t{strings.specieslist[species]}\t{names[form]}");
    }
}
return 0;
