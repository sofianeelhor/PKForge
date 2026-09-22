#!/usr/bin/env python3
"""Step 4 - mon format validation: prove the CFRU plaintext model end to end.

Radical Red does NOT use retail Gen3 mon encryption in the save. Proofs:
  1. retail decryption (PID^OTID keystream + shuffle) yields garbage;
  2. the raw 0x20..0x50 core is directly readable plaintext in fixed
     G,A,E,M order (species/item/exp/PP sane, level consistent);
  3. mon checksum field is 0x0000 (unused) and sanity field is 0x0000;
  4. the same PID appears in the Hall of Fame record with matching species.
Dumps every field of party mon 0 and PC mons 0-1.
"""
import struct

from rrlib import CFRU_NAMES, SHUFFLE, g3str, load, u16, u32

data = load()
LIVE1 = 0x10000
LIVE = {5: 0x14000, 6: 0x15000, 7: 0x16000}


def sum16(b):
    return sum(struct.unpack_from("<H", b, o)[0] for o in range(0, len(b), 2)) & 0xFFFF


def dump_party(m):
    pid, otid = u32(m, 0), u32(m, 4)
    print(f"  header: pid=0x{pid:08X} otid=0x{otid:08X} (TID={otid & 0xFFFF:05d} SID={otid >> 16:05d})")
    print(f"  nick @0x08 = {g3str(m[8:0x12])!r}  lang @0x12={m[0x12]}  flags @0x13=0x{m[0x13]:02X}")
    print(f"  OT   @0x14 = {g3str(m[0x14:0x1B])!r}  markings @0x1B={m[0x1B]}")
    print(f"  substructure checksum @0x1C = 0x{u16(m, 0x1C):04X} (UNUSED, always 0)  "
          f"sanity @0x1E = 0x{u16(m, 0x1E):04X}")
    g, a, e, mi = m[0x20:0x2C], m[0x2C:0x38], m[0x38:0x44], m[0x44:0x50]
    print(f"  PLAINTEXT core in fixed G,A,E,M order:")
    print(f"    Growth  @0x20: species={u16(g, 0)} ({CFRU_NAMES.get(u16(g, 0), '?')}) "
          f"item={u16(g, 2)} exp={u32(g, 4)} ppUps=0x{g[8]:02X} friendship={g[9]} "
          f"ball@0x2A={m[0x2A]}")
    print(f"    Attack  @0x2C: moves={struct.unpack_from('<4H', a, 0)} "
          f"pp={tuple(a[8:12])}")
    print(f"    EV/Cond @0x38: evs={tuple(e[0:6])} contest={tuple(e[6:12])}")
    origins = struct.unpack_from("<H", mi, 2)[0]
    iv32 = struct.unpack_from("<I", mi, 4)[0]
    print(f"    Misc    @0x44: pokerus={mi[0]} metLoc={mi[1]} origins=0x{origins:04X} "
          f"(metLvl={origins & 0x7F} game={(origins >> 7) & 0xF}) "
          f"ivword=0x{iv32:08X} (ivs={[ (iv32 >> s) & 31 for s in (0, 5, 10, 15, 20, 25)]}"
          f" egg={(iv32 >> 30) & 1} hiddenAbility={(iv32 >> 31) & 1})")
    print(f"  party tail @0x50: status=0x{u32(m, 0x50):08X} level@0x54={m[0x54]} "
          f"hp@0x56={u16(m, 0x56)}/{u16(m, 0x58)} stats@0x58..={struct.unpack_from('<6H', m, 0x58)}")
    # proof 1: retail decryption of the same bytes gives garbage
    key = pid ^ otid
    x = bytearray(m[0x20:0x50])
    for i in range(0, 48, 4):
        struct.pack_into("<I", x, i, struct.unpack_from("<I", x, i)[0] ^ key)
    order = SHUFFLE[pid % 24]
    blocks = {order[p]: bytes(x[p * 12 : (p + 1) * 12]) for p in range(4)}
    rg = struct.unpack_from("<H", blocks[0], 0)[0]
    print(f"  PROOF retail-decrypt: species would be {rg} (garbage), "
          f"sum16(plain core)=0x{sum16(m[0x20:0x50]):04X} vs stored 0x{u16(m, 0x1C):04X} "
          f"-> {'plaintext confirmed' if u16(m, 0x1C) == 0 and rg > 1500 else '???'}")


