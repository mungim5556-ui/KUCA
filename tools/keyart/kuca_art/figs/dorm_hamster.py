"""기숙사 햄스터: 분홍 줄무늬 잠옷 + 크림 테 수면 모자(진홍 방울), 볼 빵빵, 졸린 눈, 받침 위 구름 베개 · 접힌 담요"""
import math

import numpy as np

from .. import sculpt as S
from .. import figures as Fg

TIER = 'gold'

CREAM = (0.99, 0.95, 0.89)
FUR = (0.98, 0.68, 0.24)
EAR_IN = (1.0, 0.68, 0.70)
PJ_W = (0.99, 0.95, 0.93)
PJ_P = (0.95, 0.55, 0.58)
PJ_EDGE = (0.90, 0.42, 0.47)
CUFF = (0.99, 0.94, 0.88)
PAW = (1.0, 0.87, 0.82)
POM = (0.78, 0.10, 0.14)
PILLOW = (0.99, 0.96, 0.90)


def stripe_w(origin, axis, n, duty=0.38, phase=math.pi, ref=(0, 0, 1)):
    """줄무늬 가중치 0..1 (axis 둘레 각도)"""
    o = np.asarray(origin, np.float64)
    a = np.asarray(axis, np.float64)
    a = a / np.linalg.norm(a)
    u = np.asarray(ref, np.float64) - a * (np.dot(ref, a))
    u /= np.linalg.norm(u)
    v = np.cross(a, u)
    T = math.cos(duty * math.pi)

    def w(P):
        q = P - o
        c = np.cos(np.arctan2(q @ v, q @ u) * n + phase)
        return np.clip((c - T) / 0.22 + 0.5, 0, 1)
    return w


def add_striped(fig, f, sw, k, relief=0.02):
    """줄무늬를 살짝 돋운 잠옷 천 (돋움이 있어야 메시를 줄여도 줄 경계에 정점이 남는다 — 돋움 없이 칠만 하면 줄이 톱니 얼룩이 됨) + 분홍 칠"""
    fig.add(lambda P: f(P) - relief * sw(P), PJ_W, k=k, layer='pj')
    fig.paint(lambda P: (0.5 - sw(P)) * 0.2 + np.maximum(0, f(P) - relief * sw(P) - 0.05), PJ_P, soft=0.03)


def band_w(origin, axis, period, duty=0.45):
    o = np.asarray(origin, np.float64)
    a = np.asarray(axis, np.float64)
    a = a / np.linalg.norm(a)
    T = math.cos(duty * math.pi)

    def w(P):
        c = np.cos(((P - o) @ a) * 2 * math.pi / period)
        return np.clip((c - T) / 0.22 + 0.5, 0, 1)
    return w


def bands(fig, region, origin, axis, period, col, duty=0.45, soft=0.03):
    """axis 방향으로 번갈아 띠 (모자·담요)"""
    o = np.asarray(origin, np.float64)
    a = np.asarray(axis, np.float64)
    a = a / np.linalg.norm(a)

    def f(P):
        t = (P - o) @ a
        w = np.cos(t * 2 * math.pi / period)
        return np.maximum(math.cos(duty * math.pi) - w, region(P)) * 0.2
    fig.paint(f, col, soft=soft)


def head_frame(fig, c, yaw, pitch, layer='head', out=0.0):
    """머리 중심 c 에서 (yaw, pitch) 방향의 실제 조형 표면 위 Frame"""
    y, p = math.radians(yaw), math.radians(pitch)
    d = np.array((math.cos(p) * math.sin(y), math.sin(p), math.cos(p) * math.cos(y)))
    return Fg.surface_frame(fig, np.asarray(c) + d * 7.0, -d, out=out, layer=layer)


def w_mouth(fig, f, w, col):
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            Fg._ellipsoid(fig.extra, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.01), (0.05, 0.05, 0.04), col, 6, 4, f)


def closed_eye(fig, f, s, col=(0.20, 0.11, 0.09)):
    """졸린 ∪ 감은 눈: 촘촘한 구슬로 이은 매끈한 곡선 + 바깥 끝 짧은 속눈썹"""
    fig.eye_spots.append((np.asarray(f.o, np.float64), s * 1.3))
    for k in range(29):
        a = math.pi * k / 28
        th = math.sin(a) * 0.5 + 0.5
        Fg._ellipsoid(fig.extra, (math.cos(a) * s * 0.62, -math.sin(a) * s * 0.34, 0.0), (s * 0.075 * th + 0.02, s * 0.075 * th + 0.02, s * 0.05), col, 8, 4, f)


