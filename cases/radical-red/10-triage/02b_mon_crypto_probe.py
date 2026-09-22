#!/usr/bin/env python3
"""Step 2b - probe the mon substructure crypto used by Radical Red.

Party headers (pid/otid/nick/ot/level) sit at vanilla offsets, but the 0x20..0x50
blob does not decrypt under vanilla Gen3 rules. Try candidate keystreams and
layouts to find what this build actually uses.
"""
import struct

from rrlib import Mon, SHUFFLE, g3str, load, u16, u32

data = load()
LIVE1 = 0x10000  # section id 1 in newer slot B
raw = data[LIVE1 + 0x38 : LIVE1 + 0x38 + 100]
print("party[0] full 100 bytes:")
for off in range(0, 100, 16):
    print(f"  +0x{off:02X}: {raw[off:off+16].hex(' ')}")
pid = u32(raw, 0)
otid = u32(raw, 4)
print(f"\npid=0x{pid:08X} otid=0x{otid:08X} tid={otid & 0xFFFF} sid={otid >> 16}")
print(f"nick={g3str(raw[8:0x12])!r} ot={g3str(raw[0x14:0x1B])!r} "
      f"chk@0x1C=0x{u16(raw, 0x1C):04X} sanity@0x1E=0x{u16(raw, 0x1E):04X}")
print(f"flags@0x13=0x{raw[0x13]:02X} (bit1 hasSpecies) lang@0x12={raw[0x12]}")
print(f"party tail: level@0x54={raw[0x54]} hp@0x56={u16(raw, 0x56)} max@0x58={u16(raw, 0x58)}")

blob = raw[0x20:0x50]
print(f"\nblob[0x20..0x50]: {blob.hex(' ')}")


def sum16(b: bytes) -> int:
    return sum(struct.unpack_from("<H", b, o)[0] for o in range(0, len(b), 2)) & 0xFFFF


stored = u16(raw, 0x1C)
print(f"\nstored substructure checksum: 0x{stored:04X}")

print("\n-- candidate decryptions (compare sum16 of decrypted blob vs stored) --")
cands = {}
# 1. vanilla: XOR u32 with pid^otid, then unshuffle by pid%24
key = pid ^ otid
x = bytearray(blob)
for i in range(0, 48, 4):
    struct.pack_into("<I", x, i, struct.unpack_from("<I", x, i)[0] ^ key)
cands["vanilla xor + shuffle"] = bytes(x)
# 2. plaintext, no crypto
cands["plaintext"] = blob
# 3. incrementing keystream seed+i
x = bytearray(blob)
for i, o in enumerate(range(0, 48, 4)):
    struct.pack_into("<I", x, o, struct.unpack_from("<I", x, o)[0] ^ ((key + i) & 0xFFFFFFFF))
cands["xor seed+i"] = bytes(x)
# 4. lcg keystream (gen4 style) seeded by checksum
chk = stored
x = bytearray(blob)
s = chk
for o in range(0, 48, 4):
    s = (0x41C64E6D * s + 0x6073) & 0xFFFFFFFF
    struct.pack_into("<I", x, o, struct.unpack_from("<I", x, o)[0] ^ s)
cands["lcg from chk"] = bytes(x)

for name, dec in cands.items():
    order = SHUFFLE[pid % 24]
    # unshuffle: position p holds block order[p]
    blocks = {order[p]: dec[p * 12 : (p + 1) * 12] for p in range(4)}
    g = blocks[0]
    print(f"  {name:24s} sum16(dec)={sum16(dec):#06x} growth={g.hex(' ')}")
    if sum16(dec) == stored:
        print("      *** CHECKSUM MATCH ***")

print("\n-- also try: shuffle with different sv derivations on vanilla-xor --")
base = cands["vanilla xor + shuffle"]
for sv_name, sv in [("pid%24", pid % 24), ("pid>>16 %24", (pid >> 16) % 24),
                    ("(pid&0xFFFF)%24", (pid & 0xFFFF) % 24), ("otid^pid %24", (pid ^ otid) % 24)]:
    order = SHUFFLE[sv]
    blocks = {order[p]: base[p * 12 : (p + 1) * 12] for p in range(4)}
    g = blocks[0]
    spc = struct.unpack_from("<H", g, 0)[0]
    print(f"  sv={sv_name:15s} growth={g.hex(' ')} species_raw={spc}")
