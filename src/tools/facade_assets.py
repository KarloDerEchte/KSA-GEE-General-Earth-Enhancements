"""Procedural facade and roof textures for the GEE variants (numpy + Pillow).

The textures are stretched over each wall and roof, so only detail that survives any stretch is painted:
weathering streaks running down, soil at the foot, a shadowed cornice, ambient occlusion in the AO channel,
and on flat roofs a gravel surface with a darker parapet and a few plant rooms. Metre-sized detail (windows,
floor lines) comes from the shader pass in FacadeTechnique.cs.
"""
import numpy as np
from PIL import Image, ImageFilter

SIZE = 256


def noise(rng, size, blur):
    """Zero-mean, unit-variance noise with features about `blur` pixels wide."""
    a = rng.random((size, size)).astype("float32")
    im = Image.fromarray((a * 255).astype("uint8")).filter(ImageFilter.GaussianBlur(blur))
    b = np.asarray(im, dtype="float32")
    return (b - b.mean()) / (b.std() + 1e-6)


def streaks(rng, h, blur):
    """One noise row smoothed along x, for streaks running down."""
    a = rng.random((8, h)).astype("float32")
    row = np.asarray(Image.fromarray((a * 255).astype("uint8")).filter(ImageFilter.GaussianBlur(blur)), dtype="float32")[4]
    return (row - row.mean()) / (row.std() + 1e-6)


def to_image(a):
    return Image.fromarray(np.clip(a, 0, 255).astype("uint8"), "RGBA")


def grid():
    n, h = SIZE, SIZE // 2
    y = np.broadcast_to(np.arange(n)[:, None] / (n - 1), (n, h)).astype("float32")
    x = np.broadcast_to(np.arange(h)[None, :] / (h - 1), (n, h)).astype("float32")
    return n, h, x, y


def pack(diffuse_rgb, ao, rough, metal):
    n = SIZE
    diffuse = np.concatenate([diffuse_rgb, np.full((n, n, 1), 255.0)], axis=-1)
    pbr = np.stack([ao, rough, metal, np.full((n, n), 255.0)], axis=-1)
    return to_image(diffuse), to_image(pbr)


def facade(name, facade_rgb, roof_rgb, rough, metal, seed):
    """Diffuse and AoRoughMetal images: facade in the left half (v down from the roof edge), roof in the right."""
    rng = np.random.default_rng(1000 + seed)
    n, h, x, y = grid()

    grain = noise(rng, n, 0.8)[:, :h]
    blotch = noise(rng, n, 14)[:, :h]
    streak = np.broadcast_to(streaks(rng, h, 1.6)[None, :], (n, h))
    streak = streak * (0.35 + 0.65 * np.clip(0.5 + 0.5 * noise(rng, n, 20)[:, :h], 0, 1))
    foot = np.clip((y - 0.86) / 0.14, 0, 1)
    shade = 1.0 + 0.025 * grain + 0.035 * blotch - 0.035 * streak * (0.4 + 0.8 * y)
    shade *= 1.0 - 0.20 * foot ** 1.3                                        # soil at the foot
    shade *= 1.0 - 0.10 * np.exp(-((y - 0.05) / 0.025) ** 2)                 # shadow under the cornice
    shade *= 1.0 + 0.05 * np.exp(-((y - 0.012) / 0.012) ** 2)                # lit cornice edge
    shade *= 1.0 - 0.10 * np.clip((0.07 - np.minimum(x, 1 - x)) / 0.07, 0, 1)  # worn corners
    face = np.array(facade_rgb, dtype="float32")[None, None, :] * shade[..., None]
    face_rough = rough + 8 * grain - 6 * streak + 18 * foot
    if name == "glass":
        sky = np.array([170, 200, 235], dtype="float32")
        k = (0.23 * (1 - y))[..., None]
        face = face * (1 - k) + sky * k
        face = face * (1.0 + 0.04 * np.sin(x * 140.0 + 0.5 * blotch)[..., None])
    elif name == "metal":
        face = face * (1.0 + 0.05 * np.broadcast_to(streaks(rng, h, 0.6)[None, :], (n, h))[..., None])
    elif name == "brick":
        courses = np.sin(y * n * 0.5) * np.sin(x * h * 0.55 + (np.floor(y * n * 0.16) % 2) * 3.14159)
        face = face * (1.0 - 0.07 * np.clip(courses, 0, 1)[..., None])
    face_ao = 255 - 85 * foot ** 1.5 - 30 * np.exp(-((y - 0.04) / 0.03) ** 2)

    rgrain = noise(rng, n, 0.9)[:, :h]
    rblotch = noise(rng, n, 10)[:, :h]
    edge = np.minimum(np.minimum(x, 1 - x), np.minimum(y, 1 - y))
    rshade = 1.0 + 0.05 * rgrain + 0.06 * rblotch
    rshade *= 1.0 - 0.16 * np.clip((0.05 - edge) / 0.05, 0, 1)               # parapet edge in shadow
    rshade *= 1.0 + 0.07 * np.exp(-((edge - 0.075) / 0.012) ** 2)            # lit coping
    roof = np.array(roof_rgb, dtype="float32")[None, None, :] * rshade[..., None]
    for _ in range(int(rng.integers(2, 5))):                                  # plant rooms, skylights
        cx, cy = rng.uniform(0.2, 0.8, 2)
        hw, hh = rng.uniform(0.04, 0.1, 2)
        inside = (np.abs(x - cx) < hw) & (np.abs(y - cy) < hh)
        rim = (np.abs(x - cx) < hw + 0.012) & (np.abs(y - cy) < hh + 0.012) & ~inside
        roof = np.where(inside[..., None], roof * rng.uniform(0.82, 1.18), roof)
        roof = np.where(rim[..., None], roof * 0.8, roof)
    roof_rough = 205 + 12 * rgrain
    roof_ao = 255 - 70 * np.clip((0.04 - edge) / 0.04, 0, 1)

    return pack(np.concatenate([face, roof], axis=1),
                np.concatenate([face_ao, roof_ao], axis=1),
                np.concatenate([face_rough, roof_rough], axis=1),
                np.concatenate([np.full((n, h), float(metal)), np.zeros((n, h))], axis=1))


