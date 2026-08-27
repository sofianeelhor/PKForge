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
/// Full editor for a stored bank Pokémon, drawn as the Gen-6 Pokémon Information
/// screen: a SummaryBg surface with white panels and the maroon header strip, a stats
/// table with IV · EV · value columns, quick actions as stack buttons and the
/// STATS/MOVES/SAVE choice capsules. The mon is opened in its own throwaway save
/// context so every capability the in-save editor has - legality, ability tables,
/// stat maths - works here too. Edits stay in memory until "SAVE" writes them back
/// in place (same box, slot and id). The d-pad walks every row and button.
/// </summary>
public static class BankEntryEditor
{
    public static async Task<bool> ShowAsync(Grid host, IBankService bank, ISaveEngine engine, BankEntry entry)
    {
        var services = IPlatformApplication.Current!.Services;
        var data = services.GetRequiredService<IGameDataService>();
        var legalizer = services.GetService<ILegalizerService>();
        var sprites = services.GetRequiredService<ISpriteService>();

        ISaveEngineSession? session;
        try
        {
            var bytes = bank.GetData(entry.Id);
            session = engine.OpenEntitySession(bytes, entry.Info.Nickname);
        }
        catch (Exception error)
        {
            await EditorMenu.ShowAsync(host, "CAN'T EDIT", error.Message, "OK");
            return false;
        }
        if (session is null)
        {
            await EditorMenu.ShowAsync(host, "CAN'T EDIT",
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
                await EditorMenu.ShowAsync(host, "EDIT ERROR", error.Message, "OK");
                return false;
            }

            var saved = await window.Completion;
            if (window.Failure is { } failure)
            {
                await EditorMenu.ShowAsync(host, "EDIT ERROR", failure.Message, "OK");
                return false;
            }
            return saved;
        }
    }

    private static List<PickItem> NameItems(IReadOnlyList<string> names, bool includeZero, string? zeroLabel = null)
    {
        var items = new List<PickItem>(names.Count);
        for (var id = includeZero ? 0 : 1; id < names.Count; id++)
        {
            var name = id == 0 && zeroLabel is not null ? zeroLabel : names[id];
            if (name.Length > 0) items.Add(new PickItem(id, name));
        }
        return items;
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
        private static readonly string[] StatNames = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];

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
        private readonly List<(IFocusTarget Target, View View, bool Scrolls, Func<Task> Activate)> _slots = [];
        private readonly SKCanvasView _spriteView;
        private readonly Label _nickname;
        private readonly Label _speciesLine;
        private readonly Label _levelLine;
        private readonly Image _genderIcon;
        private readonly Image _shinyIcon;
        private readonly StatRow[] _statRows = new StatRow[6];
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

            // ── Identity panel: sprite hero over the editable fact rows.
            _spriteView = new SKCanvasView
            {
                WidthRequest = 68,
                HeightRequest = 68,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true,
            };
            _spriteView.PaintSurface += PaintSprite;