def build(fig, rng):
    Fg.base(fig, rng, flowers=7)
    y0 = Fg.TOP
    g = (Fg.GOLD, Fg.GOLD_HI)
    # ---- 다리: 아주 짧은 줄무늬 바지 + 크림 밑단 + 발 ----
    for s in (-1, 1):
        x = s * 0.66
        fig.add(S.ellipsoid((x, y0 + 0.22, 0.32), (0.46, 0.27, 0.56)), PAW, k=0.1, layer='paw')
        add_striped(fig, S.capsule((x, y0 + 0.6, 0.05), (x, y0 + 1.2, 0.0), 0.56, 0.6), stripe_w((x, 0, 0.03), (0, 1, 0), 8), 0.3)
        fig.add(S.torus((x, y0 + 0.55, 0.05), 0.55, 0.13), CUFF, k=0.0, layer='cuff')
    # ---- 몸통: 동그란 공 (잠옷) ----
    BC, BR = (0, y0 + 2.15, 0.0), (1.5, 1.38, 1.36)
    tw = stripe_w((0, 0, 0), (0, 1, 0), 18)
    body = S.ellipsoid(BC, BR)
    add_striped(fig, body, tw, 0.4)
    # 윗도리 밑단 (공 아래쪽 띠)
    fig.add(S.intersect(S.ellipsoid(BC, (BR[0] + 0.05, BR[1] + 0.05, BR[2] + 0.05)), S.box((0, y0 + 1.42, 0), (3, 0.09, 3))), PJ_EDGE, k=0.0, layer='hem')
    # 앞단 + 금 단추 4개
    fig.paint(S.box((0, y0 + 2.5, 1.4), (0.08, 1.0, 0.5)), PJ_EDGE, soft=0.03)
    for k in range(4):
        y = y0 + 3.05 - k * 0.42
        z = math.sqrt(max(0.0, 1 - ((y - BC[1]) / BR[1]) ** 2 - (0.17 / BR[0]) ** 2)) * BR[2] + 0.02
        fig.add(S.ellipsoid((0.17, y, z), (0.11, 0.11, 0.06)), Fg.GOLD, k=0.0, layer='btn', metal=g)
    # 가슴 주머니 (오른쪽 가슴) + 금 단추
    pz = math.sqrt(max(0.0, 1 - ((0.25) / BR[1]) ** 2 - (0.7 / BR[0]) ** 2)) * BR[2]
    R = S.rot(30, -12, 0)
    pc = (0.7, BC[1] + 0.25, pz)
    fig.add(S.box(pc, (0.3, 0.27, 0.06), round_=0.05, R=R), PJ_W, k=0.0, layer='pocket')
    fig.paint(S.subtract(S.box(pc, (0.33, 0.3, 0.2), R=R), S.box(pc, (0.25, 0.22, 0.3), R=R)), PJ_EDGE, soft=0.03)
    fig.add(S.box((pc[0], pc[1] + 0.21, pc[2] + 0.03), (0.32, 0.07, 0.07), round_=0.03, R=R), PJ_EDGE, k=0.0, layer='pocket')
    fig.add(S.ellipsoid((pc[0] + 0.02, pc[1] + 0.15, pc[2] + 0.11), (0.07, 0.07, 0.04)), Fg.GOLD, k=0.0, layer='btn', metal=g)
    # 깃: 넓적한 셔츠 깃 두 장
    for s in (-1, 1):
        Rk = S.rot(s * 30, -40, s * 38)
        cpos = (s * 0.46, y0 + 3.3, 0.78)
        fig.add(S.box(cpos, (0.4, 0.3, 0.05), round_=0.05, R=Rk), PJ_W, k=0.0, layer='collar')
        fig.paint(S.subtract(S.box(cpos, (0.42, 0.32, 0.2), R=Rk), S.box(cpos, (0.32, 0.22, 0.3), R=Rk)), PJ_EDGE, soft=0.03)
    # ---- 팔: 짧은 줄무늬 소매 + 크림 소맷부리 + 분홍 손 ----
    for s in (-1, 1):
        sh, wr = np.array((s * 1.25, y0 + 2.75, 0.05)), np.array((s * 1.95, y0 + 1.95, 0.35))
        fig.add(S.sphere(tuple(sh), 0.5), PJ_W, k=0.15, layer='pj')
        add_striped(fig, S.capsule(tuple(sh + (wr - sh) * 0.2), tuple(wr), 0.47, 0.42), stripe_w(tuple(sh), tuple(wr - sh), 6), 0.25)
        fig.add(S.torus(tuple(wr + (wr - sh) * 0.04), 0.4, 0.12, Rm=S.rot(0, 20, s * 45)), CUFF, k=0.0, layer='cuff')
        fig.add(S.sphere(tuple(wr + (wr - sh) * 0.32), 0.36), PAW, k=0.1, layer='paw')
    # 꼬리
    fig.add(S.sphere((0, y0 + 1.55, -1.38), 0.3), CREAM, k=0.05, layer='tail')
    # ---- 머리: 아주 동그랗고 아래가 빵빵한 찹쌀떡 얼굴 (따로 붙인 볼 없음) ----
    hc = (0, y0 + 5.1, 0.15)
    HR = 2.0
    head_f = S.ellipsoid(hc, (HR * 1.1, HR * 0.95, HR * 0.98))
    fig.add(head_f, CREAM, k=0.0, layer='head')
    puff = S.ellipsoid((0, hc[1] - 0.62, hc[2] + 0.12), (HR * 1.14, HR * 0.7, HR * 0.94))
    fig.add(puff, CREAM, k=0.9, layer='head')
    head_u = lambda P: S.smin(head_f(P), puff(P), 0.9)
    fig.add(S.capsule((0, y0 + 3.1, 0.0), (0, hc[1] - 1.2, 0.05), 0.85), CREAM, k=0.25, layer='head')
    # 털 무늬: 위쪽·뒤쪽 주황, 가운데 흰 이마 줄
    fig.paint(lambda P: np.maximum.reduce([head_u(P) - 0.1, (hc[1] - 1.25) - P[:, 1], (hc[1] + 0.05 - 0.62 * np.abs(P[:, 0])) - P[:, 1] + np.maximum(0, P[:, 2] - hc[2] - 1.3) * 0.6]), FUR, soft=0.22)
    fig.paint(S.ellipsoid((0, hc[1] + 0.35, hc[2] + 1.95), (0.5, 1.2, 0.75)), CREAM, soft=0.14)
    # ---- 귀: 둥근 주황 귀 + 분홍 안쪽 ----
    for s in (-1, 1):
        ec = (s * 1.82, hc[1] + 1.45, hc[2] + 0.15)
        Re = S.rot(s * 18, 0, -s * 22)
        ear = S.ellipsoid(ec, (0.7, 0.68, 0.32), R=Re)
        inner = S.ellipsoid(tuple(np.array(ec) + Re @ np.array((0, 0.02, 0.26))), (0.48, 0.46, 0.2), R=Re)
        fig.add(S.subtract(ear, inner, k=0.06), FUR, k=0.0, layer='ear')
        fig.paint(S.ellipsoid(tuple(np.array(ec) + Re @ np.array((0, 0.02, 0.2))), (0.5, 0.48, 0.2), R=Re), EAR_IN, soft=0.04)
    # ---- 수면 모자: 크림 테 + 분홍 줄무늬 원뿔이 오른쪽으로 접혀 내려옴 + 진홍 방울 ----
    Rb = S.rot(0, -10, 0)
    hq = lambda P: (P - np.array(hc)) @ Rb
    head_big = S.ellipsoid((0, 0, 0), (HR * 1.1 + 0.28, HR * 0.95 + 0.28, HR * 0.98 + 0.28))
    def brim(P):
        q = hq(P)
        a_, b_ = head_big(q) + 0.12, np.abs(q[:, 1] - 1.12) - 0.3 + 0.12
        return np.linalg.norm(np.maximum(np.stack([a_, b_], 1), 0), axis=1) + np.minimum(np.maximum(a_, b_), 0) - 0.12
    fig.add(brim, CUFF, k=0.0, layer='brim')
    path = [np.array(p) for p in ((0.0, hc[1] + 1.25, -0.15), (0.4, hc[1] + 2.05, -0.3), (1.15, hc[1] + 2.55, -0.45),
                                  (1.85, hc[1] + 2.5, -0.5), (2.35, hc[1] + 1.85, -0.5), (2.6, hc[1] + 1.1, -0.45), (2.65, hc[1] + 0.55, -0.4))]
    rads = (1.78, 1.25, 0.82, 0.58, 0.42, 0.3, 0.24)
    cap_parts = []
    for (a, b), (r0, r1) in zip(zip(path, path[1:]), zip(rads, rads[1:])):
        cap_parts.append(S.capsule(tuple(a), tuple(b), r0, r1))
    cw = band_w((0, hc[1] + 1.4, 0), (0.6, 1, -0.1), 0.55)
    capmask = lambda P: np.clip((hq(P)[:, 1] - 1.42) / 0.1, 0, 1)
    for k_, f in enumerate(cap_parts):
        g_ = f if k_ else S.intersect(f, lambda P: 1.07 - hq(P)[:, 1])
        fig.add(lambda P, g_=g_: g_(P) - 0.022 * cw(P) * capmask(P), PJ_W, k=0.35, layer='cap')

    def cap_region(P):
        return np.min(np.stack([f(P) for f in cap_parts]), axis=0) - 0.08

    fig.paint(lambda P: np.maximum((0.5 - cw(P) * capmask(P)) * 0.2, cap_region(P)), PJ_P, soft=0.03)
    pom = path[-1] + np.array((0.05, -0.38, 0.0))
    fig.add(lambda P: S.sphere(tuple(pom), 0.55)(P) + 0.025 * np.sin(P[:, 0] * 30) * np.sin(P[:, 1] * 27) * np.sin(P[:, 2] * 29), POM, k=0.0, layer='pom')
    # ---- 얼굴: 졸린 ∪ 감은 눈, 작은 분홍 코, 'w' 입, 볼터치는 칠만 ----
    for s in (-1, 1):
        ef = head_frame(fig, hc, s * 26, -8)
        closed_eye(fig, ef, 0.5)
    nf = head_frame(fig, hc, 0, -19)
    Fg._ellipsoid(fig.extra, (0, 0, 0), (0.15, 0.1, 0.08), (1.0, 0.58, 0.64), 12, 6, nf)
    w_mouth(fig, head_frame(fig, hc, 0, -26), 0.26, (0.36, 0.2, 0.18))
    for s in (-1, 1):
        p = np.array(head_frame(fig, hc, s * 46, -22).o)
        fig.paint(S.sphere(p, 0.55), Fg.BLUSH, soft=0.65)
    # ---- 받침 소품: 세운 구름 베개 (오른손이 기대고) + 접힌 줄무늬 담요 ----
    px, pz = 2.85, 0.75
    Rp = S.rot(-30, 0, 0)
    blobs = [((0, 0.95, 0), 0.62), ((-0.55, 0.75, 0), 0.5), ((0.55, 0.75, 0), 0.5), ((-0.35, 1.35, 0), 0.48), ((0.3, 1.4, 0), 0.5),
             ((0, 0.42, 0), 0.45), ((-0.6, 0.35, 0), 0.32), ((0.6, 0.35, 0), 0.32)]
    for (bx, by, bz), r in blobs:
        c = np.array((px, y0 - 0.05, pz)) + Rp @ np.array((bx, by, bz))
        fig.add(S.ellipsoid(tuple(c), (r, r, r * 0.62), R=Rp), PILLOW, k=0.22, layer='pillow')
    bx_, bz_ = -2.75, 1.45
    Rbk = S.rot(25, 0, 0)
    bw = band_w((bx_, 0, bz_), tuple(Rbk @ np.array((1, 0, 0))), 0.5, duty=0.42)
    for k in range(3):
        bf = S.box((bx_, y0 + 0.2 + k * 0.32, bz_), (1.0, 0.15, 0.7), round_=0.13, R=Rbk)
        fig.add(lambda P, bf=bf: bf(P) - 0.018 * bw(P), PJ_W, k=0.06, layer='blanket')
    fig.add(S.capsule(tuple(np.array((bx_, y0 + 0.5, bz_)) + Rbk @ np.array((-1.0, 0, 0.62))), tuple(np.array((bx_, y0 + 0.5, bz_)) + Rbk @ np.array((1.0, 0, 0.62))), 0.3), PJ_W, k=0.08, layer='blanket')
    bands(fig, lambda P: np.maximum(S.box((bx_, y0 + 0.5, bz_), (1.2, 0.7, 1.0), R=Rbk)(P), (y0 + 0.08) - P[:, 1]), (bx_, 0, bz_), tuple(Rbk @ np.array((1, 0, 0))), 0.5, PJ_P, duty=0.42)
