"""한국 다람쥐 (blue): 복슬한 갈색 털 + 크림 배·주둥이, 짙은 귀 끝 털, 큰 S자 꼬리, 파란 털모자, 파란 도토리 가방(진홍 배지) 끈"""
import math

import numpy as np

from .. import sculpt as S
from .. import figures as Fg
from .pigeon import (axis_R, basis, surf_point, surf_frame, masked, pebble, ell, kawaii_eyes2, smile2, blush2,
                     soft_head, face_frame, eye_pair, mouth_w, blush_paint)

TIER = 'blue'

FUR = (0.53, 0.40, 0.31)
FUR_D = (0.36, 0.24, 0.16)
FUR_L = (0.76, 0.62, 0.50)
CREAM = (0.98, 0.91, 0.79)
EAR_IN = (0.97, 0.74, 0.62)
KNIT = (0.30, 0.60, 0.96)
KNIT_HI = (0.62, 0.82, 1.0)
ACORN = (0.72, 0.42, 0.20)
ACORN_CAP = (0.55, 0.36, 0.20)


def fuzz(P, amp=0.05, f=7.0):
    x, y, z = P[:, 0], P[:, 1], P[:, 2]
    return amp * (np.sin(x * f + y * 0.7 * f) * np.sin(z * f - y * 0.5 * f) + 0.5 * np.sin((x + z) * f * 1.9 + y * 2.3))


