"""Builds the forest tree pack assets of GEE (Trees.xml, Meshes/GEE_Forest.glb, Textures/GEE_Forest_*.ktx2).

Source: "Low Poly Forest Tree Pack" by 99.Miles, CC BY 4.0,
https://sketchfab.com/3d-models/low-poly-forest-tree-pack-5ff5a51e74324845a4e4905f182dfb2b
Sketchfab needs a login to download; get the glTF version and pass the zip:

    python tree_assets.py path/to/low_poly_forest_tree_pack.zip

The trees become ground clutter objects named GEE_Forest_<Ecotype>_<n>; TreePack.cs appends them to Earth's
ecotypes of that name, so the game places, fades, shadows and collides them like its own trees. A clutter object
needs exactly 5 LODs: the model itself for the near three, and for trees three crossed cards rendered from the
model (like the game's own Lod3_Cards) for the far two, where a forest has hundreds of thousands of instances.
"""
import json
import os
import struct
import sys
import tempfile
import zipfile
import zlib

import numpy as np
from PIL import Image

MOD_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PREFIX = "GEE_Forest"

# (ecotype, glTF node names, target height in m or None to keep the pack's size). The pack is modelled in metres.
# Tree: the game's own are ~30 m (x0.67..1.6); SmallTree ~15-18 m; Shrub ~4 m (x1..2.3). The reed tuft and the
# rocks of the pack are left out.
MODELS = [
    ("Tree", ["Background_Tree_Atlas"], None),            # spruce, 26 m
    ("Tree", ["Background_Tree_Atlas.001"], None),        # autumn poplar, 29 m
    ("Tree", ["Background_Tree_Atlas.003"], None),        # spruce, 26 m
    ("Tree", ["Background_Tree_Atlas.004"], None),        # weeping birch, 24.5 m
    ("Tree", ["Background_Tree_Atlas.005"], None),        # pine, 33 m
    ("Tree", ["Background_Tree_Atlas.006"], None),        # thin spruce, 26 m
    ("Tree", ["Background_Tree_Atlas.007"], None),        # pine, 26 m
    ("Tree", ["Tree_Trunk_01.001", "Tree_Branches_01"], None),        # detailed spruce, 26 m
    ("SmallTree", ["Background_Tree_Atlas.002"], None),               # spruce, 18 m
    ("SmallTree", ["Tree_Trunk_01.002", "Tree_Branches_01.002"], None),  # detailed spruce, 18.5 m
    ("SmallTree", ["Tree_Trunk_01", "Tree_Branches_01.001"], None),   # detailed spruce, 11 m
    ("SmallTree", ["Tree_Trunk_02", "Tree_Branches_02"], None),       # detailed autumn birch, 12 m
    ("Shrub", ["Background_Tree_Atlas.009"], 3.5),        # young fir
    ("Shrub", ["Background_Tree_Atlas.010"], None),       # round bush, 2.2 m
    ("Shrub", ["Background_Tree_Atlas.011"], None),       # small fir, 2 m
    ("Shrub", ["Background_Tree_Atlas.012"], None),       # open shrub, 2.6 m
]
CARD_ECOTYPES = {"Tree", "SmallTree"}
# The game's LOD switches (EarthTreesAssets.xml).
LOD_SCREEN_SIZES = [512, 316, 256, 96, 0.001]
# Stock trees: Wood.OakWhite, about this much wood per m of height^3 (TreeType1: 7.24 m3 at 31 m).
WOOD_PER_M3 = 7.24 / 31 ** 3
# Trunk bottoms go this far under the ground so trees on slopes do not stand on air.
SINK = {"Tree": 0.8, "SmallTree": 0.6, "Shrub": 0.3}

