#!/usr/bin/env python3
"""Builds PKForge's sprite form table: which PokeAPI HOME render / Showdown animation
belongs to each PKHeX (species, form), and which shiny / female variants really exist.

Why: PKHeX numbers forms per species (Charizard form 2 = Mega Y), while the PokeAPI
sprite repo names files by PokeAPI *pokemon id* (10035.png = charizard-mega-y) or by
"<species>-<form identifier>" for cosmetic forms (201-b.png, 666-poke-ball.png). The
two numbering schemes share nothing, so the mapping is derived by name:

  PKHeX FormConverter.GetFormList English names   (tools/SpriteForms/FormDump)
      -> slug -> PokeAPI pokemon_forms.form_identifier   (data/v2/csv, BSD-3-Clause)
      -> file stem that exists in PokeAPI/sprites sprites/pokemon/other/{home,showdown}
         (CC0 / fair-use sprite repo, file listing via the GitHub trees API)

Names that do not slug-match (totems "Large", noble "Lord", Minior "M-Red", Ogerpon
Tera "*Teal", ...) are resolved by the explicit OVERRIDES table below, each one checked
against pokemon_forms.csv. The generator fails loudly if any PKHeX form stays unmapped.

Usage:
    dotnet run --project tools/SpriteForms/FormDump -- /tmp/forms.tsv
    python3 tools/SpriteForms/build.py /tmp/forms.tsv [cache-dir] [output]
        output defaults to src/PKForge.Domain/Resources/spriteforms.tsv

Output format (UTF-8 TSV, sorted, deterministic; see PKForge.Domain.SpriteCatalog):
    # header / attribution
    key  homeStem  homeFlags  showdownStem  showdownFlags
      key:   "<species>-<form>"            regular form
             "<species>-<form>c"           Gen 6 cosplay Pikachu (PKHeX context Gen6)
             "<species>-<form>g"           Gigantamax
             "<species>-<form>-<formarg>"  Alcremie cream + sweet decoration
      stem:  file name without extension, "-" when the art does not exist
      flags: 1 normal, 2 shiny, 4 female, 8 female shiny
"""
import collections
import csv
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CSV_BASE = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/"
TREE_API = "https://api.github.com/repos/PokeAPI/sprites/git/trees/"

# PKHeX form -> PokeAPI pokemon_forms.identifier where the English names differ.
OVERRIDES = {
    # Totem ("Large") forms.
    "20-2": "raticate-totem-alola", "105-2": "marowak-totem", "735-1": "gumshoos-totem",
    "738-1": "vikavolt-totem", "743-1": "ribombee-totem", "752-1": "araquanid-totem",
    "754-1": "lurantis-totem", "758-1": "salazzle-totem", "777-1": "togedemaru-totem",
    "778-2": "mimikyu-totem-disguised", "778-3": "mimikyu-totem-busted", "784-1": "kommo-o-totem",
    # Legends: Arceus nobles look exactly like their Hisuian form.
    "59-2": "arcanine-hisui", "101-2": "electrode-hisui", "549-2": "lilligant-hisui",
    "713-2": "avalugg-hisui", "900-1": "kleavor",
    "201-26": "unown-exclamation", "201-27": "unown-question",
    "493-18": "arceus-normal",  # Legend Arceus (Gen8a event) wears the Normal plate look
    "555-2": "darmanitan-galar-standard", "555-3": "darmanitan-galar-zen",
    "649-1": "genesect-douse", "649-2": "genesect-shock", "649-3": "genesect-burn", "649-4": "genesect-chill",
    # PKHeX Greninja: 1 = Battle Bond (looks normal), 2 = Ash-Greninja (active).
    "658-1": "greninja-battle-bond", "658-2": "greninja-ash",
    "678-0": "meowstic-male", "678-1": "meowstic-female",
    "678-2": "meowstic-male-mega", "678-3": "meowstic-female-mega",
    "876-1": "indeedee-female", "902-1": "basculegion-female", "916-1": "oinkologne-female",
    "710-3": "pumpkaboo-super", "711-3": "gourgeist-super",
    # PokeAPI's default Xerneas is Active; PKHeX form 0 is Neutral.
    "716-0": "xerneas-neutral", "716-1": "xerneas-active",
    "718-2": "zygarde-10-power-construct", "718-3": "zygarde-50-power-construct",
    "741-2": "oricorio-pau", "744-1": "rockruff-own-tempo",
    "801-2": "magearna-mega", "801-3": "magearna-original-mega",
    "875-1": "eiscue-noice",
    "978-3": "tatsugiri-curly-mega", "978-4": "tatsugiri-droopy-mega", "978-5": "tatsugiri-stretchy-mega",
    # Terastallized Ogerpon (forms 4-7) has no separate art: it is the matching mask.
    "1017-4": "ogerpon", "1017-5": "ogerpon-wellspring-mask",
    "1017-6": "ogerpon-hearthflame-mask", "1017-7": "ogerpon-cornerstone-mask",
}
_MINIOR = ["red", "orange", "yellow", "green", "blue", "indigo", "violet"]
for _i in range(1, 7):
    OVERRIDES[f"774-{_i}"] = f"minior-{_MINIOR[_i]}-meteor"
