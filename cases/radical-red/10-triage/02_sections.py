#!/usr/bin/env python3
"""Step 2 (final) - section content dissection using the DISCOVERED RR model.

Model (derived in 02b-02f):
  - envelope: 14 sections/slot, trailer u16 id @+0xFF4 / u16 chk @+0xFF6 /
    u32 signature 0x08012025 @+0xFF8 / u32 save counter @+0xFFC
  - section 0 = Small (trainer card, dex): vanilla FRLG offsets
  - sections 1-4 = Large chunks: party u32 count @+0x34, 100-byte mons @+0x38
    with PLAINTEXT unshuffled cores (CFRU), money @+0x290, bag @+0x298
  - sections 5..7 = PC storage stream: u32 current-box @payload+0, then
    58-byte compact mons (no per-section headers), 211-slot capacity (7 boxes)
  - section 13 = box names ("Box1".."Box25" @+0x360, 9B stride) + CFRU stashes
  - sections 8-12 present but zero (future stream chunks / unused)
  - 0x1C000 = Hall of Fame, CFRU 0x18-byte records (otid,pid,species,level,nick)
"""
from rrlib import CFRU_NAMES, g3str, last_nonzero, load, u16, u32

data = load()

# live copies from newer slot B (counter 0x127 > 0x126)
LIVE = {0: 0x0F000, 1: 0x10000, 2: 0x11000, 3: 0x12000, 4: 0x13000,
        5: 0x14000, 6: 0x15000, 7: 0x16000, 13: 0x0E000}
small = data[LIVE[0] : LIVE[0] + 0x1000]
large = b"".join(data[LIVE[i] : LIVE[i] + 0xF80] for i in (1, 2, 3, 4))

print("=" * 100)
print("SECTION 0 - Small (trainer card + dex)")
print("=" * 100)
print(f"  OT name @+0x000 : {g3str(small[0:8])!r}")
print(f"  gender  @+0x008 : {small[8]}")
print(f"  TID/SID @+0x00A : {u16(small, 0x0A):05d} / {u16(small, 0x0C):05d}")
print(f"  played  @+0x00E : {u16(small, 0x0E)}h {small[0x10]}m {small[0x11]}s")
print(f"  security key @+0xF20 : 0x{u32(small, 0xF20):08X}")
print(f"  checksum window 0xF24 (vanilla), stash 0xF24..0xF38: {small[0xF24:0xF38].hex(' ')}")

print()
print("=" * 100)
print("SECTION 1 - Large chunk 0: PARTY (vanilla offsets, CFRU plaintext cores)")
print("=" * 100)
count = u32(large, 0x34)
print(f"  party count u32 @Large+0x34 = {count}")
print(f"  party mons   @Large+0x38, 100 bytes each (vanilla offset, vanilla layout,"
      f" but data core is PLAINTEXT):")
for i in range(count):
    o = 0x38 + 100 * i
    m = large[o : o + 100]
    spc = u16(m, 0x20)
    print(f"   party[{i}] @+0x{o:03X}: pid=0x{u32(m, 0):08X} otid=0x{u32(m, 4):08X} "
          f"nick={g3str(m[8:0x12]):<11} OT={g3str(m[0x14:0x1B]):<4} spc={spc} "
          f"({CFRU_NAMES.get(spc, '?')}) item={u16(m, 0x22)} exp={u32(m, 0x20 + 4)} "
          f"ball={m[0x2A]} lvl@0x54={m[0x54]} hp={u16(m, 0x56)} "
          f"ivword=0x{u32(m, 0x48):08X} chk@0x1C=0x{u16(m, 0x1C):04X}")
key = u32(small, 0xF20)
print(f"  money @Large+0x290 xor key: {u32(large, 0x290) ^ key}")
print(f"  bag   @Large+0x298 (0x360 bytes): CF RU expanded pockets,")
nz = [(u16(large, 0x298 + 4 * i), u16(large, 0x29A + 4 * i)) for i in range(0xD2)]
print(f"        {sum(1 for it, _ in nz if it)} nonzero rows, sample: {nz[:6]}")

print()
print("=" * 100)
print("SECTIONS 5..7 - PC STORAGE STREAM (CFRU compact)")
print("=" * 100)
stream = b"".join(data[LIVE[i] : LIVE[i] + 0xFF0] for i in (5, 6, 7))
print(f"  current box u32 @sec5+0x000 = {u32(stream, 0)}")
print(f"  mons @stream+0x004, 58 bytes each, capacity {(len(stream) - 4) // 58}")
occ = {}
total = 0
for i in range(211):
    o = 4 + 58 * i
    m = stream[o : o + 58]
    if u32(m, 0) == 0:
        continue
    total += 1
    occ[i // 30] = occ.get(i // 30, 0) + 1
print(f"  occupied slots: {total}; per box: " + ", ".join(f"box{b}={n}" for b, n in sorted(occ.items())))
print(f"  box names in section 13 @+0x360 (25 x 9 bytes):")
s13 = data[LIVE[13] : LIVE[13] + 0x1000]
names = [g3str(s13[0x361 + 9 * i : 0x361 + 9 * (i + 1)]) for i in range(25)]
print(f"    {names}")

print()
print("=" * 100)
print("OTHER SECTIONS")
print("=" * 100)
print(f"  section 2  : sparse records to +0x2C5 (roamer/battle-misc); enemy team area unused")
print(f"  section 3  : mail templates & misc (ENIGMA/Hello/Trade/...), full to +0xF96")
print(f"  section 4  : daycare zone @+0x100 EMPTY, checksum window ends 0xDAC, "
      f"stash 0xDAC..0xFC9 (unverified)")
print(f"  sections 8-12: payload all-zero (ids + signature intact, checksum 0)")
print(f"  section 13 : names @0x360, window <=0x52C, unverified stash 0x530..0xD13")
print(f"                (incl. eleven 42-byte records @0x9E8 and a (u16,u16) table)")
print(f"  0x1C000    : Hall of Fame - CFRU records (otid,pid,species u16,level u8,nick 10B), "
      f"6 entries = champion team")
print(f"  0x1D000    : zero, signature-only")
print(f"  0x1E000    : CFRU dex/flag extension block (id/count pairs + bitfields), no mons")
print(f"  0x1F000    : zero")
