"""
고품질 수집 동물 피규어 (SDF 조형, sculpt.py). 키아트 3D 레퍼런스 시트를 보고 만든다.

한 캐릭터 = 몸통 조형(부드럽게 녹아 붙은 하나의 표면, 접촉 그늘 구움) + 얼굴 부품(눈·코·입, 또렷한 별도 메시)
           + 받침(등급 색 금속 테 + 잔디 + 꽃).
좌표: 받침 중심 원점, +Y 위, +Z 얼굴 앞, m. 받침 윗면 y = TOP.
"""
import math
import random

import numpy as np

from . import sculpt as S
from .mesh import Builder, Frame, IDENT

TOP = 1.05

# ---------- 팔레트 (sRGB) ----------
WHITE = (0.985, 0.975, 0.965)
SUIT = (0.96, 0.955, 0.95)
GOLD = (0.98, 0.70, 0.10)
GOLD_HI = (1.0, 0.93, 0.55)
SILVER = (0.80, 0.82, 0.86)
PINK = (0.99, 0.70, 0.74)
BLUSH = (1.0, 0.62, 0.66)
IRIS = (0.36, 0.20, 0.11)
IRIS_HI = (0.62, 0.38, 0.20)
PUPIL = (0.07, 0.04, 0.04)
SHINE = (1.0, 1.0, 1.0)
CRIMSON = (0.72, 0.12, 0.20)
RED = (0.88, 0.20, 0.20)
GRASS = (0.42, 0.72, 0.22)
GRASS_HI = (0.62, 0.86, 0.34)
RIMS = {'green': ((0.36, 0.74, 0.30), (0.70, 0.94, 0.56)), 'blue': ((0.25, 0.55, 0.92), (0.66, 0.86, 1.0)),
        'gold': ((0.95, 0.62, 0.08), (1.0, 0.90, 0.45))}


# ---------- 금속·광택 색 (버텍스 색으로 굽는 가짜 반사) ----------

def metal_shade(C, N, base, hi):
    """금속 부위: 위를 볼수록 밝은 반사, 옆 아래는 짙은 색 + 가로 반사 띠"""
    ny = N[:, 1]
    nz = N[:, 2]
    t = np.clip(0.55 + 0.5 * ny + 0.15 * nz, 0, 1) ** 1.4
    band = np.exp(-((ny - 0.35) / 0.1) ** 2) * 0.4 + np.exp(-((ny + 0.25) / 0.08) ** 2) * 0.1
    dark = np.asarray(base) * 0.8
    col = dark[None, :] * (1 - t[:, None]) + np.asarray(hi)[None, :] * t[:, None]
    return np.clip(col + band[:, None] * 0.6, 0, 1)


def vivid(C, sat=1.22, contrast=1.06):
    """색을 확실하게: 채도를 올리고 살짝 대비를 준다 (흰색·검정은 그대로 두고 색 있는 곳만 또렷해진다)"""
    C = np.asarray(C, np.float64)
    L = (C @ np.array([0.299, 0.587, 0.114]))[:, None]
    C = L + (C - L) * sat
    C = 0.5 + (C - 0.5) * contrast
    return np.clip(C, 0, 1)


class Figure:
    """조형 + 금속 부위 표시 + 얼굴 부품 Builder"""

    def __init__(self, tier):
        self.s = S.Sculpt()
        self.metal = []           # (part index, base, hi)
        self.extra = Builder()    # 눈·코·입 등 또렷한 부품
        self.extra.ao_strength = 0.0
        self.smooth = ([], [], [])   # 법선을 직접 계산한 부품 (받침): pos, nrm, col
        self.glass = ([], [], [])    # 투명 유리 (헬멧 바이저): 따로 __glass 메시로 낸다
        self.eye_spots = []          # (중심, 반지름): 지도용 간략화에서 빼는 눈 자리 (_reduce_extra)
        self.tier = tier

    def add(self, f, col, k=0.2, layer='body', metal=None):
        self.s.add(f, col, k, layer)
        if metal:
            self.metal.append((len(self.s.parts) - 1, metal[0], metal[1]))

    def paint(self, f, col, soft=0.05):
        self.s.paint(f, col, soft)

    def build(self, tris, voxel=0.04, ao=0.55, lo=(-4.8, -0.05, -4.8), hi=(4.8, 12.6, 4.8)):
        V, F, C = self.s.build(lo, hi, voxel, tris, ao=ao)
        C = vivid(C)
        if self.metal:
            dmin, per = self.s.field(V, with_parts=True)
            D = np.abs(np.stack(per, axis=1))
            owner = D.argmin(axis=1)
            N = self.s.normals(V)
            for idx, base, hi_ in self.metal:
                m = owner == idx
                if m.any():
                    ao_keep = C[m] / np.maximum(np.asarray(self.s.parts[idx][1])[None, :], 1e-3)
                    shade = np.clip(ao_keep.mean(axis=1, keepdims=True), 0.5, 1.0)
                    C[m] = metal_shade(C[m], N[m], base, hi_) * shade
        return V, F, C


# ---------- 공통: 받침 ----------

