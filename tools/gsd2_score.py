"""Offline scorer for Suikoden 2 camp (a0v0m0) walk dumps. Reads game-derived
DUMPS from outside the repo; contains no game data itself.

Usage:
    python tools/gsd2_score.py <session-dir> [<session-dir> ...] <subject-dir>

Each dir holds a suikoden2_walk_a0v0m0.txt. The LAST dir is the SUBJECT session:
its [SLIDE] and [ENTITIES] are what get scored. [WALKED], [EDGES] and [BLOCKED]
are unioned across all dirs, because terrain is static; the otyp=4 score is
reported against the subject session's own [WALKED] as well, because props are
static within a session and NOT necessarily across sessions (measured: otyp=4 at
(60,74) sat on a cell walked in an earlier session).

Two scores, both about POSITIONS rather than fields (pathfinding handoff §0):
  1. [SLIDE] - retired in v64; kept so old dumps can be re-scored. Test 1: no
     cardinal claim may land on a walked cell. Test 2: no (cell, dir) claim may
     appear as an observed step. Test 2 is what refuted both formulations.
  2. otyp=4 rectangle - the TATEMONO method. Sweep the anchor, keep only anchors
     with ZERO walked violations, then rank by coverage of cardinal-refused
     cells. Zero violations alone proves nothing: a rule that blocks nothing
     also scores zero. Uses fine= EVENT_POS when the dump has it (v64+).
     DEAD as of 2026-09-28 (all 12 sized, 0 of 1458 anchors walk-clean even
     against the subject session alone); kept so the result can be re-run.
  3. wallOrg label - is wallOrg=255 at a refusal the ENTITY channel, and
     wallOrg set the TERRAIN channel? Per session, with distance to the
     displayed otyp=2 people and the same walked/step contradiction tests.

Typical camp run, oldest first, newest (subject) last. ~ is expanded by the
script, so the same line works in PowerShell and bash:
    python tools/gsd2_score.py ~/Desktop/Suikoden-Access-groundtruth/2026-09-21 ~/Desktop/Suikoden-Access-groundtruth/2026-09-21-v59-session ~/Desktop/Suikoden-Access-groundtruth/2026-09-23-v62-session ~/Desktop/Suikoden-Access-groundtruth/<new-session-folder>
"""
import re, io, os, sys, collections, itertools

DIR = {0: (0, 1), 1: (0, -1), 2: (-1, 0), 3: (1, 0),
       11: (-1, 1), 12: (1, 1), 13: (-1, -1), 14: (1, -1)}
CARD = {0, 1, 2, 3}


def sections(path):
    out, sec = collections.defaultdict(list), None
    for l in io.open(path, encoding='utf-8', errors='replace').read().splitlines():
        if l.startswith('['):
            sec = l.split(']')[0][1:]
            out[sec].append(l)
            continue
        if sec and not l.startswith('#'):
            out[sec].append(l)
    return out


def cells_in(lines):
    s = set()
    for l in lines[1:]:
        for m in re.finditer(r'\((\d+),(\d+)\)', l):
            s.add((int(m.group(1)), int(m.group(2))))
    return s


def edges_in(lines):
    s = set()
    for l in lines[1:]:
        for m in re.finditer(r'\((\d+),(\d+)\)>\((\d+),(\d+)\)', l):
            s.add(((int(m.group(1)), int(m.group(2))), (int(m.group(3)), int(m.group(4)))))
    return s


def blocked_in(lines):
    rows = []
    for l in lines[1:]:
        m = re.match(r'\s*x(\d+)\s+\((\d+),(\d+)\).*?blockedDir=(\d+)', l)
        if m:
            e = l.split('| ent=')[1].strip() if '| ent=' in l else None
            rows.append(dict(pos=(int(m.group(2)), int(m.group(3))), d=int(m.group(4)), ent=e, raw=l))
    return rows


def slide_in(lines):
    rows = []
    for l in lines[1:]:
        m = re.match(r'\s*x(\d+)\s+\((\d+),(\d+)\) refusedDir=(\d+) wouldEnter=\S+ slidTo=(\d+) cur=(\d+) moving=\((\S+),(\S+)\)', l)
        if m:
            rows.append(dict(n=int(m.group(1)), pos=(int(m.group(2)), int(m.group(3))), d=int(m.group(4)),
                             sub=int(m.group(5)), cur=int(m.group(6)), mv=(m.group(7), m.group(8)),
                             ent=l.split('| ent=')[1].strip() if '| ent=' in l else None))
    return rows


def step(p, d):
    return (p[0] + DIR[d][0], p[1] + DIR[d][1])


