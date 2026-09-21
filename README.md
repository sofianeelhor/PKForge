<p align="center">
  <img src="docs/images/logo-transparent.png" alt="PKForge" width="140">
</p>

<h1 align="center">PKForge</h1>

<p align="center">
  <b>A Pokémon save editor and bank for Android, built for dual-screen handhelds.</b><br>
  The whole interface is drawn with SkiaSharp in the visual language of the DS-era games.
</p>

<p align="center">
  <a href="https://discord.gg/bMtzZmTDfu"><img alt="Discord" src="https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?logo=discord&logoColor=white"></a>
  <a href="https://github.com/sofianeelhor/PKForge/releases"><img alt="Releases" src="https://img.shields.io/badge/Download-APK-2B4E95"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/License-GPLv3-blue"></a>
</p>

---

**PKForge** is a Pokémon save editor and cross-generation Bank for Android, tuned for
dual-screen handhelds like the AYN Thor. Box wallpapers, pixel sprites, gamepad controls,
and a second screen that shows a live summary of whatever is under the cursor — all
rendered in the visual language of the DS-era games.

Built on [PKHeX.Core](https://github.com/kwsch/PKHeX) with the
[Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins) compiled in-process, so
legalizing a Pokémon works **fully offline, on device**.

> Not affiliated with Nintendo, Game Freak, or The Pokémon Company.

---

## ✨ Features

### 🎮 A home that feels like a game
Your linked saves appear as cartridge shelves — filter by release order, alphabetically,
by console, or by generation. Gamepad-first: the cursor and selection are distinct, and
every action has a button.

![Home](docs/screenshots/home.png)

### ✏️ Edit any Pokémon
Stats, IVs, EVs, moves, Met/Origin, Tera type, Hyper Training, shininess, and more —
with a live legality check as you go.

![Editor](docs/screenshots/editor.png)

### 🏦 A cross-game Bank
Unlimited themed boxes across every generation. Grab, move, deposit, and organize with
the gamepad.

![Bank](docs/screenshots/bank.png)

### 🧬 Living Dex tracker
Track your collection across all 1025 Pokémon, generation by generation, including
shiny variants.

![Living Dex](docs/screenshots/living-dex.png)

### 🎁 Mystery Gift database
The full event database, fully offline — browse and inject Wonder Cards into your save.

![Mystery Gift](docs/screenshots/mystery-gift.png)

### 🎒 Bag editor
Edit any pouch, any item, any quantity.

![Bag](docs/screenshots/bag.png)

### 🧪 RNG tools
Inspect PID, IVs, and nature — and reroll a nature while keeping shininess.

![RNG](docs/screenshots/rng.png)

### 🏞️ Poképark
A living habitat where your Pokémon wander while you're away.

![Poképark](docs/screenshots/pokepark.png)

### And more

| | |
|---|---|
| **Trainer card** — name, IDs, money, gender | **Showdown QR** — export a set as a QR code |
| ![Trainer card](docs/screenshots/trainer-card.png) | ![Showdown QR](docs/screenshots/showdown-qr.png) |
| **Nuzlocke report** — first encounters and dupes per route | **Encounter browser** — every way to catch a species |
| ![Nuzlocke](docs/screenshots/nuzlocke.png) | ![Encounters](docs/screenshots/encounters.png) |

- **One-tap legalize** — Auto Legality Mod, offline
- **Showdown import/export** and **QR codes**
- **Restore points** and safe, atomic save writes

---

## 🛡️ Save safety

Every write follows **validate → backup → atomic write**. No exceptions, even for bulk
operations. An invalid candidate means no backup and no write. Your saves are never at
risk.

---

## 📥 Install

Download the APK from [Releases](https://github.com/sofianeelhor/PKForge/releases) and
allow installs from unknown sources. First run walks you through linking an emulator
(RetroArch, melonDS, Azahar, Eden) or opening a single save file.

---

## 💬 Discord

Join for updates, support, bug reports, feature requests, or just to chat about the project.

👉 **[Join the PKForge Discord](https://discord.gg/bMtzZmTDfu)**

---

## 🛠️ Build

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

---

## 🙏 Credits

- [PKHeX](https://github.com/kwsch/PKHeX), the engine everything runs on
- [PKSM](https://github.com/FlagBrew/PKSM), the pixel chrome this UI builds on (GPL-3,
  see src/PKForge.App/Resources/UI/ATTRIBUTION.md)
- Sprites and Pokémon names © Nintendo, Creatures Inc., GAME FREAK inc.

## 📄 License

GPLv3 or later, inherited from PKHeX.Core. See [LICENSE](LICENSE).