def lathe(fig, profile, col_fn, segs=96):
    segs = max(32, int(segs * LOD)) if LOD < 1.0 else segs
    """회전체 (profile = [(반지름, 높이)...] 아래→위), 법선은 단면 접선에서 정확히 계산 → 아주 매끈한 받침"""
    pr = np.asarray(profile, np.float64)
    tan = np.gradient(pr, axis=0)
    nrm2 = np.stack([tan[:, 1], -tan[:, 0]], axis=1)
    nrm2 /= np.maximum(np.linalg.norm(nrm2, axis=1, keepdims=True), 1e-9)
    b = fig.smooth
    for j in range(segs):
        a0, a1 = 2 * math.pi * j / segs, 2 * math.pi * (j + 1) / segs
        for i in range(len(pr) - 1):
            quad = []
            for (k, a) in ((i, a0), (i, a1), (i + 1, a1), (i + 1, a0)):
                r, y = pr[k]
                nr, ny = nrm2[k]
                quad.append(((r * math.cos(a), y, r * math.sin(a)), (nr * math.cos(a), ny, nr * math.sin(a)), col_fn(k, y, (nr, ny))))
            for t in ((0, 2, 1), (0, 3, 2)):
                for v in t:
                    p, n, c = quad[v]
                    b[0].extend(p); b[1].extend(n); b[2].extend(c)


def base(fig, rng, flowers=7):
    rim, rim_hi = RIMS[fig.tier]
    prof = []
    R0, H0, rr = 4.5, 1.0, 0.22
    prof.append((0.0, 0.0))
    for k in range(6):                                        # 아래 둥근 모서리
        a = -math.pi / 2 + (math.pi / 2) * k / 5
        prof.append((R0 - rr + math.cos(a) * rr, rr + math.sin(a) * rr))
    for k in range(6):                                        # 위 둥근 모서리
        a = (math.pi / 2) * k / 5
        prof.append((R0 - rr + math.cos(a) * rr, H0 - rr + math.sin(a) * rr))
    prof.append((R0 - 0.35, H0 - 0.02))                       # 잔디 쪽으로 살짝 들어간 턱
    prof = [prof[0]] + prof[1:]

    def rim_col(k, y, n):
        ny = n[1]
        t = np.clip(0.55 + 0.45 * ny, 0, 1) ** 2
        band = math.exp(-((y - 0.62) / 0.09) ** 2) * 0.55 + math.exp(-((y - 0.2) / 0.12) ** 2) * 0.15
        c = np.asarray(rim) * (0.55 + 0.35 * t) + np.asarray(rim_hi) * (t * 0.45 + band)
        return tuple(np.clip(c, 0, 1))
    lathe(fig, prof, rim_col)
    grass_top = lambda P: S.cylinder((0, TOP - 0.12, 0), 4.22, 0.14, round_=0.12)(P) - 0.05 * np.sin(P[:, 0] * 3.1) * np.sin(P[:, 2] * 2.7)
    fig.add(grass_top, GRASS, k=0.0, layer='grass')
    fig.paint(lambda P: 0.15 - np.abs(np.sin(P[:, 0] * 1.7 + P[:, 2] * 1.3) * np.cos(P[:, 2] * 1.9)) - (TOP - 0.3 - P[:, 1]) * 9, GRASS_HI, soft=0.4)
    fig.paint(lambda P: 0.2 - np.abs(np.sin(P[:, 0] * 4.1 - P[:, 2] * 2.3) * np.sin(P[:, 2] * 3.7 + 1.0)) - (TOP - 0.3 - P[:, 1]) * 9, (0.30, 0.56, 0.14), soft=0.3)
    b = fig.extra
    for k in range(int(260 * LOD)):   # 잔디 덤불 (잎 3장씩)
        a = rng.uniform(0, 2 * math.pi)
        d = math.sqrt(rng.uniform(0.02, 1.0)) * 4.1
        x, z = math.cos(a) * d, math.sin(a) * d
        for j in range(3):
            h = rng.uniform(0.28, 0.6)
            tilt = rng.uniform(-25, 25)
            f = Frame((x, TOP - 0.08, z), *[tuple(S.rot(rng.uniform(0, 360), tilt, rng.uniform(-20, 20))[:, i]) for i in range(3)])
            b.cone(f, (0, 0, 0), 0.08, h, 3 if LOD < 1.0 else 4, ((0.62, 0.86, 0.30), (0.46, 0.76, 0.22), (0.34, 0.62, 0.16))[(k + j) % 3])
    placed = 0
    flowers = max(3, int(round(flowers * min(1.0, LOD * 1.5))))
    while placed < flowers:   # 데이지 (흰 꽃잎 8장 + 노란 가운데)
        a = rng.uniform(0, 2 * math.pi)
        d = rng.uniform(2.4, 3.95)
        x, z = math.cos(a) * d, math.sin(a) * d
        placed += 1
        for p in range(8):
            pa = p * math.pi / 4
            _ellipsoid(b, (x + math.cos(pa) * 0.26, TOP + 0.3, z + math.sin(pa) * 0.26), (0.18, 0.05, 0.1), WHITE, 8, 4,
                       Frame((0, 0, 0), (math.cos(pa), 0, math.sin(pa)), (0, 1, 0), (-math.sin(pa), 0, math.cos(pa))) if False else IDENT)
        _ellipsoid(b, (x, TOP + 0.33, z), (0.12, 0.08, 0.12), (1.0, 0.76, 0.16), 8, 4)


# ---------- 또렷한 부품 (Builder) ----------

LOD = 1.0   # 세부 정도: 1 = 도감용 고품질, 0.4 = 지도용 (작은 부품 면 수·잔디 수를 줄인다). build() 가 정한다


