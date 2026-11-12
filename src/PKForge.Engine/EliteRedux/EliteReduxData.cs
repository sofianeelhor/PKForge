using System.Reflection;
using System.Text.Json;
using PKForge.Domain;
using PKHeX.Core;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.Unbound;

namespace PKForge.Engine.EliteRedux;

/// <summary>
/// Elite Redux's own tables, generated from the hack's config repository (see Data/README.md):
/// every species id with its national dex number, form, base stats, types, three ability
/// slots, innates and gender ratio, plus the move (name, PP), item (name, pocket) and ability
/// name tables. Species, moves and abilities are bridged to PKHeX's national ids by name, as
/// the app names them through PKHeX; items stay in the hack's own id space
/// (<see cref="ISaveEngineSession.GetItemNames"/>).
/// </summary>
internal sealed class EliteReduxData
{
    private static readonly Lazy<EliteReduxData> Lazy = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    public static EliteReduxData Instance => Lazy.Value;

    private readonly Dictionary<int, SpeciesEntry> _species;
    private readonly Dictionary<int, int> _speciesByNational = [];
    private readonly Dictionary<int, MoveEntry> _moves;
    private readonly Dictionary<int, ItemEntry> _items;
    private readonly Dictionary<int, string> _abilities;
    private readonly CfruIdBridge _moveBridge;
    private readonly CfruIdBridge _abilityBridge;
    private readonly Dictionary<int, int> _ballByItem = [];
    private readonly Dictionary<int, int> _itemByBall = [];
    private Dictionary<(int National, int Form), int>? _speciesByForm;
    private readonly Lock _gate = new();

    public int MaxSpecies { get; }
    public int MaxMove { get; }
    public int MaxItem { get; }

    private EliteReduxData(Pack pack)
    {
        _species = pack.Species.ToDictionary(entry => entry.Id);
        MaxSpecies = pack.MaxSpecies;
        MaxMove = pack.MaxMove;
        MaxItem = pack.MaxItem;
        foreach (var entry in pack.Species.OrderBy(entry => entry.Id))
            if (entry.Nat > 0 && entry.Form.Length == 0 && !entry.Hidden)
                _speciesByNational.TryAdd(entry.Nat, entry.Id);
        foreach (var entry in pack.Species.OrderBy(entry => entry.Id))
            if (entry.Nat > 0 && !entry.Hidden)
                _speciesByNational.TryAdd(entry.Nat, entry.Id);

        _moves = pack.Moves.ToDictionary(move => move.Id);
        _items = pack.Items.ToDictionary(item => item.Id);
        _abilities = pack.Abilities.ToDictionary(ability => ability.Id, ability => ability.Name);
        var strings = GameInfo.GetStrings("en");
        _moveBridge = new CfruIdBridge(pack.Moves.ToDictionary(move => move.Id, move => move.Name), strings.movelist, []);
        _abilityBridge = new CfruIdBridge(new Dictionary<int, string>(_abilities), strings.abilitylist, []);

        // The ball field stores the ball's item id (5 bits). PKHeX numbers balls on their own.
        for (var ball = 1; ball < strings.balllist.Length; ball++)
        {
            var key = UnboundData.NormalizeName(strings.balllist[ball]);
            if (key.Length == 0) continue;
            foreach (var item in pack.Items)
                if (item.Id is > 0 and <= BallFieldMax && UnboundData.NormalizeName(item.Name) == key)
                {
                    _ballByItem.TryAdd(item.Id, ball);
                    _itemByBall.TryAdd(ball, item.Id);
                }
        }
    }

    /// <summary>Highest item id the 5-bit ball field can hold.</summary>
    public const int BallFieldMax = 31;

