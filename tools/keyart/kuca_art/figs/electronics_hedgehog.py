"""전자정보대 고슴도치: 갈색 가시 (끝마다 밤에 빛나는 따뜻한 LED), 크림 얼굴, 진홍 쿠션 헤드셋 + 마이크,
흰 작업복 (금 깃 고리·벨트·발목 테 — 키아트 턴어라운드), 손에 초록 회로 기판, 받침 위 마이크로칩"""
import math

import numpy as np

from .. import sculpt as S
from .. import figures as Fg
from .pigeon import axis_R

TIER = 'gold'

BROWN = (0.55, 0.35, 0.21)
BROWN_LT = (0.74, 0.52, 0.34)
SPINE = (0.50, 0.32, 0.20)
SPINE_TIP = (0.80, 0.62, 0.44)
CREAM = (0.99, 0.94, 0.86)
LED = (1.0, 0.86, 0.42)
SUIT = (0.97, 0.95, 0.91)
HP_W = (0.96, 0.94, 0.90)
PCB = (0.16, 0.52, 0.24)
CHIP = (0.13, 0.13, 0.15)
PIN = (0.82, 0.82, 0.85)
POUCH = (0.58, 0.59, 0.62)
STRIPE = (0.80, 0.14, 0.18)


def fib_dirs(n):
    out = []
    ga = math.pi * (3 - math.sqrt(5))
    for i in range(n):
        y = 1 - 2 * (i + 0.5) / n
        r = math.sqrt(1 - y * y)
        out.append(np.array((math.cos(ga * i) * r, y, math.sin(ga * i) * r)))
    return out


def spine_field(spines, bound_c, bound_r, inner_r, pad=0.5):
    """가시 묶음 하나의 SDF: 경계 타원체 밖 먼 점은 경계 거리 (빠름), 가까운 점만 가시별 계산"""
    A = np.array([s[0] for s in spines])
    B = np.array([s[1] for s in spines])
    R0 = np.array([s[2] for s in spines])
    R1 = np.array([s[3] for s in spines])
    bound = S.ellipsoid(bound_c, bound_r)
    inner = S.ellipsoid(bound_c, inner_r)

    def f(P):
        d = bound(P)
        out = np.where(inner(P) < -0.1, 1.0, d)
        near = np.where((d < pad) & (out < 1.0))[0]
        if len(near):
            Q = P[near]
            best = np.full(len(Q), 1e9)
            for a, b, r0, r1 in zip(A, B, R0, R1):
                ba = b - a
                pa = Q - a
                h = np.clip(pa @ ba / (ba @ ba), 0, 1)
                dd = np.linalg.norm(pa - h[:, None] * ba, axis=1) - (r0 + (r1 - r0) * h)
                best = np.minimum(best, dd)
            out[near] = best
        return out
    return f


def head_frame(fig, c, yaw, pitch, layer='body', out=0.0, parts=None):
    """머리 중심 c 에서 (yaw, pitch) 방향의 실제 조형 표면 위 Frame (가시 넣기 전에 부른다)"""
    y, p = math.radians(yaw), math.radians(pitch)
    d = np.array((math.cos(p) * math.sin(y), math.sin(p), math.cos(p) * math.cos(y)))
    return Fg.surface_frame(fig, np.asarray(c) + d * 7.0, -d, out=out, layer=layer)


def w_mouth(fig, f, w, col):
    for s in (-1, 1):
        for k in range(9):
            a = math.pi * k / 8
            Fg._ellipsoid(fig.extra, (s * w * 0.5 + math.cos(a) * w * 0.5, -math.sin(a) * w * 0.42, 0.01), (0.045, 0.045, 0.04), col, 6, 4, f)


