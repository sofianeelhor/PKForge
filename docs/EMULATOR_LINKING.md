# Emulator linking

PKForge links Android Storage Access Framework folders, not running emulator
processes. Save in game and stop emulation before editing. Start the game normally
afterwards: an old save state can overwrite edited battery-save data.

- **Dolphin:** links individual Colosseum/XD `.gci` saves. Grant the GC root,
  a region folder, Card A / Card B, or a custom GCI folder. Default paths are
  `GC/<region>/Card A` and `GC/<region>/Card B`; recursive discovery also handles
  extra region nesting. Raw `.raw` / `.gcp` memory cards are deliberately not linked.
  Export a game as GCI using Dolphin's desktop Memory Card Manager, or configure
  the card slot to use GCI Folder. An exported GCI needs importing back when
  continuing to use a raw card.
- **DraStic:** grant `backup/` containing `.dsv` battery saves or the emulator
  data root. Emulator save states are excluded.
- **Pizza Boy A / C:** A is GBA, C is GB/GBC. Grant a folder containing battery
  saves (`.sav`), including a configured save folder or exported saves. If only
  an export is accessible, import the edited file back in Pizza Boy. Do not
  promise a fixed storage path across versions or Android storage policies.
- **RetroArch:** per-core save folders are traversed, including nested GCI folders.
  **melonDS, Linkboy, Azahar and Eden** remain available in their platform menus.

Android decides which folders providers expose and whether it grants access.
Selecting a platform groups the choices; a linked root is still scanned for all
supported Pokémon saves, so one RetroArch root need not be linked per platform.
Old persisted enum values (0–4) remain stable. Parse caches include the emulator
kind so overlapping grants do not inherit another emulator's identity.

## Evidence

Reviewed September 2026:

- [Dolphin MainSettings.cpp](https://github.com/dolphin-emu/dolphin/blob/master/Source/Core/Core/Config/MainSettings.cpp): `GetGCIFolderPath` and default region/Card paths.
- [Dolphin Android strings](https://github.com/dolphin-emu/dolphin/blob/master/Source/Android/app/src/main/res/values/strings.xml): GCI Folder device and scoped-storage limitations.
- [Dolphin GCI-folder implementation history](https://dolphin-emu.org/download/list/memcard_directory/1/): GCI-folder virtual cards and state interactions.
- [DraStic support forum](https://drastic-ds.com/viewtopic.php?t=5295): developer support confirms `DraStic/backup` and ROM-named `.dsv` battery saves.
- [Pizza Emulators](https://pizzaemulators.com/): current A/C product platform mapping. No authoritative fixed save-path documentation was found; setup instructions use the user's chosen folder/export.

The added menu art uses attributed CC-BY illustrations, not official emulator logos.
On-device SAF discovery and emulator reloads still require device verification.

## Save recognition fixes

RetroArch can compress battery saves using its `#RZIPv1#` envelope. The supplied
Sapphire `(Fix).srm` is a 13,556-byte rzip file containing an ordinary 131,072-byte
Gen 3 save. The compression prevented recognition before any patch-specific data
was examined. PKForge now bounds decompression, validates chunk lengths and
Adler-32 trailers, and recompresses edits with the original chunk size. An unchanged
payload preserves the exact original compressed bytes. Backups retain the original
container. Format reference: [RetroArch rzip implementation](https://github.com/libretro/libretro-common/blob/master/streams/rzip_stream.c).

Ruby and Sapphire share an ambiguous RS save layout. When the parsed save is RS,
standalone Ruby/Sapphire filename tokens resolve the displayed name and engine
version context. Conflicting/missing tokens remain “Ruby / Sapphire”. Filename hints
never override a different parsed game. Colosseum/XD are named by save type because
their enum values are outside the handheld game-name table.

Opening an invalid file now displays an error popup, and a failed open keeps the
previous live session usable. Whole GameCube cards receive GCI-export guidance.

## Personal item presets

In the bag editor, choose **ITEM PRESETS → Save current bag as preset**. The
**My item presets** menu applies, updates from the current bag, renames, and deletes
presets. Entries persist in app-private `item-presets.json` with atomic replacement
and generated JSON metadata for Android trimming. Compatibility requires the same
generation, matching item id/name, and a legal destination pouch. Applying sets
saved quantities and keeps other items; the engine enforces game quantity limits.
A capacity error rolls back already-applied preset entries in memory.

## Verification (2026-09-06)

- 269 engine tests and 76 domain tests passed.
- The supplied Sapphire sample was enabled with `PKFORGE_SAPPHIRE_FIX_SAVE`:
  exact unchanged round trip, edit/reopen, correct Sapphire/Ruby filename labels,
  and original source file unchanged.
- Generated Colosseum/XD GCI fixtures cover all six region/game header combinations,
  trainer edits, preserved 64-byte headers, and stable serialized output.
- DS raw and DeSmuME-footer containers preserve their format on edit/reopen.
- Preset persistence and generation/name/pouch compatibility have regression tests.
- Requested Android Debug build succeeded with zero warnings/errors.
- ARM64 Release APK published locally and verified against the permanent PKForge signing certificate.

These are host-side checks. Real Android folder grants, touch/controller layouts,
and emulator reloads still need the owner's device testing; the generated GCI
fixtures do not replace testing existing in-game Colosseum/XD saves.

## Follow-up: XD art and native duplication

XD now bundles a SteamGridDB icon, hero image, and English logo under the exact
`xd--gale-of-darkness` asset slug. Art cache version is v6; credits are in
`Resources/UI/ATTRIBUTION.md`.

The Sinking Sapphire duplication report led to a reproducible Gen 6 format ambiguity:
`ExportSlot` produces PK6 bytes, but `EntityFormat.GetFromBytes` without a context
can choose PK7 for the shared layout. The conversion back to PK6 is then refused.
Single and bulk Duplicate now call `ISaveEngineSession.DuplicateSlot`, which clones
the already-typed native entity directly. It never invokes legality or format
conversion, keeps the source intact, refuses occupied box destinations, and enforces
the six-member party cap. Unbound uses native same-layout copies too. The party UI
refresh includes newly appended slots, and single-duplicate failures show a popup
with the actual reason instead of always reporting a full party.

Verification: 281 engine tests passed, including native XY/ORAS box and party
reproduction, byte preservation, save/reopen, occupied/full destinations, clone
independence across generations, and the existing Unbound ground-truth save.
The reporter's Sinking Sapphire file was not supplied; its device retest remains
necessary. The Android Debug build completed with zero warnings/errors.
