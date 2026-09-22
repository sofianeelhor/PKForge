#!/usr/bin/env python3
"""Step 3 - string/markers scan + stream continuity proof.

1. Prove all PC stream mons decode cleanly across section payload boundaries
   (no per-section headers in RR, unlike Unbound's stream).
2. ASCII string scan of the whole file (version/config markers).
3. Gen3-encoded string scan (uppercase runs) by region.
"""
import re

from rrlib import CFRU_NAMES, g3str, load, u16, u32

data = load()
TID = 0x9025B36A
LIVE = {5: 0x14000, 6: 0x15000, 7: 0x16000}

print("=" * 100)
print("STREAM CONTINUITY PROOF")
stream = b"".join(data[LIVE[i] : LIVE[i] + 0xFF0] for i in (5, 6, 7))
clean = bad = 0
crossers = []
for i in range(211):
    o = 4 + 58 * i
    m = stream[o : o + 58]
    pid, otid = u32(m, 0), u32(m, 4)
    if pid == 0:
        continue
    ok = otid == TID and 0 < u16(m, 0x1C) <= 1500 and u32(m, 0x20) < 1_250_000
    # does this mon span a payload boundary (0xFF0, 0x1FE0)?
    spans = any(o < b <= o + 57 for b in (0xFF0, 0x1FE0))
    if spans:
        crossers.append((i, o, ok, g3str(m[8:0x12])))
    if ok:
        clean += 1
    else:
        bad += 1
        print(f"  BAD mon #{i} @0x{o:X} pid=0x{pid:08X} otid=0x{otid:08X} spc={u16(m, 0x1C)}")
print(f"clean: {clean}, bad: {bad}")
print("mons spanning payload boundaries (would break if headers existed):")
for i, o, ok, nick in crossers:
    print(f"  mon#{i} @stream 0x{o:X} clean={ok} nick={nick!r}")

print()
print("=" * 100)
print("ASCII STRING SCAN (runs >= 4 printable)")
hits = []
for m in re.finditer(rb"[ -~]{4,}", data):
    hits.append((m.start(), m.group().decode()))
interesting = [
    (o, s) for o, s in hits
    if re.search(r"[A-Za-z]{3}", s)
    and not re.fullmatch(r"[0-9A-F]{4,}", s)
]
for o, s in interesting:
    print(f"  0x{o:05X}: {s[:90]!r}")
print(f"total ascii runs>=4: {len(hits)} (letter-bearing: {len(interesting)})")

print()
print("=" * 100)
print("MARKER SEARCH")
for marker in (b"CFRU", b"Radical", b"RADICAL", b"Unbound", b"PC01", b"\x00\x20\x01\x08",
               b"\x99\x19\x12\x01", b"RR", b"v4.", b"FireRed", b"Pokemon"):
    found = [
        m.start() for m in re.finditer(re.escape(marker), data)
        if len(marker) > 2 or (m.start() % 0x1000) < 0x100
    ][:6]
    print(f"  {marker!r}: {len(found)} hit(s) at " + ", ".join(f"0x{o:X}" for o in found))

print()
print("=" * 100)
print("GEN3-ENCODED TEXT SCAN (len>=5, in live sections)")
UPPER = range(0xBB, 0xD5)
LOWER = range(0xD5, 0xEF)
LIVEO = {0: 0x0F000, 1: 0x10000, 2: 0x11000, 3: 0x12000, 4: 0x13000,
         5: 0x14000, 6: 0x15000, 7: 0x16000, 13: 0x0E000}
for sid, base in sorted(LIVEO.items()):
    buf = data[base : base + 0x1000]
    strings = []
    i = 0
    while i < 0xFF4 - 5:
        run = []
        j = i
        while j < 0xFF4 and (buf[j] in UPPER or buf[j] in LOWER):
            run.append(buf[j])
            j += 1
        if len(run) >= 5:
            strings.append((i, g3str(bytes(run))))
            i = j
        else:
            i += 1
    if strings:
        print(f"  section {sid}:")
        for off, s in strings[:20]:
            print(f"    +0x{off:03X}: {s!r}")
        if len(strings) > 20:
            print(f"    ... {len(strings) - 20} more")
