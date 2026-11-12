# Fire Red Extended and Crystal Advance Redux: where the tables come from

No table file ships here: neither hack publishes its data, and every id PKForge names is
either FireRed's own (PKHeX's Gen 3 tables) or proven by a rule the real saves confirm.
Searched (2026-11-12) and found nothing usable: the PokéCommunity threads for Fire Red
Extended (466535) and MrDollSteak's Decap and Attack Rombase (326301, blocked to tools),
pokeromwiki, PokéHarbor, the hacks' Discord-only documentation. No source repository exists
for either hack.

## Fire Red Extended 3.5.3 (`loose/Pokemon Fire Red Extended 3.5.3.sav`)

| Data | Rule | Evidence on the save |
|---|---|---|
| Species 1-251, 277-411 | FireRed internal ids | 30 boxed Pokémon (legendaries, starters) with their default nickname; party Mew (151) |
| Species 440-958 | national + 53 | 87 boxed Pokémon of generations 4-8 whose default nickname is the species name (Turtwig 440, Arceus 546, Genesect 702, Xerneas 769, Melmetal 862, Enamorus 958). The ROM spells Blacephalon "Blacphalon" |
| Species 412-439, 959+ | unnamed ("Species #N") | 959+ are forms and new Megas (1140-1142 nicknamed Deoxys, 1152-1153 Zygarde, 1162 Urshifu, 1181-1183 Galarian birds, 1020-1093 nicknamed Weavile, Dragonite, Torterra, Throh…) whose exact form is not in the save |
| Moves 1-354 | FireRed's | level-10 starters' moves: Ember 52, Water Gun 55, Bubble 145, Lick 122, Howl 336, Odor Sleuth 316, Taunt 269, Smokescreen 108, Vine Whip 22… |
| Moves 355+ | unnamed | 49 distinct ids seen. Neither the national order nor the CFRU (Unbound/Radical Red) order fits all of them: CFRU fits Reshiram/Zekrom's Fire/Thunder Fang 431/433, Victini's Incinerate 474, Heatran's Magma Storm 494, Popplio's Disarming Voice 467, but not Rowlet's 487, Totodile/Mudkip's 498 or the Regis' 393 |
| Items | FireRed's, minus reused ids | FireRed's unnamed "???" ids are reused (item 91 ×999 in the bag); Lum Berry 141, Star Piece 109, Stardust 108 held as expected |
| Growth | FireRed's for 1-411 | every retail-id boxed Pokémon holds the exact level-10 EXP of its retail rate. Past 411 the hack changed rates (Arceus 540 EXP, Type: Null 1000), so those levels are shown when the EXP is a level threshold of exactly one rate and never written |
| Base stats, types, abilities | not assumed | a party Pokémon's stats are recomputed only when its stored stats already equal FireRed's formula (the party Mew does) |

## Crystal Advance Redux (`romhack/romhack saves/Pokemon - Crystal Advance - Redux.sav`)

A FireRed (US 1.0) binary hack without a species or move expansion. The party Chikorita (152,
level 5, Oran Berry 139, Tackle 33 / Growl 45 / Vine Whip 22) has exactly the stats FireRed's
base stats give for its IVs, EVs and nature, and its EXP fits Medium Slow. The rest of the
tables are FireRed's, except the mail block 121-132: item 121 (Orange Mail) sits in the Key
Items pocket, so the hack reuses it and those ids are never offered. The save has no boxed
Pokémon: boxes take the party record's first 80 bytes, the form the game itself deposits.

## What the ROM would unblock

With the hack's ROM (any build of the same version), these tables could be read directly:
Fire Red Extended's move names past 354, its species names past 958, every species' base
stats, types, abilities, growth rate and gender ratio, and both hacks' item names. That would
make species changes, ability editing, withdrawing a boxed Pokémon into the party (Fire Red
Extended) and the bag editable.
