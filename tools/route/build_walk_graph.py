"""
캠퍼스 보행 길 그래프 만들기 → My project/Assets/Resources/KUCA/WalkGraph.json

    python3 tools/route/build_walk_graph.py

지도 바닥을 만든 OSM 데이터(tools/keyart/osm_ground.json)에서 사람이 걸을 수 있는 길만 골라
교차점 · 꺾이는 점을 노드로, 이어진 구간을 간선으로 저장한다. 앱의 WalkGraph 가 읽어서 길 찾기를 한다.
  lon[i] · lat[i]: 노드 i 의 경도 · 위도,  a[k] · b[k]: 간선 k 가 잇는 두 노드 번호 (양방향)
  w[k]: 간선 k 의 힘든 정도 (길이에 곱함, 평지 1 · 사용자 표시 오르막 조금 1.5 · 많이 2.2)
  rest[j]: 사용자가 표시한 쉼터 j 에서 가장 가까운 노드 번호
  (Unity JsonUtility 가 그대로 읽게 평평한 배열로)
가장 큰 연결 덩어리만 남긴다 (따로 떨어진 짧은 길로 길 찾기가 끊기지 않게).
"""
import json
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'keyart'))
from kuca_art.geo import MAP_H, MAP_W, to_world

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, '..', 'keyart', 'osm_ground.json')
OUT = os.path.join(HERE, '..', '..', 'My project', 'Assets', 'Resources', 'KUCA', 'WalkGraph.json')

# 사용자가 산책길 지도(아티팩트)에 '막힘'으로 표시한 구간 · 구역 (지도 m 좌표, 왼쪽 위 북서). 표시를 바꾸면 다시 내려받아 이 파일을 갱신한다
MARKS = os.path.join(HERE, 'marks.json')
marks = json.load(open(MARKS, encoding='utf-8')) if os.path.exists(MARKS) else {'blocked_segs': [], 'blocked_areas': []}
blocked_by_way = defaultdict(list)
for sgm in marks['blocked_segs']:
    blocked_by_way[sgm['way']].append(sgm['p'])
blocked_areas = [list(zip(a[::2], a[1::2])) for a in marks['blocked_areas']]
rest_points = marks.get('rest_points', [])
# 사용자가 표시한 오르막 (tools/route/slopes.json, 같은 표시가 지도 경사 빗금도 그린다)
SLOPE_W = {1: 1.5, 2: 2.2}
slopes_by_way = defaultdict(list)
for sl in json.load(open(os.path.join(HERE, 'slopes.json'), encoding='utf-8'))['slopes']:
    slopes_by_way[int(sl['seg'].split(':')[0])].append((sl['p'], sl['level']))

WALKABLE = {'footway', 'path', 'pedestrian', 'steps', 'service', 'residential', 'living_street', 'tertiary',
            'secondary', 'track', 'unclassified', 'cycleway', 'corridor', 'primary'}

data = json.load(open(SRC, encoding='utf-8'))


def map_m(lon, lat):
    """경위도 → 지도 m (tools/keyart/kuca_art/geo.py 와 같은 범위)"""
    x, z = to_world(lon, lat)
    return x + MAP_W / 2, MAP_H / 2 - z


def near_line(p, line, tol):
    for i in range(0, len(line) - 2, 2):
        ax, ay, bx, by = line[i], line[i + 1], line[i + 2], line[i + 3]
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((p[0] - ax) * dx + (p[1] - ay) * dy) / (dx * dx + dy * dy or 1e-9)))
        if (p[0] - ax - t * dx) ** 2 + (p[1] - ay - t * dy) ** 2 <= tol * tol:
            return True
    return False


