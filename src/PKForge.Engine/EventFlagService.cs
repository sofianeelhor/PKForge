using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A flag or a work value (script var).</summary>
public enum EventEntryKind { Flag, Work }

/// <summary>Which flag array an entry lives in. Only BDSP has two (event + system flags);
/// Gen 8/9 flags are boolean save blocks addressed by their 32-bit key.</summary>
public enum EventFlagBank { Event, System, Block }

/// <summary>A named value PKHeX's label file lists for a work var ("0:Not Activated,4617:Activated").</summary>
public sealed record EventWorkPreset(string Name, long Value);

/// <summary>
/// One flag or work var as the editor lists it. Flags use 0/1 in <see cref="Value"/>.
/// <see cref="Original"/> is the value in the file as it was opened.
/// </summary>
/// <param name="Id">Display index: decimal for Gen 1-8 (PKHeX label files are decimal), the block key in hex for Gen 8/9 blocks.</param>
/// <param name="Label">PKHeX's label, or null when PKHeX has none for this index.</param>
/// <param name="Category">Section: the label file's category, or the game's own flag range for unlabeled entries.</param>
/// <param name="Risky">PKHeX tags it story progress, or it is a system flag/var: flipping it can break the story.</param>
public sealed record EventEntry(
    EventEntryKind Kind,
    EventFlagBank Bank,
    int Index,
    string Id,
    string? Label,
    string Category,
    long Value,
    long Original,
    long Min,
    long Max,
    bool Risky,
    IReadOnlyList<EventWorkPreset> Presets)
{
    public bool IsOn => Value != 0;
    public bool Changed => Value != Original;
    public bool Labeled => Label is not null;
    public string DisplayName => Label ?? (Kind == EventEntryKind.Flag ? $"Flag {Id}" : $"Work {Id}");

    /// <summary>The preset name for the current value, when the label file names it.</summary>
    public string? ValueName => Presets.FirstOrDefault(p => p.Value == Value)?.Name;
}

/// <summary>Everything the editor shows for one save.</summary>
/// <param name="LabelSource">Which PKHeX resource the labels came from (shown in the footer).</param>
/// <param name="HasBaseline">False when there is no opened-file image to diff against.</param>
public sealed record EventCatalog(
    string Game,
    string LabelSource,
    IReadOnlyList<EventEntry> Flags,
    IReadOnlyList<EventEntry> Work,
    bool HasBaseline);

/// <summary>A list view over a catalog: section, search text, unlabeled toggle, changed-only.</summary>
/// <param name="Category">Null for every section.</param>
public sealed record EventFilter(string? Category = null, string? Query = null, bool ShowUnlabeled = false, bool ChangedOnly = false);

/// <summary>
/// Event flag and work (script var) editor over PKHeX's own accessors and label files, the
/// same data PKHeX.WinForms SAVEditor.B_OpenEventFlags_Click routes to:
///  - Gen 1 <see cref="SAV1"/> flags/work (byte); PKHeX has no label file, so the only names are
///    the static-encounter flags from PKHeX.Core G1OverworldSpawner.
///  - Gen 2 <see cref="SAV2"/> (SAV_EventFlags2): flags + byte work, labels gen2/flags_{gs,c}, const_{gs,c}.
///  - Gen 3-7 <see cref="IEventFlag37"/> / <see cref="IEventFlagProvider37"/> (SAV_EventFlags, EventWorkspace):
///    flags + u16 work, labels gen3..7/flags_*, const_* parsed by <see cref="EventLabelParsing"/>.
///  - Let's Go <see cref="SAV7b"/> (SAV_EventWork, SplitEventEditor): typed flag/work groups,
///    labels gen7/flags_gg, const_gg parsed by <see cref="EventWorkUtil.GetVars"/>.
///  - BDSP <see cref="SAV8BS"/> (SAV_FlagWork8b): event + system flags + i32 work, labels
///    gen8/flag_bdsp, system_bdsp, work_bdsp (<see cref="EventLabelCollectionSystem"/>).
///  - SW/SH, Legends: Arceus, S/V, Legends: Z-A: flags are boolean SCBlocks, listed and written
///    through <see cref="SaveBlockEditorService"/> (numbers stay in the Save blocks editor).
/// </summary>
public static class EventFlagService
{
    private const long WorkMaxU16 = ushort.MaxValue;
    private const long WorkMaxU8 = byte.MaxValue;
    private const string Unlabeled = "Unlabeled";

