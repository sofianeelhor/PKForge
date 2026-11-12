# Expansion data pack

`expansion.json` is the ROM-truth table pack for standard pokeemerald-expansion saves
(and the downstream 14-box hacks that keep its codec: Heart and Soul 2.0, Lazarus,
Emerald Seaglass, Emerald Enhanced). It is embedded as `expansion.expansion.json` by
`PKForge.Engine.csproj`.

## Source pin

Generated from a local snapshot of **rh-hideout/pokeemerald-expansion**:

- repository: <https://github.com/rh-hideout/pokeemerald-expansion>
- snapshot: `master`, taken 2026-11-11
- `include/constants/expansion.h`: `EXPANSION_VERSION 1.17.3-dev`, "Last version: 1.17.2"
  (`EXPANSION_TAGGED_RELEASE FALSE`)
- stable reference for the same data: tag `expansion/1.17.2`
  (`79dd436920845ebc145577649de624f65298c209`). The snapshot had no `.git` directory, so the
  master commit hash could not be read; the version bytes above are the pin.

The generator missed entries whose enum value is another constant (`MOVE_SKETCH =
MOVES_COUNT_GEN1`, `ABILITY_TANGLED_FEET = ABILITIES_COUNT_GEN3`), the TM/HM items
(their data entries are keyed by move aliases such as `[ITEM_TM_FOCUS_PUNCH]`), and
moves whose PP is a `B_UPDATED_MOVE_DATA` conditional. Those 10 moves, 61 items and 6
abilities were filled by hand from tag `expansion/1.17.2`
(`include/constants/{moves,items,abilities}.h`, `src/data/{moves_info,items,abilities}.h`);
every other name in the pack matches that tag exactly. Conditional PP takes the latest
generation's value, as the rest of the pack does.

## What is parsed

- `include/constants/species.h` — the packed `enum Species` (raw save id → constant).
- `include/constants/pokedex.h` — the `NationalDexOrder` enum
  (`NATIONAL_DEX_*` → national id).
- `src/data/pokemon/species_info/gen_1_families.h` … `gen_9_families.h` and
  `species_info.h`:
  - `[SPECIES_X] = { ... }` designated initializers,
  - `[SPECIES_X] = MACRO(...)` / `[SPECIES_X] = MACRO` macro-generated entries,
  - simple `#define` bodies are expanded recursively and arguments substituted, so
    form families (Floette, Vivillon, Arceus, Unown, …) keep their `.natDexNum`,
    stats, types, abilities, gender ratio and growth rate.
  - `.natDexNum`, `.baseHP`, `.baseAttack`, `.baseDefense`, `.baseSpeed`,
    `.baseSpAttack`, `.baseSpDefense`, `.types`, `.abilities`, `.genderRatio`,
    `.growthRate`.
  - Forms with no literal `.natDexNum` are resolved by stripping form suffixes until a
    base species constant matches (e.g. `SPECIES_FLOETTE_ETERNAL` → `SPECIES_FLOETTE`),
    then inherit the base entry's parsed fields. Unknown/custom ids keep national 0 and
    the engine falls back to `Species N`.
- `src/data/moves_info.h` — `.name`, `.pp` (moves from `include/constants/moves.h`).
- `src/data/items.h` — `.name` (items from `include/constants/items.h`).
- `src/data/abilities.h` — `.name` (abilities from `include/constants/abilities.h`).

Base stats are written in the app's stat order: **HP, Atk, Def, SpA, SpD, Spe**.
Types are the engine's standard 0-17 ids (Normal = 0 … Fairy = 17), mapped from the
expansion's `TYPE_*` enum. Species names are resolved at runtime from PKHeX's English
national table for national 1..1025; the JSON stores the raw → national mapping only.

## Schema

```json
{
  "source": { "repository": "...", "revision": "..." },
  "maxSpecies": 1572, "maxMove": 934, "maxItem": 873, "maxAbility": 319,
  "species": [
    { "id": 1, "nat": 1, "hp": 45, "atk": 49, "def": 49,
      "spa": 65, "spd": 65, "spe": 45,
      "types": [11, 3], "abilities": [65, 0, 34], "gender": 31, "growth": 3 }
  ],
  "moves":     [ { "id": 1, "name": "Pound", "pp": 35 } ],
  "items":     [ { "id": 1, "name": "Poké Ball" } ],
  "abilities": [ { "id": 1, "name": "Stench" } ]
}
```

`gender` is the Gen 3 ratio byte (`0` male-only, `254` female-only, `255` genderless,
otherwise the female threshold). `growth` is the `GrowthRate` enum
(`0` medium-fast, `1` erratic, `2` fluctuating, `3` medium-slow, `4` fast, `5` slow),
which is the same index PKHeX's `Experience` tables use.

## Regenerating

The parser needs the upstream source checkout, so no self-contained script is vendored
here. The generation steps are the list above: parse the four constant enums, expand
the species-info macros, emit one record per raw id 1..1572, then parse the three data
tables for names/PP. The checked-in pack is the result for the pinned snapshot.

## Views for hacks on an unknown or older release

`ExpansionData.SharedSince18` (Team Rocket Edition, Fire Emerald) limits the pack to the ids
that every rh-hideout tag from `expansion/1.8.0` (`107bcdf6231beb59f29e2d964c0602d844e4c259`)
to `expansion/1.17.2` (`79dd436920845ebc145577649de624f65298c209`) gives the same constant:
species below `NUM_SPECIES` 1524, moves below `MOVES_COUNT` 848 (no Z or Max moves), items
below `ITEMS_COUNT` 828 and abilities below `ABILITIES_COUNT` 311. This was checked by
evaluating `include/constants/{species,moves,items,abilities}.h` at 1.8.0, 1.9.0, 1.10.0 …
1.17.2: in that range no id changes its constant (only aliases were renamed), so the pack's
names hold, and a picker never offers an id that the hack's own build may lack.

## `blackpearl-items.json`

This is Black Pearl Emerald's own item table, used for saves from before 2.1. The hack inserts
TM101-TM219 at 682-800, so every item after the TMs takes a different id from the expansion's.

- repository: <https://github.com/blackpearlemerald/BlackPearlEmerald>
- tag `bpe/v1.0.1`, commit `ed784bef37b5d37431867c57fb03c958913398a0` (expansion 1.8.6). It is
  generated from `include/constants/items.h`, `include/constants/tms_hms.h` (the
  `ITEM_TM_<MOVE>` aliases) and the `.name` fields of `src/data/items.h`. It covers ids 1-974.
  `{POKEBLOCK} Case` is written out as `Pokéblock Case`.
- Ids 1-974 name the same items at tag `bpe/v2.0.7-beta`
  (`f28a04f6a6481fb35da008eadb50e40fed1d6e6d`, expansion 1.16.3). Only 132 spellings differ,
  because 1.0.1 abbreviates names ("PewtrCrnches" for "Pewter Crunchies"). The table keeps the
  1.0.1 spellings, which the much longer-lived 1.0.x ROM shows. Ids 975+ exist only in 2.0.x
  (new Mega Stones) and are not offered.
- Species, moves and abilities in both tags match the expansion's (the diff of the constant
  headers is empty apart from aliases). The view is `ExpansionData.BlackPearlLegacy`: the
  shared expansion ids above, plus these items.

## Source

These tables are names and numbers extracted from the hack's public source so PKForge
can read its saves. The pin above reproduces the pack exactly. Credit goes to the upstream authors
of the upstream project.