def gable_roof(tile_rgb, wall_rgb, seed):
    """Pitched roof: gable wall in the left half, slope in the right (v from ridge to eave): tile courses with
    per-tile variation, darker overlap edges, moss patches."""
    rng = np.random.default_rng(2000 + seed)
    n, h, x, y = grid()
    grain = noise(rng, n, 0.8)[:, :h]
    blotch = noise(rng, n, 9)[:, :h]
    courses = 16
    row_t = (y * courses) % 1.0
    row_id = np.floor(y * courses)
    col_t = (x * 20 + (row_id % 2) * 0.5) % 1.0
    tile_id = np.floor(x * 20 + (row_id % 2) * 0.5) + row_id * 31
    per_tile = ((np.sin(tile_id * 12.9898) * 43758.5453) % 1.0) - 0.5
    shade = (0.80 + 0.20 * row_t ** 0.7) * (1.0 + 0.10 * per_tile + 0.03 * grain + 0.05 * blotch)
    shade *= 1.0 - 0.12 * np.clip((0.06 - np.minimum(col_t, 1 - col_t)) / 0.06, 0, 1)
    slope = np.array(tile_rgb, dtype="float32")[None, None, :] * shade[..., None]
    moss = (np.clip(blotch - 0.9, 0, 1.0) * 0.35)[..., None]
    slope = slope * (1 - moss) + np.array([68, 84, 52], dtype="float32") * moss
    wall_shade = 1.0 + 0.03 * grain + 0.03 * blotch - 0.12 * np.clip((y - 0.85) / 0.15, 0, 1)
    wall = np.array(wall_rgb, dtype="float32")[None, None, :] * wall_shade[..., None]
    return pack(np.concatenate([wall, slope], axis=1),
                np.concatenate([np.full((n, h), 255.0), 255 - 55 * (1 - row_t) ** 2], axis=1),
                np.concatenate([230 + 8 * grain, 195 + 14 * grain + 20 * (1 - row_t)], axis=1),
                np.zeros((n, n)))


def metal_roof(seed):
    """Low metal gable roof of a hall: ribbed gable ends in the left half, the slope with standing seams running
    from ridge to eave in the right half."""
    rng = np.random.default_rng(3000 + seed)
    n, h, x, y = grid()
    grain = noise(rng, n, 0.8)[:, :h]
    blotch = noise(rng, n, 12)[:, :h]
    seams = np.clip(1 - np.abs(((x * 48) % 1.0) - 0.5) / 0.08, 0, 1)
    slope_shade = (1.0 + 0.02 * grain + 0.03 * blotch) * (1 - 0.12 * seams) * (1 - 0.05 * y)
    slope = np.array((178, 182, 186), dtype="float32")[None, None, :] * slope_shade[..., None]
    ribs = 0.5 + 0.5 * np.cos(x * h * 0.9)
    wall = np.array((214, 217, 219), dtype="float32")[None, None, :] * ((1 - 0.06 * ribs) * (1 + 0.02 * grain))[..., None]
    return pack(np.concatenate([wall, slope], axis=1),
                np.full((n, n), 255.0),
                np.concatenate([180 + 6 * grain, 150 + 10 * grain - 25 * seams], axis=1),
                np.concatenate([np.full((n, h), 20.0), np.full((n, h), 70.0)], axis=1))
