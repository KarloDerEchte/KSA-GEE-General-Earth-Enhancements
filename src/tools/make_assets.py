"""Generates the GEE render assets: a unit box mesh, one material per building colour and Assets.xml.

Run from anywhere: python make_assets.py
Writes into the mod folder (two levels above this script). The colour table must match Buildings.Variants in Buildings.cs.
Landmark models and textures come from landmark_assets.py (needs Pillow and numpy).
"""
import json
import os
import random
import struct
import zlib

import facade_assets
import furniture_assets
import house_assets
import landmark_assets
import structure_assets

MOD_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# name, facade rgb, roof rgb, facade roughness, facade metalness
VARIANTS = [
    ("concrete", (178, 176, 170), (152, 152, 150), 225, 0),
    ("plaster", (222, 218, 208), (166, 158, 146), 230, 0),
    ("sand", (205, 185, 150), (122, 116, 108), 230, 0),
    ("brick", (150, 80, 60), (96, 94, 92), 235, 0),
    ("dark", (95, 98, 104), (104, 104, 104), 170, 20),
    ("glass", (110, 135, 160), (150, 150, 150), 80, 60),
    ("metal", (185, 190, 195), (160, 165, 170), 140, 120),
    ("ochre", (210, 170, 100), (142, 90, 70), 230, 0),
]

# More facade colours (Buildings.ExtraFacades, after the hip roofs): same columns as VARIANTS. Painted houses and
# apartment blocks in pastel plaster, so a street is not one white.
EXTRA_FACADES = [
    ("cream", (232, 224, 200), (150, 148, 144), 232, 0),
    ("pale yellow", (226, 208, 148), (146, 142, 134), 232, 0),
    ("sage", (174, 188, 166), (140, 142, 138), 232, 0),
    ("terracotta", (196, 122, 92), (138, 128, 120), 236, 0),
    ("grey blue", (150, 166, 184), (136, 140, 146), 225, 0),
    ("salmon", (214, 164, 148), (146, 138, 130), 232, 0),
    ("white", (240, 240, 236), (158, 156, 152), 228, 0),
    ("dark brick", (112, 62, 54), (92, 90, 88), 238, 0),
]

# Hip roofs (Buildings.FirstHipVariant on): ridge inset from each end as a share of the ridge-axis length, for
# footprints of increasing squareness. Variant = first + shape * 3 + roof colour.
HIP_INSETS = (0.20, 0.33, 0.50)

TEX_W, TEX_H = 128, 64

# Alpha of the diffuse and PBR textures of ground surfaces (roads, airfields): the game's static object shader is
# patched to look up their shadows a little higher up (ShaderPatch.cs, same values).
DIFFUSE_MARKER, PBR_MARKER = 0x2B, 0xB2


def write_png(path, width, height, pixel):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(pixel(x, y))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def clamp(v):
    return max(0, min(255, int(round(v))))


def diffuse_pixel(facade, roof, rng):
    def pixel(x, y):
        if x < TEX_W // 2:
            # facade: slightly darker toward the ground (v grows downward, v=0 is the roof edge)
            shade = 1.0 - 0.18 * (y / (TEX_H - 1))
            c = facade
        else:
            shade = 0.97 + 0.06 * rng.random()
            c = roof
        return bytes((clamp(c[0] * shade), clamp(c[1] * shade), clamp(c[2] * shade), 255))
    return pixel


def pbr_pixel(rough, metal):
    # AoRoughMetal: R = ambient occlusion, G = roughness, B = metalness
    def pixel(x, y):
        if x < TEX_W // 2:
            return bytes((255, rough, metal, 255))
        return bytes((255, 205, 0, 255))
    return pixel


# Road surfaces from scans (Buildings.RoadVariant and the variants after the crew arm): a square texture whose first
# SCAN_SPLIT columns are the slab sides and the rest its top, SCAN_GAP pixels of margin kept off the edges
# (GEE_ScannedSlab maps the same). AoRoughMetal at half size: roughness and AO need less detail than colour.
SCAN_SIZE, SCAN_PBR_SIZE, SCAN_SPLIT, SCAN_GAP = 2048, 1024, 128, 4
# The longest road piece (Placement.RoadPieceMeters): every dash period divides it and each scan repeats a whole
# number of times along it, so the surface runs on seamlessly from piece to piece.
ROAD_PIECE = 16.0
POLYHAVEN_DIR = os.path.join(os.path.dirname(__file__), "data", "polyhaven")
SCAN_RESOLUTION = "8k"

# Poly Haven scans (CC0, 8k): asset, size of the scan in metres, saturation kept, mean brightness (0-255).
SCANS = {
    # Clean, even race-track asphalt: highways and their ramps.
    "pit lane": ("asphalt_pit_lane", 2.0, 0.35, 64),
    # Fine-grained grey asphalt: main roads (asphalt_02's cracks repeat in every tile and read as joints).
    "road": ("clean_asphalt", 2.1, 0.35, 66),
    # Rough, worn asphalt: streets.
    "street": ("asphalt_01", 2.08, 0.35, 70),
    # Compacted dirt: unpaved tracks.
    "dirt": ("dirt", 2.0, 0.85, 100),
}

