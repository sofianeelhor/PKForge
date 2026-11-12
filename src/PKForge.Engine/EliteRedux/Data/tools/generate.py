"""Generates PKForge's Elite Redux data pack from Elite-Redux/er-config (proto submodule of eliteredux-source)."""
import json, re, sys, subprocess

P = sys.argv[1].rstrip('/') + '/'
OUT = sys.argv[2]
SRC_REV = sys.argv[3]

def enum(f):
    m = {}
    for line in open(P + f, encoding='utf-8'):
        r = re.match(r'\s*(\w+)\s*=\s*(\d+);', line)
        if r: m[r.group(1)] = int(r.group(2))
    return m

SP = enum('SpeciesEnum.proto'); MV = enum('MoveEnum.proto'); IT = enum('ItemEnum.proto'); AB = enum('AbilityEnum.proto')

def unescape(s):
    # textproto octal escapes are UTF-8 bytes
    b = bytearray(); i = 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            n = s[i+1]
            if n in '01234567':
                b.append(int(s[i+1:i+4], 8)); i += 4; continue
            b.extend({'n': b'\n', 't': b'\t', '"': b'"', "'": b"'", '\\': b'\\'}.get(n, n.encode())); i += 2; continue
        b.extend(c.encode('utf-8')); i += 1
    return b.decode('utf-8')

def top_blocks(text, key):
    """Top-level `key { ... }` blocks, as their body text (depth 1 lines only kept by caller)."""
    out = []; depth = 0; cur = None
    for line in text.split('\n'):
        s = line.strip()
        if depth == 0 and s == key + ' {':
            cur = []; depth = 1; continue
        if cur is None: continue
        opens = s.count('{'); closes = s.count('}')
        # ignore braces inside quoted strings
        q = re.sub(r'"(\\.|[^"\\])*"', '', s)
        opens = q.count('{'); closes = q.count('}')
        if depth == 1: cur.append(s)
        else: cur.append(None)
        depth += opens - closes
        if depth == 0:
            out.append([x for x in cur[:-1] if x is not None]); cur = None
    return out

def fields(lines):
    d = {}
    for s in lines:
        m = re.match(r'(\w+): (.*)$', s)
        if m:
            v = m.group(2)
            if v.startswith('"'): v = unescape(v[1:-1])
            d.setdefault(m.group(1), []).append(v)
    return d

TYPES = ['NORMAL', 'FIGHTING', 'FLYING', 'POISON', 'GROUND', 'ROCK', 'BUG', 'GHOST', 'STEEL', 'FIRE', 'WATER', 'GRASS',
         'ELECTRIC', 'PSYCHIC', 'ICE', 'DRAGON', 'DARK', 'FAIRY']
def typ(t):
    t = t.removeprefix('TYPE_')
    return TYPES.index(t) if t in TYPES else -1

REGION = {'ALOLAN': 'Alola', 'GALARIAN': 'Galar', 'HISUIAN': 'Hisui', 'PALDEAN': 'Paldea'}
def form_label(sid, base):
    suffix = sid[len(base) + 1:] if sid.startswith(base + '_') else sid.removeprefix('SPECIES_')
    return '-'.join(REGION.get(tok, tok.capitalize()) for tok in suffix.split('_') if tok)

text = open(P + 'SpeciesList.textproto', encoding='utf-8').read()
dex_raw = {}
species = {}
for body in top_blocks(text, 'species'):
    f = fields(body)
    if 'id' not in f: continue
    sid = f['id'][0]
    species[sid] = f
# dex blocks are nested: parse names separately
for blk in text.split('\nspecies {')[1:]:
    m = re.search(r'^  id: (SPECIES_\w+)', blk, re.M)
    if not m: continue
    d = re.search(r'^  dex \{\n(.*?)\n  \}', blk, re.M | re.S)
    if d:
        name = re.search(r'^    name: "(.*)"', d.group(1), re.M)
        nat = re.search(r'^    national_dex_num: (\d+)', d.group(1), re.M)
        dex_raw[m.group(1)] = (unescape(name.group(1)) if name else '', int(nat.group(1)) if nat else 0)

