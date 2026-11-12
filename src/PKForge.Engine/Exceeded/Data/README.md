# Emerald Exceeded: format evidence and tables

Emerald Exceeded has no public source. A search of PokéCommunity, GitHub, Project Pokémon and
the PKHeX plugins found no repository, save editor or format document. The format below was
reverse-engineered from the two local samples:

- `romhacks/batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav`: OT "Mia", 6 party
  and 26 boxed Pokémon. This is the newer build.
- `romhacks/batch-2026-11-12/saves/saves/Pokemon - Exceeded.sav`: OT "Leaf", 6 party and
  197 boxed Pokémon. This is an older build.

The engine has no table of its own. It reads species, moves, items and growth rates through
the shared expansion pack (`../../Expansion/Data/expansion.json`, rh-hideout
pokeemerald-expansion `1.17.3-dev` snapshot, stable tag `expansion/1.17.2` `79dd4369`).

## Envelope (verified on both saves)

- Retail Emerald layout: two slots of 14 sectors, footer `0x08012025`, ids 0-13.
- Every sector holds **0xFF4** bytes of data. Every live and backup sector checksums over
  0xFF4, and ids 1 and 3 fail the retail 0xF80 window.
- SaveBlock1 contains the party count (`+0x234`) and the party (`+0x238`, 6 × 108 bytes).
- PC storage is ids 5-13 reassembled in 0xFF4 chunks:
  - a `u32` current box;
  - 13 boxes × 30 × **88-byte** records;
  - box names `Box1`…`Box13` at `+0x8614` (9 bytes each);
  - 13 wallpaper bytes, for a total of `0x8696` bytes.
- SaveBlock2:
  - the name, gender (`+8`), TID/SID (`+0xA`/`+0xC`) and play time (`+0xE`) are at the
    retail offsets;
  - the money/coin key is at `+0xA4`, with a copy at `+0x21C`;
  - the money is at SaveBlock1 `+0x4C0` and the coins at `+0x4C4`;
  - the bag pockets sit at `+0x4C8` onwards, as item `u16` + count `u16` XOR the low half of
    the key.

## Pokémon record (88 bytes boxed, 108 in the party)

Records are plaintext, with no encryption, no substruct shuffle and **no checksum**. A sweep
over all 235 records for any 16-bit sum or XOR that matches a field found only coincidences
(at most 13/235).

| Offset | Field | Confidence | Evidence |
|---|---|---|---|
| 0x00 | PID `u32` | high | random per record; never zero |
| 0x04 | OT id `u32` | high | equals SaveBlock2 TID/SID for the player's own catches |
| 0x08 | nickname, 10 bytes, Gen 3 charset | high | names read as species names, `0xFF`-terminated |
| 0x12 | language | high | 2 (English) on 235/235 |
| 0x13 | retail flags (`hasSpecies`) | high | 2 on 235/235 |
| 0x14 | OT name, 7 bytes | high | "Mia" / "Leaf" |
| 0x1B | markings | medium | 0 on 235/235 |
| 0x1C | species `u16`, expansion raw id | high (≤905 both builds; all ids in the newer build) | nickname = species name on 175/175 shared-range records; newer build 32/32 including forms (976 Galarian Rapidash, 1023 Pichu, 1104 Kyurem, 1312 Dachsbun, 1325 Ceruledge, 1387 Iron Thorns) |
| 0x1E | held item `u16`, expansion ids | medium | Power Belt on Pichu. Bag pockets hold coherent expansion-ordered runs: balls 1-26, treasures 136-204, plates/drives/memories/mega stones 251-332 |
| 0x20 | `u32`, unknown | – | 0 on 226/235; never written |
| 0x24 | EXP `u32` | high | the level it gives through the pack's growth rate equals the stored party level on 12/12 party Pokémon |
| 0x28 | PP bonuses | medium | 0 on 235/235 |
| 0x29 | friendship | medium | 70 (base) on most fresh catches, 0x46/0x64/0xBA… |
| 0x2A | `u16`, unknown | – | 0x341-0x353; never written |
| 0x2C | moves `u16` ×4, expansion ids | high (< 851) | current PP ≤ the pack's max PP on 117/118 slots in the newer build. The exception is Wish (10 vs the pack's Gen 9 value of 5) |
| 0x34 | PP ×4 | high | see moves |
| 0x38 | 12 bytes, all zero | – | zero on 235/235 (EV/contest position in retail); never written |
| 0x44 | pokérus, met location, origins `u16`, IV word, ribbons | low | retail misc order, but the IV word does not reproduce the stored stats; never written |
| 0x4E | five `u16`, move-like ids | – | unknown (relearn or egg moves?); never written |
| party 0x58 | status `u32`, level, mail, HP, max HP, Atk, Def, Spe, SpA, SpD | high | level = EXP level on 12/12; HP ≤ max HP |

The party stats are roughly 2-5× the retail Gen 3 formula. For example, a level 28 Kyurem
has 275 HP, where retail gives at most 134. The hack computes them with a formula of its own,
so nothing that would need new party stats is offered: a party Pokémon's level, or a boxed
Pokémon joining the party. When a Pokémon is withdrawn in the game, the game recomputes its
stats.

## The two builds

The **older build** numbers its species past 905 differently. Its Gen 9 natives sit at 905 +
the expansion 1.7 `GEN9_START` offsets: 907 Floragato, 921 Lokix, 957 Scovillain, 1001 Iron
Hands. Its forms follow, regional forms first: 1051 Alolan Sandshrew, 1067 Galarian Ponyta.
No rh-hideout tag uses this numbering (0.9.0-1.10.0 were checked). The build also stores
move ids above 934 (1000-1069) and Z-move ids with ordinary PP. The engine therefore:

- trusts species ids ≤ 905 in every save;
- trusts ids above 905 only when no record contradicts the pack, meaning no nickname names a
  different species than the pack's;
- otherwise shows those species as "Species N", with no level edit;
- offers and accepts moves 1-850 only (below the expansion's Z-moves), the range where both
  builds agree.

## What would unblock more

- The ROM of each build would allow the following:
  - the older build's species table (`gSpeciesInfo` or the species-name table);
  - its move table above 850;
  - the hack's stat routine (`CalculateMonStats`), which would allow party level edits
    and box-to-party moves;
  - the meaning of the IV word, for IV, nature and ability editing.
- A save holding an egg would prove the egg flag.
