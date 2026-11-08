"""Builds the scatter clutter assets of GEE (Scatter.xml, Meshes/GEE_Scatter.glb, Textures/GEE_Scatter_*.ktx2).

Everything is generated here (no third-party models or photos, so nothing to credit): low-poly rocks, a pebble
patch, two bushes, two wildflower patches and a fallen log, with procedural textures. Run with

    python -I src/tools/scatter_assets.py

The objects are named GEE_Scatter_<Kind>_<n> (Kind = Rock, Pebbles, Bush, Flowers, Log; n counts per kind from 0);
Scatter.cs appends them to Earth's Shrub ecotype and TreeCover.cs thins them by land cover, slope and noise. The
Shrub ecotype scales its objects 1..2.3x, so the models here are small. Writes are complete or fail early: the
glb and the xml are written last, from the same object list, so their names always match.

The texture and glb writers are the ones of tree_assets.py (KTX2 with mips, glb with tangents); this module borrows
them by pointing that module's material table at ours.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # works with python -I, which drops the script dir

import numpy as np
from PIL import Image, ImageDraw

import tree_assets as T

MOD_DIR = T.MOD_DIR
PREFIX = "GEE_Scatter"
TEX_DIR = os.path.join(MOD_DIR, "Textures")

# Atlas materials in glb order: (name, source, foliage). A LOD lists its materials in ascending order of this list.
MATERIALS = [
    ("RockGrey", None, False),
    ("RockTan", None, False),
    ("Bark", None, False),
    ("LeavesGreen", None, True),
    ("LeavesDry", None, True),
    ("Flowers", None, True),
]
MATERIAL_INDEX = {name: i for i, (name, _, _) in enumerate(MATERIALS)}
# Small things only show close up: the last LOD is cut below this many pixels (the game's rocks use 8).
LOD_SCREEN_SIZES = [96, 48, 24, 12, 5]


# ---------------------------------------------------------------------------------------------- textures

def tile_noise(size, beta, seed, aniso=(1.0, 1.0)):
    """Tileable noise in 0..1 with a 1/f^beta spectrum; aniso stretches the features (x, y frequency weights)."""
    rng = np.random.default_rng(seed)
    f = np.fft.fftfreq(size)
    fx, fy = np.meshgrid(f * aniso[0], f * aniso[1])
    radius = np.sqrt(fx ** 2 + fy ** 2)
    radius[0, 0] = 1.0
    spectrum = np.fft.fft2(rng.standard_normal((size, size))) / radius ** beta
    spectrum[0, 0] = 0
    n = np.fft.ifft2(spectrum).real
    n = (n - n.mean()) / (n.std() + 1e-9)
    return np.clip(0.5 + 0.22 * n, 0, 1)


def height_normals(height, strength):
    """Tangent-space normal map (green up) from a tileable height field."""
    gx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * 0.5 * strength
    gy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * 0.5 * strength
    n = np.dstack([-gx, gy, np.ones_like(gx)])
    return T._unit(n) * 0.5 + 0.5


def rock_texture(base, seed, lichen=0.0):
    size = 256
    h = 0.6 * tile_noise(size, 1.6, seed) + 0.4 * tile_noise(size, 0.8, seed + 1)
    speck = tile_noise(size, 0.2, seed + 2)
    shade = 0.86 + 0.75 * (h - 0.5) + 0.35 * (speck - 0.5)
    colour = np.array(base)[None, None, :] / 255 * shade[..., None]
    if lichen > 0:
        patch = np.clip((tile_noise(size, 2.0, seed + 3) - 0.62) * 8, 0, 1) * lichen
        colour = colour * (1 - patch[..., None]) + np.array([0.46, 0.5, 0.3])[None, None, :] * shade[..., None] * patch[..., None]
    return np.clip(colour, 0, 1), height_normals(h, 3.0), 0.82 + 0.15 * speck


def bark_texture(seed):
    """Left three quarters bark (vertical furrows), top-right square the sawn end of the log (rings)."""
    size = 256
    furrow = tile_noise(size, 1.2, seed, aniso=(1.0, 7.0))
    fine = tile_noise(size, 0.5, seed + 1, aniso=(1.0, 3.0))
    h = 0.75 * furrow + 0.25 * fine
    shade = 0.55 + 1.1 * (h - 0.5)
    colour = np.array([84, 64, 48])[None, None, :] / 255 * shade[..., None]
    # Sawn end: 64 x 64 texels at x 196..260 would not fit; use 56 x 56 at (198, 4).
    x0, y0, s = 198, 4, 56
    yy, xx = np.mgrid[0:s, 0:s]
    r = np.hypot(xx - s / 2 + 0.5, yy - s / 2 + 0.5) / (s / 2)
    rng = np.random.default_rng(seed + 7)
    wobble = 0.04 * np.sin(np.arctan2(yy - s / 2, xx - s / 2) * 3 + rng.uniform(0, 6))
    rings = 0.5 + 0.5 * np.sin((r + wobble) * 2 * math.pi * 5.5)
    wood = np.array([196, 160, 112])[None, None, :] / 255 * (0.78 + 0.22 * rings[..., None])
    wood = np.where(r[..., None] > 0.9, np.array([84, 64, 48]) / 255 * 0.8, wood)
    colour[y0:y0 + s, x0:x0 + s] = wood
    h[y0:y0 + s, x0:x0 + s] = 0.5
    return np.clip(colour, 0, 1), height_normals(h, 3.5), np.full((size, size), 0.9)


def leaf_clump(rgb_dark, rgb_light, seed, size=256, leaves=46, length=0.30):
    """RGBA texture of a leafy twig clump: pointed leaves around a centre, drawn supersampled."""
    ss = 4
    rng = np.random.default_rng(seed)
    img = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx = cy = size * ss / 2
    for _ in range(leaves):
        ang = rng.uniform(0, 2 * math.pi)
        dist = rng.uniform(0.0, 0.26) * size * ss
        x, y = cx + math.cos(ang) * dist, cy + math.sin(ang) * dist
        a = ang + rng.uniform(-0.6, 0.6)
        ln = length * size * ss * rng.uniform(0.7, 1.15)
        wd = ln * rng.uniform(0.28, 0.4)
        t = rng.uniform(0, 1)
        col = tuple(int(rgb_dark[i] + (rgb_light[i] - rgb_dark[i]) * t) for i in range(3))
        pts = []
        for k in range(13):                       # outline of a leaf: width profile sin(pi u)^0.8
            u = k / 12
            w = math.sin(math.pi * u) ** 0.8 * wd / 2
            pts.append((u * ln, w))
        outline = [(p, w) for p, w in pts] + [(p, -w) for p, w in reversed(pts)]
        poly = [(x + math.cos(a) * p - math.sin(a) * w, y + math.sin(a) * p + math.cos(a) * w) for p, w in outline]
        d.polygon(poly, fill=col + (255,))
        d.line([(x, y), (x + math.cos(a) * ln * 0.95, y + math.sin(a) * ln * 0.95)],
               fill=tuple(int(c * 0.75) for c in col) + (255,), width=max(2, ss))
    return np.asarray(img.resize((size, size), Image.BOX)).astype(np.float64) / 255


def flower_tile(petal, centre, seed, size=256, flowers=9):
    """RGBA tile of a wildflower patch seen from the side: grass blades and stems with flower heads."""
    ss = 4
    rng = np.random.default_rng(seed)
    img = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    S = size * ss
    greens = [(70, 112, 44), (88, 128, 52), (58, 96, 40)]
    for _ in range(46):                           # grass blades
        x = rng.uniform(0.06, 0.94) * S
        h = rng.uniform(0.18, 0.5) * S
        lean = rng.uniform(-0.12, 0.12) * S
        w = rng.uniform(0.012, 0.026) * S
        d.polygon([(x - w, S), (x + w, S), (x + lean, S - h)], fill=greens[rng.integers(3)] + (255,))
    heads = []
    for _ in range(flowers):
        x = rng.uniform(0.12, 0.88) * S
        top = S - rng.uniform(0.35, 0.8) * S
        lean = rng.uniform(-0.05, 0.05) * S
        d.line([(x, S), (x + lean * 0.5, S - (S - top) * 0.5), (x + lean, top)], fill=(66, 104, 42, 255), width=int(0.014 * S))
        heads.append((x + lean, top))
    for x, y in heads:
        r = rng.uniform(0.035, 0.06) * S
        tint = rng.uniform(0.9, 1.08)
        col = tuple(min(255, int(c * tint)) for c in petal)
        for k in range(6):
            a = k * math.pi / 3 + rng.uniform(0, 0.4)
            px, py = x + math.cos(a) * r * 0.9, y + math.sin(a) * r * 0.9
            d.ellipse([px - r * 0.7, py - r * 0.7, px + r * 0.7, py + r * 0.7], fill=col + (255,))
        d.ellipse([x - r * 0.5, y - r * 0.5, x + r * 0.5, y + r * 0.5], fill=centre + (255,))
    return np.asarray(img.resize((size, size), Image.BOX)).astype(np.float64) / 255


def flat_normal(w, h):
    return np.tile([0.5, 0.5, 1.0], (h, w, 1))


def card_pbr(w, h, rough=0.75):
    return np.dstack([np.ones((h, w)), np.full((h, w), rough), np.zeros((h, w))])


def write_textures():
    """Writes all KTX2 files; returns {material name: {slot: path}}."""
    os.makedirs(TEX_DIR, exist_ok=True)
    path = lambda name, slot: os.path.join(TEX_DIR, f"{PREFIX}_{name}_{slot}.ktx2")
    thickness = os.path.join(TEX_DIR, f"{PREFIX}_Thickness.ktx2")
    T.write_plain(thickness, np.full((4, 4, 3), T.FOLIAGE_THICKNESS))
    slots = {}

    def solid(name, colour, normal, rough):
        T.write_colour(path(name, "Diffuse"), colour)
        T.write_normal(path(name, "Normal"), normal)
        r = rough[::2, ::2]
        T.write_plain(path(name, "PBR"), np.dstack([np.ones_like(r), r, np.zeros_like(r)]))
        slots[name] = {"Diffuse": path(name, "Diffuse"), "Normal": path(name, "Normal"), "AoRoughMetal": path(name, "PBR")}

    def cutout(name, rgba, normal=None):
        alpha = rgba[..., 3]
        colour = T.push_pull_fill(rgba[..., :3], (alpha > 0.5).astype(float))
        T.write_colour(path(name, "Diffuse"), colour)
        T.write_normal(path(name, "Normal"), normal if normal is not None else flat_normal(*rgba.shape[1::-1]))
        T.write_plain(path(name, "PBR"), card_pbr(rgba.shape[1] // 2, rgba.shape[0] // 2))
        T.write_opacity(path(name, "Opacity"), alpha)
        slots[name] = {"Diffuse": path(name, "Diffuse"), "Normal": path(name, "Normal"), "AoRoughMetal": path(name, "PBR"),
                       "Opacity": path(name, "Opacity"), "Thickness": thickness}

    solid("RockGrey", *rock_texture((128, 124, 118), 11, lichen=0.5))
    solid("RockTan", *rock_texture((156, 134, 104), 21))
    solid("Bark", *bark_texture(31))
    cutout("LeavesGreen", leaf_clump((52, 92, 36), (104, 146, 56), 41))
    cutout("LeavesDry", leaf_clump((98, 88, 44), (170, 140, 64), 51))
    a = flower_tile((244, 242, 232), (240, 190, 40), 61, flowers=9)
    b = flower_tile((150, 84, 170), (236, 210, 80), 71, flowers=9)
    cutout("Flowers", np.concatenate([a, b], axis=1))        # two variants side by side
    return slots


# ---------------------------------------------------------------------------------------------- geometry

def icosphere(sub):
    t = (1 + 5 ** 0.5) / 2
    v = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
         (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    f = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
         (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
         (8, 6, 7), (9, 8, 1)]
    verts = [np.array(p, float) / np.linalg.norm(p) for p in v]
    for _ in range(sub):
        cache, faces = {}, []

        def mid(a, b):
            key = (min(a, b), max(a, b))
            if key not in cache:
                m = verts[a] + verts[b]
                verts.append(m / np.linalg.norm(m))
                cache[key] = len(verts) - 1
            return cache[key]

        for a, b, c in f:
            ab, bc, ca = mid(a, b), mid(b, c), mid(c, a)
            faces += [(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]
        f = faces
    return np.array(verts), np.array(f)


def fnoise(d, seed, octaves=3):
    """Smooth noise as a function of a direction (so every LOD of a rock has the same shape)."""
    rng = np.random.default_rng(seed)
    out = np.zeros(len(d))
    for o in range(octaves):
        k = rng.normal(size=(4, 3))
        k /= np.linalg.norm(k, axis=1, keepdims=True)
        freq = 1.7 * 2 ** o
        phase = rng.uniform(0, 2 * math.pi, 4)
        out += (0.5 ** o) * np.sin(freq * (d @ k.T) + phase).sum(1) / 2
    return out / 1.6


def tangents_for(pos, nrm, uv, idx):
    """Per-vertex tangents (xyz + handedness) from the UVs of the triangles; glTF convention (v down, green up)."""
    tan = np.zeros((len(pos), 3))
    bit = np.zeros((len(pos), 3))
    for a, b, c in idx:
        e1, e2 = pos[b] - pos[a], pos[c] - pos[a]
        du1, dv1 = uv[b][0] - uv[a][0], -(uv[b][1] - uv[a][1])
        du2, dv2 = uv[c][0] - uv[a][0], -(uv[c][1] - uv[a][1])
        det = du1 * dv2 - du2 * dv1
        if abs(det) < 1e-12:
            continue
        t = (e1 * dv2 - e2 * dv1) / det
        s = (e2 * du1 - e1 * du2) / det
        for i in (a, b, c):
            tan[i] += t
            bit[i] += s
    tan = tan - nrm * (tan * nrm).sum(1, keepdims=True)
    bad = np.linalg.norm(tan, axis=1) < 1e-9
    tan[bad] = np.cross(nrm[bad], [0.0, 1.0, 0.01])
    tan = T._unit(tan)
    w = np.sign((np.cross(nrm, tan) * bit).sum(1))
    w[w == 0] = 1
    return np.hstack([tan, w[:, None]])


def flat_part(pos, faces, material, uv_scale, smooth=0.35):
    """Faceted part: every triangle has its own vertices, normals mostly the face normal, planar UVs per face."""
    fn = np.cross(pos[faces[:, 1]] - pos[faces[:, 0]], pos[faces[:, 2]] - pos[faces[:, 0]])
    fn = T._unit(fn)
    vn = np.zeros_like(pos)
    for k in range(3):
        np.add.at(vn, faces[:, k], fn)
    vn = T._unit(vn)
    P, N, UV = [], [], []
    for i, f in enumerate(faces):
        axis = int(np.argmax(np.abs(fn[i])))
        keep = [a for a in range(3) if a != axis]
        for v in f:
            P.append(pos[v])
            N.append(fn[i] * (1 - smooth) + vn[v] * smooth)
            UV.append((pos[v][keep[0]] * uv_scale + 0.13 * axis, pos[v][keep[1]] * uv_scale + 0.31 * axis))
    P, N, UV = np.array(P), T._unit(np.array(N)), np.array(UV)
    idx = np.arange(len(P)).reshape(-1, 3)
    return T.Part(P, N, tangents_for(P, N, UV, idx), UV, idx, material)


def rock_part(sub, seed, half, rough, material, sink=0.16):
    d, faces = icosphere(sub)
    r = 1 + rough * fnoise(d, seed)
    p = d * r[:, None] * np.array(half)
    p[:, 1] = np.maximum(p[:, 1], -0.55 * half[1])               # flat bottom
    height = p[:, 1].max() - p[:, 1].min()
    p[:, 1] -= p[:, 1].min() + sink * height                      # lowest point sinks into the ground
    return flat_part(p, faces, material, 1 / 0.5), height


def pebble_part(sub, seed, count, radius, material):
    rng = np.random.default_rng(seed)
    pos, faces, offset = [], [], 0
    d, f = icosphere(sub)
    placed = []
    while len(placed) < count:
        c = rng.uniform(-1, 1, 2) * radius
        s = rng.uniform(0.03, 0.085)
        if np.hypot(*c) > radius or any(np.hypot(*(c - o)) < 0.8 * (s + so) for o, so in placed):
            continue
        placed.append((c, s))
    for k, (c, s) in enumerate(placed):
        r = 1 + 0.3 * fnoise(d, seed + k)
        sc = np.array([s * rng.uniform(1.0, 1.4), s * rng.uniform(0.45, 0.7), s * rng.uniform(0.9, 1.3)])
        p = d * r[:, None] * sc
        p[:, 1] -= p[:, 1].min() + 0.3 * (p[:, 1].max() - p[:, 1].min())
        p[:, 0] += c[0]
        p[:, 2] += c[1]
        pos.append(p)
        faces.append(f + offset)
        offset += len(p)
    return flat_part(np.vstack(pos), np.vstack(faces), material, 1 / 0.2, smooth=0.5)


def tube(path, radii, segments, material, u_repeat, v_len, jitter=0.0, seed=0, cap_uv=None):
    """Tube along a polyline (bark texture: furrows along the path), closed with fans using the sawn-end texels."""
    rng = np.random.default_rng(seed)
    path = np.asarray(path, float)
    n = len(path)
    P, N, UV, I = [], [], [], []
    for i in range(n):
        t = path[min(i + 1, n - 1)] - path[max(i - 1, 0)]
        t /= np.linalg.norm(t)
        a = np.cross(t, [0.0, 1.0, 0.0])
        if np.linalg.norm(a) < 1e-6:
            a = np.cross(t, [1.0, 0.0, 0.0])
        a /= np.linalg.norm(a)
        b = np.cross(t, a)
        along = np.linalg.norm(path[i] - path[0]) if i else 0.0
        ring_r = radii[i] * (1 + jitter * rng.uniform(-1, 1, segments))
        for k in range(segments + 1):
            ang = 2 * math.pi * (k % segments) / segments
            nrm = math.cos(ang) * a + math.sin(ang) * b
            P.append(path[i] + nrm * ring_r[k % segments])
            N.append(nrm)
            UV.append((0.74 * u_repeat * k / segments, along / v_len))
    for i in range(n - 1):
        for k in range(segments):
            a0, a1 = i * (segments + 1) + k, i * (segments + 1) + k + 1
            b0, b1 = a0 + segments + 1, a1 + segments + 1
            I += [(a0, b0, a1), (a1, b0, b1)]
    for end, sign in ((0, -1), (n - 1, 1)):
        t = (path[1] - path[0]) if end == 0 else (path[-1] - path[-2])
        t /= np.linalg.norm(t)
        a = np.cross(t, [0.0, 1.0, 0.0])
        if np.linalg.norm(a) < 1e-6:
            a = np.cross(t, [1.0, 0.0, 0.0])
        a /= np.linalg.norm(a)
        b = np.cross(t, a)
        base = len(P)
        P.append(path[end])
        N.append(t * sign)
        UV.append(cap_uv)
        for k in range(segments):
            ang = 2 * math.pi * k / segments
            P.append(path[end] + (math.cos(ang) * a + math.sin(ang) * b) * radii[end] * 0.98)
            N.append(t * sign)
            UV.append((cap_uv[0] + 0.5 * 56 / 256 * math.cos(ang) * 0.96, cap_uv[1] + 0.5 * 56 / 256 * math.sin(ang) * 0.96))
        for k in range(segments):
            k2 = (k + 1) % segments
            I.append((base, base + 1 + k, base + 1 + k2) if sign > 0 else (base, base + 1 + k2, base + 1 + k))
    P, N, UV, I = np.array(P), T._unit(np.array(N)), np.array(UV), np.array(I)
    # Triangles must face outward: flip any whose geometric normal opposes the vertex normals.
    fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]])
    flip = (fn * N[I].mean(1)).sum(1) < 0
    I[flip] = I[flip][:, ::-1]
    return T.Part(P, N, tangents_for(P, N, UV, I), UV, I, material)


def merge(parts, material):
    off = np.cumsum([0] + [len(p.pos) for p in parts])
    return T.Part(np.vstack([p.pos for p in parts]), np.vstack([p.nrm for p in parts]), np.vstack([p.tan for p in parts]),
                  np.vstack([p.uv for p in parts]), np.vstack([p.idx + o for p, o in zip(parts, off[:-1])]), material)


def log_parts(segments, rings, seed, with_stubs):
    rng = np.random.default_rng(seed)
    length, r0, r1 = 2.4, 0.15, 0.11
    xs = np.linspace(-length / 2, length / 2, rings)
    bend = 0.05 * np.sin(np.linspace(0, math.pi, rings))
    radii = np.linspace(r0, r1, rings)
    y0 = r0 * 0.85                                              # sunk about 15 % of its thickness
    path = np.stack([xs, y0 + bend * 0.5 + 0.0 * xs, 0.04 * np.sin(np.linspace(0, 2.2, rings))], axis=1)
    cap = (0.78 + 0.5 * 56 / 256, 0.02 + 0.5 * 56 / 256)
    parts = [tube(path, radii, segments, "Bark", 1, 1.0, jitter=0.05, seed=seed, cap_uv=cap)]
    if with_stubs:
        for x, ang in ((-0.35, 0.9), (0.45, -1.5)):
            i = int(np.argmin(np.abs(xs - x)))
            base = path[i] + [0, 0, 0]
            d = np.array([0.25, math.cos(ang), math.sin(ang)])
            d /= np.linalg.norm(d)
            stub = [base + d * radii[i] * 0.6, base + d * 0.3]
            parts.append(tube(stub, [0.03, 0.02], 5, "Bark", 1, 0.6, cap_uv=cap))
    return merge(parts, "Bark"), length


def card(centre, up, normal, width, height, uv_rect, tilt=0.0):
    """A vertical-ish quad centred at centre; up = image up direction; returns (pos, uv, nrm, tan)."""
    up = up / np.linalg.norm(up)
    side = np.cross(up, normal)
    side /= np.linalg.norm(side)
    n = np.cross(side, up)
    pos = np.array([centre - side * width / 2 - up * height / 2, centre + side * width / 2 - up * height / 2,
                    centre + side * width / 2 + up * height / 2, centre - side * width / 2 + up * height / 2])
    u0, v0, u1, v1 = uv_rect
    uv = np.array([[u0, v1], [u1, v1], [u1, v0], [u0, v0]])
    return pos, uv, n, side


def cards_part(cards, material, normals):
    P, UV, N, TN, I = [], [], [], [], []
    for i, (pos, uv, n, side) in enumerate(cards):
        P.append(pos)
        UV.append(uv)
        N.append(np.tile(n, (4, 1)))
        TN.append(np.tile(np.append(side, 1.0), (4, 1)))
        I.append(np.array([[0, 1, 2], [0, 2, 3]]) + 4 * i)
    P, UV, N, TN, I = np.vstack(P), np.vstack(UV), np.vstack(N), np.vstack(TN), np.vstack(I)
    N = normals(P, N)
    return T.Part(P, N, TN, UV, I, material)


def bush_part(count, size, seed, material, tile):
    rng = np.random.default_rng(seed)
    centre = np.array([0.0, 0.5, 0.0])
    radii = np.array([0.55, 0.5, 0.55])
    cards = []
    for _ in range(count):
        while True:
            q = rng.uniform(-1, 1, 3)
            if np.linalg.norm(q) <= 1:
                break
        c = centre + q * radii
        c[1] = max(c[1], 0.12)
        out = c - centre
        out /= np.linalg.norm(out) + 1e-9
        normal = out + rng.normal(0, 0.5, 3)
        up = rng.normal(0, 0.6, 3) + [0, 1.0, 0]
        normal -= up * (normal @ up) / (up @ up)
        if np.linalg.norm(normal) < 1e-6:
            normal = np.array([1.0, 0, 0])
        normal /= np.linalg.norm(normal)
        cards.append(card(c, up, normal, size * rng.uniform(0.85, 1.15), size * rng.uniform(0.85, 1.15), tile))

    def normals(P, N):
        return T.foliage_normals(P, N, 1.1)

    return cards_part(cards, material, normals)


def flower_part(variant, seed, groups=3):
    rng = np.random.default_rng(seed)
    third = 1 / 2
    rect = (variant * third + 0.002, 0.0, (variant + 1) * third - 0.002, 1.0)
    cards = []
    for g in range(groups):
        offset = np.array([rng.uniform(-0.14, 0.14), 0, rng.uniform(-0.14, 0.14)]) if g else np.zeros(3)
        yaw = rng.uniform(0, math.pi)
        h = rng.uniform(0.42, 0.55)
        for k in range(3):
            a = yaw + k * math.pi / 3
            normal = np.array([-math.sin(a), 0, math.cos(a)])
            cards.append(card(offset + [0, h / 2 - 0.02, 0], np.array([0, 1.0, 0]), normal, h * 1.15, h, rect))
    return cards_part(cards, "Flowers", lambda P, N: np.tile([0.0, 1.0, 0.0], (len(P), 1)) * 0.8 + N * 0.2)


# ---------------------------------------------------------------------------------------------- objects

def sphere_collider(name, y, radius):
    return [f'      <Sphere Id="{name}">', f'        <LocationAsmb X="0" Y="{y:.3f}" Z="0" />',
            '        <Collider2Asmb X="0" Y="0" Z="0" />', f'        <Radius M="{radius:.3f}" />', "      </Sphere>"]


def capsule_collider(name, centre, length, radius, along_x):
    return [f'      <Capsule Id="{name}">', f'        <LocationAsmb X="0" Y="{centre:.3f}" Z="0" />',
            f'        <Collider2Asmb X="0" Y="0" Z="{1.5707963 if along_x else 0}" />',
            f'        <LengthY M="{length:.3f}" />', f'        <Radius M="{radius:.3f}" />', "      </Capsule>"]


def build_objects():
    """[(object id, [5 LOD part lists], collider xml lines, substance, volume m3)]"""
    objs = []
    for n, (mat, half, rough, seed) in enumerate([("RockGrey", (0.28, 0.19, 0.25), 0.28, 101),
                                                   ("RockTan", (0.2, 0.12, 0.17), 0.3, 202),
                                                   ("RockGrey", (0.45, 0.3, 0.4), 0.22, 303)]):
        lods = [rock_part(s, seed, half, rough, mat)[0] for s in (2, 2, 1, 1, 0)]
        height = rock_part(2, seed, half, rough, mat)[1]
        objs.append((f"{PREFIX}_Rock_{n}", [[p] for p in lods], sphere_collider("Rock", 0.42 * height, 0.62 * min(half[0], half[2])),
                     "Rock.Granodiorite", 4.19 * half[0] * half[1] * half[2]))
    pebbles = [pebble_part(s, 404, 11, 0.42, "RockGrey") for s in (1, 1, 0, 0, 0)]
    objs.append((f"{PREFIX}_Pebbles_0", [[p] for p in pebbles], sphere_collider("Pebbles", 0.03, 0.05), "Rock.Granodiorite", 0.003))
    for n, (mat, tile, seed) in enumerate([("LeavesGreen", (0.0, 0.0, 1.0, 1.0), 505), ("LeavesDry", (0.0, 0.0, 1.0, 1.0), 606)]):
        lods = [bush_part(c, s, seed, mat, tile) for c, s in ((46, 0.34), (46, 0.34), (24, 0.46), (12, 0.62), (12, 0.62))]
        objs.append((f"{PREFIX}_Bush_{n}", [[p] for p in lods], sphere_collider("Bush", 0.45, 0.4), "Wood.OakWhite", 0.15))
    for n in range(2):
        part = flower_part(n, 700 + n)
        objs.append((f"{PREFIX}_Flowers_{n}", [[part]] * 5, sphere_collider("Flowers", 0.03, 0.04), "Wood.OakWhite", 0.002))
    near, length = log_parts(10, 12, 800, True)
    mid, _ = log_parts(8, 6, 800, False)
    far, _ = log_parts(5, 3, 800, False)
    objs.append((f"{PREFIX}_Log_0", [[near], [near], [mid], [far], [far]],
                 capsule_collider("Log", 0.13, length - 0.28, 0.13, True), "Wood.OakWhite", 0.13))
    return objs


def object_xml(object_id, lods, colliders, substance, volume):
    lines = [f'  <ClutterObject Id="{object_id}" Atlas="{PREFIX}" AngularDamping="0.5">', "    <LODs>"]
    for k, (size, parts) in enumerate(zip(LOD_SCREEN_SIZES, lods)):
        lines.append(f'      <LOD MinScreenSize="{size}">')
        lines.append(f'        <Mesh Id="{object_id}_Lod{k}" />')
        for m in sorted({p.material for p in parts}, key=MATERIAL_INDEX.get):
            lines.append(f'        <Material Id="{PREFIX}_{m}" />')
        lines.append("      </LOD>")
    lines += ["    </LODs>", "    <Colliders>"] + colliders + ["    </Colliders>", f'    <Substance Id="{substance}">',
                                                               f'      <Volume M3="{volume:.3f}" />', "    </Substance>", "  </ClutterObject>"]
    return lines


def main():
    # Point tree_assets' glb writer and material writer at our material table.
    T.PREFIX, T.MATERIALS, T.MATERIAL_INDEX = PREFIX, MATERIALS, MATERIAL_INDEX
    os.makedirs(os.path.join(MOD_DIR, "Meshes"), exist_ok=True)
    objects = build_objects()
    slots = write_textures()
    meshes, xml_objects = [], []
    for object_id, lods, colliders, substance, volume in objects:
        for k, parts in enumerate(lods):
            meshes.append((f"{object_id}_Lod{k}", parts))
        xml_objects.append(object_xml(object_id, lods, colliders, substance, volume))
        print(f"{object_id}: " + "/".join(str(sum(len(p.idx) for p in parts)) for parts in lods) + " triangles per LOD")
    xml = ['<?xml version="1.0" encoding="utf-8"?>',
           "<!-- Generated by src/tools/scatter_assets.py (procedural, no third-party assets). -->",
           "<Assets>", f'  <MeshAtlas Id="{PREFIX}" Path="Meshes/{PREFIX}.glb" HostOnly="true" />']
    for name, _, foliage in MATERIALS:
        lines = T.material_xml(name, slots[name], foliage)
        if name == "Flowers":
            lines.insert(-1, '    <CastShadows Value="false" />')
        xml += lines
    for lines in xml_objects:
        xml += lines
    xml.append("</Assets>")
    # Check before writing: every mesh the xml names is in the glb.
    names = {n for n, _ in meshes}
    for lines in xml_objects:
        for line in lines:
            if "<Mesh Id=" in line:
                assert line.split('"')[1] in names, line
    T.write_glb(os.path.join(MOD_DIR, "Meshes", f"{PREFIX}.glb"), meshes)
    with open(os.path.join(MOD_DIR, "Scatter.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(xml) + "\n")
    print(f"wrote Scatter.xml, {len(objects)} clutter objects, {len(meshes)} meshes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
