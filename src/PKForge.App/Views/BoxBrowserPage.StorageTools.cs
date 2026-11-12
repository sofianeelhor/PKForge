using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Infrastructure;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The painted storage tools: Sort (<see cref="SortPaint"/>), the batch editor
/// (<see cref="BatchPaint"/>) and Presets (<see cref="PresetPaint"/>). Every write still goes
/// through <see cref="ViewModels.BoxBrowserViewModel.RunMutationAsync"/>: a restore point first,
/// the Hardcore guard, locked boxes left alone.
/// </summary>
public sealed partial class BoxBrowserPage
{
    private static string MonPresetPath => Path.Combine(FileSystem.AppDataDirectory, "mon-presets.json");

    /// <summary>Auto-sort: choose a key, a direction and the boxes, watch the result, sort.</summary>
    private async Task ShowSortMenuAsync()
    {
        if (_sessionsFor() is not { } session) return;
        await new SortScreen(this, session).RunAsync();
        _canvas.InvalidateSurface();
    }

    /// <summary>One-tap preset packs (and the player's own), tried on a Pokémon before applying.</summary>
    private async Task ShowPresetsMenuAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        if (_sessionsFor() is not { } session) return;
        await new PresetsScreen(this, session).RunAsync();
        _canvas.InvalidateSurface();
    }

    /// <summary>
    /// The batch editor: tick operations (level, IVs, EV spread, heal, training, friendship,
    /// nicknames, held items, expert lines), see who changes and how, then one backed-up write
    /// to the organizer selection, one box or every box. A dry run on copies comes first.
    /// </summary>
    private async Task RunBatchEditorAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        if (_sessionsFor() is not { } session) return;
        await new BatchScreen(this, session).RunAsync();
        _canvas.InvalidateSurface();
    }

    /// <summary>The unlocked boxes of the save (all when it has no document id).</summary>
    private List<int> UnlockedBoxes(IEnumerable<int> boxes)
    {
        var docId = DocumentId;
        return [.. boxes.Where(box => docId is null || !Protection.IsBoxLocked(docId, box))];
    }

    private bool IsLocked(int box) => DocumentId is { } docId && box >= 0 && Protection.IsBoxLocked(docId, box);

    /// <summary>The non-empty Pokémon of some boxes (box -1 is the party), read once.</summary>
    private static List<EntityDetail> ReadMons(ISaveEngineSession session, IEnumerable<int> boxes)
    {
        var list = new List<EntityDetail>();
        foreach (var box in boxes)
        {
            var count = box == -1 ? 6 : session.Snapshot.Slots.Count(s => s.Box == box) is > 0 and var real ? real : BoxGridRenderer.Columns * BoxGridRenderer.Rows;
            for (var slot = 0; slot < count; slot++)
            {
                try
                {
                    var mon = session.ReadEntity(box, slot);
                    if (!mon.IsEmpty) list.Add(mon);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
        }
        return list;
    }

    private static string NameOf(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : "";

    // ══ Sort ══════════════════════════════════════════════════════════════

    private sealed class SortScreen : ToolScreen
    {
        private static readonly (string Label, SortCriteria Criteria, string Normal, string Reversed)[] Keys =
        [
            ("Dex no.", SortCriteria.DexNumber, "Lowest first", "Highest first"),
            ("A to Z", SortCriteria.Alphabetical, "A to Z", "Z to A"),
            ("Level", SortCriteria.LevelDesc, "Strongest first", "Weakest first"),
            ("IV total", SortCriteria.IvTotalDesc, "Best first", "Worst first"),
            ("Type", SortCriteria.Type, "Normal to Fairy", "Fairy to Normal"),
            ("Age", SortCriteria.AgeOldest, "Oldest first", "Newest first"),
            ("Shiny first", SortCriteria.ShinyFirst, "Shinies first", "Shinies last"),
            ("Trainer (OT)", SortCriteria.OriginalTrainer, "A to Z", "Z to A"),
        ];
        private static readonly string[] Scopes = ["This box", "Range", "All boxes"];

        private readonly BoxBrowserPage _page;
        private readonly ISaveEngineSession _session;
        private readonly Dictionary<(int, int), EntityDetail> _mons = [];
        private int _key;
        private bool _reverse;
        private int _scope;
        private int _from;
        private int _to;
        private List<SortPreviewBox> _preview = [];
        private int _count;
        private string _status = "";

        public SortScreen(BoxBrowserPage page, ISaveEngineSession session) : base(page._hostGrid)
        {
            _page = page;
            _session = session;
            var box = page._viewModel.BoxIndex;
            _scope = box >= 0 ? 0 : 2;
            _from = Math.Max(0, box);
            _to = Math.Min(page._viewModel.BoxCount - 1, _from + 1);
            Focus = SortPaint.KeyBase;
            Rebuild();
        }

        private int BoxCount => _page._viewModel.BoxCount;

        protected override IReadOnlyList<(string Key, string Label, PadButton Button)> Prompts =>
        [
            ("A", "Choose", PadButton.A), ("Y", "Sort", PadButton.Y), ("LR", "Order by", PadButton.R),
            ("X", "Reverse", PadButton.X), ("B", "Close", PadButton.B),
        ];

        /// <summary>The boxes the sort writes (locked ones out, except a single box chosen on purpose, as before).</summary>
        private IReadOnlyList<int> Targets() => _scope switch
        {
            0 => _page._viewModel.BoxIndex >= 0 ? [_page._viewModel.BoxIndex] : [],
            1 => _page.UnlockedBoxes(Enumerable.Range(Math.Min(_from, _to), Math.Abs(_to - _from) + 1)),
            _ => _page.UnlockedBoxes(Enumerable.Range(0, BoxCount)),
        };

        /// <summary>The boxes the preview shows: the targets, plus the locked ones in the scope as they are.</summary>
        private IReadOnlyList<int> Shown() => _scope switch
        {
            0 => Targets(),
            1 => [.. Enumerable.Range(Math.Min(_from, _to), Math.Abs(_to - _from) + 1)],
            _ => [.. Enumerable.Range(0, BoxCount)],
        };

        private EntityDetail? Mon(int box, int slot)
        {
            if (_mons.TryGetValue((box, slot), out var mon)) return mon;
            try { mon = _session.ReadEntity(box, slot); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
            _mons[(box, slot)] = mon;
            return mon;
        }

        private void Rebuild()
        {
            var targets = Targets();
            var criteria = Keys[_key].Criteria;
            IReadOnlyList<(int Box, int Slot)>? order = null;
            try { order = targets.Count == 0 ? [] : _session.PreviewSortOrder(criteria, targets, _reverse); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { }
            _status = order is null ? "This save can't be sorted here" : HardcoreMode.Blocks(SaveAction.Move, out var why) ? why : "";
            _count = order?.Count ?? 0;
            var placed = 0;
            _preview = [];
            foreach (var box in Shown())
            {
                var locked = _scope != 0 && _page.IsLocked(box);
                // A Gen 1 or 2 box holds 20: the sort fills each box only that far, and so does its preview.
                var capacity = _page._viewModel.BoxCapacity;
                var mons = new List<EntityDetail?>(capacity);
                if (locked || order is null)
                    for (var slot = 0; slot < capacity; slot++) mons.Add(Mon(box, slot) is { IsEmpty: false } m ? m : null);
                else
                    for (var slot = 0; slot < capacity; slot++)
                        mons.Add(placed < order.Count ? Mon(order[placed].Box, order[placed++].Slot) : null);
                _preview.Add(Card(box, mons, locked));
            }
        }

        private SortPreviewBox Card(int box, List<EntityDetail?> mons, bool locked)
        {
            var icons = mons.Select(m => m is null ? null : IconOf(_page._sprites, m.Look, RedrawLater)).ToList();
            var shiny = mons.Select(m => m?.IsShiny ?? false).ToList();
            var name = SafeBoxName(box);
            var present = mons.OfType<EntityDetail>().ToList();
            if (locked) return new SortPreviewBox(name, icons, shiny, true, "Locked: stays as it is");
            if (present.Count == 0) return new SortPreviewBox(name, icons, shiny, false, "Empty");
            var (first, last) = (present[0], present[^1]);
            string caption = Keys[_key].Criteria switch
            {
                SortCriteria.DexNumber => $"#{first.Species:000} → #{last.Species:000}",
                SortCriteria.LevelDesc => $"Lv. {first.Level} → {last.Level}",
                SortCriteria.IvTotalDesc => $"IVs {first.IVs.Sum()} → {last.IVs.Sum()}",
                SortCriteria.ShinyFirst => present.Count(m => m.IsShiny) is var n && n > 0 ? $"{n} shiny" : "No shiny",
                SortCriteria.OriginalTrainer => $"{first.OriginalTrainer} → {last.OriginalTrainer}",
                _ => $"{first.SpeciesName} → {last.SpeciesName}",
            };
            if (Keys[_key].Criteria == SortCriteria.Type && first.Types is { Count: > 0 } a && last.Types is { Count: > 0 } b)
                return new SortPreviewBox(name, icons, shiny, false, caption, a[0], b[0]);
            return new SortPreviewBox(name, icons, shiny, false, caption);
        }

        private string SafeBoxName(int box)
        {
            try { return _session.GetBoxName(box) is { Length: > 0 } n ? n : $"BOX {box + 1}"; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { return $"BOX {box + 1}"; }
        }

        private void RedrawLater() => MainThread.BeginInvokeOnMainThread(() => { Rebuild(); Redraw(); });

        protected override ToolHits Paint(SKCanvas canvas, SKImageInfo info)
        {
            var scene = new SortScene([.. Keys.Select(k => k.Label)], _key, [Keys[_key].Normal, Keys[_key].Reversed], _reverse,
                Scopes, _scope, Math.Min(_from, _to) + 1, Math.Max(_from, _to) + 1, _scope == 1, _preview, Scroll, Focus, _count,
                !HardcoreMode.Blocks(SaveAction.Move, out _), _status, PromptLabels(), TypePlates.Paint);
            return SortPaint.Paint(canvas, info, scene, PixelTypeface());
        }

        private void SetKey(int key)
        {
            _key = (key + Keys.Length) % Keys.Length;
            Rebuild();
        }

        private void SetScope(int scope)
        {
            if (scope == 0 && _page._viewModel.BoxIndex < 0)
            {
                _status = "The party can't be sorted: pick a box first";
                return;
            }
            _scope = scope;
            Scroll = 0;
            Rebuild();
            if (scope != 1 && Focus is SortPaint.From or SortPaint.To) Focus = SortPaint.ScopeBase + scope;
        }

        private void Step(int id, int direction)
        {
            if (BoxCount == 0) return;
            if (id == SortPaint.From) _from = (_from + direction + BoxCount) % BoxCount;
            else _to = (_to + direction + BoxCount) % BoxCount;
            Rebuild();
        }

        protected override void Select(int id, SKPoint at)
        {
            base.Select(id, at);
            Choose(id);
            if (id is SortPaint.From or SortPaint.To && Hits?.Targets.FirstOrDefault(t => t.Id == id).Rect is { Width: > 0 } r)
                Step(id, at.X < r.MidX ? -1 : 1);
        }

        private bool Choose(int id)
        {
            switch (id)
            {
                case >= SortPaint.KeyBase and < SortPaint.Normal: SetKey(id - SortPaint.KeyBase); return true;
                case SortPaint.Normal or SortPaint.Reversed: _reverse = id == SortPaint.Reversed; Rebuild(); return true;
                case >= SortPaint.ScopeBase and < SortPaint.From: SetScope(id - SortPaint.ScopeBase); return true;
                default: return false;
            }
        }

        protected override async Task ActivateAsync(int id)
        {
            if (Choose(id)) return;
            if (id == SortPaint.From || id == SortPaint.To) { Step(id, 1); return; }
            if (id == SortPaint.Go) await SortAsync();
        }

        protected override bool Horizontal(int direction)
        {
            if (Focus is SortPaint.From or SortPaint.To)
            {
                // Past the end of a stepper the d-pad walks on, as everywhere else.
                Step(Focus, direction);
                return true;
            }
            return false;
        }

        protected override bool Vertical(int direction)
        {
            if (Focus != SortPaint.Preview || Hits is null) return false;
            var size = Canvas.CanvasSize;
            var next = Math.Clamp(Scroll + direction * SortPaint.RowStep(new SKSizeI((int)size.Width, (int)size.Height), _preview.Count), 0f, Hits.MaxScroll);
            if (Math.Abs(next - Scroll) < 0.5f) return false;
            Scroll = next;
            return true;
        }

        protected override bool Button(PadButton button)
        {
            switch (button)
            {
                case PadButton.L: SetKey(_key - 1); return true;
                case PadButton.R: SetKey(_key + 1); return true;
                case PadButton.X: _reverse = !_reverse; Rebuild(); return true;
                case PadButton.Y: Activate(SortPaint.Go); return true;
                default: return false;
            }
        }

        private async Task SortAsync()
        {
            if (_count == 0) { _status = "Nothing to sort here"; return; }
            var boxes = Targets();
            var reverse = _reverse;
            var criteria = Keys[_key].Criteria;
            var directionNote = reverse ? " (reversed)" : "";
            var scopeText = _scope switch
            {
                0 => "This box's Pokémon are reordered and compacted to the top.",
                1 => $"Boxes {Math.Min(_from, _to) + 1:00} to {Math.Max(_from, _to) + 1:00} are pooled, ordered, and compacted from the first. Locked boxes stay as they are.",
                _ => "Every box's Pokémon are pooled, ordered, and compacted from box 1. Empties gather at the end. Locked boxes stay as they are.",
            };
            var confirmed = await PadMenu.ConfirmAsync(Host, "Sort now?", $"{Keys[_key].Label}, {(reverse ? Keys[_key].Reversed : Keys[_key].Normal).ToLowerInvariant()}. {scopeText}", "Sort");
            if (!confirmed) return;
            var vm = _page._viewModel;
            var sorted = await vm.RunMutationAsync(session =>
            {
                var placed = session.SortBoxes(criteria, boxes, reverse);
                return new GenerationOutcome(true, $"Sorted {placed} Pokémon{directionNote}.");
            }, Math.Max(0, vm.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!sorted) { _status = vm.Status; return; }
            vm.RefreshAllSlots();
            Close();
        }
    }

    // ══ Batch editor ══════════════════════════════════════════════════════

    private sealed class BatchScreen : ToolScreen
    {
        private static readonly (string Label, string Summary, string[] Lines)[] Spreads =
        [
            ("Keep EVs as they are", "keep", []),
            ("Physical sweeper (252 Atk / 252 Spe / 4 HP)", "Atk/Spe 252+4", ["EV_HP=4", "EV_ATK=252", "EV_SPE=252"]),
            ("Special sweeper (252 SpA / 252 Spe / 4 HP)", "SpA/Spe 252+4", ["EV_HP=4", "EV_SPA=252", "EV_SPE=252"]),
            ("Physical wall (252 HP / 252 Def / 4 SpD)", "HP/Def 252+4", ["EV_HP=252", "EV_DEF=252", "EV_SPD=4"]),
            ("Special wall (252 HP / 252 SpD / 4 Def)", "HP/SpD 252+4", ["EV_HP=252", "EV_SPD=252", "EV_DEF=4"]),
            ("Reset EVs (all zero)", "reset", ["EV_HP=0", "EV_ATK=0", "EV_DEF=0", "EV_SPA=0", "EV_SPD=0", "EV_SPE=0"]),
        ];

        private const int Level = 0, Ivs = 1, Evs = 2, Heal = 3, Train = 4, Friend = 5, Names = 6, Items = 7, Expert = 8;

        private readonly BoxBrowserPage _page;
        private readonly ISaveEngineSession _session;
        private readonly TrainingCaps _caps;
        private readonly IReadOnlyList<string> _itemNames;
        private readonly List<(string Label, int Kind)> _scopes = [];
        private readonly bool[] _on = new bool[9];
        private int _spread;
        private IReadOnlyList<string> _expert = [];
        private int _scope;
        private List<EntityDetail> _mons = [];
        private List<BatchMon> _strip = [];
        private List<int> _order = [];
        private int _changing;
        private int _cursor;
        private string _field = "";
        private string _status = "";
        private string? _blocked;
        private IReadOnlyList<(int Box, int Slot)>? _slots;
        private IReadOnlyList<int>? _boxes;

        public BatchScreen(BoxBrowserPage page, ISaveEngineSession session) : base(page._hostGrid)
        {
            _page = page;
            _session = session;
            _caps = session.GetTrainingCaps();
            // Hyper Training exists from Sun/Moon on: older games never offer it (QA 2026-11-12).
            _hyperTraining = session.Generation >= 7;
            try { _itemNames = session.GetItemNames(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { _itemNames = []; }
            var vm = page._viewModel;
            if (vm.MarkedCount > 0) _scopes.Add(($"Selected ({vm.MarkedCount})", 0));
            _scopes.Add((vm.BoxIndex == -1 ? "The party" : "This box", 1));
            _scopes.Add(("All boxes", 2));
            Focus = BatchPaint.OpBase;
            SetScope(0);
        }

        private string StatKind => _caps.IvMax == 15 ? "DVs" : "IVs";

        protected override IReadOnlyList<(string Key, string Label, PadButton Button)> Prompts =>
        [
            ("A", "Toggle", PadButton.A), ("Y", "Apply", PadButton.Y), ("LR", "Scope", PadButton.R),
            ("X", "Save as preset", PadButton.X), ("B", "Close", PadButton.B),
        ];

        private IReadOnlyList<string> LinesOf(int op) => op switch
        {
            Level => ["Level=100"],
            Ivs => [.. new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" }.Select(stat => $"IV_{stat}={_caps.IvMax}")],
            Evs => Spreads[_spread].Lines,
            // PP Ups first so Heal's built-in PP refill tops off at the raised maximum.
            Heal => ["Move1_PPUps=3", "Move2_PPUps=3", "Move3_PPUps=3", "Move4_PPUps=3", "Heal"],
            Train => ["HyperTrain=$suggest"],
            Friend => ["Friendship=255"],
            Names => ["IsNicknamed=false"],
            Items => ["HeldItem=0"],
            _ => _expert,
        };

        private bool IsOn(int op) => op switch { Evs => _spread > 0, Expert => _expert.Count > 0, _ => _on[op] };

        private List<string> Plan()
        {
            var plan = new List<string>();
            for (var op = 0; op <= Expert; op++)
                if (IsOn(op)) plan.AddRange(LinesOf(op).Where(line => line.Length > 0));
            return plan;
        }

        private void SetScope(int index)
        {
            _scope = Math.Clamp(index, 0, _scopes.Count - 1);
            var vm = _page._viewModel;
            _blocked = null;
            _slots = null;
            _boxes = null;
            switch (_scopes[_scope].Kind)
            {
                case 0:
                    _slots = vm.MarkedSlots.Where(m => !_page.IsLocked(m.Box)).ToList();
                    if (_slots.Count == 0) _blocked = "Every marked box is locked";
                    break;
                case 1:
                    if (_page.IsLocked(vm.BoxIndex)) _blocked = "This box is locked";
                    else _boxes = [vm.BoxIndex];
                    break;
                default:
                    _boxes = _page.UnlockedBoxes(Enumerable.Range(0, vm.BoxCount));
                    if (_boxes.Count == 0) _blocked = "Every box is locked";
                    break;
            }
            _mons = _slots is not null
                ? [.. _slots.Select(s => Read(s.Box, s.Slot)).OfType<EntityDetail>()]
                : _boxes is not null ? ReadMons(_session, _boxes) : [];
            _cursor = 0;
            _order = [];
            Scroll = 0;
            Rebuild();
        }

        private EntityDetail? Read(int box, int slot)
        {
            try { return _session.ReadEntity(box, slot) is { IsEmpty: false } mon ? mon : null; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        }

        private string ItemName(int id) => NameOf(_itemNames, id);

        private void Rebuild()
        {
            var plan = Plan();
            // The strip shows the field under the cursor's operation, or the first one ticked.
            var shown = Focus - BatchPaint.OpBase is var f && f is >= 0 and <= Expert && IsOn(f) ? f
                : Enumerable.Range(0, Expert + 1).FirstOrDefault(IsOn, -1);
            var shownLines = shown >= 0 ? LinesOf(shown) : [];
            _changing = 0;
            _field = "";
            var changes = new bool[_mons.Count];
            for (var i = 0; i < _mons.Count; i++)
            {
                changes[i] = plan.Count > 0 && BatchForecast.Of(_mons[i], plan, ItemName).Any(l => l.Changes);
                if (changes[i]) _changing++;
            }
            // Who changes comes first, so a handful among hundreds is not lost down the strip.
            var pointed = _order.Count > 0 && _cursor < _order.Count ? _order[_cursor] : 0;
            _order = [.. Enumerable.Range(0, _mons.Count).OrderBy(i => changes[i] ? 0 : 1)];
            _cursor = Math.Max(0, _order.IndexOf(pointed));
            _strip = new List<BatchMon>(_mons.Count);
            foreach (var i in _order)
            {
                var mon = _mons[i];
                var icon = IconOf(_page._sprites, mon.Look, RedrawLater);
                if (plan.Count == 0)
                {
                    _strip.Add(new BatchMon(icon, "", "", true, mon.IsShiny));
                    continue;
                }
                var line = shownLines.Count > 0 ? BatchForecast.Of(mon, shownLines, ItemName).FirstOrDefault() : null;
                if (line is not null && _field.Length == 0) _field = line.Field.ToUpperInvariant();
                _strip.Add(new BatchMon(icon, line?.Before ?? "", line is null ? "" : line.Known ? line.After : line.After + "?",
                    changes[i] && (line?.Changes ?? true), mon.IsShiny));
            }
            _status = _blocked ?? (HardcoreMode.Blocks(SaveAction.BatchEdit, out var why) ? why : Focus == BatchPaint.OpBase + Evs ? Spreads[_spread].Label : "");
        }

        private void RedrawLater() => MainThread.BeginInvokeOnMainThread(() => { Rebuild(); Redraw(); });

        protected override ToolHits Paint(SKCanvas canvas, SKImageInfo info)
        {
            var ops = new List<BatchOp>
            {
                new("Level = 100", "", _on[Level]),
                new($"Max {StatKind} ({_caps.IvMax} everywhere)", "", _on[Ivs]),
                new("EV spread", Spreads[_spread].Summary, _spread > 0, Cycles: true),
                new("Heal + PP Max", "", _on[Heal]),
                _hyperTraining
                    ? new("Hyper Train where legal", "", _on[Train])
                    : new("Hyper Training", "Gen 7 on", false),
                new("Max friendship", "", _on[Friend]),
                new("Nicknames → species names", "", _on[Names]),
                new("Remove held items", "", _on[Items]),
                new("Expert instructions", _expert.Count == 0 ? "" : _expert.Count == 1 ? "1 line" : $"{_expert.Count} lines", _expert.Count > 0, Opens: true),
            };
            BatchDetail? detail = null;
            if (_mons.Count > 0)
            {
                var mon = _mons[_order.Count == _mons.Count ? _order[Math.Clamp(_cursor, 0, _mons.Count - 1)] : 0];
                var lines = BatchForecast.Of(mon, Plan(), ItemName);
                var name = mon.Nickname.Length > 0 && !mon.Nickname.Equals(mon.SpeciesName, StringComparison.OrdinalIgnoreCase) ? mon.Nickname : mon.SpeciesName;
                detail = new BatchDetail(name, SpriteOf(_page._sprites, mon.Look, RedrawSoon), lines, lines.Any(l => l.Changes));
            }
            var scene = new BatchScene([.. _scopes.Select(s => s.Label)], _scope, ops, _field, _strip, _cursor, _changing, _mons.Count,
                detail, Scroll, Focus, _blocked is null && !HardcoreMode.Blocks(SaveAction.BatchEdit, out _), _status, PromptLabels());
            return BatchPaint.Paint(canvas, info, scene, PixelTypeface());
        }

        private SKSizeI Size => new((int)Canvas.CanvasSize.Width, (int)Canvas.CanvasSize.Height);

        /// <summary>Keeps the pointed Pokémon's row in view.</summary>
        private void Follow()
        {
            var (columns, step, view) = BatchPaint.Grid(Size);
            var top = (_cursor / columns) * step;
            if (top < Scroll) Scroll = top;
            else if (top + step > Scroll + view - 12f) Scroll = top + step - view + 12f;
            Scroll = Math.Clamp(Scroll, 0f, BatchPaint.MaxScroll(Size, _mons.Count));
        }

        private readonly bool _hyperTraining;

        private void Toggle(int op)
        {
            if (op == Train && !_hyperTraining)
            {
                _status = "Hyper Training came with Sun and Moon: this game has none.";
                Redraw();
                return;
            }
            if (op == Evs) _spread = (_spread + 1) % Spreads.Length;
            else if (op != Expert) _on[op] = !_on[op];
            Rebuild();
        }

        protected override void Select(int id, SKPoint at)
        {
            base.Select(id, at);
            if (id >= BatchPaint.MonBase) _cursor = id - BatchPaint.MonBase;
            else if (id is >= BatchPaint.ScopeBase and < BatchPaint.OpBase) SetScope(id - BatchPaint.ScopeBase);
            else if (id - BatchPaint.OpBase is var op && op is >= 0 and < Expert)
            {
                // A tap on a tick row ticks it; on the EV row's arrows it cycles.
                if (op == Evs && Hits?.Targets.FirstOrDefault(t => t.Id == id).Rect is { Width: > 0 } r)
                    _spread = (_spread + (at.X < r.Right - r.Width * 0.2f ? Spreads.Length - 1 : 1)) % Spreads.Length;
                else Toggle(op);
            }
            Rebuild();
        }

        protected override async Task ActivateAsync(int id)
        {
            if (id is >= BatchPaint.ScopeBase and < BatchPaint.OpBase) { SetScope(id - BatchPaint.ScopeBase); return; }
            if (id == BatchPaint.OpBase + Expert) { await ExpertAsync(); return; }
            if (id - BatchPaint.OpBase is var op && op is >= 0 and < Expert) { Toggle(op); return; }
            if (id == BatchPaint.Apply || id >= BatchPaint.MonBase) await ApplyAsync();
        }

        private async Task ExpertAsync()
        {
            var text = await TextPopup.ShowAsync(Host, "Expert instructions",
                $"One per line, PKHeX style, added on top of the ticks (empty clears them):\nLevel=100\nIV_HP={_caps.IvMax}\nShiny=Yes\nEV_ATK={_caps.EvMax}");
            if (text is null) return;
            _expert = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Rebuild();
        }

        protected override bool Horizontal(int direction)
        {
            if (Focus == BatchPaint.OpBase + Evs)
            {
                _spread = (_spread + direction + Spreads.Length) % Spreads.Length;
                Rebuild();
                return true;
            }
            if (Focus >= BatchPaint.MonBase)
            {
                var (columns, _, _) = BatchPaint.Grid(Size);
                if (direction < 0 && _cursor % columns == 0)
                {
                    Focus = BatchPaint.OpBase;
                    Rebuild();
                    return true;
                }
                _cursor = Math.Clamp(_cursor + direction, 0, _mons.Count - 1);
                Focus = BatchPaint.MonBase + _cursor;
                Follow();
                return true;
            }
            if (direction > 0 && Focus is >= BatchPaint.OpBase and <= BatchPaint.Apply && _mons.Count > 0)
            {
                // Right from the plan enters the strip where the pointer was.
                Focus = BatchPaint.MonBase + _cursor;
                Follow();
                return true;
            }
            return false;
        }

        protected override bool Vertical(int direction)
        {
            if (Focus < BatchPaint.MonBase) return false;
            var (columns, _, _) = BatchPaint.Grid(Size);
            var next = _cursor + direction * columns;
            if (next < 0 || next >= _mons.Count) return true;
            _cursor = next;
            Focus = BatchPaint.MonBase + _cursor;
            Follow();
            return true;
        }

        protected override bool Walkable(int id) => id < BatchPaint.MonBase;

        protected override void Scrolled() { }

        public override string ToString() => "Batch editor";

        protected override bool Button(PadButton button)
        {
            switch (button)
            {
                case PadButton.L: SetScope((_scope - 1 + _scopes.Count) % _scopes.Count); return true;
                case PadButton.R: SetScope((_scope + 1) % _scopes.Count); return true;
                case PadButton.Y: Activate(BatchPaint.Apply); return true;
                case PadButton.X: _ = RunBusyAsync(SaveAsPresetAsync); return true;
                default: return false;
            }
        }

        /// <summary>The ticks as a personal preset, for the Presets screen.</summary>
        private async Task SaveAsPresetAsync()
        {
            var plan = Plan();
            if (plan.Count == 0) { _status = "Tick something first"; return; }
            var name = await TextPopup.ShowAsync(Host, "Save as preset", "A name for these changes (it appears in Presets):");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                new MonPresetStore(MonPresetPath).Save(new MonPreset(Guid.NewGuid().ToString("N"), name.Trim(), plan));
                _status = $"Saved “{name.Trim()}” in Presets";
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                _status = e.Message;
            }
        }

        private async Task ApplyAsync()
        {
            if (_blocked is not null) { _status = _blocked; return; }
            var plan = Plan();
            if (plan.Count == 0) { _status = "No operations selected"; return; }
            var vm = _page._viewModel;
            var slots = _slots;
            var boxes = _boxes;
            var scopeText = slots is not null
                ? $"{slots.Count} selected Pokémon"
                : boxes!.Count == 1
                    ? (vm.BoxIndex == -1 ? "the party" : $"box {boxes[0] + 1:00}")
                    : (boxes.Count == vm.BoxCount ? "every box" : $"{boxes.Count} unlocked boxes");

            // Dry run first: the plan runs on copies so the confirm can say what would change
            // and what would turn illegal before anything is written.
            BatchDryRun? dryRun = null;
            if (InfoPickers.Info is { } info)
            {
                var rehearsal = LoadingOverlay.Show(Host, "Checking…", "Trying the batch edit on copies first.");
                try { dryRun = await Task.Run(() => info.DryRunBatch(_session, plan, boxes, slots)); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
                finally { rehearsal.Close(); }
            }
            if (dryRun is { Targeted: > 0, Affected: 0 })
            {
                await PadMenu.ShowAsync(Host, "Nothing would change", $"The {dryRun.Targeted} Pokémon in {scopeText} already match every operation.", "OK");
                return;
            }
            var impact = dryRun is null
                ? $"Will apply {plan.Count} operation(s) to {_mons.Count} Pokémon in {scopeText}."
                : $"{dryRun.Affected} of {dryRun.Targeted} Pokémon in {scopeText} would change." +
                  (dryRun.BecomeIllegal > 0 ? $"\n⚠ {dryRun.BecomeIllegal} would become ILLEGAL." : "\nNone would become illegal.") +
                  (dryRun.AlreadyIllegal > 0 ? $"\n{dryRun.AlreadyIllegal} of them are already illegal." : "");
            var confirmed = await PadMenu.ConfirmAsync(Host, dryRun is { BecomeIllegal: > 0 } ? "Apply? some would turn illegal" : "Apply batch edit?",
                $"{impact}\n{string.Join(" · ", plan)}\nOne backed-up write.", "Apply");
            if (!confirmed) return;

            var ok = await vm.RunMutationAsync(s =>
            {
                var touched = slots is not null ? s.BatchApplySlots(slots, plan) : s.BatchApply(plan, boxes);
                return touched > 0
                    ? new GenerationOutcome(true, $"Batch applied to {touched} Pokémon (legality not re-checked).")
                    : new GenerationOutcome(false, "Nothing to edit in that scope.");
            }, Math.Max(0, vm.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            if (!ok) { _status = vm.Status; return; }
            vm.RefreshAllSlots();
            Close();
        }
    }

    // ══ Presets ═══════════════════════════════════════════════════════════

    private sealed class PresetsScreen : ToolScreen
    {
        private sealed record Entry(string Title, string Summary, IReadOnlyList<string> Lines, PresetKind Kind, MonPreset? Custom = null, bool Countable = true, string? Short = null);

        private readonly BoxBrowserPage _page;
        private readonly ISaveEngineSession _session;
        private readonly TrainingCaps _caps;
        private readonly IReadOnlyList<string> _itemNames;
        private readonly MonPresetStore _store = new(MonPresetPath);
        private List<Entry> _entries = [];
        private List<PresetCard> _cards = [];
        private int _scope;
        private IReadOnlyList<int> _boxes = [];
        private List<EntityDetail> _mons = [];
        private int _cursor;
        private int _featured;
        private string _status = "";

        public PresetsScreen(BoxBrowserPage page, ISaveEngineSession session) : base(page._hostGrid)
        {
            _page = page;
            _session = session;
            _caps = session.GetTrainingCaps();
            try { _itemNames = session.GetItemNames(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { _itemNames = []; }
            Focus = PresetPaint.CardBase;
            LoadEntries();
            SetScope(0);
        }

        protected override IReadOnlyList<(string Key, string Label, PadButton Button)> Prompts =>
        [
            ("A", "Apply", PadButton.A), ("LR", "Pokémon", PadButton.R), ("X", "Rename / delete", PadButton.X),
            ("Y", "New", PadButton.Y), ("B", "Close", PadButton.B),
        ];

        private void LoadEntries()
        {
            var perfect = _caps.IvMax == 15 ? "6DV (perfect DVs)" : "6IV (perfect IVs)";
            var stats = new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" };
            _entries =
            [
                new("Level 50 flat", "Level → 50", ["Level=50"], PresetKind.Apply),
                new("Level 100", "Level → 100", ["Level=100"], PresetKind.Apply),
                new(perfect, $"Every {(_caps.IvMax == 15 ? "DV" : "IV")} → {_caps.IvMax}", [.. stats.Select(s => $"IV_{s}={_caps.IvMax}")], PresetKind.Apply, Short: _caps.IvMax == 15 ? "6DV" : "6IV"),
                new("0 Attack IV (special)", "Attack IV → 0, for special attackers", ["IV_ATK=0"], PresetKind.Apply, Short: "0 Attack IV"),
                new("0 Speed IV (Trick Room)", "Speed IV → 0, for Trick Room teams", ["IV_SPE=0"], PresetKind.Apply, Short: "0 Speed IV"),
                new("Reset EVs", "Every EV → 0", [.. stats.Select(s => $"EV_{s}=0")], PresetKind.Apply),
                new("Max friendship", "Friendship → 255", ["Friendship=255"], PresetKind.Apply),
                new("Hyper Train everything", "Hyper Training where legal", ["HyperTrain"], PresetKind.Apply, Countable: false, Short: "Hyper Train all"),
            ];
            try
            {
                foreach (var custom in _store.Read())
                    _entries.Add(new(custom.Name, string.Join(" · ", custom.Lines), custom.Lines, PresetKind.Apply, custom));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                _status = "Your presets could not be read";
            }
            _entries.Add(new("Export box (Showdown)", "Share this box as a Showdown text file.", [], PresetKind.Other, Short: "Export box"));
            _entries.Add(new("Import Showdown sets to this box", "Paste Showdown sets, see their legality, then place them in empty slots.", [], PresetKind.Other, Short: "Import Showdown"));
            _entries.Add(new("New preset", "Your own lines", [], PresetKind.New));
        }

        private void SetScope(int scope)
        {
            _scope = scope;
            var vm = _page._viewModel;
            _boxes = scope == 0 ? [vm.BoxIndex] : _page.UnlockedBoxes(Enumerable.Range(0, vm.BoxCount));
            _mons = ReadMons(_session, _boxes);
            _featured = 0;
            Rebuild();
        }

        private string ItemName(int id) => NameOf(_itemNames, id);

        private void Rebuild()
        {
            _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _entries.Count - 1));
            _cards = [.. _entries.Select(e => new PresetCard(e.Short ?? e.Title, e.Summary,
                e.Kind == PresetKind.Apply && e.Countable ? _mons.Count(m => BatchForecast.Changes(m, e.Lines)) : null,
                e.Kind, e.Custom is not null))];
            if (_status.Length == 0 || _status.StartsWith("Box", StringComparison.Ordinal) || _status.StartsWith("Every", StringComparison.Ordinal) || _status.StartsWith("All", StringComparison.Ordinal))
                _status = _scope == 1 && _boxes.Count == 0 ? "Every box is locked"
                    : _scope == 0 ? $"Box {_page._viewModel.BoxIndex + 1:00} · {_mons.Count} Pokémon" : $"All unlocked boxes · {_mons.Count} Pokémon";
        }

        protected override ToolHits Paint(SKCanvas canvas, SKImageInfo info)
        {
            PresetFeatured? featured = null;
            var entry = _entries[Math.Clamp(_cursor, 0, _entries.Count - 1)];
            if (_mons.Count > 0)
            {
                var mon = _mons[Math.Clamp(_featured, 0, _mons.Count - 1)];
                var nicknamed = mon.Nickname.Length > 0 && !mon.Nickname.Equals(mon.SpeciesName, StringComparison.OrdinalIgnoreCase);
                var name = nicknamed ? mon.Nickname : mon.SpeciesName;
                var facts = nicknamed ? $"{mon.SpeciesName} · Lv. {mon.Level}" : $"Lv. {mon.Level} · OT {mon.OriginalTrainer}";
                featured = new PresetFeatured(name, facts, SpriteOf(_page._sprites, mon.Look, RedrawSoon), mon.IsShiny,
                    mon.Types ?? [], BatchForecast.Of(mon, entry.Lines, ItemName), _featured, _mons.Count);
            }
            var scene = new PresetScene(["This box", "All unlocked boxes"], _scope, _cards, _cursor, Scroll, featured, Focus,
                !HardcoreMode.Blocks(SaveAction.BatchEdit, out _), _status, PromptLabels(), TypePlates.Paint);
            return PresetPaint.Paint(canvas, info, scene, PixelTypeface());
        }

        private SKSizeI Size => new((int)Canvas.CanvasSize.Width, (int)Canvas.CanvasSize.Height);

        private void Follow()
        {
            var (columns, step, view) = PresetPaint.Grid(Size);
            var top = (_cursor / columns) * step;
            if (top < Scroll) Scroll = top;
            else if (top + step > Scroll + view) Scroll = top + step - view;
            Scroll = Math.Clamp(Scroll, 0f, PresetPaint.MaxScroll(Size, _cards.Count));
        }

        protected override void Select(int id, SKPoint at)
        {
            base.Select(id, at);
            if (id >= PresetPaint.CardBase) _cursor = id - PresetPaint.CardBase;
            else if (id >= PresetPaint.ScopeBase) SetScope(id - PresetPaint.ScopeBase);
        }

        protected override async Task ActivateAsync(int id)
        {
            if (id is >= PresetPaint.ScopeBase and < PresetPaint.CardBase) { SetScope(id - PresetPaint.ScopeBase); return; }
            if (id < PresetPaint.CardBase) return;
            _cursor = id - PresetPaint.CardBase;
            var entry = _entries[_cursor];
            switch (entry.Kind)
            {
                case PresetKind.New: await CreateAsync(); return;
                case PresetKind.Other:
                    if (entry.Title.StartsWith("Export", StringComparison.Ordinal)) await _page.ExportBoxShowdownFromEngineAsync();
                    else
                    {
                        await _page.ImportShowdownSetsToBoxAsync();
                        SetScope(_scope);
                    }
                    return;
                default: await ApplyAsync(entry); return;
            }
        }

        // The cards are a grid of three: the d-pad walks it by index so it can scroll.
        protected override bool Vertical(int direction)
        {
            if (Focus < PresetPaint.CardBase)
            {
                if (direction > 0) { Focus = PresetPaint.CardBase + _cursor; Follow(); return true; }
                return false;
            }
            var next = _cursor + direction * 3;
            if (next < 0)
            {
                Focus = PresetPaint.ScopeBase + _scope;
                return true;
            }
            if (next >= _cards.Count) next = _cards.Count - 1;
            if (next / 3 == _cursor / 3) return true;
            _cursor = next;
            Focus = PresetPaint.CardBase + _cursor;
            Follow();
            return true;
        }

        protected override bool Horizontal(int direction)
        {
            if (Focus < PresetPaint.CardBase) return false;
            var next = _cursor + direction;
            if (next < 0 || next >= _cards.Count || next / 3 != _cursor / 3) return true;
            _cursor = next;
            Focus = PresetPaint.CardBase + _cursor;
            Follow();
            return true;
        }

        protected override bool Walkable(int id) => id < PresetPaint.CardBase;

        protected override bool Button(PadButton button)
        {
            switch (button)
            {
                case PadButton.L or PadButton.R when _mons.Count > 0:
                    _featured = (_featured + (button == PadButton.R ? 1 : -1) + _mons.Count) % _mons.Count;
                    return true;
                case PadButton.Y: _ = RunBusyAsync(CreateAsync); return true;
                case PadButton.X: _ = RunBusyAsync(ManageAsync); return true;
                default: return false;
            }
        }

        private async Task ApplyAsync(Entry entry)
        {
            if (_boxes.Count == 0) { _status = "Every box is locked"; return; }
            if (entry.Lines.Count == 0) return;
            var vm = _page._viewModel;
            var boxes = _boxes;
            var scopeText = boxes.Count == vm.BoxCount ? "every unlocked box" : _scope == 1 ? $"{boxes.Count} unlocked boxes" : $"box {vm.BoxIndex + 1:00}";
            var confirmed = await PadMenu.ConfirmAsync(Host, "Apply preset?", $"{entry.Title} on {scopeText}. Backed up first.", "Apply");
            if (!confirmed) return;
            var lines = entry.Lines;
            var ok = await vm.RunMutationAsync(s =>
            {
                var touched = s.BatchApply(lines, boxes);
                return touched > 0
                    ? new GenerationOutcome(true, $"Preset applied to {touched} Pokémon.")
                    : new GenerationOutcome(false, "Nothing to edit there.");
            }, Math.Max(0, vm.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            vm.RefreshAllSlots();
            _status = vm.Status;
            if (ok) SetScope(_scope);
        }

        private async Task CreateAsync()
        {
            var name = await TextPopup.ShowAsync(Host, "New preset", "A name for it (Trick Room team, Level 5 breeders…):");
            if (string.IsNullOrWhiteSpace(name)) return;
            var lines = await TextPopup.ShowAsync(Host, "Preset lines",
                $"One per line, PKHeX style:\nLevel=50\nIV_SPE=0\nFriendship=255\nEV_ATK={_caps.EvMax}");
            if (string.IsNullOrWhiteSpace(lines)) return;
            Save(new MonPreset(Guid.NewGuid().ToString("N"), name.Trim(), Split(lines)), $"Made “{name.Trim()}”");
        }

        private static string[] Split(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private void Save(MonPreset preset, string done)
        {
            try
            {
                _store.Save(preset);
                LoadEntries();
                _cursor = Math.Max(0, _entries.FindIndex(e => e.Custom?.Id == preset.Id));
                Focus = PresetPaint.CardBase + _cursor;
                _status = done;
                Rebuild();
                Follow();
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                _status = e.Message;
            }
        }

        /// <summary>X on one of the player's own cards: rename, change its lines or delete it.</summary>
        private async Task ManageAsync()
        {
            if (_entries[Math.Clamp(_cursor, 0, _entries.Count - 1)].Custom is not { } preset)
            {
                _status = "Only your own presets can be renamed or deleted";
                return;
            }
            var choice = await PadMenu.ShowAsync(Host, preset.Name, string.Join(" · ", preset.Lines),
                new PadOption("Rename", IconPath: "script"),
                new PadOption("Change its lines", IconPath: "code"),
                new PadOption("Delete", IconPath: "close"));
            switch (choice)
            {
                case "Rename":
                    var name = await TextPopup.ShowAsync(Host, "Rename preset", $"A new name for “{preset.Name}”:");
                    if (!string.IsNullOrWhiteSpace(name)) Save(preset with { Name = name.Trim() }, "Renamed");
                    break;
                case "Change its lines":
                    var lines = await TextPopup.ShowAsync(Host, "Preset lines", $"Its lines now:\n{string.Join("\n", preset.Lines)}");
                    if (!string.IsNullOrWhiteSpace(lines)) Save(preset with { Lines = Split(lines) }, "Lines changed");
                    break;
                case "Delete":
                    if (!await PadMenu.ConfirmAsync(Host, "Delete preset?", $"“{preset.Name}” leaves this device's presets. Pokémon it was applied to keep their changes.", "Delete")) return;
                    try
                    {
                        _store.Delete(preset.Id);
                        LoadEntries();
                        _status = "Deleted";
                        Rebuild();
                        Focus = PresetPaint.CardBase + _cursor;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        _status = e.Message;
                    }
                    break;
            }
        }
    }
}
