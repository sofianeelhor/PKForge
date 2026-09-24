using System.Globalization;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// A PKSM-style whole-save hex editor ("Byte manipulation"), game-agnostic by
/// construction: it edits a copy of the live session's Serialize() bytes — the one
/// byte shape every engine (PKHeX-backed, Unbound, Radical Red) produces — and never
/// parses them, so the same screen works on any open save.
///
/// Layout is the classic hex dump PKSM never had: a hex offset gutter on the left,
/// 16 bytes per row, and an ASCII column on the right. The cursor is byte-level
/// (yellow focus), the D-pad walks it, L/R jump a page, and Y opens a jump-to-offset
/// popup that accepts 0x hex or decimal.
///
/// Safety model: every edit is staged in a local offset→value diff (green bytes,
/// "N bytes changed" in the header, undo-all in Actions). Nothing touches the file
/// until close, when the whole diff commits as ONE write through ISafeSaveWriter —
/// engine validation, a restore point ("Byte manipulation: N bytes"), then the
/// atomic write — followed by MarkWritten + reopen so romhack raw patches go live in
/// every surface. Only byte VALUE changes are possible (no inserts or deletes), so
/// the candidate can never resize the save.
/// </summary>
public sealed class HexEditorPage : IPadHandler
{
    private const int Columns = 16;

