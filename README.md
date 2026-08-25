# PKForge

A Pokémon save editor and multi-generation bank for Android, built for dual-screen
handhelds — designed and tested on the [AYN Thor](https://www.ayn.com.cn). It renders its
whole interface with [SkiaSharp](https://github.com/mono/SkiaSharp) in a 3DS-era visual
language: white panels, maroon Gen-5 headers, striped menus, and per-box wallpapers,
driven entirely by a gamepad across two screens.

Built on the pristine [PKHeX.Core](https://github.com/kwsch/PKHeX) engine (git submodule,
pinned) with the [Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins) compiled
in-process.

**We do not support or condone cheating at the expense of others. Do not use significantly
edited Pokémon in battle or in trades with those who are unaware edited Pokémon are in use.**

Not affiliated with Nintendo, Game Freak, or The Pokémon Company.

## Features

- **Storage** — box browser with grab/place like the games' PC, legality verdicts, a live
  summary + stat radar on the second screen.
- **Editor** — full Pokémon editing (species, moves, IVs/EVs, Met/Origin, Tera, Hyper
  Training...), Showdown import/export, one-tap legalizer, QR export.
- **Bank** — durable cross-game vault with unlimited themed boxes.
- **Events** — the full mystery-gift database, offline wondercards, community boxes.
- **Save tools** — trainer card, bag editor, Pokédex, backups with one-tap restore.
- Every gamepad button mapped on every screen; touch works too.

## Safety invariants

- Every save write is preceded by a timestamped backup.
- Writes validate through the engine and fail closed — an invalid candidate never touches storage.
- Open → serialize with no edits is byte-identical (enforced by tests against a real save corpus).
- All storage access uses Android's Storage Access Framework; no raw filesystem paths.

## Layout

```
src/PKForge.Domain          engine-agnostic contracts + DTOs (no MAUI/Android deps)
src/PKForge.Engine          thin adapter over pinned PKHeX.Core
src/PKForge.AutoMod         source-compile shim for the Auto Legality Mod
src/PKForge.Infrastructure  DI-friendly service implementations
src/PKForge.App             .NET MAUI Android app (SkiaSharp UI)
src/PKForge.Chrome          the design system: tokens + chrome painters (pure Skia)
tools/ChromePreview         off-device renderer for the design system
tests/                      xUnit: domain behavior + save round-trip corpus
external/PKHeX              pristine engine submodule (pinned revision)
docs/                       ARCHITECTURE, BANK_MODEL, DEVELOPMENT, PRIOR_ART, UI_DESIGN
```

## Build

Requirements: .NET 10 SDK with the `maui-android` workload, Android SDK (API 36).

```bash
git submodule update --init --recursive
dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj
dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-android
```

Produces a debug-signed APK under `src/PKForge.App/bin/Debug/net10.0-android/`.
Releases are published from CI on version tags.

## Credits

- [PKHeX](https://github.com/kwsch/PKHeX) and its contributors — the engine everything runs on.
- [Auto Legality Mod](https://github.com/antialiasis/PKHeX-Plugins) — legal histories made easy.
- [PKSM](https://github.com/FlagBrew/PKSM) by FlagBrew — the pixel chrome this app's UI
  language builds on (GPL-3; see `src/PKForge.App/Resources/UI/ATTRIBUTION.md`).
- Pokémon sprite art © Nintendo/Creatures Inc./GAME FREAK inc., bundled via PKHeX.

## License

GPLv3-or-later, inherited from PKHeX.Core. See [LICENSE](LICENSE).
