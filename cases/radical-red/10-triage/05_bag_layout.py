#!/usr/bin/env python3
"""RR bag layout validation against Radical-red-champ.sav.

Model (from CFRU master src/item.c + save.c):
  struct ItemSlot { u16 itemId; u16 quantity; }   // quantity ^= (u16)securityKey
  sBagRegularItems = RAM 0x203BB20, then Key(+450 slots), Balls(+75), TMHM(+50), Berries(+NUM_TMSHMS)
  P3 parasite: sec13 data[0x450..0xFF0) -> RAM 0x203B498, so Items starts sec13+0xAD8.
  Raw region (sectors 30+31, no checksums): RAM 0x203C038 -> file 0x1E000, footer gap at 0x1EFF0.
  Items pocket (450 slots) crosses the boundary: 326 slots in sec13, 124 in raw region.
"""
import struct, sys, pathlib

SAVE = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".local-testdata/radicalred-champ.sav")
ITEMS = pathlib.Path("src/PKForge.Engine/RadicalRed/Data/items.txt")
SEC = 0x1000

data = SAVE.read_bytes()
assert len(data) == 0x20000, len(data)

names = {}
for line in ITEMS.read_text().splitlines():
    line = line.strip()
    if not line or line.startswith("#"):
        continue
    ident, _, nm = line.partition("\t")
    if not _:
        ident, _, nm = line.partition(" ")
    names[int(ident)] = nm

def name(i):
    return names.get(i, f"<{i}??>")

# -- sections: footer id u16 @+0xFF4, checksum u16 @+0xFF6, sig u32 @+0xFF8, index u32 @+0xFFC
live, copies = {}, {}
for s in range(32):
    off = s * SEC
    sig, = struct.unpack_from("<I", data, off + 0xFF8)
    if (sig & 0xFFFFFF00) != 0x08012000:
        continue
    sid, = struct.unpack_from("<H", data, off + 0xFF4)
    idx, = struct.unpack_from("<I", data, off + 0xFFC)
    copies.setdefault(sid, []).append((off, idx))
    if sid not in live or idx >= live[sid][1]:
        live[sid] = (off, idx)

print("live sections:", {k: hex(v[0]) for k, v in sorted(live.items())})
sec = {k: v[0] for k, v in live.items()}
for sid in range(14):
    assert sid in sec, sid

sec0, sec1, sec13 = sec[0], sec[1], sec[13]

# -- money
key, = struct.unpack_from("<I", data, sec0 + 0xF20)
money_raw, = struct.unpack_from("<I", data, sec1 + 0x290)
money = money_raw ^ key
print(f"securityKey=0x{key:08X}  money raw=0x{money_raw:08X} decoded={money:,}")
assert money == 1_391_024, money

qkey = key & 0xFFFF

# -- checksum helper (retail fold32 over CFRU windows)
CFRU = [0xF24] + [0xFF0]*3 + [0xD98] + [0xFF0]*8 + [0x450]
def chk(off, length):
    total = 0
    for o in range(0, length & ~3, 4):
        total += struct.unpack_from("<I", data, off + o)[0]
    return (total + (total >> 16)) & 0xFFFF

for sid in range(14):
    off = sec[sid]
    stored, = struct.unpack_from("<H", data, off + 0xFF6)
    assert chk(off, CFRU[sid]) == stored, (sid, "checksum mismatch")

# -- bag region assembly: 'ram' = flat [0x203B174 ... ) mapped parasite P3 + raw region
#    P3: sec13 data[0x450..0xFF0)  -> RAM 0x203B498
#    raw: file 0x1E000..0x1EFF0 + 0x1F000..0x1FF0 -> RAM 0x203C038 (gap skipped)
p3 = data[sec13 + 0x450: sec13 + 0xFF0]
raw = data[0x1E000:0x1EFF0] + data[0x1F000:0x1FF0]
ram_base = 0x203B498

def ram_read(addr, n):
    if addr >= 0x203C038:
        o = addr - 0x203C038
        return raw[o:o+n]
    o = addr - ram_base
    return p3[o:o+n]

def slots(addr, count):
    out = []
    for i in range(count):
        iid, q = struct.unpack("<HH", ram_read(addr + 4*i, 4))
        out.append((iid, q ^ qkey))
    return out

def run(addr, cap):
    out = []
    for iid, q in slots(addr, cap):
        if iid == 0:
            break
        out.append((iid, q))
    return out

BAG = 0x203BB20
N_ITEMS, N_KEY, N_BALLS, N_TM, N_BERRY = 450, 75, 50, 128, 75
pockets = [
    ("Items",   BAG),
    ("Key",     BAG + 4*N_ITEMS),
    ("Balls",   BAG + 4*(N_ITEMS+N_KEY)),
    ("TMs",     BAG + 4*(N_ITEMS+N_KEY+N_BALLS)),
    ("Berries", BAG + 4*(N_ITEMS+N_KEY+N_BALLS+N_TM)),
]

for pname, addr in pockets:
    r = run(addr, 450)
    print(f"\n== {pname} @RAM 0x{addr:08X}: {len(r)} entries")
    for iid, q in r[:200]:
        print(f"   {iid:4d} x{q:<4d} {name(iid)}")
    bad = [(i, q) for i, q in r if i not in names]
    if bad:
        print("   !! unknown ids:", bad[:8])

# -- raw-region probe: locate actual run boundaries to confirm constants
print("\n== raw region nonzero map (first 0x800) ==")
runs, start = [], None
for o in range(0, 0x800, 4):
    iid, q = struct.unpack_from("<HH", raw, o)
    nz = iid != 0 or q != 0
    if nz and start is None:
        start = o
    if not nz and start is not None:
        runs.append((start, o))
        start = None
if start is not None:
    runs.append((start, 0x800))
for a, b in runs:
    iid, q = struct.unpack_from("<HH", raw, a)
    last, _ = struct.unpack_from("<HH", raw, b - 4)
    print(f"   region 0x{a:03X}..0x{b:03X} ({(b-a)//4} slots): "
          f"{name(iid)} .. {name(last)}  [first id {iid}, last id {last}]")
print("\nOK: money, checksums, and all five pockets decoded.")