    private static SKTypeface? _hexTypeface; // lazy: type init must not do font I/O
    private static readonly char[] HexLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'a', 'b', 'c', 'd', 'e', 'f'];

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession _session;
    private readonly byte[] _bytes; // the Serialize() truth this editor works on
    private readonly SortedDictionary<int, byte> _edits = []; // offset → staged value
    private readonly SKCanvasView _canvas;
    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _cursorInfo;
    private int _rows = 12; // refined from the canvas at every paint
    private int _offset; // absolute cursor byte offset

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session)
    {
        try
        {
            await new HexEditorPage(host, viewModel, session)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"Hex editor closed: {error.Message}";
        }
    }

    private HexEditorPage(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session)
    {
        _host = host;
        _viewModel = viewModel;
        _session = session;
        _bytes = session.Serialize().ToArray();
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _title = new Label { Text = "Byte manipulation", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        _progress = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody, HorizontalTextAlignment = TextAlignment.End, HorizontalOptions = LayoutOptions.End };
        _cursorInfo = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody };

        _canvas = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        View hints = Kit.WindowHints(
            ("A", "Edit", null),
            ("B", "Done", () => _ = CloseAsync()),
            ("X", "Actions", () => _ = ShowActionsAsync()),
            ("Y", "Jump", () => _ = JumpAsync()),
            ("LR", "Page", null));

        var content = new Grid
        {
            RowSpacing = 6,
            // header / HEX GRID (the only elastic row) / cursor line / hints.
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                    Children = { _title, _progress },
                },
                _canvas,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetRow(_canvas, 1);
        Grid.SetRow(_cursorInfo, 2);
        Grid.SetRow(hints, 3);

        var window = Kit.DevicePanel(content, padding: 10);
        window.Margin = new Thickness(24, 12);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => _ = CloseAsync();
        scrim.GestureRecognizers.Add(scrimTap);
        _overlay = new Grid { Children = { scrim, window } };
        _host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, _host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, _host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        RefreshChrome();
        _router?.Push(this);
    }

    private int PageSize => Math.Max(1, _rows) * Columns;
    private int PageCount => Math.Max(1, (_bytes.Length + PageSize - 1) / PageSize);
    private byte ByteAt(int offset) => _edits.TryGetValue(offset, out var staged) ? staged : _bytes[offset];

    // ── Layout ──────────────────────────────────────────────────────────────

    /// <summary>Grid metrics derived from the canvas size; shared by paint and touch so
    /// taps always land where the bytes were drawn, whatever the device screen.</summary>
    private readonly record struct Layout(float GlyphW, float RowH, float Top, float GutterX, float HexX, float AsciiX, int Rows, int Digits)
    {
        public float ByteX(int column) => HexX + column * GlyphW * 3;
        public float CharX(int column) => AsciiX + column * GlyphW;
        public float RowY(int row) => Top + row * RowH + RowH / 2;

        public int HitTest(SKPoint point, int pageBase)
        {
            var row = (int)((point.Y - Top) / RowH);
            if (row < 0 || row >= Rows) return -1;
            if (point.X < HexX) return -1;
            var column = (int)((point.X - HexX) / (GlyphW * 3));
            return column >= 0 && column < Columns ? pageBase + row * Columns + column : -1;
        }
    }

    private Layout LayoutFor(int width, int height)
    {
        var digits = Math.Max(4, ((uint)(_bytes.Length - 1)).ToString("X").Length);
        // gutter + gap + 16 hex cells (2 chars + space each) + gap + ASCII column.
        var units = digits + 2 + Columns * 3 + 1 + Columns;
        var glyph = Math.Max(4f, (width - 20f) / units);
        var rowH = glyph * 2.6f;
        return new Layout(
            GlyphW: glyph, RowH: rowH, Top: 8f,
            GutterX: 10f,
            HexX: 10f + (digits + 2) * glyph,
            AsciiX: 10f + (digits + 2 + Columns * 3 + 1) * glyph,
            Rows: Math.Clamp((int)((height - 16f) / rowH), 4, 32),
            Digits: digits);
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(Pksm.LogoVoid);
        var layout = LayoutFor(args.Info.Width, args.Info.Height);
        _rows = layout.Rows;

        var page = _offset / PageSize;
        var pageBase = page * PageSize;
        _hexTypeface ??= SKTypeface.FromFamilyName("monospace");
        using var font = new SKFont { Size = layout.GlyphW * 1.55f, Edging = SKFontEdging.Antialias, Typeface = _hexTypeface };
        using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
        using var inkSoft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true };
        using var edited = new SKPaint { Color = Pksm.Legal, IsAntialias = true };
        using var cursorInk = new SKPaint { Color = Pksm.LogoVoid, IsAntialias = true };
        using var focus = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
        using var band = new SKPaint { Color = Pksm.PaperShade.WithAlpha(0x30), IsAntialias = true };

        for (var row = 0; row < layout.Rows; row++)
        {
            var rowOffset = pageBase + row * Columns;
            if (rowOffset >= _bytes.Length) break;
            if (row % 2 == 1)
                canvas.DrawRect(0, layout.Top + row * layout.RowH, args.Info.Width, layout.RowH, band);

            var baseline = layout.RowY(row) + font.Size * 0.35f;
            canvas.DrawText(rowOffset.ToString($"X{layout.Digits}"), layout.GutterX, baseline, SKTextAlign.Left, font, inkSoft);

            for (var column = 0; column < Columns; column++)
            {
                var offset = rowOffset + column;
                if (offset >= _bytes.Length) break;
                var value = ByteAt(offset);
                var isEdited = _edits.ContainsKey(offset);
                var cellX = layout.ByteX(column);
                var charX = layout.CharX(column);

                if (offset == _offset)
                {
                    // PKSM's yellow focus: filled byte cell + ringed ASCII twin.
                    var cell = new SKRect(cellX - layout.GlyphW * 0.25f, layout.RowY(row) - font.Size * 0.85f,
                        cellX + layout.GlyphW * 1.75f, layout.RowY(row) + font.Size * 0.85f);
                    canvas.DrawRoundRect(cell, 3, 3, focus);
                    using var ring = new SKPaint { Color = Pksm.ShinyGold, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                    canvas.DrawRoundRect(new SKRect(charX - layout.GlyphW * 0.25f, cell.Top, charX + layout.GlyphW * 1.1f, cell.Bottom), 3, 3, ring);
                    canvas.DrawText(value.ToString("X2"), cellX, baseline, SKTextAlign.Left, font, cursorInk);
                }
                else
                {
                    canvas.DrawText(value.ToString("X2"), cellX, baseline, SKTextAlign.Left, font, isEdited ? edited : ink);
                }

                var c = value is >= 0x20 and < 0x7F ? (char)value : '·';
                canvas.DrawText(c.ToString(), charX, baseline, SKTextAlign.Left, font,
                    offset == _offset ? cursorInk : isEdited ? edited : inkSoft);
            }
        }
    }

    // ── Input ───────────────────────────────────────────────────────────────

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;
        var size = _canvas.CanvasSize;
        var offset = LayoutFor((int)size.Width, (int)size.Height)
            .HitTest(args.Location, _offset / PageSize * PageSize);
        if (offset < 0 || offset >= _bytes.Length) return;
        _offset = offset;
        RefreshChrome();
        _ = EditByteAsync();
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Move(-Columns); return true;
            case PadButton.Down: Move(Columns); return true;
            case PadButton.Left: Move(-1); return true;
            case PadButton.Right: Move(1); return true;
            case PadButton.L: PageMove(-1); return true;
            case PadButton.R: PageMove(1); return true;
            case PadButton.A: _ = EditByteAsync(); return true;
            case PadButton.B: _ = CloseAsync(); return true;
            case PadButton.X:
            case PadButton.Start: _ = ShowActionsAsync(); return true;
            case PadButton.Y: _ = JumpAsync(); return true;
            default: return true;
        }
    }

    private void Move(int delta)
    {
        _offset = Math.Clamp(_offset + delta, 0, _bytes.Length - 1);
        RefreshChrome();
    }

    private void PageMove(int delta)
    {
        var page = _offset / PageSize;
        var target = Math.Clamp(page + delta, 0, PageCount - 1);
        _offset = Math.Min(target * PageSize + _offset % PageSize, _bytes.Length - 1);
        RefreshChrome();
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    /// <summary>PKSM's hex keypad: two PadMenu passes, high nibble then low nibble.</summary>
    private async Task EditByteAsync()
    {
        var value = ByteAt(_offset);
        var context = $"Current {value:X2} · u8 {value} · s8 {(sbyte)value}";
        var high = await PadMenu.ShowAsync(_host, $"EDIT BYTE 0x{_offset:X}", $"{context} — high nibble", Keypad());
        if (high is null) return;
        var low = await PadMenu.ShowAsync(_host, $"EDIT BYTE 0x{_offset:X}", $"{high}_ — low nibble", Keypad());
        if (low is null) return;
        Stage(_offset, (byte)((HexValue(high[0]) << 4) | HexValue(low[0])));
    }

    private static PadOption[] Keypad() =>
        Enumerable.Range(0, 16).Select(i => new PadOption(((uint)i).ToString("X"))).ToArray();

    private static int HexValue(char c) => c <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;

    private void Stage(int offset, byte value)
    {
        // Staging back the original byte un-stages it, so the diff only ever counts
        // real differences and "No changes." stays honest.
        if (value == _bytes[offset]) _edits.Remove(offset);
        else _edits[offset] = value;
        RefreshChrome();
    }

    private async Task ShowActionsAsync()
    {
        var undoLabel = $"Undo all changes ({_edits.Count})";
        var options = new List<PadOption> { new("Jump to offset", IconPath: "search") };
        if (_edits.Count > 0)
            options.Add(new PadOption(undoLabel, IconPath: "restore"));
        options.Add(new PadOption("Close", IconPath: "close"));

        var choice = await PadMenu.ShowAsync(_host, "Byte editor", $"{_edits.Count} byte(s) changed.", options.ToArray());
        if (choice == "Jump to offset")
        {
            await JumpAsync();
            return;
        }
        if (choice == undoLabel)
            _edits.Clear();
        RefreshChrome();
    }

    /// <summary>Jump-to-offset popup: StatsPopup's shape with a hex-aware parser
    /// (0x prefix or bare hex letters → hex, otherwise decimal).</summary>
    private async Task JumpAsync()
    {
        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry
        {
            Text = $"0x{_offset:X}",
            FontSize = 16,
            FontFamily = DsChrome.PixelFont,
            TextColor = UiTokens.Ink0,
            BackgroundColor = UiTokens.ShellPress,
            HorizontalTextAlignment = TextAlignment.Center,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };

        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close(int? value)
        {
            _host.Remove(overlay);
            pad?.Dispose();
            completion.TrySetResult(value);
        }
        void Apply()
        {
            if (TryParseOffset(entry.Text, _bytes.Length, out var target))
                Close(target);
        }

        var jump = Kit.Capsule("Jump", UiTokens.Green);
        jump.Clicked += (_, _) => Apply();
        var cancel = Kit.Capsule("Cancel", UiTokens.Ink1);
        cancel.Clicked += (_, _) => Close(null);

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar("Jump to offset"),
                new Label { Text = $"0 to 0x{_bytes.Length - 1:X} · hex (0x…) or decimal", TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall },
                entry,
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, jump } },
            },
        };

        var window = Kit.OverlayWindow(_host, content, preferredMaxWidth: 360);
        overlay = Kit.AttachOverlay(_host, window, () => Close(null));
        pad = new PadOverlay(() => Close(null), Apply);

        if (await completion.Task is { } offset)
        {
            _offset = offset;
            RefreshChrome();
        }
    }

    internal static bool TryParseOffset(string? text, int length, out int offset)
    {
        offset = 0;
        var source = (text ?? "").Trim();
        if (source.Length == 0 || length == 0) return false;

        int? parsed;
        if (source.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            parsed = int.TryParse(source[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var prefixed) ? prefixed : null;
        else if (source.IndexOfAny(HexLetters) >= 0)
            parsed = int.TryParse(source, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bare) ? bare : null;
        else
            parsed = int.TryParse(source, out var decimalValue) ? decimalValue : null;
        if (parsed is not { } value) return false;

        offset = Math.Clamp(value, 0, length - 1);
        return true;
    }

    // ── Close & commit ──────────────────────────────────────────────────────

    private async Task CloseAsync()
    {
        if (_edits.Count > 0)
        {
            var choice = await PadMenu.ShowAsync(_host, "Save byte changes?",
                $"{_edits.Count} byte(s) changed. Raw edits skip format checks; a restore point is created first.",
                "Save changes", "Discard changes", "Keep editing");
            if (choice == "Save changes") { await CommitAsync(); return; }
            if (choice == "Keep editing" || choice is null) return;
        }
        TearDown();
        _result.TrySetResult(false);
    }

    /// <summary>One commit for the whole diff: safe writer (validate → backup → atomic
    /// write), then MarkWritten + reopen so every surface re-parses the patched bytes.</summary>
    private async Task CommitAsync()
    {
        // This commit bypasses RunMutationAsync (the diff goes straight to the writer),
        // so it carries the Hardcore guard itself; the menu entry is hidden as well.
        if (HardcoreMode.Blocks(SaveAction.WriteRawBytes, out var hardcoreStatus))
        {
            _viewModel.Status = hardcoreStatus;
            return;
        }
        var services = IPlatformApplication.Current?.Services;
        var sessions = services?.GetService<ISaveSessionService>();
        var writer = services?.GetService<ISafeSaveWriter>();
        var current = sessions?.Current;
        if (sessions is null || writer is null || current is null)
        {
            _viewModel.Status = "No open save - byte changes kept.";
            return;
        }

        var baseline = _session.Serialize();
        var candidate = _bytes.ToArray();
        foreach (var (offset, value) in _edits)
            candidate[offset] = value;

        if (candidate.Length != baseline.Length)
        {
            _viewModel.Status = "Save length changed - write refused.";
            return;
        }
        if (candidate.AsSpan().SequenceEqual(baseline.Span))
        {
            _viewModel.Status = "No changes.";
            _edits.Clear();
            TearDown();
            _result.TrySetResult(true);
            return;
        }

        var confirmed = await PadMenu.ConfirmAsync(_host, "Write byte changes?",
            $"{_edits.Count} byte(s) will be written to {current.Document.DisplayName}. A restore point is created first.",
            "Write");
        if (!confirmed) return;

        var loader = LoadingOverlay.Show(_host, "Writing bytes…", "Validating, backing up, writing.");
        try
        {
            var receipt = await writer.WriteAsync(current.Document.DocumentId, current.Snapshot, candidate,
                $"Byte manipulation: {_edits.Count} bytes");
            if (receipt.Changed)
            {
                sessions.MarkWritten(current.Document.DocumentId, candidate);
                await sessions.OpenAsync(current.Document);
                _viewModel.RefreshFromCurrentSession();
            }
            _viewModel.Status = receipt.Changed
                ? $"Bytes saved. Restore point {receipt.BackupId[..13]}…"
                : "No changes.";
            _edits.Clear();
            TearDown();
            _result.TrySetResult(true);
        }
        catch (Exception error)
        {
            // The write aborted (validation, storage, format refusal): keep the editor
            // open with the staged changes instead of closing as if it worked.
            _viewModel.Status = $"Byte write aborted: {error.Message}";
        }
        finally
        {
            loader.Close();
        }
    }

    private void TearDown()
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
    }

    private void RefreshChrome()
    {
        var page = _offset / PageSize;
        _progress.Text = $"{_bytes.Length:N0} bytes · {_edits.Count} changed · page {page + 1}/{PageCount}";
        var value = ByteAt(_offset);
        _cursorInfo.Text = $"SELECTED BYTE 0x{_offset:X} = 0x{value:X2} ({value}/{(sbyte)value})"
            + (_edits.ContainsKey(_offset) ? " · EDITED" : "");
        _canvas.InvalidateSurface();
    }
}
