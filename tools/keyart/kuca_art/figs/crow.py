"""까마귀 (파랑 희귀): 검은 동그란 몸, 화난 눈썹, 회색 부리, 파란 목도리(빨간 꽃 브로치), 병뚜껑 든 파란 크로스백.
받침: 달 돌 + 톱니 두 개."""
import math

import numpy as np

from .. import sculpt as S
from .. import figures as Fg

TIER = 'blue'

INK = (0.27, 0.27, 0.31)        # 까마귀 깃 (아주 짙은 남색빛 검정)
INK_HI = (0.36, 0.36, 0.41)
BEAK = (0.47, 0.47, 0.50)
FEET = (0.22, 0.22, 0.25)
SCARF = (0.33, 0.64, 0.96)
SCARF_DK = (0.25, 0.52, 0.86)
BAG = (0.36, 0.66, 0.96)
CARD = (0.97, 0.96, 0.93)
CRIMSON = (0.72, 0.10, 0.14)
SILVER = (0.80, 0.82, 0.86)
SIL = (SILVER, (1.0, 1.0, 1.0))
GLD = (Fg.GOLD, Fg.GOLD_HI)


# ---------- 공통 도우미 ----------

_GRID = []


def bounded(f, margin=1.0):
    """빠르게: 성긴 격자로 부품의 경계 상자를 찾아, 상자에서 margin 안쪽 점만 진짜 SDF 를 계산 (밖은 상자까지 거리 = 하한)"""
    if not _GRID:
        xs, ys = np.arange(-4.8, 4.81, 0.2), np.arange(-0.05, 12.61, 0.2)
        X, Y, Z = np.meshgrid(xs, ys, xs, indexing='ij')
        _GRID.append(np.stack([X.ravel(), Y.ravel(), Z.ravel()], axis=1))
    G = _GRID[0]
    m = f(G) < 0.32
    if not m.any():
        return f
    lo, hi = G[m].min(axis=0) - 0.32, G[m].max(axis=0) + 0.32
    c, h = (lo + hi) / 2, (hi - lo) / 2

    def g(P):
        db = np.linalg.norm(np.maximum(np.abs(P - c) - h, 0.0), axis=1)
        sel = db < margin
        if sel.any():
            db[sel] = f(P[sel])
        return db
    return g


def U(fs, k=0.0):
    """여러 SDF 를 하나로 (k>0 이면 부드럽게)"""
    fs = list(fs)
    if len(fs) > 2:
        fs = [bounded(g, 0.6) for g in fs]

    def f(P):
        d = fs[0](P)
        for g in fs[1:]:
            d = S.smin(d, g(P), k) if k > 0 else np.minimum(d, g(P))
        return d
    return f


class Fast:
    """Figure 대리: add 하는 모든 부품을 bounded 로 감싼다"""

    def __init__(self, fig):
        self._fig = fig

    def add(self, f, col, k=0.2, layer='body', metal=None):
        self._fig.add(bounded(f), col, k, layer, metal)

    def __getattr__(self, n):
        return getattr(self._fig, n)


def _dir(yaw, pitch):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    return np.array((cp * sy, sp, cp * cy))


def hit(f, c, d, rmax=6.0, n=3000):
    """c 에서 d 방향으로 나가며 f 가 처음 0 을 넘는 거리 (표면 찾기)"""
    d = np.asarray(d, np.float64)
    d = d / np.linalg.norm(d)
    ts = np.linspace(0.0, rmax, n)
    v = f(np.asarray(c, np.float64)[None, :] + ts[:, None] * d[None, :])
    i = int(np.argmax(v > 0))
    return ts[i]


def surf(f, c, yaw, pitch, out=0.0):
    d = _dir(yaw, pitch)
    return np.asarray(c, np.float64) + d * (hit(f, c, d) + out)


def sface(fig, c, yaw, pitch, out=0.0, layer='body', up=(0, 1, 0)):
    """머리 중심 c 에서 (yaw, pitch) 방향의 실제 조형 표면 Frame (바깥에서 안으로 광선, Fg.surface_frame)"""
    d = _dir(yaw, pitch)
    return Fg.surface_frame(fig, np.asarray(c, np.float64) + d * 8.0, -d, up=up, out=out, layer=layer)


