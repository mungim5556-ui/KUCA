"""
부드러운 토이 피규어 조형 (SDF): 덩어리들을 부드럽게 녹여 붙인 뒤(smooth union) 표면을 뽑아(marching cubes)
줄이고(quadric decimation), 버텍스 색을 칠하고, 접촉부 그늘(AO)을 굽는다.

  s = Sculpt()
  s.add(ellipsoid(c, r), WHITE, k=0.4)              # 같은 layer 끼리 k 만큼 부드럽게 붙는다
  s.add(capsule(a, b, r0, r1), WHITE, k=0.25)
  s.add(box(...), GOLD, layer='prop', k=0.05)       # 다른 layer 와는 또렷하게 (min)
  s.paint(ellipsoid(...), BLUSH, soft=0.08)         # 모양 없이 색만 (볼터치, 배, 무늬)
  verts, faces, cols = s.build(voxel=0.06, tris=16000)

좌표: m, +Y 위, +Z 앞. numpy 배열 연산으로 격자 전체를 한 번에 계산한다.
"""
import math

import numpy as np

# ---------- 회전·좌표계 ----------


def rot(yaw=0.0, pitch=0.0, roll=0.0):
    """로컬 → 세계 회전 행렬 (열 = 로컬 축). 롤(Z) → 피치(X) → 요(Y) 순서, 도"""
    y, p, r = (math.radians(a) for a in (yaw, pitch, roll))
    Rz = np.array([[math.cos(r), -math.sin(r), 0], [math.sin(r), math.cos(r), 0], [0, 0, 1]])
    Rx = np.array([[1, 0, 0], [0, math.cos(p), -math.sin(p)], [0, math.sin(p), math.cos(p)]])
    Ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    return Ry @ Rx @ Rz


def _local(P, c, R):
    q = P - np.asarray(c, np.float64)
    return q if R is None else q @ R      # R 의 열이 로컬 축 → 로컬 좌표 = q · 축


# ---------- SDF 도형 (세계 좌표 P: (N, 3) → 거리 (N,)) ----------


def ellipsoid(c, r, R=None):
    r = np.asarray(r, np.float64)

    def f(P):
        q = _local(P, c, R)
        k0 = np.linalg.norm(q / r, axis=1)
        k1 = np.linalg.norm(q / (r * r), axis=1)
        return k0 * (k0 - 1.0) / np.maximum(k1, 1e-9)
    return f


def sphere(c, r):
    c = np.asarray(c, np.float64)
    return lambda P: np.linalg.norm(P - c, axis=1) - r


def capsule(a, b, r0, r1=None):
    """둥근 원뿔 막대 (a 쪽 반지름 r0, b 쪽 r1)"""
    r1 = r0 if r1 is None else r1
    a, b = np.asarray(a, np.float64), np.asarray(b, np.float64)
    ba = b - a
    L2 = float(ba @ ba) or 1e-9

    def f(P):
        pa = P - a
        h = np.clip(pa @ ba / L2, 0.0, 1.0)
        d = np.linalg.norm(pa - h[:, None] * ba, axis=1)
        return d - (r0 + (r1 - r0) * h)
    return f


def box(c, half, round_=0.0, R=None):
    half = np.asarray(half, np.float64) - round_

    def f(P):
        q = np.abs(_local(P, c, R)) - half
        return np.linalg.norm(np.maximum(q, 0.0), axis=1) + np.minimum(q.max(axis=1), 0.0) - round_
    return f


def cylinder(c, r, h, round_=0.0, R=None):
    """세로 원기둥 (로컬 Y 축), 중심 c, 반높이 h"""
    def f(P):
        q = _local(P, c, R)
        d = np.stack([np.linalg.norm(q[:, [0, 2]], axis=1) - (r - round_), np.abs(q[:, 1]) - (h - round_)], axis=1)
        return np.linalg.norm(np.maximum(d, 0.0), axis=1) + np.minimum(d.max(axis=1), 0.0) - round_
    return f