    private static readonly ConditionalWeakTable<SaveEngineSession, Baseline> Baselines = new();
    private static readonly ConcurrentDictionary<string, Labels> LabelCache = new();

    /// <summary>Why this session has no event editor, or null when it has one.</summary>
    public static string? UnsupportedReason(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engine)
            return "ROM hacks (Unbound, Radical Red) move the flag table and use their own numbering; PKHeX has no labels for them.";
        return engine.SaveFile switch
        {
            SAV1 or SAV2 or IEventFlag37 or IEventFlagProvider37 or SAV7b or SAV8BS => null,
            ISCBlockArray => null,
            SAV3Colosseum or SAV3XD => "Colosseum/XD keep story state in their own script structures; PKHeX has no event flag editor for them.",
            SAV4BR => "Battle Revolution has no story, so there are no event flags.",
            _ => "PKHeX exposes no event flags for this save type.",
        };
    }

    public static bool IsSupported(ISaveEngineSession session) => UnsupportedReason(session) is null;

    /// <summary>Every flag and work var, labeled where PKHeX has a label.</summary>
    public static EventCatalog Load(ISaveEngineSession session)
    {
        if (UnsupportedReason(session) is { } reason)
            throw new NotSupportedException(reason);
        var engine = (SaveEngineSession)session;
        var backend = Backend.For(engine.SaveFile);
        var baseline = Baselines.GetValue(engine, BuildBaseline);
        var original = baseline.Backend;

        var flags = new List<EventEntry>();
        foreach (var bank in backend.Banks)
        {
            var before = original?.Banks.FirstOrDefault(b => b.Bank == bank.Bank);
            foreach (var index in bank.Indices())
            {
                var value = bank.Get(index);
                var was = before is not null && before.Has(index) ? before.Get(index) : value;
                bank.Labels.TryGetValue(index, out var label);
                flags.Add(new EventEntry(EventEntryKind.Flag, bank.Bank, index, bank.Id(index), label?.Name,
                    label?.Category ?? bank.Section(index), value ? 1 : 0, was ? 1 : 0, 0, 1,
                    label?.Risky ?? bank.Risky, []));
            }
        }

        var work = new List<EventEntry>();
        if (backend.Work is { } vars)
        {
            for (var index = 0; index < vars.Count; index++)
            {
                var value = vars.Get(index);
                var was = original?.Work is { } w && index < w.Count ? w.Get(index) : value;
                vars.Labels.TryGetValue(index, out var label);
                work.Add(new EventEntry(EventEntryKind.Work, EventFlagBank.Event, index, index.ToString("0000", CultureInfo.InvariantCulture),
                    label?.Name, label?.Category ?? vars.Section(index), value, was, vars.Min, vars.Max,
                    label?.Risky ?? vars.Risky, label?.Presets ?? []));
            }
        }

        return new EventCatalog(backend.Game, backend.Source, flags, work, original is not null);
    }

    /// <summary>Sections in list order, for the L/R pager. Only sections the filter can show.</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<EventEntry> entries, bool showUnlabeled) =>
        entries.Where(e => showUnlabeled || e.Labeled).Select(e => e.Category).Distinct().ToList();

    /// <summary>
    /// Applies <paramref name="filter"/>. A number in the query ("137", "#137", "0x89") finds exactly
    /// that index, even when it is unlabeled, so a flag number from a guide is always reachable;
    /// any other text matches the label, the displayed id (block keys) or the section.
    /// </summary>
    public static IReadOnlyList<EventEntry> Filter(IEnumerable<EventEntry> entries, EventFilter filter)
    {
        var query = filter.Query?.Trim() ?? "";
        var number = ParseNumber(query);
        return entries.Where(e =>
        {
            if (filter.ChangedOnly && !e.Changed) return false;
            if (filter.Category is { } category && e.Category != category) return false;
            if (query.Length == 0)
                return filter.ShowUnlabeled || e.Labeled || filter.ChangedOnly;
            if (number is { } n)
                return e.Index == n || (e.Bank == EventFlagBank.Block && unchecked((uint)e.Index) == n);
            if (!filter.ShowUnlabeled && !e.Labeled) return false;
            return (e.Label?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                   || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || e.Category.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    public static GenerationOutcome SetFlag(ISaveEngineSession session, EventFlagBank bank, int index, bool value)
    {
        if (session is not SaveEngineSession engine || !IsSupported(session))
            return new GenerationOutcome(false, "This save has no event flags.");
        var backend = Backend.For(engine.SaveFile);
        var flags = backend.Banks.FirstOrDefault(b => b.Bank == bank);
        if (flags is null || !flags.Has(index))
            return new GenerationOutcome(false, $"No flag {index} in this save.");
        if (bank == EventFlagBank.Block)
            return SaveBlockEditorService.SetBool(session, unchecked((uint)index), value);
        flags.Set(index, value);
        backend.AfterWrite?.Invoke();
        flags.Labels.TryGetValue(index, out var label);
        return new GenerationOutcome(true, $"Flag {flags.Id(index)}{(label is null ? "" : $" {label.Name}")} {(value ? "set" : "cleared")}");
    }

    /// <summary>Stores a work var; values outside the var's storage range are refused, never wrapped.</summary>
    public static GenerationOutcome SetWork(ISaveEngineSession session, int index, long value)
    {
        if (session is not SaveEngineSession engine || !IsSupported(session))
            return new GenerationOutcome(false, "This save has no event work.");
        var backend = Backend.For(engine.SaveFile);
        if (backend.Work is not { } work || (uint)index >= (uint)work.Count)
            return new GenerationOutcome(false, $"No work {index} in this save.");
        if (value < work.Min || value > work.Max)
            return new GenerationOutcome(false, $"Work {index} holds {work.Min}–{work.Max}; {value} does not fit.");
        work.Set(index, value);
        backend.AfterWrite?.Invoke();
        work.Labels.TryGetValue(index, out var label);
        return new GenerationOutcome(true, $"Work {index:0000}{(label is null ? "" : $" {label.Name}")} = {value}");
    }

    private static long? ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        if (text.StartsWith('#')) text = text[1..];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }

    // ── Baseline (the file as opened) ──

    private sealed class Baseline(Backend? backend)
    {
        public Backend? Backend { get; } = backend;
    }

    /// <summary>Parses the opened bytes once per session. An in-memory session has none, and
    /// then the first read becomes the baseline, which is what the player has seen.</summary>
    private static Baseline BuildBaseline(SaveEngineSession session)
    {
        var bytes = session.OriginalBytes;
        if (bytes.Length != 0 && SaveParser.TryGetSaveFile(bytes.ToArray(), out var save) && save is not null
            && save.GetType() == session.SaveFile.GetType())
            return new Baseline(Backend.For(save));
        return new Baseline(Snapshot(session.SaveFile));
    }

    /// <summary>A frozen copy of the current values, for saves with no file image.</summary>
    private static Backend? Snapshot(SaveFile save)
    {
        var live = Backend.For(save);
        var banks = live.Banks.Select(bank =>
        {
            var values = bank.Indices().ToDictionary(i => i, bank.Get);
            return bank with { Get = i => values[i], Set = (_, _) => { }, Has = values.ContainsKey, Indices = () => values.Keys };
        }).ToArray();
        WorkVars? work = null;
        if (live.Work is { } w)
        {
            var values = Enumerable.Range(0, w.Count).Select(w.Get).ToArray();
            work = w with { Get = i => values[i], Set = (_, _) => { } };
        }
        return live with { Banks = banks, Work = work };
    }

    // ── Backends ──

    private sealed record Label(string Name, string Category, bool Risky, IReadOnlyList<EventWorkPreset> Presets);

    private sealed record Labels(Dictionary<int, Label> Flags, Dictionary<int, Label> System, Dictionary<int, Label> Work)
    {
        public static readonly Labels None = new([], [], []);
    }

    private sealed record FlagBank(
        EventFlagBank Bank,
        Func<IEnumerable<int>> Indices,
        Func<int, bool> Has,
        Func<int, bool> Get,
        Action<int, bool> Set,
        Func<int, string> Id,
        Func<int, string> Section,
        IReadOnlyDictionary<int, Label> Labels,
        bool Risky = false);

    private sealed record WorkVars(
        int Count,
        Func<int, long> Get,
        Action<int, long> Set,
        long Min,
        long Max,
        Func<int, string> Section,
        IReadOnlyDictionary<int, Label> Labels,
        bool Risky = false);

    private sealed record Backend(string Game, string Source, FlagBank[] Banks, WorkVars? Work, Action? AfterWrite = null)
    {
        public static Backend For(SaveFile save) => save switch
        {
            SAV1 sav1 => Gen1(sav1),
            SAV2 sav2 => Gen2(sav2),
            SAV7b gg => LetsGo(gg),
            SAV8BS bdsp => Bdsp(bdsp),
            IEventFlag37 direct => Gen3To7(save, direct),
            IEventFlagProvider37 provider => Gen3To7(save, provider.EventWork),
            ISCBlockArray blocks => Blocks(blocks),
            _ => throw new NotSupportedException(),
        };
    }

    private static string Decimal(int index) => index.ToString("0000", CultureInfo.InvariantCulture);

    private static FlagBank LinearBank(IEventFlagArray flags, IReadOnlyDictionary<int, Label> labels, Func<int, string>? section = null) =>
        new(EventFlagBank.Event, () => Enumerable.Range(0, flags.EventFlagCount), i => (uint)i < (uint)flags.EventFlagCount,
            flags.GetEventFlag, flags.SetEventFlag, Decimal, section ?? (_ => Unlabeled), labels);

    // Gen 1: PKHeX.Core G1OverworldSpawner is the only named Gen 1 flag set (its event-flag half;
    // the sprite hide/show half lives in SAV1.EventSpawnFlags, which PKHeX's reset button also clears).
    private static Backend Gen1(SAV1 save)
    {
        var yellow = save.Version == GameVersion.YW;
        var names = new Dictionary<int, string>
        {
            [0x045] = "Eevee (Celadon Mansion)", [0x069] = "Aerodactyl (Old Amber)",
            [0x356] = "Hitmonlee (Fighting Dojo)", [0x357] = "Hitmonchan (Fighting Dojo)",
            [0x461] = "Voltorb 1 (Power Plant)", [0x462] = "Voltorb 2 (Power Plant)", [0x463] = "Voltorb 3 (Power Plant)",
            [0x464] = "Electrode 1 (Power Plant)", [0x465] = "Voltorb 4 (Power Plant)", [0x466] = "Voltorb 5 (Power Plant)",
            [0x467] = "Electrode 2 (Power Plant)", [0x468] = "Voltorb 6 (Power Plant)",
            [0x469] = "Zapdos", [0x53E] = "Moltres", [0x57E] = "Kabuto (Dome Fossil)", [0x57F] = "Omanyte (Helix Fossil)",
            [0x8C1] = "Mewtwo", [0x9DA] = "Articuno",
        };
        if (yellow)
        {
            names[0x0A8] = "Bulbasaur (Cerulean City)";
            names[0x147] = "Squirtle (Vermilion City)";
            names[0x54F] = "Charmander (Route 24)";
        }
        var labels = names.ToDictionary(pair => pair.Key, pair => new Label(pair.Value, "Encounters", false, []));
        var work = new WorkVars(save.EventWorkCount, i => save.GetWork(i), (i, v) => save.SetWork(i, (byte)v), 0, WorkMaxU8,
            _ => Unlabeled, new Dictionary<int, Label>());
        return new Backend("Gen 1", "PKHeX G1OverworldSpawner (static encounters only)", [LinearBank(save, labels)], work);
    }

    private static Backend Gen2(SAV2 save)
    {
        var game = save.Version == GameVersion.C ? "c" : "gs";
        var labels = LoadClassic(game, save.EventFlagCount, save.EventWorkCount);
        var work = new WorkVars(save.EventWorkCount, i => save.GetWork(i), (i, v) => save.SetWork(i, (byte)v), 0, WorkMaxU8,
            _ => Unlabeled, labels.Work);
        return new Backend(game.ToUpperInvariant(), $"PKHeX gen2/flags_{game}, const_{game}", [LinearBank(save, labels.Flags)], work);
    }

    private static Backend Gen3To7(SaveFile save, IEventFlag37 events)
    {
        var game = save switch
        {
            SAV3E => "e",
            SAV3RS => "rs",
            SAV3FRLG => "frlg",
            SAV4Pt => "pt",
            SAV4DP => "dp",
            SAV4HGSS => "hgss",
            SAV5B2W2 => "b2w2",
            SAV5BW => "bw",
            SAV6AO => "oras",
            SAV6XY => "xy",
            SAV7USUM => "usum",
            SAV7SM => "sm",
            _ => null,
        };
        var labels = game is null ? Labels.None : LoadClassic(game, events.EventFlagCount, events.EventWorkCount);
        var work = new WorkVars(events.EventWorkCount, i => events.GetWork(i), (i, v) => events.SetWork(i, (ushort)v), 0, WorkMaxU16,
            _ => Unlabeled, labels.Work);
        // PKHeX EventWorkspace.Save: SM/USUM keep the QR-scan flag's magic constants in step.
        Action? after = events is EventWork7 gen7 ? gen7.UpdateQrConstants : null;
        var source = game is null ? "no PKHeX labels" : $"PKHeX gen{save.Generation}/flags_{game}, const_{game}";
        return new Backend(game?.ToUpperInvariant() ?? save.Version.ToString(), source, [LinearBank(events, labels.Flags)], work, after);
    }

    // Let's Go: flags_gg/const_gg lines are "<type><relative index>\t<name>" (z/s/v/c/e), resolved to
    // raw indices by the block itself, as SplitEventEditor does. The System group is marked risky.
    private static Backend LetsGo(SAV7b save)
    {
        var block = save.EventWork;
        var labels = LabelCache.GetOrAdd($"gg:{GameInfo.CurrentLanguage}", _ =>
        {
            var flags = new Dictionary<int, Label>();
            var work = new Dictionary<int, Label>();
            foreach (var group in EventWorkUtil.GetVars(Lines("gg", "flags"), (i, t, d) => new EventFlag(i, t, d)))
                foreach (var item in group.Vars)
                    AddTyped(flags, item, () => block.GetFlagRawIndex(item.Type, item.RelativeIndex), block.CountFlag, isWork: false);
            foreach (var group in EventWorkUtil.GetVars(Lines("gg", "const"), (i, t, d) => new EventWork<int>(i, t, d)))
                foreach (var item in group.Vars)
                    AddTyped(work, item, () => block.GetWorkRawIndex(item.Type, item.RelativeIndex), block.CountWork, isWork: true);
            return new Labels(flags, [], work);
        });
        var bank = new FlagBank(EventFlagBank.Event, () => Enumerable.Range(0, block.CountFlag), i => (uint)i < (uint)block.CountFlag,
            block.GetFlag, (i, v) => block.SetFlag(i, v), Decimal, i => TypeName(block.GetFlagType(i, out _)), labels.Flags);
        var work = new WorkVars(block.CountWork, i => block.GetWork(i), (i, v) => block.SetWork(i, (int)v), int.MinValue, int.MaxValue,
            i => SafeWorkType(block, i), labels.Work);
        return new Backend("GG", "PKHeX gen7/flags_gg, const_gg", [bank], work);
    }

    /// <summary>A label whose relative index falls outside its group is skipped, not fatal.</summary>
    private static void AddTyped(Dictionary<int, Label> into, EventVar item, Func<int> raw, int count, bool isWork)
    {
        int index;
        try { index = raw(); }
        catch (ArgumentOutOfRangeException) { return; }
        if ((uint)index < (uint)count)
            into.TryAdd(index, new Label(item.Name, TypeName(item.Type, isWork), item.Type == EventVarType.System, []));
    }

    private static string SafeWorkType(EventWork7b block, int index)
    {
        try { return TypeName(block.GetWorkType(index, out _), work: true); }
        catch (ArgumentOutOfRangeException) { return "Unused"; }
    }

    /// <summary>PKHeX aliases Scene = Vanish: the same group value is "vanish" flags but "scene" work.</summary>
    private static string TypeName(EventVarType type, bool work = false) => type switch
    {
        EventVarType.Zone => "Zone",
        EventVarType.System => "System",
        EventVarType.Vanish => work ? "Scene" : "Visibility",
        EventVarType.Event => "Event",
        _ => type.ToString(),
    };

    // BDSP: PKHeX FlagWork8b range constants name the unlabeled flag sections
    // (FH hidden items, FE events, FV vanish, FT trainers, FV_FLD field vanish, TMFLG).
    private static Backend Bdsp(SAV8BS save)
    {
        var block = save.FlagWork;
        var labels = LabelCache.GetOrAdd($"bdsp:{GameInfo.CurrentLanguage}", _ =>
        {
            try
            {
                var set = new EventLabelCollectionSystem("bdsp", block.CountFlag, block.CountSystem, block.CountWork);
                return new Labels(ToLabels(set.Flag), ToLabels(set.System, risky: true), ToLabels(set.Work));
            }
            catch (ArgumentException)
            {
                return Labels.None;
            }
        });
        static string Section(int i) => i switch
        {
            < FlagWork8b.FH_END => "Hidden items",
            < FlagWork8b.FE_END => "Events",
            < FlagWork8b.FV_END => "Visibility",
            < FlagWork8b.FT_END => "Trainers",
            < FlagWork8b.FV_FLD_END => "Field visibility",
            _ => "TMs & misc",
        };
        var flags = new FlagBank(EventFlagBank.Event, () => Enumerable.Range(0, block.CountFlag), i => (uint)i < (uint)block.CountFlag,
            block.GetFlag, block.SetFlag, Decimal, Section, labels.Flags.ToDictionary(p => p.Key, p => p.Value with { Category = Section(p.Key) }));
        var system = new FlagBank(EventFlagBank.System, () => Enumerable.Range(0, block.CountSystem), i => (uint)i < (uint)block.CountSystem,
            block.GetSystemFlag, block.SetSystemFlag, i => $"S{Decimal(i)}", _ => "System", labels.System.ToDictionary(p => p.Key, p => p.Value with { Category = "System" }), Risky: true);
        var work = new WorkVars(block.CountWork, i => block.GetWork(i), (i, v) => block.SetWork(i, (int)v), int.MinValue, int.MaxValue,
            _ => Unlabeled, labels.Work);
        return new Backend("BDSP", "PKHeX gen8/flag_bdsp, system_bdsp, work_bdsp", [flags, system], work);
    }

    // SW/SH, PLA, S/V, Z-A: boolean blocks through SaveBlockEditorService (the key is the index).
    private static Backend Blocks(ISCBlockArray array)
    {
        // Bool3 is only ever an array element type, never a stored single flag.
        var byKey = new Dictionary<uint, SCBlock>();
        foreach (var block in array.AllBlocks)
            if (block.Type is SCTypeCode.Bool1 or SCTypeCode.Bool2)
                byKey.TryAdd(block.Key, block);
        var keys = byKey.Keys;
        var names = NameBlocks(array);
        SCBlock Find(int index) => byKey[unchecked((uint)index)];
        var labels = names.ToDictionary(pair => unchecked((int)pair.Key), pair => new Label(pair.Value, "Named blocks", false, []));
        var bank = new FlagBank(EventFlagBank.Block, () => keys.Select(k => unchecked((int)k)), i => byKey.ContainsKey(unchecked((uint)i)),
            i => Find(i).Type == SCTypeCode.Bool2, (i, v) => Find(i).ChangeBooleanType(v ? SCTypeCode.Bool2 : SCTypeCode.Bool1),
            i => unchecked((uint)i).ToString("X8", CultureInfo.InvariantCulture), _ => "Unnamed blocks", labels);
        return new Backend("Switch", "PKHeX SCBlock accessor names (bool blocks)", [bank], null);
    }

    private static Dictionary<uint, string> NameBlocks(ISCBlockArray array) =>
        SaveBlockEditorService.GetBlocks(array).Where(b => b.Type == "Bool" && b.Name is not null)
            .ToDictionary(b => b.Key, b => b.Name!);

    // ── Label files ──

    private static Labels LoadClassic(string game, int flagCount, int workCount) =>
        LabelCache.GetOrAdd($"{game}:{GameInfo.CurrentLanguage}", _ =>
        {
            try
            {
                var set = new EventLabelCollection(game, flagCount, workCount);
                return new Labels(ToLabels(set.Flag), [], ToLabels(set.Work));
            }
            catch (ArgumentException)
            {
                // PKHeX refuses a label file with an out-of-range or duplicate index; stay unlabeled.
                return Labels.None;
            }
        });

    private static string[] Lines(string game, [System.Diagnostics.CodeAnalysis.ConstantExpected] string type) => GameLanguage.GetStrings(game, GameInfo.CurrentLanguage, type)
        .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length > 5).ToArray();

    private static Dictionary<int, Label> ToLabels(IEnumerable<NamedEventValue> values, bool risky = false)
    {
        var result = new Dictionary<int, Label>();
        foreach (var value in values)
        {
            IReadOnlyList<EventWorkPreset> presets = value is NamedEventWork work
                ? work.PredefinedValues.Where(p => !p.IsCustom).Select(p => new EventWorkPreset(p.Name, p.Value)).ToArray()
                : [];
            result.TryAdd(value.Index, new Label(value.Name, CategoryName(value.Type), risky || value.Type == NamedEventType.StoryProgress, presets));
        }
        return result;
    }

    private static string CategoryName(NamedEventType type) => type switch
    {
        NamedEventType.StoryProgress => "Story",
        NamedEventType.EventEncounter => "Event encounters",
        NamedEventType.GiftAvailable => "Gifts",
        NamedEventType.Rebattle => "Rebattle",
        NamedEventType.HiddenItem => "Hidden items",
        NamedEventType.TrainerToggle => "Trainers",
        NamedEventType.FlyToggle => "Fly spots",
        NamedEventType.Achievement => "Achievements",
        NamedEventType.UsefulFeature => "Features",
        NamedEventType.Statistic => "Stats",
        NamedEventType.Misc => "Misc",
        _ => "Other",
    };
}