def face_eyes(fig, c, spread, pitch, style, size, iris=Fg.IRIS, tilt=0.0, lid=None, layer='body', sink=0.14):
    """성격별 눈 한 쌍 (Fg.eye_at) 을 실제 표면에 붙인다. sink = 표면 아래로 묻는 비율 (size 배)"""
    for s in (-1, 1):
        f = sface(fig, c, s * spread, pitch, -size * sink, layer)
        Fg.eye_at(fig, f, style=style, size=size, iris=iris, side=s, tilt=tilt, lid=lid)


def mouth_w(fig, f, w=0.24, col=(0.30, 0.16, 0.16), r=0.05):
    """표면 Frame f 위 'w' 입 (작은 곡선 두 개, 표면에 얕게 박음)"""
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            Fg._ellipsoid(fig.extra, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.0), (r, r, r * 0.7), col, 6, 4, f)


def frame_at(f, c, yaw, pitch, out=0.0):
    """머리 표면 위 (yaw, pitch) 지점의 Frame (Z = 바깥)"""
    t = hit(f, c, _dir(yaw, pitch))
    return Fg._frame_on(c, t, yaw, pitch, out)


def axes(ydir, zdir):
    """로컬 Y = ydir, Z ≈ zdir 인 회전 행렬 (열 = 로컬 축)"""
    y = np.asarray(ydir, np.float64)
    y = y / np.linalg.norm(y)
    z = np.asarray(zdir, np.float64)
    z = z - y * (z @ y)
    z = z / np.linalg.norm(z)
    x = np.cross(y, z)
    return np.stack([x, y, z], axis=1)


def ribbon(fig, pts, nrm, width, thick, col, layer, k=0.06, metal=None):
    """점 열을 따라 납작한 띠 (목도리 끝, 끈). nrm = 띠 면의 바깥 방향"""
    pts = [np.asarray(p, np.float64) for p in pts]
    for i in range(len(pts) - 1):
        a, b = pts[i], pts[i + 1]
        L = np.linalg.norm(b - a)
        n = nrm[i] if isinstance(nrm, list) else nrm
        R = axes(b - a, n)
        w = width[i] if isinstance(width, list) else width
        fig.add(S.box((a + b) / 2, (w, L / 2 + thick * 0.6, thick), round_=thick * 0.95, R=R), col, k=k, layer=layer, metal=metal)


def feather(pivot, yaw, pitch, roll, dist, size):
    """pivot 에서 아래(-Y)로 dist 떨어진 깃털 하나 (yaw·pitch·roll 로 방향), size = (폭, 길이, 두께)"""
    R = S.rot(yaw, pitch, roll)
    c = np.asarray(pivot, np.float64) + R @ np.array((0.0, -dist, 0.0))
    return S.ellipsoid(c, size, R=R)


def eyes(fig, f, c, spread, pitch, size, iris=Fg.IRIS, tall=1.1, sclera=True, out=0.0, iris_hi=None):
    """반짝이는 큰 눈 (표면에 붙임): 흰자 테두리 + 홍채 + 동공 + 반짝임 2개"""
    b = fig.extra
    E = Fg._ellipsoid
    ih = iris_hi or tuple(min(1.0, x * 1.6 + 0.05) for x in iris)
    for s in (-1, 1):
        fr = frame_at(f, c, s * spread, pitch, -size * 0.2 + out)
        if sclera:
            E(b, (s * size * 0.13, -size * 0.07, -size * 0.02), (size * 0.92, size * tall * 0.92, size * 0.33), (0.99, 0.98, 0.97), 22, 10, fr)
        E(b, (0, 0, size * 0.04), (size * 0.86, size * tall * 0.86, size * 0.34), Fg.PUPIL, 22, 10, fr)
        E(b, (0, -size * 0.12, size * 0.09), (size * 0.76, size * tall * 0.74, size * 0.32), iris, 20, 10, fr)
        E(b, (0, -size * 0.38, size * 0.12), (size * 0.5, size * 0.34, size * 0.28), ih, 16, 8, fr)
        E(b, (0, size * 0.05, size * 0.19), (size * 0.42, size * 0.48, size * 0.28), Fg.PUPIL, 16, 8, fr)
        E(b, (-size * 0.3 * s, size * 0.34, size * 0.33), (size * 0.3, size * 0.3, size * 0.12), Fg.SHINE, 12, 6, fr)
        E(b, (size * 0.3 * s, -size * 0.42, size * 0.31), (size * 0.12, size * 0.12, size * 0.06), Fg.SHINE, 8, 4, fr)