# Markings, metres from the right edge (v = 0, the slab's -B side; right-hand traffic): centre, width and either
# None (solid) or (dash, period). Each profile is laid out for one width; narrower or wider roads stretch it.
ROAD_PROFILES = {
    # Primary, secondary, tertiary (Buildings.RoadVariant): edge lines and a dashed centre line.
    "road": ("road", 8.0, [(0.35, 0.12, None), (7.65, 0.12, None), (4.0, 0.12, (3.0, 8.0))]),
    # The same asphalt without lines where roads meet (Buildings.RoadPlainVariant).
    "road plain": ("road", 8.0, []),
    # One carriageway of a motorway or trunk (Buildings.HighwayVariant): 2.5 m hard shoulder, two 3.75 m lanes split
    # by a dashed line (6 m dash, 10 m gap), solid edge lines and a 0.75 m strip on the left.
    "highway": ("pit lane", 11.0, [(2.5, 0.20, None), (10.25, 0.15, None), (6.25, 0.15, (6.0, 16.0))]),
    # Estimated 3-lane one-way motorway carriageway (Buildings.Highway3Variant, Roads.Highway3Width): 2.5 m shoulder,
    # three 3.75 m lanes, 0.75 m left strip, two dashed dividers.
    "highway 3": ("pit lane", 14.5, [(2.5, 0.20, None), (13.75, 0.15, None), (6.25, 0.15, (6.0, 16.0)), (10.0, 0.15, (6.0, 16.0))]),
    # Estimated 4-lane two-way road drawn as one line (Buildings.Highway4Variant, Roads.Highway4Width): 1 m edge strips,
    # four 3.6 m lanes, dashed lane lines and a double solid centre line.
    "highway 4": ("pit lane", 16.4, [(1.0, 0.15, None), (15.4, 0.15, None), (4.6, 0.15, (6.0, 16.0)), (11.8, 0.15, (6.0, 16.0)),
                                     (8.0, 0.12, None), (8.4, 0.12, None)]),
    # Minor, residential, service (Buildings.StreetVariant): no markings.
    "street": ("street", 6.0, []),
    # One-lane motorway and trunk links (Buildings.RampVariant): edge lines.
    "ramp": ("pit lane", 6.0, [(0.6, 0.15, None), (5.5, 0.15, None)]),
    # Ramp with gore hatching (Buildings.GoreVariant, RoadGore): the ramp's edge lines plus chevrons every 4 m, 0.4 m
    # thick along the road, arms at 45 degrees. Only the part outside the carriageway shows.
    "gore": ("pit lane", 6.0, [(0.6, 0.15, None), (5.5, 0.15, None), ("chevron", 0.4, (4.0, 1.0))]),
    # Tracks and unpaved roads (Buildings.TrackVariant).
    "track": ("dirt", 4.0, []),
    # Solid white paint (Buildings.JunctionPaintVariant): crosswalk stripes and stop lines, thin slabs on top of the road.
    "paint": ("road", 8.0, [(4.0, 8.6, None)]),
}


def scan_uv():
    """u ranges of the top and the sides and v range of the top, as unit_box takes them."""
    n, s, g = SCAN_SIZE, SCAN_SPLIT, SCAN_GAP
    return dict(top=((s + g) / n, (n - g) / n), side=(g / n, (s - g) / n), across=(g / n, (n - g) / n))


def polyhaven_maps(asset, resolution):
    """Diffuse and ARM (AO, roughness, metal) scans of a Poly Haven texture (CC0), downloaded once into data/polyhaven."""
    import urllib.request
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None
    os.makedirs(POLYHAVEN_DIR, exist_ok=True)
    images = []
    for kind in ("diff", "arm"):
        name = f"{asset}_{kind}_{resolution}.jpg"
        path = os.path.join(POLYHAVEN_DIR, name)
        if not os.path.exists(path):
            url = f"https://dl.polyhaven.org/file/ph-assets/Textures/jpg/{resolution}/{asset}/{name}"
            request = urllib.request.Request(url, headers={"User-Agent": "GEE-mod make_assets.py"})
            with urllib.request.urlopen(request) as response, open(path + ".part", "wb") as f:
                f.write(response.read())
            os.replace(path + ".part", path)
        images.append(Image.open(path).convert("RGB"))
    return images


_scan_cache = {}