def dump_path(arg):
    # PowerShell does not expand ~ for a native program, so do it here. A path
    # to the dump file itself is accepted as well as its folder.
    p = os.path.expanduser(arg)
    if os.path.isdir(p):
        p = os.path.join(p, 'suikoden2_walk_a0v0m0.txt')
    if not os.path.isfile(p):
        sys.exit('no walk dump at %s - check the folder name, and that O was '
                 'pressed in game before quitting' % p)
    return p


dirs = sys.argv[1:]
if not dirs:
    sys.exit(__doc__)
walk = [sections(dump_path(d)) for d in dirs]
subj = walk[-1]

W, E, B = set(), set(), []
for s in walk:
    W |= cells_in(s['WALKED'])
    E |= edges_in(s['EDGES'])
    B += blocked_in(s['BLOCKED'])
Wsub, Esub = cells_in(subj['WALKED']), edges_in(subj['EDGES'])
print('UNION over %d sessions: %d walked, %d edges, %d blocked rows' % (len(walk), len(W), len(E), len(B)))
print('subject session: %d walked, %d edges' % (len(Wsub), len(Esub)))

# ── baseline: how often CAN a (walked cell, dir) claim be contradicted at all? ──
def exposure(W, E, dset):
    n = hit = 0
    for p in W:
        for d in dset:
            n += 1
            hit += (p, step(p, d)) in E
    return hit, n

for name, dset in (('cardinal', CARD), ('diagonal', set(DIR) - CARD)):
    h, n = exposure(W, E, dset)
    print('  base rate, %s: %d of %d (walked cell, dir) pairs have an observed step = %.1f%%' % (name, h, n, 100.0 * h / n))

# ── 1. THE SLIDE REPAIR ──────────────────────────────────────────────────────
S = slide_in(subj.get('SLIDE', []))
if not S:
    print()
    print('[SLIDE] no rows in subject session (retired as of v64) - slide scoring skipped')
print()
print('=== [SLIDE] v61 repair: %d rows in subject session ===' % len(S))
claims = collections.OrderedDict()
for r in S:
    claims.setdefault((r['pos'], r['d']), []).append(r)
print('distinct (pos, refusedDir) claims: %d   dirs: %s' % (
    len(claims), dict(collections.Counter(k[1] for k in claims))))
print('substitutions: %s' % dict(collections.Counter('%d>%d' % (r['d'], r['sub']) for r in S)))
print('cur==sub on every row: %s' % all(r['cur'] == r['sub'] for r in S))

bad_walk, bad_edge_any, bad_edge_own, info_diag_walked = [], [], [], []
for (p, d), rs in claims.items():
    q = step(p, d)
    if d in CARD and q in W: bad_walk.append((p, d))
    if d not in CARD and q in W: info_diag_walked.append((p, d))
    if (p, q) in E: bad_edge_any.append((p, d))
    if (p, q) in Esub: bad_edge_own.append((p, d))
print('TEST 1 walked: cardinal claims landing on a walked cell: %d of %d cardinal claims'
      % (len(bad_walk), sum(1 for k in claims if k[1] in CARD)))
print('       (diagonal claims whose destination is walked - NOT a violation of an edge claim: %d of %d)'
      % (len(info_diag_walked), sum(1 for k in claims if k[1] not in CARD)))
print('TEST 2 edges: claims contradicted by an observed step in exactly that direction out of that cell:')
print('       any session: %d of %d      same session: %d of %d' % (len(bad_edge_any), len(claims), len(bad_edge_own), len(claims)))
for c in bad_edge_any: print('         CONTRADICTED %s dir=%d' % c)

# control: [BLOCKED] under the same test
bk = set((r['pos'], r['d']) for r in B)
bc = [k for k in bk if (k[0], step(*k)) in E]
print('CONTROL [BLOCKED] same test 2: %d of %d contradicted' % (len(bc), len(bk)))

# expected contradictions if slide claims were random diagonal claims at walked cells
h, n = exposure(W, E, set(DIR) - CARD)
nd = sum(1 for k in claims if k[1] not in CARD)
print('EXPECTED contradictions if these %d diagonal claims were RANDOM: %.1f  (observed %d)'
      % (nd, nd * h / n, len(bad_edge_any)))
# a sharper null: random diagonal claims drawn only at cells where a slide happened
h2 = n2 = 0
for (p, d) in claims:
    for dd in set(DIR) - CARD:
        n2 += 1; h2 += (p, step(p, dd)) in E
if n2: print('   sharper null, any diagonal at the SAME cells: %.1f%% have an observed step -> expect %.1f' % (100.0*h2/n2, nd*h2/n2))

# agreement with the referee
agree = [k for k in claims if k in bk]
print('claims also present in [BLOCKED] (same pos, same dir): %d of %d' % (len(agree), len(claims)))

