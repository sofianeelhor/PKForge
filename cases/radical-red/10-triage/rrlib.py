"""Shared helpers for the Radical Red .sav structural triage (read-only analysis).

The original save at ~/Desktop/Radical-red-champ.sav is NEVER written to; scripts
copy it to /tmp/rrtriage/save.sav (verifying the known sha256) and work on that.
Format references: PKHeX.Core SAV3*/PK3/PokeCrypto/SpeciesConverter/StringConverter3
and PKForge's own UnboundEngineSession/UnboundFormat (CFRU conventions).
"""
from __future__ import annotations

import hashlib
import re
import shutil
import struct
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
ORIGINAL = Path("/Users/sof/Desktop/Radical-red-champ.sav")
WORKDIR = Path("/tmp/rrtriage")
COPY = WORKDIR / "save.sav"
SHA256 = "c115d09c89b8c800c30a4f53e3d9741a743cc89effac858918368b914104128a"

SIZE = 0x20000      # 128 KiB dumped flash
SECTOR = 0x1000     # 4 KiB sector
N_SECTORS = 32
MAIN_SECTIONS = 14  # section ids 0..13 per slot
SLOT_SIZE = MAIN_SECTIONS * SECTOR  # 0xE000
TRAILER = 0xFF4     # u16 id @+0xFF4, u16 checksum @+0xFF6, u32 @+0xFF8, u32 @+0xFFC

# ---------------------------------------------------------------------------
# save access


def load() -> bytes:
    WORKDIR.mkdir(exist_ok=True)
    ok = COPY.exists() and hashlib.sha256(COPY.read_bytes()).hexdigest() == SHA256
    if not ok:
        shutil.copyfile(ORIGINAL, COPY)  # reads the original, writes only the copy
        got = hashlib.sha256(COPY.read_bytes()).hexdigest()
        assert got == SHA256, f"copy mismatch: {got}"
    return COPY.read_bytes()


def region_label(off: int) -> str:
    if off < SLOT_SIZE:
        return f"slotA[{off // SECTOR:02d}]"
    if off < 2 * SLOT_SIZE:
        return f"slotB[{(off - SLOT_SIZE) // SECTOR:02d}]"
    return f"extra[{(off - 2 * SLOT_SIZE) // SECTOR}]"


def u16(b: bytes, o: int) -> int:
    return struct.unpack_from("<H", b, o)[0]


def u32(b: bytes, o: int) -> int:
    return struct.unpack_from("<I", b, o)[0]


class Sector:
    def __init__(self, data: bytes, index: int):
        self.index = index
        self.off = index * SECTOR
        self.raw = data[self.off : self.off + SECTOR]
        self.section_id = u16(self.raw, 0xFF4)
        self.checksum = u16(self.raw, 0xFF6)
        self.w_ff8 = u32(self.raw, 0xFF8)
        self.w_ffc = u32(self.raw, 0xFFC)

    @property
    def region(self) -> str:
        return region_label(self.off)


def sectors(data: bytes) -> list[Sector]:
    return [Sector(data, i) for i in range(N_SECTORS)]


# ---------------------------------------------------------------------------
# checksum algorithms (candidates verified empirically)


def fold32(data: bytes, length: int) -> int:
    """Retail Gen3 sector checksum: sum LE u32 words, fold high half once."""
    total = 0
    for o in range(0, length & ~3, 4):
        total += struct.unpack_from("<I", data, o)[0]
    total = (total + (total >> 16)) & 0xFFFFFFFF
    return total & 0xFFFF


def u16sum(data: bytes, length: int) -> int:
    total = 0
    for o in range(0, length & ~1, 2):
        total += struct.unpack_from("<H", data, o)[0]
    return total & 0xFFFF


def valid_windows(payload: bytes, stored: int) -> list[int]:
    """All 4-byte-aligned window lengths whose fold32 checksum equals `stored`."""
    prefix = 0
    out = []
    for L in range(4, 0xFF5, 4):
        prefix += struct.unpack_from("<I", payload, L - 4)[0]
        val = ((prefix + (prefix >> 16)) & 0xFFFFFFFF) & 0xFFFF
        if val == stored:
            out.append(L)
    return out


def compact_ranges(values: list[int], step: int = 4) -> str:
    if not values:
        return "-"
    parts = []
    start = prev = values[0]
    for v in values[1:]:
        if v == prev + step:
            prev = v
            continue
        parts.append(f"0x{start:X}" if start == prev else f"0x{start:X}-0x{prev:X}")
        start = prev = v
    parts.append(f"0x{start:X}" if start == prev else f"0x{start:X}-0x{prev:X}")
    return ",".join(parts)


def last_nonzero(payload: bytes, end: int = 0xFF4) -> int:
    """Offset just past the last non-zero byte in payload[:end] (0 if all zero)."""
    for i in range(end - 1, -1, -1):
        if payload[i] != 0:
            return i + 1
    return 0