def scanned_images(scan, width, lines):
    """Road surface from a Poly Haven scan (SCANS) at its real size: the top stands for width metres across and a
    ROAD_PIECE along, the scan repeated a whole number of times along it (stretched by a few per cent if need be) so
    pieces join seamlessly. lines as in ROAD_PROFILES."""
    import numpy as np
    from PIL import Image
    asset, scan_m, saturation, brightness = SCANS[scan]
    if asset not in _scan_cache:
        _scan_cache[asset] = polyhaven_maps(asset, SCAN_RESOLUTION)
    diff, arm = _scan_cache[asset]
    n, s, g = SCAN_SIZE, SCAN_SPLIT, SCAN_GAP
    used_along, used_across = n - s - 2 * g, n - 2 * g
    tiles_along = max(1, round(ROAD_PIECE / scan_m))
    tile_w = round(used_along / tiles_along)
    tile_h = round(used_across / (width / scan_m))

    def flatten(a):
        """Divides out the scan's soft light falloff (a wrap-around Gaussian blur of a fifth of the tile), keeping
        its grain: otherwise every repeat shows as a square on the road."""
        h, w = a.shape[:2]
        sigma = 0.2 * min(h, w)
        fy, fx = np.fft.fftfreq(h)[:, None], np.fft.fftfreq(w)[None, :]
        kernel = np.exp(-2 * (np.pi * sigma) ** 2 * (fx * fx + fy * fy))
        low = np.real(np.fft.ifft2(np.fft.fft2(a, axes=(0, 1)) * kernel[..., None], axes=(0, 1)))
        return a / np.maximum(low, 1.0) * a.mean(axis=(0, 1))

    def tile_resize(img):
        """The scan scaled to one tile; it is padded with its own opposite edges first, so the filter wraps around
        and the tile edges still meet (a plain resize clamps them and leaves a seam at every repeat)."""
        src = np.asarray(img)
        pad = 4
        my, mx = round(pad * src.shape[0] / tile_h), round(pad * src.shape[1] / tile_w)
        padded = np.pad(src, ((my, my), (mx, mx), (0, 0)), mode="wrap")
        small = Image.fromarray(padded).resize((tile_w + 2 * pad, tile_h + 2 * pad), Image.LANCZOS)
        return np.asarray(small, dtype="float32")[pad:-pad, pad:-pad]

    def region(img):
        a = flatten(tile_resize(img))
        strip = np.tile(np.clip(a, 0, 255), (n // tile_h + 2, tiles_along, 1))[:n]
        if strip.shape[1] != used_along:
            strip = np.asarray(Image.fromarray(strip.astype("uint8")).resize((used_along, n), Image.LANCZOS), dtype="float32")
        # The margins continue the pattern, so it wraps exactly from one piece to the next.
        return np.concatenate([strip[:, -g:], strip, strip[:, :g]], axis=1)

    rgb, pbr = region(diff), region(arm)
    # Scans are warm brown; real asphalt is a neutral dark grey with an albedo of about 0.1 (lighter read as
    # concrete paths in game).
    lum = rgb.mean(axis=-1, keepdims=True)
    rgb = lum + saturation * (rgb - lum)
    rgb *= brightness / rgb.mean()
    rows = np.arange(n, dtype="float32")[:, None]
    cols = np.arange(n - s, dtype="float32")[None, :]
    across_m = np.clip((rows - g) / (used_across - 1), 0, 1) * width
    along_m = (cols - g) / used_along * ROAD_PIECE
    px_across, px_along = used_across / width, used_along / ROAD_PIECE

    paint = np.zeros((n, n - s), dtype="float32")
    for centre, line_width, dash in lines:
        if centre == "chevron":
            # Gore hatching: V's pointing along the road (+u), arms trailing back to both edge lines. line_width is
            # the thickness along the road, dash = (period, slope = metres back per metre out from the middle).
            period, slope = dash
            d = np.abs(across_m - width / 2)
            at = (along_m + slope * d) % period
            cover = np.clip((line_width / 2 - np.minimum(at, period - at)) * px_along + 0.5, 0, 1)
            cover = cover * np.clip((width / 2 - 0.8 - d) * px_across + 0.5, 0, 1)
            paint = np.maximum(paint, cover)
            continue
        cover = np.clip(line_width / 2 * px_across - np.abs(across_m - centre) * px_across + 0.5, 0, 1)
        if dash:
            d, period = dash
            at = along_m % period
            cover = cover * np.clip(np.minimum(at, d - at) * px_along + 0.5, 0, 1)
        paint = np.maximum(paint, cover)
    lum = rgb.mean(axis=-1)
    worn = np.clip(0.92 + (lum - lum.mean()) / (lum.std() + 1e-6) * 0.06, 0.75, 1.0)
    paint *= worn
    white = np.array([214, 214, 206], dtype="float32")
    rgb = rgb * (1 - paint[..., None]) + white * paint[..., None]
    pbr[..., 1] = pbr[..., 1] * (1 - paint) + 170 * paint
    pbr[..., 0] = pbr[..., 0] * (1 - paint) + 255 * paint
    pbr[..., 2] = 0

    rng = np.random.default_rng(11)
    side = np.array([98, 90, 74], dtype="float32") + 6 * rng.standard_normal((n, s, 1)).astype("float32")
    diffuse = np.concatenate([side, rgb], axis=1)
    side_pbr = np.broadcast_to(np.array([255, 240, 0], dtype="float32"), (n, s, 3))
    aorm = np.concatenate([side_pbr, pbr], axis=1)
    diffuse = np.concatenate([diffuse, np.full((n, n, 1), float(DIFFUSE_MARKER))], axis=-1)
    aorm = np.concatenate([aorm, np.full((n, n, 1), float(PBR_MARKER))], axis=-1)
    to_image = lambda a: Image.fromarray(np.clip(a, 0, 255).astype("uint8"), "RGBA")
    pbr_image = to_image(aorm).resize((SCAN_PBR_SIZE, SCAN_PBR_SIZE), Image.BOX)
    # Keep the marker exact after filtering.
    pbr_image.putalpha(PBR_MARKER)
    return to_image(diffuse), pbr_image


def road_profile_images(name):
    scan, width, lines = ROAD_PROFILES[name]
    return scanned_images(scan, width, lines)


# Gabled roofs (Buildings.FirstRoofVariant on in C#): name, tile rgb, gable wall rgb.
ROOFS = [
    ("tile red", (140, 82, 64), (214, 206, 192)),
    ("slate", (92, 92, 94), (200, 196, 188)),
    ("brown", (108, 88, 74), (220, 212, 196)),
]


def roof_diffuse_pixel(tile, wall, rng):
    def pixel(x, y):
        if x < TEX_W // 2:
            shade = 0.96 + 0.04 * rng.random()
            c = wall
        else:
            # Rows of tiles across the slope (v runs from ridge to eave), darker at each row's lower edge.
            row = (y % 8) / 7
            shade = (0.82 + 0.18 * row) * (0.94 + 0.08 * rng.random())
            c = tile
        return bytes((clamp(c[0] * shade), clamp(c[1] * shade), clamp(c[2] * shade), 255))
    return pixel


def roof_pbr_pixel(x, y):
    return bytes((255, 230 if x < TEX_W // 2 else 190, 0, 255))


def airfield_diffuse_pixel(kind, rng):
    """Top faces: u runs along the surface, v across (as on roads). Runways: dark asphalt with white edge
    stripes and a dashed white centre line (one dash per piece), taxiways: yellow centre and edge lines,
    aprons: plain concrete."""
    def pixel(x, y):
        v = y / (TEX_H - 1)
        u = (x - TEX_W // 2) / (TEX_W // 2 - 1)
        if kind == "apron":
            g = 150 + 14 * rng.random()
            return bytes((clamp(g), clamp(g), clamp(g * 0.96), DIFFUSE_MARKER))
        base = 46 if kind == "runway" else 56
        g = base + 10 * rng.random()
        if x >= TEX_W // 2:
            if kind == "runway" and (abs(v - 0.05) < 0.012 or abs(v - 0.95) < 0.012 or (abs(v - 0.5) < 0.01 and u < 0.6)):
                w = 215 + 15 * rng.random()
                return bytes((clamp(w), clamp(w), clamp(w * 0.97), DIFFUSE_MARKER))
            if kind == "taxiway" and (abs(v - 0.5) < 0.012 or abs(v - 0.06) < 0.01 or abs(v - 0.94) < 0.01):
                return bytes((clamp(220 + 20 * rng.random()), clamp(170 + 15 * rng.random()), 40, DIFFUSE_MARKER))
        return bytes((clamp(g), clamp(g), clamp(g * 1.04), DIFFUSE_MARKER))
    return pixel


def airfield_pbr_pixel(x, y):
    return bytes((255, 235, 0, PBR_MARKER))


def gable():
    """Gabled roof over the unit footprint: x up in [0, 1], ridge along y at x = 1 over z = 0, eaves at z = +-0.5.
    Slopes use the roof half of the texture (u in [0.5, 1], v from ridge to eave), the gable ends the wall half."""
    m = 0.03
    faces = []
    for sign in (1, -1):
        faces.append([(0, -.5, .5 * sign), (0, .5, .5 * sign), (1, .5, 0), (1, -.5, 0)])
    for sign in (1, -1):
        faces.append([(0, .5 * sign, -.5), (0, .5 * sign, .5), (1, .5 * sign, 0)])

    positions, normals, uvs, indices = [], [], [], []
    for poly in faces:
        a, b, c = poly[0], poly[1], poly[2]
        u = [b[k] - a[k] for k in range(3)]
        v = [c[k] - a[k] for k in range(3)]
        n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]
        # Outward: away from the footprint's centre line, at half height.
        centre = [sum(p[k] for p in poly) / len(poly) for k in range(3)]
        outward = [centre[0] - 0.3, centre[1], centre[2]]
        flip = sum(n[k] * outward[k] for k in range(3)) < 0
        if flip:
            n = [-n[k] for k in range(3)]
        length = sum(n[k] * n[k] for k in range(3)) ** 0.5
        n = tuple(n[k] / length for k in range(3))
        sloped = len(poly) == 4
        base = len(positions)
        for p in poly:
            positions.append(p)
            normals.append(n)
            if sloped:
                uvs.append((0.5 + m + (0.5 - 2 * m) * (p[1] + .5), m + (1 - 2 * m) * (1 - p[0])))
            else:
                uvs.append((m + (0.5 - 2 * m) * (p[2] + .5), m + (1 - 2 * m) * (1 - p[0])))
        for k in range(1, len(poly) - 1):
            tri = (0, k + 1, k) if flip else (0, k, k + 1)
            indices.extend(base + i for i in tri)
    return positions, normals, uvs, indices


def hip(inset):
    """Hipped roof over the unit footprint, same frame and texture layout as gable(): ridge along y at x = 1, eaves at
    z = +-0.5. The ridge stops `inset` short of each end (0.5 = pyramid); the four slopes all use the roof half of
    the texture, courses running from ridge to eave."""
    m = 0.03
    r = 0.5 - inset
    faces = []
    for sign in (1, -1):
        quad = [(0, -.5, .5 * sign), (0, .5, .5 * sign), (1, r, 0), (1, -r, 0)]
        faces.append(quad if r > 1e-6 else [quad[0], quad[1], quad[2]])
    for sign in (1, -1):
        faces.append([(0, .5 * sign, -.5), (0, .5 * sign, .5), (1, r * sign, 0)])

    positions, normals, uvs, indices = [], [], [], []
    for poly in faces:
        a, b, c = poly[0], poly[1], poly[2]
        u = [b[k] - a[k] for k in range(3)]
        v = [c[k] - a[k] for k in range(3)]
        n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]
        centre = [sum(p[k] for p in poly) / len(poly) for k in range(3)]
        flip = sum(n[k] * (centre[k] - (0.3 if k == 0 else 0)) for k in range(3)) < 0
        if flip:
            n = [-n[k] for k in range(3)]
        length = sum(n[k] * n[k] for k in range(3)) ** 0.5
        n = tuple(n[k] / length for k in range(3))
        base = len(positions)
        for p in poly:
            positions.append(p)
            normals.append(n)
            # Along the eave for the long slopes, across for the ends; v from ridge (1 - x) to eave.
            long_slope = abs(poly[0][2] - poly[1][2]) < 1e-9      # eave along y; the end slopes have theirs along z
            u_coord = (p[1] + .5) if long_slope else (p[2] + .5)
            uvs.append((0.5 + m + (0.5 - 2 * m) * u_coord, m + (1 - 2 * m) * (1 - p[0])))
        for k in range(1, len(poly) - 1):
            tri = (0, k + 1, k) if flip else (0, k, k + 1)
            indices.extend(base + i for i in tri)
    return positions, normals, uvs, indices


def unit_box(up_normals=False, top=(0.53, 0.97), side=(0.03, 0.47), across=(0.03, 0.97)):
    """Box with x in [0, 1] (up) and y, z in [-0.5, 0.5]; no bottom face. Facade uses u in [0, 0.5], roof u in [0.5, 1]
    (top, side: the u ranges; across: the top face's v range).

    With up_normals every face is shaded as if it faced up. Road slabs use that: their low sides would
    otherwise catch grazing sunlight at dawn and dusk and outline every road in bright orange."""
    m = 0.03
    faces = []
    # top (+x)
    faces.append(((1, 0, 0), [(1, y, z) for y, z in ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5))],
                  lambda p: (top[0] + (top[1] - top[0]) * (p[1] + .5), across[0] + (across[1] - across[0]) * (p[2] + .5))))
    # sides: normal axis, the horizontal axis used for u
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
        faces.append((tuple(n), quad,
                      lambda p, o=other: (side[0] + (side[1] - side[0]) * (p[o] + .5), m + (1 - 2 * m) * (1 - p[0]))))

    positions, normals, uvs, indices = [], [], [], []
    for n, quad, uv in faces:
        base = len(positions)
        for p in quad:
            positions.append(p)
            normals.append((1, 0, 0) if up_normals else n)
            uvs.append(uv(p))
        for tri in ((0, 1, 2), (0, 2, 3)):
            a, b, c = (quad[i] for i in tri)
            u = [b[k] - a[k] for k in range(3)]
            v = [c[k] - a[k] for k in range(3)]
            cross = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
            if sum(cross[k] * n[k] for k in range(3)) < 0:  # counter-clockwise seen from outside
                tri = (tri[0], tri[2], tri[1])
            indices.extend(base + i for i in tri)
    return positions, normals, uvs, indices


def write_flat_normal_bc5(path):
    """Flat normal map (x = y = 0) as a 4x4 BC5 KTX2 with its mips, uncompressed. The game reads normal maps as BC5
    only: a PNG or RGBA8 one came out as a non-flat placeholder that tilted every normal by tens of degrees (dark navy
    roads, blue shade on the towers)."""
    block = bytes((128, 128, 0, 0, 0, 0, 0, 0)) * 2           # BC4 red + BC4 green, every texel 128
    # Data format descriptor of a BC5 UNORM texture, as libktx writes it (KHR_DF_MODEL_BC5, 4x4 blocks of 16 bytes).
    dfd = bytes.fromhex("3c00000000000000020038008401010003030000100000000000000000003f00"
                        "0000000000000000ffffffff40003f010000000000000000ffffffff")
    levels = 3                                               # 4x4, 2x2, 1x1: one block each
    index_end = 80 + 24 * levels
    data_start = (index_end + len(dfd) + 15) // 16 * 16
    out = b"\xabKTX 20\xbb\r\n\x1a\n"
    out += struct.pack("<9I", 141, 1, 4, 4, 0, 0, 1, levels, 0)   # VK_FORMAT_BC5_UNORM_BLOCK, no supercompression
    out += struct.pack("<4I", index_end, len(dfd), 0, 0) + struct.pack("<2Q", 0, 0)
    for level in range(levels):                              # smallest mip first in the file
        out += struct.pack("<3Q", data_start + 16 * (levels - 1 - level), 16, 16)
    out += dfd
    out += b"\0" * (data_start - len(out)) + block * levels
    with open(path, "wb") as f:
        f.write(out)


def write_glb(path, meshes):
    """meshes: list of (name, (positions, normals, uvs, indices)), one glTF mesh and node each."""
    buffer = b""
    views, accessors, gltf_meshes, nodes = [], [], [], []
    for mesh_index, (mesh_name, (positions, normals, uvs, indices)) in enumerate(meshes):
        blobs = [
            struct.pack(f"<{len(indices)}{'I' if len(positions) > 65535 else 'H'}", *indices),
            b"".join(struct.pack("<3f", *p) for p in positions),
            b"".join(struct.pack("<3f", *n) for n in normals),
            b"".join(struct.pack("<2f", *t) for t in uvs),
        ]
        first = len(views)
        for blob, target, stride in zip(blobs, (34963, 34962, 34962, 34962), (None, 12, 12, 8)):
            buffer += b"\0" * (-len(buffer) % 4)
            view = {"buffer": 0, "byteOffset": len(buffer), "byteLength": len(blob), "target": target}
            if stride:
                view["byteStride"] = stride
            views.append(view)
            buffer += blob
        accessors += [
            {"bufferView": first, "componentType": 5125 if len(positions) > 65535 else 5123, "count": len(indices), "type": "SCALAR"},
            {"bufferView": first + 1, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": [float(min(p[k] for p in positions)) for k in range(3)],
             "max": [float(max(p[k] for p in positions)) for k in range(3)]},
            {"bufferView": first + 2, "componentType": 5126, "count": len(normals), "type": "VEC3"},
            {"bufferView": first + 3, "componentType": 5126, "count": len(uvs), "type": "VEC2"},
        ]
        gltf_meshes.append({"name": mesh_name, "primitives": [
            {"attributes": {"POSITION": first + 1, "NORMAL": first + 2, "TEXCOORD_0": first + 3}, "indices": first}]})
        nodes.append({"mesh": mesh_index, "name": mesh_name})
    buffer += b"\0" * (-len(buffer) % 4)
    gltf = {
        "asset": {"version": "2.0", "generator": "GEE make_assets.py"},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
        "meshes": gltf_meshes,
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{"byteLength": len(buffer)}],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    total = 12 + 8 + len(js) + 8 + len(buffer)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, total))
        f.write(struct.pack("<I4s", len(js), b"JSON") + js)
        f.write(struct.pack("<I4s", len(buffer), b"BIN\0") + buffer)