def _ellipsoid(mb, c, r, col, seg=16, rings=10, m=IDENT):
    if LOD < 1.0:
        seg = max(6, int(round(seg * LOD ** 0.5 * 0.85)))
        rings = max(3, int(round(rings * LOD ** 0.5 * 0.85)))

    def P(i, j):
        th = -math.pi / 2 + math.pi * i / rings
        ph = 2 * math.pi * j / seg
        return (c[0] + r[0] * math.cos(th) * math.cos(ph), c[1] + r[1] * math.sin(th), c[2] + r[2] * math.cos(th) * math.sin(ph))
    for i in range(rings):
        for j in range(seg):
            a, b_, d, e = P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1)
            if i < rings - 1:
                mb.tri(m, a, b_, d, col, c)
            if i > 0:
                mb.tri(m, a, d, e, col, c)


def _frame_on(center, R, yaw, pitch, out=0.0):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    p = (center[0] + (R + out) * cp * sy, center[1] + (R + out) * sp, center[2] + (R + out) * cp * cy)
    Rm = S.rot(yaw, -pitch, 0)
    return Frame(p, tuple(Rm[:, 0]), tuple(Rm[:, 1]), tuple(Rm[:, 2]))


def kawaii_eyes(fig, head_c, R, spread=24, pitch=-12, size=0.62, iris=IRIS, tall=1.12, look=0.0):
    """키아트 눈: 큰 갈색 홍채(아래로 밝아짐) + 검은 동공 + 큰 흰 반짝임 + 작은 반짝임, 살짝 볼록"""
    b = fig.extra
    for s in (-1, 1):
        f = _frame_on(head_c, R, s * spread, pitch, -size * 0.18)
        _ellipsoid(b, (0, 0, 0), (size * 0.9, size * tall, size * 0.34), PUPIL, 20, 10, f)
        _ellipsoid(b, (0, -size * 0.18, size * 0.06), (size * 0.78, size * tall * 0.78, size * 0.32), iris, 20, 10, f)
        _ellipsoid(b, (0, -size * 0.42, size * 0.1), (size * 0.5, size * 0.38, size * 0.28), IRIS_HI if iris == IRIS else tuple(min(1, c * 1.5) for c in iris), 16, 8, f)
        _ellipsoid(b, (0, size * 0.05, size * 0.16), (size * 0.42, size * 0.5, size * 0.28), PUPIL, 16, 8, f)
        _ellipsoid(b, (-size * 0.3 * s + look, size * 0.36, size * 0.3), (size * 0.3, size * 0.3, size * 0.12), SHINE, 12, 6, f)
        _ellipsoid(b, (size * 0.32 * s, -size * 0.45, size * 0.3), (size * 0.12, size * 0.12, size * 0.06), SHINE, 8, 4, f)


def eye_at(fig, f, style='round', size=0.45, iris=IRIS, side=1, tilt=0.0, lid=None):
    """눈 하나 (_eye_at). 지도용(LOD < 1)에서도 눈은 조금 더 촘촘히: 거친 다각형이면 동공이 홍채 밖으로 삐죽 나온다"""
    global LOD
    keep = LOD
    LOD = max(LOD, 0.5)
    try:
        _eye_at(fig, f, style, size, iris, side, tilt, lid)
    finally:
        LOD = keep


def _eye_at(fig, f, style='round', size=0.45, iris=IRIS, side=1, tilt=0.0, lid=None):
    """
    한쪽 눈. f = 표면 위 좌표계 (로컬 +Z 바깥, +Y 위) — 머리가 구가 아니어도 실제 표면 점에서 만든 Frame 을 넘긴다.
    style (캐릭터 개성에 맞춰 고른다):
      'round'    둥근 키아트 눈 (홍채 + 동공 + 반짝임 2개)        — 기본, 순한 성격
      'almond'   아몬드형, 눈꼬리 올라감 (tilt 로 기울기)          — 영리·당돌 (여우, 까마귀, 진돗개)
      'bead'     작고 반짝이는 까만 구슬 눈                        — 작은 동물 (오리, 고슴도치, 두더지)
      'big_dark' 홍채가 거의 다 검은 큰 눈, 큰 반짝임              — 야행성 (날다람쥐)
      'cat'      큰 홍채 + 세로로 긴 동공                          — 고양이
      'ring'     흰 눈테(아이링) 두른 눈                           — 앵무새, 비둘기
      'closed'   ∪ 모양으로 웃으며 감은 눈                         — 졸린·행복 (햄스터)
      'droopy'   위 눈꺼풀이 반쯤 덮은 멍한 눈 (lid = 눈꺼풀 색)   — 비둘기, 달팽이
    """
    b = fig.extra
    s = size
    fig.eye_spots.append((np.asarray(f.o, np.float64), size * 2.0))   # 흰자·눈테·눈꺼풀까지 덮는 반지름
    rot = Frame(f.o, *[tuple(c) for c in (np.array(f.x) * math.cos(math.radians(tilt * side)) + np.array(f.y) * math.sin(math.radians(tilt * side)),
                                         -np.array(f.x) * math.sin(math.radians(tilt * side)) + np.array(f.y) * math.cos(math.radians(tilt * side)),
                                         np.array(f.z))])
    hi = tuple(min(1, c * 1.55) for c in iris)
    if style == 'closed':
        for k in range(11):
            a = math.pi * k / 10
            _ellipsoid(b, (math.cos(a) * s * 0.75, -math.sin(a) * s * 0.42 + s * 0.15, 0.02), (s * 0.13, s * 0.13, s * 0.08), PUPIL, 6, 4, rot)
        return
    if style == 'bead':
        _ellipsoid(b, (0, 0, 0), (s * 0.55, s * 0.62, s * 0.3), PUPIL, 16, 8, rot)
        _ellipsoid(b, (-s * 0.18 * side, s * 0.2, s * 0.22), (s * 0.18, s * 0.18, s * 0.08), SHINE, 8, 4, rot)
        return
    w, h = {'round': (0.9, 1.08), 'almond': (1.05, 0.72), 'big_dark': (1.0, 1.12), 'cat': (0.95, 1.0), 'ring': (0.82, 0.9), 'droopy': (0.95, 1.0)}[style]
    if style == 'ring':
        _ellipsoid(b, (0, 0, -s * 0.05), (s * w * 1.35, s * h * 1.35, s * 0.3), (0.97, 0.96, 0.92), 20, 10, rot)
    _ellipsoid(b, (0, 0, 0), (s * w, s * h, s * 0.34), PUPIL, 22, 10, rot)
    if style != 'big_dark':
        _ellipsoid(b, (0, -s * 0.12, s * 0.06), (s * w * 0.84, s * h * 0.8, s * 0.32), iris, 20, 10, rot)
        _ellipsoid(b, (0, -s * h * 0.42, s * 0.1), (s * w * 0.55, s * h * 0.32, s * 0.27), hi, 16, 8, rot)
    pupil = (s * w * 0.18, s * h * 0.62) if style == 'cat' else (s * w * 0.46, s * h * 0.5)
    _ellipsoid(b, (0, s * 0.02, s * 0.15), (pupil[0], pupil[1], s * 0.27), PUPIL, 16, 8, rot)
    big = 0.38 if style == 'big_dark' else 0.3
    _ellipsoid(b, (-s * w * 0.32 * side, s * h * 0.36, s * 0.3), (s * big, s * big, s * 0.12), SHINE, 12, 6, rot)
    _ellipsoid(b, (s * w * 0.34 * side, -s * h * 0.42, s * 0.3), (s * 0.12, s * 0.12, s * 0.06), SHINE, 8, 4, rot)
    if style == 'droopy' or lid:
        _ellipsoid(b, (0, s * h * 0.5, s * 0.08), (s * w * 1.08, s * h * 0.62, s * 0.42), lid or (0.7, 0.7, 0.75), 18, 8, rot)


