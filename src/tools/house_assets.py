"""Meshes and textures for the house relief (Buildings.TrimVariant .. FirstGarageVariant, see HouseDetail.cs).

Variant order (must match Buildings.cs): trim box, bargeboard, 2 dormer bodies, 3 front doors, 2 garage doors.
Every variant gets its own texture files (two materials sharing one KTX2 path broke every texture in game).
Face painting works in metres on the typical size of the piece (the unit box stretches the texture over it).
"""
import numpy as np

import facade_assets

SIZE = facade_assets.SIZE
HALF = SIZE // 2
BARGE_THICKNESS = 0.07          # vertical thickness of a bargeboard as a share of the roof rise (HouseDetail.BargeThickness)

TRIM_RGB = (238, 236, 230)
DORMER_WALLS = [("white", (234, 232, 226), False), ("slate", (86, 88, 94), True)]
DOOR_LEAVES = [("brown", (92, 60, 42)), ("green", (46, 72, 58)), ("cream", (226, 222, 212))]
GARAGE_BODIES = [("white", (232, 230, 224)), ("grey", (138, 140, 146))]

DORMER_VARIANTS = len(DORMER_WALLS)
DOOR_VARIANTS = len(DOOR_LEAVES)
GARAGE_VARIANTS = len(GARAGE_BODIES)
VARIANTS = 2 + DORMER_VARIANTS + DOOR_VARIANTS + GARAGE_VARIANTS


# ---- meshes (x up, y and z in [-0.5, 0.5], no bottom face; CCW seen from outside) ----

def _emit(positions, normals, uvs, indices, quad, normal, uv):
    base = len(positions)
    for p in quad:
        positions.append(p)
        normals.append(normal)
        uvs.append(uv(p))
    for tri in ((0, 1, 2), (0, 2, 3)):
        a, b, c = (quad[i] for i in tri)
        u = [b[k] - a[k] for k in range(3)]
        v = [c[k] - a[k] for k in range(3)]
        cross = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        if sum(cross[k] * normal[k] for k in range(3)) < 0:
            tri = (tri[0], tri[2], tri[1])
        indices.extend(base + i for i in tri)


def dormer():
    """Unit box whose +z face (the front, texture left half) carries the window; the other sides and the top use the
    plain right half."""
    m = 0.03
    front_u, plain_u = (0.03, 0.47), (0.53, 0.97)
    positions, normals, uvs, indices = [], [], [], []
    top = [(1, y, z) for y, z in ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5))]
    _emit(positions, normals, uvs, indices, top, (1, 0, 0),
          lambda p: (plain_u[0] + (plain_u[1] - plain_u[0]) * (p[1] + .5), m + (1 - 2 * m) * (p[2] + .5)))
    for axis, sign in ((1, 1), (1, -1), (2, 1), (2, -1)):
        other = 3 - axis
        n = [0, 0, 0]
        n[axis] = sign
        quad = []
        for h, x in ((-.5, 0), (.5, 0), (.5, 1), (-.5, 1)):
            p = [x, 0, 0]
            p[axis] = 0.5 * sign
            p[other] = h
            quad.append(tuple(p))
        u_range = front_u if (axis == 2 and sign == 1) else plain_u
        _emit(positions, normals, uvs, indices, quad, tuple(n),
              lambda p, o=other, r=u_range: (r[0] + (r[1] - r[0]) * (p[o] + .5), m + (1 - 2 * m) * (1 - p[0])))
    return positions, normals, uvs, indices


def barge(thickness=BARGE_THICKNESS):
    """Bargeboard of a gable end: two slanted boards in the gable plane (x up, z across, eaves at z = +-0.5, apex at
    x = 1), `thickness` tall measured vertically (hangs below the eave and the apex line), extruded over y in [-0.5, 0.5].
    The instance scales y to the board thickness, z to the roof width and x to the roof rise."""
    c = thickness
    positions, normals, uvs, indices = [], [], [], []

    def planar(quad, region):
        # parallelogram quad: p = p0 + s (p1 - p0) + t (p3 - p0); uv inside `region` (u0, u1, v0, v1)
        p0, p1, p3 = quad[0], quad[1], quad[3]
        e1 = [p1[k] - p0[k] for k in range(3)]
        e2 = [p3[k] - p0[k] for k in range(3)]
        d11 = sum(x * x for x in e1); d22 = sum(x * x for x in e2); d12 = sum(e1[k] * e2[k] for k in range(3))
        det = d11 * d22 - d12 * d12

        def uv(p):
            r = [p[k] - p0[k] for k in range(3)]
            r1 = sum(r[k] * e1[k] for k in range(3)); r2 = sum(r[k] * e2[k] for k in range(3))
            s = (r1 * d22 - r2 * d12) / det
            t = (r2 * d11 - r1 * d12) / det
            return (region[0] + (region[1] - region[0]) * s, region[2] + (region[3] - region[2]) * t)
        return uv

    region = (0.1, 0.4, 0.1, 0.9)
    for s in (1, -1):
        z = 0.5 * s
        top0, top1 = (0, z), (1, 0)            # upper edge (x, z)
        # faces: front (+y), back (-y), upper slanted, lower slanted, eave end
        for y, ny in ((0.5, 1), (-0.5, -1)):
            quad = [(-c, y, z), (0, y, z), (1, y, 0), (1 - c, y, 0)]
            _emit(positions, normals, uvs, indices, quad, (0, ny, 0), planar(quad, region))
        upper = [(0, -.5, z), (0, .5, z), (1, .5, 0), (1, -.5, 0)]
        n_up = _perp(upper, outward=(1, 0, 0.3 * s))
        _emit(positions, normals, uvs, indices, upper, n_up, planar(upper, region))
        lower = [(-c, -.5, z), (-c, .5, z), (1 - c, .5, 0), (1 - c, -.5, 0)]
        n_low = tuple(-v for v in _perp(lower, outward=(1, 0, 0.3 * s)))
        _emit(positions, normals, uvs, indices, lower, n_low, planar(lower, region))
        end = [(-c, -.5, z), (-c, .5, z), (0, .5, z), (0, -.5, z)]
        _emit(positions, normals, uvs, indices, end, (0, 0, s), planar(end, region))
    return positions, normals, uvs, indices


