# Elite Redux data pack

`eliteredux.json` holds the Elite Redux tables that the engine reads. `PKForge.Engine.csproj` embeds it as
`eliteredux.eliteredux.json`.

## Source pin

- Game source: <https://github.com/Elite-Redux/eliteredux-source>, branch `upcoming`, commit
  `aac91aa497e02bc8fb6c17cc88b161ef092e8a73` (2026-11-11). `CURRENT_GAME_VERSION` is 1049, and the
  engine is pinned to that save version.
- Data: <https://github.com/Elite-Redux/er-config>, the `proto` submodule of the source. Commit
  `ab68e94c9bf1f388b65050a71741e6c38d546599`, the submodule's pinned revision.

The game generates its C tables from the textprotos in er-config (`tools/codegen`). This pack is built
from the same files:

- `SpeciesEnum.proto`, `MoveEnum.proto`, `ItemEnum.proto` and `AbilityEnum.proto` give the ids that are
  stored in the save.
- `SpeciesList.textproto` gives the dex name and national dex number. A form follows `form_of` up to
  its base species, and its form label comes from the id suffix (`SPECIES_GOLEM_ALOLAN` becomes
  "Alola"). It also gives the base stats in the app's order (HP, Atk, Def, SpA, SpD, Spe), the types
  (mapped to 0-17), the three ability slots and the innates. Ability slots are padded with the last
  ability, as `BaseStatsGenerator.kt` pads them. The gender ratio uses `PERCENT_FEMALE`. A species
  whose generator entry is empty keeps zeros, as in `gBaseStats`. National numbers above 1025 are the
  hack's own Pokémon and are stored as `nat` 0.
- `MoveList.textproto` gives the move name and PP.
- `items/*.textproto` gives the item name and pocket (`grouping`). It also records whether the bag
  lists the item: the bag lists an item only when it has both an image and a palette.
- `AbilityList.textproto` gives the ability name.

The game has no growth-rate table: every species uses the "slow" curve (`5n³/4`) from
`src/data/pokemon/experience_tables.h`. The engine hard-codes it instead of storing it here.

## Regenerating

```
git clone https://github.com/Elite-Redux/er-config proto && git -C proto checkout <rev>
python3 -I tools/generate.py proto eliteredux.json <eliteredux-source commit>
```

## Verified on bytes

The batch sample `Pokemon - Elite Redux.sav` (save version 1049) checks out against this pack in
three ways:

- Every party and box species id matches the Pokémon's nickname.
- Five of the six party members' stored stats are reproduced exactly by the game's formula with these
  base stats. The sixth, Lucineon (one of the hack's own species), does not match. Its base stats
  changed after the build that wrote the save. The engine catches this mismatch and will not
  recompute that Pokémon's stats.
- The bag's ownership bits read as plausible pockets (27 balls, 108 TMs/HMs, mega stones).