def gear(fig, gc, gr, layer='gear'):
    teeth = []
    for k in range(8):
        R = S.rot(k * 45 + 10, 0, 0)
        teeth.append(S.box(tuple(np.array(gc) + R @ np.array((gr * 1.12, 0, 0))), (gr * 0.24, 0.14, gr * 0.2), round_=0.04, R=R))
    fig.add(S.subtract(U([S.cylinder(gc, gr, 0.15, round_=0.05)] + teeth), S.cylinder(gc, gr * 0.38, 0.3)), SILVER, k=0.0, layer=layer, metal=SIL)


def moon_rock(fig, c, r, layer='rock'):
    c = np.asarray(c, np.float64)
    base = lambda P: S.ellipsoid(c, (r, r * 0.82, r))(P) + 0.025 * np.sin(P[:, 0] * 9) * np.sin(P[:, 2] * 8)
    craters = []
    for (yaw, pit, rr) in ((-20, 25, 0.24), (40, 15, 0.2), (5, 55, 0.18), (-60, 5, 0.17), (80, 40, 0.15), (150, 30, 0.2)):
        d = _dir(yaw, pit)
        craters.append((c + d * np.array((r, r * 0.82, r)) * 1.04, rr * r / 0.8))

    def f(P):
        d = base(P)
        for cc, rr in craters:
            d = np.maximum(d, -(S.sphere(cc, rr)(P)))
        return d
    fig.add(f, (0.68, 0.68, 0.70), k=0.0, layer=layer)
    for cc, rr in craters:
        fig.paint(S.sphere(cc, rr * 1.05), (0.55, 0.55, 0.58), soft=0.05)


def bottle_cap(fig, c, R, r=0.2, col=Fg.GOLD, metal=GLD, layer='cap'):
    """병뚜껑: 납작한 원판 + 톱니 테 (R = 회전 행렬, 로컬 Y = 뚜껑 축)"""
    c = np.asarray(c, np.float64)
    fs = [S.cylinder(c, r, 0.035, round_=0.02, R=R)]
    for k in range(14):
        a = 2 * math.pi * k / 14
        fs.append(S.sphere(c + R @ np.array((math.cos(a) * r, -0.03, math.sin(a) * r)), 0.045))
    fig.add(U(fs, 0.03), col, k=0.0, layer=layer, metal=metal)


def flower_brooch(fig, c, n, size=0.17):
    """빨간 꽃 브로치 (꽃잎 5장 + 금색 가운데). n = 바깥 방향"""
    n = np.asarray(n, np.float64)
    n = n / np.linalg.norm(n)
    R = axes((0, 1, 0) - n * n[1], n)
    c = np.asarray(c, np.float64)
    for k in range(5):
        a = math.pi / 2 + 2 * math.pi * k / 5
        p = c + R @ np.array((math.cos(a) * size * 0.75, math.sin(a) * size * 0.75, 0.0))
        fig.add(S.ellipsoid(p, (size * 0.55, size * 0.55, size * 0.25), R=R), CRIMSON, k=0.04, layer='brooch')
    fig.add(S.sphere(c + n * size * 0.15, size * 0.38), Fg.GOLD, k=0.0, layer='brooch_c', metal=GLD)


# ---------- 까마귀 ----------

def fringe(fig, end, tang, n, gap, L, col, layer='fringe'):
    """띠 끝 술 장식 (tang: 띠 끝의 로컬 축, Y = 띠 진행 방향)"""
    fs = []
    for i in range(n):
        u = (i - (n - 1) / 2) * gap
        a = end + tang @ np.array((u, 0.0, 0))
        fs.append(S.capsule(a, a + tang @ np.array((u * 0.3, L, 0.0)), 0.045, 0.04))
    fig.add(U(fs), col, k=0.0, layer=layer)


