<p align="center"><img src="docs/images/logo-transparent.png" alt="PKForge" width="140" /></p>

<p align="center"><b>A Pokémon save editor and bank for Android, built for dual-screen handhelds.</b></p>

<p align="center">
  <a href="https://discord.gg/bMtzZmTDfu"><img src="https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?logo=discord&logoColor=white" alt="Discord" /></a>
  <a href="https://github.com/sofianeelhor/PKForge/releases"><img src="https://img.shields.io/badge/Download-APK-2B4E95" alt="Download" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue" alt="License" /></a>
</p>

---


A Pokémon save editor and cross-generation Bank for Android, tuned for dual-screen
handhelds like the AYN Thor. The whole interface is drawn with SkiaSharp in the visual
language of the DS-era games: box wallpapers, pixel sprites, gamepad controls, and a
second screen that shows a live summary of whatever is under the cursor.

Built on [PKHeX.Core](https://github.com/kwsch/PKHeX) with the
[Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins) compiled in-process, so
legalizing a Pokémon works fully offline, on device.

Not affiliated with Nintendo, Game Freak, or The Pokémon Company.

## Features

- **Game library:** Linked saves appear as cartridge shelves, filterable by release
  order, alphabetically, by console, or by generation. Gamepad-first navigation with
  distinct cursor and selection states.
- **Full Pokémon editor:** Stats, IVs, EVs, moves, Met/Origin, Tera type, Hyper
  Training, shininess, and more, with a live legality check as you edit.
- **Cross-game Bank:** Unlimited themed boxes across every generation, with gamepad
  grab, move, and organize.
- **Living Dex tracker:** Track your collection across all 1025 Pokémon, generation by
  generation, including shiny variants.
- **Mystery Gift database:** The full event database, fully offline. Browse and inject
  Wonder Cards into your save.
- **Bag editor:** Edit any pouch, any item, any quantity.
- **RNG tools:** Inspect PID, IVs, and nature, and reroll a nature while keeping
  shininess.
- **Poképark:** A living habitat where your Pokémon wander while you're away.
- **One-tap legalize:** Auto Legality Mod, offline.
- **Showdown import/export** and QR codes.
- **Trainer card editor:** Name, IDs, money, gender.
- **Nuzlocke report:** Track first encounters and dupes per route.
- **Encounter browser:** Every way to catch a species, per game.
- **Restore points** and safe, atomic save writes.

## Screenshots

<p align="center">
  <img src="docs/screenshots/home.png" alt="Home" width="49%" />
  <img src="docs/screenshots/editor.png" alt="Editor" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/bank.png" alt="Bank" width="49%" />
  <img src="docs/screenshots/living-dex.png" alt="Living Dex" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/mystery-gift.png" alt="Mystery Gift" width="49%" />
  <img src="docs/screenshots/bag.png" alt="Bag" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/rng.png" alt="RNG tools" width="49%" />
  <img src="docs/screenshots/pokepark.png" alt="Poképark" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/trainer-card.png" alt="Trainer card" width="49%" />
  <img src="docs/screenshots/showdown-qr.png" alt="Showdown QR" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/nuzlocke.png" alt="Nuzlocke report" width="49%" />
  <img src="docs/screenshots/encounters.png" alt="Encounter browser" width="49%" />
</p>

## Supported games

PKForge reads and edits saves from every mainline generation, plus the GameCube side
games and popular romhacks:

- **Generation I:** Red, Blue, Green (JP), Yellow
- **Generation II:** Gold, Silver, Crystal
- **Generation III:** Ruby, Sapphire, Emerald, FireRed, LeafGreen, Pokémon Box: Ruby & Sapphire, Colosseum, XD: Gale of Darkness
- **Generation IV:** Diamond, Pearl, Platinum, HeartGold, SoulSilver
- **Generation V:** Black, White, Black 2, White 2
- **Generation VI:** X, Y, Omega Ruby, Alpha Sapphire
- **Generation VII:** Sun, Moon, Ultra Sun, Ultra Moon, Let's Go Pikachu, Let's Go Eevee
- **Generation VIII:** Sword, Shield, Brilliant Diamond, Shining Pearl, Legends: Arceus
- **Generation IX:** Scarlet, Violet
- **Romhacks:** Pokémon Unbound, Luminescent Platinum, Pokémon Compass

## Supported emulators

Link a storage unit and PKForge finds your saves automatically:

- **Game Boy / Game Boy Color:** RetroArch, Linkboy, Pizza Boy C
- **Game Boy Advance:** RetroArch, Linkboy, Pizza Boy A
- **Nintendo DS:** melonDS, DraStic, RetroArch
- **GameCube:** Dolphin
- **Nintendo 3DS:** Azahar
- **Nintendo Switch:** Eden

You can also open a single save file directly.

## Save safety

Every write follows **validate → backup → atomic write**. No exceptions, even for bulk
operations. An invalid candidate means no backup and no write. Your saves are never at
risk.

## Installation

Download the APK from [Releases](https://github.com/sofianeelhor/PKForge/releases) and
allow installs from unknown sources. First run walks you through linking an emulator
(RetroArch, melonDS, Azahar, Eden) or opening a single save file.

## 💬 Discord

Join for updates, support, bug reports, feature requests, or just to chat about the project.

👉 **[Join the PKForge Discord](https://discord.gg/bMtzZmTDfu)**

[![Discord](https://discordapp.com/api/guilds/1542192456018427926/widget.png?style=banner3&time-)](https://discord.gg/bGKEyfY)

## Building

.NET 10 SDK with the `maui-android` workload, Android SDK (API 36).

```bash
git submodule update --init --recursive
dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj
dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-android
```

Version tags build and publish the APK from CI.

### Layout

```
src/PKForge.Domain          contracts and DTOs, no engine or Android dependencies
src/PKForge.Engine          adapters over pinned PKHeX.Core
src/PKForge.AutoMod         compiles the Auto Legality Mod against our Core
src/PKForge.Infrastructure  bank, backups, atomic save writer
src/PKForge.App             MAUI app, SkiaSharp UI, gamepad and second screen
src/PKForge.Chrome          the design system: tokens and painters, pure Skia
tools/ChromePreview         renders the design system off-device
docs/                       architecture, bank model, development, art direction
```

## Credits

- [PKHeX](https://github.com/kwsch/PKHeX), the engine everything runs on
- [PKHeX-Plugins / Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins), the
  offline legalizer compiled in-process
- [PKSM](https://github.com/FlagBrew/PKSM), the pixel chrome this UI builds on (GPL-3,
  see src/PKForge.App/Resources/UI/ATTRIBUTION.md)
- [SteamGridDB](https://www.steamgriddb.com), cartridge icons, second-screen logos, and
  hero banners for the game library (community submissions; see
  src/PKForge.App/Resources/GameArt/ATTRIBUTION.md)
- [PokeAPI](https://pokeapi.co), item art fetched at runtime and cached on device
- [game-icons.net](https://game-icons.net) (CC-BY 3.0, © Lorc, Delapouite, Guard13007,
  Carl Olsen and other contributing artists), UI symbols
- [Bulbagarden Archives](https://archives.bulbagarden.net), Pokérus status sprites
- Sprites and Pokémon names © Nintendo, Creatures Inc., GAME FREAK inc.

## License

GPLv3 or later, inherited from PKHeX.Core. See [LICENSE](LICENSE).