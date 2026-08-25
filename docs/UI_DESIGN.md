# PKForge — art direction

## The language

PKForge speaks the **3DS-era Pokémon UI** dialect: the visual world of PKSM, the DS
system menus, and the Gen-5/6 games. It is a pixel-craft language, not a flat-design
theme.

One-line brief: *a handheld you want to hold — the memory of DS-era Pokémon storage,
rebuilt crisp for a modern screen.*

## Structure (what makes it read as authentic)

- **White Paper panels** with warm-grey (`#D1CBC0`) 2px borders and soft lift shadows.
- **Maroon Gen-5 header strips** (`#4C1212`) with white pixel text, on panels and screens.
- **Striped menu rows**: indigo-light selected band, indigo edge bar, red glove pointer.
- **Per-screen worlds**: storage sits on saturated box wallpapers with a dot lattice and
  white crosshair brackets; the editor/summary world is light summary blue (`#7FA0D2`);
  events is gift pink (`#D3766A`) with white four-point sparkles; the bag is navy
  (`#1B2C5D`) with cyan pills ringed yellow-green.
- **Choice buttons** (STATS/MOVES/SAVE): cream fill, cyan rim, white inner rim.
- **Stack buttons** (View/Clear/Release): deep blue fill, white inner border, dark outline.
- **The icon set** is PKSM's two-tone indigo pixel set (bundled, GPL-3; see
  `src/PKForge.App/Resources/UI/ATTRIBUTION.md`), tinted deep indigo on white surfaces.
- **Typography**: NDS12 (`PixelUI`) for the console voice at 16-multiple sizes; text on
  wallpapers carries a 2px offset shadow.
- **Hint bars**: dark strip, round blue key discs, white pixel labels — the app declares
  itself gamepad-first on every screen.

## Tokens and painters

- All colors live in `src/PKForge.Chrome/Pksm.cs` (`Pksm.*`), mapped to MAUI in
  `src/PKForge.App/Theme/UiTheme.cs` (`UiTokens.*`). Views never hardcode colors.
- Drawn chrome primitives live in `src/PKForge.Chrome/PksmPaint.cs` (Panel, HeaderStrip,
  StripeRow, StackButton, ChoiceButton, BagPill, LangChip, Wallpaper, Crosshair,
  BoxNameBar, Pointer, Selection, Slot, HintBar, Sparkle).
- Iterate the design off-device with `dotnet run --project tools/ChromePreview` — it
  renders every component and mock screens to PNG.

## Hard rules

- Clarity survives the style: where am I, what's selected, is it legal, is my save safe.
  Legality green/red are functional signals, never decoration.
- Gamepad-first, dual-screen-aware: focus (the cursor) and selection are distinct states.
- Nothing tiny; confident sizing for a handheld held at arm's length.
- No unicode-glyph icons standing in for pixel art. No em-dashes in UI text.
  Roman numerals for generations. Sprites are the hero.
- Consistency by construction: compose from the kit; a new surface must reuse its
  primitives or extend them, never freelance.

## Anti-goals

Generic Material/flat-SaaS defaults · cold dark dashboards · web-gradient panels ·
full 8-bit throwback · visual clutter · anything that looks auto-generated.
