"""캠퍼스 비둘기 (green): 졸린 반쯤 감긴 눈, 무지개빛 목 비늘 깃, 흰 배, 회색 날개 + 초록 빵부스러기 주머니"""
import math

import numpy as np

from .. import sculpt as S
from .. import figures as Fg
from ..mesh import Frame

TIER = 'green'

GREY = (0.60, 0.60, 0.645)
GREY_D = (0.40, 0.40, 0.46)
GREY_L = (0.74, 0.74, 0.79)
BELLY = (0.95, 0.94, 0.93)
IRI_G = (0.26, 0.64, 0.44)
IRI_T = (0.32, 0.62, 0.66)
IRI_P = (0.52, 0.38, 0.78)
FOOT = (0.94, 0.46, 0.44)
BEAK = (0.30, 0.30, 0.35)
BAG = (0.52, 0.68, 0.25)
BAG_D = (0.42, 0.56, 0.19)
CRUMB = (0.93, 0.72, 0.40)
CRUMB_D = (0.80, 0.55, 0.26)
LID_EDGE = (0.47, 0.47, 0.53)


# ---------- 공용 도우미 ----------

def basis(z, up=(0, 1, 0)):
    """앞(z) 방향 기준 직교 축 행렬 (열 = x, y, z)"""
    z = np.asarray(z, np.float64); z = z / np.linalg.norm(z)
    x = np.cross(np.asarray(up, np.float64), z)
    if np.linalg.norm(x) < 1e-6:
        x = np.array([1.0, 0, 0])
    x /= np.linalg.norm(x)
    y = np.cross(z, x)
    return np.stack([x, y, z], axis=1)


def axis_R(y_dir, z_hint=(0, 0, 1)):
    """로컬 Y 를 y_dir 로 두는 축 행렬 (Z 는 z_hint 쪽)"""
    y = np.asarray(y_dir, np.float64); y = y / np.linalg.norm(y)
    z = np.asarray(z_hint, np.float64); z = z - y * (z @ y); z /= np.linalg.norm(z)
    x = np.cross(y, z)
    return np.stack([x, y, z], axis=1)


def surf_point(fn, c, yaw, pitch, rmax=4.0):
    """중심 c 에서 (yaw, pitch) 방향으로 fn 표면을 찾는다 → (점, 법선)"""
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    d = np.array([cp * sy, sp, cp * cy])
    c = np.asarray(c, np.float64)
    lo, hi = 0.0, rmax
    for _ in range(40):
        m = (lo + hi) / 2
        if fn((c + d * m)[None, :])[0] < 0:
            lo = m
        else:
            hi = m
    p = c + d * lo
    e = 0.003
    g = np.array([fn((p + np.eye(3)[i] * e)[None, :])[0] - fn((p - np.eye(3)[i] * e)[None, :])[0] for i in range(3)])
    return p, g / np.linalg.norm(g)


def surf_frame(fn, c, yaw, pitch, out=0.0, up=(0, 1, 0)):
    p, n = surf_point(fn, c, yaw, pitch)
    R = basis(n, up)
    o = p + n * out
    return Frame(tuple(o), tuple(R[:, 0]), tuple(R[:, 1]), tuple(R[:, 2])), o, R


def ell(mb, f, c, r, col, seg=20, rings=10):
    Fg._ellipsoid(mb, c, r, col, seg, rings, f)


def masked(f, mask, w=0.06):
    """칠 범위를 mask(P) <= w 인 곳(어떤 표면 근처)으로 제한"""
    return lambda P: np.maximum(f(P), (mask(P) - w) * 20.0)


def pebble(fig, x, z, r, col=(0.66, 0.66, 0.68), seed=0, layer='pebble'):
    fig.add(S.ellipsoid((x, Fg.TOP + r * 0.15, z), (r, r * 0.55, r * 0.8), R=S.rot(seed * 47 % 360, 0, 0)), col, k=0.0, layer=layer)


def dirt(fig, x, z, r, col=(0.55, 0.40, 0.26)):
    def f(P):
        d = np.hypot(P[:, 0] - x, (P[:, 2] - z) * 1.3) - r + 0.06 * np.sin(P[:, 0] * 9) * np.cos(P[:, 2] * 8)
        return np.maximum(d, (P[:, 1] - (Fg.TOP + 0.08)) * 5)
    fig.paint(f, col, soft=0.12)