# geometric plausibility: a diagonal refused in fully open ground would be odd
open3 = []
for (p, d) in claims:
    if d in CARD: continue
    dx, dy = DIR[d]
    trio = [(p[0] + dx, p[1]), (p[0], p[1] + dy), (p[0] + dx, p[1] + dy)]
    if all(c in W for c in trio): open3.append((p, d))
bopen = []
for (p, d) in bk:
    if d in CARD: continue
    dx, dy = DIR[d]
    if all(c in W for c in [(p[0] + dx, p[1]), (p[0], p[1] + dy), (p[0] + dx, p[1] + dy)]): bopen.append((p, d))
print('diagonal claims whose destination AND both orthogonals are walked (refusal in open ground): '
      'slide %d of %d, blocked %d of %d' % (len(open3), nd, len(bopen), sum(1 for k in bk if k[1] not in CARD)))
for c in open3: print('         OPEN-GROUND %s dir=%d' % c)

# what does the slide oracle ADD? new blocked edges not in [BLOCKED]
new = [k for k in claims if k not in bk]
print('NEW directed edges the slide set adds beyond [BLOCKED]: %d' % len(new))

# ── 2. THE otyp=4 RECTANGLE ─────────────────────────────────────────────────
# Scored against the SUBJECT session's walked cells: a disp=1 prop at (60,74)
# sat on a cell walked in an earlier session and not in its own, so props are
# static within a session and not necessarily across sessions.
print()
print('=== otyp=4 rectangle ===')
ents = {}   # px anchor -> dict(w, h in EVENT_POS, fx, fy fine EVENT_POS or None, disp, extra)
src = 'ENTITIES'
if subj.get('ENTITIES') and any('otyp=4' in l for l in subj['ENTITIES']):
    for l in subj['ENTITIES'][1:]:
        m = re.match(r'\s*ehuman\[\s*\d+\] otyp=4\s+disp=(\S+)\s+px=\((\d+),(\d+)\)(?: fine=\(([\d.]+),([\d.]+)\))? wh=(\d+)x(\d+)(.*)', l)
        if m:
            ents[(int(m.group(2)), int(m.group(3)))] = dict(
                w=int(m.group(6)), h=int(m.group(7)), disp=m.group(1),
                fx=float(m.group(4)) if m.group(4) else None, fy=float(m.group(5)) if m.group(5) else None,
                extra=m.group(8).strip())
else:
    src = 'NPC WALKED + ent= annotations (PRELIMINARY - no otyp=4 in [ENTITIES])'
    cur = None
    for l in subj['NPC WALKED'][1:]:
        m = re.match(r'\s*otyp=(\d+) disp=(\S+):', l)
        if m: cur = (m.group(1), m.group(2)); continue
        if cur and cur[0] == '4':
            for mm in re.finditer(r'\((\d+),(\d+)\)', l):
                ents[(int(mm.group(1)), int(mm.group(2)))] = dict(w=None, h=None, disp=cur[1], fx=None, fy=None, extra='')
    rows = [(r['pos'], r['d'], r['ent']) for s in walk for r in blocked_in(s['BLOCKED'])]
    rows += [(r['pos'], r['d'], r['ent']) for r in slide_in(subj.get('SLIDE', []))]
    for pos, d, ent in rows:
        if not ent: continue
        ref = step(pos, d)
        for m in re.finditer(r'\[otyp=4@(ON|[+-]?\d+,[+-]?\d+) disp=\S+ wh=(\d+)x(\d+)\]', ent):
            off = (0, 0) if m.group(1) == 'ON' else tuple(int(v) for v in m.group(1).split(','))
            ep = (ref[0] + off[0], ref[1] + off[1])
            if ep in ents: ents[ep].update(w=int(m.group(2)), h=int(m.group(3)))
print('source: %s' % src)
for p, e in sorted(ents.items()):
    print('  otyp4 px=%s fine=%s wh=%s disp=%s anchorWalked: this session %s, any session %s  %s'
          % (p, '?' if e['fx'] is None else '(%g,%g)' % (e['fx'], e['fy']),
             '?' if e['w'] is None else '%dx%d' % (e['w'], e['h']), e['disp'], p in Wsub, p in W, e['extra']))
sized = {p: e for p, e in ents.items() if e['w'] is not None}
print('sized: %d of %d' % (len(sized), len(ents)))

refC = set(step(r['pos'], r['d']) for r in B if r['d'] in CARD)
refD = set(step(r['pos'], r['d']) for r in B if r['d'] not in CARD) - refC
print('refused cells: cardinal %d, diagonal-only %d' % (len(refC), len(refD)))