def build(fig, rng):
    Fg.base(fig, rng, flowers=6)
    y0 = Fg.TOP
    g = (Fg.GOLD, (1.0, 0.84, 0.36))   # 가는 금 테가 탁한 카키로 뜨지 않게 반사색을 진하게
    # ---- 다리·발: 짧고 통통 ----
    for s in (-1, 1):
        x = s * 0.66
        fig.add(S.ellipsoid((x, y0 + 0.28, 0.28), (0.5, 0.3, 0.6)), CREAM, k=0.1, layer='feet')
        fig.add(S.capsule((x, y0 + 0.5, 0.05), (x, y0 + 1.15, 0.0), 0.5, 0.56), SUIT, k=0.12)
        fig.add(S.torus((x, y0 + 0.62, 0.04), 0.5, 0.075), Fg.GOLD, k=0.0, layer='trim', metal=g)   # 발목 금 테
    # ---- 몸통: 동그란 공 + 크림 배 ----
    BC, BR = (0, y0 + 2.2, 0.0), (1.48, 1.32, 1.3)
    ball = S.ellipsoid(BC, BR)
    chest = S.ellipsoid((0, y0 + 2.7, 0.0), (1.2, 0.95, 1.05))
    fig.add(ball, SUIT, k=0.4)
    fig.add(chest, SUIT, k=0.45)
    # 흰 작업복의 금 벨트 + 목 둘레 금 고리 (갈색 머리와 흰 몸의 경계를 덮음)
    fig.add(S.intersect(S.ellipsoid(BC, (BR[0] + 0.07, BR[1] + 0.07, BR[2] + 0.07)), S.box((0, y0 + 1.82, 0), (3, 0.14, 3))),
            Fg.GOLD, k=0.0, layer='trim', metal=g)
    fig.add(S.torus((0, y0 + 3.3, 0.05), 0.9, 0.14), Fg.GOLD, k=0.0, layer='trim', metal=g)
    torso = lambda P: S.smin(ball(P), chest(P), 0.45)
    # 벨트 금 버클 + 왼가슴 금 고리 표식 + 양 엉덩이 회색 공구 주머니 (펜치 손잡이 두 개)
    fig.add(S.box((0, y0 + 1.82, 1.34), (0.24, 0.18, 0.06), round_=0.04), Fg.GOLD, k=0.0, layer='buckle', metal=g)
    fig.add(S.box((0, y0 + 1.82, 1.39), (0.12, 0.08, 0.03), round_=0.02), SUIT, k=0.0, layer='buckle2')
    fig.add(S.torus((0.42, y0 + 2.78, 1.2), 0.2, 0.05, Rm=S.rot(18, 70, 0)), Fg.GOLD, k=0.0, layer='trim', metal=g)
    for s in (-1, 1):
        pc = np.array((s * 1.17, y0 + 1.5, 0.72))
        Rp = S.rot(s * 55, 0, 0)
        fig.add(S.box(tuple(pc), (0.26, 0.3, 0.15), round_=0.08, R=Rp), POUCH, k=0.0, layer='pouch')
        fig.add(S.box(tuple(pc + Rp @ np.array((0, 0.22, 0.13))), (0.27, 0.09, 0.05), round_=0.03, R=Rp), (0.48, 0.49, 0.52), k=0.0, layer='pouch')
        for dx in (-0.09, 0.09):
            q = pc + Rp @ np.array((dx, 0.25, -0.02))
            fig.add(S.capsule(tuple(q), tuple(q + (0, 0.32, 0)), 0.05, 0.04), (0.30, 0.30, 0.33), k=0.0, layer='tool')
    # ---- 팔: 짧은 갈색 팔 + 크림 손 ----
    hands = {}
    for s in (-1, 1):
        sh = np.array((s * 1.2, y0 + 2.95, 0.05))
        if s > 0:   # 오른손(+x): 기판을 가슴 앞으로 들어 올림
            el, wr = np.array((1.72, y0 + 2.3, 0.45)), np.array((1.48, y0 + 2.5, 1.1))
        else:
            el, wr = np.array((-1.68, y0 + 2.35, 0.15)), np.array((-1.95, y0 + 1.8, 0.4))
        fig.add(S.sphere(tuple(sh), 0.46), SUIT, k=0.15)
        fig.add(S.capsule(tuple(sh), tuple(el), 0.42, 0.38), SUIT, k=0.12)
        fig.add(S.capsule(tuple(el), tuple(wr), 0.38, 0.36), SUIT, k=0.1)
        hand = wr + (wr - el) / np.linalg.norm(wr - el) * 0.25
        fig.add(S.sphere(tuple(hand), 0.36), CREAM, k=0.12, layer='paw')
        hands[s] = hand
        # 옷 신호 (펫 검토 v1: 흰 작업복이 맨몸처럼 읽힘): 손목 금 소매 테 + 윗팔 빨간 줄 2개 (키아트 턴어라운드)
        fig.add(S.torus(tuple(wr - (wr - el) * 0.05), 0.37, 0.085, Rm=axis_R(wr - el)), Fg.GOLD, k=0.0, layer='trim', metal=g)
        for t in (0.5, 0.7):
            fig.add(S.torus(tuple(sh + (el - sh) * t), 0.43, 0.06, Rm=axis_R(el - sh)), STRIPE, k=0.0, layer='decal')
    # ---- 회로 기판 (오른손) ----
    hb = hands[1]
    Rb = S.rot(-12, -8, 8)
    bc = hb + np.array((-0.05, 0.55, 0.32))
    fig.add(S.box(tuple(bc), (0.62, 0.72, 0.045), round_=0.03, R=Rb), PCB, k=0.0, layer='pcb')
    fig.add(S.box(tuple(bc + Rb @ np.array((0.02, 0.05, 0.06))), (0.26, 0.26, 0.045), round_=0.02, R=Rb), CHIP, k=0.0, layer='chip')
    for cx, cy in ((-0.45, 0.55), (0.45, 0.55), (-0.45, -0.55), (0.45, -0.55)):
        fig.add(S.cylinder(tuple(bc + Rb @ np.array((cx, cy, 0.03))), 0.07, 0.03, R=Rb @ S.rot(0, 90, 0)), Fg.GOLD, k=0.0, layer='pin', metal=g)
    for cx, cy, w, h in ((-0.32, -0.42, 0.12, 0.08), (0.34, 0.44, 0.12, 0.07)):
        fig.add(S.box(tuple(bc + Rb @ np.array((cx, cy, 0.06))), (w, h, 0.035), round_=0.01, R=Rb), (0.22, 0.22, 0.24), k=0.0, layer='chip')
    # ---- 머리: 둥근 뒤통수 + 앞으로 뾰족하게 나온 주둥이 (볼은 머리 아래 볼륨으로만) ----
    hc = (0, y0 + 5.1, 0.05)
    HR = 1.95
    HRX = (HR * 1.06, HR * 0.95, HR * 0.98)
    head_f = S.ellipsoid(hc, HRX)
    fig.add(head_f, BROWN, k=0.3)
    fig.add(S.ellipsoid((0, hc[1] - 0.5, hc[2] + 0.25), (HR * 1.0, HR * 0.7, HR * 0.85)), BROWN, k=0.8)
    fig.add(S.capsule((0, y0 + 3.2, 0), (0, hc[1] - 1.3, 0.05), 0.75), BROWN, k=0.2)
    snout_a, snout_b = np.array((0, hc[1] - 0.5, hc[2] + 1.0)), np.array((0, hc[1] - 0.72, hc[2] + 2.55))
    fig.add(S.capsule(tuple(snout_a), tuple(snout_b), 0.78, 0.17), BROWN, k=0.45)
    face = S.ellipsoid((0, hc[1] - 0.35, hc[2] + 1.3), (1.6, 1.42, 1.5))
    fig.paint(lambda P: np.maximum.reduce([face(P), -S.ellipsoid((0, hc[1] + 1.25, hc[2] + 1.5), (0.32, 0.6, 0.9))(P), (hc[1] - 2.0) - P[:, 1], P[:, 1] - (hc[1] + 0.95)]), CREAM, soft=0.06)
    # 얼굴 부품 자리 (가시·헤드셋 넣기 전에 표면을 구한다)
    eye_f = {s: head_frame(fig, hc, s * 28, 0) for s in (-1, 1)}
    nose_f = head_frame(fig, hc, 0, -14.5)
    mouth_f = head_frame(fig, hc, 0, -23)
    blush_p = {s: np.array(head_frame(fig, hc, s * 46, -18).o) for s in (-1, 1)}
    # ---- 가시: 머리 위·뒤 + 등. 끝마다 LED ----
    spines = []
    tips = []
    for d in fib_dirs(110):
        in_face = d[2] > 0.12 and abs(d[0]) < 0.92 and d[1] < 0.55
        if in_face or d[1] < -0.6 or (d[2] > 0.0 and d[1] < -0.2):
            continue
        if abs(d[2] - 0.05) < 0.13 and d[1] > 0.2:      # 헤드셋 밴드 자리 (가르마)
            continue
        if abs(d[0]) > 0.8 and abs(d[1]) < 0.3 and abs(d[2]) < 0.45:      # 헤드셋 컵 자리
            continue
        p = np.array(hc) + d * np.array(HRX) * 0.95
        dd = d + np.array((0, 0.15, -0.25))
        dd /= np.linalg.norm(dd)
        L = 0.78 + 0.12 * rng.random()    # 키아트: 짧고 끝이 둥근 물방울 가시
        tip = p + dd * L
        spines.append((p, tip, 0.52, 0.19))
        tips.append(tip + dd * 0.15)
    fig.add(spine_field(spines, hc, (HRX[0] + 1.4, HRX[1] + 1.4, HRX[2] + 1.4), (HR * 0.75, HR * 0.7, HR * 0.75)), SPINE, k=0.12)
    bspines = []
    bc_ = np.array((0, y0 + 2.4, 0.0))
    for d in fib_dirs(52):
        if d[2] > -0.35 or d[1] < -0.05 or d[1] > 0.8:    # 등 위쪽만 (아래는 흰 작업복)
            continue
        p = bc_ + d * np.array((1.55, 1.3, 1.36)) * 0.9
        dd = d + np.array((0, 0.25, -0.3))
        dd /= np.linalg.norm(dd)
        tip = p + dd * (0.7 + 0.1 * rng.random())
        bspines.append((p, tip, 0.48, 0.18))
        tips.append(tip + dd * 0.14)
    fig.add(spine_field(bspines, tuple(bc_), (2.6, 2.3, 2.5), (1.0, 0.8, 0.85)), SPINE, k=0.12)
    # 가시 끝은 밝은 갈색
    tipsA = np.array(tips)
    def tipglow(P):
        d = np.full(len(P), 9.0)
        for t in tipsA:
            d = np.minimum(d, np.linalg.norm(P - t, axis=1))
        return d - 0.38
    fig.paint(tipglow, SPINE_TIP, soft=0.35)
    fig.extra.emissive = True
    for t in tips[::2]:   # 펫 검토 v1: 끝마다 달면 촘촘한 반짝이 점처럼 보였다 → 하나 걸러, 조금 작게
        Fg._ellipsoid(fig.extra, tuple(t), (0.1, 0.1, 0.1), LED, 10, 6)
    fig.extra.emissive = False
    # ---- 헤드셋: 크림 밴드, 진홍 쿠션, 금 테, 마이크 ----
    band = S.intersect(S.torus((0, hc[1] - 0.35, hc[2] + 0.1), 2.85, 0.19, Rm=S.rot(0, 90, 0)), lambda P: hc[1] - 0.05 - P[:, 1])
    fig.add(band, HP_W, k=0.0, layer='hp')
    Rc = S.rot(0, 0, 90)
    for s in (-1, 1):
        cx = s * (HR * 1.06)
        fig.add(S.torus((cx + s * 0.12, hc[1] - 0.15, hc[2] - 0.05), 0.55, 0.25, Rm=Rc), Fg.CRIMSON, k=0.0, layer='cush')
        fig.add(S.cylinder((cx + s * 0.42, hc[1] - 0.15, hc[2] - 0.05), 0.72, 0.17, round_=0.12, R=Rc), HP_W, k=0.0, layer='hp')
        fig.add(S.torus((cx + s * 0.56, hc[1] - 0.15, hc[2] - 0.05), 0.52, 0.07, Rm=Rc), Fg.GOLD, k=0.0, layer='hpg', metal=g)
        fig.add(S.cylinder((cx + s * 0.58, hc[1] - 0.15, hc[2] - 0.05), 0.46, 0.05, round_=0.04, R=Rc), Fg.CRIMSON, k=0.0, layer='cush')
        fig.add(S.cylinder((s * 2.64, hc[1] + 0.75, hc[2] + 0.1), 0.24, 0.17, round_=0.06), Fg.GOLD, k=0.0, layer='hpg', metal=g)
    # 마이크 (왼쪽 컵 → 입 옆)
    m0 = np.array((-(HR * 1.06 + 0.45), hc[1] - 0.45, hc[2] + 0.3))
    m1 = np.array((-1.6, hc[1] - 1.05, hc[2] + 1.35))
    m2 = np.array((-0.85, hc[1] - 1.05, hc[2] + 1.85))
    fig.add(S.capsule(tuple(m0), tuple(m1), 0.06), (0.25, 0.25, 0.28), k=0.04, layer='mic')
    fig.add(S.capsule(tuple(m1), tuple(m2), 0.06), (0.25, 0.25, 0.28), k=0.04, layer='mic')
    fig.add(S.sphere(tuple(m2 + np.array((0.08, 0.0, 0.03))), 0.2), (0.16, 0.16, 0.18), k=0.03, layer='mic')
    # ---- 얼굴: 작고 반짝이는 까만 구슬 눈, 주둥이 끝 까만 코, 작은 입, 볼터치는 칠만 ----
    for s in (-1, 1):
        Fg.eye_at(fig, eye_f[s], 'bead', size=0.43, side=s)
        fig.paint(S.sphere(blush_p[s], 0.45), Fg.BLUSH, soft=0.55)
    Fg._ellipsoid(fig.extra, (0, 0, 0.02), (0.2, 0.15, 0.14), (0.12, 0.08, 0.07), 12, 6, nose_f)
    Fg._ellipsoid(fig.extra, (-0.06, 0.06, 0.12), (0.06, 0.035, 0.03), (0.6, 0.6, 0.6), 6, 4, nose_f)
    w_mouth(fig, mouth_f, 0.18, (0.3, 0.14, 0.12))
    # 목 아래 몸통은 흰 작업복: 갈색 머리·가시 색이 가슴·등으로 번진 것을 덮음
    fig.paint(lambda P: np.maximum(P[:, 1] - (y0 + 3.3), torso(P) - 0.2), SUIT, soft=0.06)
    # ---- 받침 소품: 마이크로칩 (왼쪽 앞) + 회색 돌 ----
    cx_, cz_ = -2.5, 1.9
    Rk = S.rot(25, 0, 0)
    fig.add(S.box((cx_, y0 + 0.2, cz_), (0.62, 0.15, 0.42), round_=0.04, R=Rk), CHIP, k=0.0, layer='mchip')
    fig.add(S.cylinder(tuple(np.array((cx_, y0 + 0.36, cz_)) + Rk @ np.array((-0.4, 0, -0.22))), 0.07, 0.01), (0.3, 0.3, 0.33), k=0.0, layer='mchip')
    for k in range(5):
        for s in (-1, 1):
            a = np.array((cx_, y0 + 0.2, cz_)) + Rk @ np.array(((k - 2) * 0.25, 0, s * 0.42))
            b = np.array((cx_, y0 + 0.02, cz_)) + Rk @ np.array(((k - 2) * 0.25, 0, s * 0.6))
            fig.add(S.capsule(tuple(a), tuple(b), 0.045), PIN, k=0.0, layer='mpin', metal=(PIN, (1, 1, 1)))
    rock = lambda P: S.ellipsoid((2.9, y0 + 0.25, 1.6), (0.75, 0.5, 0.6))(P) + 0.03 * np.sin(P[:, 0] * 9) * np.sin(P[:, 2] * 8)
    fig.add(rock, (0.66, 0.66, 0.68), k=0.0, layer='rock')
    # 마이크는 얼굴 칠에 덮이지 않게 다시 칠
    fig.paint(lambda P: S.sphere(tuple(m2 + np.array((0.08, 0.0, 0.03))), 0.2)(P) - 0.04, (0.16, 0.16, 0.18), soft=0.02)
    fig.paint(lambda P: np.minimum(S.capsule(tuple(m0), tuple(m1), 0.06)(P), S.capsule(tuple(m1), tuple(m2), 0.06)(P)) - 0.03, (0.25, 0.25, 0.28), soft=0.02)
    # 기판: 얼굴 칠이 번지지 않게 다시 칠하고 금색 배선
    board = S.box(tuple(bc), (0.62, 0.72, 0.045), round_=0.03, R=Rb)
    fig.paint(lambda P: board(P) - 0.025, PCB, soft=0.02)
    fig.paint(lambda P: np.maximum(np.abs(np.sin(((P - bc) @ Rb)[:, 0] * 14)) - 0.07, np.maximum(board(P) - 0.02, np.abs(((P - bc) @ Rb)[:, 1]) - 0.62)), (0.80, 0.68, 0.26), soft=0.02)