for _i in range(7):
    OVERRIDES[f"774-{7 + _i}"] = f"minior-{_MINIOR[_i]}"

# Gen 6 PKHeX Pikachu forms 1-6 are the ORAS cosplay outfits.
COSPLAY = ["pikachu-rock-star", "pikachu-belle", "pikachu-pop-star", "pikachu-phd", "pikachu-libre", "pikachu-cosplay"]
# Mothim, Scatterbug, Spewpa, Sinistea, Polteageist, Poltchageist, Sinistcha.
IDENTICAL_FORMS = {414, 664, 665, 854, 855, 1012, 1013}
# PKHeX Alcremie FormArgument 0-6 (AlcremieDecoration) in order.
SWEETS = ["strawberry", "berry", "love", "star", "clover", "flower", "ribbon"]
# Newest context first: SV names, then Z-A (megas), then older games' extra forms.
CONTEXT_ORDER = ["Gen9", "Gen9a", "Gen8", "Gen8a", "Gen8b", "Gen7", "Gen7b", "Gen6"]
SUFFIXES = ["", "-cap", "-breed", "-mode", "-build", "-striped", "-plumage", "-mask", "-cream"]
REGIONAL = {"alolan": "alola", "galarian": "galar", "hisuian": "hisui", "paldean": "paldea"}


def fetch(url, path):
    if not os.path.exists(path):
        req = urllib.request.Request(url, headers={"User-Agent": "pkforge-spriteforms"})
        with urllib.request.urlopen(req) as r, open(path, "wb") as f:
            f.write(r.read())
    return path


def listing(cache, kind):
    """Front sprite files of sprites/pokemon/other/<kind> (non-recursive dirs only)."""
    other = json.load(open(fetch("https://api.github.com/repos/PokeAPI/sprites/contents/sprites/pokemon/other",
                                 os.path.join(cache, "other.json"))))
    sha = next(x["sha"] for x in other if x["name"] == kind)
    tree = json.load(open(fetch(TREE_API + sha + "?recursive=1", os.path.join(cache, kind + ".json"))))
    if tree.get("truncated"):
        sys.exit(f"{kind}: GitHub tree listing truncated")
    files = set()
    for entry in tree["tree"]:
        if entry["type"] != "blob":
            continue
        path = entry["path"]
        parts = path.split("/")
        # Keep "<stem>.ext", "shiny/…", "female/…", "shiny/female/…"; drop back sprites.
        if "back" in parts:
            continue
        files.add(path)
    return files