def build(fig, rng):
    Fg.base(fig, rng, flowers=8)
    y0 = Fg.TOP
    # ---- 머리 ----
    # 작은 머리 (아래 볼 쪽이 살짝 넓은 한 덩어리, 따로 붙인 볼 없음) — 약 2.35 등신
    hc = np.array((0.0, y0 + 5.55, 0.15))
    head0 = soft_head(hc, (1.66, 1.48, 1.52), flare=0.12, taper=0.04, flare_y=-0.45)
    head = head0
    # ---- 몸통 / 다리 / 팔: 작고 가는 몸, 통통한 허벅지 ----
    torso = S.ellipsoid((0, y0 + 2.5, -0.05), (1.02, 1.32, 0.92))
    belly = S.ellipsoid((0, y0 + 2.3, 0.2), (0.9, 1.05, 0.8))
    parts = []
    for s in (-1, 1):
        parts.append(S.ellipsoid((s * 0.62, y0 + 1.1, 0.0), (0.6, 0.72, 0.8)))                       # 허벅지
        parts.append(S.ellipsoid((s * 0.68, y0 + 0.26, 0.5), (0.36, 0.26, 0.68), R=S.rot(s * 10, 0, 0)))   # 발
    hands = {}
    for s in (-1, 1):
        sh = np.array((s * 0.85, y0 + 3.3, 0.1))
        el = np.array((s * 1.4, y0 + 2.85, 0.3))
        hd = np.array((s * 1.85, y0 + 2.5, 0.5))
        parts += [S.capsule(sh, el, 0.32, 0.26), S.capsule(el, hd, 0.26, 0.22), S.sphere(hd, 0.25)]
        hands[s] = hd

    def core(P):
        d = S.smin(torso(P), belly(P), 0.4)
        for g in parts:
            d = S.smin(d, g(P), 0.3)
        return S.smin(d, head(P), 0.35)
    # ---- 귀 + 귀 끝 털 ----
    ears = []
    for s in (-1, 1):
        b = np.array((s * 1.0, y0 + 6.6, -0.1))
        t = np.array((s * 1.45, y0 + 8.05, -0.15))
        Re = axis_R(t - b, (s * 0.1, 0, 1))
        cone = S.capsule(tuple(b), tuple(t), 0.62, 0.2)
        slab = S.box(tuple((b + t) / 2), (2, 2, 0.3), R=Re)
        ear = S.intersect(cone, slab)
        inner = S.capsule(tuple(b + (0, 0.25, 0.45)), tuple(t + (-s * 0.05, -0.3, 0.45)), 0.45, 0.06)
        ear = S.subtract(ear, S.intersect(inner, lambda P, b=b, Re=Re: -((P - b) @ Re[:, 2]) + 0.05), k=0.06)
        ears.append((s, ear, b, t, Re))

    def body_all(P):
        d = core(P)
        for s, e, b, t, Re in ears:
            d = S.smin(d, e(P), 0.25)
        return d
    fig.add(body_all, FUR, k=0.2)
    nb = lambda P: np.abs(core(P))
    # 귀 끝 털 (짙은 갈색, 뾰족하게 여러 가닥)
    for s, e, b, t, Re in ears:
        tb = t - (t - b) / np.linalg.norm(t - b) * 0.25
        up = np.array((s * 0.22, 1.0, -0.05)); up /= np.linalg.norm(up)
        # 부드러운 붓 모양 귀 끝 털: 통통한 뿌리에서 뾰족해지는 털 뭉치 3 개가 살짝 벌어짐
        fig.add(S.ellipsoid(tuple(tb + up * 0.2), (0.32, 0.34, 0.24), R=axis_R(up, (0, 0, 1))), FUR_D, k=0.3, layer='tuft')   # 통통한 뿌리
        for k, (dx, dz, L, r) in enumerate(((0.0, 0.0, 1.0, 0.24), (-0.3, 0.06, 0.78, 0.2), (0.3, -0.05, 0.82, 0.2))):
            dvec = up + np.array((s * dx, 0, dz)); dvec /= np.linalg.norm(dvec)
            fig.add(S.capsule(tuple(tb + up * 0.15), tuple(tb + dvec * L), r, 0.05), FUR_D, k=0.3, layer='tuft')
        fig.paint(masked(lambda P, b=b, t=t: S.capsule(tuple(b + (0, 0.35, 0)), tuple(t + (0, -0.3, 0)), 0.38, 0.08)(P) + ((P - b) @ Re[:, 2] < 0.0) * 1.0,
                         lambda P: np.abs(body_all(P)), 0.12), EAR_IN, soft=0.12)
        fig.paint(masked(lambda P, t=t: np.linalg.norm(P - t, axis=1) - 0.45, lambda P: np.abs(body_all(P)), 0.12), FUR_D, soft=0.3)

    # 볼 옆 복슬 털 뭉치 (바깥 아래로 삐죽)
    for s in (-1, 1):
        for k, (yw, pt, L) in enumerate(((74, -24, 0.34), (84, -10, 0.28), (64, -36, 0.3))):
            p, n = surf_point(head, hc, s * yw, pt)
            dvec = n + np.array((s * 0.2, -0.7, 0.0)); dvec /= np.linalg.norm(dvec)
            fig.add(S.capsule(tuple(p - n * 0.2), tuple(p + dvec * L * 0.8), 0.27, 0.1), FUR, k=0.25, layer='body')   # 뿔처럼 뾰족하지 않게 둥근 볼털
    for k, (yw, pt) in enumerate(((-12, 82), (10, 84))):   # 정수리 털
        p, n = surf_point(head0, hc, yw, pt)
        fig.add(S.capsule(tuple(p - n * 0.1), tuple(p + n * 0.35 + np.array((yw * 0.01, 0, -0.1))), 0.15, 0.03), FUR, k=0.12, layer='body')
    # ---- 크림: 주둥이·볼 아래, 가슴·배, 눈 둘레 ----
    def muzzle(P):
        q = P - (hc + (0, -0.72, 1.1))
        return np.hypot(q[:, 0] / 1.35, q[:, 1] / 0.78) - 1.0 + np.clip(-q[:, 2], 0, None) * 0.6
    fig.paint(masked(muzzle, nb, 0.12), CREAM, soft=0.3)
    def belly_p(P):
        q = P - np.array((0, y0 + 2.3, 0.9))
        return np.hypot(q[:, 0] / 0.72, q[:, 1] / 1.3) - 1.0 + np.clip(0.35 - P[:, 2], 0, None) * 2
    fig.paint(masked(belly_p, nb, 0.12), CREAM, soft=0.3)
    # 등·정수리 조금 짙게
    fig.paint(masked(lambda P: (P[:, 2] + 0.6) * 1.5, nb, 0.12), (0.50, 0.34, 0.23), soft=1.2)

    # ---- 얼굴 ----
    # 경계하는 초롱초롱한 눈: 동그랗고 반짝임 큰 round, 조금 위·바깥을 봄
    eye_pair(fig, hc, 28, 0, 'round', 0.5, iris=(0.30, 0.16, 0.07), layer='body', sink=0.14)
    for s in (-1, 1):   # 살짝 걱정스러운 눈썹 (안쪽이 올라감)
        p0 = face_frame(fig, hc, s * 13, 25).o
        p1 = face_frame(fig, hc, s * 33, 19).o
        fig.paint(masked(S.capsule(p0, p1, 0.1, 0.06), nb, 0.12), (0.26, 0.15, 0.09), soft=0.03)
    fn_ = face_frame(fig, hc, 0, -14, 'body', out=-0.02)
    ell(fig.extra, fn_, (0, 0, 0), (0.13, 0.09, 0.07), (0.96, 0.58, 0.58), 12, 6)
    mouth_w(fig, face_frame(fig, hc, 0, -22, 'body'), w=0.22, th=0.045)
    blush_paint(fig, hc, 44, -20, 'body', size=0.36, soft=0.5)

    # ---- 꼬리: 큰 S 자 곡선 (엉덩이 → 뒤로 솟아 → 끝이 바깥으로 말려 내려옴), 겹친 털 뭉치들로 ----
    # 키아트 옆모습: 엉덩이에서 가늘게 나와 위로 갈수록 넓어지고, 꼭대기가 뒤로 말려 내려오는 '?' 모양 (받침 범위 z>-4.8 안)
    # 펫 검토 v1: 옆에서 큰 둥근 덩어리 · 뒤에서 머리까지 덮는 긴 타원 → 기둥을 가늘고 낮게(머리 꼭대기 아래), 한쪽(+x)으로 살짝 비키고,
    # 끝은 가늘게 뒤로 넘어가 아래로 말려 기둥과 틈이 보이는 고리 ('?' 모양)
    ctrl = np.array([(0.0, y0 + 1.0, -0.8), (0.05, y0 + 1.35, -1.65), (0.15, y0 + 2.3, -2.25), (0.3, y0 + 3.4, -2.45),
                     (0.42, y0 + 4.5, -2.35), (0.5, y0 + 5.5, -2.4), (0.55, y0 + 6.2, -2.85), (0.6, y0 + 6.3, -3.45),
                     (0.62, y0 + 5.85, -4.0), (0.6, y0 + 5.2, -4.1), (0.55, y0 + 4.7, -3.85)])   # 바깥 끝이 조형 격자(z > -4.8) 안에 들게
    rads = np.array([0.45, 0.78, 1.1, 1.2, 1.1, 0.95, 0.82, 0.62, 0.48, 0.38, 0.3])

    def crom(t):   # Catmull-Rom 위 점 (t: 0..1)
        n = len(ctrl) - 1
        u = np.clip(t, 0, 1) * n
        i = min(int(u), n - 1)
        f = u - i
        p0, p1, p2, p3 = ctrl[max(i - 1, 0)], ctrl[i], ctrl[i + 1], ctrl[min(i + 2, n)]
        return 0.5 * ((2 * p1) + (-p0 + p2) * f + (2 * p0 - 5 * p1 + 4 * p2 - p3) * f * f + (-p0 + 3 * p1 - 3 * p2 + p3) * f ** 3), \
            np.interp(u, np.arange(n + 1), rads)
    T = np.linspace(0, 1, 40)
    spine = np.array([crom(t)[0] for t in T])
    srad = np.array([crom(t)[1] for t in T])
    tang = np.gradient(spine, axis=0)
    tang /= np.linalg.norm(tang, axis=1, keepdims=True)
    outn = np.stack([np.zeros(len(T)), -tang[:, 2], tang[:, 1]], axis=1)       # yz 평면 법선
    outn *= np.where(outn[:, 2:3] < 0, 1, -1)                                  # 몸 반대쪽(뒤)을 향하게
    # 키아트: 큰 S 자 꼬리는 하나의 매끈한 털 뭉치 (따로 붙인 털 뭉치 캡슐은 사마귀처럼 돋아 보여 뺐다)
    parts_t = []
    for t, a, b, ra, rb in zip(T[:-1:2], spine[:-1:2], spine[2::2], srad[:-1:2], srad[2::2]):
        parts_t.append((S.capsule(tuple(a), tuple(b), ra * 1.05, rb * 1.05), 0.45 if t < 0.7 else 0.2))   # 고리는 덜 녹여 틈을 남김
    lo_b = spine.min(axis=0) - 3.2
    hi_b = spine.max(axis=0) + 3.2

    def tail(P):
        out = np.full(len(P), 5.0)
        m = np.all((P > lo_b) & (P < hi_b), axis=1)
        if m.any():
            Q = P[m]
            d = parts_t[0][0](Q)
            for g, kk in parts_t[1:]:
                d = S.smin(d, g(Q), kk)
            out[m] = d + fuzz(Q, 0.02, 4.5)     # 아주 은은한 털결
        return out
    fig.add(tail, FUR, k=0.0, layer='tail')

    def tail_tip(P):     # 뭉치 끝 쪽(심에서 먼 곳)을 밝게 → 털끝
        m = np.all((P > lo_b) & (P < hi_b), axis=1)
        out = np.full(len(P), 1.0)
        if m.any():
            Q = P[m]
            D = np.linalg.norm(Q[:, None, :] - spine[None, :, :], axis=2)
            k = D.argmin(axis=1)
            out[m] = (srad[k] * 1.2 - D[np.arange(len(Q)), k]) * 2.0
        return out
    fig.paint(masked(tail_tip, lambda P: np.abs(tail(P)), 0.1), (0.64, 0.50, 0.40), soft=0.8)

    # ---- 파란 털모자 (머리 위, 골지 테 + 방울) ----
    top, tn = surf_point(head0, hc, 0, 75)
    Rh = axis_R(tn, (0, 0, 1))
    hcen = top + tn * 0.05
    dome = S.ellipsoid(tuple(hcen + tn * 0.15), (0.86, 0.74, 0.78), R=Rh)
    def hat(P):
        q = (P - hcen) @ Rh
        rib = 0.035 * np.cos(np.arctan2(q[:, 0], q[:, 2]) * 20)
        d = np.maximum(dome(P), -q[:, 1] - 0.05)
        brim = S.torus(tuple(hcen + tn * 0.1), 0.78, 0.21, Rm=Rh)(P) - rib
        knit = 0.025 * np.cos(np.arctan2(q[:, 0], q[:, 2]) * 16)
        return S.smin(d - knit, brim, 0.08)
    fig.add(hat, KNIT, k=0.0, layer='hat')
    pom = hcen + tn * 1.05
    fig.add(lambda P: S.sphere(tuple(pom), 0.36)(P) + fuzz(P, 0.012, 14.0), KNIT, k=0.08, layer='hat')
    fig.paint(masked(lambda P: ((P - hcen) @ Rh[:, 1]) - 0.3, lambda P: np.abs(hat(P)), 0.05), (0.24, 0.52, 0.88), soft=0.2)

    # ---- 도토리 가방 (-x 허리) + 대각선 끈 (+x 어깨 → -x 허리) ----
    shell_core = lambda P: S.smin(torso(P), belly(P), 0.4)
    pa = np.array((0.8, y0 + 3.75, 0.0)); pb = np.array((-1.05, y0 + 2.0, 0.0))
    dd = pb - pa; dd /= np.linalg.norm(dd)
    pn = np.cross(dd, (0, 0, 1)); pn /= np.linalg.norm(pn)
    # 대각선 끈: 몸통을 비스듬히 감는 납작한 띠 (평면으로 자른 껍질 → 또렷한 가장자리)
    def sash(P):
        shell_d = np.abs(shell_core(P) - 0.06) - 0.065
        plane = np.abs((P - pa) @ pn) - 0.12
        return np.maximum(shell_d, plane)
    fig.add(sash, KNIT, k=0.0, layer='strap', metal=(KNIT, KNIT_HI))
    bcn = np.array((-1.25, y0 + 1.8, 0.9))
    bag_body = S.ellipsoid(tuple(bcn + (0, -0.12, 0)), (0.62, 0.62, 0.5))
    fig.add(bag_body, KNIT, k=0.0, layer='bag', metal=(KNIT, KNIT_HI))
    cap = S.ellipsoid(tuple(bcn + (0, 0.25, 0)), (0.8, 0.45, 0.66))
    def cap_f(P):
        return np.maximum(cap(P), (bcn[1] + 0.05) - P[:, 1])
    fig.add(cap_f, (0.26, 0.54, 0.92), k=0.0, layer='bag2', metal=((0.26, 0.54, 0.92), KNIT_HI))
    def hatch_f(P):
        q = P - bcn
        a = np.abs(np.sin((q[:, 0] + q[:, 1]) * 9))
        b2 = np.abs(np.sin((q[:, 0] - q[:, 1]) * 9 + q[:, 2] * 4))
        return np.maximum(0.25 - np.minimum(a, b2), (np.abs(cap(P)) - 0.05) * 10)
    fig.paint(hatch_f, (0.18, 0.42, 0.80), soft=0.2)
    fig.add(S.capsule(tuple(bcn + (0, 0.6, 0)), tuple(bcn + (0.05, 0.85, 0.02)), 0.08, 0.06), KNIT, k=0.0, layer='bag3')
    # 고리: 끈 끝과 가방 연결
    fig.add(S.torus(tuple(bcn + (0.45, 0.55, -0.15)), 0.14, 0.05, Rm=S.rot(0, 0, 50)), KNIT, k=0.0, layer='bag3', metal=(KNIT, KNIT_HI))
    # 진홍 배지 (흰 도토리 문양)
    bp, bd = surf_point(bag_body, bcn + (0, -0.12, 0), 28, -4)
    bp = bp + bd * 0.03
    fig.add(S.cylinder(tuple(bp), 0.24, 0.05, round_=0.03, R=axis_R(bd)), Fg.CRIMSON, k=0.0, layer='badge')
    fig.add(S.torus(tuple(bp), 0.24, 0.035, Rm=axis_R(bd)), (0.95, 0.95, 0.97), k=0.0, layer='badge2', metal=((0.85, 0.85, 0.9), (1, 1, 1)))
    fig.paint(S.sphere(bp + bd * 0.06 - (0, 0.03, 0), 0.07), (1, 1, 1), soft=0.02)
    fig.paint(S.ellipsoid(bp + bd * 0.06 + (0, 0.05, 0), (0.1, 0.04, 0.1)), (1, 1, 1), soft=0.02)

    # ---- 받침: 도토리, 조약돌 ----
    ac = np.array((2.7, y0 + 0.42, 1.9))
    Ra = S.rot(30, 0, -60)
    fig.add(S.ellipsoid(tuple(ac), (0.38, 0.45, 0.38), R=Ra), ACORN, k=0.0, layer='acorn', metal=(ACORN, (0.95, 0.68, 0.42)))
    capc = ac + Ra[:, 1] * 0.3
    fig.add(lambda P: S.ellipsoid(tuple(capc), (0.42, 0.22, 0.42), R=Ra)(P) - 0.02 * np.cos(P[:, 0] * 30) * np.cos(P[:, 1] * 30), ACORN_CAP, k=0.0, layer='acorn2')
    fig.add(S.capsule(tuple(capc + Ra[:, 1] * 0.15), tuple(capc + Ra[:, 1] * 0.38 + Ra[:, 0] * 0.08), 0.05, 0.04), ACORN_CAP, k=0.0, layer='acorn2')
    for i, (x, z, r) in enumerate(((-3.0, 1.5, 0.32), (-2.4, 2.4, 0.2), (3.3, -0.9, 0.24), (-3.3, -1.2, 0.22), (1.2, 3.4, 0.18))):
        pebble(fig, x, z, r, seed=i)