def base_of(sid, seen=()):
    f = species.get(sid)
    if f and 'form_of' in f and f['form_of'][0] != sid and sid not in seen:
        return base_of(f['form_of'][0], seen + (sid,))
    return sid

out_species = []
for sid, f in species.items():
    if sid not in SP or SP[sid] == 0 or sid == 'SPECIES_EGG': continue
    hidden = f.get('randomizer_banned', [''])[0] == 'SPECIES_HIDDEN'
    base = base_of(sid)
    name, nat = dex_raw.get(base, ('', 0))
    form = '' if base == sid else form_label(sid, base)
    g = lambda k: int(f[k][0]) if k in f else 0
    stats = [g('hp'), g('atk'), g('def'), g('spatk'), g('spdef'), g('spe')]
    t1 = f.get('type', ['TYPE_NONE'])[0]; t2 = f.get('type2', ['TYPE_NONE'])[0]
    types = [] if t1 == 'TYPE_NONE' else [typ(t1), typ(t2 if t2 != 'TYPE_NONE' else t1)]
    abil = [AB[a] for a in f.get('ability', [])]
    abil = (abil + [abil[-1]] * (3 - len(abil))) if abil else [0, 0, 0]
    innates = [AB[a] for a in f.get('innate', [])]
    has_lines = any(s > 0 for s in stats) or t1 != 'TYPE_NONE' or f.get('ability') or f.get('innate')
    if 'genderless' in f and f['genderless'][0] == 'true': gender = 255
    else:
        pf = float(f['percent_female'][0]) if 'percent_female' in f else 0.0
        gender = 254 if pf >= 100 else 0 if pf <= 0 else min(254, int((pf * 255) / 100))
    if not has_lines: gender = 0  # gBaseStats entry left zeroed by the generator
    rec = dict(id=SP[sid], c=sid.removeprefix('SPECIES_'), name=name, form=form, nat=nat if 0 < nat <= 1025 else 0,
               dex=nat, stats=stats, types=types, abilities=abil, innates=innates, gender=gender)
    if hidden: rec['hidden'] = True
    out_species.append(rec)
out_species.sort(key=lambda r: r['id'])

moves = []
for body in top_blocks(open(P + 'MoveList.textproto', encoding='utf-8').read(), 'moves'):
    f = fields(body)
    if 'id' not in f or f['id'][0] not in MV: continue
    moves.append(dict(id=MV[f['id'][0]], name=f.get('name', [''])[0], pp=int(f.get('pp', ['0'])[0])))
moves.sort(key=lambda r: r['id'])

items = []
import glob, os
for path in sorted(glob.glob(P + 'items/*.textproto')):
    for body in top_blocks(open(path, encoding='utf-8').read(), 'item'):
        f = fields(body)
        if 'id' not in f or f['id'][0] not in IT: continue
        items.append(dict(id=IT[f['id'][0]], name=f.get('name', [''])[0], pocket=f.get('grouping', ['POCKET_NONE'])[0].removeprefix('POCKET_'),
                          bag=bool(f.get('image') and f.get('palette'))))
items.sort(key=lambda r: r['id'])

abilities = []
for body in top_blocks(open(P + 'AbilityList.textproto', encoding='utf-8').read(), 'ability'):
    f = fields(body)
    if 'id' not in f or f['id'][0] not in AB: continue
    abilities.append(dict(id=AB[f['id'][0]], name=f.get('name', [''])[0]))
abilities.sort(key=lambda r: r['id'])

rev = subprocess.check_output(['git', '-C', P, 'rev-parse', 'HEAD']).decode().strip()
pack = dict(source=dict(repository='https://github.com/Elite-Redux/eliteredux-source', revision=SRC_REV,
                        config='https://github.com/Elite-Redux/er-config', configRevision=rev, saveVersion=1049),
            maxSpecies=max(SP.values()), maxMove=max(MV.values()), maxItem=max(IT.values()), maxAbility=max(AB.values()),
            species=out_species, moves=moves, items=items, abilities=abilities)
with open(OUT, 'w', encoding='utf-8') as fh:
    json.dump(pack, fh, ensure_ascii=False, separators=(',', ':'))
print(len(out_species), len(moves), len(items), len(abilities), pack['maxSpecies'], pack['maxMove'], pack['maxItem'], pack['maxAbility'])
