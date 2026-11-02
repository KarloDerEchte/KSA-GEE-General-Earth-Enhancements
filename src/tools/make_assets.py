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
import landmark_assets
import structure_assets

MOD_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# name, facade rgb, roof rgb, facade roughness, facade metalness
VARIANTS = [
    ("concrete", (178, 176, 170), (88, 88, 90), 225, 0),
    ("plaster", (222, 218, 208), (150, 70, 50), 230, 0),
    ("sand", (205, 185, 150), (130, 62, 45), 230, 0),
    ("brick", (150, 80, 60), (80, 78, 78), 235, 0),
    ("dark", (95, 98, 104), (70, 70, 72), 170, 20),
    ("glass", (110, 135, 160), (150, 150, 150), 80, 60),
    ("metal", (185, 190, 195), (160, 165, 170), 140, 120),
    ("ochre", (210, 170, 100), (140, 65, 45), 230, 0),
]

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


# Road surfaces from a scan (road, street, ramp, highway, junction pad): a square texture whose first SCAN_SPLIT
# columns are the slab sides and the rest its top, SCAN_GAP pixels of margin kept off the edges (GEE_ScannedSlab and
# GEE_RoadDisc map the same). AoRoughMetal at half size: roughness and AO need less detail than colour.
SCAN_SIZE, SCAN_PBR_SIZE, SCAN_SPLIT, SCAN_GAP = 2048, 1024, 128, 4
# The longest road piece (Placement.RoadPieceMeters): every dash period and the 2 m scan divide it, so the surface
# runs on seamlessly from piece to piece.
ROAD_PIECE, SCAN_METERS = 16.0, 2.0
POLYHAVEN_DIR = os.path.join(os.path.dirname(__file__), "data", "polyhaven")
SCAN_ASSET, SCAN_RESOLUTION = "asphalt_pit_lane", "8k"

# Markings, metres from the right edge (v = 0, the slab's -B side; right-hand traffic): centre, width and either
# None (solid) or (dash, period). Each profile is laid out for one width; narrower or wider roads stretch it.
ROAD_PROFILES = {
    # Primary, secondary, tertiary (Buildings.RoadVariant): edge lines and a dashed centre line.
    "road": (8.0, [(0.35, 0.12, None), (7.65, 0.12, None), (4.0, 0.12, (3.0, 8.0))]),
    # Minor, residential, service, track (Buildings.StreetVariant): no markings.
    "street": (6.0, []),
    # One-lane motorway and trunk links (Buildings.RampVariant): edge lines.
    "ramp": (6.0, [(0.6, 0.15, None), (5.5, 0.15, None)]),
    # One carriageway of a motorway or trunk (Buildings.HighwayVariant): 2.5 m hard shoulder, two 3.75 m lanes split
    # by a dashed line (6 m dash, 10 m gap), solid edge lines and a 0.75 m strip on the left.
    "highway": (11.0, [(2.5, 0.20, None), (10.25, 0.15, None), (6.25, 0.15, (6.0, 16.0))]),
}


def scan_uv():
    """u ranges of the top and the sides and v range of the top, as unit_box and road_disc take them."""
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


_scan_cache = []


