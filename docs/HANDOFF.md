# PKForge continuation handoff

Updated: 2026-09-20

This is the working handoff for continuing PKForge after the v2.2.0 release. The
current work is local only. Do not push, force-push, or create a remote branch from
this checkout unless the owner explicitly asks for that later.

## Current repository state

- Repository: `/Users/sof/work/pkforge`
- Active local branch: `dev`
- `dev` is based on local `main` at `f00dbaa` (`v2.2.0`).
- `origin/dev` is stale at the 2.1.0 release; do not merge it over this work.
- Latest local commits:
  - `3e23147 perf: remove pokepark work from home startup`
  - `dcb00a3 fix: smooth pokepark transitions and gen4 bag writes`
- Working tree should be clean after this handoff commit.
- `docs/HANDOFF.md` did not exist in the v2.2.0 tree; this file is the source of
  truth for the continuation described below.

## Project shape

PKForge is an Android-first .NET MAUI app for the AYN Thor. Views are code-behind
MAUI plus SkiaSharp; there are no XAML views.

- `src/PKForge.Domain`: engine-neutral contracts, records, and domain behavior.
- `src/PKForge.Engine`: adapter around the pristine `external/PKHeX` submodule.
- `src/PKForge.Infrastructure`: atomic writes, backups, banks, settings, and file
  persistence.
- `src/PKForge.App`: MAUI pages, ViewModels, SkiaSharp rendering, Android glue, and
  DI composition in `MauiProgram.cs`.
- `tests/PKForge.Engine.Tests`: save/engine and regression tests.
- `tests/PKForge.Domain.Tests`: pure domain tests.
- `external/PKHeX`: pinned upstream input. Never edit it.

Read these before changing architecture or persistence:

1. `docs/DEVELOPMENT.md`
2. `docs/ARCHITECTURE.md`
3. `docs/UI_DESIGN.md`
4. `docs/PRODUCT_MAP.md`
5. `docs/BANK_MODEL.md`
6. `docs/EMULATOR_LINKING.md`

## Build and test

The repository uses a bundled .NET installation. The exact Android build used for
device testing is:

```bash
/tmp/pkforge-dotnet/dotnet build src/PKForge.App/PKForge.App.csproj \
  -f net10.0-android -r android-arm64
```

Run the complete host-side test pass:

```bash
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj --no-restore
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj --no-restore
```

For a side-by-side device APK that does not overwrite production `org.pkforge.app`:

```bash
/tmp/pkforge-dotnet/dotnet build src/PKForge.App/PKForge.App.csproj \
  -f net10.0-android -r android-arm64 \
  -p:DiagnosticBuild=true -p:AndroidKeyStore=false
```

The diagnostic APK is normally:
`src/PKForge.App/bin/Debug/net10.0-android/android-arm64/org.pkforge.app.debug-Signed.apk`.
Install it with `adb install -r`. It uses package ID `org.pkforge.app.debug` and
therefore has separate app-private settings/data.

The normal local debug APK is signed with the Android debug key. It cannot update a
production APK signed by PKForge's permanent release certificate. Production
updates require the tagged GitHub Actions release workflow and its protected
keystore secrets. Never copy keys into this repository.

## Important safety rules

- Never edit `external/PKHeX`.
- Every save mutation must remain validate → backup → atomic write.
- Preserve the user's existing uncommitted work; inspect `git status` before edits.
- Use `apply_patch` for source edits.
- Keep all work on local `dev`; never push to `origin`.
- Treat APKs and save files as user data. Do not delete them casually.
- `TreatWarningsAsErrors=true`; XML documentation containing raw `<` or `&` can fail
  the build.

## What the recent work changed

### PokePark and navigation

- `HomePage` no longer starts PokePark save candidate discovery during Home startup.
  Save scanning is expensive on a handheld and competed with cartridge discovery.
- `PokeparkPage` displays the persisted roster immediately. Existing rosters do not
  trigger a candidate scan when opening the park.
- First-use auto-fill is delayed until after the first navigation frame and runs only
  when the roster is empty.
- Candidate discovery uses `SaveEngineSession.Snapshot` metadata. It does not call
  `ReadEntity`, `ExportSlot`, or SHA-256 for every populated slot.
