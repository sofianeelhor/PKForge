#!/usr/bin/env python3
"""Step 2c - identify the PC storage format used by Radical Red.

Candidates: vanilla 80-byte slots at Storage+4 (but plaintext CFRU cores), or the
Unbound-style 58-byte compact stream. Parses under each model and reports which
yields sane species/exp/nicknames.
"""
import struct

from rrlib import g3str, load, u16, u32

data = load()
LIVE = {5: 0x14000, 6: 0x15000, 7: 0x16000, 13: 0x0E000}  # slot B copies
sec5 = data[LIVE[5] : LIVE[5] + 0x1000]
storage = b"".join(data[o : o + 0xF80] for o in (LIVE[5], LIVE[6], LIVE[7], LIVE[13]))

print("section 5 payload first 0x120 bytes:")
for off in range(0, 0x120, 16):
    print(f"  +0x{off:03X}: {sec5[off:off+16].hex(' ')}")
print(f"\nsection 5 bytes 0x1F0..0x2B0 (would be mon#3/4 zone at 80B stride):")
for off in range(0x1F0, 0x2B0, 16):
    print(f"  +0x{off:03X}: {sec5[off:off+16].hex(' ')}")


def sane_species(s: int) -> bool:
    return 0 < s <= 2000


def parse_80(buf: bytes, base: int, i: int):
    o = base + 80 * i
    m = buf[o : o + 80]
    return {
        "pid": u32(m, 0), "otid": u32(m, 4), "nick": g3str(m[8:0x12]),
        "ot": g3str(m[0x14:0x1B]), "species": u16(m, 0x20), "item": u16(m, 0x22),
        "exp": u32(m, 0x24), "ball": m[0x2A], "ivs": u32(m, 0x48),
        "lang": m[0x12],
    }


def parse_58(buf: bytes, base: int, i: int):
    o = base + 58 * i
    m = buf[o : o + 58]
    word = u32(m, 0x27)
    moves = [(word >> 0) & 0x3FF, (word >> 10) & 0x3FF, (word >> 20) & 0x3FF]
    moves.append(((u32(m, 0x27) >> 30) | (m[0x2B] << 2)) & 0x3FF)
    return {
        "pid": u32(m, 0), "otid": u32(m, 4), "nick": g3str(m[8:0x12]),
        "ot": g3str(m[0x14:0x1B]), "species": u16(m, 0x1C), "item": u16(m, 0x1E),
        "exp": u32(m, 0x20), "ball": m[0x26], "ivs": u32(m, 0x36),
        "moves": moves, "lang": m[0x12],
    }

print("\n" + "=" * 100)
print("MODEL A: 80-byte plaintext mons, vanilla slots at Storage+4")
for i in range(6):
    m = parse_80(storage, 4, i)
    print(f"  slot{i}: pid=0x{m['pid']:08X} nick={m['nick']!r:14} spc={m['species']:5} "
          f"item={m['item']:4} exp={m['exp']:8} ball={m['ball']:3} lang={m['lang']} "
          f"ivword=0x{m['ivs']:08X}")


def count_80():
    n = 0
    for i in range(420):
        o = 4 + 80 * i
        if o + 80 > len(storage):
            break
        m = storage[o : o + 80]
        if m[:4] == b"\0\0\0\0":
            continue
        if 0 < u16(m, 0x20) <= 2000:
            n += 1
    return n


print(f"  slots with sane species+pid across 420: {count_80()}")

print("\n" + "=" * 100)
print("MODEL B: 58-byte compact mons, stream at Storage+0 and Storage+4")
for base in (0, 4):
    print(f"  -- base +0x{base:X}")
    for i in range(6):
        m = parse_58(storage, base, i)
        print(f"   slot{i}: pid=0x{m['pid']:08X} nick={m['nick']!r:14} spc={m['species']:5} "
              f"item={m['item']:4} exp={m['exp']:8} ball={m['ball']:3} moves={m['moves']}")
    n_ok = 0
    for i in range(500):
        o = base + 58 * i
        if o + 58 > len(storage):
            break
        m = parse_58(storage, base, i)
        if m["pid"] != 0 and sane_species(m["species"]):
            n_ok += 1
    print(f"   slots with sane species+pid across 500: {n_ok}")

# stride detection: where do nicknames appear? scan for gen3 A-Z runs in storage
print("\nnickname-like strings in storage (g3 uppercase runs >=4):")
i = 0
hits = []
while i < len(storage) - 8:
    s = g3str(storage[i : i + 11])
    if len(s) >= 4 and s.isupper() and s.isalpha():
        hits.append((i, s))
        i += 10
    else:
        i += 1
for off, s in hits[:40]:
    print(f"  Storage+0x{off:04X} (chunk {off // 0xF80}, +0x{off % 0xF80:03X}): {s!r} "
          f"-> if 80B model, slot {(off - 4) // 80}; pid@mon=0x{u32(storage, off - 8):08X}, "
          f"spc@+0x18=0x{u16(storage, off + 8):04X}")
print(f"  total hits: {len(hits)}")
deltas = [b[0] - a[0] for a, b in zip(hits, hits[1:])]
print(f"  consecutive-hit deltas: {deltas[:30]}")