def _norm(v):
    length = sum(x * x for x in v) ** 0.5
    return tuple(x / length for x in v)


def _perp(quad, outward):
    a, b, c = quad[0], quad[1], quad[2]
    u = [b[k] - a[k] for k in range(3)]
    v = [c[k] - a[k] for k in range(3)]
    n = _norm((u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]))
    return n if sum(n[k] * outward[k] for k in range(3)) >= 0 else tuple(-x for x in n)


# ---- textures ----

def _canvas(width, height):
    """Metres from the left (X) and from the top (Y) of every texel of the face half, shape (SIZE, HALF)."""
    xs = (np.arange(HALF) + 0.5) / HALF * width
    ys = (np.arange(SIZE) + 0.5) / SIZE * height
    return np.meshgrid(xs, ys)


def _inside(X, Y, x0, x1, y0, y1):
    return (X >= x0) & (X < x1) & (Y >= y0) & (Y < y1)


def _soft(mask, rgb, colour, k=1.0):
    rgb[mask] = np.array(colour, dtype="float32") * k


def _assemble(face_rgb, face_ao, face_rough, face_metal, side_rgb, rng, side_rough=190.0):
    """Face half on the left; the right half (tops, sides) is a plain colour with a little grain."""
    n, h = SIZE, HALF
    grain = 1 + 0.02 * facade_assets.noise(rng, n, 0.8)[:, :h]
    side = np.array(side_rgb, dtype="float32")[None, None, :] * grain[..., None]
    return facade_assets.pack(np.concatenate([face_rgb, side], axis=1),
                              np.concatenate([face_ao, np.full((n, h), 255.0)], axis=1),
                              np.concatenate([face_rough, np.full((n, h), side_rough)], axis=1),
                              np.concatenate([face_metal, np.zeros((n, h))], axis=1))


def trim(seed):
    """Plain painted wood."""
    rng = np.random.default_rng(4000 + seed)
    n, h = SIZE, HALF
    grain = 1 + 0.015 * facade_assets.noise(rng, n, 0.8)[:, :h]
    face = np.array(TRIM_RGB, dtype="float32")[None, None, :] * grain[..., None]
    return _assemble(face, np.full((n, h), 255.0), np.full((n, h), 190.0), np.zeros((n, h)), TRIM_RGB, rng)


def dormer_images(k):
    """Dormer body: front with a white-framed window with a cross muntin, plain cheeks."""
    name, wall, clad = DORMER_WALLS[k]
    rng = np.random.default_rng(4100 + k)
    n, h = SIZE, HALF
    W, H = 1.4, 1.3
    X, Y = _canvas(W, H)
    rgb = np.empty((n, h, 3), dtype="float32")
    rgb[:] = np.array(wall, dtype="float32")
    if clad:                                                    # horizontal boarding
        lines = 0.5 + 0.5 * np.cos(Y / 0.09 * 2 * np.pi)
        rgb *= (1 - 0.10 * lines)[..., None]
    ao = np.full((n, h), 255.0)
    rough = np.full((n, h), 200.0)
    metal = np.zeros((n, h))
    frame = np.array(TRIM_RGB, dtype="float32")
    outer = _inside(X, Y, 0.30, 1.10, 0.12, 1.10)
    glass = _inside(X, Y, 0.36, 1.04, 0.18, 1.04)
    sill = _inside(X, Y, 0.26, 1.14, 1.10, 1.17)
    rgb[outer | sill] = frame
    sky = np.clip((Y - 0.18) / 0.86, 0, 1)[..., None]
    pane = np.array((120, 140, 160), dtype="float32") * (1 - sky) + np.array((46, 56, 70), dtype="float32") * sky
    rgb[glass] = pane[glass]
    rough[glass] = 40.0
    muntin = glass & ((np.abs(X - 0.70) < 0.016) | (np.abs(Y - 0.61) < 0.016))
    rgb[muntin] = frame
    rough[muntin] = 190.0
    shadow = _inside(X, Y, 0.30, 1.10, 0.12, 0.16) | _inside(X, Y, 0.30, 0.34, 0.12, 1.10)
    ao[shadow & ~glass] = 200.0                                  # reveal shadow
    ao[glass & ((X < 0.40) | (Y < 0.22))] = 170.0
    return _assemble(rgb, ao, rough, metal, wall, rng)