def rect_cells(e, p, ax, ay, mode):
    """Cells a w x h EVENT_POS rectangle blocks. Its top-left is the entity's
    position plus (ax, ay) EVENT_POS. The position is fine= when recorded, else
    the px anchor's origin (8*px). mode 'overlap': a cell is solid if its 8x8
    square overlaps the rect; 'origin': if the cell origin (where the player
    stands when stopped - every [BLOCKED] row has dx=dy=0) lies inside it."""
    ox = (e['fx'] if e['fx'] is not None else 8 * p[0]) + ax
    oy = (e['fy'] if e['fy'] is not None else 8 * p[1]) + ay
    x1, y1 = ox + e['w'], oy + e['h']
    out = set()
    for cx in range(int(ox // 8) - 1, int(x1 // 8) + 2):
        for cy in range(int(oy // 8) - 1, int(y1 // 8) + 2):
            if mode == 'overlap':
                hit = cx * 8 < x1 and cx * 8 + 8 > ox and cy * 8 < y1 and cy * 8 + 8 > oy
            else:
                hit = ox <= cx * 8 < x1 and oy <= cy * 8 < y1
            if hit: out.add((cx, cy))
    return out


for label, WW in (('THIS session walked', Wsub), ('ALL sessions walked (reference only)', W)):
    res = []
    # anchor offsets relative to the rect's own size (left/centre/right, top/centre/bottom)
    # plus a shift in 4-unit steps: covers "sprite anchored at its base", centred, and corners.
    for kx in (0, 0.5, 1):
        for ky in (0, 0.5, 1):
            for sx in range(-16, 17, 4):
                for sy in range(-16, 17, 4):
                    for mode in ('overlap', 'origin'):
                        solid = set()
                        for p, e in sized.items():
                            solid |= rect_cells(e, p, -kx * e['w'] + sx, -ky * e['h'] + sy, mode)
                        res.append((len(solid & WW), -len(solid & refC), -len(solid & refD),
                                    kx, ky, sx, sy, mode, len(solid)))
    zero = sorted(r for r in res if r[0] == 0)
    best = max((-r[1] for r in zero), default=0)
    print()
    print('[%s] %d anchors swept, %d with ZERO walked violations; best cardinal coverage at zero: %d of %d'
          % (label, len(res), len(zero), best, len(refC)))
    for r in zero[:8]:
        print('   covC=%d covDiagOnly=%d  anchor k=(%s,%s) shift=(%+d,%+d) %s cells=%d'
              % (-r[1], -r[2], r[3], r[4], r[5], r[6], r[7], r[8]))
    print('   ties at best coverage: %d' % sum(1 for r in zero if -r[1] == best))

# ── 3. wallOrg AS AN ENTITY/TERRAIN LABEL ────────────────────────────────────
# Measured 2026-09-28 over four camp sessions: every [BLOCKED] row with
# wallOrg=255 (and wallMove set) sits within Chebyshev 2 of one of the three
# DISPLAYED otyp=2 entities, and none anywhere else. Rows with wallOrg set are
# spread over the whole map. The one Shift+O reading at an entity refusal said
# the map is open there; the one at a terrain refusal agreed with all four rows.
# Reported per session so a counterexample shows up where it happened.
print()
print('=== wallOrg label: 255 = entity channel, set = terrain channel ===')
peo = []
for l in subj.get('ENTITIES', [])[1:]:
    m = re.match(r'\s*ehuman\[\s*\d+\] otyp=2\s+disp=1\s+px=\((\d+),(\d+)\)', l)
    if m: peo.append((int(m.group(1)), int(m.group(2))))
print('displayed otyp=2 in the subject session: %s' % (peo or 'none recorded - distances skipped'))


def worg(r):
    m = re.search(r'wallOrg=(\d+)', r['raw'])
    return int(m.group(1)) if m else None


for i, s in enumerate(walk):
    rows = blocked_in(s['BLOCKED'])
    Ws, Es = cells_in(s['WALKED']), edges_in(s['EDGES'])
    unread = sum(1 for r in rows if worg(r) is None)
    line = 'session %d: %d rows, unreadable wallOrg %d' % (i + 1, len(rows), unread)
    for lab, sel in (('entity', lambda r: worg(r) == 255), ('terrain', lambda r: worg(r) not in (None, 255))):
        rs = [r for r in rows if sel(r)]
        card = [r for r in rs if r['d'] in CARD]
        walked = sum(1 for r in card if step(r['pos'], r['d']) in Ws)
        stepped = sum(1 for r in rs if (r['pos'], step(r['pos'], r['d'])) in Es)
        dist = collections.Counter()
        if peo:
            for r in rs:
                t = step(r['pos'], r['d'])
                dist[min(3, min(max(abs(t[0] - c[0]), abs(t[1] - c[1])) for c in peo))] += 1
        line += ('\n   %-7s %3d rows (%3d cardinal): cardinal refused cell walked %d, step observed %d; '
                 'dist to displayed otyp=2 (3 = 3+): %s' % (lab, len(rs), len(card), walked, stepped,
                                                             sorted(dist.items()) if peo else '-'))
    print(line)
