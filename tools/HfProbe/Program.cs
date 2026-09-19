using PKHeX.Core;
foreach (var (sp,label) in new (int,string)[]{(1,"Bulbasaur"),(25,"Pikachu"),(7,"Squirtle"),(6,"Charizard"),(130,"Gyarados"),(448,"Lucario"),(890,"Eternatus"),(321,"Wailord"),(172,"Pichu"),(95,"Onix"),(143,"Snorlax"),(3,"Venusaur"),(700,"Sylveon"),(94,"Gengar"),(144,"Articuno")})
{
    var e = PersonalTable.SV.GetFormEntry((ushort)sp,0);
    System.Console.WriteLine($"{label,-10} Height={e.Height,4} Weight={e.Weight,5} Type1={e.Type1} Type2={e.Type2}");
}
