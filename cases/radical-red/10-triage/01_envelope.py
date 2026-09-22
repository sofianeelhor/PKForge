#!/usr/bin/env python3
"""Step 1 - GBA sector envelope of Radical-red-champ.sav.

Dumps every physical sector's trailer (section id, checksum, the two u32 words),
reconstructs both slots' section maps, decides which slot is newer, verifies the
checksum algorithm against every sector, and discovers each sector's exact
checksum window (which reveals CFRU deviations from vanilla coverage).
"""
from rrlib import (
    SECTOR, SLOT_SIZE, compact_ranges, fold32, last_nonzero, load, sectors,
    u16, u16sum, valid_windows,
)

data = load()
secs = sectors(data)

print("=" * 100)
print("PHYSICAL SECTOR TRAILERS  (u16 id @+0xFF4 | u16 chk @+0xFF6 | u32 @+0xFF8 | u32 @+0xFFC)")
print("=" * 100)
print(f"{'phys':>10} {'region':>10} {'id':>6} {'checksum':>9} {'u32@FF8':>11} {'u32@FFC':>11}")
for s in secs:
    print(
        f"0x{s.off:05X} {s.region:>10} "
        f"{s.section_id if s.section_id < 0x8000 else f'0x{s.section_id:04X}':>6} "
        f"0x{s.checksum:04X}  0x{s.w_ff8:08X}  0x{s.w_ffc:08X}"
    )

# signature census
print()
print("u32@+0xFF8 distinct values:", {f"0x{x:08X}" for x in (s.w_ff8 for s in secs)})
print("u32@+0xFFC distinct values:", {f"0x{x:08X}" for x in (s.w_ffc for s in secs)})

# ---------------------------------------------------------------- slot maps
print()
print("=" * 100)
print("SLOT SECTION MAPS")
print("=" * 100)
for slot in (0, 1):
    base = slot * SLOT_SIZE
    rows = secs[base // SECTOR : base // SECTOR + 14]
    ids = [s.section_id for s in rows]
    counters = {s.w_ffc for s in rows}
    print(f"slot {slot} (0x{base:05X}..0x{base + SLOT_SIZE - 1:05X})")
    print(f"  physical sector order -> section ids: {ids}")
    present = set(ids)
    missing = [i for i in range(14) if i not in present]
    dupes = [i for i in present if ids.count(i) > 1]
    print(f"  present ids: {sorted(present)}  missing: {missing or 'none'}  duplicates: {dupes or 'none'}")
    print(f"  u32@+0xFFC values across sections: {sorted(f'0x{c:08X}' for c in counters)}")

slotA, slotB = secs[:14], secs[14:28]
ctrA, ctrB = slotA[0].w_ffc, slotB[0].w_ffc
print()
print(f"save counter slotA=0x{ctrA:08X} ({ctrA})  slotB=0x{ctrB:08X} ({ctrB})")
newer = 0 if (ctrA - ctrB) & 0xFFFFFFFF < 0x80000000 and ctrA != ctrB else 1
if ctrA == ctrB:
    print("counters equal (unusual); defaulting to slot A")
else:
    print(f"=> newer slot: {'A' if newer == 0 else 'B'} (higher save counter)")

# ----------------------------------------------------------- checksum proofs
print()
print("=" * 100)
print("CHECKSUM VERIFICATION (retail algorithm: fold16 of LE-u32 word sum)")
print("=" * 100)
print(f"{'sector':>10} {'id':>3} {'stored':>7} {'fold32@F80':>11} {'u16@F80':>8} "
      f"{'fold32@FF4':>11} {'min_window':>10} {'valid windows (4-byte steps)':>44} {'lastNZ':>7}")
for s in secs[:28]:
    payload = s.raw
    f80 = fold32(payload, 0xF80)
    ff4 = fold32(payload, 0xFF4)
    w = valid_windows(payload, s.checksum)
    print(
        f"0x{s.off:05X} {s.section_id:>3} 0x{s.checksum:04X} "
        f"{'MATCH' if f80 == s.checksum else f'0x{f80:04X}':>7} "
        f"{'MATCH' if u16sum(payload, 0xF80) == s.checksum else 'no':>8} "
        f"{'MATCH' if ff4 == s.checksum else f'0x{ff4:04X}':>9} "
        f"{f'0x{min(w):X}' if w else '-':>10} "
        f"{compact_ranges(w):>44} "
        f"0x{last_nonzero(payload):X}"
    )

print()
print("extra sectors (0x1C000..0x1FFFF):")
for s in secs[28:]:
    allff = s.raw[:-0xC] == b"\xFF" * (SECTOR - 0xC)
    all00 = s.raw[:-0xC] == b"\x00" * (SECTOR - 0xC)
    tail = s.raw[-0xC:].hex()
    print(
        f"  0x{s.off:05X} payload {'all-FF' if allff else 'all-00' if all00 else 'data'} "
        f"lastNZ=0x{last_nonzero(s.raw):X} last 0xC bytes: {tail}"
    )

# recompute proof for one section, spelled out
print()
print("=" * 100)
print("CHECKSUM ALGORITHM RECOMPUTED BY HAND FOR ONE SECTOR")
print("=" * 100)
s = secs[:28][next(i for i, x in enumerate(secs[:28]) if x.section_id == 1)]
total = 0
for o in range(0, 0xFF4, 4):
    total += int.from_bytes(s.raw[o : o + 4], "little")
folded = (total + (total >> 16)) & 0xFFFF
print(f"sector 0x{s.off:05X} (section id {s.section_id}):")
print(f"  sum of {0xFF4 // 4} LE-u32 words = 0x{total:X}")
print(f"  fold: (sum + (sum >> 16)) & 0xFFFF = 0x{folded:X}")
print(f"  stored u16 @+0xFF6 = 0x{s.checksum:04X}  -> {'MATCH' if folded == s.checksum else 'MISMATCH'}")