def surface_frame(fig, origin, direction, up=(0, 1, 0), out=0.0, layer=None):
    """
    origin 에서 direction 으로 광선을 쏴 조형 표면(layer 만, 없으면 전체)에 닿은 점의 Frame. 눈·코·입을 실제 표면에 붙일 때 쓴다
    (머리가 타원·달걀·납작한 모양이어도 묻히거나 뜨지 않게).
    """
    o = np.asarray(origin, np.float64)
    d = np.asarray(direction, np.float64)
    d = d / np.linalg.norm(d)
    parts = [p for p in fig.s.parts if layer is None or p[3] == layer]

    def field(P):
        out_ = None
        for f, _, k, _ in parts:
            v = f(P)
            out_ = v if out_ is None else S.smin(out_, v, k)
        return out_
    if field(o[None, :])[0] < 0:
        # 안(머리 중심 등)에서 시작하면: 밖(d 방향 멀리)에서 거꾸로 쏴서 가장 바깥 표면을 찾는다
        o, d = o + d * 12.0, -d
    t = 0.0
    for _ in range(400):
        dist = field((o + d * t)[None, :])[0]
        if dist < 1e-3:
            break
        t += max(dist * 0.9, 1e-3)
    p = o + d * t
    e = np.eye(3) * 1e-3
    n = np.array([field((p + e[i])[None, :])[0] - field((p - e[i])[None, :])[0] for i in range(3)])
    n /= np.linalg.norm(n) or 1
    p = p + n * out
    x = np.cross(np.asarray(up, np.float64), n)
    x /= np.linalg.norm(x) or 1
    y = np.cross(n, x)
    return Frame(tuple(p), tuple(x), tuple(y), tuple(n))


def smile(fig, head_c, R, pitch=-30, w=0.32, col=(0.30, 0.16, 0.16), open_=False):
    """'w' 입 (작은 곡선 두 개). open_ 이면 분홍 혀가 보이는 열린 입"""
    b = fig.extra
    f = _frame_on(head_c, R, 0, pitch, -0.04)
    if open_:
        _ellipsoid(b, (0, -w * 0.15, 0), (w * 0.9, w * 0.75, 0.1), (0.42, 0.14, 0.16), 14, 8, f)
        _ellipsoid(b, (0, -w * 0.45, 0.04), (w * 0.6, w * 0.35, 0.08), (0.98, 0.50, 0.55), 12, 6, f)
        return
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            _ellipsoid(b, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.02), (0.055, 0.055, 0.04), col, 6, 4, f)


def nose(fig, head_c, R, pitch=-20, size=0.16, col=PINK):
    f = _frame_on(head_c, R, 0, pitch, -0.02)
    _ellipsoid(fig.extra, (0, 0, 0), (size * 1.2, size * 0.8, size * 0.6), col, 12, 6, f)


def blush(fig, head_c, R, spread=46, pitch=-24, size=0.55):
    for s in (-1, 1):
        p = np.array(_frame_on(head_c, R, s * spread, pitch, 0).p((0, 0, 0)))
        fig.paint(S.sphere(p, size), BLUSH, soft=size * 1.1)


# ---------- 공학관 우주 토끼 ----------

FACE = (0.995, 0.985, 0.975)
EAR_PINK = (1.0, 0.66, 0.72)
SUIT_GREY = (0.70, 0.72, 0.76)