def sleepy_eye(fig, body_fn, hc, s, yaw, pitch, size, lid_cut=0.12, droop=12, lid_col=GREY, iris=(0.55, 0.30, 0.14),
               look=(0.0, -0.08), layer='body', lid_k=0.12, lid_z=0.36):
    """반쯤 감긴 눈: 흰자 + 갈색 홍채 + 동공 + 반짝임 (또렷한 부품) + 위를 덮는 눈꺼풀 (조형)"""
    f, o, R = surf_frame(body_fn, hc, s * yaw, pitch)
    b = fig.extra
    lx, ly = look
    ell(b, f, (0, 0, -size * 0.12), (size, size * 0.98, size * 0.34), (0.99, 0.98, 0.97), 24, 12)
    ell(b, f, (lx * size + s * 0.0, ly * size, size * 0.16), (size * 0.62, size * 0.64, size * 0.12), iris, 20, 10)
    ell(b, f, (lx * size, ly * size - size * 0.22, size * 0.2), (size * 0.42, size * 0.3, size * 0.1),
        tuple(min(1, c * 1.45) for c in iris), 16, 8)
    ell(b, f, (lx * size, ly * size + size * 0.04, size * 0.24), (size * 0.32, size * 0.34, size * 0.08), Fg.PUPIL, 16, 8)
    ell(b, f, (lx * size - size * 0.2, ly * size + size * 0.22, size * 0.3), (size * 0.15, size * 0.15, size * 0.05), Fg.SHINE, 12, 6)
    ell(b, f, (lx * size + size * 0.22, ly * size - size * 0.3, size * 0.29), (size * 0.07, size * 0.07, size * 0.03), Fg.SHINE, 8, 4)
    # 눈꺼풀: 눈 위 덮개 (바깥쪽이 처짐)
    Rl = R @ S.rot(0, 0, -s * droop)
    lid_e = S.ellipsoid(tuple(o + R[:, 2] * (-size * 0.1)), (size * 1.1, size * 1.08, size * lid_z), R=R)
    cut_y = size * lid_cut

    def lid(P, lid_e=lid_e, o=o, Rl=Rl, cut_y=cut_y):
        ly_ = (P - o) @ Rl[:, 1]
        return np.maximum(lid_e(P), cut_y - ly_)
    fig.add(lid, lid_col, k=lid_k, layer=layer)
    return f, o, R, Rl


def kawaii_eyes2(fig, fn, hc, spread=24, pitch=-6, size=0.6, iris=Fg.IRIS, tall=1.12, out=0.0, sink=0.16, hi=None):
    """figures.kawaii_eyes 와 같은 눈이지만 실제 표면(fn)에 붙인다 (타원체 머리에서도 묻히지 않게)"""
    b = fig.extra
    hi = hi or tuple(min(1, c * 1.6) for c in iris)
    for s in (-1, 1):
        f, o, R = surf_frame(fn, hc, s * spread, pitch, out - size * sink)
        ell(b, f, (0, 0, 0), (size * 0.9, size * tall, size * 0.34), Fg.PUPIL, 22, 12)
        ell(b, f, (0, -size * 0.16, size * 0.07), (size * 0.78, size * tall * 0.8, size * 0.32), iris, 22, 12)
        ell(b, f, (0, -size * 0.44, size * 0.11), (size * 0.5, size * 0.34, size * 0.28), hi, 16, 8)
        ell(b, f, (0, size * 0.05, size * 0.17), (size * 0.42, size * 0.5, size * 0.28), Fg.PUPIL, 16, 8)
        ell(b, f, (-size * 0.3 * s, size * 0.38, size * 0.31), (size * 0.3, size * 0.3, size * 0.12), Fg.SHINE, 12, 6)
        ell(b, f, (size * 0.32 * s, -size * 0.45, size * 0.31), (size * 0.12, size * 0.12, size * 0.06), Fg.SHINE, 8, 4)


def smile2(fig, fn, hc, pitch=-25, w=0.28, col=(0.30, 0.16, 0.16), open_=False, th=0.055, yaw=0.0):
    b = fig.extra
    f, o, R = surf_frame(fn, hc, yaw, pitch, -0.03)
    if open_:
        ell(b, f, (0, -w * 0.15, 0), (w * 0.9, w * 0.75, 0.1), (0.42, 0.14, 0.16), 14, 8)
        ell(b, f, (0, -w * 0.45, 0.04), (w * 0.6, w * 0.35, 0.08), (0.98, 0.50, 0.55), 12, 6)
        return
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            ell(b, f, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.02), (th, th, 0.04), col, 6, 4)


