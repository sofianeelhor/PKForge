#!/usr/bin/env python3
"""Step 2d - pin down RR's 58-byte compact PC mon layout via nickname matching.

Mons sit at section5_payload+4 with 58-byte stride (confirmed structurally).
Unbound's compact layout (species@0x1C) gives wrong species for the nicknames,
so scan each mon's bytes for a species id consistent with its default nickname
(looked up in Unbound's CFRU species table) to locate the real fields.
"""
from rrlib import load, u16, u32, g3str, CFRU_NAMES

data = load()
LIVE = {5: 0x14000, 6: 0x15000, 7: 0x16000, 13: 0x0E000}

name_to_ids = {}
for sid, name in CFRU_NAMES.items():
    name_to_ids.setdefault(name.lower().replace(" ", "").replace("?", "").replace("'", ""), []).append(sid)

stream = b"".join(data[o : o + 0xFF0] for o in (LIVE[5], LIVE[6], LIVE[7]))
print(f"stream = sections 5,6,7 payloads (first 4 bytes skipped per section?) len=0x{len(stream):X}")
for sid, off in sorted(LIVE.items()):
    print(f"  section {sid} first 8 bytes: {data[off:off+8].hex(' ')}")

BASE = 4  # current-box u32 at +0, mons from +4


def mons(buf, base=BASE, stride=58, count=None):
    n = count if count else (len(buf) - base) // stride
    for i in range(n):
        o = base + i * stride
        yield i, buf[o : o + stride]


print("\nfirst 10 compact mons:")
for i, m in mons(stream, count=10):
    if len(m) < 58:
        break
    print(f"  #{i:3d} pid=0x{u32(m,0):08X} otid=0x{u32(m,4):08X} nick={g3str(m[8:0x12]):<12} "
          f"ot={g3str(m[0x14:0x1B]):<8} bytes[0x1C:0x3A]={m[0x1C:].hex(' ')}")

# reverse lookup: which species ids would match each nickname?
print("\nnickname -> candidate CFRU ids (Unbound table) + search of those ids in mon bytes:")
found_at = {}
for i, m in mons(stream, count=400):
    nick = g3str(m[8:0x12])
    key = nick.lower().replace(" ", "").replace("?", "").replace("'", "")
    ids = name_to_ids.get(key)
    if not ids or len(m) < 58:
        continue
    hits = {}
    for sid in ids:
        pat_le = sid.to_bytes(2, "little")
        for off in range(0x1A, 0x3A - 1):
            if m[off : off + 2] == pat_le:
                hits.setdefault(off, []).append(sid)
    if hits:
        offs = sorted(hits)
        # vote for offsets that hit for many mons
        for off in offs:
            found_at[off] = found_at.get(off, 0) + 1
        if i < 40:
            print(f"  #{i:3d} {nick:<12} ids={ids} u16 hit offsets: "
                  + ", ".join(f"0x{o:X}:{hits[o]}" for o in offs))

print("\nvote table (offset -> how many nickname-matched mons carry their id there):")
for off, votes in sorted(found_at.items(), key=lambda kv: -kv[1]):
    print(f"  mon+0x{off:02X}: {votes}")