def sphere_cap(fig, c, R, z_cut, segs=64, rings=24):
    if LOD < 1.0:
        segs, rings = max(24, int(segs * LOD)), max(10, int(rings * LOD))
    """유리 바이저: 중심 c, 반지름 R 구에서 z >= z_cut (로컬, 앞) 부분. 법선 정확 (투명 재질 메시)"""
    b = fig.glass
    t0 = math.acos(max(-1.0, min(1.0, z_cut / R)))
    def P(i, j):
        th = t0 * i / rings              # 앞쪽 극(+Z)에서의 각
        ph = 2 * math.pi * j / segs
        n = (math.sin(th) * math.cos(ph), math.sin(th) * math.sin(ph), math.cos(th))
        return (c[0] + R * n[0], c[1] + R * n[1], c[2] + R * n[2]), n
    for i in range(rings):
        for j in range(segs):
            q = [P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1)]
            for t in ((0, 2, 1), (0, 3, 2)):
                for v in t:
                    b[0].extend(q[v][0]); b[1].extend(q[v][1]); b[2].extend((0.92, 0.97, 1.0))


def space_rabbit(fig, rng):
    base(fig, rng, flowers=9)
    W = SUIT
    g = (GOLD, GOLD_HI)
    y0 = TOP
    # ---- 다리: 통통한 장화 + 무릎 금색 고리 + 무릎 주름 ----
    for s in (-1, 1):
        x = s * 0.72
        fig.add(S.ellipsoid((x, y0 + 0.42, 0.22), (0.66, 0.45, 0.85)), W, k=0.2)                      # 장화 앞코
        fig.add(S.capsule((x, y0 + 0.5, 0.05), (x, y0 + 1.25, 0.0), 0.62, 0.6), W, k=0.25)            # 장화
        fig.add(S.capsule((x, y0 + 1.35, 0.0), (x * 1.02, y0 + 2.1, 0.0), 0.56, 0.6), W, k=0.25)      # 정강이
        fig.add(S.torus((x, y0 + 1.3, 0.0), 0.6, 0.11), GOLD, k=0.0, layer='trim', metal=g)
    # ---- 몸통: 위가 살짝 넓은 우주복, 가슴판 ----
    fig.add(S.ellipsoid((0, y0 + 2.45, 0.0), (1.38, 0.75, 1.1)), W, k=0.4)                           # 엉덩이
    fig.add(S.ellipsoid((0, y0 + 3.35, 0.0), (1.48, 1.05, 1.15)), W, k=0.45)                          # 가슴
    fig.add(S.intersect(S.ellipsoid((0, y0 + 2.62, 0.0), (1.47, 0.85, 1.19)), S.box((0, y0 + 2.62, 0), (3, 0.13, 3))), GOLD, k=0.0, layer='trim', metal=g)   # 벨트
    for x in (-0.95, 0.0, 0.95):                                                                     # 벨트 버클 3개
        R = S.rot(math.degrees(math.atan2(x, 1.05)), 0, 0)
        p = (x * 1.0, y0 + 2.62, math.sqrt(max(0.0, 1.0 - (x / 1.47) ** 2)) * 1.19 + 0.02)
        fig.add(S.box(p, (0.2, 0.17, 0.07), round_=0.05, R=R), SUIT_GREY if x else GOLD, k=0.0, layer='buckle',
                metal=None if x else g)
    fig.add(S.ellipsoid((0, y0 + 3.45, 1.05), (0.34, 0.34, 0.12)), (0.98, 0.98, 0.99), k=0.0, layer='chest')
    fig.add(S.torus((0, y0 + 3.45, 1.1), 0.33, 0.07, Rm=S.rot(0, 90 - 12, 0)), GOLD, k=0.0, layer='trim', metal=g)
    for s in (-1, 1):   # 가슴 멜빵 (어깨 → 벨트)
        pts = [(s * 0.62, y0 + 4.1, 0.55), (s * 0.66, y0 + 3.7, 1.0), (s * 0.68, y0 + 3.15, 1.15), (s * 0.7, y0 + 2.7, 1.2)]
        for a_, b_ in zip(pts, pts[1:]):
            fig.add(S.capsule(a_, b_, 0.065), GOLD, k=0.02, layer='trim', metal=g)
        fig.add(S.box((s * 1.15, y0 + 2.35, 0.75), (0.22, 0.28, 0.14), round_=0.08, R=S.rot(s * 40, 0, 0)), SUIT_GREY, k=0.04, layer='buckle')   # 허리 주머니
    # ---- 팔: 어깨 → 팔꿈치 고리 → 손목 금색 → 흰 장갑 ----
    for s in (-1, 1):
        sh, el, wr = (s * 1.3, y0 + 3.85, 0.0), (s * 1.75, y0 + 3.05, 0.15), (s * 1.95, y0 + 2.35, 0.3)
        fig.add(S.sphere(sh, 0.58), W, k=0.3)
        fig.add(S.capsule(sh, el, 0.48, 0.44), W, k=0.25)
        fig.add(S.capsule(el, wr, 0.44, 0.42), W, k=0.2)
        fig.add(S.torus(wr, 0.44, 0.12, Rm=S.rot(0, 0, s * 18)), GOLD, k=0.0, layer='trim', metal=g)
        hand = (s * 2.02, y0 + 1.95, 0.38)
        fig.add(S.sphere(hand, 0.43), WHITE, k=0.12, layer='glove')
        fig.add(S.capsule((s * 1.85, y0 + 2.05, 0.65), (s * 1.92, y0 + 1.8, 0.75), 0.15), WHITE, k=0.1, layer='glove')
    for k in range(2):   # 왼팔 빨간 패치
        fig.add(S.box((-1.5, y0 + 3.55 - k * 0.17, 0.38), (0.06, 0.05, 0.22), round_=0.025, R=S.rot(-62, 0, -28)), RED, k=0.0, layer='decal')
    fig.add(S.sphere((1.18, y0 + 3.95, 0.72), 0.08), CRIMSON, k=0.0, layer='decal')
    # 금색 코일 호스 (벨트 앞 → 등)
    for k in range(34):
        t = k / 33
        x, y, z = -1.5 - 0.32 * math.sin(t * math.pi), y0 + 2.5 - 0.7 * math.sin(t * math.pi), 0.95 - 2.1 * t
        a = t * 30 * math.pi
        fig.add(S.sphere((x + math.cos(a) * 0.09, y + math.sin(a) * 0.09, z), 0.09), GOLD, k=0.0, layer='trim', metal=g)
    fig.add(S.torus((0, y0 + 4.32, 0.0), 0.95, 0.2), (0.95, 0.96, 0.98), k=0.0, layer='trim')          # 목 고리 (흰 옷깃: 금색은 얼굴 아래 갈색 얼룩처럼 보였다)
    # ---- 머리: 토끼 얼굴 (유리 안) + 뒤쪽 흰 헬멧 껍질 + 귀 ----
    hc = (0, y0 + 6.2, 0.05)
    HR = 2.3                                     # 헬멧 반지름
    cut = 0.55                                   # 바이저 경계 (로컬 z)
    shell = S.subtract(S.sphere(hc, HR), S.box((0, hc[1], hc[2] + cut + 3), (4, 4, 3)))
    shell = S.subtract(shell, S.sphere(hc, HR - 0.14))
    fig.add(shell, W, k=0.0, layer='helmet')
    fc, FR = (0, hc[1] - 0.12, hc[2] + 0.12), 1.95
    # 두상: 세로로 살짝 긴 타원, 아래쪽(턱·볼)이 자연스럽게 넓어지는 형태 — 따로 붙인 볼살 없음
    fig.add(S.ellipsoid(fc, (FR * 1.02, FR * 1.0, FR * 0.96)), FACE, k=0.0, layer='face')
    fig.add(S.ellipsoid((0, fc[1] - 0.55, fc[2] + 0.1), (FR * 1.08, FR * 0.66, FR * 0.9)), FACE, k=0.7, layer='face')
    ring_r = math.sqrt(HR ** 2 - cut ** 2)
    fig.add(S.torus((0, hc[1], hc[2] + cut), ring_r, 0.17, Rm=S.rot(0, 90, 0)), (0.95, 0.96, 0.98), k=0.0, layer='ring',
            metal=((0.82, 0.84, 0.88), (1.0, 1.0, 1.0)))
    for s in (-1, 1):   # 귀: 헬멧 위로 뚫고 나온 흰 귀 + 오목한 분홍 안쪽 + 헬멧 귀 고리
        ear = S.ellipsoid((s * 0.98, hc[1] + 2.95, -0.35), (0.8, 1.38, 0.52), R=S.rot(0, 0, -s * 8))
        inner = S.ellipsoid((s * 1.0, hc[1] + 3.05, 0.14), (0.5, 1.02, 0.3), R=S.rot(0, 0, -s * 8))
        fig.add(S.subtract(ear, inner, k=0.1), W, k=0.35, layer='helmet')
        fig.paint(S.ellipsoid((s * 1.0, hc[1] + 3.05, 0.06), (0.56, 1.08, 0.42), R=S.rot(0, 0, -s * 8)), EAR_PINK, soft=0.05)
        fig.add(S.torus((s * HR * 0.99, hc[1] - 0.15, hc[2] - 0.2), 0.5, 0.15, Rm=S.rot(0, 0, 90)), GOLD, k=0.0, layer='trim', metal=g)
        fig.add(S.ellipsoid((s * HR * 0.99, hc[1] - 0.15, hc[2] - 0.2), (0.14, 0.4, 0.4)), (0.97, 0.97, 0.98), k=0.0, layer='port')
    fig.add(S.sphere((0, hc[1] + HR + 0.02, hc[2] - 0.15), 0.22), (0.78, 0.48, 0.20), k=0.08, layer='helmet')   # 꼭대기 단추
    sphere_cap(fig, hc, HR - 0.04, cut)
    # 얼굴: 키아트처럼 작고 동그란 눈, 넓은 간격, 분홍 코·'w' 입·볼터치
    for s in (-1, 1):   # 눈: 실제 얼굴 표면에 (round, 넓은 간격)
        d = (math.sin(math.radians(s * 27)), math.sin(math.radians(-3)), 1.0)
        f = surface_frame(fig, fc, d, layer='face', out=-0.06)
        eye_at(fig, f, 'round', size=0.47, side=s)
    f = surface_frame(fig, fc, (0, -0.28, 1), layer='face', out=0.02)
    _ellipsoid(fig.extra, (0, 0, 0), (0.15, 0.1, 0.07), PINK, 12, 6, f)
    f = surface_frame(fig, fc, (0, -0.44, 1), layer='face', out=-0.01)
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            _ellipsoid(fig.extra, (s * 0.12 + math.cos(a) * 0.12, -math.sin(a) * 0.1, 0.02), (0.045, 0.045, 0.035), (0.30, 0.16, 0.16), 6, 4, f)
    for s in (-1, 1):   # 볼터치: 칠만 (돌출 없음)
        f = surface_frame(fig, fc, (math.sin(math.radians(s * 42)), -0.38, 0.85), layer='face')
        fig.paint(S.sphere(f.o, 0.5), BLUSH, soft=0.6)
    # ---- 제트팩 ----
    fig.add(S.box((0, y0 + 3.3, -1.42), (0.95, 0.95, 0.42), round_=0.25), (0.94, 0.94, 0.96), k=0.06, layer='pack')
    fig.add(S.torus((0, y0 + 3.6, -1.86), 0.27, 0.08, Rm=S.rot(0, 90, 0)), GOLD, k=0.0, layer='trim', metal=g)
    fig.add(S.sphere((0, y0 + 3.6, -1.83), 0.19), GOLD, k=0.0, layer='trim', metal=g)
    for k in range(3):
        fig.add(S.box((0, y0 + 3.08 - k * 0.16, -1.86), (0.3, 0.035, 0.05), round_=0.02), SUIT_GREY, k=0.0, layer='decal')
    for s in (-1, 1):
        fig.add(S.cylinder((s * 1.05, y0 + 3.2, -1.42), 0.28, 0.6, round_=0.12), (0.9, 0.9, 0.92), k=0.04, layer='pack')
        fig.add(S.capsule((s * 1.05, y0 + 2.55, -1.42), (s * 1.05, y0 + 2.2, -1.42), 0.26, 0.34), GOLD, k=0.0, layer='trim', metal=g)
    # ---- 받침 소품: 로켓 · 달 돌 · 톱니 ----
    rx, rz = -3.0, 1.5
    fig.add(S.capsule((rx, y0 + 0.4, rz), (rx, y0 + 1.45, rz), 0.44, 0.38), WHITE, k=0.0, layer='prop')   # 위 반구가 빨간 코 밖으로 비치지 않게
    fig.add(S.capsule((rx, y0 + 1.62, rz), (rx, y0 + 2.45, rz), 0.39, 0.04), RED, k=0.0, layer='prop2')
    fig.add(S.torus((rx, y0 + 1.2, rz + 0.41), 0.17, 0.06, Rm=S.rot(0, 90, 0)), GOLD, k=0.0, layer='prop3', metal=g)
    fig.add(S.sphere((rx, y0 + 1.2, rz + 0.37), 0.13), (0.25, 0.25, 0.3), k=0.0, layer='prop3')
    for k in range(3):
        R = S.rot(k * 120 + 30, 0, 0)
        fig.add(S.box(tuple(np.array((rx, y0 + 0.5, rz)) + R @ np.array((0.52, 0, 0))), (0.24, 0.34, 0.05), round_=0.04, R=R), RED, k=0.0, layer='prop2')
    fig.add(S.capsule((rx, y0 + 0.1, rz), (rx, y0 + 0.4, rz), 0.3, 0.36), SILVER, k=0.0, layer='prop3', metal=(SILVER, (1, 1, 1)))
    rock = lambda P: S.sphere((2.8, y0 + 0.4, 1.7), 0.8)(P) + 0.03 * np.sin(P[:, 0] * 9) * np.sin(P[:, 2] * 8)
    craters = [((2.45, y0 + 0.82, 2.25), 0.2), ((3.3, y0 + 0.85, 1.85), 0.17), ((2.75, y0 + 1.12, 1.35), 0.15), ((2.25, y0 + 0.5, 2.35), 0.14)]
    def rock_f(P, rock=rock):
        d = rock(P)
        for cc, rr in craters:
            d = np.maximum(d, -(S.sphere(cc, rr)(P)))
        return d
    fig.add(rock_f, (0.68, 0.68, 0.70), k=0.0, layer='rock')
    for gc, gr in (((1.5, y0 + 0.12, 2.9), 0.44), ((2.35, y0 + 0.1, 3.3), 0.28)):
        fig.add(S.subtract(S.cylinder(gc, gr, 0.12, round_=0.04), S.cylinder(gc, gr * 0.38, 0.3)), SILVER, k=0.0, layer='gear', metal=(SILVER, (1, 1, 1)))
        for k in range(8):
            R = S.rot(k * 45, 0, 0)
            fig.add(S.box(tuple(np.array(gc) + R @ np.array((gr * 1.12, 0, 0))), (gr * 0.22, 0.11, gr * 0.2), round_=0.03, R=R), SILVER, k=0.0, layer='gear', metal=(SILVER, (1, 1, 1)))
    # ---- 오른손 스패너 (금색 손잡이 + 은색 머리) ----
    fig.add(S.capsule((2.05, y0 + 1.75, 0.55), (2.25, y0 + 2.55, 0.62), 0.14), GOLD, k=0.0, layer='tool', metal=g)
    fig.add(S.capsule((2.25, y0 + 2.5, 0.62), (2.4, y0 + 3.15, 0.66), 0.1), SILVER, k=0.0, layer='tool2', metal=(SILVER, (1, 1, 1)))
    fig.add(S.subtract(S.cylinder((2.47, y0 + 3.38, 0.67), 0.3, 0.08, R=S.rot(0, 90, -14)),
                       S.box((2.52, y0 + 3.6, 0.67), (0.11, 0.24, 0.3), R=S.rot(0, 0, -14))), SILVER, k=0.0, layer='tool2', metal=(SILVER, (1, 1, 1)))