    private static EliteReduxData Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("eliteredux.eliteredux.json")
            ?? throw new InvalidDataException("The Elite Redux data pack is missing from the build.");
        var pack = JsonSerializer.Deserialize<Pack>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The Elite Redux data pack could not be read.");
        return new EliteReduxData(pack);
    }

    // ── Species ──

    public bool IsKnownSpecies(int species) => _species.ContainsKey(species);

    /// <summary>The national dex number behind a species (its base species' for a form), or 0
    /// for one of the hack's own Pokémon, which has no national twin.</summary>
    public int NationalIdOf(int species) => Find(species)?.Nat ?? 0;

    /// <summary>The hack's id for a national species: its base form, or 0 when the hack lacks it.</summary>
    public int SpeciesFromNational(int national) => _speciesByNational.GetValueOrDefault(national);

    /// <summary>The hack's id for a national species and the PKHeX form the snapshot shows,
    /// falling back to the base form.</summary>
    public int SpeciesFromNational(int national, int form)
    {
        if (form == 0) return SpeciesFromNational(national);
        lock (_gate)
        {
            _speciesByForm ??= BuildFormIndex();
            return _speciesByForm.TryGetValue((national, form), out var id) ? id : SpeciesFromNational(national);
        }
    }

    private Dictionary<(int, int), int> BuildFormIndex()
    {
        var index = new Dictionary<(int, int), int>();
        foreach (var entry in _species.Values.OrderBy(entry => entry.Id))
            if (entry.Nat > 0 && entry.Form.Length > 0 && !entry.Hidden)
                index.TryAdd((entry.Nat, RomhackForms.Resolve(entry.Nat, SpeciesName(entry.Id)).Form), entry.Id);
        return index;
    }

    /// <summary>"Golem-Alola", "Kirlia-Redux", "Eevee-Partner": the base name and the form
    /// suffix in the Showdown style <see cref="RomhackForms"/> reads.</summary>
    public string SpeciesName(int species)
    {
        if (Find(species) is not { } entry) return $"Species {species}";
        var name = entry.Name.Length > 0 ? entry.Name : $"Species {species}";
        return entry.Form.Length > 0 ? $"{name}-{entry.Form}" : name;
    }

    public int[] BaseStats(int species) => Find(species)?.Stats ?? [0, 0, 0, 0, 0, 0];

    public IReadOnlyList<int> Types(int species) => Find(species)?.Types.Where(type => type >= 0).Distinct().ToArray() ?? [];

    /// <summary>The three ability slots, padded as the game's generator pads them.</summary>
    public int[] AbilitySlots(int species) => Find(species)?.Abilities is { Length: 3 } slots ? slots : [0, 0, 0];

    public int[] Innates(int species) => Find(species)?.Innates ?? [];

    /// <summary>The ability the game gives a slot: slot 0's when the slot is empty or out of range.</summary>
    public int Ability(int species, int slot)
    {
        var slots = AbilitySlots(species);
        var ability = slot is >= 0 and < 3 ? slots[slot] : slots[0];
        return ability == 0 ? slots[0] : ability;
    }

    /// <summary>The Gen 3 gender ratio byte (0 male, 254 female, 255 genderless).</summary>
    public int GenderRatio(int species) => Find(species)?.Gender ?? 255;

    /// <summary>The game's GetGenderFromSpeciesAndPersonality: 0 male, 1 female, 2 genderless.</summary>
    public int GenderOf(uint pid, int species)
    {
        var ratio = GenderRatio(species);
        // The game nudges every Sylveon form's personality by one before comparing.
        var nudge = NationalIdOf(species) == (int)PKHeX.Core.Species.Sylveon ? 1u : 0u;
        return ratio switch
        {
            0 => 0,
            254 => 1,
            255 => 2,
            _ => ratio > (int)((pid + nudge) & 0xFF) ? 1 : 0,
        };
    }

    // ── Moves, items, abilities ──

    public bool IsKnownMove(int move) => move == 0 || _moves.ContainsKey(move);
    public string MoveName(int move) => _moves.TryGetValue(move, out var entry) ? entry.Name : $"Move {move}";
    public int MovePp(int move) => _moves.TryGetValue(move, out var entry) ? entry.Pp : 0;
    public int MoveToNational(int move) => move == 0 ? 0 : _moveBridge.ToNational(move);
    public int MoveFromNational(int move) => move == 0 ? 0 : _moveBridge.FromNational(move);

    public string AbilityName(int ability) => _abilities.TryGetValue(ability, out var name) ? name : $"Ability {ability}";
    public int AbilityToNational(int ability) => ability == 0 ? 0 : _abilityBridge.ToNational(ability);

    public bool IsKnownItem(int item) => item == 0 || _items.ContainsKey(item);
    public string ItemName(int item) => item == 0 ? "None" : _items.TryGetValue(item, out var entry) ? entry.Name : $"Item {item}";

    /// <summary>The bag pocket an item sits in, or null for an item the bag never lists.</summary>
    public string? Pocket(int item) => _items.TryGetValue(item, out var entry) && entry.Bag && entry.Pocket != "NONE" ? entry.Pocket : null;

    public IEnumerable<int> ItemIds => _items.Keys;

    public int BallToNational(int itemId) => _ballByItem.GetValueOrDefault(itemId);
    public int BallFromNational(int ball) => _itemByBall.GetValueOrDefault(ball);

    private SpeciesEntry? Find(int species) => _species.GetValueOrDefault(species);

    // ── Pack model ──

    private sealed class Pack
    {
        public int MaxSpecies { get; set; }
        public int MaxMove { get; set; }
        public int MaxItem { get; set; }
        public List<SpeciesEntry> Species { get; set; } = [];
        public List<MoveEntry> Moves { get; set; } = [];
        public List<ItemEntry> Items { get; set; } = [];
        public List<AbilityEntry> Abilities { get; set; } = [];
    }

    private sealed class SpeciesEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Form { get; set; } = string.Empty;
        public int Nat { get; set; }
        public int[] Stats { get; set; } = [];
        public int[] Types { get; set; } = [];
        public int[] Abilities { get; set; } = [];
        public int[] Innates { get; set; } = [];
        public int Gender { get; set; }
        public bool Hidden { get; set; }
    }

    private sealed class MoveEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Pp { get; set; }
    }

    private sealed class ItemEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Pocket { get; set; } = string.Empty;
        public bool Bag { get; set; }
    }

    private sealed class AbilityEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