# Atlas materials, in this order (a LOD lists its materials in ascending atlas order).
# (name, source material, foliage)
MATERIALS = [
    ("Atlas", "Background_Tree_Atlas", True),
    ("Branches1", "Tree_Branches_01", True),
    ("Branches2", "Tree_Branches_02", True),
    ("Trunk1", "Tree_Trunk_01", False),
    ("Trunk2", "Tree_Trunk_02", False),
    ("Cards", None, True),
]
MATERIAL_INDEX = {name: i for i, (name, _, _) in enumerate(MATERIALS)}
SOURCE_TO_MATERIAL = {src: name for name, src, _ in MATERIALS if src}
FOLIAGE_THICKNESS = 0.45          # Thickness R: 1 - this is the leaves' translucency
CARD_TILE = (128, 256)            # far LODs only show below 96 px
CARD_COLUMNS, CARD_ROWS = 16, 4
CARD_SUPERSAMPLE = 4


# ---------------------------------------------------------------------------------------------- glTF input

_CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
_NC = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def _node_matrix(n):
    if "matrix" in n:
        return np.array(n["matrix"], dtype=np.float64).reshape(4, 4).T
    m = np.eye(4)
    if "scale" in n:
        m = np.diag(list(n["scale"]) + [1.0]) @ m
    if "rotation" in n:
        x, y, z, w = n["rotation"]
        r = np.eye(4)
        r[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
        m = r @ m
    if "translation" in n:
        t = np.eye(4)
        t[:3, 3] = n["translation"]
        m = t @ m
    return m


def load_gltf(path):
    """World-space primitives by node name: {name: [dict(pos, nrm, tan, uv, idx, material)]}, plus the JSON."""
    j = json.load(open(path))
    base = os.path.dirname(path)
    bufs = [open(os.path.join(base, b["uri"]), "rb").read() for b in j["buffers"]]

    def accessor(i):
        a = j["accessors"][i]
        bv = j["bufferViews"][a["bufferView"]]
        n, dt = _NC[a["type"]], np.dtype(_CT[a["componentType"]])
        stride = bv.get("byteStride", 0) or n * dt.itemsize
        off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
        raw = np.frombuffer(bufs[bv["buffer"]], np.uint8, stride * (a["count"] - 1) + n * dt.itemsize, off)
        out = np.lib.stride_tricks.as_strided(raw, (a["count"], n * dt.itemsize), (stride, 1)).copy()
        out = out.view(dt).reshape(a["count"], n)
        return out if n > 1 else out[:, 0]

    result = {}

    def walk(ni, parent, owner):
        n = j["nodes"][ni]
        world = parent @ _node_matrix(n)
        if "mesh" in n:
            normal_matrix = np.linalg.inv(world[:3, :3]).T
            for p in j["meshes"][n["mesh"]]["primitives"]:
                at = p["attributes"]
                pos = accessor(at["POSITION"]).astype(np.float64) @ world[:3, :3].T + world[:3, 3]
                nrm = accessor(at["NORMAL"]).astype(np.float64) @ normal_matrix.T
                tan = accessor(at["TANGENT"]).astype(np.float64)
                tan[:, :3] = tan[:, :3] @ world[:3, :3].T
                result.setdefault(owner, []).append(dict(
                    pos=pos, nrm=_unit(nrm), tan=np.hstack([_unit(tan[:, :3]), np.sign(tan[:, 3:4]) + (tan[:, 3:4] == 0)]),
                    uv=accessor(at["TEXCOORD_0"]).astype(np.float64),
                    idx=accessor(p["indices"]).astype(np.int64).reshape(-1, 3),
                    source=j["materials"][p["material"]]["name"]))
        for c in n.get("children", []):
            walk(c, world, n.get("name") if "mesh" not in n else owner)

    for root in j["scenes"][j.get("scene", 0)]["nodes"]:
        walk(root, np.eye(4), None)
    return j, result


def _unit(v):
    return v / (np.linalg.norm(v, axis=-1, keepdims=True) + 1e-12)


# ---------------------------------------------------------------------------------------------- textures

def push_pull_fill(rgb, weight):
    """Colours under transparent texels from their opaque neighbours, so mips and filtering show no dark fringe."""
    if min(rgb.shape[:2]) <= 1:
        w = weight.sum()
        mean = (rgb * weight[..., None]).sum((0, 1)) / w if w > 0 else rgb.mean((0, 1))
        return np.broadcast_to(mean, rgb.shape).copy()
    h, w = rgb.shape[:2]
    h2, w2 = (h + 1) // 2, (w + 1) // 2
    pad_rgb = np.zeros((h2 * 2, w2 * 2, 3))
    pad_w = np.zeros((h2 * 2, w2 * 2))
    pad_rgb[:h, :w] = rgb * weight[..., None]
    pad_w[:h, :w] = weight
    sum_rgb = pad_rgb.reshape(h2, 2, w2, 2, 3).sum((1, 3))
    sum_w = pad_w.reshape(h2, 2, w2, 2).sum((1, 3))
    small = np.where(sum_w[..., None] > 0, sum_rgb / np.maximum(sum_w, 1e-9)[..., None], 0)
    small = push_pull_fill(small, np.minimum(sum_w, 1))
    up = small.repeat(2, 0).repeat(2, 1)[:h, :w]
    return rgb * weight[..., None] + up * (1 - weight[..., None])


def box_levels(array):
    """Mip chain of a float HxWxC array (box filter), down to 1x1."""
    levels = [array]
    while levels[-1].shape[0] > 1 or levels[-1].shape[1] > 1:
        a = levels[-1]
        h, w = a.shape[:2]
        h2, w2 = max(h // 2, 1), max(w // 2, 1)
        a = a[:h2 * (h // h2), :w2 * (w // w2)]
        levels.append(a.reshape(h2, h // h2, w2, w // w2, -1).mean((1, 3)))
    return levels


def coverage_scaled(alpha, target):
    """alpha scaled so that the share of texels above the 0.5 cutoff matches target (keeps far foliage dense)."""
    if target <= 0:
        return alpha
    lo, hi = 0.0, 64.0
    for _ in range(30):
        s = (lo + hi) / 2
        if (alpha * s > 0.5).mean() < target:
            lo = s
        else:
            hi = s
    return np.clip(alpha * hi, 0, 1)


def to_u8(a):
    return (np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)


def write_ktx2(path, levels):
    """RGBA8 UNORM KTX2 with the given mip levels (float HxWx4, 0..1), zlib supercompressed (see landmark_assets)."""
    raw = [to_u8(level).tobytes() for level in levels]
    data = [zlib.compress(r, 9) for r in raw]
    n = len(levels)
    samples = b"".join(struct.pack("<IIII", (8 * i) | (7 << 16) | (channel << 24), 0, 0, 255)
                       for i, channel in enumerate((0, 1, 2, 15)))
    block = struct.pack("<IIIIII", 0, 2 | ((24 + len(samples)) << 16), 1 | (1 << 8) | (1 << 16), 0, 0, 0) + samples
    dfd = struct.pack("<I", 4 + len(block)) + block
    dfd_offset = 80 + 24 * n
    offset = dfd_offset + len(dfd)
    level_index, chunks = [None] * n, []
    for i in reversed(range(n)):
        chunks.append(data[i])
        level_index[i] = struct.pack("<QQQ", offset, len(data[i]), len(raw[i]))
        offset += len(data[i])
    h, w = levels[0].shape[:2]
    header = bytes([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A])
    header += struct.pack("<9I", 37, 1, w, h, 0, 0, 1, n, 3)
    header += struct.pack("<IIIIQQ", dfd_offset, len(dfd), 0, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(header + b"".join(level_index) + dfd + b"".join(chunks))


def rgba(rgb, a=None):
    a = np.ones(rgb.shape[:2]) if a is None else a
    return np.dstack([rgb, a])


def write_colour(path, rgb):
    """Diffuse: alpha 0 makes the game's clutter shader take the colour as sRGB as it is (no terrain tint)."""
    write_ktx2(path, [rgba(level, np.zeros(level.shape[:2])) for level in box_levels(rgb)])


def write_normal(path, rgb):
    levels = []
    for level in box_levels(rgb * 2 - 1):
        levels.append(rgba(_unit(level) * 0.5 + 0.5))
    write_ktx2(path, levels)


def write_plain(path, rgb):
    write_ktx2(path, [rgba(level) for level in box_levels(rgb)])


def write_opacity(path, alpha, coverage=None):
    """Opacity (R, cut at 0.5) with every mip scaled to the coverage of the full size."""
    target = (alpha > 0.5).mean() if coverage is None else coverage
    levels = []
    for level in box_levels(alpha[..., None]):
        a = coverage_scaled(level[..., 0], target)
        levels.append(np.dstack([a, a, a, np.ones_like(a)]))
    write_ktx2(path, levels)


def load_image(path, mode="RGBA", size=None):
    im = Image.open(path).convert(mode)
    if size and im.size != size:
        im = im.resize(size, Image.LANCZOS)
    return np.asarray(im).astype(np.float64) / 255


def material_textures(j, pack_dir, out_dir, write):
    """Writes the pack material textures; returns {material name: {slot: texture file}}."""
    by_name = {m["name"]: m for m in j["materials"]}
    files = {}

    def image_path(info):
        return os.path.join(pack_dir, j["images"][j["textures"][info["index"]]["source"]]["uri"])

    for name, source, foliage in MATERIALS:
        if source is None:
            continue
        m = by_name[source]
        pbr = m["pbrMetallicRoughness"]
        diffuse = load_image(image_path(pbr["baseColorTexture"]))
        size = (diffuse.shape[1], diffuse.shape[0])
        if "metallicRoughnessTexture" in pbr:
            rough = load_image(image_path(pbr["metallicRoughnessTexture"]), "RGB", size)[..., 1]
        else:
            rough = np.full(diffuse.shape[:2], pbr.get("roughnessFactor", 1.0))
        rough = np.clip(rough * pbr.get("roughnessFactor", 1.0), 0.35, 1.0)
        normal = load_image(image_path(m["normalTexture"]), "RGB")
        if normal.shape[0] > 1024:
            normal = load_image(image_path(m["normalTexture"]), "RGB", (1024, 1024))
        slots = {}
        tex = lambda slot: os.path.join(out_dir, f"{PREFIX}_{name}_{slot}.ktx2")
        alpha = diffuse[..., 3] if foliage else np.ones(diffuse.shape[:2])
        if write:
            write_colour(tex("Diffuse"), push_pull_fill(diffuse[..., :3], (alpha > 0.5).astype(float)))
            write_normal(tex("Normal"), normal)
            pbr_size = (max(size[0] // 2, 4), max(size[1] // 2, 4))
            r = np.asarray(Image.fromarray(to_u8(rough)).resize(pbr_size, Image.BOX)).astype(float) / 255
            write_plain(tex("PBR"), np.dstack([np.ones_like(r), r, np.zeros_like(r)]))
            if foliage:
                write_opacity(tex("Opacity"), alpha)
        slots["Diffuse"], slots["Normal"], slots["AoRoughMetal"] = tex("Diffuse"), tex("Normal"), tex("PBR")
        if foliage:
            slots["Opacity"], slots["Thickness"] = tex("Opacity"), os.path.join(out_dir, f"{PREFIX}_Thickness.ktx2")
        files[name] = dict(slots=slots, diffuse=diffuse, foliage=foliage)
    if write:
        write_plain(os.path.join(out_dir, f"{PREFIX}_Thickness.ktx2"), np.full((4, 4, 3), FOLIAGE_THICKNESS))
    return files


# ---------------------------------------------------------------------------------------------- models

class Part:
    """One primitive of a model in model space (Y up, trunk base at the origin)."""

    def __init__(self, pos, nrm, tan, uv, idx, material):
        self.pos, self.nrm, self.tan, self.uv, self.idx, self.material = pos, nrm, tan, uv, idx, material


def build_model(prims, ecotype, target_height):
    allpos = np.concatenate([p["pos"] for p in prims])
    ground = allpos[:, 1].min()
    trunk = [p for p in prims if p["source"].startswith("Tree_Trunk")] or prims
    trunk_pos = np.concatenate([p["pos"] for p in trunk])
    low = trunk_pos[trunk_pos[:, 1] < ground + 1.0]
    centre = np.array([low[:, 0].mean(), ground, low[:, 2].mean()])
    height = allpos[:, 1].max() - ground
    scale = target_height / height if target_height else 1.0
    sink = SINK[ecotype] * min(1.0, scale * height / 10)
    parts = []
    for p in prims:
        pos = (p["pos"] - centre) * scale
        pos[:, 1] = np.where(pos[:, 1] < 0.05 * scale, -sink, pos[:, 1])
        material = SOURCE_TO_MATERIAL[p["source"]]
        nrm = p["nrm"]
        if MATERIALS[MATERIAL_INDEX[material]][2]:
            nrm = foliage_normals(pos, nrm, scale * height)
        parts.append(Part(pos, nrm, p["tan"], p["uv"], p["idx"], material))
    return parts, scale * height


def foliage_normals(pos, nrm, height):
    """Crown-shaped normals (UsesCustomNormals): leaf cards lit as one rounded crown, not as single planes."""
    # Away from the trunk axis, tilted toward the sky near the top and a little down at the foot. The background
    # trees share one material for trunk and leaves, so the trunk takes these too: they must stay near horizontal.
    out = pos.copy()
    out[:, 1] = 0
    out = _unit(out)
    out[:, 1] = np.clip(1.2 * (pos[:, 1] / height - 0.45), -0.15, 0.6)
    return _unit(0.35 * nrm + 0.65 * _unit(out))


def trunk_capsule(parts, height):
    pos = np.concatenate([p.pos for p in parts])
    band = pos[(pos[:, 1] > 0.15) & (pos[:, 1] < max(1.3, 0.06 * height))]
    r = np.percentile(np.hypot(band[:, 0], band[:, 2]), 70) if len(band) else 0.2
    r = float(np.clip(r, 0.08, 0.8))
    top = 0.6 * height
    return dict(radius=r, length=max(top - 2 * r, 0.1), centre=top / 2)


# ---------------------------------------------------------------------------------------------- far LOD cards

def rasterize(parts, textures, axis_u, axis_n, bounds, size):
    """Orthographic view of the model along -axis_n: albedo, coverage, model-space normal and depth."""
    (umin, umax), (ymin, ymax) = bounds
    w, h = size
    colour = np.zeros((h, w, 3))
    normal = np.zeros((h, w, 3))
    depth = np.full((h, w), -np.inf)
    for part in parts:
        tex = textures[part.material]["diffuse"]
        th, tw = tex.shape[:2]
        foliage = textures[part.material]["foliage"]
        px = (part.pos @ axis_u - umin) / (umax - umin) * w
        py = (ymax - part.pos[:, 1]) / (ymax - ymin) * h
        pz = part.pos @ axis_n
        for a, b, c in part.idx:
            xs, ys = px[[a, b, c]], py[[a, b, c]]
            x0, x1 = max(0, int(xs.min())), min(w - 1, int(np.ceil(xs.max())))
            y0, y1 = max(0, int(ys.min())), min(h - 1, int(np.ceil(ys.max())))
            if x1 < x0 or y1 < y0:
                continue
            d = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
            if abs(d) < 1e-12:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            w0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / d
            w1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / d
            w2 = 1 - w0 - w1
            inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not inside.any():
                continue
            uv = w0[..., None] * part.uv[a] + w1[..., None] * part.uv[b] + w2[..., None] * part.uv[c]
            texel = tex[((uv[..., 1] % 1) * (th - 1)).astype(int), ((uv[..., 0] % 1) * (tw - 1)).astype(int)]
            if foliage:
                inside &= texel[..., 3] > 0.5
            z = w0 * pz[a] + w1 * pz[b] + w2 * pz[c]
            sub = depth[y0:y1 + 1, x0:x1 + 1]
            inside &= z > sub
            if not inside.any():
                continue
            n = w0[..., None] * part.nrm[a] + w1[..., None] * part.nrm[b] + w2[..., None] * part.nrm[c]
            sub[inside] = z[inside]
            colour[y0:y1 + 1, x0:x1 + 1][inside] = texel[..., :3][inside]
            normal[y0:y1 + 1, x0:x1 + 1][inside] = n[inside]
    return colour, np.isfinite(depth), _unit(normal), depth


def card_views(parts, height, slot_start, atlas):
    """Three crossed vertical cards through the trunk axis; renders their views into the atlas slots."""
    pos = np.concatenate([p.pos for p in parts])
    ymin, ymax = pos[:, 1].min(), pos[:, 1].max()
    reach = np.hypot(pos[:, 0], pos[:, 2]).max()
    tw, th = CARD_TILE
    ss = CARD_SUPERSAMPLE
    quads = []
    for k in range(3):
        angle = k * np.pi / 3
        axis_u = np.array([np.cos(angle), 0, np.sin(angle)])
        axis_n = np.array([-np.sin(angle), 0, np.cos(angle)])
        proj = pos @ axis_u
        umin, umax = proj.min() - 0.05 * reach, proj.max() + 0.05 * reach
        colour, mask, normal, depth = rasterize(parts, atlas["textures"], axis_u, axis_n,
                                                ((umin, umax), (ymin, ymax)), (tw * ss, th * ss))
        # Tangent space of the card: x along axis_u, y up (glTF: green = up in the image), z toward the viewer.
        n_ts = np.dstack([normal @ axis_u, normal[..., 1], normal @ axis_n])
        n_ts[~mask] = (0, 0, 1)
        # Inner crown darker: depth behind the trunk axis, as seen from the card's front.
        ao = np.where(mask, np.clip(0.8 + 0.35 * depth / max(reach, 1e-3), 0.45, 1.0), 1.0)
        fmask = mask.astype(float)
        down = lambda a: a.reshape(th, ss, tw, ss, -1).mean((1, 3))
        cov = down(fmask[..., None])[..., 0]
        col = down(colour * fmask[..., None]) / np.maximum(cov[..., None], 1e-9)
        nrm = down(n_ts * fmask[..., None]) + (1 - cov[..., None]) * np.array([0, 0, 1])
        aod = down(ao[..., None])[..., 0]
        slot = slot_start + k
        sx, sy = (slot % CARD_COLUMNS) * tw, (slot // CARD_COLUMNS) * th
        atlas["colour"][sy:sy + th, sx:sx + tw] = col
        atlas["alpha"][sy:sy + th, sx:sx + tw] = cov
        atlas["normal"][sy:sy + th, sx:sx + tw] = _unit(nrm) * 0.5 + 0.5
        atlas["ao"][sy:sy + th, sx:sx + tw] = aod
        W, H = CARD_COLUMNS * tw, CARD_ROWS * th
        u0, u1 = (sx + 0.5) / W, (sx + tw - 0.5) / W
        v0, v1 = (sy + 0.5) / H, (sy + th - 0.5) / H
        corners = np.array([axis_u * umin + [0, ymin, 0], axis_u * umax + [0, ymin, 0],
                            axis_u * umax + [0, ymax, 0], axis_u * umin + [0, ymax, 0]])
        quads.append((corners, np.array([[u0, v1], [u1, v1], [u1, v0], [u0, v0]]), axis_n, axis_u))
    pos_l, nrm_l, tan_l, uv_l, idx_l = [], [], [], [], []
    for i, (corners, uv, n, u) in enumerate(quads):
        pos_l.append(corners)
        uv_l.append(uv)
        nrm_l.append(np.tile(n, (4, 1)))
        tan_l.append(np.tile(np.append(u, 1.0), (4, 1)))
        idx_l.append(np.array([[0, 1, 2], [0, 2, 3]]) + 4 * i)
    return Part(np.vstack(pos_l), np.vstack(nrm_l), np.vstack(tan_l), np.vstack(uv_l), np.vstack(idx_l), "Cards")


# ---------------------------------------------------------------------------------------------- output

def write_glb(path, meshes):
    """meshes: [(name, [Part])]; one glTF mesh and node each, one primitive per part (material = atlas index)."""
    buffer = bytearray()
    views, accessors, gltf_meshes, nodes = [], [], [], []

    def add(blob, target, stride=None):
        buffer.extend(b"\0" * (-len(buffer) % 4))
        view = {"buffer": 0, "byteOffset": len(buffer), "byteLength": len(blob), "target": target}
        if stride:
            view["byteStride"] = stride
        views.append(view)
        buffer.extend(blob)
        return len(views) - 1

    for mesh_index, (name, parts) in enumerate(meshes):
        primitives = []
        for p in parts:
            # Y up like the game's EarthTrees.glb (clutter "asmb" frame); the pack's winding is kept.
            pos = p.pos.astype(np.float32)
            attrs = {}
            for key, data, kind in (("POSITION", pos, "VEC3"), ("NORMAL", p.nrm.astype(np.float32), "VEC3"),
                                    ("TEXCOORD_0", p.uv.astype(np.float32), "VEC2"),
                                    ("TANGENT", p.tan.astype(np.float32), "VEC4")):
                view = add(data.tobytes(), 34962, data.shape[1] * 4)
                acc = {"bufferView": view, "componentType": 5126, "count": len(data), "type": kind}
                if key == "POSITION":
                    acc["min"], acc["max"] = pos.min(0).tolist(), pos.max(0).tolist()
                accessors.append(acc)
                attrs[key] = len(accessors) - 1
            assert len(pos) < 65536
            view = add(p.idx.astype(np.uint16).tobytes(), 34963)
            accessors.append({"bufferView": view, "componentType": 5123, "count": int(p.idx.size), "type": "SCALAR"})
            primitives.append({"attributes": attrs, "indices": len(accessors) - 1, "material": MATERIAL_INDEX[p.material]})
        gltf_meshes.append({"name": name, "primitives": primitives})
        nodes.append({"mesh": mesh_index, "name": name})
    buffer.extend(b"\0" * (-len(buffer) % 4))
    gltf = {"asset": {"version": "2.0", "generator": "GEE tree_assets.py"}, "scene": 0,
            "scenes": [{"nodes": list(range(len(nodes)))}], "nodes": nodes, "meshes": gltf_meshes,
            "materials": [{"name": f"{PREFIX}_{name}"} for name, _, _ in MATERIALS],
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buffer)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buffer)))
        f.write(struct.pack("<I4s", len(js), b"JSON") + js)
        f.write(struct.pack("<I4s", len(buffer), b"BIN\0") + bytes(buffer))


def material_xml(name, slots, foliage):
    rel = lambda p: os.path.relpath(p, MOD_DIR).replace(os.sep, "/")
    lines = [f'  <GroundClutterMaterial Id="{PREFIX}_{name}">']
    for slot in ("Diffuse", "Normal", "AoRoughMetal", "Opacity", "Thickness"):
        if slot in slots:
            lines.append(f'    <{slot} Path="{rel(slots[slot])}" Category="Terrain" />')
    if foliage:
        lines += ['    <DoubleSided Value="true" />', '    <KeepBackfaceNormals Value="true" />',
                  '    <UsesCustomNormals Value="true" />']
    lines += ['    <DistanceFadeDither Value="true" />', "  </GroundClutterMaterial>"]
    return lines


def object_xml(object_id, lods, capsule, height):
    lines = [f'  <ClutterObject Id="{object_id}" Atlas="{PREFIX}" AngularDamping="0.5">', "    <LODs>"]
    for size, (mesh_names, materials) in zip(LOD_SCREEN_SIZES, lods):
        lines.append(f'      <LOD MinScreenSize="{size}">')
        lines += [f'        <Mesh Id="{m}" />' for m in mesh_names]
        lines += [f'        <Material Id="{PREFIX}_{m}" />' for m in sorted(materials, key=MATERIAL_INDEX.get)]
        lines.append("      </LOD>")
    lines += ["    </LODs>", "    <Colliders>", '      <Capsule Id="Trunk">',
              f'        <LocationAsmb X="0" Y="{capsule["centre"]:.3f}" Z="0" />',
              '        <Collider2Asmb X="0" Y="0" Z="0" />',
              f'        <LengthY M="{capsule["length"]:.3f}" />',
              f'        <Radius M="{capsule["radius"]:.3f}" />',
              "      </Capsule>", "    </Colliders>",
              '    <Substance Id="Wood.OakWhite">',
              f'      <Volume M3="{max(WOOD_PER_M3 * height ** 3, 0.05):.2f}" />',
              "    </Substance>", "  </ClutterObject>"]
    return lines


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    source = argv[1]
    with tempfile.TemporaryDirectory() as tmp:
        if zipfile.is_zipfile(source):
            zipfile.ZipFile(source).extractall(tmp)
            pack_dir = tmp
        else:
            pack_dir = source
        j, nodes = load_gltf(os.path.join(pack_dir, "scene.gltf"))
        tex_dir = os.path.join(MOD_DIR, "Textures")
        os.makedirs(tex_dir, exist_ok=True)
        os.makedirs(os.path.join(MOD_DIR, "Meshes"), exist_ok=True)
        textures = material_textures(j, pack_dir, tex_dir, write=True)

    W, H = CARD_COLUMNS * CARD_TILE[0], CARD_ROWS * CARD_TILE[1]
    atlas = dict(textures=textures, colour=np.zeros((H, W, 3)), alpha=np.zeros((H, W)),
                 normal=np.tile([0.5, 0.5, 1.0], (H, W, 1)), ao=np.ones((H, W)))
    meshes, objects, counters, slot = [], [], {}, 0
    for ecotype, node_names, target in MODELS:
        n = counters.get(ecotype, 0)
        counters[ecotype] = n + 1
        object_id = f"{PREFIX}_{ecotype}_{n}"
        prims = [p for name in node_names for p in nodes[name]]
        parts, height = build_model(prims, ecotype, target)
        mesh_name = f"{object_id}_Mesh"
        meshes.append((mesh_name, parts))
        near = ([mesh_name], {p.material for p in parts})
        if ecotype in CARD_ECOTYPES:
            cards = card_views(parts, height, slot, atlas)
            slot += 3
            card_name = f"{object_id}_Cards"
            meshes.append((card_name, [cards]))
            far = ([card_name], {"Cards"})
        else:
            far = near
        objects.append(object_xml(object_id, [near, near, near, far, far], trunk_capsule(parts, height), height))
        print(f"{object_id}: {height:.1f} m, {sum(len(p.idx) for p in parts)} triangles")
    assert slot <= CARD_COLUMNS * CARD_ROWS

    card = lambda slot_name: os.path.join(tex_dir, f"{PREFIX}_Cards_{slot_name}.ktx2")
    # The supersampled coverage is the share of the card the model really covers; keep that after the cutoff.
    write_colour(card("Diffuse"), push_pull_fill(atlas["colour"], (atlas["alpha"] > 0.02).astype(float)))
    write_opacity(card("Opacity"), atlas["alpha"], atlas["alpha"].mean())
    write_normal(card("Normal"), atlas["normal"])
    write_plain(card("PBR"), np.dstack([atlas["ao"], np.full((H, W), 0.85), np.zeros((H, W))]))
    textures["Cards"] = dict(slots={"Diffuse": card("Diffuse"), "Normal": card("Normal"), "AoRoughMetal": card("PBR"),
                                    "Opacity": card("Opacity"),
                                    "Thickness": os.path.join(tex_dir, f"{PREFIX}_Thickness.ktx2")}, foliage=True)

    write_glb(os.path.join(MOD_DIR, "Meshes", f"{PREFIX}.glb"), meshes)
    xml = ['<?xml version="1.0" encoding="utf-8"?>',
           "<!-- Generated by src/tools/tree_assets.py from \"Low Poly Forest Tree Pack\" by 99.Miles (CC BY 4.0). -->",
           "<Assets>", f'  <MeshAtlas Id="{PREFIX}" Path="Meshes/{PREFIX}.glb" HostOnly="true" />']
    for name, _, foliage in MATERIALS:
        xml += material_xml(name, textures[name]["slots"], foliage)
    for lines in objects:
        xml += lines
    xml.append("</Assets>")
    with open(os.path.join(MOD_DIR, "Trees.xml"), "w", encoding="utf-8", newline="\n") as f:
        import port_xml  # KSA 2026.11.10 asset format
        f.write(port_xml.convert_text("\n".join(xml) + "\n"))
    print(f"wrote Trees.xml, {len(objects)} clutter objects, {slot} card views")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
