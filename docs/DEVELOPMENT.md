# PKForge — development guide

Everything needed to build, test, and extend PKForge. Start here, then
`docs/ARCHITECTURE.md` (layering), `docs/BANK_MODEL.md` (vault storage),
`docs/UI_DESIGN.md` (art direction), `docs/PRIOR_ART.md` (prior-art study).

## What PKForge is

A GPLv3 **.NET MAUI Android** app for dual-screen gamepad handhelds (developed on the
AYN Thor). A Pokémon **save editor + persistent cross-generation Bank** on **pristine
PKHeX.Core** (submodule, pinned at the revision in `external/PKHeX`, never edited) with
the **Auto Legality Mod compiled in-process** (`src/PKForge.AutoMod` recompiles the
plugins' sources against our pinned Core so versions cannot drift).

## Non-negotiable invariants

1. **PKHeX.Core is pristine** — never edit `external/PKHeX`. All engine access goes
   through adapters in `PKForge.Engine`.
2. **Data safety is sacred** — every write is **validate → backup → atomic write**. No
   exceptions, even for bulk ops. Invalid candidate = no backup, no write (test-covered).
3. **Offline-first** — assets are bundled or cached at runtime; the app works with no network.
4. **Layering** — Domain (interfaces/DTOs) → Engine (PKHeX adapters) → Infrastructure →
   App. The App layer never touches PKHeX.Core directly.
5. **Never ship secrets** — `sgdb.key` (SteamGridDB) is gitignored and must be absent
   from every APK (`unzip -l <apk> | grep -c sgdb.key` must be 0).

## Build / test

```bash
export DOTNET_CLI_HOME=$PWD/.dotnet-home DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet test tests/PKForge.Engine.Tests
dotnet test tests/PKForge.Domain.Tests
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-android
# Release APK:
dotnet publish src/PKForge.App/PKForge.App.csproj -f net10.0-android -c Release -o dist/
```

- `TreatWarningsAsErrors=true` everywhere — warnings fail the build. A raw `&` or `<`
  in a `///` comment breaks it.
- The design system can be iterated off-device:
  `dotnet run --project tools/ChromePreview` renders every chrome component to PNG.
- CI (`.github/workflows/build.yml`) runs tests + debug APK on every push.

## Engine API facts (verified against the pin — trust these over the wiki)

- `SaveUtil.TryGetSaveFile(byte[], out SaveFile)`; `save.Write()`; `save.BoxCount`,
  `save.BoxSlotCount` (**per-box**), `GetBoxSlotAtIndex/SetBoxSlotAtIndex`,
  `save.Personal.GetFormEntry(...)`, `save.Inventory.Pouches`, `BlankPKM`, `Context`.
- **Blank save for a loose mon:** `BlankSaveFile.Get(GameVersion, trainerName, LanguageID)`;
  version via `entity.Context.GetSingleGameVersion()`. **CAUTION:** a freshly-built blank
  Gen3/4 save **cannot be serialized** (`SAV3.WriteSectors` throws) — the standalone
  `SaveEngineSession` ctor deliberately does not call `save.Write()`.
- `PKM` interface-gated fields: gate UI with `pk is ITeraType`, `IHyperTrain`, etc., and
  hide when absent. `ResetPartyStats()` already applies mints and Hyper Training.
- `GameInfo.GetStrings("en")` name tables; `GameInfo.GetLocationList(version, context, egg)`.
- ALM: `sav.GetLegalFromSet(set)`, `sav.Legalize(pk)`, `sav.GenerateLivingDex(sav.Personal)`.
- Events: `EncounterEvent.MGDB_G4..G9` embedded wondercards.
- Corpus for tests: `external/PKHeX/Tests/PKHeX.Core.Tests/` (Gen7 save + ~195 loose
  `.pk*` across every generation).

## Release procedure

1. Build clean (0 warnings), tests green.
2. `dotnet publish ... -c Release` → verify `unzip -l <apk> | grep -c sgdb.key` == 0.
3. Tag `vX.Y.Z` (csproj `ApplicationDisplayVersion` must match), push; CI attaches the APK.
4. Verify the uploaded asset digest matches the local `shasum -a 256`.

## Roadmap

PKHeX's `PKM` has 60+ editable surfaces; PKForge exposes a growing subset. Next up:
"How do I get this?" encounter cards (`EncounterMovesetGenerator`), batch editor
(`EntityBatchEditor`), box ops (`BoxManipulator`), ribbons/marks album, form editor.
Full tier list in the git history of this file; `docs/PRODUCT_MAP.md` tracks surfaces.

## Known gotchas

- **Gender on PID-based formats (Gen 3–5):** gender derives from the PID; `Gender = x`
  may not stick. Legalize resolves it.
- **AutoMod ImplicitUsings** must stay disabled in `src/PKForge.AutoMod`.
- **SkiaSharp 3.x:** touch `Released` only fires if `Pressed` was `Handled`;
  `GetPixelSpan()` for pixel scans, never per-pixel `GetPixel`.
- **MAUI bootstrap:** `MainApplication : MauiApplication` is required or the app shows a
  blank screen. Landscape locked, immersive fullscreen.
- **Second screen:** the Thor's lower display is not presentation-category →
  `GetDisplays()[1]` fallback; page inflated via `ToPlatform(new MauiContext(...))`.
- **Gamepad:** `MainActivity.DispatchKeyEvent` → `GamepadRouter` stack; pages implement
  `IPadHandler`, popups push themselves while open. `StatsPopup`/`TextPopup` need the
  touchscreen (numeric/text entry).