            _genderIcon = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center };
            _shinyIcon = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center, Source = PksmIcons.Source("shiny") };
            _nickname = new Label
            {
                FontFamily = Font,
                FontSize = 18,
                FontAttributes = FontAttributes.Bold,
                TextColor = UiTokens.Ink0,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            };
            _speciesLine = PixelLine();
            _levelLine = PixelLine();

            var hero = new Grid
            {
                ColumnDefinitions = [new(new GridLength(76)), new(GridLength.Star)],
                ColumnSpacing = 10,
                Children =
                {
                    _spriteView,
                    new VerticalStackLayout
                    {
                        Spacing = 3,
                        VerticalOptions = LayoutOptions.Center,
                        Children =
                        {
                            new HorizontalStackLayout { Spacing = 8, Children = { _nickname, _genderIcon, _shinyIcon } },
                            new HorizontalStackLayout { Spacing = 10, Children = { _speciesLine, _levelLine } },
                        },
                    },
                },
            };
            hero.SetColumn((View)hero.Children[1], 1);

            var identity = new VerticalStackLayout { Spacing = 6 };
            identity.Add(hero);
            AddRow(identity, "nickname", "NICKNAME", "editor", EditNicknameAsync);
            AddRow(identity, "species", "SPECIES", "search", EditSpeciesAsync);
            AddRow(identity, "level", "LEVEL", null, EditLevelAsync);
            AddRow(identity, "nature", "NATURE", null, EditNatureAsync);
            AddRow(identity, "ability", "ABILITY", null, EditAbilityAsync);
            AddRow(identity, "item", "HELD ITEM", "item", EditItemAsync);
            AddRow(identity, "ball", "BALL", null, EditBallAsync);
            AddRow(identity, "gender", "GENDER", "genderless", EditGenderAsync);
            AddRow(identity, "friendship", "FRIENDSHIP", null, EditFriendshipAsync);
            AddRow(identity, "ot", "TRAINER", null, EditOtAsync);
            AddRow(identity, "shiny", "SHINY", "shiny", ToggleShinyAsync);
            var identityPanel = Kit.DevicePanel(identity, padding: 10);

            // ── Stats panel: the IV · EV · value table plus the spread editors.
            var stats = new VerticalStackLayout { Spacing = 4 };
            stats.Add(StatHeader());
            for (var i = 0; i < _statRows.Length; i++)
            {
                _statRows[i] = new StatRow(i, StatNames[i]);
                stats.Add(_statRows[i]);
            }
            stats.Add(new BoxView { HeightRequest = 6 });
            AddRow(stats, "ivs", "IVS", null, EditIvsAsync);
            AddRow(stats, "evs", "EVS", null, EditEvsAsync);
            var statsPanel = Kit.DevicePanel(stats, padding: 10);

            // ── Quick actions: the little blue stack buttons on the summary surface.
            var quick = new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    QuickButton("MAX IV", MaxIvsAsync),
                    QuickButton("0 EV", ClearEvsAsync),
                    QuickButton("LV 100", Level100Async),
                },
            };

            var surface = new Border
            {
                BackgroundColor = UiTokens.SummaryBg,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(10),
                Content = new VerticalStackLayout { Spacing = 10, Children = { identityPanel, statsPanel, quick } },
            };
            _scroll.Content = surface;

            var actions = new HorizontalStackLayout
            {
                Spacing = 8,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    ActionButton("MOVES", EditMovesAsync),
                    ActionButton("MET / ORIGIN", EditMetAsync),
                    ActionButton("POTENTIAL", EditPotentialAsync),
                    ActionButton("LEGALIZE", LegalizeAsync),
                    ActionButton("SAVE", SaveAsync, UiTokens.Green),
                },
            };

            var content = new Grid
            {
                RowSpacing = 8,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            };
            content.Add(Kit.HeaderBar("POKéMON INFORMATION"));
            content.Add(_scroll);
            Grid.SetRow(_scroll, 1);
            content.Add(actions);
            Grid.SetRow(actions, 2);
            var hints = Kit.HintBar(("A", "OPEN", null), ("B", "CLOSE", RequestClose));
            content.Add(hints);
            Grid.SetRow(hints, 3);

            var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 620, scroll: false);
            _overlay = Kit.AttachOverlay(host, window, RequestClose);

            ApplyValues();
            Highlight(0);
            _router?.Push(this);
        }

        private static Label PixelLine() => new()
        {
            FontFamily = Font,
            FontSize = 13,
            TextColor = UiTokens.Ink1,
            VerticalTextAlignment = TextAlignment.Center,
        };

        private static ColumnDefinitionCollection StatColumns() => [new(new GridLength(64)), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)];

        private static View StatHeader()
        {
            var grid = new Grid { ColumnDefinitions = StatColumns(), HeightRequest = 20 };
            void Cap(string text, int column)
            {
                var label = new Label
                {
                    Text = text,
                    FontFamily = Font,
                    FontSize = 11,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = UiTokens.Indigo,
                    VerticalTextAlignment = TextAlignment.Center,
                };
                grid.Add(label);
                Grid.SetColumn(label, column);
            }
            Cap("STAT", 0);
            Cap("IV", 1);
            Cap("EV", 2);
            Cap("VALUE", 3);
            return grid;
        }

        private void AddRow(VerticalStackLayout stack, string key, string caption, string? icon, Func<Task> activate)
        {
            var row = new SummaryRow(caption, icon);
            row.Activated = () => RunFrom(row, activate);
            stack.Add(row);
            _rows[key] = row;
            _slots.Add((row, row, true, activate));
        }

        private View QuickButton(string label, Func<Task> activate)
        {
            var button = Kit.MiniCapsule(label, UiTokens.MenuBlue);
            button.FontFamily = Font;
            button.FontSize = 12;
            button.WidthRequest = 86;
            var frame = new FocusFrame(button);
            button.Clicked += (_, _) => RunFrom(frame, activate);
            _slots.Add((frame, frame, false, activate));
            return frame;
        }

        private View ActionButton(string label, Func<Task> activate, Color? accent = null)
        {
            var button = Kit.Capsule(label, accent ?? UiTokens.Cyan, primary: accent is not null);
            var frame = new FocusFrame(button);
            button.Clicked += (_, _) => RunFrom(frame, activate);
            _slots.Add((frame, frame, false, activate));
            return frame;
        }

        // ── Display refresh ──────────────────────────────────────────────────────

        private void ApplyValues()
        {
            var d = _detail;
            _nickname.Text = d.Nickname;
            _speciesLine.Text = NameOf(_data.SpeciesNames, d.Species);
            _levelLine.Text = $"LV. {d.Level}";
            _genderIcon.Source = PksmIcons.Source(d.Gender switch { 0 => "male", 1 => "female", _ => "genderless" });
            _shinyIcon.IsVisible = d.IsShiny;

            _rows["nickname"].Value = d.Nickname;
            _rows["species"].Value = _speciesLine.Text;
            _rows["level"].Value = d.Level.ToString();
            _rows["nature"].Value = NameOf(_data.NatureNames, d.Nature);
            _rows["ability"].Value = NameOf(_data.AbilityNames, d.Ability);
            _rows["item"].Value = d.HeldItem == 0 ? "none" : NameOf(_data.ItemNames, d.HeldItem);
            _rows["ball"].Value = NameOf(_data.BallNames, d.Ball);
            _rows["gender"].Value = d.Gender switch { 0 => "Male", 1 => "Female", _ => "Genderless" };
            _rows["gender"].Icon = d.Gender switch { 0 => "male", 1 => "female", _ => "genderless" };
            _rows["friendship"].Value = d.Friendship.ToString();
            _rows["ot"].Value = d.OriginalTrainer;
            _rows["shiny"].Value = d.IsShiny ? "yes" : "no";
            for (var i = 0; i < _statRows.Length; i++)
                _statRows[i].Set(d.IVs[i], d.EVs[i], d.Stats is { } values && i < values.Count ? values[i] : null);
            _rows["ivs"].Value = $"TOTAL {d.IVs.Sum()}";
            _rows["evs"].Value = $"TOTAL {d.EVs.Sum()}/510";
            _spriteView.InvalidateSurface();
        }

        private string NameOf(IReadOnlyList<string> names, int id) =>
            (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id]
            : (uint)id < (uint)_data.ItemNames.Count && _data.ItemNames[id].Length > 0 ? _data.ItemNames[id]
            : $"#{id}";

        private void PaintSprite(object? sender, SKPaintSurfaceEventArgs args)
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var bitmap = _sprites.GetSprite(_detail.Species, 0, false);
            if (bitmap is null)
            {
                // Not decoded yet: show the resting-ball mark and warm the cache.
                _sprites.Warm(_detail.Species, 0, false,
                    () => MainThread.BeginInvokeOnMainThread(_spriteView.InvalidateSurface));
                using var ball = new SKPaint { Color = UiTokens.SkEmptyMark, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
                var cx = args.Info.Width / 2f;
                var cy = args.Info.Height / 2f;
                var r = Math.Min(args.Info.Width, args.Info.Height) * 0.3f;
                canvas.DrawCircle(cx, cy, r, ball);
                canvas.DrawLine(cx - r, cy, cx + r, cy, ball);
                canvas.DrawCircle(cx, cy, r * 0.3f, ball);
                return;
            }
            var size = Math.Min(args.Info.Width, args.Info.Height) * 0.92f;
            var scale = Math.Min(size / bitmap.Width, size / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image,
                new SKRect((args.Info.Width - w) / 2, (args.Info.Height - h) / 2, (args.Info.Width + w) / 2, (args.Info.Height + h) / 2),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

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
            var discard = await EditorMenu.ConfirmAsync(_host, "DISCARD CHANGES?",
                "Leave without saving your edits back to the bank?", "Discard");
            if (discard) Close(false);
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up:
                case PadButton.Left:
                    Highlight(_focus - 1);
                    return true;
                case PadButton.Down:
                case PadButton.Right:
                    Highlight(_focus + 1);
                    return true;
                case PadButton.A:
                    Run(_slots[_focus].Activate);
                    return true;
                case PadButton.B:
                    RequestClose();
                    return true;
                default:
                    return true; // the summary owns the pad while open
            }
        }

        private void Highlight(int index)
        {
            _focus = Math.Clamp(index, 0, _slots.Count - 1);
            for (var i = 0; i < _slots.Count; i++)
                _slots[i].Target.SetFocused(i == _focus);
            var slot = _slots[_focus];
            if (slot.Scrolls && _scroll.Handler is not null)
                _ = _scroll.ScrollToAsync(slot.View, ScrollToPosition.MakeVisible, false);
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
            var text = await TextPopup.ShowAsync(_host, "NICKNAME", "Rename this Pokémon.");
            if (!string.IsNullOrWhiteSpace(text)) { _session.ApplyEdit(0, 0, new EntityEdit(Nickname: text.Trim())); _dirty = true; }
        }

        private async Task EditSpeciesAsync()
        {
            var picked = await PokedexPicker.ShowAsync(_host, _data, _session);
            if (picked is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Species: picked.Id)); _dirty = true; }
        }

        private async Task EditLevelAsync()
        {
            var lv = await StatsPopup.ShowSingleAsync(_host, "LEVEL", _detail.Level, 100);
            if (lv is { } v) { _session.ApplyEdit(0, 0, new EntityEdit(Level: Math.Max(1, v))); _dirty = true; }
        }

        private async Task EditNatureAsync()
        {
            var pick = await PickerMenu.ShowAsync(_host, "NATURE", NameItems(_data.NatureNames, includeZero: true), _detail.Nature);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Nature: pick.Id)); _dirty = true; }
        }

        private async Task EditAbilityAsync()
        {
            var choices = (Services.HaXMode.IsOn
                    ? Enumerable.Range(0, _data.AbilityNames.Count).ToList()
                    : _session.GetAbilityChoices(_detail.Species, _detail.Form))
                .Select(id => new PickItem(id, NameOf(_data.AbilityNames, id))).ToList();
            var pick = await PickerMenu.ShowAsync(_host, "ABILITY", choices, _detail.Ability);
            if (pick is not null)
            {
                _session.ApplyEdit(0, 0, new EntityEdit(Ability: pick.Id));
                var applied = _session.ReadEntity(0, 0).Ability;
                _dirty = true;
                if (applied != pick.Id)
                    await EditorMenu.ShowAsync(_host, "ABILITY DID NOT STICK",
                        $"Asked for {NameOf(_data.AbilityNames, pick.Id)}, the mon holds {NameOf(_data.AbilityNames, applied)}. " +
                        "Tell the developer: this is the diagnostic he asked for.", "OK");
            }
        }

        private async Task EditItemAsync()
        {
            var pick = await PickerMenu.ShowAsync(_host, "HELD ITEM", ItemIcons(_data.ItemNames), _detail.HeldItem);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(HeldItem: pick.Id)); _dirty = true; }
        }

        private async Task EditBallAsync()
        {
            var pick = await PickerMenu.ShowAsync(_host, "BALL", BallIcons(_data.BallNames), _detail.Ball);
            if (pick is not null) { _session.ApplyEdit(0, 0, new EntityEdit(Ball: pick.Id)); _dirty = true; }
        }

        private async Task EditGenderAsync()
        {
            var g = await EditorMenu.ShowAsync(_host, "GENDER", null,
                new PadOption("Male", Accent: UiTokens.MenuBlue),
                new PadOption("Female", Accent: UiTokens.GiftRed),
                new PadOption("Genderless", Accent: UiTokens.Ink1));
            var gender = g switch { "Male" => 0, "Female" => 1, "Genderless" => 2, _ => (int?)null };
            if (gender is { } value) { _session.ApplyEdit(0, 0, new EntityEdit(Gender: value)); _dirty = true; }
        }

        private async Task EditFriendshipAsync()
        {
            var f = await StatsPopup.ShowSingleAsync(_host, "FRIENDSHIP", _detail.Friendship, 255);
            if (f is { } v) { _session.ApplyEdit(0, 0, new EntityEdit(Friendship: v)); _dirty = true; }
        }

        private async Task EditOtAsync()
        {
            var text = await TextPopup.ShowAsync(_host, "ORIGINAL TRAINER", "The OT name shown on this Pokémon.");
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
            var ivs = await StatsPopup.ShowAsync(_host, "IVS (0-31)", _detail.IVs, 31);
            if (ivs is not null) { _session.ApplyEdit(0, 0, new EntityEdit(IVs: ivs)); _dirty = true; }
        }

        private async Task EditEvsAsync()
        {
            var evs = await StatsPopup.ShowAsync(_host, "EVS (0-252)", _detail.EVs, 252);
            if (evs is not null) { _session.ApplyEdit(0, 0, new EntityEdit(EVs: evs)); _dirty = true; }
        }

        private Task MaxIvsAsync()
        {
            _session.ApplyEdit(0, 0, new EntityEdit(IVs: [31, 31, 31, 31, 31, 31]));
            _dirty = true;
            return Task.CompletedTask;
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
            var slot = await EditorMenu.ShowAsync(_host, "WHICH MOVE?", null,
                new PadOption($"Move 1 · {MoveName(current[0])}", "1", UiTokens.MenuBlue),
                new PadOption($"Move 2 · {MoveName(current[1])}", "2", UiTokens.MenuBlue),
                new PadOption($"Move 3 · {MoveName(current[2])}", "3", UiTokens.MenuBlue),
                new PadOption($"Move 4 · {MoveName(current[3])}", "4", UiTokens.MenuBlue));
            if (slot is null) return;
            var which = slot[5] - '1';
            if ((uint)which >= 4) return;

            var pick = await PickerMenu.ShowAsync(_host, $"MOVE {which + 1}",
                NameItems(_data.MoveNames, includeZero: true, zeroLabel: "(none)"), current[which]);
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

        private async Task LegalizeAsync()
        {
            var legalizer = _legalizer;
            if (legalizer is null) return;
            var overlay = LoadingOverlay.Show(_host, "LEGALIZING…", "Finding the closest real, legal version.");
            try
            {
                var outcome = await Task.Run(() => legalizer.LegalizeSlot(_session, 0, 0));
                _dirty = true;
                overlay.Close();
                if (!outcome.Success)
                    await EditorMenu.ShowAsync(_host, "LEGALIZE", outcome.Message, "OK");
            }
            catch (Exception error)
            {
                overlay.Close();
                await EditorMenu.ShowAsync(_host, "LEGALIZE", error.Message, "OK");
            }
        }

        private Task SaveAsync()
        {
            var export = _session.ExportSlot(0, 0);
            var info = _engine.TryDescribeEntity(export.Data, _entry.Info.SourceName) ?? _entry.Info;
            _bank.Replace(_entry.Id, export.Data, info);
            Close(true);
            return Task.CompletedTask;
        }

        // ── Row and focus chrome ─────────────────────────────────────────────────

        /// <summary>
        /// One fact row of the summary panels: PKSM pixel icon, PixelUI caption, right-set
        /// value, and the striped-row cursor (indigo-light band).
        /// </summary>
        private sealed class SummaryRow : Grid, IFocusTarget
        {
            private readonly SKCanvasView _bg;
            private readonly Label _caption;
            private readonly Label _value;
            private readonly Image _icon;
            private bool _selected;

            public Action? Activated { get; set; }

            public SummaryRow(string caption, string? icon)
            {
                HeightRequest = 36;
                ColumnDefinitions = [new(new GridLength(26)), new(new GridLength(26)), new(new GridLength(104)), new(GridLength.Star)];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) => DsFolderButton.DrawRow(args.Surface.Canvas, args.Info, _selected);
                _icon = new Image
                {
                    WidthRequest = 18,
                    HeightRequest = 18,
                    VerticalOptions = LayoutOptions.Center,
                    Source = icon is null ? null : PksmIcons.Source(icon),
                    IsVisible = icon is not null,
                };
                _caption = new Label
                {
                    Text = caption,
                    FontFamily = Font,
                    FontSize = 14,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                };
                _value = new Label
                {
                    FontFamily = Font,
                    FontSize = 13,
                    TextColor = UiTokens.Ink1,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1,
                };
                Children.Add(_bg);
                Children.Add(_icon);
                Children.Add(_caption);
                Children.Add(_value);
                Grid.SetColumn(_icon, 2);
                Grid.SetColumn(_caption, 3);
                Grid.SetColumn(_value, 4);

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Activated?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public string Value { set => _value.Text = value; }

            public string Icon
            {
                set
                {
                    _icon.Source = PksmIcons.Source(value);
                    _icon.IsVisible = true;
                }
            }

            public void SetFocused(bool focused)
            {
                if (_selected == focused) return;
                _selected = focused;
                _caption.TextColor = focused ? UiTokens.IndigoInk : UiTokens.Ink0;
                _bg.InvalidateSurface();
            }
        }

        /// <summary>One zebra-striped line of the stats table: stat, IV, EV, computed value.</summary>
        private sealed class StatRow : Grid
        {
            private readonly Label _iv;
            private readonly Label _ev;
            private readonly Label _value;

            public StatRow(int index, string stat)
            {
                HeightRequest = 24;
                ColumnDefinitions = StatColumns();
                var bg = new SKCanvasView { InputTransparent = true };
                bg.PaintSurface += (_, args) =>
                {
                    if (index % 2 == 1)
                        PksmPaint.StripeRow(args.Surface.Canvas, new SKRect(0, 0, args.Info.Width, args.Info.Height), false);
                };
                Children.Add(bg);
                var name = new Label
                {
                    Text = stat,
                    FontFamily = Font,
                    FontSize = 13,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                };
                _iv = Number();
                _ev = Number();
                _value = Number();
                Children.Add(name);
                Children.Add(_iv);
                Children.Add(_ev);
                Children.Add(_value);
                Grid.SetColumn(_iv, 1);
                Grid.SetColumn(_ev, 2);
                Grid.SetColumn(_value, 3);
            }

            private static Label Number() => new()
            {
                FontFamily = Font,
                FontSize = 13,
                TextColor = UiTokens.Ink0,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };

            public void Set(int iv, int ev, int? stat)
            {
                _iv.Text = iv.ToString();
                _ev.Text = ev.ToString();
                _value.Text = stat?.ToString() ?? "-";
            }
        }

        /// <summary>The gold focus ring around the capsules when the d-pad lands on them.</summary>
        private sealed class FocusFrame : Border, IFocusTarget
        {
            public FocusFrame(View content)
            {
                BackgroundColor = Colors.Transparent;
                StrokeShape = new RoundRectangle { CornerRadius = 9 };
                StrokeThickness = 2.5;
                Stroke = Colors.Transparent;
                Padding = new Thickness(2);
                Content = content;
            }

            public void SetFocused(bool focused) => Stroke = focused ? UiTokens.SelectBorder : Colors.Transparent;
        }
    }
}