def scanned_images(width, length, lines, wrap=True):
    """Road surface from the Poly Haven "Asphalt Pit Lane" scan (2 m square, 8k) at its real size: the top stands for
    width metres across and length along (tiled; with wrap the scan repeats exactly length / 2 m times so pieces
    join seamlessly). lines as in ROAD_PROFILES. Toned to a neutral grey asphalt."""
    import numpy as np
    from PIL import Image
    if not _scan_cache:
        _scan_cache.extend(polyhaven_maps(SCAN_ASSET, SCAN_RESOLUTION))
    diff, arm = _scan_cache
    n, s, g = SCAN_SIZE, SCAN_SPLIT, SCAN_GAP
    used_along, used_across = n - s - 2 * g, n - 2 * g
    tiles_along = length / SCAN_METERS
    tile_w = round(used_along / tiles_along)
    if wrap:
        assert tile_w * tiles_along == used_along
    tile_h = round(used_across / (width / SCAN_METERS))

    def region(img):
        a = np.asarray(img.resize((tile_w, tile_h), Image.LANCZOS), dtype="float32")
        big = np.tile(a, (n // tile_h + 2, (n - s) // tile_w + 2, 1))
        # The pattern starts at the first used column, so it wraps exactly from one piece to the next.
        off = (-g) % tile_w
        return big[:n, off:off + n - s].copy()

    rgb, pbr = region(diff), region(arm)
    # The scan is warm brown; real asphalt is a neutral dark grey with an albedo of about 0.1 (lighter read as
    # concrete paths in game).
    lum = rgb.mean(axis=-1, keepdims=True)
    rgb = lum + 0.35 * (rgb - lum)
    rgb *= 64 / rgb.mean()
    rows = np.arange(n, dtype="float32")[:, None]
    cols = np.arange(n - s, dtype="float32")[None, :]
    across_m = np.clip((rows - g) / (used_across - 1), 0, 1) * width
    along_m = (cols - g) / used_along * length
    px_across, px_along = used_across / width, used_along / length

    paint = np.zeros((n, n - s), dtype="float32")
    for centre, line_width, dash in lines:
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
    width, lines = ROAD_PROFILES[name]
    return scanned_images(width, ROAD_PIECE, lines)


def junction_pad_images():
    """The pad's top is mapped from above: about a 10 m pad, plain."""
    return scanned_images(10.0, 10.0, [], wrap=False)


# Gabled roofs (Buildings.FirstRoofVariant on in C#): name, tile rgb, gable wall rgb.
ROOFS = [
    ("tile red", (150, 66, 46), (214, 206, 192)),
    ("slate", (70, 73, 80), (200, 196, 188)),
    ("brown", (112, 76, 56), (220, 212, 196)),
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


def road_disc(segments=32, top=(0.53, 0.97), side=(0.03, 0.47), across=(0.03, 0.97)):
    """Round junction pad: a flat cylinder, x in [0, 1] up, radius 0.5 in y and z, no bottom face. All normals face
    up (as on the road slabs). The top is mapped from above into the texture's top range, the rim into its side
    range (as unit_box takes them)."""
    import math
    m = 0.03
    positions, normals, uvs, indices = [], [], [], []
    ring = [(0.5 * math.cos(2 * math.pi * k / segments), 0.5 * math.sin(2 * math.pi * k / segments)) for k in range(segments)]
    top_uv = lambda y, z: (top[0] + (top[1] - top[0]) * (y + .5), across[0] + (across[1] - across[0]) * (z + .5))
    centre = len(positions)
    positions.append((1, 0, 0))
    normals.append((1, 0, 0))
    uvs.append(top_uv(0, 0))
    for y, z in ring:
        positions.append((1, y, z))
        normals.append((1, 0, 0))
        uvs.append(top_uv(y, z))
    for k in range(segments):
        a, b = centre + 1 + k, centre + 1 + (k + 1) % segments
        # counter-clockwise seen from above (+x): (y, z) turns from k to k + 1 about +x
        indices += [centre, a, b]
    for k in range(segments):
        (y0, z0), (y1, z1) = ring[k], ring[(k + 1) % segments]
        base = len(positions)
        for (y, z), x in (((y0, z0), 0), ((y1, z1), 0), ((y1, z1), 1), ((y0, z0), 1)):
            positions.append((x, y, z))
            normals.append((1, 0, 0))
            uvs.append((side[0] + (side[1] - side[0]) * (k + (y == y1 and z == z1)) / segments, m + (1 - 2 * m) * (1 - x)))
        # outward: counter-clockwise seen from outside
        indices += [base, base + 1, base + 2, base, base + 2, base + 3]
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
               ("GEE_CrewArm", structure_assets.crew_arm()), ("GEE_RoadDisc", road_disc(**scan_uv())),
               ("GEE_ScannedSlab", unit_box(up_normals=True, **scan_uv()))]
              + landmark_assets.meshes())
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
    # Round asphalt pads over road junctions (Buildings.JunctionVariant): plain asphalt, ground surface markers.
    xml += ktx_material(i, "road junction", junction_pad_images(), "GEE_RoadDisc")
    i += 1
    # One-way motorway and trunk carriageways (Buildings.HighwayVariant), then streets and ramps (StreetVariant,
    # RampVariant), all from the Poly Haven scan like the road (variant 8).
    for profile in ("highway", "street", "ramp"):
        xml += ktx_material(i, profile, road_profile_images(profile), "GEE_ScannedSlab")
        i += 1
    xml.append("</Assets>")
    with open(os.path.join(MOD_DIR, "Assets.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(xml) + "\n")
    print(f"wrote assets for {i} variants to {MOD_DIR}")


def ktx_material(i, name, images, mesh):
    """Variant i with mipmapped KTX2 textures (diffuse, AoRoughMetal); returns its Assets.xml lines."""
    for kind, img in zip(("Diffuse", "PBR"), images):
        landmark_assets.write_ktx2(os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_{kind}.ktx2"), img)
        stale = os.path.join(MOD_DIR, "Textures", f"GEE_V{i}_{kind}.png")
        if os.path.exists(stale):
            os.remove(stale)
    return [line.replace("_Diffuse.png", "_Diffuse.ktx2").replace("_PBR.png", "_PBR.ktx2") for line in material_xml(i, name, mesh)]


def material_xml(i, name, mesh):
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