def inside(p, poly):
    x, y, c = p[0], p[1], False
    for (x1, y1), (x2, y2) in zip(poly, poly[1:] + poly[:1]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            c = not c
    return c


def blocked(way, a, b):
    """OSM 길 조각 a-b(경위도)가 막힌 구간 위(같은 길 번호, 2 m 안)이거나 막힌 구역 안이면 길 찾기에서 뺀다"""
    m = map_m((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    return any(near_line(m, line, 2.0) for line in blocked_by_way.get(way, ())) or in_blocked_area(a, b)


def in_blocked_area(a, b):
    """경위도 a-b 선의 어느 곳이라도 막힌 구역 안이면 (긴 간선이 구역 모서리를 스치는 것도 막는다)"""
    pa, pb = map_m(*a), map_m(*b)
    n = max(2, int(((pa[0] - pb[0]) ** 2 + (pa[1] - pb[1]) ** 2) ** 0.5 / 2) + 1)   # 2 m 마다
    return any(inside((pa[0] + (pb[0] - pa[0]) * k / n, pa[1] + (pb[1] - pa[1]) * k / n), ar)
               for k in range(n + 1) for ar in blocked_areas)


coord = {}
way_of = {}   # 간선 (작은 노드, 큰 노드) → OSM 길 번호 (오르막 찾기)
adj = defaultdict(set)
for e in data['elements']:
    tags = e.get('tags', {})
    if e.get('type') != 'way' or tags.get('highway') not in WALKABLE or tags.get('foot') == 'no' or tags.get('access') == 'no':
        continue
    nodes, geom = e.get('nodes'), e.get('geometry')
    if not nodes or not geom or len(nodes) != len(geom):
        continue
    for nid, g in zip(nodes, geom):
        coord[nid] = (round(g['lon'], 7), round(g['lat'], 7))
    for a, b in zip(nodes, nodes[1:]):
        if a != b and not blocked(e['id'], coord[a], coord[b]):
            adj[a].add(b)
            adj[b].add(a)
            way_of[(min(a, b), max(a, b))] = e['id']

def xy(nid):
    lon, lat = coord[nid]
    return (lon - 127.08) * 88_700, (lat - 37.244) * 111_320   # m (캠퍼스 위도에서)


def components():
    seen, out = set(), []
    for start in adj:
        if start in seen:
            continue
        comp, stack = [], [start]
        seen.add(start)
        while stack:
            n = stack.pop()
            comp.append(n)
            for m in adj[n]:
                if m not in seen:
                    seen.add(m)
                    stack.append(m)
        out.append(comp)
    return sorted(out, key=len, reverse=True)



# 1) 끊긴 길 잇기: OSM 에서 몇 m 떨어져 그려진 길 (사색의 광장 안 길은 둘레 길과 4 m 떨어져 통째로 빠졌었음, 10/27)
JOIN_M = 6.0
main, *rest = components()
main_pts = [(xy(n), n) for n in main]
joined = 0
for comp in rest:
    for n in comp:
        p = xy(n)
        d, m = min(((abs(q[0] - p[0]) + abs(q[1] - p[1]), m) for q, m in main_pts), default=(1e9, None))
        if m is not None and d <= JOIN_M and not in_blocked_area(coord[n], coord[m]):
            adj[n].add(m)
            adj[m].add(n)
            joined += 1

# 2) 열린 광장: 둘레 · 안의 점끼리 광장 안을 가로질러 걸을 수 있다 (사용자 10/27: "사색의 광장은 모든 공간을 다 거닐 수 있음")
#    광장 = place=square · 보행 구역(highway=pedestrian + area=yes · area:highway). 연못(natural=water) 위는 지나지 않는다
def polys(test):
    for e in data['elements']:
        t = e.get('tags', {})
        if e.get('type') == 'way' and e.get('geometry') and test(t):
            yield [((g['lon'] - 127.08) * 88_700, (g['lat'] - 37.244) * 111_320) for g in e['geometry']]


squares = list(polys(lambda t: t.get('place') == 'square' or t.get('area:highway') or (t.get('highway') == 'pedestrian' and t.get('area') == 'yes')))
waters = list(polys(lambda t: t.get('natural') == 'water'))
ACROSS_M = 80.0
crossings = 0
for sq in squares:
    xs, ys = [p[0] for p in sq], [p[1] for p in sq]
    ins = [n for n in adj if min(xs) - 3 <= xy(n)[0] <= max(xs) + 3 and min(ys) - 3 <= xy(n)[1] <= max(ys) + 3
           and (inside(xy(n), sq) or min(abs(xy(n)[0] - x) + abs(xy(n)[1] - y) for x, y in sq) < 3)]
    for i, a in enumerate(ins):
        for b in ins[i + 1:]:
            pa, pb = xy(a), xy(b)
            d = ((pa[0] - pb[0]) ** 2 + (pa[1] - pb[1]) ** 2) ** 0.5
            if d > ACROSS_M or b in adj[a]:
                continue
            samples = [(pa[0] + (pb[0] - pa[0]) * k / 10, pa[1] + (pb[1] - pa[1]) * k / 10) for k in range(1, 10)]
            if all(inside(q, sq) for q in samples) and not any(inside(q, w) for q in samples for w in waters) \
                    and not in_blocked_area(coord[a], coord[b]):
                adj[a].add(b)
                adj[b].add(a)
                crossings += 1

# 가장 큰 연결 덩어리
keep = set(components()[0])
print(f"user marks: {len(marks['blocked_segs'])} blocked segments, {len(blocked_areas)} blocked areas")
print(f'joined {joined} near-miss ends, {crossings} open-square crossings in {len(squares)} squares')
index = {nid: i for i, nid in enumerate(sorted(keep))}
nodes = [list(coord[nid]) for nid in sorted(keep)]
edges = sorted({tuple(sorted((index[a], index[b]))) for a in keep for b in adj[a] if b in keep})
ids = sorted(keep)


def effort(i, j):
    """간선이 사용자 표시 오르막 위면 그 단계의 무게 (같은 길 번호, 가운데가 선에서 3 m 안)"""
    a, b = ids[i], ids[j]
    way = way_of.get((min(a, b), max(a, b)))
    m = map_m((coord[a][0] + coord[b][0]) / 2, (coord[a][1] + coord[b][1]) / 2)
    return max([SLOPE_W[lv] for line, lv in slopes_by_way.get(way, ()) if near_line(m, line, 3.0)], default=1.0)


weights = [effort(i, j) for i, j in edges]
mxy = [map_m(*coord[nid]) for nid in ids]
rest = [min(range(len(ids)), key=lambda i: (mxy[i][0] - x) ** 2 + (mxy[i][1] - y) ** 2) for x, y in rest_points]

# 확인: 남은 간선은 어느 곳도 막힌 구역에 들어가지 않는다
assert not any(in_blocked_area(coord[ids[i]], coord[ids[j]]) for i, j in edges), 'edge enters a blocked area'
print(f'slope edges {sum(w > 1 for w in weights)} ({sum(w > 2 for w in weights)} steep), rest nodes '
      + ', '.join(f'{rest_points[k]} -> {((mxy[i][0] - rest_points[k][0]) ** 2 + (mxy[i][1] - rest_points[k][1]) ** 2) ** 0.5:.1f} m' for k, i in enumerate(rest)))
# 경희스팟 사이 실제 걷는 거리 (앱 WalkGraph 와 같은 길: 힘든 정도 w 로 고르고 길이는 평지 m, 길까지 붙는 직선 포함)
#  → 서버 RouteRecommender 가 직선 × 1.3 대신 쓴다 (숲길을 막은 뒤 실제 길이 직선의 1.7배쯤이라 코스 시간이 안 맞았음, 10/29)
import heapq
SERVER_DATA = os.path.join(HERE, '..', '..', 'server', 'KucaMemoServer', 'Data')
bld = {b['id']: b for b in json.load(open(os.path.join(SERVER_DATA, 'buildings.json'), encoding='utf-8'))}
spot_ids = [s['id'] for s in json.load(open(os.path.join(SERVER_DATA, 'spots.json'), encoding='utf-8')) if s['id'] in bld]
nbr = defaultdict(list)
for (i, j), w in zip(edges, weights):
    d = ((mxy[i][0] - mxy[j][0]) ** 2 + (mxy[i][1] - mxy[j][1]) ** 2) ** 0.5
    nbr[i].append((j, d, w))
    nbr[j].append((i, d, w))
spot_xy = [map_m(bld[i]['lon'], bld[i]['lat']) for i in spot_ids]
snap = [min(range(len(ids)), key=lambda n: (mxy[n][0] - x) ** 2 + (mxy[n][1] - y) ** 2) for x, y in spot_xy]
gap = [((mxy[n][0] - x) ** 2 + (mxy[n][1] - y) ** 2) ** 0.5 for n, (x, y) in zip(snap, spot_xy)]


def walk_from(s):
    """s 에서 모든 노드까지: 힘든 정도로 고른 길의 평지 길이"""
    cost, flat, h = {s: 0.0}, {s: 0.0}, [(0.0, s)]
    while h:
        c, u = heapq.heappop(h)
        if c > cost[u]:
            continue
        for v, d, w in nbr[u]:
            if c + d * w < cost.get(v, 1e18):
                cost[v], flat[v] = c + d * w, flat[u] + d
                heapq.heappush(h, (cost[v], v))
    return flat


rows = []
for a_, s in enumerate(snap):
    flat = walk_from(s)
    rows.append([0 if a_ == b_ else round(gap[a_] + flat[t] + gap[b_]) for b_, t in enumerate(snap)])
json.dump({'about': '경희스팟 사이 실제 걷는 거리 (m). tools/route/build_walk_graph.py 가 앱 길 찾기 그래프(사용자 막힘 · 오르막 표시 반영)로 만든다. 고치지 말고 다시 만들 것',
           'ids': spot_ids, 'meters': rows}, open(os.path.join(SERVER_DATA, 'walk_meters.json'), 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
print(f'walk_meters: {len(spot_ids)} spots')

os.makedirs(os.path.dirname(OUT), exist_ok=True)
json.dump({'lon': [n[0] for n in nodes], 'lat': [n[1] for n in nodes],
           'a': [e[0] for e in edges], 'b': [e[1] for e in edges], 'w': weights, 'rest': rest}, open(OUT, 'w'), separators=(',', ':'))
print(f'nodes {len(nodes)} (of {len(adj)}), edges {len(edges)} -> {os.path.normpath(OUT)} ({os.path.getsize(OUT)//1024} KB)')
