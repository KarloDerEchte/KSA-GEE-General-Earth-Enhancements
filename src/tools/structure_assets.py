"""Steel towers (the launch towers and the water tower of LC-39A) and round tanks, used by make_assets.py and landmark_assets.py.

Meshes live in the unit box of the other building meshes: x up in [0, 1], y (AxisA) and z (AxisB) in [-0.5, 0.5],
so a model is scaled by its landmark's length, width and height. Towers are built in metres and divided by
those dimensions, so beams keep their metric thickness. Parts may reach outside the box (the crew access arm).
Colours follow photos of the pad (Wikimedia Commons: Crew-2/Demo-2 launch, Falcon Heavy Roman, LC-39A from the
Visitor Complex): the fixed service structure is black steel with white corner strips and a white crew arm, the
Starship integration tower silver.
"""
import math
import os

GROUND_M = 4.0          # the box reaches about 4 m into the ground (Settings.FoundationMeters)

# Palettes: four swatches (colour, roughness, metalness) in a 16 x 4 texture; a part uses its swatch's centre.
FSS_PALETTE = [((44, 46, 50), 150, 10), ((34, 35, 38), 180, 0), ((232, 232, 228), 170, 0), ((26, 32, 42), 60, 0)]
TOWER_PALETTE = [((176, 180, 186), 150, 25), ((205, 207, 210), 150, 10), ((232, 232, 228), 170, 0), ((196, 44, 36), 160, 20)]
WATER_PALETTE = [((238, 238, 236), 150, 0), ((226, 226, 224), 150, 0), ((196, 198, 200), 160, 10), ((205, 52, 44), 150, 0)]


def sw(i):
    return ((i + 0.5) / 4, 0.5)


STEEL, CLAD, WHITE, DARK = sw(0), sw(1), sw(2), sw(3)


class Mesh:
    def __init__(self):
        self.positions, self.normals, self.uvs, self.indices = [], [], [], []

    def quad(self, p, n, uv):
        base = len(self.positions)
        # A small UV rectangle inside the swatch (its colour is uniform): a constant UV would leave the engine no
        # tangents for the normal map, and such faces came out dark blue.
        corners = ((-0.08, -0.3), (0.08, -0.3), (0.08, 0.3), (-0.08, 0.3))
        for q, (du, dv) in zip(p, corners):
            self.positions.append(q)
            self.normals.append(n)
            self.uvs.append((uv[0] + du, uv[1] + dv))
        # counter-clockwise seen from outside (along n)
        a, b, c = p[0], p[1], p[2]
        e1 = [b[k] - a[k] for k in range(3)]
        e2 = [c[k] - a[k] for k in range(3)]
        cross = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
        order = (0, 1, 2, 0, 2, 3) if sum(cross[k] * n[k] for k in range(3)) >= 0 else (0, 2, 1, 0, 3, 2)
        self.indices.extend(base + i for i in order)


def _sub(a, b): return tuple(a[k] - b[k] for k in range(3))
def _add(a, b): return tuple(a[k] + b[k] for k in range(3))
def _mul(a, s): return tuple(a[k] * s for k in range(3))
def _cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _unit(a):
    n = math.sqrt(sum(c * c for c in a))
    return tuple(c / n for c in a)