def blush2(fig, fn, hc, spread=42, pitch=-16, size=0.5, soft=0.65, col=Fg.BLUSH):
    for s in (-1, 1):
        p, _ = surf_point(fn, hc, s * spread, pitch)
        fig.paint(S.sphere(p, size), col, soft=soft)


# ---------- 2차: 두상 · 표면 위 눈/입 도우미 ----------

def soft_head(c, r, flare=0.08, taper=0.0, flare_y=-0.4, R=None, front=0.0):
    """
    한 덩어리 두상 (따로 붙인 볼 구 없음): 타원체인데 볼 높이(flare_y, 반지름 비율)가 flare 만큼 옆으로 넓고,
    위쪽은 taper 만큼 좁아진다 (달걀·물방울·넓적형). front 는 아래쪽이 앞으로 살짝 나오는 정도 (주둥이 볼륨).
    """
    c = np.asarray(c, np.float64)
    r = np.asarray(r, np.float64)
    base_e = S.ellipsoid((0, 0, 0), r)

    def f(P):
        q = P - c if R is None else (P - c) @ R
        yn = np.clip(q[:, 1] / r[1], -1.3, 1.3)
        s = 1.0 + flare * np.exp(-((yn - flare_y) / 0.55) ** 2) - taper * np.clip(yn, 0, None) ** 1.5
        sz = 1.0 + (s - 1.0) * 0.5 + front * np.exp(-((yn - flare_y) / 0.5) ** 2)
        Q = np.stack([q[:, 0] / s, q[:, 1], q[:, 2] / sz], axis=1)
        return base_e(Q) * np.minimum(np.minimum(s, sz), 1.0)
    return f


def dir_of(yaw, pitch):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    return np.array((cp * sy, sp, cp * cy))


def face_frame(fig, c, yaw, pitch, layer='body', out=0.0, up=(0, 1, 0), reach=7.0):
    """머리 중심 c 에서 (yaw, pitch) 방향의 실제 조형 표면(layer) 위 Frame — 바깥에서 광선을 쏴 표면에 붙인다"""
    d = dir_of(yaw, pitch)
    o = np.asarray(c, np.float64) + d * reach
    return Fg.surface_frame(fig, tuple(o), tuple(-d), up=up, out=out, layer=layer)


def eye_pair(fig, c, yaw, pitch, style, size, iris=Fg.IRIS, layer='body', tilt=0.0, lid=None, sink=0.14, look=0.0):
    """양쪽 눈 (figures.eye_at) 을 실제 머리 표면에 붙인다. 반환: [(side, Frame)]"""
    out = []
    for s in (-1, 1):
        f = face_frame(fig, c, s * yaw + look, pitch, layer, out=-size * sink)
        Fg.eye_at(fig, f, style, size, iris, side=s, tilt=tilt, lid=lid)
        out.append((s, f))
    return out


def mouth_w(fig, f, w=0.26, col=(0.30, 0.16, 0.16), th=0.05):
    """'w' 입 — f 는 표면 Frame (face_frame)"""
    b = fig.extra
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            ell(b, f, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.0), (th, th, th * 0.7), col, 6, 4)


def blush_paint(fig, c, yaw, pitch, layer='body', size=0.45, soft=0.6, col=Fg.BLUSH):
    """볼터치: 표면에 칠만 (모양 변화 없음)"""
    for s in (-1, 1):
        f = face_frame(fig, c, s * yaw, pitch, layer)
        fig.paint(S.sphere(f.o, size), col, soft=soft)


def tuft(fig, x, z, rng, col=(0.30, 0.60, 0.16), n=5, h=0.7, layer='tuft'):
    """받침 위 뾰족 잎 덤불"""
    for k in range(n):
        a = k * 2 * math.pi / n + rng.uniform(-0.3, 0.3)
        lean = rng.uniform(20, 40)
        d = np.array((math.sin(a), 0, math.cos(a)))
        base = np.array((x, Fg.TOP, z)) + d * 0.08
        tip = base + d * h * math.sin(math.radians(lean)) + np.array((0, h * math.cos(math.radians(lean)), 0)) * rng.uniform(0.8, 1.2)
        Rl = axis_R(tip - base, d)
        L = np.linalg.norm(tip - base)
        fig.add(S.ellipsoid(tuple((base + tip) / 2), (0.13, L * 0.55, 0.035), R=Rl), col, k=0.03, layer=layer)


