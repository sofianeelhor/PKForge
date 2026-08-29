#!/usr/bin/env python3
"""Packs projectpokemon/EventsGallery wondercards into per-generation bundles.

Usage: python3 pack.py <EventsGallery checkout> <output directory>

Only Released/ gift files are packed (Unreleased and non-gift media stay out).
Bundle format (events-gN.bin.gz): gzip of [u32 count] then per file
[u16 name length][utf-8 relative path][u32 length][raw gift bytes].
The app extracts the matching generation at runtime and feeds the folder to
PKHeX's EncounterEvent.RefreshMGDB, merging it with the embedded database.
"""
import gzip, os, struct, sys

GENS = {
    4: {".wc4", ".pcd", ".pgt"},
    5: {".pgf"},
    6: {".wc6", ".wc6full"},
    7: {".wc7", ".wc7full", ".wb7", ".wb7full"},
    8: {".wc8", ".wc8full", ".wb8", ".wa8"},
    9: {".wc9", ".wa9"},
}

def pack(gallery: str, out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    for gen, exts in GENS.items():
        root = os.path.join(gallery, "Released", f"Gen {gen}")
        if not os.path.isdir(root):
            print(f"gen {gen}: missing, skipped")
            continue
        files = []
        for base, _, names in os.walk(root):
            for name in names:
                if os.path.splitext(name)[1].lower() in exts:
                    files.append(os.path.join(base, name))
        files.sort()
        out = os.path.join(out_dir, f"events-g{gen}.bin.gz")
        with gzip.open(out, "wb", compresslevel=9) as gz:
            gz.write(struct.pack("<I", len(files)))
            for path in files:
                rel = os.path.relpath(path, root).encode("utf-8")
                data = open(path, "rb").read()
                gz.write(struct.pack("<H", len(rel)))
                gz.write(rel)
                gz.write(struct.pack("<I", len(data)))
                gz.write(data)
        print(f"gen {gen}: {len(files)} gifts -> {out} ({os.path.getsize(out)//1024} KB)")

if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    pack(sys.argv[1], sys.argv[2])