def beam(mesh, p0, p1, thickness, uv):
    """Square-section beam from p0 to p1 (metres, coordinates (up, A, B))."""
    d = _unit(_sub(p1, p0))
    ref = (1, 0, 0) if abs(d[0]) < 0.9 else (0, 1, 0)
    u = _unit(_cross(d, ref))
    v = _unit(_cross(d, u))
    h = thickness / 2
    corners = [_add(_mul(u, su * h), _mul(v, sv * h)) for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    for i in range(4):
        a, b = corners[i], corners[(i + 1) % 4]
        n = _unit(_add(a, b))
        mesh.quad([_add(p0, a), _add(p0, b), _add(p1, b), _add(p1, a)], n, uv)
    mesh.quad([_add(p0, c) for c in corners], _mul(d, -1), uv)
    mesh.quad([_add(p1, c) for c in corners], d, uv)


def box(mesh, lo, hi, uv):
    """Axis-aligned box (up, A, B) from lo to hi."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    mesh.quad([(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)], (1, 0, 0), uv)
    mesh.quad([(x0, y0, z0), (x0, y0, z1), (x0, y1, z1), (x0, y1, z0)], (-1, 0, 0), uv)
    mesh.quad([(x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)], (0, 1, 0), uv)
    mesh.quad([(x0, y0, z0), (x0, y0, z1), (x1, y0, z1), (x1, y0, z0)], (0, -1, 0), uv)
    mesh.quad([(x0, y0, z1), (x0, y1, z1), (x1, y1, z1), (x1, y0, z1)], (0, 0, 1), uv)
    mesh.quad([(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)], (0, 0, -1), uv)


def revolve(mesh, profile, uv, segments=32):
    """Surface of revolution around the up axis from (height, radius) points, smooth around the axis."""
    for (h0, r0), (h1, r1) in zip(profile, profile[1:]):
        r1 = max(r1, 0.01)
        slope = (r0 - r1, h1 - h0)                 # outward normal in the (up, radial) plane
        ln = math.hypot(*slope)
        nu, nr = slope[0] / ln, slope[1] / ln
        for i in range(segments):
            a0, a1 = 2 * math.pi * i / segments, 2 * math.pi * (i + 1) / segments
            pts = [(h0, r0, a0), (h0, r0, a1), (h1, r1, a1), (h1, r1, a0)]
            base = len(mesh.positions)
            for (h, r, a), (du, dv) in zip(pts, ((-0.08, -0.3), (0.08, -0.3), (0.08, 0.3), (-0.08, 0.3))):
                mesh.positions.append((h, r * math.cos(a), r * math.sin(a)))
                mesh.normals.append((nu, nr * math.cos(a), nr * math.sin(a)))
                mesh.uvs.append((uv[0] + du, uv[1] + dv))
            am = (a0 + a1) / 2
            face_n = (nu, nr * math.cos(am), nr * math.sin(am))
            p = mesh.positions[base:base + 4]
            e1 = _sub(p[1], p[0])
            e2 = _sub(p[2], p[0])
            cross = _cross(e1, e2)
            order = (0, 1, 2, 0, 2, 3) if sum(cross[k] * face_n[k] for k in range(3)) >= 0 else (0, 2, 1, 0, 3, 2)
            mesh.indices.extend(base + k for k in order)


def frame_levels(m, hw, hd, leg, top, panels, brace, uv, every=2):
    """Legs, ring beams, X bracing and platforms of a four-sided steel tower up to `top`."""
    for sa in (-1, 1):
        for sb in (-1, 1):
            beam(m, (0, sa * (hw - leg / 2), sb * (hd - leg / 2)), (top, sa * (hw - leg / 2), sb * (hd - leg / 2)), leg, uv)
    corners = [(-hw + leg / 2, -hd + leg / 2), (hw - leg / 2, -hd + leg / 2), (hw - leg / 2, hd - leg / 2), (-hw + leg / 2, hd - leg / 2)]
    step = top / panels
    for level in range(panels + 1):
        h = level * step
        for (a0, b0), (a1, b1) in zip(corners, corners[1:] + corners[:1]):
            beam(m, (h, a0, b0), (h, a1, b1), brace, uv)
        if level < panels:
            h1 = (level + 1) * step
            for k, ((a0, b0), (a1, b1)) in enumerate(zip(corners, corners[1:] + corners[:1])):
                beam(m, (h, a0, b0), (h1, a1, b1), brace, uv)
                if (level + k) % 2 == 0:
                    beam(m, (h, a1, b1), (h1, a0, b0), brace, uv)
        if level >= 1 and level % every == 0 and 0 < h < top:
            box(m, (h - 0.15, -hw + leg, -hd + leg), (h + 0.15, hw - leg, hd - leg), uv)


def fixed_service_structure(width, depth, height):
    """The launch complex 39 fixed service structure, from NASA's free "Gantry" model (NASA / Michael D. Carbajal,
    science.nasa.gov/3d-resources/gantry, public NASA media): the lattice tower with its elevator core, platforms,
    ladders and lightning mast, cut out of the shuttle-era pad model (data/fss_nasa.npy: world-space triangles,
    1 unit about 5.7 m). SpaceX's white crew access arm is added on the pad side (-B, east) 70 m up."""
    import numpy as np
    tris = np.load(os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "fss_nasa.npy")).astype(np.float64)
    total = height + GROUND_M
    y0, y1 = tris[:, :, 1].min(), tris[:, :, 1].max()
    scale = total / (y1 - y0)
    xc = (-0.26 + 1.81) / 2
    zc = (4.065 + 6.042) / 2
    mast_from = y0 + (y1 - y0) * 0.66
    m = Mesh()
    for t in tris:
        p = [((v[1] - y0) * scale, -(v[2] - zc) * scale, -(v[0] - xc) * scale) for v in t]
        n = _cross(_sub(p[1], p[0]), _sub(p[2], p[0]))
        length = math.sqrt(sum(c * c for c in n))
        if length < 1e-9:
            continue
        n = tuple(c / length for c in n)
        area = length / 2
        if t[:, 1].mean() > mast_from:
            uv = WHITE
        elif area > 3.0 and abs(n[0]) < 0.3:
            uv = CLAD
        else:
            uv = STEEL
        base = len(m.positions)
        m.positions.extend(p)
        m.normals.extend([n] * 3)
        m.uvs.extend([(uv[0] - 0.08, uv[1] - 0.3), (uv[0] + 0.08, uv[1] - 0.3), (uv[0], uv[1] + 0.3)])
        m.indices.extend((base, base + 1, base + 2))
    # The crew access arm is its own model (crew_arm), so it can swing; see CrewArm.cs.
    return m, total


def truss_arm(m, base_h, a_center, a_width, b_from, reach, depth_h, uv, brace=0.35, sloped=True):
    """Cantilever truss along +B: top and bottom chords, verticals and diagonals every ~2.4 m, a deck on top and,
    if sloped, a strut from the tower below back up to the tip (the catch and service arms of the tower)."""
    a0, a1 = a_center - a_width / 2, a_center + a_width / 2
    n = max(int(reach / 2.4), 2)
    step = reach / n
    for a in (a0, a1):
        beam(m, (base_h, a, b_from), (base_h, a, b_from + reach), 0.5, uv)                      # bottom chord
        beam(m, (base_h + depth_h, a, b_from), (base_h + depth_h, a, b_from + reach), 0.5, uv)  # top chord
        for k in range(n + 1):
            b = b_from + k * step
            beam(m, (base_h, a, b), (base_h + depth_h, a, b), brace, uv)
            if k < n:
                if k % 2 == 0:
                    beam(m, (base_h, a, b), (base_h + depth_h, a, b + step), brace, uv)
                else:
                    beam(m, (base_h + depth_h, a, b), (base_h, a, b + step), brace, uv)
        if sloped:
            beam(m, (base_h - depth_h * 2.6, a, b_from), (base_h, a, b_from + reach * 0.82), 0.5, uv)
    for k in range(0, n + 1, 2):                                                                   # cross beams
        b = b_from + k * step
        beam(m, (base_h + depth_h, a0, b), (base_h + depth_h, a1, b), 0.35, uv)
    box(m, (base_h + depth_h, a0, b_from), (base_h + depth_h + 0.15, a1, b_from + reach), uv)      # deck


