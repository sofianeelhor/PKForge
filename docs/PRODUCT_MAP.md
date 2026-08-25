# PKForge product map

The information architecture for everything PKHeX.Core can do, mapped to surfaces.
Every surface exists in the app today — working or as a visible, honest placeholder —
so the structure never has to be rethought, only filled in. **Status current as of v0.23.0.**

## Surfaces

### Home
| Item | Status |
|---|---|
| Detected games as cartridge cards (auto-scan of linked emulators, SteamGridDB art) | DONE |
| First-run wizard (link emulators, offer sprite-pack download) | DONE |
| Open single save file | DONE |
| Settings (link/rescan, sprite-pack download, scan report, restore points, about) | DONE |
| **POKéMON BANK** tile → cross-game vault | DONE |
| **EVENTS** tile → Collection Center (community boxes) + wonder-cards pointer | DONE |

### Storage (box screen)
| Interaction | Action | Status |
|---|---|---|
| Tap/select occupied slot | select + editor panel | DONE |
| Empty slot menu | Generate (wizard) · Paste Showdown · Import .pk · Wonder cards | DONE |
| L/R or ◀ ▶ | box paging | DONE |
| TOOLS | Organizer (bulk move/export/release/→Bank) · .pk import · Showdown-team import · box export · Living Dex · Batch editor | DONE (batch editor = placeholder) |
| SAVE DATA | Trainer card · Bag/items · Pokédex complete · Wonder cards · Restore points | DONE |
| Second screen | mon summary + **stat radar**, dex preview, game hero art | DONE |

### Editor panel (selected Pokémon) — in-save AND bank (BankEntryEditor)
| Item | Status |
|---|---|
| Common fields (species, nick, level, nature, ability, item, moves, IVs/EVs, ball, OT, shiny, gender, friendship) | DONE (name-based pickers, sprites) |
| Quick actions (Max IV / 0 EV / Lv100) | DONE |
| **Met / Origin** (location, level, date, egg, origin game, language, fateful, TID/SID) | DONE (v0.22.0) |
| LEGALIZE · SHOWDOWN · EXPORT .pk · QR | DONE |
| Legality verdict + plain-language report | DONE |
| Tera · Hyper Training · ability slot | DONE (v0.23.0, PotentialEditor) |
| Relearn moves | **NEXT (Tier 1)** |
| Ribbons/marks · form · size/scale · memories · Dynamax · contest stats | Tier 2 (see HANDOFF §6) |

### Bank (dual-screen vault)
| Item | Status |
|---|---|
| Themed animated boxes, carry/place, unlimited boxes | DONE |
| Edit / Send-to-game / Move / Export / Release | DONE |
| Create / Paste-Showdown / Import into empty slot | DONE |
| Restore points / search / archive export | TODO |

`EncounterMovesetGenerator` ("how do I get this?" cards) · `EntityBatchEditor` (batch editor) ·
`BoxManipulator` (sort/clear/heal) · ribbon/mark/form/memory applicators · LiveHeX (Injection lib).
Full roadmap with API names in **docs/HANDOFF.md §6**.

## Rule
New features land inside this map — extend a menu or fill a placeholder; never bolt on a new screen
without updating this document and HANDOFF.md.
