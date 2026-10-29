"""
경로 추천 검사: 무작위 요청 N개를 로컬 서버 POST /api/recommendations 에 보내고, 앱과 같은 방식
(RoutePlanner.MakeOption · Trace · 시간 넘으면 줄이기 · AddRest, WalkGraph A*)으로 길을 이어
막힌 구역 · 막힌 길(tools/route/marks.json) · 출입 제한 · 중복 · 시간을 검사한다.

    KUCA_SERVER=http://127.0.0.1:5299 N=1000 SEED=1000 python3 tools/route/check_routes.py out.json
    (운영 서버에는 보내지 말 것. GRAPH=<옛 WalkGraph.json> 으로 검사기가 위반을 잡는지 볼 수 있다)
"""
import os, json, sys, random, time, heapq, urllib.request, urllib.error, collections
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'keyart'))
from kuca_art.geo import MAP_W, MAP_H, to_world
BASE = os.environ.get('KUCA_SERVER', 'http://127.0.0.1:5299')
R = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
bld = {b['id']: b for b in json.load(open(f'{R}/server/KucaMemoServer/Data/buildings.json'))}
spots = {s['id']: s for s in json.load(open(f'{R}/server/KucaMemoServer/Data/spots.json'))}
g = json.load(open(os.environ.get('GRAPH') or f'{R}/My project/Assets/Resources/KUCA/WalkGraph.json'))
g.setdefault('w', [1.0] * len(g['a'])); g.setdefault('rest', [])
mk = json.load(open(f'{R}/tools/route/marks.json'))

def mm(lo, la):
    x, z = to_world(lo, la); return (x + MAP_W / 2, MAP_H / 2 - z)
