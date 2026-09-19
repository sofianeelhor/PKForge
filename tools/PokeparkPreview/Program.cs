using PKForge.App.Services;
using PKForge.App.Views;
using PKForge.Domain;
using SkiaSharp;

var output = args.FirstOrDefault() ?? "/tmp/pokepark-qa";
Directory.CreateDirectory(output);
Directory.CreateDirectory(FileSystem.CacheDirectory);
var walking = new PokeparkSpriteService();
var residents = new[] { (1,"Bulbasaur"),(4,"Charmander"),(7,"Squirtle"),(25,"Pikachu"),(133,"Eevee"),(94,"Gengar"),(130,"Gyarados"),(448,"Lucario"),(700,"Sylveon"),(6,"Charizard"),(144,"Articuno"),(134,"Vaporeon") }
    .Select((m,i) => new ParkPokemon($"preview-{i}", m.Item1, 0, false, m.Item2, "Preview")).ToArray();
foreach (var mon in residents) walking.Warm(mon.Species, mon.Form, mon.Shiny, () => {});
int Loaded() => residents.Count(p => walking.GetFrame(p.Species,p.Form,p.Shiny,0,0) is not null);
var deadline = DateTime.UtcNow.AddSeconds(25);
while (Loaded() < residents.Length && DateTime.UtcNow < deadline) await Task.Delay(250);
Console.WriteLine($"PMD sprites loaded: {Loaded()}/{residents.Length}");
var scene = new PokeparkScene(new Offline(), walking) { Residents = residents };
for (var env = 0; env < 4; env++)
{
    scene.EnvironmentIndex = env;
    for (var i = 0; i < residents.Length; i++) scene.GetResidentState(i, 0);
    var map = ParkMap.For(env);
    var slug = Path.GetFileNameWithoutExtension(map.Asset);
    using var surface = SKSurface.Create(new SKImageInfo(960,540));
    scene.Draw(surface.Canvas,960,540,20000,labels:false);
    Save(surface,Path.Combine(output,$"{slug}-scene.png"));
    using var mask = new SKBitmap(ParkMap.Width,ParkMap.Height);
    var all = ParkTraversal.Land | ParkTraversal.Water | ParkTraversal.Lava | ParkTraversal.Air;
    for (var y=0;y<ParkMap.Height;y++) for(var x=0;x<ParkMap.Width;x++)
    {
        var terrain = map.TerrainAt(x,y);
        var color = terrain switch { ParkTerrain.Land => new SKColor(70,230,85), ParkTerrain.Water => new SKColor(20,150,255), ParkTerrain.Lava => new SKColor(255,100,20), ParkTerrain.Air => new SKColor(190,135,255), _ => new SKColor(255,30,60) };
        var fits = map.CanOccupy(x,y,all,12);
        mask.SetPixel(x,y,color.WithAlpha((byte)(fits ? 60 : 140)));
    }
    using var paint = new SKPaint();
    using var maskImage = SKImage.FromBitmap(mask);
    surface.Canvas.DrawImage(maskImage,new SKRect(0,0,960,540),paint);
    Save(surface,Path.Combine(output,$"{slug}-collision.png"));
    using var land = SKSurface.Create(new SKImageInfo(960,540));
    scene.Draw(land.Canvas,960,540,20000,labels:false);
    for (var y=0;y<ParkMap.Height;y++) for(var x=0;x<ParkMap.Width;x++)
        mask.SetPixel(x,y,map.CanOccupy(x,y,ParkTraversal.Land,12) ? new SKColor(40,255,80,70) : new SKColor(255,30,50,140));
    land.Canvas.DrawImage(maskImage,new SKRect(0,0,960,540),paint);
    Save(land,Path.Combine(output,$"{slug}-land-clearance.png"));
    using var sheet = SKSurface.Create(new SKImageInfo(1920,1080));
    for(var frame=0;frame<4;frame++)
    {
        scene.Draw(surface.Canvas,960,540,20000+frame*2000,labels:false);
        using var snapshot = surface.Snapshot();
        sheet.Canvas.DrawImage(snapshot,(frame%2)*960,(frame/2)*540);
    }
    Save(sheet,Path.Combine(output,$"{slug}-motion.png"));
    using var tall = SKSurface.Create(new SKImageInfo(540,960));
    scene.Draw(tall.Canvas,540,960,28000,labels:false);
    var view = PokeparkScene.Viewport(540,960);
    var found=0;
    for(var i=0;i<residents.Length;i++)
    {
        var state=scene.GetResidentState(i,28000);
        var hit=scene.HitTest((view.Left+state.X/480*view.Width)/540,(view.Top+(state.Y-15)/270*view.Height)/960,28000);
        if(hit==i) found++;
    }
    if(scene.HitTest(.5f,.01f,28000)!=-1 || scene.HitTest(.5f,.99f,28000)!=-1 || found!=residents.Length)
        throw new InvalidOperationException($"{slug}: tall viewport hit test failed ({found}/{residents.Length}).");
    Save(tall,Path.Combine(output,$"{slug}-tall.png"));
    Console.WriteLine($"{slug}: {found} viewport hit targets verified; margins rejected.");
}
Console.WriteLine($"Rendered actual scene and collision overlays to {output}");
static void Save(SKSurface surface,string path) { using var image=surface.Snapshot(); using var data=image.Encode(SKEncodedImageFormat.Png,100); using var stream=File.Create(path); data.SaveTo(stream); }

namespace PKForge.App.Services
{
    public sealed record ParkPokemon(string Id,int Species,int Form,bool Shiny,string Name,string Source);
    public interface ISpriteService { SKBitmap? GetSprite(int species,int form,bool shiny); }
    public sealed class Offline : ISpriteService
    {
        private readonly Dictionary<int,SKBitmap?> _sprites=new();
        public SKBitmap? GetSprite(int species,int form,bool shiny)
        {
            if(_sprites.TryGetValue(species,out var sprite)) return sprite;
            var path=Path.Combine(FileSystem.Root,"external/PKHeX/PKHeX.Drawing.PokeSprite/Resources/img/Big Pokemon Sprites",$"b_{species}.png");
            return _sprites[species]=File.Exists(path)?SKBitmap.Decode(path):null;
        }
    }
}
public static class FileSystem
{
    public static readonly string Root=FindRoot();
    public static string CacheDirectory => Environment.GetEnvironmentVariable("POKEPARK_QA_CACHE") ?? "/tmp/pkforge-park-qa/cache";
    public static Task<Stream> OpenAppPackageFileAsync(string name)
    {
        var resources=Path.Combine(Root,"src/PKForge.App/Resources");
        var path=name.StartsWith("pokepark/maps/")?Path.Combine(resources,"Pokepark/maps",Path.GetFileName(name)):Path.Combine(resources,"Fonts",name);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
    private static string FindRoot()
    {
        var current=new DirectoryInfo(AppContext.BaseDirectory);
        while(current is not null) { if(Directory.Exists(Path.Combine(current.FullName,"src/PKForge.App"))) return current.FullName; current=current.Parent; }
        throw new DirectoryNotFoundException("Run from a built checkout of PKForge.");
    }
}