def door_images(k):
    """Front door with white frame: panelled leaf, glazing in the first two, brass handle."""
    name, leaf = DOOR_LEAVES[k]
    rng = np.random.default_rng(4200 + k)
    n, h = SIZE, HALF
    W, H = 1.0, 2.15
    X, Y = _canvas(W, H)
    rgb = np.empty((n, h, 3), dtype="float32")
    rgb[:] = np.array(TRIM_RGB, dtype="float32")
    ao = np.full((n, h), 255.0)
    rough = np.full((n, h), 190.0)
    metal = np.zeros((n, h))
    body = _inside(X, Y, 0.07, W - 0.07, 0.07, H)
    rgb[body] = np.array(leaf, dtype="float32")
    rough[body] = 150.0
    grain = 1 + 0.03 * facade_assets.noise(rng, n, 1.2)[:, :h]
    rgb[body] *= grain[body][:, None]
    glazed = k < 2

    def panel(x0, x1, y0, y1, glass):
        outer = _inside(X, Y, x0, x1, y0, y1)
        inner = _inside(X, Y, x0 + 0.04, x1 - 0.04, y0 + 0.04, y1 - 0.04)
        ring = outer & ~inner
        rgb[ring] = rgb[ring] * 0.78
        ao[ring] = 170.0
        if glass:
            sky = np.clip((Y - y0) / (y1 - y0), 0, 1)[..., None]
            pane = np.array((130, 150, 168), dtype="float32") * (1 - sky) + np.array((52, 62, 76), dtype="float32") * sky
            rgb[inner] = pane[inner]
            rough[inner] = 40.0
        else:
            rgb[inner] = rgb[inner] * 1.06
    panel(0.2, 0.8, 0.22, 1.0, glazed)
    panel(0.2, 0.8, 1.2, 2.0, False)
    handle = (X - 0.82) ** 2 + (Y - 1.12) ** 2 < 0.035 ** 2
    rgb[handle] = (190, 150, 70)
    metal[handle] = 200.0
    rough[handle] = 90.0
    foot = np.clip((Y - (H - 0.15)) / 0.15, 0, 1)
    ao -= 70 * foot
    return _assemble(rgb, ao, rough, metal, leaf, rng)


def garage_images(k):
    """Sectional garage door: four horizontal panels, the top one with a strip of four windows, white frame."""
    name, body = GARAGE_BODIES[k]
    rng = np.random.default_rng(4300 + k)
    n, h = SIZE, HALF
    W, H = 2.9, 2.1
    X, Y = _canvas(W, H)
    rgb = np.empty((n, h, 3), dtype="float32")
    rgb[:] = np.array(TRIM_RGB, dtype="float32")
    ao = np.full((n, h), 255.0)
    rough = np.full((n, h), 170.0)
    metal = np.zeros((n, h))
    fw = 0.08
    door = _inside(X, Y, fw, W - fw, fw, H)
    rows = 4
    row_h = (H - fw) / rows
    t = np.clip((Y - fw) / row_h % 1.0, 0, 1)                     # position within a panel row
    shade = 0.94 + 0.08 * t
    rgb[door] = (np.array(body, dtype="float32")[None, None, :] * shade[..., None])[door]
    seam = door & (t < 0.07)
    rgb[seam] = rgb[seam] * 0.7
    ao[seam] = 150.0
    panes = 4
    pane_w = (W - 2 * fw - 0.3) / panes
    for i in range(panes):
        x0 = fw + 0.15 + i * pane_w + 0.04
        x1 = fw + 0.15 + (i + 1) * pane_w - 0.04
        inner = _inside(X, Y, x0, x1, fw + 0.18, fw + row_h - 0.1)
        sky = np.clip((Y - (fw + 0.18)) / (row_h - 0.28), 0, 1)[..., None]
        glass = np.array((110, 128, 148), dtype="float32") * (1 - sky) + np.array((46, 56, 70), dtype="float32") * sky
        rgb[inner] = glass[inner]
        rough[inner] = 40.0
    handle = _inside(X, Y, W / 2 - 0.12, W / 2 + 0.12, H - 0.62, H - 0.58)
    rgb[handle] = (60, 60, 64)
    foot = np.clip((Y - (H - 0.2)) / 0.2, 0, 1)
    ao -= 70 * foot
    return _assemble(rgb, ao, rough, metal, body, rng)


def trim_images(seed):
    return trim(seed)