def main():
    os.makedirs(os.path.join(MOD_DIR, "Meshes"), exist_ok=True)
    os.makedirs(os.path.join(MOD_DIR, "Textures"), exist_ok=True)
    box_mesh, road_mesh, gable_mesh, tank_mesh = "GEE_UnitBox", "GEE_RoadSlab", "GEE_Gable", "GEE_Tank"
    write_glb(os.path.join(MOD_DIR, "Meshes", "GEE_MeshAtlas.glb"),
              [(box_mesh, unit_box()), (road_mesh, unit_box(up_normals=True)), (gable_mesh, gable()),
               (tank_mesh, structure_assets.tank_mesh()), ("GEE_TankDome", structure_assets.dome_mesh()),
               ("GEE_HTank", structure_assets.horizontal_tank()), ("GEE_Tent", structure_assets.tent()),
               ("GEE_CrewArm", structure_assets.crew_arm()),
               ("GEE_ScannedSlab", unit_box(up_normals=True, **scan_uv()))]
              + [(f"GEE_Hip{k}", hip(inset)) for k, inset in enumerate(HIP_INSETS)]
              + [("GEE_Dormer", house_assets.dormer()), ("GEE_Barge", house_assets.barge())]
              + landmark_assets.meshes() + furniture_assets.meshes())
    write_flat_normal_bc5(os.path.join(MOD_DIR, "Textures", "GEE_Normal.ktx2"))

    xml = ['<?xml version="1.0" encoding="utf-8"?>',
           "<!-- Generated by src/tools/make_assets.py. -->",
           "<Assets>",
           '  <MeshAtlas Path="Meshes/GEE_MeshAtlas.glb" />']
    textures = [(name, None, None) for name, *_ in VARIANTS]
    # Variant 8 (Buildings.RoadVariant in C#): the road asset.
    textures.append(("road", None, None))
    for i, (name, diffuse, pbr) in enumerate(textures):
        if name == "road":
            xml += ktx_material(i, "road", road_profile_images("road"), "GEE_ScannedSlab")
        else:
            _, facade, roof, rough, metal = VARIANTS[i]
            xml += ktx_material(i, name, facade_assets.facade(name, facade, roof, rough, metal, i), box_mesh)
    # From variant 9 (Buildings.FirstLandmarkVariant in C#): the landmarks.
    landmarks = landmark_assets.write(MOD_DIR, len(textures), write_png)
    xml += landmarks
    i = len(textures) + sum(line.strip().startswith("<StaticObject ") for line in landmarks)
    # Then the gabled roofs and the airfield surfaces (Buildings.FirstRoofVariant, RunwayVariant, TaxiwayVariant
    # and ApronVariant in C#).
    roof_first = i
    for k, (name, tile, wall) in enumerate(ROOFS):
        xml += ktx_material(i, f"roof {name}", facade_assets.gable_roof(tile, wall, k), gable_mesh)
        i += 1
    for k, name in enumerate(("runway", "taxiway", "apron")):
        xml += material(i, name, airfield_diffuse_pixel(name, random.Random(300 + k)), airfield_pbr_pixel, road_mesh)
        i += 1
    # Round tanks and silos (Buildings.TankVariant in C#), after the airfield surfaces.
    tank_images = facade_assets.facade("tank", (228, 230, 230), (190, 192, 194), 150, 30, 9)
    xml += ktx_material(i, "tank", tank_images, tank_mesh)
    i += 1
    xml += ktx_material(i, "tank dome", tank_images, "GEE_TankDome")
    i += 1
    xml += ktx_material(i, "horizontal tank", tank_images, "GEE_HTank")
    i += 1
    xml += ktx_material(i, "tent", facade_assets.facade("tent", (236, 236, 232), (226, 226, 222), 200, 0, 11), "GEE_Tent")
    i += 1
    # Low metal roof of the launch complex halls (Buildings.MetalRoofVariant).
    xml += ktx_material(i, "roof metal", facade_assets.metal_roof(0), gable_mesh)
    i += 1
    # The swinging crew access arm (Buildings.CrewArmVariant), with the fixed service structure's palette.
    xml += [f'  <!-- variant {i}: LC-39A crew access arm -->',
            f'  <StaticObject Id="GEE_Box{i}">',
            f'    <PartModel Id="GEE_Box{i}_Model">',
            '      <Mesh Id="GEE_CrewArm" />',
            '      <Material Id="GEE_Fss_Material" />',
            '    </PartModel>',
            '  </StaticObject>']
    i += 1
    # Road surfaces after the crew arm, in the order of Buildings.RoadPlainVariant, HighwayVariant, StreetVariant
    # and RampVariant; Poly Haven scans like the road (variant 8).
    for profile in ("road plain", "highway", "street", "ramp"):
        xml += ktx_material(i, profile, road_profile_images(profile), "GEE_ScannedSlab")
        i += 1
    # Tracks and unpaved roads (Buildings.TrackVariant).
    xml += ktx_material(i, "track", road_profile_images("track"), "GEE_ScannedSlab")
    i += 1
    # Lane-count highway variants (Buildings.Highway3Variant, Highway4Variant), after the track.
    for profile in ("highway 3", "highway 4"):
        xml += ktx_material(i, profile, road_profile_images(profile), "GEE_ScannedSlab")
        i += 1
    # Ramp surface with gore hatching (Buildings.GoreVariant), after the lane-count variants.
    xml += ktx_material(i, "gore", road_profile_images("gore"), "GEE_ScannedSlab")
    i += 1
    # White paint slab for junction markings (Buildings.JunctionPaintVariant), before the furniture.
    xml += ktx_material(i, "junction paint", road_profile_images("paint"), "GEE_ScannedSlab")
    i += 1
    # Hip roofs (Buildings.FirstHipVariant): shape-major, roof colour-minor, with their own copies of the gable roofs' textures (sharing files broke the game's texture binding).
    for shape in range(len(HIP_INSETS)):
        for k, (name, _, _) in enumerate(ROOFS):
            tile, wall = ROOFS[k][1], ROOFS[k][2]
            xml += ktx_material(i, f"hip {shape} {name}", facade_assets.gable_roof(tile, wall, k), f"GEE_Hip{shape}")
            i += 1
    # More facade colours (Buildings.ExtraFacades), before the furniture.
    for k, (name, facade, roof, rough, metal) in enumerate(EXTRA_FACADES):
        xml += ktx_material(i, name, facade_assets.facade(name, facade, roof, rough, metal, len(VARIANTS) + k), box_mesh)
        i += 1
    # House relief (Buildings.TrimVariant ... FirstGarageVariant, HouseDetail.cs), before the furniture: trim box,
    # bargeboard, dormer bodies, front doors, garage doors; own texture files each (house_assets.py).
    house = [("trim", house_assets.trim(0), box_mesh), ("barge", house_assets.trim(1), "GEE_Barge")]
    house += [(f"dormer {w[0]}", house_assets.dormer_images(k), "GEE_Dormer") for k, w in enumerate(house_assets.DORMER_WALLS)]
    house += [(f"door {d[0]}", house_assets.door_images(k), box_mesh) for k, d in enumerate(house_assets.DOOR_LEAVES)]
    house += [(f"garage {g[0]}", house_assets.garage_images(k), box_mesh) for k, g in enumerate(house_assets.GARAGE_BODIES)]
    for name, images, mesh in house:
        xml += ktx_material(i, name, images, mesh)
        i += 1
    # Roadside furniture (Buildings.FurnitureVariant: lamp, sign, gantry), after the extra facades.
    xml += furniture_assets.write(MOD_DIR, i, landmark_assets.write_ktx2)
    i += len(furniture_assets.MODELS)
    xml.append("</Assets>")
    with open(os.path.join(MOD_DIR, "Assets.xml"), "w", encoding="utf-8", newline="\n") as f:
        import port_xml  # KSA 2026.11.10 asset format
        f.write(port_xml.convert_text("\n".join(xml) + "\n"))
    print(f"wrote assets for {i} variants to {MOD_DIR}")