# ---------------------------------------------------------------------------
# Gen3 text decoding (charmap parsed from PKHeX StringConverter3.G3_EN)


def _load_charmap() -> list:
    src = (
        REPO / "external/PKHeX/PKHeX.Core/PKM/Strings/StringConverter3.cs"
    ).read_text()
    body = src.split("G3_EN =>", 1)[1].split("];", 1)[0]
    names = {"HGM": "\u2642", "HGF": "\u2640", "FGM": "\u2642", "FGF": "\u2640"}
    table: list = []
    i = 0
    while i < len(body) and len(table) < 256:
        c = body[i]
        if c == "'":  # char literal
            if body[i + 1 : i + 2] == "\\":
                lit = body[i + 2]
                table.append({"'": "'", "n": "\n", "t": "\t"}.get(lit, lit))
                i += 4
            else:
                table.append(body[i + 1])
                i += 3
        elif body[i : i + 9] == "Terminator":
            table.append(None)
            i += 9
        elif c == "_" or c.isupper():
            j = i
            while j < len(body) and (body[j].isalnum() or body[j] == "_"):
                j += 1
            tok = body[i:j]
            if tok in names:
                table.append(names[tok])
            i = j
        else:
            i += 1
    while len(table) < 256:
        table.append(None)
    return table


CHARMAP = _load_charmap()


def g3str(raw: bytes) -> str:
    out = []
    for b in raw:
        if b in (0xFF, 0x00):
            break
        c = CHARMAP[b]
        if c is None:
            break
        out.append(c)
    return "".join(out)


# ---------------------------------------------------------------------------
# species ids


def _load_internal_to_national() -> list[int]:
    src = (
        REPO / "external/PKHeX/PKHeX.Core/PKM/Util/Conversion/SpeciesConverter.cs"
    ).read_text()
    body = src.split("Table3InternalToNational =>", 1)[1].split("];", 1)[0]
    return [int(x) for x in re.findall(r"-?\d+", body)]


_DELTA3 = _load_internal_to_national()
_FIRST_UNALIGNED_INTERNAL3 = 277


def national3(raw: int):
    """Gen3 internal species id -> national dex (PKHeX table). None past vanilla."""
    if raw < _FIRST_UNALIGNED_INTERNAL3:
        return raw
    shift = raw - _FIRST_UNALIGNED_INTERNAL3
    if shift >= len(_DELTA3):
        return None
    return raw + _DELTA3[shift]


def _load_cfru_names() -> dict[int, str]:
    path = REPO / "src/PKForge.Engine/Unbound/Data/pokemon.txt"
    names = {}
    for line in path.read_text().splitlines():
        sid, _, name = line.partition(":")
        if sid.strip().isdigit():
            names[int(sid)] = name.strip()
    return names


# Unbound's own CFRU ROM table: id<411 matches vanilla internal order, ids >411
# are CFRU-expanded species (Unbound build; Radical Red's table may differ there).
CFRU_NAMES = _load_cfru_names()


def species_label(raw: int) -> str:
    nat = national3(raw)
    cfru = CFRU_NAMES.get(raw)
    if nat is None:
        return f"raw={raw} (CFRU-expanded; Unbound table: {cfru or '?'})"
    if nat != raw and cfru:
        return f"raw={raw} nat={nat} ({cfru})"
    return f"raw={raw} nat={nat}" + (f" ({cfru})" if cfru else "")


# ---------------------------------------------------------------------------
# Gen3 pokemon (80-byte box / 100-byte party)


# PKHeX PokeCrypto.BlockPosition: row sv = block ids (0=G,1=A,2=E,3=M) in the
# order they are stored at successive 12-byte positions.
SHUFFLE = [
    [0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3], [0, 3, 1, 2],
    [0, 2, 3, 1], [0, 3, 2, 1], [1, 0, 2, 3], [1, 0, 3, 2],
    [2, 0, 1, 3], [3, 0, 1, 2], [2, 0, 3, 1], [3, 0, 2, 1],
    [1, 2, 0, 3], [1, 3, 0, 2], [2, 1, 0, 3], [3, 1, 0, 2],
    [2, 3, 0, 1], [3, 2, 0, 1], [1, 2, 3, 0], [1, 3, 2, 0],
    [2, 1, 3, 0], [3, 1, 2, 0], [2, 3, 1, 0], [3, 2, 1, 0],
]
BLOCK_NAMES = ("Growth", "Attack", "EV/Cond", "Misc")


