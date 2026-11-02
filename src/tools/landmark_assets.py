"""Models and facade textures of the landmarks (Landmarks.All in Landmarks.cs), used by make_assets.py.

The Vehicle Assembly Building is a box with one texture atlas: its four facades and the roof, painted after
photos of the building (the flag on the south face, the stepped transfer aisle door in
the grey middle of the south and north faces, two high bay doors on the east and west faces). The atlas is
written as KTX2 with mipmaps; the game loads PNGs without mipmaps, which shimmer at a distance.

Needs Pillow and numpy.
"""
import math
import os
import random
import struct
import zlib

import numpy as np
import structure_assets
from PIL import Image, ImageDraw

# Box of the high bay, as in Landmarks.cs: A runs west to east along the south face, B south to north.
VAB_A, VAB_B = 157.16, 136.82
# The box reaches about 4 m into the ground (Settings.FoundationMeters), so the texture spans 164 m.
TEX_H_M, GROUND_M = 164.0, 160.0

ATLAS = 2048
FACE_W, FACE_H = 1024, 896          # four facades in a 2 x 2 grid, the roof in the strip below
ROOF_Y = 2 * FACE_H
SS = 3                              # supersampling while painting

WHITE = (226, 227, 224)
MID_GREY = (112, 115, 120)
BAY_GREY = (140, 143, 149)
DOOR_FRAME = (220, 221, 218)
DOOR_PANEL = (188, 198, 191)
DOOR_DARK = (98, 101, 106)
UPPER_DOOR = (126, 129, 135)
JOINT = (88, 91, 96)
ROOF = (128, 128, 124)
FLAG_RED, FLAG_WHITE, FLAG_BLUE = (178, 34, 52), (238, 238, 235), (60, 59, 110)


class Face:
    """One facade, painted in meters: x from the left edge as seen from outside, y down from the roof edge."""

    def __init__(self, width_m, height_m=TEX_H_M, size=(FACE_W, FACE_H), base=WHITE):
        self.size = size
        self.img = Image.new("RGB", (size[0] * SS, size[1] * SS), base)
        self.draw = ImageDraw.Draw(self.img)
        self.sx = size[0] * SS / width_m
        self.sy = size[1] * SS / height_m

    def p(self, x, y):
        return x * self.sx, y * self.sy

    def rect(self, x0, y0, x1, y1, colour):
        self.draw.rectangle([x0 * self.sx, y0 * self.sy, x1 * self.sx - 1, y1 * self.sy - 1], fill=colour)

    def polygon(self, points, colour):
        self.draw.polygon([self.p(x, y) for x, y in points], fill=colour)

    def ellipse(self, cx, cy, rx, ry, colour):
        self.draw.ellipse([(cx - rx) * self.sx, (cy - ry) * self.sy, (cx + rx) * self.sx, (cy + ry) * self.sy], fill=colour)

    def result(self):
        return self.img.resize(self.size, Image.LANCZOS)


def star(face, cx, cy, r, colour):
    points = []
    for i in range(10):
        a = -math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.382
        points.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    face.polygon(points, colour)


def flag(face, x0, y0, w=33.53):
    """The painted flag hangs vertically: union at the top, to the left; the leftmost stripe is the top stripe."""
    h = 1.9 * w
    stripe = w / 13
    for i in range(13):
        face.rect(x0 + i * stripe, y0, x0 + (i + 1) * stripe, y0 + h, FLAG_RED if i % 2 == 0 else FLAG_WHITE)
    face.rect(x0, y0, x0 + 7 * stripe, y0 + 0.76 * w, FLAG_BLUE)
    for row in range(9):
        for col in range(11):
            if (row + col) % 2 == 0:
                star(face, x0 + (row + 1) * 0.054 * w, y0 + (col + 1) * 0.063 * w, 0.0308 * w, FLAG_WHITE)


def windows(face, rng, spans, count):
    """A few small dark openings in the white wall strips."""
    for _ in range(count):
        x0, x1 = spans[rng.randrange(len(spans))]
        x = rng.uniform(x0 + 2, x1 - 4)
        y = rng.uniform(30, 150)
        face.rect(x, y, x + 1.5, y + 1.5, (70, 72, 76))


