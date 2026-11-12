# SoulGold data pack

ROM-truth tables for Pokémon Soul Gold (rahtak; Eemeliri/soulgold, a
pokeemerald-expansion 1.15.2 port of Heart & Soul). Embedded as
`soulgold.<name>.json` by `PKForge.Engine.csproj`.

## Source pin

Generated from a local clone of **Eemeliri/soulgold** at release tag `1.2.1`
(commit `e73803309e78231142160b3e1029df884bc8b5f1`, published 2026-10-25):

- `include/constants/species.h` — raw species ids (`SPECIES_PAWMI 1305`, …)
- `src/data/pokemon/species_info/gen_*_families.h` — `.speciesName`,
  `.natDexNum` (resolved through the `NATIONAL_DEX_*` enum in
  `include/constants/pokedex.h`), base stats, `.types`, `.genderRatio`
  (`PERCENT_FEMALE(x)` → `x * 255 / 100`, truncated like the C macro),
  `.growthRate`, `.abilities`
- `include/constants/moves.h` + `src/data/moves_info.h` — move ids, names, PP
- `include/constants/items.h` + `src/data/items.h` — item ids, names, pockets
- `include/constants/abilities.h` + `src/data/abilities.h` — ability ids, names

The expansion version pin matters: ids above the national range shift between
expansion releases (this pack's max species id is 1577). The tables are the
1.15.2 generation only; the save layout is described in `SoulGoldFormat.cs`.

Species ids 1..1025 coincide with the national dex; ids above it (Pawmi 1305 →
national 921, …) resolve through `natDexNum`.

Move ids 1..621 are the national ones. Past Hyperspace Fury the expansion leaves
out the Z-moves (Shore Up is 622 here, 659 in PKHeX; Brutal Swing 656 here, 693
in PKHeX), so the engine translates moves by name past 621
(`SoulGoldData.MoveToNational` / `MoveFromNational`). Shadow Blast (848,
`MOVE_DARK_AERO`) is the hack's own and has no PKHeX id. Ability ids up to 310
are the national ones; past it are the hack's own (Fightilate …).

## Generation rules (2026-11 regeneration)

The first generation of this pack mis-evaluated the headers. The pack was
regenerated from the same pin with these rules, and checked against PKHeX: every
move 1..847 maps to the PKHeX move of the same name, and every base species'
base stats and types equal PKHeX's (SV, else USUM), except Aegislash (140/140).

- Enum members are evaluated in order, including the `X = MOVES_COUNT_GENn` /
  `ABILITIES_COUNT_GENn` openers of each generation (Sketch, Fake Out, Roost,
  Hone Claws, Flying Press, Shore Up, Dynamax Cannon, Tera Blast; Tangled Feet,
  Pickpocket, Aroma Veil, Stamina, Intrepid Sword, Lingering Aroma were missing)
  and the implicit members after `FIRST_MAX_MOVE` (the Max moves had overwritten
  ids 0..51). Moves are kept below `MOVES_COUNT` (no Z or Max moves).
- TM/HM items come from `FOREACH_TM` / `FOREACH_HM` (`ITEM_TM_<MOVE>` is
  `ITEM_TM<nn>`); TM02-TM100, HM02-HM10, Orange Mail, Cheri Berry, the e-Reader
  Enigma Berry and the HGSS key items 876-885 were missing.
- Conditional values (`P_UPDATED_STATS >= GEN_2 ? 60 : 50`, `#if … #else`) take
  the latest-generation branch, the hack's configuration (`GEN_LATEST`); they had
  been read as 0.
- Species defined through a macro (`UNOWN_MISC_INFO`, `ARCEUS_SPECIES_INFO`,
  `GENESECT_SPECIES_INFO`, …) are expanded; 245 such ids were missing.
- `types` are PKHeX's 0-17 order (Fire 9); they had been the expansion's id - 1,
  which is wrong past Steel (the expansion has Mystery at 10).

## Files

- `species.json` — id → `{name, natDex, base[HP,Atk,Def,SpA,SpD,Spe], growth,
  types[], gender, abilities[a1,a2,hidden]}`
- `moves.json` — id → `{name, pp}`
- `items.json` — id → `{name, pocket}`
- `abilities.json` — id → `{name}`

## Source

These tables are names and numbers extracted from the hack's public source so PKForge
can read its saves. The pin above reproduces the pack exactly. Credit goes to the author
of the upstream project.