# ---------- 비둘기 ----------

def feather_scales(P, y_top, rows, n_around=20, H=0.24, hh=0.21, w=0.36, A=0.085, Rr=1.95, cz=0.0):
    """가리비 깃 비늘: 줄마다 엇갈린 둥근 돔, 위 줄이 아래 줄을 덮음 → (높이, 줄 번호)"""
    th = np.arctan2(P[:, 0], P[:, 2] - cz)
    y = P[:, 1]
    best = np.zeros(len(P))
    row = np.full(len(P), -1)
    step = 2 * math.pi / n_around * Rr
    for k in range(rows):
        tc = y_top - k * H - hh
        uk = th * n_around / (2 * math.pi) + 0.5 * k
        du = uk - np.round(uk)
        ty = (y - tc) / hh
        prof = 1 - 0.5 * np.clip((ty + 1) / 2, 0, 1)
        for o in (-1, 0, 1):
            s = (du + o) * step / w
            inside = np.clip(1 - s * s - ty * ty, 0, 1) ** 0.6
            val = A * inside * prof * (1.0 + 0.02 * k)
            upd = val > best
            row = np.where(upd, k, row)
            best = np.maximum(best, val)
    return best, row


def pigeon_eye(fig, f, size, side, iris=(0.42, 0.22, 0.09), lid_col=GREY, lid_drop=0.75):
    """멍한 비둘기 눈: 흰자 + eye_at('droopy') 홍채 + 흰자 위를 반쯤 덮는 회색 눈꺼풀 (바깥쪽이 처짐)"""
    b = fig.extra
    ell(b, f, (0, 0, 0), (size * 1.18, size * 1.08, size * 0.28), (0.99, 0.985, 0.975), 26, 12)
    # 홍채: 아래로 살짝 (멍하게 앞을 봄)
    fi = Frame(f.p((side * size * 0.04, -size * 0.12, size * 0.05)), f.x, f.y, f.z)
    Fg.eye_at(fig, fi, 'droopy', size * 0.8, iris, side=side, lid=lid_col)
    # 눈꺼풀: 흰자 위쪽을 덮는 두꺼운 덮개, 아래 경계는 바깥쪽이 처진 완만한 곡선
    ang = math.radians(-side * 9)
    fl = Frame(f.o, tuple(np.array(f.x) * math.cos(ang) + np.array(f.y) * math.sin(ang)),
               tuple(-np.array(f.x) * math.sin(ang) + np.array(f.y) * math.cos(ang)), f.z)
    ly = size * (1.0 - lid_drop)            # 눈꺼풀 아래 경계 높이
    hh = (size * 1.06 - ly) / 2 + size * 0.02
    ell(b, fl, (0, ly + hh, size * 0.04), (size * 1.17, hh, size * 0.44), lid_col, 26, 12)
    ell(b, fl, (0, ly + hh - size * 0.05, size * 0.03), (size * 1.18, hh, size * 0.435), LID_EDGE, 26, 12)