def torus(c, R_, r, Rm=None):
    """로컬 XZ 평면 고리"""
    def f(P):
        q = _local(P, c, Rm)
        return np.linalg.norm(np.stack([np.linalg.norm(q[:, [0, 2]], axis=1) - R_, q[:, 1]], axis=1), axis=1) - r
    return f


def cone(base, tip, r, r_tip=0.04):
    """밑면 반지름 r → 끝 r_tip 로 가늘어지는 둥근 원뿔 (귀·부리·가시)"""
    return capsule(base, tip, r, r_tip)


def onion(f, t):
    """껍질 (두께 t)"""
    return lambda P: np.abs(f(P)) - t


def subtract(f, g, k=0.0):
    """f 에서 g 를 (부드럽게) 뺀다"""
    if k <= 0:
        return lambda P: np.maximum(f(P), -g(P))

    def h(P):
        a, b = f(P), g(P)
        hh = np.clip(0.5 - 0.5 * (a + b) / k, 0.0, 1.0)
        return a * (1 - hh) - b * hh + k * hh * (1 - hh)
    return h


def intersect(f, g):
    return lambda P: np.maximum(f(P), g(P))


def smin(a, b, k):
    if k <= 0:
        return np.minimum(a, b)
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0.0, 1.0)
    return b + (a - b) * h - k * h * (1.0 - h)


# ---------- 조형 ----------


