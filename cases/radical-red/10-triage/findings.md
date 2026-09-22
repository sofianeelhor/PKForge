# Radical Red save triage — structural dissection of `Radical-red-champ.sav`

Sample: `/Users/sof/Desktop/Radical-red-champ.sav`, 128 KiB (0x20000),
sha256 `c115d09c89b8c800c30a4f53e3d9741a743cc89effac85891836b914104128a`.
The original was never opened for writing; every script copies it to
`/tmp/rrtriage/save.sav` (sha-verified) and analyzes the copy. Re-run any
script with `python3 <script>` from this directory (they import `rrlib`).

**Verdict up front:** this is a **CFRU (Complete Fire-Red Upgrade) FireRed
save** — the same family as Unbound — but it is *not* vanilla FRLG and not a
clone of Unbound either. The sector envelope is retail, the Small/Large object
offsets are vanilla-FRLG, but mon storage is radically relocated: **party mons
carry a plaintext unencrypted core** and **the PC is a 58-byte compact mon
stream** over sections 5–7. Species/move IDs are CFRU expanded (gen 1–9).

---

## 1. Sector envelope

128 KiB = 32 × 0x1000 sectors = two 14-section save slots (0x00000–0x0DFFF,
0x0E000–0x1BFFF) + 4 extra sectors (0x1C000–0x1FFFF). Every main/extra sector
trailer (last 12 bytes, +0xFF4):

| offset | size | meaning | observed value |
|---|---|---|---|
| +0xFF4 | u16 | section id (0–13) | per map below (extra sectors: 0 or junk) |
| +0xFF6 | u16 | sector checksum | fold32 of payload (see §2) |
| +0xFF8 | u32 | **build signature** | **0x08012025** on all main + HoF sectors |
| +0xFFC | u32 | **save counter** | 0x126 (slot A) / 0x127 (slot B) |

The signature is *not* vanilla FRLG (`0x08012000`) and *not* Unbound's
(`0x01121999`, see `UnboundFormat.SectorSignature`) — it is a per-build
constant; `0x08012025` identifies this Radical Red build (a good future
`IsPokemonRadicalRed` discriminator together with the layout fingerprints
below). No `PC01` string exists anywhere in the file.

### Slot section maps (physical order → section id)

```
slot A (0x00000): [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]  counter 0x126 (294)
slot B (0x0E000): [13, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]  counter 0x127 (295)  <- LIVE
```

Both slots contain all 14 ids exactly once; slot B's physical placement is
rotated one step (retail rotation behavior). **Slot B is newer** (0x127 > 0x126)
— live offsets used throughout: id0=0x0F000, id1=0x10000, id2=0x11000,
id3=0x12000, id4=0x13000, id5=0x14000, id6=0x15000, id7=0x16000, id8–12=
0x17000–0x1B000, id13=0x0E000.

Extra sectors: 0x1C000 = **Hall of Fame** (data to +0x8E, checksum u16@+0xFF4 =
fold32(payload[0..0xF80]) = 0x0FA7 ✓, signature present); 0x1D000 zero payload
but signature present; 0x1E000 = CFRU dex/flag extension block (no mons, no
trailer); 0x1F000 zero, no trailer.

## 2. Checksum algorithm — verified by recomputation

Retail Gen3 algorithm, confirmed byte-exact on every sector:

```
chk = ( sum of LE u32 words over payload[0..L) + (sum >> 16) ) & 0xFFFF   stored @+0xFF6
```

u16-word summation does **not** match anywhere (decisive discriminator).
Hand recomputation (script `01_envelope.py`): section 1 @0x10000, 1021 words
over 0xFF4 → sum 0x40BC93A9CF → fold → 0x6662/0x3729 (per copy) = stored ✓.
Section 5 @0x14000: fold32(0xFF4) = 0x3F1B = stored ✓.

**Checksum window L per section id (empirically discovered; ranges because
trailing zero words are free):**

| id | content | valid window(s) | note |
|---|---|---|---|
| 0 | Small/trainer | 0xADC–**0xF24** | vanilla FRLG window; **stash 0xF24–0xF38 unverified** |
| 1 | Large0: party+money+bag | 0xFEC–**0xFF4** | full-sector window, data to 0xFE9 |
| 2 | Large1: misc | 0x2C8–0xFF4 | sparse (data to 0x2C5) |
| 3 | Large2: mail/misc | 0xF98–**0xFF4** | data to 0xF96 |
| 4 | Large3: daycare zone | 0xD94–**0xDAC** | **stash 0xDAC–0xFC9 unverified** |
| 5–7 | **PC stream** | 0xFF0–**0xFF4** | full payload used |
| 8–12 | present, **all-zero** | 0x4–0xFF4 (chk 0x0000) | empty future stream/unused |
| 13 | box names + stashes | 0x450–**0x52C** | **stash 0x530–0xD13 unverified** |