def transfer_aisle_face(width, rng, south):
    """South or north face: white sides, grey middle with the stepped transfer aisle door."""
    f = Face(width)
    mid0, mid1 = 48.6, 112.4
    f.rect(mid0, 0, mid1, TEX_H_M, MID_GREY)
    f.rect(58.6, 23.5, 101.5, 78, DOOR_FRAME)          # upper door: frame and panels
    f.rect(65.1, 34.8, 95.2, 78, DOOR_PANEL)
    f.rect(56.6, 78, 110.9, TEX_H_M, DOOR_FRAME)       # lower door, wider
    f.rect(64.7, 84.3, 103.1, TEX_H_M, DOOR_PANEL)
    f.rect(72.0, 123, 94.7, TEX_H_M, DOOR_DARK)
    for y in np.arange(37.8, 78, 3.0):                 # panel rows of the doors
        f.rect(65.1, y, 95.2, y + 0.25, (172, 181, 175))
    for y in np.arange(87.3, 123, 3.0):
        f.rect(64.7, y, 103.1, y + 0.25, (172, 181, 175))
    if south:
        flag(f, 24.3 - 33.53 / 2, 5.5)
    windows(f, rng, [(2, mid0 - 2), (mid1 + 2, width - 2)], 4)
    return f.result()


def high_bay_face(width, rng):
    """East or west face: two high bay doors, 139 m tall; the upper door 23 m wide in seven lift panels,
    the lower one 46 m wide and 35 m tall in four sliding panels."""
    f = Face(width)
    bay_w, door_top, split = 33.0, GROUND_M - 139, GROUND_M - 35
    for centre in (0.29 * width, 0.71 * width):
        f.rect(centre - bay_w / 2, 0, centre + bay_w / 2, TEX_H_M, BAY_GREY)
        f.rect(centre - 25, split - 4, centre + 25, TEX_H_M, BAY_GREY)
        f.rect(centre - 12.6, door_top - 1, centre + 12.6, split, (172, 175, 180))
        f.rect(centre - 11.6, door_top, centre + 11.6, split, UPPER_DOOR)
        for i in range(1, 7):
            y = door_top + i * (split - door_top) / 7
            f.rect(centre - 11.6, y - 0.35, centre + 11.6, y + 0.35, JOINT)
        f.rect(centre - 23.2, split, centre + 23.2, TEX_H_M, UPPER_DOOR)
        for i in range(1, 4):
            x = centre - 23.2 + i * 46.4 / 4
            f.rect(x - 0.3, split, x + 0.3, TEX_H_M, JOINT)
        f.rect(centre - 23.2, split, centre + 23.2, split + 0.6, JOINT)
    windows(f, rng, [(2, 0.29 * width - 19), (0.29 * width + 19, 0.71 * width - 19), (0.71 * width + 19, width - 2)], 6)
    return f.result()


def roof(rng):
    f = Face(VAB_A, VAB_B, (ATLAS, ATLAS - ROOF_Y), ROOF)
    for _ in range(40):                                # roof patches and plant
        x, y = rng.uniform(4, VAB_A - 12), rng.uniform(4, VAB_B - 12)
        w, h = rng.uniform(2, 8), rng.uniform(2, 8)
        g = rng.randrange(110, 175)
        f.rect(x, y, x + w, y + h, (g, g, g - 3))
    edge = 1.2
    for x0, y0, x1, y1 in ((0, 0, VAB_A, edge), (0, VAB_B - edge, VAB_A, VAB_B), (0, 0, edge, VAB_B), (VAB_A - edge, 0, VAB_A, VAB_B)):
        f.rect(x0, y0, x1, y1, (190, 190, 186))
    return f.result()


