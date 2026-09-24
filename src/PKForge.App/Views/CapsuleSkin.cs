using System.Runtime.CompilerServices;
using PKForge.App.Theme;
#if ANDROID
using Android.Graphics.Drawables;
using Google.Android.Material.Button;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AColor = Android.Graphics.Color;
#endif

namespace PKForge.App.Views;

/// <summary>
/// The native skin every <see cref="Kit.Capsule"/> wears: the PadMenu / summary-tab button
/// rebuilt as an Android layer drawable (MAUI's Button can't do gradient + hard shadow +
/// pressed states on its own). Body = a whisper of vertical gradient from the button's
/// BackgroundColor, 1 px top light, BorderColor edge, hard void drop under it. Pressed sinks
/// onto the shadow; focus (SetButtonFocus) lifts it a pixel; disabled goes flat and dim.
/// Callers keep setting BackgroundColor / BorderColor / IsEnabled — the skin re-reads them.
/// </summary>
public static class CapsuleSkin
{
    internal sealed class State
    {
        public string? Icon;
        public bool Focused;
        public bool Primary;
        public Color? Strip;
    }

    private static readonly ConditionalWeakTable<Button, State> States = new();

    /// <summary>Resting drop depth (dp); focus adds one.</summary>
    public const double Drop = 2;

    internal static State Attach(Button button, string? icon, bool primary, Color? strip = null)
    {
        var state = States.GetValue(button, _ => new State());
        state.Icon = icon;
        state.Primary = primary;
        state.Strip = strip;
        return state;
    }

    internal static bool IsPrimary(Button button) => States.TryGetValue(button, out var s) && s.Primary;

    internal static bool IsSkinned(Button button) => States.TryGetValue(button, out _);

    internal static void SetFocused(Button button, bool focused)
    {
        if (!States.TryGetValue(button, out var state) || state.Focused == focused) return;
        state.Focused = focused;
        button.Handler?.UpdateValue(nameof(Button.Background));
        button.Handler?.UpdateValue(nameof(Button.Padding));
    }

#if ANDROID
    private static bool _registered;
    private static readonly ConditionalWeakTable<MaterialButton, StateListDrawable> Skins = new();
    private static readonly ConditionalWeakTable<MaterialButton, object> Hooked = new();
    private static readonly Dictionary<string, Android.Graphics.Bitmap> Bitmaps = new(StringComparer.Ordinal);

    private static bool Near(Color a, Color b) =>
        Math.Abs(a.Red - b.Red) < 0.01f && Math.Abs(a.Green - b.Green) < 0.01f && Math.Abs(a.Blue - b.Blue) < 0.01f;

