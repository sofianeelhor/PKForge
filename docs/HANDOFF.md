# PKForge Continuation Notes

Date: September 20, 2026

## Current state

- Repository: /Users/sof/work/pkforge
- Stable branch: main
- Current stable release: 2.2.1
- Release tag: v2.2.1
- Latest stable commit: c543f5c
- main is the authoritative branch.
- Do not modify or publish release history without explicit approval.

## Project architecture

- Android-first .NET MAUI application.
- UI is C# code-behind with SkiaSharp.
- There are no XAML views.
- `src/PKForge.Domain` contains domain contracts and behavior.
- `src/PKForge.Engine` integrates with PKHeX.
- `src/PKForge.Infrastructure` handles persistence and backups.
- `src/PKForge.App` contains MAUI UI, ViewModels, rendering, and Android code.
- `external/PKHeX` must never be edited.

## Engineering rules

- Inspect the repository before editing.
- Preserve all existing user changes.
- Do not use destructive Git commands.
- Do not force-push.
- Do not push, tag, publish, or merge to main without explicit approval.
- Use `apply_patch` for source changes.
- Keep changes focused.
- Measure performance before optimizing.
- Do not block the UI thread with file I/O, save parsing, image decoding, or PKHeX initialization.
- Do not use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` in UI paths.
- Preserve save safety:
  `validate -> backup -> atomic write`.
- Never edit `external/PKHeX`.
- Never add keys, passwords, tokens, APKs, save files, logs, or local tooling state to Git.

## Validation commands

```bash
git status --short --branch

/tmp/pkforge-dotnet/dotnet test \
  tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj \
  --no-restore --nologo

/tmp/pkforge-dotnet/dotnet test \
  tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj \
  --no-restore --nologo

/tmp/pkforge-dotnet/dotnet build \
  src/PKForge.App/PKForge.App.csproj \
  -f net10.0-android \
  -r android-arm64 \
  --no-restore --nologo

## Session note (September 20, 2026): encounter cards

PKSM gap analysis chose the Tier-1 roadmap item "How do I get this?" and it now
ships end to end:

- Domain: `EncounterCard` record plus `GetEncounterCards` / `PlaceEncounter` on
  `ISaveEngineSession` (`src/PKForge.Domain/EntityEditing.cs`).
- Engine: `SaveEngineSession` enumerates the pinned `EncounterMovesetGenerator`
  with empty moves (whole species line, including pre-evolutions), scoped to the
  open game, de-duplicated for display, cloned so the source mon is never touched.
  `PlaceEncounter` materializes a card via `IEncounterConvertible.ConvertToPKM`,
  gates on `LegalityAnalysis.Valid`, and refuses occupied/full destinations.
  Unbound returns no cards (custom maps are not in the pinned database).
- App: `EncounterGallery` (mon menu: "How to get this?") shows the grouped card
  wall with a close-up pane; A confirms a CATCH into the first empty slot through
  the usual validate -> backup -> atomic write.
- Verification: 6 new engine tests (`EncounterCardTests`) covering Trophy Garden
  ground truth in Platinum, determinism, no source mutation, legal placement,
  serialize round trip, refusal paths, and Unbound/empty-slot behavior.
  Suites: 297 engine + 76 domain tests green; Android arm64 build clean.
  Timing probe (since removed): worst cases (Bidoof/Pt, Sobble and Caterpie
  lines/Sword) each enumerate in tens of milliseconds off the UI thread.

PKSM-inspired backlog that remains: storage filter/search (bank search TODO),
QR import, multiple banks, event flags editor, mon hex editor.

## Session note 2 (September 20, 2026): picker perf, encounter UX, Poképark, collection dex

- **Pokédex picker CPU fix:** the cold-cache icon sweep (`EnsureIconsAsync`) ran
  ~1000 sprite decodes at 64-way parallelism on first open, starving the UI and the
  offline legalizer (the fan-screaming Create flow on fresh installs). It now runs
  8-wide with pauses, once per process.
- **Encounter cards rework:** species-first (TOOLS -> "How to get a Pokémon…" ->
  Pokédex picker -> form -> cards), two-column 280px cards with 22px location text,
  hero sprite beside the title; `GetEncounterCards(species, form)` /
  `PlaceEncounter` on `ISaveEngineSession` build the rough criteria entity from
  `_save.BlankPKM`. CATCH places through the usual backed-up write.
- **Poképark:** auto-invited residents no longer fall back to "Pokémon #N" (species
  names everywhere); persisted rosters self-heal on load (`IsLegacyFallbackName`).
  Residents now carry GameName/TrainerName/Origin captured at invite; the resident
  journal shows an ORIGIN section at the bottom.
- **Collection dex (living dex tracker):** `CollectionDex.Compute` in Domain (pure,
  tested), `CollectionDexPage` in App: national + shiny living dex over bank plus
  open save, per-gen and this-game scopes, missing-only filter, "How to get" jump
  from any species. Entries: Bank strip "LIVING DEX" capsule and TOOLS ->
  "Collection dex…".
- Verification: 298 engine + 79 domain tests green (3 new `CollectionDexTests`);
  diagnostic arm64 APK builds clean. Not yet device-verified: tracker layout on the
  Thor, Poképark migration on real data.

Backlog added from user feedback: Android home-screen widget showing a rotating
bank Pokémon; storage filter/search; QR import; multiple banks.

## Session note 3 (September 20, 2026): save-free encounter guide, sprite threading, tracker

- **Sprites were missing on the collection dex** because `CollectionDexPage` passed a
  raw `_canvas.InvalidateSurface` to `SpriteService.Warm`, whose callback runs on a
  thread-pool thread; repaints never landed. Now marshalled through
  `MainThread.BeginInvokeOnMainThread` (same fix applied at `DexEditorPage`).
- **"How to get" no longer needs a save open**: `IEncounterLookup` /
  `EncounterLookupService` walk one blank save per mainline game (37 concrete games)
  and describe each. Verified: 37 games in ~195 ms, per-species cached (0 ms repeat);
  Sprigatito only in Scarlet/Violet.
- **PKHeX version-group trap**: `BlankSaveFile.Get(D)` returns a save whose version is
  the lumped `DP`, which `EncounterPossible4` rejects with
  `ArgumentOutOfRangeException`. `EncounterDatabase.Enumerate/Describe` now take an
  explicit `GameVersion`, and `ConcreteVersions` maps groups (DP→D/P, HGSS→HG/SS, …).
  Same trap would have crashed real Diamond/Pearl saves through the session path.
- **Diamond vs Pearl asymmetry explained and pinned**: wild/egg/trade are mirrored
  (both list Trophy Garden Lv16/18); the delta is PKHeX's per-version Gen 4 event-gift
  tables. Regression test `DiamondAndPearlAreMirroredForWildEncounters` guards it.
- **New UI**: species → game list grouped by generation (`N ways · Wild, Eggs, …` or
  "Not obtainable here", THIS GAME flagged and promoted) → that game's card wall, with
  repeated (kind, place) sightings collapsed into one card carrying a level span and a
  "N spots" count. CATCH appears only for a game the open save can be; other games stay
  informational. `ISaveEngineSession.GameNames` (plural, group-expanded) drives that.
- Verification: 303 engine + 79 domain tests green; diagnostic arm64 APK builds clean.
  Still device-unverified: the new guide layout and the collapsed-SV case.