def panels(img, width_m, height_m, rng):
    """Aluminium wall panels, 14 by 6 ft: slight tone per panel and faint joints, also through the paint."""
    a = np.asarray(img).astype(np.float32)
    h, w = a.shape[:2]
    ys = np.arange(h)[:, None] * height_m / h
    xs = np.arange(w)[None, :] * width_m / w
    row, col = (ys // 1.83).astype(int), (xs // 4.27).astype(int)
    tones = 1 + 0.035 * (np.array([rng.random() for _ in range(64 * 128)]).reshape(64, 128) - 0.5)
    tone = tones[row % 64, col % 128]
    joint = np.where((ys % 1.83) < 0.12, 0.96, 1.0) * np.where((xs % 4.27) < 0.12, 0.97, 1.0)
    a *= (tone * joint)[:, :, None]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def vab_atlas():
    rng = random.Random(1966)
    atlas = Image.new("RGB", (ATLAS, ATLAS), WHITE)
    faces = [(transfer_aisle_face(VAB_A, rng, True), VAB_A, (0, 0)),
             (high_bay_face(VAB_B, rng), VAB_B, (FACE_W, 0)),
             (transfer_aisle_face(VAB_A, rng, False), VAB_A, (0, FACE_H)),
             (high_bay_face(VAB_B, rng), VAB_B, (FACE_W, FACE_H))]
    for img, width_m, pos in faces:
        atlas.paste(panels(img, width_m, TEX_H_M, rng), pos)
    atlas.paste(roof(rng), (0, ROOF_Y))
    return atlas


# UV rectangles (u0, v0, u1, v1) of the atlas regions; v grows downward from the image top.
def _region(x, y, w, h, inset=1.0):
    return ((x + inset) / ATLAS, (y + inset) / ATLAS, (x + w - inset) / ATLAS, (y + h - inset) / ATLAS)


VAB_REGIONS = {
    "south": _region(0, 0, FACE_W, FACE_H),
    "east": _region(FACE_W, 0, FACE_W, FACE_H),
    "north": _region(0, FACE_H, FACE_W, FACE_H),
    "west": _region(FACE_W, FACE_H, FACE_W, FACE_H),
    "roof": _region(0, ROOF_Y, ATLAS, ATLAS - ROOF_Y),
}


def atlas_box(regions):
    """Unit box like make_assets.unit_box (x up in [0, 1], y and z in [-0.5, 0.5]; y = AxisA = east,
    z = AxisB = north), with every face mapped to its own atlas region, u to the right as seen from outside."""
    lerp = lambda r, u, v: (r[0] + (r[2] - r[0]) * u, r[1] + (r[3] - r[1]) * v)
    faces = [
        ((1, 0, 0), [(1, y, z) for y, z in ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5))],
         lambda p: lerp(regions["roof"], p[1] + .5, .5 - p[2])),
        ((0, 1, 0), [(x, .5, z) for x, z in ((0, -.5), (0, .5), (1, .5), (1, -.5))],
         lambda p: lerp(regions["east"], p[2] + .5, 1 - p[0])),
        ((0, -1, 0), [(x, -.5, z) for x, z in ((0, -.5), (0, .5), (1, .5), (1, -.5))],
         lambda p: lerp(regions["west"], .5 - p[2], 1 - p[0])),
        ((0, 0, 1), [(x, y, .5) for x, y in ((0, -.5), (0, .5), (1, .5), (1, -.5))],
         lambda p: lerp(regions["north"], .5 - p[1], 1 - p[0])),
        ((0, 0, -1), [(x, y, -.5) for x, y in ((0, -.5), (0, .5), (1, .5), (1, -.5))],
         lambda p: lerp(regions["south"], p[1] + .5, 1 - p[0])),
    ]
    positions, normals, uvs, indices = [], [], [], []
    for n, quad, uv in faces:
        base = len(positions)
        for p in quad:
            positions.append(p)
            normals.append(n)
            uvs.append(uv(p))
        for tri in ((0, 1, 2), (0, 2, 3)):
            a, b, c = (quad[i] for i in tri)
            e1 = [b[k] - a[k] for k in range(3)]
            e2 = [c[k] - a[k] for k in range(3)]
            cross = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            if sum(cross[k] * n[k] for k in range(3)) < 0:  # counter-clockwise seen from outside
                tri = (tri[0], tri[2], tri[1])
            indices.extend(base + i for i in tri)
    return positions, normals, uvs, indices