def crawler_crane(m, a0, b0, boom, angle_deg):
    """Large crawler crane: tracks, a red superstructure and a lattice boom raised toward +A, with a hook line."""
    red = DARK
    for sb in (-1, 1):
        box(m, (0, a0 - 7.0, b0 + sb * 4.5 - 1.2), (GROUND_M + 1.8, a0 + 7.0, b0 + sb * 4.5 + 1.2), CLAD)   # tracks
    box(m, (GROUND_M + 1.8, a0 - 6.0, b0 - 3.5), (GROUND_M + 6.5, a0 + 4.0, b0 + 3.5), red)             # house
    box(m, (GROUND_M + 1.8, a0 - 9.0, b0 - 3.0), (GROUND_M + 5.0, a0 - 6.0, b0 + 3.0), CLAD)            # counterweight
    t = math.radians(angle_deg)
    foot = (GROUND_M + 5.5, a0 + 3.0)
    tip = (foot[0] + boom * math.sin(t), foot[1] + boom * math.cos(t))
    half = 1.4
    chords = [(sa, sb) for sa in (-1, 1) for sb in (-1, 1)]
    n = int(boom / 6)
    for sa, sb in chords:
        taper = lambda s: half * (1 - 0.55 * s)
        beam(m, (foot[0], foot[1] + sa * half, b0 + sb * half), (tip[0], tip[1] + sa * taper(1), b0 + sb * taper(1)), 0.35, red)
    for k in range(n + 1):
        s = k / n
        h = foot[0] + (tip[0] - foot[0]) * s
        a = foot[1] + (tip[1] - foot[1]) * s
        w = half * (1 - 0.55 * s)
        ring = [(a - w, b0 - w), (a + w, b0 - w), (a + w, b0 + w), (a - w, b0 + w)]
        for (pa, pb), (qa, qb) in zip(ring, ring[1:] + ring[:1]):
            beam(m, (h, pa, pb), (h, qa, qb), 0.18, red)
        if k < n:
            s1 = (k + 1) / n
            h1 = foot[0] + (tip[0] - foot[0]) * s1
            a1 = foot[1] + (tip[1] - foot[1]) * s1
            w1 = half * (1 - 0.55 * s1)
            for sb in (-1, 1):
                beam(m, (h, a - w, b0 + sb * w), (h1, a1 + w1, b0 + sb * w1), 0.15, red)
    beam(m, (tip[0], tip[1], b0), (tip[0] - 60.0, tip[1], b0), 0.12, CLAD)                          # hook line
    box(m, (tip[0] - 62.0, tip[1] - 0.8, b0 - 0.8), (tip[0] - 60.0, tip[1] + 0.8, b0 + 0.8), red)   # hook block


def integration_tower(width, depth, height, olm=True, crane=True):
    """Starship integration tower after the photos of the tower at LC-39A and Starbase: four chords, dense ring
    beams every 3.6 m, X bracing every 7.2 m on all faces, platforms every 21.6 m, a clad base, two catch arms
    (trusses with a strut) near the top and a lower service arm toward the pad (+B), a machine house and a mast."""
    total = height + GROUND_M
    m = Mesh()
    hw, hd = width / 2, depth / 2
    chord = 0.9
    top = total - 7.0
    ring = 3.6
    rings = int(top / ring)
    pitch = top / rings
    c = [(-hw + chord / 2, -hd + chord / 2), (hw - chord / 2, -hd + chord / 2), (hw - chord / 2, hd - chord / 2), (-hw + chord / 2, hd - chord / 2)]
    for a, b in c:
        beam(m, (0, a, b), (top, a, b), chord, STEEL)
    for k in range(rings + 1):
        h = k * pitch
        for (a0, b0), (a1, b1) in zip(c, c[1:] + c[:1]):
            beam(m, (h, a0, b0), (h, a1, b1), 0.32, STEEL)
        if k < rings and k % 2 == 0:
            h1 = (k + 2) * pitch if k + 2 <= rings else top
            for (a0, b0), (a1, b1) in zip(c, c[1:] + c[:1]):
                beam(m, (h, a0, b0), (h1, a1, b1), 0.28, STEEL)
                beam(m, (h, a1, b1), (h1, a0, b0), 0.28, STEEL)
        if k > 0 and k % 6 == 0 and k * pitch < top:
            box(m, (h - 0.12, -hw + chord, -hd + chord), (h + 0.12, hw - chord, hd - chord), STEEL)
    # Clad base and an elevator shaft up the whole height on the side away from the pad.
    for sa, sb, lo, hi in ((0, -1, (-hw + chord, -hd + 0.2), (hw - chord, -hd + 0.6)), (0, 1, (-hw + chord, hd - 0.6), (hw - chord, hd - 0.2)),
                           (-1, 0, (-hw + 0.2, -hd + chord), (-hw + 0.6, hd - chord)), (1, 0, (hw - 0.6, -hd + chord), (hw - 0.2, hd - chord))):
        box(m, (GROUND_M * 0.5, lo[0], lo[1]), (24.0, hi[0], hi[1]), CLAD)
    box(m, (GROUND_M * 0.5, hw - 3.4, -hd + 0.9), (top - 2.0, hw - 0.9, -hd + 3.2), CLAD)
    # Catch arms (two chopsticks) near the top, a service arm lower down.
    truss_arm(m, 0.86 * total, 0.0, 2 * (hw * 0.6), hd, 23.0, 4.2, STEEL)
    for a in (-hw * 0.6, hw * 0.6):
        pass
    box(m, (0.86 * total + 4.2, -hw * 0.6 - 0.9, hd + 22.0), (0.86 * total + 4.8, hw * 0.6 + 0.9, hd + 23.0), CLAD)
    truss_arm(m, 0.60 * total, 0.0, 3.2, hd, 17.0, 3.2, STEEL, sloped=True)
    # Orbital launch mount: a dark table on six legs between the tower and the pad, with the launch deck on top.
    b0 = hd + 30.0
    if olm:
        for a in (-8.0, 0.0, 8.0):
            for b in (b0 - 8.0, b0 + 8.0):
                box(m, (0, a - 1.2, b - 1.2), (GROUND_M + 7.0, a + 1.2, b + 1.2), CLAD)
        box(m, (GROUND_M + 7.0, -11.0, b0 - 11.0), (GROUND_M + 10.0, 11.0, b0 + 11.0), CLAD)
        box(m, (GROUND_M + 10.0, -6.5, b0 - 6.5), (GROUND_M + 11.2, 6.5, b0 + 6.5), STEEL)
    if crane:
        crawler_crane(m, -hw - 38.0, 0.0, 150.0, 81.0)   # boom tip about 15 m clear of the tower, above its top
    # Machine house, hoist beam and mast.
    box(m, (top, -hw + 0.3, -hd + 0.3), (top + 5.0, hw - 0.3, hd - 0.3), CLAD)
    beam(m, (top + 5.0, 0, 0), (total, 0, 0), 0.5, WHITE)
    beam(m, (top + 2.5, -hw, 0), (top + 2.5, hw + 8.0, 0), 0.6, STEEL)
    return m, total


