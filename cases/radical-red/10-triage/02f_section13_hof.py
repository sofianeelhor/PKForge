#!/usr/bin/env python3
"""Step 2f - final region detail: section 13 nonzero ranges (box names? extra PC?),
full HoF decode, and leftover stashes."""
from rrlib import CFRU_NAMES, g3str, load, u16, u32

data = load()
TID = 0x9025B36A
S13 = 0x0E000  # live section id 13 (slot B)

s13 = data[S13 : S13 + 0x1000]
print("section 13 payload nonzero ranges:")
in_run = False
start = 0
for i in range(0xF80):
    nz = s13[i] != 0
    if nz and not in_run:
        start, in_run = i, True
    elif not nz and in_run:
        in_run = False
        print(f"  0x{start:03X}..0x{i:03X}  ({i - start} bytes)  "
              f"first16: {s13[start:start+16].hex(' ')}")
if in_run:
    print(f"  0x{start:03X}..0x{0xF80:03X}")

print("\nbox-name candidates at vanilla storage-mapped offset (section13 + 0x784):")
for b in range(14):
    chunk = s13[0x784 + 9 * b : 0x784 + 9 * (b + 1)]
    print(f"  box {b:2d}: raw={chunk.hex(' ')}  g3={g3str(chunk)!r}")
print(f"  wallpapers @+0x802: {list(s13[0x802:0x802+14])}")

print("\nsection 13 region 0x2C0..0x480 detail (checksum-window start):")
for off in range(0x2C0, 0x480, 16):
    row = s13[off : off + 16]
    if any(row):
        print(f"  0x{off:03X}: {row.hex(' ')}")

print("\nsection 13 region 0x530..0xD20 nonzero rows:")
for off in range(0x530, 0xD20, 16):
    row = s13[off : off + 16]
    if any(row):
        print(f"  0x{off:03X}: {row.hex(' ')}")

print()
print("=" * 100)
print("HALL OF FAME @0x1C000 (CFRU format: otid, pid, species, level, nickname)")
hof = data[0x1C000 : 0x1C000 + 0x100]
REC = 0x18
for i in range(8):
    off = i * REC
    otid = u32(hof, off)
    pid = u32(hof, off + 4)
    spc = u16(hof, off + 8)
    lvl = hof[off + 10]
    nick = g3str(hof[off + 11 : off + 21])
    if otid == 0 and pid == 0:
        print(f"  entry {i}: end of records")
        break
    print(f"  entry {i}: otid=0x{otid:08X} pid=0x{pid:08X} species={spc} "
          f"({CFRU_NAMES.get(spc, '?')}) level={lvl} nick={nick!r} "
          f"tail={hof[off+21:off+24].hex()}")

print()
print("party pids for cross-check:")
P1 = 0x10000
for i in range(6):
    o = P1 + 0x38 + 100 * i
    print(f"  party[{i}] pid=0x{u32(data, o):08X} spc={u16(data, o + 0x20)} "
          f"lvl={data[o + 0x54]} nick={g3str(data[o + 8 : o + 18])!r}")

print()
print("0x1E000 nonzero rows:")
for off in range(0, 0xB10, 16):
    row = data[0x1E000 + off : 0x1E000 + off + 16]
    if any(row):
        print(f"  0x{off:03X}: {row.hex(' ')}")