class Sculpt:
    def __init__(self):
        self.parts = []      # (f, col, k, layer)
        self.paints = []     # (f, col, soft)
        self.order = []

    def add(self, f, col, k=0.2, layer='body'):
        self.parts.append((f, np.asarray(col, np.float64), k, layer))
        if layer not in self.order:
            self.order.append(layer)

    PAINT_EDGE = 0.35   # 칠 경계 선명도 (모든 paint 의 soft 에 곱한다; 작을수록 또렷한 색 경계)

    def paint(self, f, col, soft=0.05):
        self.paints.append((f, np.asarray(col, np.float64), soft * self.PAINT_EDGE))

    def field(self, P, with_parts=False):
        layers = {}
        per = []
        for f, col, k, layer in self.parts:
            d = f(P)
            per.append(d)
            layers[layer] = d if layer not in layers else smin(layers[layer], d, k)
        out = None
        for layer in self.order:
            out = layers[layer] if out is None else np.minimum(out, layers[layer])
        return (out, per) if with_parts else out

    def _grid_field(self, lo, hi, voxel, chunk=600000):
        nx, ny, nz = (np.ceil((hi - lo) / voxel).astype(int) + 1)
        xs, ys, zs = (lo[i] + np.arange(n) * voxel for i, n in enumerate((nx, ny, nz)))
        vol = np.empty((nx, ny, nz), np.float32)
        X, Y = np.meshgrid(xs, ys, indexing='ij')
        plane = np.stack([X.ravel(), Y.ravel()], axis=1)
        for k0 in range(0, nz, max(1, chunk // len(plane))):
            k1 = min(nz, k0 + max(1, chunk // len(plane)))
            Z = zs[k0:k1]
            P = np.concatenate([np.column_stack([plane, np.full(len(plane), z)]) for z in Z])
            d = self.field(P).reshape(k1 - k0, nx, ny)
            vol[:, :, k0:k1] = np.transpose(d, (1, 2, 0))
        return vol

    def normals(self, V, eps=0.01):
        e = np.eye(3) * eps
        g = np.stack([self.field(V + e[i]) - self.field(V - e[i]) for i in range(3)], axis=1)
        return g / np.maximum(np.linalg.norm(g, axis=1, keepdims=True), 1e-9)

    def colors(self, V, sharp=0.012, ao=0.5):
        dmin, per = self.field(V, with_parts=True)
        D = np.abs(np.stack(per, axis=1))                                # (N, parts) 표면까지 거리
        cols = np.stack([c for _, c, _, _ in self.parts])                # (parts, 3)
        layer_of = [l for _, _, _, l in self.parts]
        owner = D.argmin(axis=1)
        same = np.array([[layer_of[i] == layer_of[j] for j in range(len(layer_of))] for i in range(len(layer_of))])
        W = np.exp(-(D - D.min(axis=1, keepdims=True)) / sharp) * same[owner]
        C = (W @ cols) / W.sum(axis=1, keepdims=True)
        for f, col, soft in self.paints:
            a = np.clip(0.5 - f(V) / soft * 0.5, 0.0, 1.0)[:, None]
            C = C * (1 - a) + col * a
        if ao > 0:
            N = self.normals(V)
            occ = np.zeros(len(V))
            for i, h in enumerate((0.08, 0.18, 0.32, 0.5, 0.75)):
                occ += (h - self.field(V + N * h)) / (2 ** i)
            a = np.clip(1.0 - ao * np.clip(occ, 0, None) * 1.2, 0.62, 1.0)[:, None]
            C = C * a   # 접촉 그늘만 (색을 탁하게 하는 보랏빛 섞기 없음)
        return np.clip(C, 0, 1)

    def _relax(self, V, F, iters=6):
        """줄인 뒤 울퉁불퉁함 제거: Taubin 다듬기 + 표면(SDF 0)으로 다시 붙이기"""
        n = len(V)
        e = np.concatenate([F[:, [0, 1]], F[:, [1, 2]], F[:, [2, 0]]])
        e = np.concatenate([e, e[:, ::-1]])
        deg = np.bincount(e[:, 0], minlength=n).astype(np.float64)
        deg[deg == 0] = 1

        def lap(X):
            acc = np.zeros_like(X)
            np.add.at(acc, e[:, 0], X[e[:, 1]])
            return acc / deg[:, None] - X
        for _ in range(iters):
            V = V + 0.5 * lap(V)
            V = V - 0.53 * lap(V)
        for _ in range(3):
            d = self.field(V)
            N = self.normals(V)
            V = V - N * d[:, None]
        return V

    def build(self, lo, hi, voxel=0.06, tris=16000, ao=0.5):
        from skimage import measure
        import fast_simplification
        lo, hi = np.asarray(lo, np.float64), np.asarray(hi, np.float64)
        vol = self._grid_field(lo, hi, voxel)
        V, F, _, _ = measure.marching_cubes(vol, 0.0, spacing=(voxel,) * 3)
        V = V + lo
        # 마칭 큐브 면의 1/38 아래로는 줄이지 않는다: 그보다 세게 줄이면 이차 오차 간략화가 귀·팔·받침 잔디판을 찢어
        # 뾰족하게 무너뜨렸다 (우주 토끼 지도용 LOD1: 35만 → 5천 에서 긴 귀가 고양이 귀처럼). 단순한 펫은 그대로 tris.
        tris = max(tris, len(F) // 38)
        if len(F) > tris:
            V, F = fast_simplification.simplify(V.astype(np.float32), F.astype(np.int32), 1.0 - tris / len(F))
            V = V.astype(np.float64)
        V = self._relax(V, F)
        # 감는 방향: 면 법선이 SDF 기울기(바깥)와 같은 쪽이 되게
        a, b, c = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
        fn = np.cross(b - a, c - a)
        g = self.normals((a + b + c) / 3)
        if np.median(np.sum(fn * g, axis=1)) < 0:
            F = F[:, ::-1]
        return V, F, self.colors(V, ao=ao)


def add_to_builder(mb, V, F, C, emissive_mask=None):
    """조형 결과를 Builder 에 (정점 3개씩) 넣는다"""
    tri = V[F].reshape(-1, 3)
    col = C[F].reshape(-1, 3)
    mb.pos.extend(tri.ravel().tolist())
    mb.col.extend(col.ravel().tolist())
    if emissive_mask is not None or mb.alpha:
        al = np.ones(len(tri)) if emissive_mask is None else np.where(emissive_mask[F].reshape(-1), 0.0, 1.0)
        if not mb.alpha:
            mb.alpha.extend([1.0] * (len(mb.pos) // 3 - len(tri)))
        mb.alpha.extend(al.tolist())