    /// <summary>Hooks the Button handler once (called from MauiProgram).</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // One mapping per property we care about; each re-skins the whole thing.
        ButtonHandler.Mapper.AppendToMapping(nameof(IButton.Background), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.StrokeColor), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.StrokeThickness), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.CornerRadius), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IView.IsEnabled), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(ITextStyle.TextColor), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IPadding.Padding), (h, v) => ApplyPadding(h, v));
    }

    private static float Dp(Android.Views.View view, double dp) => (float)(dp * (view.Resources?.DisplayMetrics?.Density ?? 2f));

    private static AColor Native(Color c) => c.ToPlatform();

    private static Color Shade(Color c, float t) =>
        t >= 0 ? new Color(c.Red + (1 - c.Red) * t, c.Green + (1 - c.Green) * t, c.Blue + (1 - c.Blue) * t, c.Alpha)
               : new Color(c.Red * (1 + t), c.Green * (1 + t), c.Blue * (1 + t), c.Alpha);

    private static Color Grey(Color c) { var l = c.Red * 0.3f + c.Green * 0.59f + c.Blue * 0.11f; return new Color(l, l, l); }

    private static void Apply(IButtonHandler handler, IButton view)
    {
        if (view is not Button button || !States.TryGetValue(button, out var state)) return;
        if (handler.PlatformView is not MaterialButton native) return;

        var enabled = button.IsEnabled;
        var body = button.BackgroundColor ?? UiTokens.ButtonTop;
        var edge = button.BorderColor ?? UiTokens.ButtonEdge;
        if (!enabled)
        {
            body = Color.FromRgba((body.Red + Grey(body).Red) / 2 * 0.8f, (body.Green + Grey(body).Green) / 2 * 0.8f, (body.Blue + Grey(body).Blue) / 2 * 0.8f, 1f);
            edge = UiTokens.ButtonEdge.WithAlpha(0.5f);
        }

        var radius = Dp(native, button.CornerRadius >= 0 ? button.CornerRadius : UiTokens.ControlRadius);
        var stroke = (int)Math.Max(1, Math.Round(Dp(native, button.BorderWidth > 0 ? button.BorderWidth : UiTokens.ControlEdge)));
        var drop = (int)Math.Round(Dp(native, Drop + (state.Focused ? 1 : 0)));
        var hair = (int)Math.Max(1, Math.Round(Dp(native, 1)));

        LayerDrawable Build(bool pressed)
        {
            var sink = pressed ? drop : 0;
            var top = pressed ? Shade(body, -0.02f) : Shade(body, enabled ? 0.16f : 0.03f);
            var bottom = pressed ? Shade(body, -0.12f) : Shade(body, -0.14f);

            var shadow = new GradientDrawable();
            shadow.SetCornerRadius(radius);
            shadow.SetColor(Native(UiTokens.PanelShadow.WithAlpha(enabled ? 0.9f : 0.45f)));

            var fill = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, [Native(top), Native(bottom)]);
            fill.SetCornerRadius(radius);
            fill.SetStroke(stroke, Native(edge));

            var light = new GradientDrawable();
            light.SetColor(Native(Colors.White.WithAlpha(pressed || !enabled ? 0.06f : state.Primary ? 0.34f : 0.22f)));

            var strip = new GradientDrawable();
            var stripColor = state.Strip is { } sc && !state.Focused ? (enabled ? sc : sc.WithAlpha(0.35f)) : Colors.Transparent;
            strip.SetColor(Native(stripColor));
            var sw = Dp(native, 3);
            strip.SetCornerRadii([Math.Max(0, radius - stroke), Math.Max(0, radius - stroke), 0, 0, 0, 0, Math.Max(0, radius - stroke), Math.Max(0, radius - stroke)]);

            var modern = OperatingSystem.IsAndroidVersionAtLeast(23);
            var layers = modern ? new LayerDrawable([shadow, fill, light, strip]) : new LayerDrawable([shadow, fill]);
            // Shadow: the full box, peeking out below the body by `drop`.
            layers.SetLayerInset(0, 0, drop, 0, 0);
            // Body: sits `drop` above the bottom at rest; pressed = sunk onto the shadow.
            layers.SetLayerInset(1, 0, sink, 0, drop - sink);
            if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return layers;
            // 1 px top light just inside the edge.
            layers.SetLayerInset(2, stroke + (int)(radius / 2), sink + stroke, stroke + (int)(radius / 2), 0);
            layers.SetLayerHeight(2, hair);
            layers.SetLayerGravity(2, Android.Views.GravityFlags.Top | Android.Views.GravityFlags.FillHorizontal);
            // Accent strip: the leading edge inside the stroke (secondary signal buttons).
            layers.SetLayerInset(3, stroke, sink + stroke, 0, drop - sink + stroke);
            layers.SetLayerWidth(3, (int)sw);
            layers.SetLayerGravity(3, Android.Views.GravityFlags.Left | Android.Views.GravityFlags.FillVertical);
            return layers;
        }

        var states = new StateListDrawable();
        states.AddState([Android.Resource.Attribute.StatePressed], Build(true));
        states.AddState([], Build(false));

        native.BackgroundTintList = null;
        native.Background = states;
        // MAUI re-installs its own ripple/border background after the mapper pass (on attach /
        // layout); keep ours by re-asserting it whenever the view lays out with a foreign one.
        Skins.AddOrUpdate(native, states);
        if (!Hooked.TryGetValue(native, out _))
        {
            Hooked.Add(native, new object());
            native.LayoutChange += (_, _) =>
            {
                if (Skins.TryGetValue(native, out var mine) && !ReferenceEquals(native.Background, mine))
                {
                    native.BackgroundTintList = null;
                    native.Background = mine;
                }
            };
        }
        native.StateListAnimator = null;
        native.Elevation = 0;
        native.StrokeWidth = 0;
        native.RippleColor = null;

        var ink = button.TextColor ?? UiTokens.Ink0;
        native.SetTextColor(Native(enabled ? ink : ink.WithAlpha(0.45f)));
        native.SetShadowLayer(0.01f, 0, hair, Native(UiTokens.PanelShadow.WithAlpha(enabled ? 0.8f : 0f)));

        if (state.Icon is { } icon)
        {
            try
            {
                var neutral = button.BackgroundColor is null || Near(button.BackgroundColor, UiTokens.ButtonTop);
                var tint = neutral && !state.Focused ? PksmIcons.Cyan : PksmIcons.White;
                var px = (int)Dp(native, Math.Round(button.FontSize + 3));
                var key = $"{icon}|{tint}|{px}";
                if (!Bitmaps.TryGetValue(key, out var bmp))
                {
                    var png = PksmIcons.GetPng(icon, tint);
                    using var raw = Android.Graphics.BitmapFactory.DecodeByteArray(png, 0, png.Length)!;
                    bmp = Android.Graphics.Bitmap.CreateScaledBitmap(raw, px, px, false)!;
                    Bitmaps[key] = bmp;
                }
                var drawable = new BitmapDrawable(native.Resources, bmp);
                drawable.SetFilterBitmap(false);
                if (!enabled) drawable.SetAlpha(110);
                native.Icon = drawable;
                native.IconTint = null;
                native.IconSize = px;
                native.IconPadding = (int)Dp(native, 6);
                native.IconGravity = MaterialButton.IconGravityTextStart;
            }
            catch { /* icon is decoration; never fail the button */ }
        }
    }

    /// <summary>Text sits centered in the body, not the body + shadow box.</summary>
    private static void ApplyPadding(IButtonHandler handler, IButton view)
    {
        if (view is not Button button || !States.TryGetValue(button, out var state)) return;
        if (handler.PlatformView is not Android.Widget.Button native) return;
        var p = button.Padding;
        var drop = (int)Math.Round(Dp(native, Drop + (state.Focused ? 1 : 0)));
        native.SetPadding((int)Dp(native, p.Left), (int)Dp(native, p.Top), (int)Dp(native, p.Right), (int)Dp(native, p.Bottom) + drop);
    }
#endif
}