def water_tower(width, depth, height):
    """Elevated water tank on four braced legs, white, dome-topped (the one beside the fixed service structure)."""
    total = height + GROUND_M
    m = Mesh()
    base, topspan = 8.0, 5.5
    tank_bottom = total - 24.0
    for sa in (-1, 1):
        for sb in (-1, 1):
            beam(m, (0, sa * base, sb * base), (tank_bottom, sa * topspan, sb * topspan), 1.0, WHITE)
    levels = int(tank_bottom // 11)
    for k in range(1, levels + 1):
        h = tank_bottom * k / (levels + 1)
        span = base + (topspan - base) * h / tank_bottom
        corners = [(-span, -span), (span, -span), (span, span), (-span, span)]
        for (a0, b0), (a1, b1) in zip(corners, corners[1:] + corners[:1]):
            beam(m, (h, a0, b0), (h, a1, b1), 0.45, WHITE)
        h1 = tank_bottom * (k + 1) / (levels + 1)
        span1 = base + (topspan - base) * h1 / tank_bottom
        c1 = [(-span1, -span1), (span1, -span1), (span1, span1), (-span1, span1)]
        for (a0, b0), (a1, b1) in zip(corners, c1[1:] + c1[:1]):
            beam(m, (h, a0, b0), (h1, a1, b1), 0.25, WHITE)
    revolve(m, [(0, 0.6), (tank_bottom, 0.6)], WHITE, 10)
    r = 8.5
    revolve(m, [(tank_bottom, 3.0), (tank_bottom + 3.0, r), (tank_bottom + 14.0, r), (tank_bottom + 17.0, r * 0.8),
                (tank_bottom + 20.0, r * 0.45), (tank_bottom + 22.0, 0.3)], STEEL, 20)
    beam(m, (tank_bottom + 22.0, 0, 0), (total, 0, 0), 0.3, DARK)
    return m, total


def lightning_tower(width, depth, height):
    """Pad 39B lightning tower: a three-legged steel lattice tapering from about 22 m to 3 m, rings and X bracing
    every 8 m, platforms, and a fibreglass mast on top that carries the catenary wires."""
    total = height + GROUND_M
    m = Mesh()
    mast = 20.0
    top = total - mast
    r0, r1 = min(width, depth) * 0.48, 1.6
    legs = [math.radians(90 + 120 * k) for k in range(3)]

    def corner(k, h):
        r = r0 + (r1 - r0) * h / top
        return (h, r * math.cos(legs[k]), r * math.sin(legs[k]))

    for k in range(3):
        beam(m, corner(k, 0), corner(k, top), 0.9, STEEL)
    levels = int(top / 8)
    for i in range(levels + 1):
        h = top * i / levels
        for k in range(3):
            beam(m, corner(k, h), corner((k + 1) % 3, h), 0.35, STEEL)
        if i < levels:
            h1 = top * (i + 1) / levels
            for k in range(3):
                beam(m, corner(k, h), corner((k + 1) % 3, h1), 0.28, STEEL)
                beam(m, corner((k + 1) % 3, h), corner(k, h1), 0.28, STEEL)
    for i in range(3, levels, 4):
        h = top * i / levels
        c = [corner(k, h) for k in range(3)]
        r = math.hypot(c[0][1], c[0][2]) * 0.8
        box(m, (h - 0.15, -r * 0.6, -r * 0.6), (h + 0.15, r * 0.6, r * 0.6), STEEL)
    box(m, (top - 0.5, -2.0, -2.0), (top + 1.5, 2.0, 2.0), CLAD)
    beam(m, (top + 1.5, 0, 0), (total, 0, 0), 0.7, WHITE)
    return m, total


def starbase_tower(width, depth, height):
    """Starship launch tower at Starbase (OSM "Starbase Integration Tower 1/2", 143-145 m): the integration tower
    without its own launch mount and crane, since the rocket stands on the game's pad; the catch arms face it (+B)."""
    return integration_tower(width, depth, height, olm=False, crane=False)


def lattice_mast(width, depth, height):
    """Lightning protection mast after the photos of the Ariane 6 pad (ELA-4): a square galvanised lattice, slightly
    tapering, rings and X bracing every 6 m, rest platforms, and a slim air terminal on top."""
    total = height + GROUND_M
    m = Mesh()
    pole = min(12.0, total * 0.12)
    top = total - pole
    r0 = min(width, depth) * 0.46
    r1 = max(r0 * 0.45, 0.9)

    def corner(k, h):
        r = r0 + (r1 - r0) * h / top
        sa, sb = ((-1, -1), (1, -1), (1, 1), (-1, 1))[k]
        return (h, sa * r, sb * r)

    for k in range(4):
        beam(m, corner(k, 0), corner(k, top), 0.45, STEEL)
    levels = max(int(top / 6.0), 2)
    for i in range(levels + 1):
        h = top * i / levels
        for k in range(4):
            beam(m, corner(k, h), corner((k + 1) % 4, h), 0.18, STEEL)
        if i < levels:
            h1 = top * (i + 1) / levels
            for k in range(4):
                beam(m, corner(k, h), corner((k + 1) % 4, h1), 0.14, STEEL)
                beam(m, corner((k + 1) % 4, h), corner(k, h1), 0.14, STEEL)
    for i in range(4, levels, 5):
        h = top * i / levels
        r = abs(corner(0, h)[1])
        box(m, (h - 0.1, -r, -r), (h + 0.1, r, r), STEEL)
    box(m, (top - 0.3, -r1 - 0.3, -r1 - 0.3), (top + 0.3, r1 + 0.3, r1 + 0.3), STEEL)
    beam(m, (top + 0.3, 0, 0), (total, 0, 0), 0.35, STEEL)
    return m, total


def pole_mast(width, depth, height):
    """Slim white lightning mast on a small base (Rocket Lab Launch Complex 1): a tapering tube."""
    total = height + GROUND_M
    m = Mesh()
    r = min(width, depth) * 0.5
    box(m, (0, -r, -r), (GROUND_M + 0.6, r, r), CLAD)
    revolve(m, [(GROUND_M + 0.6, r * 0.32), (total - 3.0, r * 0.12), (total, 0.05)], WHITE, 12)
    return m, total


def ariane_gantry(width, depth, height):
    """Ariane 6 mobile gantry (ELA-4, OSM "Portique Mobile", 90 m), after the CNES/ESA photo of the launch complex:
    white clad, a wide lower block and a narrower upper tower with a roof cap, and the full-height vehicle bay facing
    the pad (+B), open so the dark steel access platforms inside show; louvres under the roof and floor seams."""
    total = height + GROUND_M
    m = Mesh()
    hw, hd = width / 2, depth / 2
    split = GROUND_M + 0.55 * height              # top of the lower block
    roof = total - 1.5
    uw, ud = hw * 0.74, hd * 0.80                 # upper tower half sizes
    uoff = hd - ud                                # the upper tower is flush with the pad side
    bay_low, bay_up = 0.27 * width, 0.13 * width  # half width of the vehicle bay, lower and upper
    bay_depth = 0.55 * depth
    front = hd
    # Lower block: two side wings and the back, around the bay.
    box(m, (0, -hw, -hd), (split, -bay_low, hd), WHITE)
    box(m, (0, bay_low, -hd), (split, hw, hd), WHITE)
    box(m, (0, -bay_low, -hd), (split, bay_low, front - bay_depth), WHITE)
    # Upper tower, also open at the front.
    box(m, (split, -uw, uoff - ud), (roof, -bay_up, front), WHITE)
    box(m, (split, bay_up, uoff - ud), (roof, uw, front), WHITE)
    box(m, (split, -bay_up, uoff - ud), (roof, bay_up, front - bay_depth), WHITE)
    # Bay ceiling below the roof cap, and the cap itself overhanging a little.
    box(m, (roof - 6.0, -bay_up, front - bay_depth), (roof, bay_up, front), WHITE)
    box(m, (roof, -uw - 0.6, uoff - ud - 0.6), (total, uw + 0.6, front + 0.6), CLAD)
    # Roof of the lower block around the tower.
    box(m, (split - 0.4, -hw - 0.3, -hd - 0.3), (split + 0.4, hw + 0.3, hd + 0.3), CLAD)
    # Inside the bay: dark platforms every 7 m and the steel frame at the back and sides.
    for h in range(int(GROUND_M + 9), int(roof - 6), 7):
        half = bay_low if h < split else bay_up
        box(m, (h - 0.3, -half + 0.2, front - bay_depth + 0.2), (h + 0.3, half - 0.2, front - bay_depth * 0.45), DARK)
    for side in (-1, 1):
        for b in (front - bay_depth + 0.6, front - bay_depth * 0.5):
            beam(m, (GROUND_M, side * (bay_up - 0.6), b), (roof - 6.0, side * (bay_up - 0.6), b), 0.8, STEEL)
            beam(m, (GROUND_M, side * (bay_low - 0.6), b), (split, side * (bay_low - 0.6), b), 0.8, STEEL)
    box(m, (GROUND_M, -bay_up, front - bay_depth + 0.05), (roof - 6.0, bay_up, front - bay_depth + 0.25), DARK)
    box(m, (GROUND_M, -bay_low, front - bay_depth + 0.05), (split, bay_low, front - bay_depth + 0.25), DARK)
    # Floor seams on all outer walls and a louvre band under the roof cap on the long sides.
    for h in [GROUND_M + 12.0 * k for k in range(1, 8)]:
        if h >= roof - 2:
            break
        a, b = (hw, hd) if h < split else (uw, ud)
        o = 0.0 if h < split else uoff
        box(m, (h - 0.2, -a - 0.08, o - b - 0.08), (h + 0.2, a + 0.08, o + b + 0.08), CLAD)
    for side in (-1, 1):
        box(m, (roof - 5.0, side * (uw + 0.05) - 0.05, uoff - ud * 0.7), (roof - 2.5, side * (uw + 0.05) + 0.05, uoff + ud * 0.7), DARK)
    # Wheel bogies on the rails at the four corners.
    for sa in (-1, 1):
        for sb in (-1, 1):
            box(m, (GROUND_M - 0.5, sa * hw * 0.85 - 2.0, sb * hd * 0.85 - 4.0), (GROUND_M + 2.5, sa * hw * 0.85 + 2.0, sb * hd * 0.85 + 4.0), DARK)
    return m, total


def concrete_water_tower(width, depth, height):
    """Water tower of the Ariane 6 pad: a plain concrete cylinder with a slightly wider tank head and a roof rim."""
    total = height + GROUND_M
    m = Mesh()
    r = min(width, depth) * 0.5
    head = total - 9.0
    revolve(m, [(0, r * 0.86), (head, r * 0.86), (head + 1.0, r), (total - 0.6, r), (total - 0.6, r * 0.98), (total, r * 0.3)], WHITE, 32)
    box(m, (head - 0.4, -r * 0.88, -0.4), (head + 0.2, r * 0.88, 0.4), CLAD)
    return m, total


GANTRY_PALETTE = [((150, 154, 160), 150, 25), ((206, 208, 210), 165, 5), ((236, 236, 232), 170, 0), ((36, 40, 48), 140, 10)]
CONCRETE_PALETTE = [((176, 178, 178), 200, 0), ((168, 168, 164), 210, 0), ((198, 196, 190), 215, 0), ((120, 120, 118), 210, 0)]

# key, name, A, B, height (Landmarks.cs), palette, builder
TOWERS = [
    ("Fss", "LC-39A Fixed Service Structure", 22.02, 13.27, 100.0, FSS_PALETTE, fixed_service_structure),
    ("IntegrationTower", "LC-39A Integration Tower", 11.33, 11.31, 144.5, TOWER_PALETTE, integration_tower),
    ("WaterTower", "LC-39A water tower", 20.0, 20.0, 80.5, WATER_PALETTE, water_tower),
    ("LightningTower", "LC-39B lightning tower", 22.0, 24.1, 147.0, TOWER_PALETTE, lightning_tower),
    ("StarbaseTower", "Starbase launch tower 1", 13.0, 13.0, 143.0, TOWER_PALETTE, starbase_tower),
    ("LatticeMast", "ELA-4 lightning mast", 4.5, 4.5, 115.0, TOWER_PALETTE, lattice_mast),
    ("ArianeGantry", "ELA-4 mobile gantry", 44.1, 68.1, 90.0, GANTRY_PALETTE, ariane_gantry),
    ("ConcreteWaterTower", "ELA-4 water tower", 15.0, 15.0, 68.0, CONCRETE_PALETTE, concrete_water_tower),
    ("PoleMast", "LC-1A lightning mast", 2.0, 2.0, 24.0, WATER_PALETTE, pole_mast),
]


def tower_mesh(builder, width, depth, height):
    m, total = builder(width, depth, height)
    positions = [(p[0] / total, p[1] / width, p[2] / depth) for p in m.positions]
    return positions, m.normals, m.uvs, m.indices


def poly(mesh, pts, n, uv):
    """Convex polygon as a triangle fan, with small varying UVs inside the swatch (tangents need them)."""
    base = len(mesh.positions)
    count = len(pts)
    for k, p in enumerate(pts):
        a = 2 * math.pi * k / count
        mesh.positions.append(p)
        mesh.normals.append(n)
        mesh.uvs.append((uv[0] + 0.08 * math.cos(a), uv[1] + 0.3 * math.sin(a)))
    for k in range(1, count - 1):
        tri = (0, k, k + 1)
        a, b, c = (pts[i] for i in tri)
        cross = _cross(_sub(b, a), _sub(c, a))
        if sum(cross[i] * n[i] for i in range(3)) < 0:
            tri = (0, k + 1, k)
        mesh.indices.extend(base + i for i in tri)


def extrude(mesh, profile, axis, start, end, uv, caps=(False, False)):
    """Prism of a convex profile along one axis. profile: (u, v) points in the plane of the two other axes, in the
    order of the remaining axes (up, A, B without `axis`); axis: 0 up, 1 A, 2 B."""
    others = [i for i in range(3) if i != axis]

    def point(u, v, w):
        p = [0.0, 0.0, 0.0]
        p[others[0]], p[others[1]], p[axis] = u, v, w
        return tuple(p)

    cu = sum(p[0] for p in profile) / len(profile)
    cv = sum(p[1] for p in profile) / len(profile)
    for (u0, v0), (u1, v1) in zip(profile, profile[1:] + profile[:1]):
        du, dv = u1 - u0, v1 - v0
        nu, nv = dv, -du
        if nu * ((u0 + u1) / 2 - cu) + nv * ((v0 + v1) / 2 - cv) < 0:
            nu, nv = -nu, -nv
        ln = math.hypot(nu, nv)
        n = [0.0, 0.0, 0.0]
        n[others[0]], n[others[1]] = nu / ln, nv / ln
        mesh.quad([point(u0, v0, start), point(u1, v1, start), point(u1, v1, end), point(u0, v0, end)], tuple(n), uv)
    for w, on, sign in ((start, caps[0], -1), (end, caps[1], 1)):
        if on:
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            poly(mesh, [point(u, v, w) for u, v in profile], tuple(n), uv)


def crew_arm():
    """SpaceX crew access arm at LC-39A, after the Crew-2 / Demo-2 photos: an enclosed white walkway with chamfered
    roof edges and a band of windows on both sides, the taller white room at the end with its sloped nose and the
    dark hatch opening toward the capsule, a black triangular truss under the tower end, the steel hinge column at
    the tower, a cable tray underneath and lights on the roof. Hinge at the origin; x up (over 3 m), y along the arm
    (over 21 m; the hatch face is at 24.5 m), z across (over 2.8 m)."""
    L, H, W = 21.0, 3.0, 2.8
    m = Mesh()
    tube_end, room_end = 20.6, 24.5
    # Walkway: profile across (x up, z across) with chamfered top edges, extruded along y.
    walk = [(0.0, -1.35), (0.0, 1.35), (2.45, 1.35), (2.95, 0.95), (2.95, -0.95), (2.45, -1.35)]
    extrude(m, walk, 1, 0.6, tube_end, WHITE)
    # Window band: eight panes per side, dark glass set in white mullions (a few millimetres proud of the skin).
    panes = 8
    pitch = (tube_end - 2.6) / panes
    for k in range(panes):
        y0 = 2.0 + k * pitch
        for sz in (-1, 1):
            z0, z1 = sorted((sz * 1.35, sz * 1.38))
            box(m, (1.05, y0 + 0.18, z0), (2.15, y0 + pitch - 0.18, z1), DARK)
    # Dark base rail along the foot of the walkway.
    for sz in (-1, 1):
        box(m, (0.0, 0.6, sz * 1.35 - 0.06), (0.22, tube_end, sz * 1.35 + 0.06), CLAD)
    # White room: taller and wider, with a sloped nose; profile in (x up, y along), extruded across z.
    room = [(-0.7, tube_end - 0.2), (-0.7, room_end), (2.3, room_end), (3.75, room_end - 1.3), (3.75, tube_end - 0.2)]
    extrude(m, room, 2, -1.85, 1.85, WHITE, caps=(True, True))
    # Hatch opening and its seal frame on the nose face, a small window in each side of the white room.
    box(m, (-0.05, room_end - 0.02, -0.9), (2.15, room_end + 0.06, 0.9), CLAD)
    box(m, (0.1, room_end + 0.06, -0.65), (2.0, room_end + 0.1, 0.65), DARK)
    for sz in (-1, 1):
        z0, z1 = sorted((sz * 1.85, sz * 1.89))
        box(m, (1.2, room_end - 2.4, z0), (2.3, room_end - 1.2, z1), DARK)
    # Cable and duct tray under the walkway.
    box(m, (-0.45, 1.0, -0.5), (0.0, tube_end - 0.5, 0.5), CLAD)
    # Triangular black support truss under the tower end: two side frames, cross members, a diagonal web.
    foot, reach = -5.5, 7.5
    for sz in (-1.05, 1.05):
        beam(m, (foot, 0.4, sz), (-0.45, 0.4, sz), 0.35, CLAD)            # vertical at the hinge
        beam(m, (-0.45, 0.4, sz), (-0.45, reach, sz), 0.35, CLAD)         # top chord
        beam(m, (foot, 0.4, sz), (-0.45, reach, sz), 0.4, CLAD)           # diagonal strut
        for t in (0.33, 0.66):
            y = 0.4 + (reach - 0.4) * t
            x = foot + (-0.45 - foot) * t
            beam(m, (x, y, sz), (-0.45, y, sz), 0.22, CLAD)
    for t in (0.0, 0.5, 1.0):
        y = 0.4 + (reach - 0.4) * t
        x = foot + (-0.45 - foot) * t
        beam(m, (x, y, -1.05), (x, y, 1.05), 0.22, CLAD)
    # Hinge column with its bearing housings, and the collar where the walkway meets the tower platform.
    beam(m, (foot - 1.0, 0.0, 0.0), (3.9, 0.0, 0.0), 0.75, STEEL)
    box(m, (3.25, -0.7, -0.7), (3.9, 0.7, 0.7), CLAD)
    box(m, (foot - 0.4, -0.7, -0.7), (foot + 0.4, 0.7, 0.7), CLAD)
    extrude(m, [(-0.1, -1.5), (-0.1, 1.5), (3.1, 1.5), (3.1, -1.5)], 1, 0.0, 0.6, CLAD, caps=(False, False))
    # Roof lights and the hatch camera.
    for y in (4.0, 12.0, tube_end - 1.0):
        box(m, (2.95, y - 0.25, -0.25), (3.2, y + 0.25, 0.25), STEEL)
    box(m, (2.4, room_end - 0.1, 1.0), (2.7, room_end + 0.25, 1.3), STEEL)
    positions = [(p[0] / H, p[1] / L, p[2] / W) for p in m.positions]
    return positions, m.normals, m.uvs, m.indices


def tank():
    """Round tank: 24-sided cylinder of diameter 1 (y and z in [-0.5, 0.5]) from x = 0 to 1, flat top. The side
    uses the facade half of the building textures, the top the roof half."""
    n = 24
    m = 0.03
    positions, normals, uvs, indices = [], [], [], []
    for i in range(n + 1):
        t = i / n
        ang = 2 * math.pi * t
        c, s = math.cos(ang), math.sin(ang)
        u = m + (0.5 - 2 * m) * t
        for x, v in ((0, 1 - m), (1, m)):
            positions.append((x, 0.5 * c, 0.5 * s))
            normals.append((0, c, s))
            uvs.append((u, v))
    for i in range(n):
        a = 2 * i
        indices.extend((a, a + 2, a + 1, a + 1, a + 2, a + 3))
    centre = len(positions)
    positions.append((1, 0, 0))
    normals.append((1, 0, 0))
    uvs.append((0.75, 0.5))
    for i in range(n):
        ang = 2 * math.pi * i / n
        positions.append((1, 0.5 * math.cos(ang), 0.5 * math.sin(ang)))
        normals.append((1, 0, 0))
        uvs.append((0.75 + 0.2 * math.cos(ang), 0.5 + 0.2 * math.sin(ang)))
    for i in range(n):
        indices.extend((centre, centre + 1 + (i + 1) % n, centre + 1 + i))
    return positions, normals, uvs, indices


def tank_dome():
    """Hemispherical cap of diameter 1: y and z in [-0.5, 0.5], x from 0 (rim) to 1 (apex), smooth normals,
    textured with the roof half of the building textures."""
    rings, n = 8, 24
    positions, normals, uvs, indices = [], [], [], []
    for r in range(rings + 1):
        th = (math.pi / 2) * r / rings
        for i in range(n + 1):
            ph = 2 * math.pi * i / n
            positions.append((math.sin(th), 0.5 * math.cos(th) * math.cos(ph), 0.5 * math.cos(th) * math.sin(ph)))
            normals.append(_unit((math.sin(th) * 0.6 + 0.001, math.cos(th) * math.cos(ph), math.cos(th) * math.sin(ph))))
            uvs.append((0.75 + 0.15 * math.cos(th) * math.cos(ph), 0.5 + 0.15 * math.cos(th) * math.sin(ph)))
    for r in range(rings):
        for i in range(n):
            a = r * (n + 1) + i
            b = a + n + 1
            indices.extend((a, a + 1, b, a + 1, b + 1, b))
    return positions, normals, uvs, indices


def horizontal_tank():
    """Cylinder lying along A: y in [-0.5, 0.5] long, circular cross-section in x (height) and z of diameter 1,
    flat ends. Side uses the facade half of the building textures, the ends the roof half."""
    n = 24
    m = 0.03
    positions, normals, uvs, indices = [], [], [], []
    for i in range(n + 1):
        t = i / n
        ph = 2 * math.pi * t
        c, s = math.cos(ph), math.sin(ph)
        for y, u in ((-0.5, m), (0.5, 0.5 - m)):
            positions.append((0.5 + 0.5 * c, y, 0.5 * s))
            normals.append((c, 0, s))
            uvs.append((u, m + (1 - 2 * m) * t))
    for i in range(n):
        a = 2 * i
        indices.extend((a, a + 2, a + 1, a + 1, a + 2, a + 3))
    for y, nrm in ((-0.5, -1), (0.5, 1)):
        centre = len(positions)
        positions.append((0.5, y, 0))
        normals.append((0, nrm, 0))
        uvs.append((0.75, 0.5))
        for i in range(n):
            ph = 2 * math.pi * i / n
            positions.append((0.5 + 0.5 * math.cos(ph), y, 0.5 * math.sin(ph)))
            normals.append((0, nrm, 0))
            uvs.append((0.75 + 0.2 * math.cos(ph), 0.5 + 0.2 * math.sin(ph)))
        for i in range(n):
            indices.extend((centre, centre + 1 + i, centre + 1 + (i + 1) % n))
    return _fix_winding((positions, normals, uvs, indices))


def tent():
    """Half cylinder along A (y in [-0.5, 0.5]) for the arched payload tent: z in [-0.5, 0.5], x from 0 to 1 at the
    crown, so a height of half the width makes a semicircle. Ends are closed half discs."""
    n = 20
    positions, normals, uvs, indices = [], [], [], []
    for i in range(n + 1):
        ph = math.pi * i / n
        c, s = math.cos(ph), math.sin(ph)
        for y in (-0.5, 0.5):
            positions.append((s, y, 0.5 * c))
            normals.append((s, 0, c))
            uvs.append((0.55 + 0.4 * (y + 0.5), 0.1 + 0.8 * i / n))
    for i in range(n):
        a = 2 * i
        indices.extend((a, a + 2, a + 1, a + 1, a + 2, a + 3))
    for y, nrm in ((-0.5, -1), (0.5, 1)):
        centre = len(positions)
        positions.append((0, y, 0))
        normals.append((0, nrm, 0))
        uvs.append((0.75, 0.5))
        for i in range(n + 1):
            ph = math.pi * i / n
            positions.append((math.sin(ph), y, 0.5 * math.cos(ph)))
            normals.append((0, nrm, 0))
            uvs.append((0.75 + 0.2 * math.cos(ph), 0.5 + 0.2 * math.sin(ph)))   # varying UVs, so the ends get tangents
        for i in range(n):
            indices.extend((centre, centre + 1 + i, centre + 2 + i))
    return _fix_winding((positions, normals, uvs, indices))


def dome_mesh():
    return _fix_winding(tank_dome())


def _fix_winding(mesh):
    """Flips triangles whose winding disagrees with the vertex normals."""
    positions, normals, uvs, indices = mesh
    out = []
    for i in range(0, len(indices), 3):
        a, b, c = (positions[indices[i + k]] for k in range(3))
        n = _cross(_sub(b, a), _sub(c, a))
        vn = normals[indices[i]]
        if sum(n[k] * vn[k] for k in range(3)) < 0:
            out.extend((indices[i], indices[i + 2], indices[i + 1]))
        else:
            out.extend(indices[i:i + 3])
    return positions, normals, uvs, out


def meshes():
    return [(f"GEE_{key}", tower_mesh(builder, a, b, h)) for key, _, a, b, h, _, builder in TOWERS]


def tank_mesh():
    return _fix_winding(tank())


def write(mod_dir, first_variant, write_png):
    """Textures and Assets.xml lines of the towers, from first_variant on (the order of TOWERS)."""
    lines = []
    for k, (key, name, a, b, h, palette, _) in enumerate(TOWERS):
        i = first_variant + k
        tex = os.path.join(mod_dir, "Textures")
        write_png(os.path.join(tex, f"GEE_{key}_Diffuse.png"), 16, 4,
                  lambda x, y, palette=palette: bytes((*palette[x // 4][0], 255)))
        write_png(os.path.join(tex, f"GEE_{key}_PBR.png"), 16, 4,
                  lambda x, y, palette=palette: bytes((255, palette[x // 4][1], palette[x // 4][2], 255)))
        lines += [f'  <!-- variant {i}: landmark {name} -->',
                  f'  <PbrMaterial Id="GEE_{key}_Material">',
                  f'    <Diffuse Path="Textures/GEE_{key}_Diffuse.png" Category="Vessel" />',
                  '    <Normal Path="Textures/GEE_Normal.png" Category="Vessel" />',
                  f'    <AoRoughMetal Path="Textures/GEE_{key}_PBR.png" Category="Vessel" />',
                  '  </PbrMaterial>',
                  f'  <StaticObject Id="GEE_Box{i}">',
                  f'    <PartModel Id="GEE_Box{i}_Model">',
                  f'      <Mesh Id="GEE_{key}" />',
                  f'      <Material Id="GEE_{key}_Material" />',
                  '    </PartModel>',
                  '  </StaticObject>']
    return lines