def build(fig, rng):
    Fg.base(fig, rng, flowers=7)
    y0 = Fg.TOP
    # ---- 체형: 몸에 비해 작은 달걀형 머리 + 가슴이 앞으로 나온 통통한 몸 (약 2.3 등신) ----
    # 키아트: 머리가 크고 목 잘록함 없이 몸과 한 덩어리로 이어진다
    hc = np.array((0.0, y0 + 5.95, 0.25))
    head = soft_head(hc, (1.9, 1.82, 1.76), flare=0.07, taper=0.06, flare_y=-0.5)
    belly = S.ellipsoid((0, y0 + 2.75, -0.1), (2.15, 2.15, 1.95))
    chest = S.ellipsoid((0, y0 + 3.6, 0.95), (1.7, 1.65, 1.6))
    def core(P):
        return S.smin(S.smin(belly(P), chest(P), 0.6), head(P), 1.1)
    # ---- 목 무지개 깃 띠: 매끈한 띠 + 크고 은은한 비늘 3 줄, 초록(위) / 보라(아래) 색 구역을 또렷하게 ----
    Y_TOP, ROWS = y0 + 5.0, 4
    sc = lambda P: feather_scales(P, Y_TOP, ROWS, n_around=13, H=0.32, hh=0.3, w=0.62, A=0.05, Rr=2.0, cz=0.2)
    fig.add(lambda P: core(P) - sc(P)[0], GREY, k=0.3)
    near_body = lambda P: np.abs(core(P))
    th_of = lambda P: np.arctan2(P[:, 0], P[:, 2] - 0.2)
    y_mid = y0 + 4.45
    y_bot = y0 + 3.78

    def scallop(P, y_edge, n=9, amp=0.2):      # 아래 가장자리를 비늘 모양으로 물결
        u = th_of(P) * n / (2 * math.pi)
        du = u - np.round(u)
        return y_edge - amp * (1 - (2 * du) ** 2)
    belly_f = lambda P: (P[:, 1] - scallop(P, y_bot)) * 4.0      # 띠 아래: 흰 배
    fig.paint(masked(lambda P: belly_f(P), near_body, 0.2), BELLY, soft=0.2)
    fig.paint(masked(lambda P: (P[:, 1] - (y0 + 6.9)) * -1.5, near_body, 0.2), (0.66, 0.66, 0.71), soft=1.4)   # 정수리 살짝 밝게
    band = lambda P: np.maximum((P[:, 1] - Y_TOP) * 4.0, -belly_f(P))      # 띠 안쪽 < 0
    green = lambda P: np.maximum(band(P), (scallop(P, y_mid, amp=0.14) - P[:, 1]) * 4.0)
    purple = lambda P: np.maximum(band(P), (P[:, 1] - scallop(P, y_mid, amp=0.14)) * 4.0)
    fig.paint(masked(green, near_body, 0.2), IRI_G, soft=0.2)
    fig.paint(masked(purple, near_body, 0.2), IRI_P, soft=0.2)
    # 띠 위 가장자리는 회색으로 부드럽게 녹아듦 + 초록 위쪽에 밝은 청록 반사
    fig.paint(masked(lambda P: (P[:, 1] - Y_TOP) * -4.0 + 0.4, near_body, 0.2), GREY, soft=0.6)
    # 비늘 겹친 곳만 아주 살짝 짙게 (큰 비늘 모양이 은은하게 보이도록)
    def crease(zone):
        def f(P):
            b, row = sc(P)
            return np.where((row >= 0) & (b < 0.02) & (zone(P) < 0), -1.0, 1.0)
        return masked(f, near_body, 0.2)
    fig.paint(crease(green), (0.14, 0.44, 0.32), soft=0.6)
    fig.paint(crease(purple), (0.36, 0.24, 0.58), soft=0.6)

    # ---- 얼굴: 멍한 반쯤 감긴 눈 (흰자 + 처진 눈꺼풀), 짧은 부리 + 흰 납막, 칠한 볼터치 ----
    for s in (-1, 1):
        f = face_frame(fig, hc, s * 28, 4, 'body', out=0.0)
        pigeon_eye(fig, f, 0.64, s, iris=(0.50, 0.30, 0.13))
    for s in (-1, 1):   # 이마 짧은 주름 선
        p0 = face_frame(fig, hc, s * 20, 36, 'body').o
        p1 = face_frame(fig, hc, s * 32, 34, 'body').o
        fig.paint(masked(S.capsule(p0, p1, 0.04), near_body, 0.1), GREY_D, soft=0.05)
    fb = face_frame(fig, hc, 0, -12, 'body')
    ob, n = np.array(fb.o), np.array(fb.z)
    m1 = ob + n * 0.2 + np.array((0, -0.03, 0))
    tip = ob + n * 0.42 + np.array((0, -0.2, 0))
    fig.add(S.capsule(tuple(ob - n * 0.1), tuple(m1), 0.23, 0.16), BEAK, k=0.12, layer='beak')
    fig.add(S.capsule(tuple(m1), tuple(tip), 0.15, 0.05), BEAK, k=0.12, layer='beak')
    fig.paint(S.box(tuple(ob + n * 0.3 + np.array((0, -0.08, 0))), (0.3, 0.012, 0.4)), (0.16, 0.16, 0.2), soft=0.03)
    fcere = face_frame(fig, hc, 0, -5, 'body')
    oc = np.array(fcere.o)
    for x, yy, r in ((-0.11, 0.0, 0.12), (0.11, 0.0, 0.12), (0.0, 0.065, 0.11), (0.0, -0.07, 0.11)):
        fig.add(S.sphere(tuple(oc + np.array(fcere.x) * x + np.array(fcere.y) * yy + np.array(fcere.z) * 0.08), r), (0.97, 0.96, 0.95), k=0.1, layer='cere')
    blush_paint(fig, hc, 46, -17, 'body', size=0.34, soft=0.5)

    # ---- 날개: 몸 옆에 납작하게 접혀 붙는다 (팔처럼 뜨지 않게), 짙은 깃 끝 3개 (가는 날개 띠 칠은 메시 해상도에서 지글거려 뺐다) ----
    WING = (0.64, 0.64, 0.665)

    def wing(s, top, bot, fingers, out_dir):
        top, bot = np.asarray(top, float), np.asarray(bot, float)
        dirv = bot - top
        L = np.linalg.norm(dirv)
        Rw = axis_R(dirv, out_dir)
        c = top + dirv * 0.5
        we = S.ellipsoid(tuple(c), (0.9, L * 0.6, 0.32), R=Rw)
        sh = S.ellipsoid(tuple(top + dirv * 0.12), (0.72, 0.62, 0.4), R=Rw)
        fig.add(we, WING, k=0.25, layer=f'wing{s}')
        fig.add(sh, WING, k=0.4, layer=f'wing{s}')
        tips = []
        for fp in fingers:
            fp = np.asarray(fp, float)
            a = top + dirv * 0.7
            Rf = axis_R(fp - a, out_dir)
            tips.append(S.ellipsoid(tuple((a + fp) / 2), (0.24, np.linalg.norm(fp - a) * 0.58, 0.13), R=Rf))
            fig.add(tips[-1], GREY_D, k=0.18, layer=f'wing{s}')
        # 배 흰 칠이 날개 안쪽까지 번지지 않게 날개 색을 다시 칠한다
        fig.paint(lambda P: np.minimum(we(P), sh(P)) - 0.04, WING, soft=0.1)
        for tp in tips:
            fig.paint(lambda P, tp=tp: tp(P) - 0.04, GREY_D, soft=0.1)
    wing(-1, (-2.0, y0 + 4.35, -0.15), (-2.25, y0 + 2.35, -0.3),
         [(-2.35, y0 + 1.8, -1.0), (-2.42, y0 + 1.7, -0.6), (-2.4, y0 + 1.75, -0.2)], (-1, 0.0, 0.1))
    bx, by, bz = 2.42, y0 + 2.2, 1.22
    wing(1, (2.0, y0 + 4.35, -0.05), (2.55, y0 + 2.95, 0.65),
         [(2.45, y0 + 3.0, 1.6), (2.8, y0 + 2.9, 1.6), (3.0, y0 + 2.8, 1.3)], (1, 0.0, 0.0))

    # ---- 주머니 (초록 천, 접힌 두꺼운 테, 빵부스러기, 진홍 보석 단추) ----
    bc = np.array((bx, by, bz))
    outer = S.ellipsoid(tuple(bc), (0.82, 0.9, 0.72))
    inner = S.ellipsoid(tuple(bc + (0, 0.2, 0)), (0.66, 0.85, 0.56))
    top = by + 0.55
    pouch = S.subtract(S.intersect(outer, lambda P: P[:, 1] - top), inner, k=0.05)
    fig.add(pouch, BAG, k=0.0, layer='bag')
    fig.add(S.torus((bx, top + 0.05, bz), 0.66, 0.2, Rm=S.rot(0, 0, -6)), BAG, k=0.1, layer='bag')
    fig.paint(masked(lambda P: P[:, 1] - (by - 0.5), lambda P: np.abs(outer(P)), 0.05), BAG_D, soft=0.5)
    for k in range(10):
        a = k * 2.4
        rr = 0.1 + 0.32 * ((k * 0.37) % 1.0)
        c = (bx + math.cos(a) * rr, top + 0.12 + 0.07 * (k % 3), bz + math.sin(a) * rr * 0.85)
        fig.add(S.box(c, (0.14, 0.12, 0.13), round_=0.05, R=S.rot(k * 37, k * 23, k * 11)), CRUMB, k=0.02, layer='crumb')
    fig.add(S.ellipsoid((bx, top - 0.02, bz), (0.6, 0.14, 0.5)), CRUMB_D, k=0.08, layer='crumb')
    gd = np.array((0.75, 0.05, 0.55)); gd /= np.linalg.norm(gd)
    gp = bc + np.array((0.78 * gd[0] * 0.95, 0.15, 0.72 * gd[2] * 0.95))
    Rg = basis(gd)
    fig.add(S.torus(tuple(gp), 0.14, 0.05, Rm=axis_R(gd)), Fg.GOLD, k=0.0, layer='gem', metal=(Fg.GOLD, Fg.GOLD_HI))
    fig.add(S.sphere(tuple(gp + gd * 0.02), 0.12), Fg.CRIMSON, k=0.0, layer='gem2')
    fig.add(S.sphere(tuple(gp + gd * 0.1 + Rg[:, 1] * 0.04 - Rg[:, 0] * 0.03), 0.03), (1.0, 0.75, 0.78), k=0.0, layer='gem3')

    # ---- 꼬리: 등 아래에서 뒤로 벌어진 깃 부채 (깃 5장, 이웃 깃은 다른 층 + 번갈아 밝기 → 뒤에서 한 덩어리로 안 뭉침, 끝이 더 짙음) ----
    TAIL = (0.37, 0.37, 0.42)
    TAIL_L = (0.47, 0.47, 0.52)
    for i, ax in enumerate((-1.0, -0.5, 0.0, 0.5, 1.0)):
        a = np.array((ax * 0.3, y0 + 2.05, -1.45))
        b = np.array((ax * 1.2, y0 + 1.3 + 0.12 * abs(ax), -2.8 + 0.3 * abs(ax)))
        Rt = axis_R(b - a, (0, 1, -0.3))
        col = TAIL if i % 2 == 0 else TAIL_L
        te = S.ellipsoid(tuple((a + b) / 2), (0.34, np.linalg.norm(b - a) * 0.56, 0.15), R=Rt)
        fig.add(te, col, k=0.12, layer=f'tail{i % 2}')
        fig.paint(lambda P, te=te: te(P) - 0.03, col, soft=0.1)
        fig.paint(masked(lambda P, b=b: np.linalg.norm(P - b, axis=1) - 0.42, lambda P, te=te: np.abs(te(P)), 0.12), (0.25, 0.25, 0.29), soft=0.3)

    # ---- 발 (짧은 분홍 다리 + 앞발가락 3 + 뒷발가락) ----
    for s in (-1, 1):
        x = s * 0.7
        fig.add(S.capsule((x, y0 + 1.0, 0.3), (x * 1.02, y0 + 0.25, 0.4), 0.17, 0.14), FOOT, k=0.1, layer='feet')
        for a in (-30, 0, 30):
            d = np.array((math.sin(math.radians(a + s * 8)), 0, math.cos(math.radians(a + s * 8))))
            p0 = np.array((x * 1.02, y0 + 0.16, 0.4))
            fig.add(S.capsule(tuple(p0), tuple(p0 + d * 0.6 + (0, -0.03, 0)), 0.12, 0.09), FOOT, k=0.1, layer='feet')
        fig.add(S.capsule((x * 1.02, y0 + 0.16, 0.4), (x * 1.02, y0 + 0.12, 0.0), 0.11, 0.08), FOOT, k=0.1, layer='feet')

    # ---- 받침: 조약돌, 흙, 빵 부스러기 ----
    for i, (x, z, r) in enumerate(((-3.3, 0.9, 0.26), (-1.4, 2.9, 0.22), (2.9, -1.5, 0.26), (-2.6, -2.3, 0.22), (3.5, 1.2, 0.2), (0.6, 3.6, 0.18))):
        pebble(fig, x, z, r, seed=i)
    dirt(fig, -1.9, 1.7, 0.5)
    dirt(fig, 1.6, -2.6, 0.45)
    fig.add(S.box((-2.7, y0 + 0.2, 2.3), (0.24, 0.2, 0.22), round_=0.08, R=S.rot(30, 8, 5)), CRUMB, k=0.0, layer='crumb2')
    fig.add(S.box((-2.15, y0 + 0.08, 2.75), (0.1, 0.08, 0.1), round_=0.04, R=S.rot(60, 0, 15)), CRUMB, k=0.0, layer='crumb2')