class Mon:
    """Decrypted view of a Gen3 mon (80 bytes box form, 100 bytes party form)."""

    def __init__(self, raw: bytes):
        self.raw = bytes(raw)
        self.party = len(raw) >= 100
        self.pid = u32(self.raw, 0x00)
        self.otid = u32(self.raw, 0x04)
        self.tid = self.otid & 0xFFFF
        self.sid = self.otid >> 16
        self.nickname = g3str(self.raw[0x08:0x12])
        self.language = self.raw[0x12]
        self.flags = self.raw[0x13]
        self.ot_name = g3str(self.raw[0x14:0x1B])
        self.markings = self.raw[0x1B]
        self.checksum = u16(self.raw, 0x1C)
        self.sanity = u16(self.raw, 0x1E)

        sub = bytearray(self.raw[0x20:0x50])
        key = self.pid ^ self.otid
        for i in range(0, 48, 4):
            struct.pack_into("<I", sub, i, struct.unpack_from("<I", sub, i)[0] ^ key)
        order = SHUFFLE[self.pid % 24]
        blocks: dict[int, bytes] = {}
        for pos, bid in enumerate(order):
            blocks[bid] = bytes(sub[pos * 12 : (pos + 1) * 12])
        self.block_order = [BLOCK_NAMES[b] for b in order]
        g, a, e, m = blocks[0], blocks[1], blocks[2], blocks[3]
        self.species_raw = struct.unpack_from("<H", g, 0)[0]
        self.held_item = struct.unpack_from("<H", g, 2)[0]
        self.exp = struct.unpack_from("<I", g, 4)[0]
        self.pp_ups = g[8]
        self.friendship = g[9]
        self.moves = struct.unpack_from("<4H", a, 0)
        self.move_pp = (a[8], a[9], a[10], a[11])
        self.evs = list(e[0:6])
        self.contest = list(e[6:12])
        self.pokerus = m[0]
        self.met_location = m[1]
        origins = struct.unpack_from("<H", m, 2)[0]
        self.met_level = origins & 0x7F
        self.origin_game = (origins >> 7) & 0xF
        self.ball = (origins >> 11) & 0xF
        self.ot_gender = (origins >> 15) & 1
        iv32 = struct.unpack_from("<I", m, 4)[0]
        self.ivs = [
            (iv32 >> 0) & 0x1F,
            (iv32 >> 5) & 0x1F,
            (iv32 >> 10) & 0x1F,
            (iv32 >> 15) & 0x1F,
            (iv32 >> 20) & 0x1F,
            (iv32 >> 25) & 0x1F,
        ]
        self.is_egg = (iv32 >> 30) & 1 == 1
        self.ability_bit = (iv32 >> 31) & 1
        self.ribbons = struct.unpack_from("<I", m, 8)[0]
        self.data_decrypted = bytes(sub)

        if self.party:
            self.status = u32(self.raw, 0x50)
            self.level = self.raw[0x54]
            self.hp = u16(self.raw, 0x56)
            self.stats = struct.unpack_from("<6H", self.raw, 0x58)
        else:
            self.status = self.level = self.hp = None
            self.stats = None

        calc = 0
        for o in range(0, 48, 2):
            calc += struct.unpack_from("<H", sub, o)[0]
        self.checksum_ok = (calc & 0xFFFF) == self.checksum

    @property
    def is_empty(self) -> bool:
        return self.pid == 0

    @property
    def shiny(self) -> bool:
        xor = self.tid ^ self.sid ^ (self.pid >> 16) ^ (self.pid & 0xFFFF)
        return xor < 8

    @property
    def nature(self) -> str:
        natures = (
            "Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile",
            "Relaxed", "Impish", "Lax", "Timid", "Hasty", "Serious", "Jolly",
            "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash", "Calm",
            "Gentle", "Sassy", "Careful", "Quirky",
        )
        return natures[self.pid % 25]

    def __str__(self) -> str:
        lvl = f"L{self.level}" if self.party else "PC"
        return (
            f"{lvl} {self.nickname or '(no nick)'} spc={species_label(self.species_raw)} "
            f"OT={self.ot_name}/{self.tid:05d}{'/shiny' if self.shiny else ''} "
            f"moves={list(self.moves)} chk={'OK' if self.checksum_ok else 'BAD'}"
        )


def reencrypt(mon: "Mon") -> bytes:
    """Re-encrypt a decrypted mon; proves the crypt model round-trips exactly."""
    out = bytearray(mon.raw)
    sub = bytearray(mon.data_decrypted)
    order = SHUFFLE[mon.pid % 24]
    packed = bytearray(48)
    for pos, bid in enumerate(order):
        packed[pos * 12 : (pos + 1) * 12] = sub[bid * 12 : (bid + 1) * 12]
    key = mon.pid ^ mon.otid
    for i in range(0, 48, 4):
        struct.pack_into("<I", packed, i, struct.unpack_from("<I", packed, i)[0] ^ key)
    out[0x20:0x50] = packed
    return bytes(out[:80])
