# Pokémon Radical Red data tables

Species and item tables for Pokémon Radical Red v4.1 (CFRU / FireRed hack), extracted
by the eliyahu1702/Rad-Red-4.1-Team-Exporter project (Species.txt @3db5de63e239,
Items.txt @ce70b61cec8e) and cross-checked against the owner's device save
(`Radical-red-champ.sav`, build signature 0x08012025): every pinned species id in the
save's party and PC matches this table, including Terapagos = 1370.

Format: `id<TAB>name` per line (NOT `id:` like the Unbound tables); `#` lines are
provenance comments. Ids are Radical Red's own internal ids — species <= 411 follow
the Hoenn-internal order of the Gen 3 games (so Bulbasaur is NOT id 1), beyond that
the CFRU/DPE expansion continues to id 1375. Duplicate names exist for form slots
(Burmy, Unown, Oricorio, ...): lookups are id-keyed, and name resolution keeps the
lowest id of a duplicate.