- `PokeparkScene` loads only the active habitat map rather than all maps at entry.
- The lower Thor presentation switches into the journal in place. It no longer
  dismisses and reconstructs the entire Presentation when entering PokePark.
- `PokeparkJournalState.IsOpen` distinguishes an open empty park from Home/Storage.
- Home PokePark navigation has a reentrancy guard.
- Widget rendering is skipped when no launcher widget exists and is not run every
  animation tick while the park is visible.

### Gen IV bag / Rare Candy

`SaveEngineSession.SetItemCount` now creates a clean pouch entry and calls
`InventoryPouch.ClearCount0()` after mutations. This restores the canonical occupied
prefix / empty tail layout expected by Gen IV games. PKHeX can parse entries after a
zero-count gap, but Diamond's bag UI can stop at that gap; this explained Potion
working while newly-added Rare Candy was invisible in-game. Regression coverage is in
`tests/PKForge.Engine.Tests/AddItemProbe.cs`.

### Second-screen lifecycle

`SecondScreenBoxPage` stores and detaches its `PropertyChanged` handlers in `Cleanup`.
This prevents old Thor presentation pages from accumulating and receiving every
PokePark journal update.

## Current known tradeoffs / follow-up work

1. Auto-filled save-backed PokePark names fall back to `Pokémon #<species>` when the
   snapshot has no nickname. This is intentional for speed; a future metadata cache
   can add localized species names without reparsing every slot.
2. The first-use auto-fill still performs a full candidate enumeration, but it is
   delayed until after navigation and is now snapshot-only. Consider adding a cached
   candidate list keyed by save document ID plus save revision if first-use scans are
   still noticeable.
3. The live Skia scene still allocates some LINQ tuples, sorting state, paints, fonts,
   and parsed colors per frame. The next performance pass should measure before
   rewriting it; target frame timing on the Thor rather than optimizing by assumption.
4. Widget rendering still shares scene state with the live page, although it is no
   longer run continuously while the park is open. A robust next step is to render
   from an immutable scene snapshot on an independent worker scene.
5. The app has no automated AYN Thor Perfetto trace yet. Capture navigation-to-first-
   paint, save scan duration, frame p95/p99, and secondary Presentation show time
   before making another broad optimization.
6. `docs/PRODUCT_MAP.md` and the older development documents describe the product
   roadmap; update them when adding a new user-facing surface.

## Recommended next investigation

Use the diagnostic APK and measure these separately:

1. Home launch until the first cartridge card is visible.
2. Home PokePark tap until the first primary canvas paint.
3. Home PokePark tap until the lower screen shows the journal.
4. First-use auto-fill completion versus opening an already-populated park.
5. Repeated enter/leave cycles, with and without an active launcher widget.

Add temporary `Stopwatch` logs around `HomePage.OnAppearing`, `RescanCommand`,
`PokeparkPage.OnAppearing`, `LoadParkAsync`, `WarmEnvironmentAsync`, and
`AndroidSecondaryDisplayHost.ShowAsync`. Remove or gate diagnostics before release.

## Device testing checklist

- Install the diagnostic package alongside production; do not uninstall production
  unless its data has been backed up.
- Confirm cartridges appear while the Home screen remains responsive.
- Confirm PokePark opens with an existing roster without a multi-second pause.
- Confirm the lower screen does not blank during Home → PokePark.
- Confirm an empty PokePark still shows the journal mode on the lower screen.
- Switch all four habitats and verify each map appears after its first load.
- Enter/leave PokePark repeatedly and watch for stale lower-screen pages or growing
  memory use.
- In a Pokémon Diamond save, add Rare Candy and Potion, restart the game, and verify
  both are visible in-game. Also test deleting a middle pouch entry.
- Re-run both test projects and the Android build after every engine or navigation
  change.

## Git continuation recipe

```bash
cd /Users/sof/work/pkforge
git status --short --branch
git log --oneline --decorate -5
git switch dev
# make focused changes
git diff --check
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj --no-restore
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj --no-restore
/tmp/pkforge-dotnet/dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-android -r android-arm64
git add <only-intended-files>
git commit -m "<local change description>"
git status --short --branch
```

Do not run `git push`. If another agent needs to continue, hand them the current
commit hash and this file.
