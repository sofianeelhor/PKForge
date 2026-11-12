using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Full editor for a stored bank Pokémon, in the art direction's panel look: a wide window
/// with a vertical list on the left (the Info and Stats pages, then every sub-editor) and
/// the drawn header over the chosen page on the right. Info holds the identity rows; Stats
/// is its own page, a Base · IV · EV · value table with totals, Hidden Power and the IV
/// stars. The mon is opened in its own throwaway save context so every capability the
/// in-save editor has - legality, ability tables, stat maths - works here too. Edits stay
/// in memory until "Save" writes them back in place (same box, slot and id). The d-pad
/// walks each column; left and right move between the list and the page.
/// </summary>
public static class BankEntryEditor
{
    public static async Task<bool> ShowAsync(Grid host, IBankService bank, ISaveEngine engine, BankEntry entry)
    {
        var services = IPlatformApplication.Current!.Services;
        var data = services.GetRequiredService<IGameDataService>();
        var legalizer = services.GetService<ILegalizerService>();
        var sprites = services.GetRequiredService<ISpriteService>();

        // A ROM hack's record keeps fields no PKHeX editor knows; saving from here would
        // rewrite it. Its Summary stays available.
        if (PKForge.Engine.EntityBytes.RomHackGame(entry.Info.Format) is { } hack)
        {
            await EditorMenu.ShowAsync(host, "Can't edit",
                $"Pokémon from {hack} can only be edited in their own game.", "OK");
            return false;
        }

        ISaveEngineSession? session;
        try
        {
            var bytes = bank.GetData(entry.Id);
            session = engine.OpenEntitySession(bytes, entry.Info.Nickname, entry.Info.Format);
        }
        catch (Exception error)
        {
            await EditorMenu.ShowAsync(host, "Can't edit", error.Message, "OK");
            return false;
        }
        if (session is null)
        {
            await EditorMenu.ShowAsync(host, "Can't edit",
                "The stored bytes aren't a Pokémon PKForge can edit.", "OK");
            return false;
        }

        using (session)
        {
            SummaryWindow window;
            try
            {
                window = new SummaryWindow(host, bank, engine, entry, session, data, legalizer, sprites);
            }
            catch (Exception error)
            {
                await EditorMenu.ShowAsync(host, "Edit error", error.Message, "OK");
                return false;
            }

            var saved = await window.Completion;
            if (window.Failure is { } failure)
            {
                await EditorMenu.ShowAsync(host, "Edit error", failure.Message, "OK");
                return false;
            }
            return saved;
        }
    }