def D(p, q): return ((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2) ** .5
def inside(p, poly):
    x, y, c = p[0], p[1], False
    for (x1, y1), (x2, y2) in zip(poly, poly[1:] + poly[:1]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1: c = not c
    return c
areas = [list(zip(a[::2], a[1::2])) for a in mk['blocked_areas']]
blk = [s['p'] for s in mk['blocked_segs']]
N = [mm(a, b) for a, b in zip(g['lon'], g['lat'])]
L = [[] for _ in N]
for a, b, w in zip(g['a'], g['b'], g['w']):
    c = D(N[a], N[b]); L[a].append((b, c, w)); L[b].append((a, c, w))
rests = [N[r] for r in g['rest']]
def near(p): return min(range(len(N)), key=lambda i: (N[i][0] - p[0]) ** 2 + (N[i][1] - p[1]) ** 2)
_cache = {}
def path(p, q):
    s, t = near(p), near(q)
    if (s, t) not in _cache:
        dist = {s: 0}; prev = {}; h = [(0, s)]
        while h:
            d, u = heapq.heappop(h)
            if u == t: break
            if d > dist[u]: continue
            for v, c, w in L[u]:
                nd = d + c * w
                if nd < dist.get(v, 1e18): dist[v] = nd; prev[v] = u; heapq.heappush(h, (nd, v))
        nodes = [t]
        while nodes[-1] != s: nodes.append(prev[nodes[-1]])
        _cache[(s, t)] = [N[n] for n in reversed(nodes)]
    return [p] + _cache[(s, t)] + [q]
def length(line): return sum(D(a, b) for a, b in zip(line, line[1:]))
def mpm(pace): return {'slow': 60, 'fast': 90}.get(pace, 75)
def budget(mins, pace): return mins * 0.8 * mpm(pace)

pos = {i: mm(bld[i]['lon'], bld[i]['lat']) for i in spots if i in bld}
open_ids = sorted(i for i in pos if not spots[i].get('restricted'))
WISHES = [None] * 6 + ['오르막 없는 길로', '천천히 쉬엄쉬엄 걷고 싶어', '사진 찍기 좋은 곳', '조용한 데로 30분', '중도 꼭 들러줘', '정문 빼고 걷자',
          '1시간 반 동안 걷고 싶어', '식후 가볍게 15분', '숲길 좋아', '사람 없는 곳', '비 와서 실내 위주로', '노천극장 들렀다 가자', '천문대 가보고 싶어',
          '오르막 빼줘', '힘들지 않게 20분', '만보 채우고 싶어', '쉬어가면서 걷고 싶어', '처음 가 보는 곳 위주로']
rnd = random.Random(int(os.environ.get('SEED', 300)))
plan = []
for k in range(int(os.environ.get('N', 300))):
    start = rnd.choice(open_ids) if rnd.random() < .8 else None
    if start: sp = bld[start]; lat, lon = sp['lat'], sp['lon']
    else:   # 캠퍼스 안 길 위의 아무 점 (내 위치)
        while True:
            n = rnd.randrange(len(N))
            if 250 < N[n][0] < 1250 and 300 < N[n][1] < 1450: break
        lon, lat = g['lon'][n], g['lat'][n]
    mode = rnd.choice(['free', 'free', 'dest', 'round'])
    dest = rnd.choice([i for i in open_ids if i != start]) if mode == 'dest' else None
    plan.append(dict(start=start, lat=lat, lon=lon, mode=mode, dest=dest, minutes=rnd.choice([15, 30, 30, 60]),
                     pace=rnd.choice([None, 'slow', 'normal', 'fast']), tastes=rnd.sample(['photo', 'quiet', 'food', 'unvisited'], rnd.randint(0, 3)),
                     afterMeal=rnd.random() < .15, talk=rnd.random() < .1, wish=rnd.choice(WISHES),
                     visits=[{'buildingId': i, 'count': rnd.randint(1, 5)} for i in rnd.sample(open_ids, rnd.randint(0, 4))]))

def one(k):
    p = plan[k]
    sp = mm(p['lon'], p['lat'])
    cands = [i for i in pos if i != p['start'] and i != p['dest']]
    if p['mode'] == 'round':   # 앱 Candidates: 갔다 돌아올 수 있는 곳만
        cands = [i for i in cands if 2 * D(sp, pos[i]) <= budget(p['minutes'], p['pace']) / 1.3] or cands
    if p['mode'] == 'dest':
        cands = [i for i in cands if D(sp, pos[i]) + D(pos[i], pos[p['dest']]) <= budget(p['minutes'], p['pace']) / 1.3] or cands
    body = {'minutes': p['minutes'], 'tastes': p['tastes'] or ['unvisited'], 'lat': p['lat'], 'lon': p['lon'], 'visitedBuildingIds': [],
            'candidateBuildingIds': cands, 'pace': p['pace'], 'afterMeal': p['afterMeal'], 'wish': p['wish'], 'visitCounts': p['visits'], 'talk': p['talk']}
    req = urllib.request.Request(f'{BASE}/api/recommendations', json.dumps(body).encode(), {'Content-Type': 'application/json'})
    t0 = time.time()
    try: r = json.load(urllib.request.urlopen(req, timeout=30)); code = 200
    except urllib.error.HTTPError as e:
        code = e.code
        try: r = json.loads(e.read() or b'{}')
        except Exception: r = {}
    except Exception as e: r = {'exc': repr(e)}; code = -1
    return {'k': k, **p, 'code': code, 'ms': int((time.time() - t0) * 1000), 'resp': r}

with ThreadPoolExecutor(8) as ex:
    rows = list(ex.map(one, range(len(plan))))

# ---- 앱처럼 잇고 검사 ----
issues = collections.defaultdict(list)
stats = collections.Counter()
for row in rows:
    p, r = row, row['resp']
    tag = f"#{row['k']} {p['minutes']}분 {p['mode']} wish={p['wish']}"
    if row['code'] != 200:
        stats[f"http {row['code']}"] += 1
        if row['code'] not in (422,): issues['서버 오류'].append(f"{tag} → {row['code']} {r}")
        else: stats['422 ' + str(r.get('code') or r.get('error'))[:40]] += 1
        continue
    stats['200'] += 1
    ids = [s['buildingId'] for s in r['stops']]
    if len(ids) != len(set(ids)): issues['같은 곳 두 번'].append(f'{tag} {ids}')
    for i in ids:
        if i not in spots: issues['모르는 장소'].append(f'{tag} {i}')
        elif spots[i].get('restricted'): issues['출입 제한 장소'].append(f"{tag} {spots[i]['name']}")
        elif p['talk'] and (spots[i].get('noisy') or spots[i].get('hill')): issues['메이트 코스에 붐빔 · 오르막'].append(f"{tag} {spots[i]['name']}")
        elif p['wish'] and '오르막' in p['wish'] and spots[i].get('hill'): issues['오르막 빼라는데 오르막 장소'].append(f"{tag} {spots[i]['name']}")
        if i in pos and any(inside(pos[i], a) for a in areas): issues['막힌 구역 안 장소'].append(f'{tag} {i}')
    if p['wish'] and '중도' in p['wish'] and 'way-474085534' not in ids: issues['말한 장소 빠짐'].append(f'{tag} {ids}')
    if p['wish'] and '정문' in p['wish'] and 'way-473963422' in ids: issues['빼라는 장소 들어감'].append(f'{tag}')
    # MakeOption
    bm = (r.get('wish') or {}).get('minutes') or p['minutes']   # 앱: 말로 정한 시간이 있으면 그 시간
    start = mm(p['lon'], p['lat'])
    stops = [(s, pos.get(s['buildingId'], mm(s['lon'], s['lat']))) for s in r['stops'] if s['buildingId'] not in (p['dest'], p['start'])]
    if p['dest']: stops.append(({'buildingId': p['dest'], 'restMinutes': 0}, pos[p['dest']]))
    elif p['mode'] == 'round':
        if not stops: stats['앱: 들를 곳 없어 코스 버림'] += 1; continue
        stops.append(({'buildingId': 'home'}, start))
    if not stops: stats['앱: 코스 버림'] += 1; continue
    def trace(st): return length([q for a, b in zip([start] + [x[1] for x in st], [x[1] for x in st]) for q in path(a, b)])
    meters = trace(stops)
    has_end = p['dest'] or p['mode'] == 'round'
    min_stops = 2 if p['mode'] == 'round' else 1
    dropped = 0
    while meters > budget(bm, p['pace']) * 1.25 and len(stops) > min_stops:
        stops.pop(len(stops) - 2 if has_end else len(stops) - 1); meters = trace(stops); dropped += 1
    if dropped: stats['앱: 시간 넘어 장소 줄임'] += 1
    # AddRest
    rest_added = False
    if meters >= 600:
        way = [start] + [x[1] for x in stops]
        best, at, spot = max(150, (bm - meters / mpm(p['pace'])) * mpm(p['pace'])), -1, None
        for i in range(len(way) - 1):
            leg = length(path(way[i], way[i + 1]))
            for q in rests:
                if D(q, way[i]) < 30 or D(q, way[i + 1]) < 30: continue
                ex = length(path(way[i], q)) + length(path(q, way[i + 1])) - leg
                if ex < best: best, at, spot = ex, i, q
        if at >= 0 and meters + best <= budget(bm, p['pace']) * 1.25:
            rest_min = max(2, min(5, bm - round((meters + best) / mpm(p['pace']))))
            stops.insert(at, ({'buildingId': '', 'restMinutes': rest_min}, spot)); meters = trace(stops); rest_added = True
    stats['쉼터 넣음' if rest_added else '쉼터 없음'] += 1
    line = [q for a, b in zip([start] + [x[1] for x in stops], [x[1] for x in stops]) for q in path(a, b)]
    # 선 전체를 2 m 마다: 막힌 구역 · 막힌 길 위
    bad_area = bad_seg = False
    for a, b in zip(line, line[1:]):
        n = max(1, int(D(a, b) / 2))
        for t in range(n + 1):
            q = (a[0] + (b[0] - a[0]) * t / n, a[1] + (b[1] - a[1]) * t / n)
            if any(inside(q, ar) for ar in areas): bad_area = True
    # 길 위를 걷는 구간(노드-노드)이 막힌 길과 겹치는지: 노드 쌍 가운데가 막힌 길 1 m 안이고 양 끝도 2 m 안
    def onseg(q, poly, tol):
        for i in range(0, len(poly) - 2, 2):
            ax, ay, bx, by = poly[i:i + 4]; dx, dy = bx - ax, by - ay
            t = max(0, min(1, ((q[0] - ax) * dx + (q[1] - ay) * dy) / (dx * dx + dy * dy or 1e-9)))
            if (q[0] - ax - t * dx) ** 2 + (q[1] - ay - t * dy) ** 2 <= tol * tol: return True
        return False
    for a, b in zip(line, line[1:]):
        m = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        if D(a, b) > 3 and any(onseg(m, s, 1.0) and onseg(a, s, 2) and onseg(b, s, 2) for s in blk): bad_seg = True
    if bad_area: issues['선이 막힌 구역을 지남'].append(tag)
    if bad_seg: issues['선이 막힌 길을 따라감'].append(tag)
    real_m = length(line)
    stats['길 기준 m 합'] += int(real_m)
    walk_min = real_m / mpm(p['pace']) + (rest_min if rest_added else 0)
    ratio = walk_min / bm
    stats['시간 비율 ' + ('<0.5' if ratio < .5 else '0.5-0.8' if ratio < .8 else '0.8-1.2' if ratio <= 1.2 else '1.2-1.4' if ratio <= 1.4 else '>1.4')] += 1
    stats['장소 수 합'] += sum(1 for x in stops if x[0].get('buildingId') not in ('', 'home'))
    if walk_min > bm * 1.4 + 3: issues['요청 시간보다 많이 김 (>1.4배)'].append(f"{tag} → {walk_min:.0f}분 ({real_m:.0f} m)")
    if r['totalDistanceM'] and abs(real_m - r['totalDistanceM']) > max(250, .5 * r['totalDistanceM']) and not p['dest'] and p['mode'] != 'round':
        issues['서버 거리와 앱 길 거리 차이 큼'].append(f"{tag} 서버 {r['totalDistanceM']} m · 앱 {real_m:.0f} m")
    row['app'] = {'meters': int(real_m), 'minutes': round(walk_min, 1), 'rest': rest_added, 'stops': [x[0]['buildingId'] for x in stops]}

json.dump(rows, open(sys.argv[1], 'w'), ensure_ascii=False, indent=0)
print(json.dumps(stats, ensure_ascii=False, indent=0))
print('평균 응답', sum(r['ms'] for r in rows) // len(rows), 'ms')
for k, v in issues.items():
    print(f'\n## {k}: {len(v)}')
    for x in v[:8]: print('  ', x)