def write_ktx2(path, image, deflate=True):
    """RGBA8 UNORM KTX2 with a full mip chain, zlib supercompressed unless deflate is False."""
    image = image.convert("RGBA")
    levels = [image]
    while levels[-1].size != (1, 1):
        w, h = levels[-1].size
        levels.append(levels[-1].resize((max(w // 2, 1), max(h // 2, 1)), Image.BOX))
    data = [zlib.compress(level.tobytes(), 9) if deflate else level.tobytes() for level in levels]

    n = len(levels)
    samples = b"".join(struct.pack("<IIII", (8 * i) | (7 << 16) | (channel << 24), 0, 0, 255)
                       for i, channel in enumerate((0, 1, 2, 15)))
    # Supercompressed data leaves bytesPlane0 unsized (0).
    block = struct.pack("<IIIIII", 0, 2 | ((24 + len(samples)) << 16), 1 | (1 << 8) | (1 << 16), 0, 0 if deflate else 4, 0) + samples
    dfd = struct.pack("<I", 4 + len(block)) + block
    dfd_offset = 80 + 24 * n
    offset = dfd_offset + len(dfd)
    level_index, chunks = [None] * n, []
    for i in reversed(range(n)):                      # smallest level first in the file
        pad = 0 if deflate else -offset % 4
        chunks.append(b"\0" * pad + data[i])
        offset += pad
        level_index[i] = struct.pack("<QQQ", offset, len(data[i]), len(levels[i].tobytes()))
        offset += len(data[i])

    header = bytes([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A])
    header += struct.pack("<9I", 37, 1, image.size[0], image.size[1], 0, 0, 1, n, 3 if deflate else 0)  # VK_FORMAT_R8G8B8A8_UNORM
    header += struct.pack("<IIIIQQ", dfd_offset, len(dfd), 0, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(header + b"".join(level_index) + dfd + b"".join(chunks))


# SpaceX LC-39A Horizontal Integration Facility (OSM way 475501780): A runs north, B west, so the long sides are
# the +B and -B faces. White corrugated cladding, a flag on both long sides (no company marks), a large door in
# each short end (photo: the hangar seen from the Kennedy visitor area).
HIF_A, HIF_B, HIF_H = 94.53, 47.81, 26.0
HIF_TEX_H = HIF_H + 4.0


def _flag_horizontal(face, x0, y0, w):
    h = w / 1.9
    stripe = h / 13
    for i in range(13):
        face.rect(x0, y0 + i * stripe, x0 + w, y0 + (i + 1) * stripe, FLAG_RED if i % 2 == 0 else FLAG_WHITE)
    face.rect(x0, y0, x0 + 0.4 * w, y0 + 7 * stripe, FLAG_BLUE)
    for row in range(5):
        for col in range(6):
            face.rect(x0 + (col + 0.5) * 0.4 * w / 6 - 0.15, y0 + (row + 0.5) * 7 * stripe / 5 - 0.15,
                      x0 + (col + 0.5) * 0.4 * w / 6 + 0.15, y0 + (row + 0.5) * 7 * stripe / 5 + 0.15, FLAG_WHITE)


def hif_long_face(width, logo_first, annex=True):
    """Long side: corrugated white wall, flag under the eave."""
    f = Face(width, HIF_TEX_H, base=(236, 237, 236))
    f.rect(0, HIF_TEX_H - 4.0, width, HIF_TEX_H, (212, 213, 212))          # soil line
    f.rect(0, 0, width, 1.0, (205, 207, 208))                                  # eave
    _flag_horizontal(f, (width - 12.0) / 2 + (8 if logo_first else -8), 6.2, 12.0)
    # Panel joints every 5 m, the lower annex (8 m) with its eave and a plainer, slightly darker wall.
    for k in range(1, int(width / 5)):
        f.rect(k * 5, 1.0, k * 5 + 0.1, HIF_TEX_H - 4.0 - 8.0, (206, 208, 210))
    annex_top = HIF_TEX_H - 4.0 - 8.0
    if not annex:
        for k in range(1, int(width / 5)):
            f.rect(k * 5, annex_top, k * 5 + 0.1, HIF_TEX_H - 4.0, (206, 208, 210))
        return f.result()
    f.rect(0, annex_top, width, annex_top + 0.5, (178, 182, 186))
    f.rect(0, annex_top + 0.5, width, HIF_TEX_H - 4.0, (226, 228, 228))
    for k in range(1, int(width / 4)):
        f.rect(k * 4, annex_top + 0.5, k * 4 + 0.08, HIF_TEX_H - 4.0, (200, 203, 205))
    for dx in (width * 0.22, width * 0.71):                                    # service doors
        f.rect(dx, HIF_TEX_H - 4.0 - 3.0, dx + 1.6, HIF_TEX_H - 4.0, (110, 114, 120))
    return f.result()


def hif_end_face(width):
    """Short end: a tall roll-up door in a recessed frame."""
    f = Face(width, HIF_TEX_H, base=(236, 237, 236))
    f.rect(0, HIF_TEX_H - 4.0, width, HIF_TEX_H, (212, 213, 212))
    f.rect(0, 0, width, 1.0, (205, 207, 208))
    cx = width / 2
    f.rect(cx - 16, 5, cx + 16, HIF_TEX_H - 4.0 + 0.5, (186, 190, 194))
    f.rect(cx - 15, 6, cx + 15, HIF_TEX_H - 4.0, (166, 170, 176))
    for i in range(1, 20):
        y = 6 + i * (HIF_TEX_H - 4.0 - 6) / 20
        f.rect(cx - 15, y - 0.12, cx + 15, y + 0.12, (140, 144, 150))
    return f.result()


def hif_atlas():
    rng = random.Random(2015)
    atlas = Image.new("RGB", (ATLAS, ATLAS), (236, 237, 236))

    def ribs(img, width_m):
        a = np.asarray(img).astype(np.float32)
        xs = np.arange(a.shape[1])[None, :] * width_m / a.shape[1]
        a *= np.where((xs % 0.55) < 0.14, 0.955, 1.0)[:, :, None]
        a *= (1 + 0.012 * np.array([rng.random() for _ in range(a.shape[1])]).reshape(1, -1, 1))
        return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))

    # Same layout as the VAB atlas: south = -B, east = +A, north = +B, west = -A.
    faces = [(hif_long_face(HIF_A, True), HIF_A, (0, 0)),
             (hif_end_face(HIF_B), HIF_B, (FACE_W, 0)),
             (hif_long_face(HIF_A, True, annex=False), HIF_A, (0, FACE_H)),
             (hif_end_face(HIF_B), HIF_B, (FACE_W, FACE_H))]
    for img, width_m, pos in faces:
        atlas.paste(ribs(img, width_m), pos)
    roof_img = Face(HIF_A, HIF_B, (ATLAS, ATLAS - ROOF_Y), (198, 200, 202))
    for k in range(1, 24):                                                    # standing seams
        roof_img.rect(HIF_A * k / 24 - 0.1, 0, HIF_A * k / 24 + 0.1, HIF_B, (176, 178, 181))
    roof_img.rect(0, 0, HIF_A, 0.8, (222, 223, 223))
    roof_img.rect(0, HIF_B - 0.8, HIF_A, HIF_B, (222, 223, 223))
    atlas.paste(roof_img.result(), (0, ROOF_Y))
    return atlas


def hif_mesh():
    """Hangar body: the high bay is set back from the -B wall and a low annex (8 m) runs along it, as in the photos.
    Unit box like atlas_box (x up over HIF_TEX_H, y along A, z along B); every face maps its part of the painted
    face (u across, v down from the roof edge), so the wall painting lines up with the geometry."""
    ground = 4.0
    xa = (ground + 8.0) / HIF_TEX_H                    # annex height
    annex_depth = 11.0
    zb = -0.5 + annex_depth / HIF_B                    # where the high bay starts
    R = VAB_REGIONS
    lerp = lambda r, u, v: (r[0] + (r[2] - r[0]) * u, r[1] + (r[3] - r[1]) * v)
    positions, normals, uvs, indices = [], [], [], []

    def quad(pts, n, uv):
        base = len(positions)
        for p in pts:
            positions.append(p)
            normals.append(n)
            uvs.append(uv(p))
        for tri in ((0, 1, 2), (0, 2, 3)):
            a, b, c = (pts[i] for i in tri)
            e1 = [b[k] - a[k] for k in range(3)]
            e2 = [c[k] - a[k] for k in range(3)]
            cross = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            if sum(cross[k] * n[k] for k in range(3)) < 0:
                tri = (tri[0], tri[2], tri[1])
            indices.extend(base + i for i in tri)

    south = lambda p: lerp(R["south"], p[1] + .5, 1 - p[0])
    north = lambda p: lerp(R["north"], .5 - p[1], 1 - p[0])
    east = lambda p: lerp(R["east"], p[2] + .5, 1 - p[0])
    west = lambda p: lerp(R["west"], .5 - p[2], 1 - p[0])
    roof = lambda p: lerp(R["roof"], p[1] + .5, .5 - p[2])
    # -B wall of the high bay above the annex, and the annex front wall (both from the painted -B face)
    quad([(xa, -.5, zb), (1, -.5, zb), (1, .5, zb), (xa, .5, zb)], (0, 0, -1), south)
    quad([(0, -.5, -.5), (xa, -.5, -.5), (xa, .5, -.5), (0, .5, -.5)], (0, 0, -1), south)
    # +B wall
    quad([(0, -.5, .5), (0, .5, .5), (1, .5, .5), (1, -.5, .5)], (0, 0, 1), north)
    # Annex and high bay roofs
    quad([(xa, -.5, -.5), (xa, .5, -.5), (xa, .5, zb), (xa, -.5, zb)], (1, 0, 0), roof)
    quad([(1, -.5, zb), (1, .5, zb), (1, .5, .5), (1, -.5, .5)], (1, 0, 0), roof)
    # The two ends: the high bay's end plus the annex's
    for y, n, uv in ((.5, (0, 1, 0), east), (-.5, (0, -1, 0), west)):
        quad([(0, y, zb), (1, y, zb), (1, y, .5), (0, y, .5)], n, uv)
        quad([(0, y, -.5), (xa, y, -.5), (xa, y, zb), (0, y, zb)], n, uv)
    # The door: a panel 30 m wide hinged 24 m up, swung out 9 m and down to 15 m (the photos show it open).
    hinge, low, out = (ground + 24.0) / HIF_TEX_H, (ground + 15.0) / HIF_TEX_H, 9.0 / HIF_A
    wz = 15.0 / HIF_B
    plain = lambda p: lerp(R["east"], 0.04 + 0.06 * (p[2] + .5), 0.06 + 0.1 * p[0])   # plain wall, varying UVs
    import math as _m
    ln = _m.hypot(9.0, 9.0)
    nx, ny = 9.0 / ln, 9.0 / ln
    for sign in (1, -1):
        n = (nx * sign, ny * sign, 0)
        quad([(hinge, .5, -wz), (hinge, .5, wz), (low, .5 + out, wz), (low, .5 + out, -wz)], n, plain)
    for z in (-wz, wz):
        quad([(hinge, .5, z), (low, .5 + out, z), (low, .5, z), (hinge, .5, z)], (0, 0, 1 if z > 0 else -1), plain)
    return positions, normals, uvs, indices


def meshes():
    """Meshes for the shared atlas GLB."""
    return ([("OsmBuildings_VabHighBay", atlas_box(VAB_REGIONS))] + structure_assets.meshes()
            + [("OsmBuildings_HifHangar", hif_mesh())])


def write(mod_dir, first_variant, write_png):
    """Writes the landmark textures; returns the Assets.xml lines. Variant numbers follow Landmarks.All."""
    tex = os.path.join(mod_dir, "Textures")
    write_ktx2(os.path.join(tex, "OsmBuildings_Vab_Diffuse.ktx2"), vab_atlas())
    # Painted aluminium: rough, not metallic.
    write_png(os.path.join(tex, "OsmBuildings_Vab_PBR.png"), 4, 4, lambda x, y: bytes((255, 205, 0, 255)))
    i = first_variant
    return [f'  <!-- variant {i}: landmark Vehicle Assembly Building -->',
            '  <PbrMaterial Id="OsmBuildings_Vab_Material">',
            '    <Diffuse Path="Textures/OsmBuildings_Vab_Diffuse.ktx2" Category="Vessel" />',
            '    <Normal Path="Textures/OsmBuildings_Normal.png" Category="Vessel" />',
            '    <AoRoughMetal Path="Textures/OsmBuildings_Vab_PBR.png" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="OsmBuildings_Box{i}">',
            f'    <PartModel Id="OsmBuildings_Box{i}_Model">',
            '      <Mesh Id="OsmBuildings_VabHighBay" />',
            '      <Material Id="OsmBuildings_Vab_Material" />',
            '    </PartModel>',
            '  </StaticObject>'] + structure_assets.write(mod_dir, first_variant + 1, write_png) + hif_xml(
        mod_dir, first_variant + 1 + len(structure_assets.TOWERS), write_png)


def hif_xml(mod_dir, i, write_png):
    tex = os.path.join(mod_dir, "Textures")
    write_ktx2(os.path.join(tex, "OsmBuildings_Hif_Diffuse.ktx2"), hif_atlas())
    write_png(os.path.join(tex, "OsmBuildings_Hif_PBR.png"), 4, 4, lambda x, y: bytes((255, 175, 70, 255)))
    return [f'  <!-- variant {i}: landmark SpaceX Horizontal Integration Facility -->',
            '  <PbrMaterial Id="OsmBuildings_Hif_Material">',
            '    <Diffuse Path="Textures/OsmBuildings_Hif_Diffuse.ktx2" Category="Vessel" />',
            '    <Normal Path="Textures/OsmBuildings_Normal.png" Category="Vessel" />',
            '    <AoRoughMetal Path="Textures/OsmBuildings_Hif_PBR.png" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="OsmBuildings_Box{i}">',
            f'    <PartModel Id="OsmBuildings_Box{i}_Model">',
            '      <Mesh Id="OsmBuildings_HifHangar" />',
            '      <Material Id="OsmBuildings_Hif_Material" />',
            '    </PartModel>',
            '  </StaticObject>']