FIGURES = {
    'space_rabbit': ('gold', space_rabbit),
}


def _load_figs():
    """kuca_art/figs/<id>.py 마다 TIER, build(fig, rng) 를 둔다 (캐릭터별 파일, 병렬 작업용)"""
    import importlib
    import pkgutil
    from . import figs
    for m in pkgutil.iter_modules(figs.__path__):
        mod = importlib.import_module(f'{figs.__name__}.{m.name}')
        FIGURES[m.name] = (mod.TIER, mod.build)


_load_figs()


EXTRA_CAP = 6000   # 지도용: 눈·꽃·잔디 등 또렷한 부품의 삼각형 상한


def _reduce_extra(fig, cap):
    """지도용: 또렷한 부품(fig.extra) 묶음을 cap 개 삼각형 근처로 줄인다. 색·빛남은 가장 가까운 원래 정점에서 가져온다.
    눈 자리(fig.eye_spots) 삼각형은 그대로 둔다: 흰자·홍채·동공·반짝임이 몇 % 간격으로 겹친 층이라 함께 줄이면
    층이 뭉개지고 색이 바둑판처럼 섞인다 (펫 검토 v1 P1)."""
    import fast_simplification
    from scipy.spatial import cKDTree
    P_all = np.asarray(fig.extra.pos, np.float64).reshape(-1, 3)
    C_all = np.asarray(fig.extra.col, np.float64).reshape(-1, 3)
    A_all = np.asarray(fig.extra.alpha, np.float64) if len(fig.extra.alpha) == len(P_all) else np.ones(len(P_all))
    cen0 = P_all.reshape(-1, 3, 3).mean(axis=1)
    keep = np.zeros(len(cen0), bool)
    for c, r in fig.eye_spots:
        keep |= np.linalg.norm(cen0 - c, axis=1) < r
    kv = np.repeat(keep, 3)
    P, C, A = P_all[~kv], C_all[~kv], A_all[~kv]
    budget = max(cap - int(keep.sum()), cap // 3)
    if len(P) // 3 > budget:
        key = np.round(P / 0.0005).astype(np.int64)
        uniq, inv = np.unique(key, axis=0, return_inverse=True)
        inv = inv.ravel()
        V = uniq * 0.0005
        F = inv.reshape(-1, 3)
        F = F[(F[:, 0] != F[:, 1]) & (F[:, 1] != F[:, 2]) & (F[:, 0] != F[:, 2])]
        V2, F2 = fast_simplification.simplify(V.astype(np.float32), F.astype(np.int32), max(0.0, 1.0 - budget / max(len(F), 1)))
        V2 = np.asarray(V2, np.float64)
        tri = V2[F2]
        cen = tri.mean(axis=1)
        _, near = cKDTree(P).query(cen)                       # 삼각형마다 가장 가까운 원래 정점의 색 (부품 경계가 섞이지 않게)
        # 감는 방향: 원래 가까운 면과 같은 쪽
        T0 = P.reshape(-1, 3, 3)[near // 3]
        n0 = np.cross(T0[:, 1] - T0[:, 0], T0[:, 2] - T0[:, 0])
        n1 = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        flip = np.sum(n0 * n1, axis=1) < 0
        tri[flip] = tri[flip][:, ::-1]
        P, C, A = tri.reshape(-1, 3), np.repeat(C[near], 3, axis=0), np.repeat(A[near], 3)
    fig.extra.pos = np.concatenate([P, P_all[kv]]).reshape(-1).tolist()
    fig.extra.col = np.concatenate([C, C_all[kv]]).reshape(-1).tolist()
    fig.extra.alpha = np.concatenate([A, A_all[kv]]).tolist()


def smooth_normals(P):
    """삼각형 묶음 (N*3, 3) 의 정점 법선: 같은 위치끼리 면 법선(넓이 가중) 평균"""
    T = P.reshape(-1, 3, 3)
    fn = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0])
    key = np.round(P / 0.0005).astype(np.int64)
    _, inv = np.unique(key, axis=0, return_inverse=True)
    inv = inv.ravel()
    acc = np.zeros((inv.max() + 1, 3))
    np.add.at(acc, inv, np.repeat(fn, 3, axis=0))
    n = acc[inv]
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)