def slug(text):
    text = text.lower().replace("♀", "f").replace("♂", "m").replace("é", "e")
    text = re.sub(r"['.%*’]", "", text)
    return re.sub(r"[^a-z0-9]+", "-", text).strip("-")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    dump = sys.argv[1]
    cache = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "tools", "SpriteForms", ".cache")
    output = sys.argv[3] if len(sys.argv) > 3 else os.path.join(ROOT, "src", "PKForge.Domain", "Resources", "spriteforms.tsv")
    os.makedirs(cache, exist_ok=True)

    pokemon = {r["id"]: r for r in csv.DictReader(open(fetch(CSV_BASE + "pokemon.csv", os.path.join(cache, "pokemon.csv")), encoding="utf-8"))}
    forms_by_species = collections.defaultdict(list)
    form_by_identifier = {}
    for r in csv.DictReader(open(fetch(CSV_BASE + "pokemon_forms.csv", os.path.join(cache, "pokemon_forms.csv")), encoding="utf-8")):
        r["species"] = int(pokemon[r["pokemon_id"]]["species_id"])
        forms_by_species[r["species"]].append(r)
        form_by_identifier[r["identifier"]] = r
    home = listing(cache, "home")
    showdown = listing(cache, "showdown")

    names = {}
    for line in open(dump, encoding="utf-8"):
        species, form, context, _, name = line.rstrip("\n").split("\t")
        names.setdefault((int(species), int(form)), {})[context] = name

    def identify(species, form, name):
        key = f"{species}-{form}"
        if key in OVERRIDES:
            if OVERRIDES[key] not in form_by_identifier:
                sys.exit(f"override {key} -> {OVERRIDES[key]} is not a PokeAPI form")
            return OVERRIDES[key]
        rows = forms_by_species[species]
        by_form_id = {r["form_identifier"]: r for r in rows}
        s = slug(name)
        if s:
            candidates = [s + suffix for suffix in SUFFIXES]
            for regional, short in REGIONAL.items():
                candidates.append(s.replace(regional, short))
            for c in candidates:
                if c in by_form_id:
                    return by_form_id[c]["identifier"]
        if form == 0:
            default = [r for r in rows if r["is_default"] == "1" and pokemon[r["pokemon_id"]]["is_default"] == "1"]
            if default:
                return default[0]["identifier"]
        return None

    def stems_for(identifier):
        r = form_by_identifier[identifier]
        species = r["species"]
        stems = []
        if int(r["pokemon_id"]) != species:
            stems.append(r["pokemon_id"])
        if r["form_identifier"]:
            stems.append(f"{species}-{r['form_identifier']}")
            # Showdown's own spelling drops inner dashes (666-pokeball.gif, 666-highplains.gif).
            if "-" in r["form_identifier"]:
                stems.append(f"{species}-{r['form_identifier'].replace('-', '')}")
        # The species' own file is the exact art only for its default form.
        if int(r["pokemon_id"]) == species and r["is_default"] == "1":
            stems.append(str(species))
        return stems

    def resolve(files, ext, identifier):
        for stem in stems_for(identifier):
            flags = 0
            for bit, prefix in ((1, ""), (2, "shiny/"), (4, "female/"), (8, "shiny/female/")):
                if f"{prefix}{stem}.{ext}" in files:
                    flags |= bit
            if flags & 1:
                return stem, flags
        return "-", 0

    rows = {}
    missing = []

    def emit(key, identifier):
        if identifier is None:
            missing.append(key)
            return
        hs, hf = resolve(home, "png", identifier)
        ss, sf = resolve(showdown, "gif", identifier)
        rows[key] = (hs, hf, ss, sf)

    for (species, form), by_context in sorted(names.items()):
        name = next(by_context[c] for c in CONTEXT_ORDER if c in by_context)
        if species == 869:  # Alcremie: cream (form) x sweet (FormArgument)
            base = slug(name)
            for arg, sweet in enumerate(SWEETS):
                emit(f"{species}-{form}-{arg}", f"alcremie-{base}-{sweet}-sweet")
            identifier = f"alcremie-{base}-strawberry-sweet"
        else:
            identifier = identify(species, form, name)
        emit(f"{species}-{form}", identifier)

        gmax = [r for r in forms_by_species[species] if r["form_identifier"].endswith("gmax")]
        if gmax and identifier:
            own = [r for r in gmax if r["identifier"] == identifier + "-gmax"]
            # A lone G-Max form belongs to the base form only (no G-Max Mega Venusaur),
            # except Alcremie, whose single G-Max covers every cream.
            lone = len(gmax) == 1 and (form == 0 or species == 869)
            pick = own[0] if own else (gmax[0] if lone else None)
            if pick:
                emit(f"{species}-{form}g", pick["identifier"])

    # Forms PKHeX also draws with the base sprite (SpriteName.SpeciesDefaultFormSprite) and
    # PokeAPI ships no separate art for: the wing pattern / authenticity mark is invisible
    # at sprite scale, so the base art IS the exact look. Everything else stays "-".
    for key, row in list(rows.items()):
        species = int(key.split("-")[0])
        if species in IDENTICAL_FORMS and key.endswith(tuple("0123456789")) and not key.endswith("-0"):
            base = rows[f"{species}-0"]
            hs, hf, ss, sf = row
            rows[key] = (hs if hf else base[0], hf or base[1], ss if sf else base[2], sf or base[3])

    for form, identifier in enumerate(COSPLAY, start=1):
        emit(f"25-{form}c", identifier)

    if missing:
        sys.exit("unmapped PKHeX forms: " + " ".join(missing))

    def sort_key(k):
        return [int(p) if p.isdigit() else p for p in re.split(r"(\d+)", k)]

    with open(output, "w", encoding="utf-8", newline="\n") as f:
        f.write("# PKHeX form -> PokeAPI sprite stems. Generated by tools/SpriteForms/build.py; do not edit.\n")
        f.write("# Form data: PokeAPI (https://github.com/PokeAPI/pokeapi) BSD-3-Clause; files: PokeAPI/sprites.\n")
        for key in sorted(rows, key=sort_key):
            hs, hf, ss, sf = rows[key]
            f.write(f"{key}\t{hs}\t{hf}\t{ss}\t{sf}\n")
    print(f"{len(rows)} rows -> {output}")


if __name__ == "__main__":
    main()