def tube_loop(fig, core, c, nrm, out, r, col, layer, n=28, k=0.15, start=0.0):
    """core 표면을 따라 c 를 지나는 (nrm 에 수직인) 고리 관 (목도리 등)"""
    nrm = np.asarray(nrm, np.float64)
    nrm = nrm / np.linalg.norm(nrm)
    u = np.cross(nrm, (0, 0, 1.0))
    u /= np.linalg.norm(u)
    v = np.cross(nrm, u)
    pts = []
    for i in range(n):
        a = 2 * math.pi * i / n + start
        d = u * math.cos(a) + v * math.sin(a)
        pts.append(np.asarray(c) + d * (hit(core, c, d) + out))
    fig.add(U([S.capsule(pts[i], pts[(i + 1) % n], r) for i in range(n)], k), col, k=k, layer=layer)
    return pts


def build(fig, rng):
    Fg.base(fig, rng, flowers=6)
    fig = Fast(fig)
    y0 = Fg.TOP
    hc = np.array((0.0, y0 + 5.62, 0.1))                    # 머리 중심
    # 둥근 머리: 위는 둥글고 아래(볼 쪽)가 살짝 넓은 하나의 덩어리 — 볼 구 없이 두상 자체 볼륨
    head_up = S.ellipsoid(hc, (2.1, 1.98, 1.95))
    head_lo = S.ellipsoid(hc + np.array((0, -0.55, 0.12)), (2.28, 1.42, 1.8))
    head = lambda P: S.smin(head_up(P), head_lo(P), 0.55)
    # 통통한 몸: 아래가 빵빵한 배
    torso = S.ellipsoid((0, y0 + 2.25, 0.0), (1.98, 1.78, 1.78))
    belly = S.ellipsoid((0, y0 + 1.92, 0.32), (1.82, 1.5, 1.6))
    trunk = lambda P: S.smin(torso(P), belly(P), 0.4)
    core = lambda P: S.smin(head(P), trunk(P), 0.75)     # 목이 잘록한 눈사람처럼 보이지 않게 머리·몸을 넓게 녹여 붙임
    fig.add(core, INK, k=0.3)
    # 정수리 깃털 (가운데 위로, 양옆으로 휘어진 뾰족한 깃 + 뒤 작은 깃)
    top = hc + np.array((0, 1.88, -0.15))
    tuft = []
    for (yaw, pitch, roll, L, w) in ((0, -28, 0, 1.25, 0.34), (0, -20, 42, 1.0, 0.3), (0, -20, -42, 1.0, 0.3), (180, -35, 0, 0.8, 0.28)):
        R = S.rot(yaw, pitch, roll)
        c1 = top + R @ np.array((0, L * 0.45, 0))
        tip = top + R @ np.array((0, L, 0)) + np.array((0, -0.12, -0.15))
        tuft.append(S.capsule(top - np.array((0, 0.2, 0)), c1, w, w * 0.8))
        tuft.append(S.capsule(c1, tip, w * 0.8, 0.13))
    fig.add(U(tuft, 0.08), INK, k=0.25)
    # 화난 눈썹 능선 (안쪽 끝이 아래로)
    for s in (-1, 1):
        a = surf(head, hc, s * 9, 21, -0.04)       # 눈 위로 올려 눈에 묻히지 않게 (안쪽 끝이 낮은 찡그린 눈썹)
        m = surf(head, hc, s * 21, 27, -0.04)
        b = surf(head, hc, s * 34, 28.5, -0.05)
        fig.add(U([S.capsule(a, m, 0.12, 0.12), S.capsule(m, b, 0.12, 0.06)], 0.06), (0.36, 0.36, 0.42), k=0.0, layer='brow')
    # 다리·발
    for s in (-1, 1):
        x = s * 0.78
        ank = np.array((x, y0 + 0.26, 0.5))
        fig.add(S.capsule((x, y0 + 0.95, 0.25), ank, 0.22, 0.15), FEET, k=0.08, layer='feet')
        for yaw in (-32, 0, 32):
            d = _dir(yaw + s * 8, -8)
            fig.add(S.capsule(ank + np.array((0, -0.12, 0)), ank + np.array((0, -0.12, 0)) + d * 0.72, 0.14, 0.09), FEET, k=0.08, layer='feet')
        fig.add(S.capsule(ank + np.array((0, -0.12, 0)), ank + np.array((0, -0.18, -0.5)), 0.12, 0.08), FEET, k=0.08, layer='feet')
    # 날개: 몸 옆에 접어 붙인 깃 (어깨 덩어리 + 첫째 깃 5 + 윗깃 3). 깃 면이 옆을 보고(yaw 80) 아래·뒤로 부채,
    # 몸 곡면을 따라 살짝 바깥으로(pitch -20) — 옆으로 뻗으면 팔처럼 보였다 (펫 검토 v1)
    for s in (-1, 1):
        piv = np.array((s * 1.7, y0 + 3.45, -0.05))
        fig.add(S.ellipsoid(piv + np.array((s * 0.12, -0.55, -0.1)), (0.36, 0.95, 0.78), R=S.rot(0, 0, s * 20)), INK, k=0.3, layer='wing')
        prim = []
        for i, roll in enumerate((-6, 6, 17, 28, 38)):
            L = (1.5, 1.7, 1.65, 1.45, 1.2)[i] * 0.85
            prim.append(feather(piv, s * 80, -20, s * roll, L * 0.82, (0.36, L * 0.56, 0.12)))
        fig.add(U(prim, 0.04), INK, k=0.06, layer='wing')
        fig.add(U([feather(piv + np.array((s * 0.1, 0.05, 0.05)), s * 80, -20, s * roll, 0.62, (0.42, 0.55, 0.13)) for roll in (0, 15, 30)], 0.04),
                INK_HI, k=0.06, layer='wing')
    # 꼬리 깃 (뒤 아래로 부채꼴)
    tp = np.array((0, y0 + 1.6, -1.35))
    # 키아트 뒷모습: 등 아래에서 뒤로 뻗은 짧고 둥근 깃 부채 (아래로 늘어지면 바닥의 새 발처럼 보였다)
    fig.add(U([feather(tp, 0, 66, roll, 0.75, (0.3, 0.62, 0.12)) for roll in (-30, -15, 0, 15, 30)], 0.06), INK, k=0.08, layer='tail')
    # 부리 (회색, 위·아래)
    bf = frame_at(head, hc, 0, -10)
    bp = np.array(bf.p((0, 0, -0.12)))
    fwd = np.array(bf.dir((0, 0, 1)))
    up = np.array(bf.dir((0, 1, 0)))
    fig.add(S.capsule(bp + up * 0.05, bp + fwd * 0.85 - up * 0.16, 0.42, 0.07), BEAK, k=0.12, layer='beak')   # 키아트: 짧고 굵은 부리
    fig.add(S.ellipsoid(bp + fwd * 0.18 + up * 0.06, (0.45, 0.28, 0.34)), BEAK, k=0.15, layer='beak')
    fig.add(S.capsule(bp - up * 0.24, bp + fwd * 0.6 - up * 0.32, 0.21, 0.05), (0.40, 0.40, 0.43), k=0.08, layer='beak')
    # 눈·볼
    # 당돌한 아몬드 눈 (눈꼬리 살짝 올라감, 눈썹 능선이 안쪽으로 찡그림)
    # 키아트: 크고 동그란 갈색 눈 + 찡그린 눈썹 능선이 당돌함을 만든다 (아몬드 눈은 졸려 보였다)
    face_eyes(fig, hc, spread=27, pitch=1, style='round', size=0.6, iris=(0.40, 0.22, 0.11), tilt=0)
    for s in (-1, 1):
        p = surf(head, hc, s * 41, -18)
        fig.paint(S.ellipsoid(p, (0.36, 0.26, 0.3)), (1.0, 0.58, 0.64), soft=0.3)
    # 목도리: 목을 감는 두툼한 관 + 매듭 + 두 끝 (술 장식)
    p0 = np.array((0, y0 + 3.75, 0.0))
    nrm = np.array((0, 1.0, 0.3))
    tube_loop(fig, core, p0, nrm, 0.12, 0.42, SCARF, 'scarf', k=0.2)
    knot = surf(core, p0 + np.array((0, -0.1, 0)), 30, -14, 0.45)
    fig.add(S.ellipsoid(knot, (0.45, 0.4, 0.34)), SCARF, k=0.12, layer='scarf')
    pts, ns = [], []
    for t in np.linspace(0, 1, 10):
        yaw = 30 + 6 * t
        q = surf(trunk, np.array((0, knot[1] - 0.2 - 1.7 * t, 0)), yaw, -10 * t, 0.12)
        pts.append(q)
        ns.append(_dir(yaw, -10))
    ribbon(fig, pts, ns[:-1], 0.34, 0.08, SCARF, 'scarf', k=0.14)
    end = pts[-1]
    fringe(fig, end, axes(pts[-1] - pts[-2], ns[-1]), 7, 0.095, 0.38, SCARF_DK)
    st = surf(core, p0, -75, -8, 0.3)
    pts = [st + np.array((-0.28 * i, -0.12 * math.sin(i * 0.9) - 0.04 * i, 0.05 * i)) for i in range(9)]
    ribbon(fig, pts, [np.array((0.0, 0.3, 1.0))] * (len(pts) - 1), 0.3, 0.08, SCARF, 'scarf', k=0.14)
    end = pts[-1]
    fringe(fig, end, axes(pts[-1] - pts[-2], (0, 0.3, 1.0)), 7, 0.085, 0.38, SCARF_DK)
    flower_brooch(fig, knot + _dir(30, -10) * 0.32 + np.array((0, 0.04, 0)), _dir(30, 5), 0.26)
    # 크로스백 끈 (오른 어깨 → 왼 엉덩이, 몸통을 한 바퀴 감는 띠)
    mid = np.array((0, y0 + 2.5, 0))
    sn = np.array((-1.5, 2.0, 0.0))
    sn /= np.linalg.norm(sn)
    strap = lambda P: np.maximum(np.maximum(np.abs(trunk(P) - 0.07) - 0.06, np.abs((P - mid) @ sn) - 0.14), P[:, 1] - (y0 + 3.7))
    fig.add(strap, BAG, k=0.0, layer='strap')
    # 가방 (왼쪽 배 앞): 몸통 + 덮개 + 은 단추 + 병뚜껑·카드
    bc = surf(trunk, np.array((0, y0 + 1.7, 0)), -34, -4, 0.36)
    bR = S.rot(-34, -4, 0)
    fig.add(S.box(bc, (0.74, 0.58, 0.36), round_=0.22, R=bR), BAG, k=0.0, layer='bag')
    fl = bc + bR @ np.array((0, 0.2, 0.35))
    fig.add(S.intersect(S.box(fl, (0.76, 0.32, 0.06), round_=0.05, R=bR), S.ellipsoid(fl + bR @ np.array((0, 0.3, 0)), (0.82, 0.62, 0.5), R=bR)),
            (0.30, 0.58, 0.90), k=0.0, layer='flap')
    fig.add(S.sphere(fl + bR @ np.array((0, -0.16, 0.06)), 0.09), SILVER, k=0.0, layer='btn', metal=SIL)
    for s in (-1, 1):
        fig.add(S.torus(bc + bR @ np.array((s * 0.72, 0.5, 0)), 0.1, 0.035, Rm=bR @ S.rot(0, 0, 90)), SILVER, k=0.0, layer='btn', metal=SIL)
    for (dx, dz, tilt, col, met) in ((-0.36, 0.08, 22, Fg.GOLD, GLD), (0.02, -0.04, -8, SILVER, SIL), (0.36, 0.1, -24, Fg.GOLD, GLD)):
        cc = bc + bR @ np.array((dx, 0.62, dz))
        bottle_cap(fig, cc, bR @ S.rot(0, 72, tilt), 0.22, col, met, layer='cap')
    fig.add(S.box(bc + bR @ np.array((0.12, 0.82, -0.14)), (0.3, 0.38, 0.03), round_=0.025, R=bR @ S.rot(0, -8, -8)), CARD, k=0.0, layer='card')
    # 받침 소품: 달 돌 (오른발 옆), 톱니 2개 (앞)
    moon_rock(fig, (2.55, y0 + 0.35, 0.8), 0.75)
    gear(fig, (0.85, y0 + 0.24, 3.1), 0.52)
    gear(fig, (1.95, y0 + 0.22, 3.25), 0.34)
