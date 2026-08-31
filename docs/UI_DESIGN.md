# PKForge — art direction

## The language

PKForge speaks the **3DS-era Pokémon UI** dialect: the visual world of PKSM, the DS
system menus, and the Gen-5/6 games. It is a pixel-craft language, not a flat-design
theme.

One-line brief: *a midnight pixel console you want to hold — the memory of DS-era
Pokémon storage, rebuilt inside the electric world of the PKForge logo.*

## Structure (what makes it read as authentic)

- **One shared logo grid**: navy `#1B2447` field, crisp cobalt `#2B4E95` lines, framed
  by void `#14121D`. It is the persistent housing behind every global screen.
- **Layered navy panels** with cobalt bezels, small pixel-offset shadows, and blue top
  edge light. Pale blue-white text adapts to every dark surface.
- **Cobalt Gen-5 header strips** with cyan signal edges and white pixel text.
- **Striped menu rows**: cobalt selected band, cyan edge bar, red glove pointer.
- **Per-screen worlds** remain recognizable but live at night: dark-tinted storage
  wallpapers with grid echoes and white crosshairs; a deep summary deck; gift plum with
  white four-point sparkles; and the logo navy/cyan bag and party worlds.
- **Choice buttons** (STATS/MOVES/SAVE): navy fill, cyan rim, cobalt structure.
- **Stack buttons** (View/Clear/Release): void/deep-navy fill, cobalt edge, white label.
- **The icon set** is PKSM's pixel set (bundled, GPL-3; see
  `src/PKForge.App/Resources/UI/ATTRIBUTION.md`), tinted cyan or white on dark surfaces.
- **Typography**: NDS12 (`PixelUI`) for the console voice at 16-multiple sizes; text on
  wallpapers carries a 2px offset shadow.
- **Hint bars**: void strip, round cyan key discs, white pixel labels — the app declares
  itself gamepad-first on every screen.

## Tokens and painters

- The five raw logo colors and all semantic colors live in
  `src/PKForge.Chrome/Pksm.cs` (`Pksm.*`), mapped to MAUI in
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

Generic Material/flat-SaaS defaults · anonymous dark dashboards · web-gradient panels ·
full 8-bit throwback · visual clutter · anything that looks auto-generated.
