using System.ComponentModel;
using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Event flags &amp; work, PKHeX's SAV_EventFlags on the pad: a virtualized list of every flag
/// (PKHeX label, section, ON/OFF) or work var (value, named presets), paged by section with
/// L/R, searchable by label or number. A toggles a flag / edits a var after a confirm; every
/// write goes through <see cref="BoxBrowserViewModel.RunMutationAsync"/> as
/// <see cref="SaveAction.EditWorld"/> (Hardcore mode keeps the list view-only).
/// </summary>
public sealed class EventFlagsEditor : IPadHandler
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly GamepadRouter? _router;
    private readonly EventCatalog _catalog;
    private readonly Dictionary<(EventEntryKind, EventFlagBank, int), EventRow> _rows = [];
    private readonly CollectionView _list;
    private readonly Label _section;
    private readonly Label _footer;
    private readonly Label _empty;
    private readonly Border _flagsTab, _workTab, _unlabeledChip, _changedChip;
    private EventEntryKind _kind = EventEntryKind.Flag;
    private bool _showUnlabeled;
    private bool _changedOnly;
    private string _query = "";
    private int _category; // 0 = all sections
    private IReadOnlyList<string> _categories = [];
    private List<EventRow> _visible = [];
    private int _index;
    private bool _writing;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (EventFlagService.UnsupportedReason(session) is { } reason)
        {
            await EditorMenu.ShowAsync(host, "Event flags", reason, "OK");
            return;
        }
        var catalog = await Task.Run(() => EventFlagService.Load(session));
        await new EventFlagsEditor(host, session, viewModel, catalog)._closed.Task;
    }

    private EventFlagsEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, EventCatalog catalog)
    {
        _host = host;
        _viewModel = viewModel;
        _catalog = catalog;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        foreach (var entry in catalog.Flags.Concat(catalog.Work))
            _rows[(entry.Kind, entry.Bank, entry.Index)] = new EventRow(entry);

        _flagsTab = Chip($"Flags · {catalog.Flags.Count}", () => SetKind(EventEntryKind.Flag));
        _workTab = Chip($"Work · {catalog.Work.Count}", () => SetKind(EventEntryKind.Work));
        _unlabeledChip = Chip("Y Unlabeled", ToggleUnlabeled);
        _changedChip = Chip("X Changed", ToggleChanged);
        var tabs = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
            Children = { _flagsTab, _workTab, _unlabeledChip, _changedChip },
        };
        foreach (var chip in new[] { _flagsTab, _workTab, _unlabeledChip, _changedChip })
            chip.Margin = new Thickness(0, 0, 6, 6);
        _workTab.IsVisible = catalog.Work.Count != 0;

        // Section pager: ◀ L  Story  R ▶, tappable halves for touch.
        _section = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextTitle, TextColor = UiTokens.Ink0,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var next = Kit.GlyphKey("R", () => Page(1));
        var pager = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { Kit.GlyphKey("L", () => Page(-1)), _section, next },
        };
        Grid.SetColumn(_section, 1);
        Grid.SetColumn(next, 2);
        // Tabs and the pager share one wrapping row: side by side in landscape, stacked on a phone.
        pager.MinimumWidthRequest = 300;
        pager.Margin = new Thickness(0, 0, 0, 6);
        FlexLayout.SetGrow(pager, 1);
        tabs.Children.Add(pager);

        var search = Kit.TextField();
        search.Placeholder = "Search label or number…";
        search.TextChanged += (_, args) =>
        {
            _query = args.NewTextValue ?? "";
            Refilter(keep: null);
        };

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(BuildRow),
        };
        // Selection is only the highlight; rows own their taps (see BuildRow), so tapping the
        // highlighted row toggles it too.
        _empty = new Label
        {
            Text = "Nothing here. Try Y to show unlabeled entries, or another section.",
            TextColor = UiTokens.InkSoft, FontSize = UiTokens.TextSmall, IsVisible = false,
            HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center,
        };

        _footer = new Label
        {
            TextColor = UiTokens.InkSoft, FontSize = UiTokens.TextSmall, MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation, HorizontalTextAlignment = TextAlignment.Center,
        };
        var hints = Kit.WindowHints(("A", "Change", () => { if (Current is { } row) _ = ActivateAsync(row); }),
            ("L", "R  Section", () => Page(1)), ("B", "Close", Close));

        var body = new Grid { Children = { _list, _empty } };
        var content = new Grid
        {
            RowSpacing = 8,
            VerticalOptions = LayoutOptions.Fill,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { Kit.HeaderBar($"Event flags · {catalog.Game}"), tabs, search, body, _footer, hints },
        };
        for (var row = 1; row < content.Children.Count; row++)
            Grid.SetRow((View)content.Children[row], row);

        var window = Kit.OverlayWindow(host, content, preferredMaxWidth: 560, scroll: false);
        _overlay = Kit.AttachOverlay(host, window, Close);
        search.Unfocus();
        Refilter(keep: null);
        _router?.Push(this);
    }

    private EventRow? Current => _index >= 0 && _index < _visible.Count ? _visible[_index] : null;

    private IEnumerable<EventEntry> Source => _kind == EventEntryKind.Flag ? _catalog.Flags : _catalog.Work;

    private static Border Chip(string text, Action onTap)
    {
        var chip = Kit.Tab(text);
        chip.Padding = new Thickness(10, 4);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    // ── Filtering ──

    private void Refilter(EventRow? keep)
    {
        // Entries carry their live value in the row, so filter on the rows' current entries.
        var current = Source.Select(e => _rows[(e.Kind, e.Bank, e.Index)].Entry).ToList();
        _categories = EventFlagService.Categories(current, _showUnlabeled || _changedOnly);
        if (_category > _categories.Count) _category = 0;
        var category = _category == 0 ? null : _categories[_category - 1];
        var filter = new EventFilter(category, _query, _showUnlabeled, _changedOnly);
        _visible = EventFlagService.Filter(current, filter).Select(e => _rows[(e.Kind, e.Bank, e.Index)]).ToList();
        _index = keep is null ? 0 : Math.Max(0, _visible.IndexOf(keep));
        _list.ItemsSource = _visible;
        _empty.IsVisible = _visible.Count == 0;

        Kit.SetTab(_flagsTab, _kind == EventEntryKind.Flag);
        Kit.SetTab(_workTab, _kind == EventEntryKind.Work);
        Kit.SetTab(_unlabeledChip, _showUnlabeled);
        Kit.SetTab(_changedChip, _changedOnly);
        _section.Text = Kit.Tidy(category is null ? $"All sections ({_categories.Count})" : $"{category} · {_category}/{_categories.Count}");
        UpdateFooter();
        Highlight();
    }

    private void UpdateFooter()
    {
        var total = Source.Count();
        var changed = Source.Count(e => _rows[(e.Kind, e.Bank, e.Index)].Entry.Changed);
        var mode = HardcoreMode.Guard.Allows(SaveAction.EditWorld) ? "" : " · Hardcore: view only";
        _footer.Text = $"{_visible.Count} shown of {total} · {changed} changed since open{mode}\nLabels: {_catalog.LabelSource}";
    }

    private void SetKind(EventEntryKind kind)
    {
        if (_kind == kind || (kind == EventEntryKind.Work && _catalog.Work.Count == 0)) return;
        _kind = kind;
        _category = 0;
        Refilter(keep: null);
    }

    private void ToggleUnlabeled()
    {
        _showUnlabeled = !_showUnlabeled;
        Refilter(Current);
    }

    private void ToggleChanged()
    {
        _changedOnly = !_changedOnly;
        Refilter(Current);
    }

    private void Page(int delta)
    {
        var count = _categories.Count + 1;
        _category = ((_category + delta) % count + count) % count;
        Refilter(keep: null);
    }

    private void Highlight(bool scroll = true)
    {
        if (_visible.Count == 0) return;
        _index = Math.Clamp(_index, 0, _visible.Count - 1);
        _list.SelectedItem = _visible[_index];
        if (scroll) _list.ScrollTo(_index, position: ScrollToPosition.MakeVisible, animate: false);
    }

    // ── Writing ──

    private async Task ActivateAsync(EventRow row)
    {
        if (_writing) return;
        _writing = true;
        try
        {
            if (HardcoreMode.Blocks(SaveAction.EditWorld, out var blocked))
            {
                await EditorMenu.ShowAsync(_host, "Hardcore mode", $"{blocked}\nFlags stay viewable.", "OK");
                return;
            }
            var entry = row.Entry;
            if (entry.Kind == EventEntryKind.Flag)
                await ToggleFlagAsync(row, entry);
            else
                await EditWorkAsync(row, entry);
        }
        finally
        {
            _writing = false;
            Highlight(scroll: false);
        }
    }

    private async Task ToggleFlagAsync(EventRow row, EventEntry entry)
    {
        var next = !entry.IsOn;
        var confirmed = await PadMenu.ConfirmAsync(_host, next ? "Set flag?" : "Clear flag?",
            $"{entry.DisplayName}\n#{entry.Id} · {entry.Category} · {OnOff(entry.IsOn)} → {OnOff(next)}\n{Warning(entry)}A restore point is created first.",
            next ? "Set ON" : "Set OFF");
        if (!confirmed) return;
        var (bank, index) = (entry.Bank, entry.Index);
        if (await Write(s => EventFlagService.SetFlag(s, bank, index, next)))
            Update(row, entry with { Value = next ? 1 : 0 });
    }

    private async Task EditWorkAsync(EventRow row, EventEntry entry)
    {
        const string Custom = "Enter a number…";
        var presets = entry.Presets.Where(p => p.Value >= entry.Min && p.Value <= entry.Max).ToList();
        long value;
        if (presets.Count != 0)
        {
            var options = presets.Select(p => new PadOption($"{p.Value}: {p.Name}",
                    Accent: p.Value == entry.Value ? UiTokens.Green : null))
                .Append(new PadOption(Custom)).ToArray();
            var choice = await PadMenu.ShowAsync(_host, entry.DisplayName,
                $"#{entry.Id} · now {entry.Value}{(entry.ValueName is { } n ? $" ({n})" : "")}", options);
            if (choice is null) return;
            if (choice != Custom)
            {
                value = presets[Array.FindIndex(options, o => o.Label == choice)].Value;
                await ConfirmWorkAsync(row, entry, value);
                return;
            }
        }
        var text = await TextPopup.ShowLineAsync(_host, entry.DisplayName,
            $"Work #{entry.Id}: {entry.Min} to {entry.Max}", entry.Value.ToString(CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
            || value < entry.Min || value > entry.Max)
        {
            await EditorMenu.ShowAsync(_host, "Out of range", $"Work #{entry.Id} holds a whole number from {entry.Min} to {entry.Max}.", "OK");
            return;
        }
        await ConfirmWorkAsync(row, entry, value);
    }

    private async Task ConfirmWorkAsync(EventRow row, EventEntry entry, long value)
    {
        if (value == entry.Value) return;
        var confirmed = await PadMenu.ConfirmAsync(_host, "Change work?",
            $"{entry.DisplayName}\n#{entry.Id} · {entry.Value} → {value}\n{Warning(entry)}A restore point is created first.", "Write");
        if (!confirmed) return;
        var index = entry.Index;
        if (await Write(s => EventFlagService.SetWork(s, index, value)))
            Update(row, entry with { Value = value });
    }

    private Task<bool> Write(Func<ISaveEngineSession, GenerationOutcome> operation) =>
        _viewModel.RunMutationAsync(operation, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditWorld);

    private void Update(EventRow row, EventEntry entry)
    {
        row.Entry = entry;
        // Changed-only drops rows that went back to their original value.
        if (_changedOnly && !entry.Changed) Refilter(row);
        else UpdateFooter();
    }

    private static string Warning(EventEntry entry) => entry.Risky
        ? "⚠ Story-critical (PKHeX tags this story progress/system). Changing it can soft-lock the game.\n"
        : entry.Labeled ? "" : "Unlabeled: PKHeX does not know what this does.\n";

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static readonly Dictionary<bool, byte[]> CheckboxPng = [];

    /// <summary>The PKSM checkbox art (same asset the Autopilot option rows draw), decoded once.</summary>
    private static ImageSource CheckboxSource(bool on)
    {
        if (!CheckboxPng.TryGetValue(on, out var bytes))
        {
            using var stream = FileSystem.OpenAppPackageFileAsync(on ? "ui/pksm/checkbox_on.png" : "ui/pksm/checkbox_blank.png").GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            CheckboxPng[on] = bytes = ms.ToArray();
        }
        return ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    // ── Rows ──

    private View BuildRow()
    {
        var id = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft,
            VerticalTextAlignment = TextAlignment.Center, WidthRequest = 58, LineBreakMode = LineBreakMode.NoWrap,
        };
        var name = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = 15, TextColor = UiTokens.Ink0,
            LineBreakMode = LineBreakMode.TailTruncation, VerticalTextAlignment = TextAlignment.Center,
        };
        var detail = new Label
        {
            FontSize = UiTokens.TextSmall, TextColor = UiTokens.InkSoft, LineBreakMode = LineBreakMode.TailTruncation,
        };
        var text = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { name, detail } };
        var valueText = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextLabel, LineBreakMode = LineBreakMode.NoWrap,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
        };
        // Flags read as a game checkbox + coloured On/Off; work values as plain coloured digits.
        var check = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        var valueCell = new HorizontalStackLayout
        {
            Spacing = 5,
            MinimumWidthRequest = 58,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
            Children = { check, valueText },
        };
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(8, 4),
            MinimumHeightRequest = 44, // touch target
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { id, text, valueCell },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(valueCell, 2);
        var cell = new Border
        {
            BackgroundColor = UiTokens.RowStripe,
            StrokeThickness = 1.2,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = UiTokens.ControlRadius },
            Margin = new Thickness(0, 0, 0, 3),
            Content = grid,
        };
        VisualStateManager.SetVisualStateGroups(cell, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState { Name = "Normal", Setters = { new Setter { Property = Border.StrokeProperty, Value = Colors.Transparent } } },
                    new VisualState { Name = "Selected", Setters = {
                        new Setter { Property = Border.StrokeProperty, Value = UiTokens.Rim },
                        new Setter { Property = Border.BackgroundColorProperty, Value = UiTokens.SelectFill } } },
                },
            },
        });

        EventRow? bound = null;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (bound is null || _writing) return;
            _index = Math.Max(0, _visible.IndexOf(bound));
            _ = ActivateAsync(bound);
        };
        cell.GestureRecognizers.Add(tap);
        void Paint()
        {
            if (bound is null) return;
            var e = bound.Entry;
            id.Text = $"#{e.Id}";
            name.Text = Kit.Tidy(e.DisplayName);
            name.TextColor = e.Labeled ? UiTokens.Ink0 : UiTokens.InkSoft;
            var facts = new List<string> { e.Category };
            if (e.Risky) facts[0] = $"⚠ {e.Category}{(e.Category == "Story" ? "" : " · story-critical")}";
            if (e.Changed) facts.Add(e.Kind == EventEntryKind.Flag ? $"was {OnOff(e.Original != 0)}" : $"was {e.Original}");
            if (e.Kind == EventEntryKind.Work && e.ValueName is { } named) facts.Add(named);
            detail.Text = string.Join(" · ", facts);
            detail.TextColor = e.Risky ? UiTokens.TextTone(UiTokens.GiftRed) : UiTokens.InkSoft;
            if (e.Kind == EventEntryKind.Flag)
            {
                check.IsVisible = true;
                check.Source = CheckboxSource(e.IsOn);
                valueText.Text = e.IsOn ? "On" : "Off";
                valueText.TextColor = e.IsOn ? UiTokens.TextTone(UiTokens.Green) : UiTokens.InkSoft;
            }
            else
            {
                valueText.Text = e.Value.ToString(CultureInfo.InvariantCulture);
                check.IsVisible = false;
                valueText.TextColor = e.Value != 0 ? UiTokens.TextTone(UiTokens.Blueprint) : UiTokens.InkSoft;
            }
            cell.Opacity = e.Labeled || e.Changed ? 1 : 0.8;
        }
        void OnChanged(object? sender, PropertyChangedEventArgs args) => Paint();
        cell.BindingContextChanged += (_, _) =>
        {
            if (bound is not null) bound.PropertyChanged -= OnChanged;
            bound = cell.BindingContext as EventRow;
            if (bound is not null) bound.PropertyChanged += OnChanged;
            Paint();
        };
        return cell;
    }

    /// <summary>A list row; the entry is replaced after a write so the cell repaints in place
    /// (no ItemsSource reset, no scroll jump).</summary>
    private sealed class EventRow(EventEntry entry) : INotifyPropertyChanged
    {
        private EventEntry _entry = entry;

        public event PropertyChangedEventHandler? PropertyChanged;

        public EventEntry Entry
        {
            get => _entry;
            set
            {
                _entry = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entry)));
            }
        }
    }

    // ── Pad ──

    public bool OnPadButton(PadButton button)
    {
        if (_writing) return true;
        switch (button)
        {
            case PadButton.Up: Move(-1); return true;
            case PadButton.Down: Move(1); return true;
            case PadButton.Left: Move(-8); return true;
            case PadButton.Right: Move(8); return true;
            case PadButton.L: Page(-1); return true;
            case PadButton.R: Page(1); return true;
            case PadButton.Y: ToggleUnlabeled(); return true;
            case PadButton.X: ToggleChanged(); return true;
            case PadButton.Select: SetKind(_kind == EventEntryKind.Flag ? EventEntryKind.Work : EventEntryKind.Flag); return true;
            case PadButton.A: if (Current is { } row) _ = ActivateAsync(row); return true;
            case PadButton.B: Close(); return true;
            default: return true; // the editor owns the pad while open
        }
    }

    private void Move(int delta)
    {
        if (_visible.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, _visible.Count - 1);
        Highlight();
    }

    private void Close()
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
        _closed.TrySetResult();
    }
}