def build(cid, tris=16000, seed=3, voxel=0.04, lod=1.0, extra_cap=None):
    global LOD
    LOD = lod
    tier, fn = FIGURES[cid]
    fig = Figure(tier)
    fn(fig, random.Random(seed))
    V, F, C = fig.build(tris, voxel=voxel)
    N = fig.s.normals(V)
    # 조형 표면: SDF 기울기 법선 (아주 매끈), 부품: 위치별 평균 법선
    body = V[F].reshape(-1, 3)
    extra = np.asarray(fig.extra.pos, np.float64).reshape(-1, 3)
    cap = extra_cap or EXTRA_CAP
    if lod < 1.0 and len(extra) // 3 > cap:
        _reduce_extra(fig, cap)
        extra = np.asarray(fig.extra.pos, np.float64).reshape(-1, 3)
    mb = Builder()
    mb.pos = body.ravel().tolist() + extra.ravel().tolist()
    ex_col = vivid(np.asarray(fig.extra.col, np.float64).reshape(-1, 3)).ravel().tolist() if fig.extra.col else []
    mb.col = C[F].reshape(-1, 3).ravel().tolist() + ex_col
    mb.nrm = N[F].reshape(-1, 3).ravel().tolist() + (smooth_normals(extra).ravel().tolist() if len(extra) else [])
    # 밤에 빛나는 부품 (fig.extra.emissive 로 만든 정점, 예: 고슴도치 LED): 알파 0 을 그대로 옮긴다
    ex_alpha = list(fig.extra.alpha) if len(fig.extra.alpha) == len(extra) else [1.0] * len(extra)
    sp, sn, sc = fig.smooth
    mb.pos += sp
    mb.nrm += sn
    mb.col += sc
    mb.alpha = [1.0] * (len(F) * 3) + ex_alpha + [1.0] * (len(sp) // 3)
    glass = None
    if fig.glass[0]:
        glass = Builder()
        glass.pos, glass.nrm, glass.col = list(fig.glass[0]), list(fig.glass[1]), list(fig.glass[2])
    return mb, glass
