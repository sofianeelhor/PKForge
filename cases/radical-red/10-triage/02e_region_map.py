#!/usr/bin/env python3
"""Step 2e - map the rest of the RR save: stream extent, other mon regions,
daycare, box metadata, and every CFRU stash.

PC stream confirmed: 58-byte compact mons, mon[0]=u32 current-box, mons from +4,
contiguous across section payloads 5,6,7. Now inventory everything else.
"""
from rrlib import CFRU_NAMES, Mon, SECTOR, g3str, load, u16, u32

data = load()
TID = 0x9025B36A
LIVE = {0: 0x0F000, 1: 0x10000, 2: 0x11000, 3: 0x12000, 4: 0x13000,
        5: 0x14000, 6: 0x15000, 7: 0x16000, 13: 0x0E000}


def compact(buf, off):
    m = buf[off : off + 58]
    if len(m) < 58:
        return None
    word = u32(m, 0x27)
    moves = [word & 0x3FF, (word >> 10) & 0x3FF, (word >> 20) & 0x3FF,
             ((word >> 30) | (m[0x2B] << 2)) & 0x3FF]
    return {
        "pid": u32(m, 0), "otid": u32(m, 4), "nick": g3str(m[8:0x12]),
        "ot": g3str(m[0x14:0x1B]), "species": u16(m, 0x1C), "item": u16(m, 0x1E),
        "exp": u32(m, 0x20), "ball": m[0x26], "moves": moves,
        "ivs": u32(m, 0x36), "evs": list(m[0x2C:0x32]),
    }


print("=" * 100)
print("PC STREAM EXTENT (sections 5,6,7 payloads, mons @+4 stride 58)")
stream = b"".join(data[LIVE[i] : LIVE[i] + 0xFF0] for i in (5, 6, 7))
print(f"stream len 0x{len(stream):X}; header u32 @0 = {u32(stream, 0)} (current box?)")
n = 0
last = -1
for i in range((len(stream) - 4) // 58):
    m = compact(stream, 4 + 58 * i)
    if m["pid"] == 0:
        continue
    n += 1
    last = i
print(f"non-empty compact mons: {n}; last used slot index {last} "
      f"-> boxes covered: {last // 30 + 1} (box {last // 30}, slot {last % 30})")
print(f"bytes after last mon: stream[{4 + 58 * (last + 1):}] all-zero = "
      f"{stream[4 + 58 * (last + 1) :] == bytes(len(stream) - 4 - 58 * (last + 1))}")

for i in range(0, min(last + 1, 12)):
    m = compact(stream, 4 + 58 * i)
    name = CFRU_NAMES.get(m["species"], "?")
    print(f"  slot{i:3d}: {m['nick']:<12} spc={m['species']:4} ({name:<12}) "
          f"item={m['item']:3} exp={m['exp']:7} ball={m['ball']:2} moves={m['moves']} "
          f"iv=0x{m['ivs']:08X} evs={m['evs']}")

print()
print("=" * 100)
print("REGION PROBES - compact-mon scan over every region with data")
REGIONS = [
    ("section 2 payload (Large chunk1)", LIVE[2], 0x2C8),
    ("section 4 payload 0x100.. (daycare zone)", LIVE[4] + 0x100, 0xDAC - 0x100),
    ("section 4 stash 0xDAC..0xFCA", LIVE[4] + 0xDAC, 0xFC9 - 0xDAC),
    ("section 13 payload (id13)", LIVE[13], 0xD54),
    ("extra 0x1C000 (HoF)", 0x1C000, 0x8E),
    ("extra 0x1E000", 0x1E000, 0xB05),
    ("section 0 stash 0xF24..0xF38", LIVE[0] + 0xF24, 0x14),
]
for label, base, ln in REGIONS:
    print(f"\n-- {label} (file 0x{base:X}, len 0x{ln:X})")
    print(f"   hex: {data[base : base + min(ln, 0x60)].hex(' ')}")
    if ln >= 0x60:
        print(f"   tail: {data[base + ln - 0x20 : base + ln].hex(' ')}")
    hits = []
    for off in range(0, ln - 58, 1):
        m = compact(data, base + off)
        if m and m["pid"] != 0 and m["otid"] == TID and 0 < m["species"] < 1500 and m["exp"] < 1_250_000:
            nick = m["nick"]
            if nick and (m["species"] in CFRU_NAMES) and nick.lower().startswith(CFRU_NAMES[m["species"]].lower()[:3]):
                hits.append((off, m))
    if hits:
        offs = [h[0] for h in hits]
        strides = {b - a for a, b in zip(offs, offs[1:])}
        print(f"   compact-mon hits (nickname-plausible): {len(hits)} at offsets {offs[:12]}... strides={strides}")
        for off, m in hits[:6]:
            print(f"     +0x{off:X}: {m['nick']:<12} spc={m['species']} ({CFRU_NAMES.get(m['species'])}) exp={m['exp']}")
    else:
        print("   no compact-mon hits")

print()
print("=" * 100)
print("SECTION 13 DECODE ATTEMPT (box names? wallpapers? mons?)")
s13 = data[LIVE[13] : LIVE[13] + SECTOR]
print("g3 strings at 9-byte stride guesses (vanilla name table is 14x9):")
for start in (0x000, 0x004, 0x008):
    names = [g3str(s13[start + 9 * i : start + 9 * (i + 1)]) for i in range(14)]
    if sum(1 for x in names if x and x.isprintable()):
        print(f"  start 0x{start:X}: {names}")
print("raw first 0x120 bytes:")
for off in range(0, 0x120, 16):
    print(f"   0x{off:03X}: {s13[off:off+16].hex(' ')}  ascii={''.join(chr(c) if 32<=c<127 else '.' for c in s13[off:off+16])}")