def ktx_material(i, name, images, mesh):
    """Variant i with mipmapped KTX2 textures (diffuse, AoRoughMetal); returns its Assets.xml lines."""
    for kind, img in zip(("Diffuse", "PBR"), images):
        landmark_assets.write_ktx2(os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_{kind}.ktx2"), img)
        stale = os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_{kind}.png")
        if os.path.exists(stale):
            os.remove(stale)
    return [line.replace("_Diffuse.png", "_Diffuse.ktx2").replace("_PBR.png", "_PBR.ktx2") for line in material_xml(i, name, mesh)]


def material_xml(i, name, mesh, texture_index=None):
    """Material and static object of variant i; texture_index reuses another variant's KTX2 textures (hip roofs)."""
    t = i if texture_index is None else texture_index
    ext = "png" if texture_index is None else "ktx2"
    return [f'  <!-- variant {i}: {name} -->',
            f'  <PbrMaterial Id="GEE_V{i}_Material">',
            f'    <Diffuse Path="Textures/GEE_V{t}_Diffuse.{ext}" Category="Vessel" />',
            '    <Normal Path="Textures/GEE_Normal.ktx2" Category="Vessel" />',
            f'    <AoRoughMetal Path="Textures/GEE_V{t}_PBR.{ext}" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="GEE_Box{i}">',
            f'    <PartModel Id="GEE_Box{i}_Model">',
            f'      <Mesh Id="{mesh}" />',
            f'      <Material Id="GEE_V{i}_Material" />',
            '    </PartModel>',
            '  </StaticObject>']


def material(i, name, diffuse, pbr, mesh):
    """Writes the textures of variant i; returns its material and static object for Assets.xml."""
    write_png(os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_Diffuse.png"), TEX_W, TEX_H, diffuse)
    write_png(os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_PBR.png"), TEX_W, TEX_H, pbr)
    return [f'  <!-- variant {i}: {name} -->',
            f'  <PbrMaterial Id="GEE_V{i}_Material">',
            f'    <Diffuse Path="Textures/GEE_V{i}_Diffuse.png" Category="Vessel" />',
            '    <Normal Path="Textures/GEE_Normal.ktx2" Category="Vessel" />',
            f'    <AoRoughMetal Path="Textures/GEE_V{i}_PBR.png" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="GEE_Box{i}">',
            f'    <PartModel Id="GEE_Box{i}_Model">',
            f'      <Mesh Id="{mesh}" />',
            f'      <Material Id="GEE_V{i}_Material" />',
            '    </PartModel>',
            '  </StaticObject>']


if __name__ == "__main__":
    main()