    /// <summary>Held-item list with sprites for anything already cached (misses show name only).</summary>
    private static List<PickItem> ItemIcons(IReadOnlyList<string> names)
    {
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        var items = new List<PickItem> { new(0, "(none)") };
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(names[id]) + ".png");
            items.Add(new PickItem(id, names[id], File.Exists(cached) ? cached : null));
        }
        return items;
    }

    private static List<PickItem> BallIcons(IReadOnlyList<string> names)
    {
        var items = new List<PickItem>();
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            items.Add(new PickItem(id, names[id], BallIconPath(id)));
        }
        return items;
    }

    private static string? BallIconPath(int ball)
    {
        var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, $"ballicon-{ball}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            using var asset = FileSystem.OpenAppPackageFileAsync($"balls/_ball{ball}.png").GetAwaiter().GetResult();
            using var output = File.Create(cache);
            asset.CopyTo(output);
            return cache;
        }
        catch { return null; }
    }

    /// <summary>Anything the d-pad can land on: rows highlight themselves, buttons get the gold ring.</summary>
    private interface IFocusTarget
    {
        void SetFocused(bool focused);
    }

    /// <summary>
    /// The Gen-6 summary window itself. Owns the gamepad while open; sub-editors
    /// (pickers, popups) stack above it on the same router exactly like the old menu
    /// loop did, and the window refreshes from the session when they close.
    /// </summary>
    private sealed class SummaryWindow : IPadHandler
    {
        private const string Font = DsChrome.PixelFont;

        private readonly Grid _host;
        private readonly IBankService _bank;
        private readonly ISaveEngine _engine;
        private readonly BankEntry _entry;
        private readonly ISaveEngineSession _session;
        private readonly IGameDataService _data;
        private readonly ILegalizerService? _legalizer;
        private readonly ISpriteService _sprites;
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Grid _overlay;
        private readonly GamepadRouter? _router;
        private readonly ScrollView _scroll = new();
        private readonly Dictionary<string, SummaryRow> _rows = new();
        // Focus stops: the side list (column 0) and the open page (column 1).
        private readonly List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> _slots = [];
        private readonly List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> _infoSlots = [];
        private readonly List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> _statsSlots = [];
        private readonly List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> _sideSlots = [];
        private readonly SKCanvasView _header;
        private readonly SKCanvasView _statsTable;
        private readonly View _infoPage;
        private readonly View _statsPage;
        private SideTab _infoTab = null!;
        private SideTab _statsTab = null!;
        private bool _statsOpen;
        private EntityDetail _detail = null!;
        private int _focus;
        private bool _dirty;
        private bool _closed;
        private Exception? _failure;

        public Task<bool> Completion => _result.Task;
        public Exception? Failure => _failure;

        public SummaryWindow(Grid host, IBankService bank, ISaveEngine engine, BankEntry entry,
            ISaveEngineSession session, IGameDataService data, ILegalizerService? legalizer, ISpriteService sprites)
        {
            _host = host;
            _bank = bank;
            _engine = engine;
            _entry = entry;
            _session = session;
            _data = data;
            _legalizer = legalizer;
            _sprites = sprites;
            _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
            _detail = session.ReadEntity(0, 0);
            var caps = session.GetTrainingCaps();
            var classicTraining = caps.IvMax == 15;

            // ── The drawn header: name, level, shiny and gender, the species and where it came from.
            _header = new SKCanvasView { HeightRequest = EditorRows.Design(EditorPaint.DesignHeaderHeight), InputTransparent = true };
            _header.PaintSurface += PaintGuard.Wrap("BankEntryEditor.PaintHeader", PaintHeader);

            // ── Info: the identity rows, then the quick tools.
            var identity = new VerticalStackLayout();
            AddRow(identity, _infoSlots, "nickname", "Nickname", EditNicknameAsync);
            AddRow(identity, _infoSlots, "species", "Species", EditSpeciesAsync);
            AddRow(identity, _infoSlots, "level", "Level", EditLevelAsync);
            AddRow(identity, _infoSlots, "nature", "Nature", EditNatureAsync);
            AddRow(identity, _infoSlots, "ability", "Ability", EditAbilityAsync);
            AddRow(identity, _infoSlots, "item", "Held item", EditItemAsync);
            AddRow(identity, _infoSlots, "ball", "Ball", EditBallAsync);
            AddRow(identity, _infoSlots, "gender", "Gender", EditGenderAsync);
            AddRow(identity, _infoSlots, "friendship", "Friendship", EditFriendshipAsync);
            AddRow(identity, _infoSlots, "ot", "Trainer", EditOtAsync);
            AddRow(identity, _infoSlots, "shiny", "Shiny", ToggleShinyAsync);
            identity.Add(ToolRow(_infoSlots, ("Lv 100", Level100Async), ("Make mine", MakeMineAsync), ("QR", QrAsync)));
            _infoPage = identity;

            // ── Stats: its own page, the table drawn, then the spread editors.
            _statsTable = new SKCanvasView { HeightRequest = EditorRows.Design(560), InputTransparent = true };
            _statsTable.PaintSurface += PaintGuard.Wrap("BankEntryEditor.PaintStatsTable", PaintStatsTable);
            // The table is drawn 1100 wide by 600 tall: its height follows its width.
            _statsTable.SizeChanged += (_, _) =>
            {
                if (_statsTable.Width > 0 && Math.Abs(_statsTable.HeightRequest - _statsTable.Width * 600 / 1100) > 1)
                    _statsTable.HeightRequest = _statsTable.Width * 600 / 1100;
            };
            var stats = new VerticalStackLayout { Children = { _statsTable } };
            AddRow(stats, _statsSlots, "ivs", classicTraining ? "DVs" : "IVs", EditIvsAsync);
            AddRow(stats, _statsSlots, "evs", classicTraining ? "Stat exp" : "EVs", EditEvsAsync);
            stats.Add(ToolRow(_statsSlots, (classicTraining ? "Max DV" : "Max IV", MaxIvsAsync), (classicTraining ? "0 Exp" : "0 EV", ClearEvsAsync)));
            _statsPage = stats;
            _statsPage.IsVisible = false;
            _scroll.Content = new Grid { Children = { _infoPage, _statsPage } };

            // ── The side list: the two pages, then every sub-editor.
            var side = new VerticalStackLayout { Spacing = EditorRows.Design(10) };
            _infoTab = SideItem(side, "Info", "info", () => { ShowPage(stats: false); return Task.CompletedTask; });
            _statsTab = SideItem(side, "Stats", "stats", () => { ShowPage(stats: true); return Task.CompletedTask; });
            side.Add(new BoxView { HeightRequest = EditorRows.Design(12), Color = Colors.Transparent });
            SideItem(side, "Moves", "moves", EditMovesAsync);
            SideItem(side, "Met / origin", "map", EditMetAsync);
            SideItem(side, "Potential", "stats", EditPotentialAsync);
            SideItem(side, "Awards", "ribbons", EditAwardsAsync);
            SideItem(side, "Legalize", "fix", LegalizeAsync);
            SideItem(side, "Form & shiny", "shiny", () => SubEditorAsync(MonFieldsEditor.FormAndShinyAsync));
            SideItem(side, "Trainers", "trainer", () => SubEditorAsync(MonFieldsEditor.TrainersAsync));
            SideItem(side, "Tech records", "moves", () => SubEditorAsync(MonFieldsEditor.TechRecordsAsync));

            var right = new Grid
            {
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                RowSpacing = EditorRows.Design(16),
                Children = { _header, _scroll },
            };
            Grid.SetRow(_scroll, 1);
            var body = new Grid
            {
                ColumnSpacing = EditorRows.Design(28),
                ColumnDefinitions = [new(new GridLength(EditorRows.Design(380))), new(GridLength.Star)],
                Children = { new ScrollView { Content = side }, right },
            };
            Grid.SetColumn(right, 1);

            var content = new Grid
            {
                RowSpacing = EditorRows.Design(16),
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                HeightRequest = host.Height > 0 ? host.Height - 60 : -1,
            };
            content.Add(EditorRows.EditorSection("Pokémon information"));
            content.Add(body);
            Grid.SetRow(body, 1);
            var hints = Kit.WindowHints(("A", "Open", () => OnPadButton(PadButton.A)), ("X", "Save", () => Run(SaveAsync)), ("B", "Close", RequestClose));
            content.Add(hints);
            Grid.SetRow(hints, 2);

            var window = Kit.OverlayWindow(host, content, preferredMaxWidth: EditorRows.Design(1780), scroll: false);
            _overlay = Kit.AttachOverlay(host, window, RequestClose);

            ShowPage(stats: false);
            ApplyValues();
            Highlight(_slots.IndexOf(_sideSlots[0]));
            _router?.Push(this);
        }

        /// <summary>Shows Info or Stats; the focus stops follow the page.</summary>
        private void ShowPage(bool stats)
        {
            _statsOpen = stats;
            _infoPage.IsVisible = !stats;
            _statsPage.IsVisible = stats;
            _infoTab.Active = !stats;
            _statsTab.Active = stats;
            var focused = _focus < _slots.Count ? _slots[_focus].View : null;
            _slots.Clear();
            _slots.AddRange(_sideSlots);
            _slots.AddRange(stats ? _statsSlots : _infoSlots);
            if (focused is not null && _slots.FindIndex(slot => ReferenceEquals(slot.View, focused)) is var at and >= 0) _focus = at;
            _ = _scroll.ScrollToAsync(0, 0, false);
        }

        private SideTab SideItem(VerticalStackLayout side, string label, string icon, Func<Task> activate)
        {
            var item = new SideTab(label, icon);
            item.Activated = () => RunFrom(item, activate);
            side.Add(item);
            _sideSlots.Add((item, item, 0, activate));
            return item;
        }

        private View ToolRow(List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> page, params (string Label, Func<Task> Activate)[] tools)
        {
            var row = new Grid
            {
                ColumnSpacing = EditorRows.Design(16),
                Margin = new Thickness(EditorRows.Design(28), EditorRows.Design(20), EditorRows.Design(28), EditorRows.Design(8)),
            };
            for (var i = 0; i < tools.Length; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var (label, activate) = tools[i];
                var button = EditorRows.EditorTool(label);
                var frame = new FocusFrame(button);
                button.Clicked += (_, _) => RunFrom(frame, activate);
                page.Add((frame, frame, 1, activate));
                row.Add(frame, i, 0);
            }
            return row;
        }

        private void PaintHeader(object? sender, SKPaintSurfaceEventArgs args)
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var d = _detail;
            var species = NameOf(_data.SpeciesNames, d.Species);
            var name = string.IsNullOrWhiteSpace(d.Nickname) ? species : d.Nickname;
            var from = string.IsNullOrWhiteSpace(_entry.Info.SourceName) ? null : $"From {_entry.Info.SourceName}";
            var unit = Math.Min(args.Info.Width / EditorPaint.DesignWidth, args.Info.Height / EditorPaint.DesignHeaderHeight);
            var accent = d.Types is { Count: > 0 } types ? InfoKit.TypeColor(types[0]).ToSKColor() : (SKColor?)null;
            EditorPaint.PaintHeader(canvas, new SKRect(0, 0, EditorPaint.DesignWidth * unit, args.Info.Height),
                new EditorPaint.Header(name, d.Level, d.IsShiny, d.Gender, species, from, accent), BoxBrowserPage.PixelTypeface(), unit);
            // The Pokémon at the header's right end.
            var bitmap = _sprites.GetSprite(d.Look);
            if (bitmap is null) { _sprites.Warm(d.Look, () => MainThread.BeginInvokeOnMainThread(_header.InvalidateSurface)); return; }
            var box = args.Info.Height * 0.95f;
            var scale = Math.Min(box / bitmap.Width, box / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            var image = Services.SpriteImages.Of(bitmap);
            canvas.DrawImage(image, new SKRect(args.Info.Width - w - 8, (args.Info.Height - h) / 2, args.Info.Width - 8, (args.Info.Height + h) / 2),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        /// <summary>
        /// The stats page's table: stat, base, IV (gold at the cap), EV and value; the totals in
        /// gold under it; then Hidden Power and the IV stars.
        /// </summary>
        private void PaintStatsTable(object? sender, SKPaintSurfaceEventArgs args)
        {
            var c = args.Surface.Canvas;
            c.Clear(SKColors.Transparent);
            var unit = args.Info.Width / 1100f;
            c.Scale(unit);
            var d = _detail;
            var caps = _session.GetTrainingCaps();
            var classic = caps.IvMax == 15;
            var baseStats = _session.GetBaseStats(d.Species);
            int[] bases = [baseStats.Hp, baseStats.Atk, baseStats.Def, baseStats.SpA, baseStats.SpD, baseStats.Spe];
            string[] names = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
            float[] columns = [250, 450, 650, 850, 1030];
            const float rowH = 60, text = 32;
            var head = new[] { "Base", classic ? "DV" : "IV", classic ? "Exp" : "EV", "Value" };
            using (var band = new SKPaint { Color = EditorPaint.ChipTop }) c.DrawRoundRect(new SKRect(200, 0, 1100, rowH), 10, 10, band);
            for (var i = 0; i < head.Length; i++)
                SummaryInk.Draw(c, head[i], (columns[i] + columns[i + 1]) / 2 - 100 + 100, SummaryInk.Center(rowH / 2, text), text, EditorPaint.ChipInk, align: SKTextAlign.Center);
            var nature = d.Nature;
            var up = _session.Generation >= 3 ? NatureFacts.Raised(nature) : null;
            var down = _session.Generation >= 3 ? NatureFacts.Lowered(nature) : null;
            for (var i = 0; i < 6; i++)
            {
                var y = rowH * (i + 1) + 6;
                using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0) }) c.DrawRect(new SKRect(0, y, 1100, y + rowH), band);
                var tone = up == i ? new SKColor(0xFA, 0x8C, 0x96) : down == i ? new SKColor(0x82, 0xB4, 0xFA) : EditorPaint.Label;
                SummaryInk.Draw(c, names[i], 40, SummaryInk.Center(y + rowH / 2, text), text, tone);
                var iv = d.IVs[i];
                string?[] cells = [bases[i].ToString(), iv.ToString(), d.EVs[i].ToString(), d.Stats is { } stats && i < stats.Count ? stats[i].ToString() : "-"];
                for (var k = 0; k < cells.Length; k++)
                {
                    var ink = k == 1 && iv >= caps.IvMax ? Pksm.ShinyGold : EditorPaint.Value;
                    SummaryInk.Draw(c, cells[k]!, (columns[k] + columns[k + 1]) / 2, SummaryInk.Center(y + rowH / 2, text), text, ink, align: SKTextAlign.Center);
                }
            }
            var totalY = rowH * 7 + 12;
            using (var band = new SKPaint { Color = EditorPaint.ChipTop }) c.DrawRoundRect(new SKRect(0, totalY, 1100, totalY + rowH), 10, 10, band);
            SummaryInk.Draw(c, "Total", 40, SummaryInk.Center(totalY + rowH / 2, text), text, EditorPaint.ChipInk);
            string[] totals = [bases.Sum().ToString(), d.IVs.Sum().ToString(), d.EVs.Sum().ToString(), d.Stats is { Count: 6 } all ? all.Sum().ToString() : "-"];
            for (var k = 0; k < totals.Length; k++)
                SummaryInk.Draw(c, totals[k], (columns[k] + columns[k + 1]) / 2, SummaryInk.Center(totalY + rowH / 2, text), text, Pksm.ShinyGold, align: SKTextAlign.Center);

            // Hidden Power and the IV stars under the table.
            var extraY = totalY + rowH + 40;
            SummaryInk.Draw(c, "Hidden Power", 40, SummaryInk.Center(extraY, text), text, EditorPaint.Label);
            if (InfoPickers.Info?.GetHiddenPowerType(_session, d.IVs) is { } hp && TypeFacts.IsValid(hp))
                TypePlates.Paint(c, new SKRect(300, extraY - 22, 460, extraY + 22), hp);
            else SummaryInk.Draw(c, "—", 300, SummaryInk.Center(extraY, text), text, EditorPaint.Value);
            if (!classic)
            {
                var stars = IvRank.Stars(d.IVs);
                var color = stars switch
                {
                    1 => new SKColor(0xF0, 0x68, 0x68),
                    2 => Pksm.ShinyGold,
                    3 => Pksm.Legal,
                    _ => Pksm.SignalBlue,
                };
                SummaryInk.Draw(c, "IV rank", 600, SummaryInk.Center(extraY, text), text, EditorPaint.Label);
                SummaryInk.Draw(c, new string('★', stars) + new string('☆', 4 - stars), 760, SummaryInk.Center(extraY, 38), 38, color);
            }
        }

        private void AddRow(VerticalStackLayout stack, List<(IFocusTarget Target, View View, int Column, Func<Task> Activate)> page,
            string key, string caption, Func<Task> activate)
        {
            var row = new SummaryRow(caption, dark: stack.Children.Count(child => child is SummaryRow) % 2 == 0);
            row.Activated = () => RunFrom(row, activate);
            stack.Add(row);
            _rows[key] = row;
            page.Add((row, row, 1, activate));
        }

        // ── Display refresh ──────────────────────────────────────────────────────

        private void ApplyValues()
        {
            var d = _detail;
            _rows["nickname"].Value = d.Nickname;
            _rows["species"].Value = NameOf(_data.SpeciesNames, d.Species);
            _rows["level"].Value = d.Level.ToString();
            _rows["nature"].Value = _session.Generation <= 2 ? "none (Gen 1/2)"
                : $"{NameOf(_data.NatureNames, d.Nature)}  {NatureFacts.EffectLabel(d.Nature)}";
            _rows["ability"].Value = NameOf(_data.AbilityNames, d.Ability);
            _rows["item"].Value = d.HeldItem == 0 ? "none" : NameOf(_data.ItemNames, d.HeldItem);
            _rows["ball"].Value = NameOf(_data.BallNames, d.Ball);
            _rows["gender"].Value = d.Gender switch { 0 => "♂ Male", 1 => "♀ Female", _ => "Genderless" };
            _rows["friendship"].Value = d.Friendship.ToString();
            _rows["ot"].Value = d.OriginalTrainer;
            _rows["shiny"].Value = d.IsShiny ? "★ Yes" : "☆ No";
            var caps = _session.GetTrainingCaps();
            _rows["ivs"].Value = $"Total {d.IVs.Sum()}";
            _rows["evs"].Value = caps.EvMax == 65535
                ? "Max 65535 per stat"
                : $"Total {d.EVs.Sum()}/510";
            _header.InvalidateSurface();
            _statsTable.InvalidateSurface();
        }

        private string NameOf(IReadOnlyList<string> names, int id) =>
            (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id]
            : (uint)id < (uint)_data.ItemNames.Count && _data.ItemNames[id].Length > 0 ? _data.ItemNames[id]
            : $"#{id}";

        // ── Command plumbing ─────────────────────────────────────────────────────

        private async void Run(Func<Task> activate)
        {
            try
            {
                await activate();
                if (_closed) return;
                _detail = _session.ReadEntity(0, 0);
                ApplyValues();
            }
            catch (PKForge.Engine.NameNotStorableException refused)
            {
                // Refused before anything was written: say why and stay in the editor.
                await PadMenu.ShowAsync(_host, "Name not changed", refused.Message, "OK");
            }
            catch (Exception error)
            {
                _failure = error;
                Close(false);
            }
        }

        private void RunFrom(View view, Func<Task> activate)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                if (!ReferenceEquals(_slots[i].View, view)) continue;
                Highlight(i);
                break;
            }
            Run(activate);
        }

        private async void RequestClose()
        {
            if (!_dirty)
            {
                Close(false);
                return;
            }
            const string save = "Save", discard = "Discard", keep = "Keep editing";
            try
            {
                var choice = await EditorMenu.ShowAsync(_host, "Save your changes?",
                    "This Pokémon has edits that are not in the bank yet.", save, discard, keep);
                if (choice == save) Run(SaveAsync);
                else if (choice == discard) Close(false);
            }
            catch (Exception error)
            {
                // async void: reported the way Run reports a failed command, never a crash.
                _failure = error;
                Close(false);
            }
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up: Step(-1); return true;
                case PadButton.Down: Step(1); return true;
                case PadButton.Left: Jump(0); return true;
                case PadButton.Right: Jump(1); return true;
                case PadButton.A:
                    Run(_slots[_focus].Activate);
                    return true;
                case PadButton.X:
                    Run(SaveAsync);
                    return true;
                case PadButton.B:
                    RequestClose();
                    return true;
                default:
                    return true; // the summary owns the pad while open
            }
        }

        /// <summary>Up or down within the focused column.</summary>
        private void Step(int direction)
        {
            var column = _slots[_focus].Column;
            for (var i = _focus + direction; i >= 0 && i < _slots.Count; i += direction)
                if (_slots[i].Column == column) { Highlight(i); return; }
        }

        /// <summary>To the list (0) or the page (1): the page's first stop, or the open page's tab.</summary>
        private void Jump(int column)
        {
            if (_slots[_focus].Column == column) return;
            var target = column == 0
                ? _slots.FindIndex(slot => ReferenceEquals(slot.View, _statsOpen ? _statsTab : _infoTab))
                : _slots.FindIndex(slot => slot.Column == 1);
            if (target >= 0) Highlight(target);
        }

        private void Highlight(int index)
        {
            _focus = Math.Clamp(index, 0, _slots.Count - 1);
            foreach (var slot in _sideSlots.Concat(_infoSlots).Concat(_statsSlots))
                slot.Target.SetFocused(false);
            var current = _slots[_focus];
            current.Target.SetFocused(true);
            if (current.Column == 1 && _scroll.Handler is not null)
                _ = _scroll.ScrollToAsync(current.View, ScrollToPosition.MakeVisible, false);
        }

        private void Close(bool result)
        {
            if (_closed) return;
            _closed = true;
            if (_router is not null) _router.Remove(this);
            _host.Remove(_overlay);
            _result.TrySetResult(result);
        }

        // ── The commands (same behavior as the old menu, row for row) ────────────

        private async Task EditNicknameAsync()
        {
            var text = await TextPopup.ShowAsync(_host, "Nickname", "Rename this Pokémon.");
            if (!string.IsNullOrWhiteSpace(text)) { _session.ApplyEdit(0, 0, new EntityEdit(Nickname: text.Trim())); _dirty = true; }
        }

        private async Task EditSpeciesAsync()
        {
            var picked = await PokedexPicker.ShowAsync(_host, _data, _session);
            if (picked is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Species: picked.Id)); _dirty = true; }
        }

        private async Task EditLevelAsync()
        {
            var lv = await StatsPopup.ShowSingleAsync(_host, "Level", _detail.Level, 100);
            if (lv is { } v) { _session.ApplyEdit(0, 0, new EntityEdit(Level: Math.Max(1, v))); _dirty = true; }
        }

        private async Task EditNatureAsync()
        {
            if (_session.Generation <= 2) return; // Gen 1/2 have no natures
            var preview = NaturePicker.Service?.PreviewSlot(_session, 0, 0);
            var pick = await NaturePicker.ShowAsync(_host, _data.NatureNames, _detail.Nature, preview);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Nature: pick.Id)); _dirty = true; }
        }

        private async Task EditAbilityAsync()
        {
            // The same rows as the save editor: HaX widens the list only where the format
            // stores the ability itself. Gen 3 and its hacks keep a slot, so an ability from
            // outside the species (Insomnia on a Jynx) was offered here and could never stick.
            var choices = InfoPickers.AbilityItems(_data, _session, _detail.Species, _detail.Form);
            var pick = await PickerMenu.ShowAsync(_host, "Ability", choices, _detail.Ability);
            if (pick is not null)
            {
                _session.ApplyEdit(0, 0, new EntityEdit(Ability: pick.Id));
                var applied = _session.ReadEntity(0, 0).Ability;
                _dirty = true;
                if (applied != pick.Id)
                    await EditorMenu.ShowAsync(_host, "Ability not changed",
                        $"This game can't store {NameOf(_data.AbilityNames, pick.Id)} on this Pokémon: " +
                        $"it keeps {NameOf(_data.AbilityNames, applied)}.", "OK");
            }
        }

        private async Task EditItemAsync()
        {
            var pick = await PickerMenu.ShowAsync(_host, "Held item", ItemIcons(_data.ItemNames), _detail.HeldItem);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(HeldItem: pick.Id)); _dirty = true; }
        }

        private async Task EditBallAsync()
        {
            var pick = await PickerMenu.ShowAsync(_host, "Ball", BallIcons(_data.BallNames), _detail.Ball);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Ball: pick.Id)); _dirty = true; }
        }

        private async Task EditGenderAsync()
        {
            var g = await EditorMenu.ShowAsync(_host, "Gender", null,
                new PadOption("Male", IconPath: "male"),
                new PadOption("Female", IconPath: "female"),
                new PadOption("Genderless", IconPath: "genderless"));
            var gender = g switch { "Male" => 0, "Female" => 1, "Genderless" => 2, _ => (int?)null };
            if (gender is { } value) { _session.ApplyEdit(0, 0, new EntityEdit(Gender: value)); _dirty = true; }
        }

        private async Task EditFriendshipAsync()
        {
            var f = await StatsPopup.ShowSingleAsync(_host, "Friendship", _detail.Friendship, 255);
            if (f is { } v) { _session.ApplyEdit(0, 0, new EntityEdit(Friendship: v)); _dirty = true; }
        }

        private async Task EditOtAsync()
        {
            var text = await TextPopup.ShowAsync(_host, "Original trainer", "The OT name shown on this Pokémon.");
            if (!string.IsNullOrWhiteSpace(text)) { _session.ApplyEdit(0, 0, new EntityEdit(OriginalTrainer: text.Trim())); _dirty = true; }
        }

        private Task ToggleShinyAsync()
        {
            _session.ApplyEdit(0, 0, new EntityEdit(IsShiny: !_detail.IsShiny));
            _dirty = true;
            return Task.CompletedTask;
        }

        private async Task EditIvsAsync()
        {
            var caps = _session.GetTrainingCaps();
            var title = caps.IvMax == 15 ? "DVS (0-15)" : $"IVS (0-{caps.IvMax})";
            var ivs = await StatsPopup.ShowAsync(_host, title, _detail.IVs, caps.IvMax);
            if (ivs is not null) { _session.ApplyEdit(0, 0, new EntityEdit(IVs: ivs)); _dirty = true; }
        }

        private async Task EditEvsAsync()
        {
            var caps = _session.GetTrainingCaps();
            var title = caps.EvMax == 65535 ? "STAT EXP (0-65535)" : $"EVS (0-{caps.EvMax})";
            var evs = await StatsPopup.ShowAsync(_host, title, _detail.EVs, caps.EvMax);
            if (evs is not null) { _session.ApplyEdit(0, 0, new EntityEdit(EVs: evs)); _dirty = true; }
        }

        private Task MaxIvsAsync()
        {
            var max = _session.GetTrainingCaps().IvMax;
            _session.ApplyEdit(0, 0, new EntityEdit(IVs: Enumerable.Repeat(max, 6).ToArray()));
            _dirty = true;
            return Task.CompletedTask;
        }

        private async Task MakeMineAsync()
        {
            var services = IPlatformApplication.Current!.Services;
            var profiles = services.GetRequiredService<TrainerProfileStore>().Profiles;
            TrainerInfo? current = null;
            try { current = services.GetService<ISaveSessionService>()?.CurrentSession?.GetTrainer(); }
            catch { current = null; }

            var labels = new List<string>();
            if (current is not null)
                labels.Add($"Current trainer · {current.Name} · {current.TID}/{current.SID}");
            labels.AddRange(profiles.Select(p => $"{p.DisplayName} · {p.OriginalTrainer} · {p.TID}/{p.SID}"));
            if (labels.Count == 0)
            {
                await EditorMenu.ShowAsync(_host, "Make mine",
                    "Open a game and save its trainer as a profile first.", "OK");
                return;
            }

            var choice = await PadMenu.ShowAsync(_host, "Make mine", null, labels.ToArray());
            if (choice is null) return;
            var index = labels.IndexOf(choice);
            var profile = index == 0 && current is not null
                ? new TrainerProfile("current", "Current trainer", current.Name, current.TID, current.SID, current.Gender)
                : profiles[index - (current is not null ? 1 : 0)];
            var outcome = _session.MakeMine(0, 0, profile);
            if (!outcome.Success)
            {
                await EditorMenu.ShowAsync(_host, "Make mine", outcome.Message, "OK");
                return;
            }

            _detail = _session.ReadEntity(0, 0);
            ApplyValues();
            _dirty = true;
        }

        private Task ClearEvsAsync()
        {
            _session.ApplyEdit(0, 0, new EntityEdit(EVs: [0, 0, 0, 0, 0, 0]));
            _dirty = true;
            return Task.CompletedTask;
        }

        private Task Level100Async()
        {
            _session.ApplyEdit(0, 0, new EntityEdit(Level: 100));
            _dirty = true;
            return Task.CompletedTask;
        }

        /// <summary>Pick a move slot, then a move for it.</summary>
        private async Task EditMovesAsync()
        {
            string MoveName(int id) => id == 0 ? "(none)" : (uint)id < (uint)_data.MoveNames.Count ? _data.MoveNames[id] : $"#{id}";
            var current = new[] { _detail.Move1, _detail.Move2, _detail.Move3, _detail.Move4 };
            var slot = await EditorMenu.ShowAsync(_host, "Which move?", null,
                new PadOption($"Move 1 · {MoveName(current[0])}", "1", UiTokens.MenuBlue),
                new PadOption($"Move 2 · {MoveName(current[1])}", "2", UiTokens.MenuBlue),
                new PadOption($"Move 3 · {MoveName(current[2])}", "3", UiTokens.MenuBlue),
                new PadOption($"Move 4 · {MoveName(current[3])}", "4", UiTokens.MenuBlue));
            if (slot is null) return;
            var which = slot[5] - '1';
            if ((uint)which >= 4) return;

            // The save editor's move list: only moves this Pokémon's game can hold (a FireRed
            // mon stops at Psycho Boost, Unbound has its own table), with their learn marks.
            var pick = await InfoPickers.ShowMovesAsync(_host, $"Move {which + 1}", _data, _session, 0, 0, current[which]);
            if (pick is null) return;
            _session.ApplyEdit(0, 0, which switch
            {
                0 => new EntityEdit(Move1: pick.Id),
                1 => new EntityEdit(Move2: pick.Id),
                2 => new EntityEdit(Move3: pick.Id),
                _ => new EntityEdit(Move4: pick.Id),
            });
            _dirty = true;
        }

        private async Task EditMetAsync()
        {
            if (await MetOriginEditor.ShowAsync(_host, _session, 0, 0)) _dirty = true;
        }

        private async Task EditPotentialAsync()
        {
            if (await PotentialEditor.ShowAsync(_host, _session, 0, 0)) _dirty = true;
        }

        private async Task EditAwardsAsync()
        {
            if (await AwardsEditor.ShowAsync(_host, _session, 0, 0)) _dirty = true;
        }

        private async Task SubEditorAsync(Func<Grid, ISaveEngineSession, int, int, Task<bool>> editor)
        {
            if (await editor(_host, _session, 0, 0)) _dirty = true;
        }

        private async Task LegalizeAsync()
        {
            var legalizer = _legalizer;
            if (legalizer is null) return;
            var overlay = LoadingOverlay.Show(_host, "Legalizing…", "Finding the closest real, legal version.");
            try
            {
                var outcome = await Task.Run(() => legalizer.LegalizeSlot(_session, 0, 0));
                _dirty = true;
                overlay.Close();
                if (!outcome.Success)
                    await EditorMenu.ShowAsync(_host, "Legalize", outcome.Message, "OK");
            }
            catch (Exception error)
            {
                overlay.Close();
                await EditorMenu.ShowAsync(_host, "Legalize", error.Message, "OK");
            }
        }

        private async Task SaveAsync()
        {
            // Hardcore mode: the stored mon may be inspected here but never rewritten.
            if (HardcoreMode.Blocks(SaveAction.EditMon, out var status))
            {
                await EditorMenu.ShowAsync(_host, "Hardcore mode", status, "OK");
                return;
            }
            var export = _session.ExportSlot(0, 0);
            var info = _engine.TryDescribeEntity(export.Data, _entry.Info.SourceName, export.Format) ?? _entry.Info;
            _bank.Replace(_entry.Id, export.Data, info);
            Close(true);
        }

        // ── QR transfer ──────────────────────────────────────────────────────────

        /// <summary>PKF1 .pk QR: show this mon as a scannable code, or receive one in its place.</summary>
        private async Task QrAsync()
        {
            var choice = await EditorMenu.ShowAsync(_host, "QR transfer", null,
                new PadOption("Show as .pk QR", IconPath: "qr", Detail: "Shows this Pokémon as a QR code to scan on another device."),
                new PadOption("Scan a .pk QR", IconPath: "scan", Detail: "Pick a picture of a QR code; its Pokémon replaces this one."));
            if (choice == "Show as .pk QR") await ShowEntityQrAsync();
            else if (choice == "Scan a .pk QR") await ScanEntityQrAsync();
        }

        private async Task ShowEntityQrAsync()
        {
            var detail = _session.ReadEntity(0, 0);
            var export = _session.ExportSlot(0, 0);
            await QrPopup.ShowBinaryAsync(_host, $"{detail.SpeciesName} · .PK QR",
                QrEntityService.MakePayload(export.Data, _entry.Info.Generation, detail.SpeciesName));
        }

        /// <summary>
        /// Receives a scanned mon over this entry. The scan preview is the confirmation,
        /// so the replace is immediate - same shape as the bank's own file import.
        /// </summary>
        private async Task ScanEntityQrAsync()
        {
            // A scan over this entry fabricates its replacement: creation, not a move.
            if (HardcoreMode.Blocks(SaveAction.CreateMon, out var status))
            {
                await EditorMenu.ShowAsync(_host, "Hardcore mode", status, "OK");
                return;
            }
            var received = await QrEntityService.ScanAsync(_host, _engine,
                "It replaces the Pokémon open in this editor.");
            if (received is null) return;
            _bank.Replace(_entry.Id, received.Data, received.Info);
            Close(true);
        }

        // ── Row and focus chrome ─────────────────────────────────────────────────

        /// <summary>
        /// One fact row of the Info page in the panel look: a band (dark and light in turn), the
        /// caption in the label blue, the value; the section chip's look when focused.
        /// </summary>
        private sealed class SummaryRow : Grid, IFocusTarget
        {
            private readonly SKCanvasView _bg;
            private readonly Label _caption;
            private readonly Label _value;
            private readonly bool _dark;
            private bool _selected;

            public Action? Activated { get; set; }

            public SummaryRow(string caption, bool dark)
            {
                _dark = dark;
                HeightRequest = EditorRows.Design(64);
                ColumnDefinitions = [new(new GridLength(EditorRows.Design(34))), new(new GridLength(EditorRows.Design(220))), new(GridLength.Star), new(GridLength.Auto), new(new GridLength(EditorRows.Design(28)))];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += PaintGuard.Wrap("BankEntryEditor _bg", (_, args) =>
                {
                    var c = args.Surface.Canvas;
                    c.Clear(SKColors.Transparent);
                    var r = new SKRect(0, 0, args.Info.Width, args.Info.Height);
                    if (_selected)
                    {
                        using var fill = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, r.Bottom), [EditorPaint.ChipTop, EditorPaint.ChipBottom], SKShaderTileMode.Clamp) };
                        c.DrawRect(r, fill);
                        using var rim = new SKPaint { Color = EditorPaint.Cyan, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
                        c.DrawRect(SKRect.Inflate(r, -1.5f, -1.5f), rim);
                    }
                    else
                    {
                        using var band = new SKPaint { Color = EditorPaint.RowBand(_dark) };
                        c.DrawRect(r, band);
                    }
                });
                _caption = new Label
                {
                    Text = caption,
                    FontFamily = Font,
                    FontSize = EditorRows.EditorText,
                    TextColor = EditorRows.EditorLabel,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                };
                _value = new Label
                {
                    FontFamily = Font,
                    FontSize = EditorRows.EditorText,
                    TextColor = EditorRows.EditorValue,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1,
                };
                var chevron = EditorRows.EditorChevron();
                Children.Add(_bg);
                Children.Add(_caption);
                Children.Add(_value);
                Children.Add(chevron);
                Grid.SetColumnSpan(_bg, 5);
                Grid.SetColumn(_caption, 1);
                Grid.SetColumn(_value, 2);
                Grid.SetColumn(chevron, 3);

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Activated?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public string Value { set => _value.Text = value; }

            public void SetFocused(bool focused)
            {
                if (_selected == focused) return;
                _selected = focused;
                _caption.TextColor = focused ? EditorPaint.ChipInk.ToMauiColor() : EditorRows.EditorLabel;
                _bg.InvalidateSurface();
            }
        }

        /// <summary>
        /// An entry of the side list: a menu row (icon and label); the open page's tab shows a
        /// cyan bar and label even while the focus is elsewhere.
        /// </summary>
        private sealed class SideTab : Grid, IFocusTarget
        {
            private readonly SKCanvasView _bg;
            private readonly Label _label;
            private bool _focused, _active;

            public Action? Activated { get; set; }

            public SideTab(string label, string icon)
            {
                HeightRequest = EditorRows.Design(68);
                ColumnDefinitions = [new(new GridLength(EditorRows.Design(24))), new(new GridLength(EditorRows.Design(56))), new(GridLength.Star)];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += PaintGuard.Wrap("BankEntryEditor _bg", (_, args) =>
                {
                    DsFolderButton.DrawRow(args.Surface.Canvas, args.Info, _focused);
                    if (!_active) return;
                    using var bar = new SKPaint { Color = EditorPaint.Cyan, IsAntialias = true };
                    var w = Math.Max(4, args.Info.Width * 0.02f);
                    args.Surface.Canvas.DrawRoundRect(new SKRect(0, args.Info.Height * 0.18f, w, args.Info.Height * 0.82f), w / 2, w / 2, bar);
                });
                var image = new Image
                {
                    Source = PksmIcons.Source(icon, PksmIcons.Cyan),
                    WidthRequest = EditorRows.Design(36),
                    HeightRequest = EditorRows.Design(36),
                    VerticalOptions = LayoutOptions.Center,
                    InputTransparent = true,
                };
                _label = new Label
                {
                    Text = label,
                    FontFamily = Font,
                    FontSize = EditorRows.EditorText,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                };
                Children.Add(_bg);
                Children.Add(image);
                Children.Add(_label);
                Grid.SetColumnSpan(_bg, 3);
                Grid.SetColumn(image, 1);
                Grid.SetColumn(_label, 2);
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Activated?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Active
            {
                set
                {
                    _active = value;
                    _label.TextColor = value ? EditorPaint.Cyan.ToMauiColor() : UiTokens.Ink0;
                    _bg.InvalidateSurface();
                }
            }

            public void SetFocused(bool focused)
            {
                if (_focused == focused) return;
                _focused = focused;
                _bg.InvalidateSurface();
            }
        }

        /// <summary>The pale focus rim around the capsules when the d-pad lands on them.</summary>
        private sealed class FocusFrame : Border, IFocusTarget
        {
            public FocusFrame(View content)
            {
                BackgroundColor = Colors.Transparent;
                StrokeShape = new RoundRectangle { CornerRadius = UiTokens.PanelRadius };
                StrokeThickness = 2.5;
                Stroke = Colors.Transparent;
                Padding = new Thickness(2);
                Content = content;
            }

            public void SetFocused(bool focused)
            {
                // The cyan rim of the focused fields.
                Stroke = focused ? EditorPaint.Cyan.ToMauiColor() : Colors.Transparent;
                StrokeThickness = 2.5;
            }
        }
    }
}