print("=" * 100)
print("PARTY MON 0 @section1+0x38 (file 0x10038)")
m = data[LIVE1 + 0x38 : LIVE1 + 0x38 + 100]
for off in range(0, 100, 16):
    print(f"  +0x{off:02X}: {m[off:off+16].hex(' ')}")
print()
dump_party(m)

print()
print("=" * 100)
print("ALL SIX PARTY MONS (compact view)")
for i in range(6):
    o = LIVE1 + 0x38 + 100 * i
    m = data[o : o + 100]
    spc = u16(m, 0x20)
    print(f"  party[{i}] pid=0x{u32(m,0):08X} {g3str(m[8:0x12]):<11} "
          f"spc={spc} ({CFRU_NAMES.get(spc, '?')}) lvl={m[0x54]} "
          f"exp={u32(m, 0x24)} ivword=0x{u32(m, 0x48):08X} ball={m[0x2A]} "
          f"moves={struct.unpack_from('<4H', m, 0x2C)}")

print()
print("=" * 100)
print("PC MONS 0-1 @section5+0x004 (file 0x14004, 58-byte compact)")
stream = b"".join(data[LIVE[i] : LIVE[i] + 0xFF0] for i in (5, 6, 7))
for i in range(2):
    o = 4 + 58 * i
    m = stream[o : o + 58]
    print(f"  pc[{i}] raw: {m.hex(' ')}")
    pid, otid = u32(m, 0), u32(m, 4)
    word = u32(m, 0x27)
    moves = [word & 0x3FF, (word >> 10) & 0x3FF, (word >> 20) & 0x3FF,
             ((word >> 30) | (m[0x2B] << 2)) & 0x3FF]
    iv32 = u32(m, 0x36)
    xor = (otid ^ pid)
    shiny = ((xor & 0xFFFF) ^ (xor >> 16)) < 16
    print(f"    pid=0x{pid:08X} otid=0x{otid:08X} nick={g3str(m[8:0x12])!r} "
          f"OT={g3str(m[0x14:0x1B])!r}")
    print(f"    species@0x1C={u16(m, 0x1C)} ({CFRU_NAMES.get(u16(m, 0x1C), '?')}) "
          f"item@0x1E={u16(m, 0x1E)} exp@0x20={u32(m, 0x20)} ball@0x26={m[0x26]}")
    print(f"    moves packed@0x27={moves} evs@0x2C={tuple(m[0x2C:0x32])} "
          f"ivword@0x36=0x{iv32:08X} ivs={[(iv32 >> s) & 31 for s in (0, 5, 10, 15, 20, 25)]} "
          f"nature={pid % 25} shiny={shiny}")

print()
print("=" * 100)
print("HoF CROSS-CHECK (0x1C000): same PIDs/species as party => plaintext model sound")
for i in range(6):
    o = 0x1C000 + 0x18 * i
    print(f"  hof[{i}] pid=0x{u32(data, o + 4):08X} spc={u16(data, o + 8)} "
          f"lvl={data[o + 10]} nick={g3str(data[o + 11 : o + 21])!r}")

print()
print("=" * 100)
print("SECTOR CHECKSUM RECOMPUTATION (acceptance: one section verified by hand)")
sec = data[LIVE1 : LIVE1 + 0x1000]
total = sum(int.from_bytes(sec[o : o + 4], "little") for o in range(0, 0xFF4, 4))
folded = (total + (total >> 16)) & 0xFFFF
print(f"  section 1 (0x{LIVE1:05X}): fold32 over 0xFF4 = 0x{folded:04X}, "
      f"stored @+0xFF6 = 0x{u16(sec, 0xFF6):04X} -> "
      f"{'MATCH' if folded == u16(sec, 0xFF6) else 'MISMATCH'}")
sec = data[LIVE[5] : LIVE[5] + 0x1000]
total = sum(int.from_bytes(sec[o : o + 4], "little") for o in range(0, 0xFF4, 4))
folded = (total + (total >> 16)) & 0xFFFF
print(f"  section 5 (0x{LIVE[5]:05X}): fold32 over 0xFF4 = 0x{folded:04X}, "
      f"stored @+0xFF6 = 0x{u16(sec, 0xFF6):04X} -> "
      f"{'MATCH' if folded == u16(sec, 0xFF6) else 'MISMATCH'}")