Interpretation: like Unbound (`UnboundFormat.ChecksumLength = 0xFF4`), the
CFRU checksums the sections it owns over the full payload; where it kept a
vanilla object (Small) it kept the vanilla window; and it parks extra data
*past* the verified window (sections 0, 4, 13) that the game does not check —
the same "unverified stash" pattern PKForge already handles for Unbound.

## 3. Section contents (live slot B)

### Section 0 — Small (trainer card), vanilla FRLG offsets
- OT name @+0x000: `sri` (G3 charset), gender 0, **TID 45930 / SID 36901**
- play time @+0x00E: 23h37m25s; security key @+0xF20 = 0x00000000 (money stored unxor'd)
- dex bitfields at the vanilla Small 0x28/0x5C offsets are **empty** (0 caught/seen
  there) — the CFRU keeps dex data in its own 0x1E000 block instead
- stash @0xF24–0xF38 (20 bytes): `40 00 10 40 01 00 f4 08 05 40 6d 96 d1 88 ef 93 07 06 2a c1` — unidentified CFRU config counters

### Sections 1–4 — Large (vanilla chunk model, 0xF80 each)
- **party count u32 @Large+0x34 = 6**; **party mons @Large+0x38, 100 bytes each,
  stride 100 — retail header offsets, plaintext core (§4)**
- money u32 @Large+0x290 (xor security key — key is 0) = 1,391,024
- bag @Large+0x298.. (expanded CFRU pockets; vanilla 0x360 window only partially meaningful)
- daycare @Large+0x2F80 (= section 4 payload +0x100): **empty** in this save
- section 2: sparse roamer/battle records to +0x2C5; section 3: mail strings
  ("ENIGMA", "Hello", "Trade", "Battle", "Sorry", "Thank") to +0xF96

### Sections 5–7 — the PC storage stream (the big relocation)
- u32 **current box @section5 payload +0x000 = 5**
- **58-byte compact mons from +0x004, stride 58, flowing *contiguously* across
  the three payloads with no per-section headers** (proven: mons #70 and #140
  straddle payload boundaries and decode cleanly; 151/151 mons validate)
- capacity 210 slots = exactly 7 boxes × 30
- occupancy: box0=27, box1=27, box2=23, box3=20, box4=22, box5=28, box6=4 (151 total)
- sections 8–12 exist with ids/signature but **zero payloads** — with 25 box
  names (below) these are most plausibly further stream chunks that this save
  simply hasn't filled (they are where boxes 8+ must eventually live)

### Section 13 — box metadata + stashes
- **box names @payload+0x361, 25 entries × 9 bytes, G3 charset**: `Box25…Box15,
  Box1…Box14` — table is stored in *reverse* order (index i ↔ box number
  25−i). Radical Red has **25 PC boxes**.
- wallpaper bytes were *not* located at the vanilla storage-mapped offset
  (section13+0x7C2 region is zero); the byte pairs following the names
  (`01 00 01 04 05 04 05 08 …`) are the leading candidate (open item)
- unverified stash 0x530–0xD13: small tables, eleven 42-byte records @0x9E8
  (each starts with an IV word 0x3FFFFFFF + pid + counts — unidentified CFRU
  battle/bonus data), and a ~300-entry (u16,u16) table

### Extra region
- **0x1C000 Hall of Fame**: CFRU record format, 0x18 bytes each:
  `otid u32 | pid u32 | species u16 | level u8 | nickname 10B | 3B pad`.
  6 entries = the champion team; **every pid/species/level matches the live
  party exactly** (independent proof of §4's plaintext model).
- 0x1E000: CFRU dex/flag extension (per-id counters + a ~1 KB bitfield), no mons.

## 4. Mon format — CFRU plaintext, not retail crypto

**Retail Gen3 encryption is NOT used.** For party mon 0 (Terapagos,
pid 0xD14F558E, otid 0x9025B36A):

- retail decrypt (`XOR u32 with PID^OTID` + `PID%24` unshuffle) yields
  "species" 59108 — garbage;
- the raw 0x20–0x50 region **is directly readable plaintext in fixed G,A,E,M
  order** (no shuffle): species=1370, item=711, exp=767,690 (=L85 ✓),
  ppUps=0, friendship=138, moves=(372,449,46,58) with PP=(10,10,5,10),
  IV word 0xBFFFFFFF → 6×31 IVs, hidden-ability bit set;
- the mon checksum field @0x1C and sanity @0x1E are **0x0000 (unused)** for
  every mon — the game no longer integrity-checks mon blobs;
- level/HP/stats @0x54–0x62 are live and sane (85, 308/308).

This is exactly PKForge's Unbound model (`UnboundMon`: "retail G3 layout with a
PLAINTEXT fixed-order B,A,D,C core"). Party form (100 B) field map — vanilla
header + plaintext core, with **ball at 0x2A** (CFRU extension, not the vanilla
origins-bitfield ball):

```
0x00 pid u32 | 0x04 otid u32 (TID lo, SID hi) | 0x08 nick 10B | 0x12 lang u8
0x13 flags u8 (bit1 hasSpecies) | 0x14 OT 7B | 0x1B markings | 0x1C chk u16=0 | 0x1E sanity=0
0x20 Growth: species u16, item u16, exp u32, ppUps u8, friendship u8, ball u8 @0x2A
0x2C Attack: moves 4×u16, PP 4×u8
0x38 EV/Cond: EVs 6×u8, contest 6×u8
0x44 Misc: pokerus, metLoc, origins u16, IV word u32 @0x48 (6×5b + egg b30 + hidden b31), ribbons u32
0x50 status u32 | 0x54 level u8 | 0x56 hp u16 | 0x58.. stats 6×u16
```

PC compact form (58 B) — identical to Unbound's:

```
0x00 pid | 0x04 otid | 0x08 nick 10B | 0x12 lang | 0x13 flags | 0x14 OT 7B
0x1C species u16 | 0x1E item u16 | 0x20 exp u32 | 0x26 ball u8
0x27 moves 4×10b packed u32(+0x2B byte) | 0x2C EVs 6×u8 | 0x36 IV word u32
(no friendship/PP/status/met data; level is derived from exp)
```

Species @0x1C was pinned by nickname reverse-matching: **117/117 mons whose
nickname equals a default species name carry exactly that species id at +0x1C**
(script `02d_compact_layout.py`). CFRU shiny rule: `((otid^pid) & 0xFFFF) ^
((otid^pid) >> 16) < 16` (not vanilla's `< 8`).

### Species table
Species ids < 411 are vanilla Hoenn-internal order (e.g. 393 = Kirlia =
national 306); ids > 411 are CFRU-expanded. RR's expansion table **largely
matches Unbound's** (`src/PKForge.Engine/Unbound/Data/pokemon.txt`):
1019 = Marshadow, 763 = Delphox, 1009 = Lunala agree; but RR diverges at some
ids (RR 1244 = "Sandy Shocks" where Unbound's 1244 is Zorua-Hisui; RR 1349 =
"Maschiff"; RR 1370 = Terapagos). Radical Red's own ROM species table will be
needed for exact naming beyond the overlap — nicknames already provide
ground truth for the divergent ids seen in this save.

## 5. Strings / build markers

ASCII scan (≥4 printable chars): **no version text, no "CFRU", "Radical Red",
"Unbound", or "PC01" markers anywhere** — only 8 short random letter runs from
mon/pid data. The trailer signature `0x08012025` is the only build
fingerprint. Gen3-charset strings: party nicknames in section 1; mail template
words in section 3; 63 PC nicknames across sections 5–7 (Regirock, Garchomp,
Gholdengo, Ursaluna, Annihilape, …); box names in section 13.

## 6. Radical Red vs vanilla FRLG — net deviation list

| aspect | vanilla FRLG | Radical Red (this save) |
|---|---|---|
| sector envelope, trailer layout | same | **same** (signature value differs: 0x08012025) |
| section 0 trainer card | same offsets | **same** + 20-byte unverified stash @0xF24 |
| party count/offsets | Large+0x34/+0x38 | **same offsets**, u32 count |
| party mon core | PID-ordered encrypted, checksummed | **plaintext, unshuffled, checksum field 0** |
| ball | origins bitfield @0x46+11 | **byte @0x2A** (CFRU) |
| PC mons | 80 B encrypted, 420 slots @Storage+4, secs 5–13 | **58 B compact plaintext stream, secs 5–7(+), stride 58 from +4** |
| box count | 14 | **25** (names in sec 13 @0x361) |
| box names/wallpaper | Storage+0x8344/0x83C2 | names **sec13+0x361**; wallpaper not located |
| daycare | Large+0x2F80 | same offset (empty here) |
| Hall of Fame | 0x1C000, 0x1F00-byte retail records | **0x1C000, 0x18-byte CFRU records** |
| dex storage | Small 0x28/0x5C + Large mirrors | moved to **0x1E000** block (empty in Small) |
| checksum windows | per-id vanilla sizes | full 0xFF4 for CFRU sections; stashes parked past windows |
| species/move/item ids | gen 3 | **CFRU-expanded gen 1–9** |

Differences vs Unbound worth noting for the future session impl: RR's stream
has **no per-section 4-byte header** (Unbound's `StreamPayloadOffset=4` is
per-section there; in RR only the first payload starts with the 4-byte
current-box field), RR uses fewer stream sections (5–7 used, 8–12 empty), and
RR exposes box names + current box; Unbound fragments boxes 19+ into other
regions, which this RR save has no evidence of (those boxes are simply empty).

## 7. Scripts (all run clean; `python3 <name>` from this dir)

| file | purpose |
|---|---|

| `rrlib.py` | shared lib: sector/trailer model, fold32 checksum, window discovery, G3 charmap (parsed from PKHeX `StringConverter3`), internal→national species table (from PKHeX `SpeciesConverter`), Unbound CFRU name table, vanilla-crypto Mon parser |
| `01_envelope.py` | trailer dump, both slot maps, newer-slot detection, checksum verification + window discovery + hand recomputation |
| `02_sections.py` | definitive content dissection under the discovered model (trainer/party/bag/PC/boxes/others) |
| `02b_mon_crypto_probe.py` | proves retail decryption fails; hexdump of a full party mon |
| `02c_pc_probe.py` | 80-byte vs 58-byte PC model comparison + nickname-based stride detection |
| `02d_compact_layout.py` | pins species offset @+0x1C via nickname reverse-match (117/117 votes) |
| `02e_region_map.py` | stream extent, region-by-region compact-mon scan, HoF glimpse |
| `02f_section13_hof.py` | section 13 nonzero map, box-name table, full HoF decode vs party |
| `03_strings.py` | stream-boundary continuity proof, ASCII + G3 string scans, marker search |
| `04_mon_crypto.py` | full field dumps (party mon 0, PC mons 0–1), plaintext proof, HoF cross-check, checksum recomputation |
| `05_bag_layout.py` | decodes the CFRU bag (five pockets) from the parasite/raw regions; validates money, checksums and item names |


## 8. The bag (supersedes two earlier guesses)

`05_bag_layout.py` plus the engine source (`src/item.c` of both Skeli789's CFRU
and RR's own build tree) pin the bag exactly:

- Five pockets of 4-byte `ItemSlot {u16 item; u16 quantity}` (quantity XOR the
  u16 security key — 0 here), **zero-id terminated**, at RAM **0x203BB20** in
  game order: Items **450** slots, Key **75**, Balls **50**, TM/HM **128**,
  Berries **75**.
- The run starts in **sector 13's parasite tail** (data +0xAD8 = P3+0x688) and
  crosses into the **raw sector-30/31 region** after 326 item slots, so key items,
  balls, TMs and berries live at file 0x1E1F0/0x1E31C/0x1E3E4/0x1E5E4 — identical
  to Unbound's known offsets (same engine).
- Consequences: the "0x1E000 = CFRU dex/flag extension block" guess in §1 was
  wrong (it is the bag plus engine runtime state), and the "bag @Large+0x298"
  note in §3 actually describes `pcItems` (the item PC), not the bag.
- Champion decode: 143 items (Rare Candy ×305, Ability Pill ×295, megastones,
  Z-crystals), 19 key items (incl. **Exp. Share**, a key item in RR), 8 balls,
  94 TM/HM discs (×1 each — reusable), 30 berries (Pomeg ×300).

## 9. Open items (not needed for a first working session, noted for later)

1. Exact wallpaper storage (candidate: byte pairs after the name table).
2. Contents/roles of the unverified stashes (sec 0 @0xF24, sec 4 @0xDAC–0xFC9,
   sec 13 @0x530–0xD13 incl. the 42-byte records). The 0x1E000 region is now
   understood (§8: the bag + runtime state); its non-bag bytes remain unidentified.
3. RR's full species/move/item name tables (nicknames cover the divergent ids
   present in this save; the ROM tables would close the gap).
4. Whether boxes 8–25 stream into sections 8–12 (consistent with the layout,
   unprovable while those boxes are empty — needs a save with >210 stored mons).
