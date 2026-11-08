"""Roadside furniture models (RoadFurniture.cs): a street lamp, a post-mounted highway sign and a cantilever sign gantry.

Used by make_assets.py. Meshes are in metres and meant for instances with unit axes (Up, AxisA, AxisB are unit
vectors, no scaling): x up from the foot, y (AxisA) is the direction the sign faces / the lamp arm reaches, z across.
All three share one 256 x 256 texture: a swatch row at the top (steel, white, dark, lamp lens) and three sign faces.
"""
import math
import os

import structure_assets as sa
from PIL import Image, ImageDraw, ImageFont

SIZE = 256
SWATCH_H = 32
# swatches: galvanised steel, white, dark rubber/steel, lamp lens; (colour, roughness, metalness)
PALETTE = [((168, 172, 176), 120, 160), ((236, 236, 232), 140, 0), ((46, 48, 52), 150, 20), ((250, 236, 200), 90, 0)]
# sign faces: (x0, y0, x1, y1) in pixels
FACE_EXIT = (0, 40, 120, 120)
FACE_INFO = (128, 40, 248, 120)
FACE_POST = (0, 136, 120, 216)


def sw(i):
    return ((i + 0.5) / 4, 0.5 * SWATCH_H / SIZE)


STEEL, WHITE, DARK, LENS = sw(0), sw(1), sw(2), sw(3)


class FMesh(sa.Mesh):
    """Mesh whose flat colours sample a small spot at their swatch centre (the swatches here are small)."""

    def quad(self, p, n, uv):
        base = len(self.positions)
        for q, (du, dv) in zip(p, ((-0.03, -0.02), (0.03, -0.02), (0.03, 0.02), (-0.03, 0.02))):
            self.positions.append(q)
            self.normals.append(n)
            self.uvs.append((uv[0] + du, uv[1] + dv))
        self._order(p, n, base)

    def face(self, p, n, rect):
        """Quad p (bottom left, bottom right, top right, top left as seen from the front) showing a texture rect."""
        x0, y0, x1, y1 = rect
        base = len(self.positions)
        for q, (u, v) in zip(p, ((x0, y1), (x1, y1), (x1, y0), (x0, y0))):
            self.positions.append(q)
            self.normals.append(n)
            self.uvs.append((u / SIZE, v / SIZE))
        self._order(p, n, base)

    def _order(self, p, n, base):
        e1, e2 = sa._sub(p[1], p[0]), sa._sub(p[2], p[0])
        cross = sa._cross(e1, e2)
        order = (0, 1, 2, 0, 2, 3) if sum(cross[k] * n[k] for k in range(3)) >= 0 else (0, 2, 1, 0, 3, 2)
        self.indices.extend(base + i for i in order)


def panel(m, x0, x1, z0, z1, y, rect, thickness=0.12):
    """Sign panel: textured front at y + thickness, grey back and edges."""
    f = y + thickness
    sa.box(m, (x0, y, z0), (x1, f - 0.001, z1), STEEL)
    m.face([(x0, f, z1), (x0, f, z0), (x1, f, z0), (x1, f, z1)], (0, 1, 0), rect)


def lamp():
    """Street lamp: 8.2 m pole with a curved arm and a cobra-head luminaire reaching 1.4 m toward +y."""
    m = FMesh()
    sa.box(m, (-0.3, -0.16, -0.16), (0.35, 0.16, 0.16), STEEL)                 # base collar
    sa.beam(m, (-0.3, 0, 0), (7.9, 0, 0), 0.13, STEEL)                         # pole
    sa.beam(m, (7.85, 0, 0), (8.25, 0.55, 0), 0.09, STEEL)                     # arm
    sa.beam(m, (8.25, 0.55, 0), (8.3, 1.05, 0), 0.09, STEEL)
    sa.box(m, (8.12, 0.85, -0.19), (8.34, 2.0, 0.19), STEEL)                   # luminaire housing
    sa.box(m, (8.08, 0.95, -0.15), (8.12, 1.92, 0.15), LENS)                   # lens
    return _done(m)


def post_sign():
    """Two posts and a 2.6 x 1.8 m sign facing +y, bottom edge at 1.5 m."""
    m = FMesh()
    for z in (-1.0, 1.0):
        sa.beam(m, (-0.3, -0.04, z), (3.4, -0.04, z), 0.09, STEEL)
    panel(m, 1.5, 3.3, -1.3, 1.3, 0.0, FACE_POST)
    return _done(m)


def gantry():
    """Cantilever sign gantry: post at z = +7, a truss over the lane(s) to z = -5.6 at 7.4 m, two sign panels
    (each 3.6 x 2.4 m, clearance 5 m) hanging on the +y side. Lamps under the truss light the signs."""
    m = FMesh()
    sa.box(m, (-0.3, -0.5, 6.5), (0.3, 0.5, 7.5), STEEL)                       # foundation plate
    sa.beam(m, (-0.3, 0, 7.0), (8.4, 0, 7.0), 0.5, STEEL)                      # post
    z_end = -5.6
    for x in (6.9, 7.7):                                                       # truss chords
        sa.beam(m, (x, 0.0, 7.0), (x, 0.0, z_end), 0.16, STEEL)
    steps = 10
    for k in range(steps):                                                      # zig-zag web
        za = 7.0 + (z_end - 7.0) * k / steps
        zb = 7.0 + (z_end - 7.0) * (k + 1) / steps
        a, b = (6.9, 0.0, za), (7.7, 0.0, zb)
        if k % 2:
            a, b = (7.7, 0.0, za), (6.9, 0.0, zb)
        sa.beam(m, a, b, 0.07, STEEL)
    sa.beam(m, (6.9, 0.0, z_end), (7.7, 0.0, z_end), 0.16, STEEL)
    for z in (-4.9, -4.7, -0.3, -0.1):                                          # hangers
        sa.beam(m, (4.95, 0.1, z), (6.9, 0.1, z), 0.06, DARK)
    panel(m, 4.95, 7.35, -5.2, -1.6, 0.05, FACE_EXIT)
    panel(m, 4.95, 7.35, -1.2, 2.4, 0.05, FACE_INFO)
    for z in (-3.4, 0.6):                                                       # sign lights
        sa.box(m, (6.55, 0.35, z - 0.25), (6.8, 0.75, z + 0.25), DARK)
    return _done(m)


def _done(m):
    return sa._fix_winding((m.positions, m.normals, m.uvs, m.indices))


MODELS = [("lamp", lamp), ("sign", post_sign), ("gantry", gantry)]


def meshes():
    return [(f"GEE_Furniture_{name}", build()) for name, build in MODELS]


def _font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def _sign(d, rect, blue, text, arrow):
    x0, y0, x1, y1 = rect
    d.rectangle(rect, fill=blue)
    d.rounded_rectangle((x0 + 3, y0 + 3, x1 - 4, y1 - 4), radius=6, outline=(240, 240, 240), width=3)
    w = x1 - x0
    if arrow == "up":
        cx = x0 + w * 0.78
        d.polygon([(cx, y0 + 12), (cx + 17, y0 + 38), (cx + 6, y0 + 38), (cx + 6, y1 - 12), (cx - 6, y1 - 12),
                   (cx - 6, y0 + 38), (cx - 17, y0 + 38)], fill=(244, 244, 244))
    elif arrow == "exit":
        cx, cy = x0 + w * 0.8, (y0 + y1) / 2
        d.polygon([(cx + 14, cy - 22), (cx + 14, cy + 6), (cx + 0, cy - 8), (cx - 22, cy + 22), (cx - 28, cy + 16),
                   (cx - 8, cy - 14), (cx - 20, cy - 22)], fill=(244, 244, 244))
    font = _font(22)
    d.text((x0 + 12, y0 + 12), text[0], fill=(244, 244, 244), font=font)
    d.text((x0 + 12, y0 + 44), text[1], fill=(244, 244, 244), font=_font(16))
    d.rectangle((x0 + 12, y1 - 22, x0 + w * 0.55, y1 - 14), fill=(244, 244, 244))


def images():
    """(diffuse, AoRoughMetal) images of the shared texture."""
    diffuse = Image.new("RGBA", (SIZE, SIZE), (46, 48, 52, 255))
    pbr = Image.new("RGBA", (SIZE, SIZE), (255, 140, 0, 255))
    d, p = ImageDraw.Draw(diffuse), ImageDraw.Draw(pbr)
    for i, (colour, rough, metal) in enumerate(PALETTE):
        box = (i * 64, 0, i * 64 + 63, SWATCH_H - 1)
        d.rectangle(box, fill=colour + (255,))
        p.rectangle(box, fill=(255, rough, metal, 255))
    blue = (16, 70, 148)
    _sign(d, FACE_EXIT, blue, ("A 9", "Exit"), "exit")
    _sign(d, FACE_INFO, blue, ("City", "Center"), "up")
    _sign(d, FACE_POST, blue, ("EXIT", "1 km"), "exit")
    for rect in (FACE_EXIT, FACE_INFO, FACE_POST):
        p.rectangle(rect, fill=(255, 110, 0, 255))
    return diffuse, pbr


def write(mod_dir, first_variant, write_ktx2):
    """Texture and Assets.xml lines of the models, from first_variant on (the order of MODELS)."""
    diffuse, pbr = images()
    write_ktx2(os.path.join(mod_dir, "Textures", "GEE_Furniture_Diffuse.ktx2"), diffuse)
    write_ktx2(os.path.join(mod_dir, "Textures", "GEE_Furniture_PBR.ktx2"), pbr)
    lines = ['  <!-- roadside furniture: shared material (furniture_assets.py) -->',
             '  <PbrMaterial Id="GEE_Furniture_Material">',
             '    <Diffuse Path="Textures/GEE_Furniture_Diffuse.ktx2" Category="Vessel" />',
             '    <Normal Path="Textures/GEE_Normal.ktx2" Category="Vessel" />',
             '    <AoRoughMetal Path="Textures/GEE_Furniture_PBR.ktx2" Category="Vessel" />',
             '  </PbrMaterial>']
    for k, (name, _) in enumerate(MODELS):
        i = first_variant + k
        lines += [f'  <!-- variant {i}: roadside furniture {name} -->',
                  f'  <StaticObject Id="GEE_Box{i}">',
                  f'    <PartModel Id="GEE_Box{i}_Model">',
                  f'      <Mesh Id="GEE_Furniture_{name}" />',
                  '      <Material Id="GEE_Furniture_Material" />',
                  '    </PartModel>',
                  '  </StaticObject>']
    return lines


if __name__ == "__main__":
    import sys
    images()[0].save(sys.argv[1] if len(sys.argv) > 1 else "furniture_preview.png")
    for name, mesh in meshes():
        print(name, len(mesh[0]), "vertices", len(mesh[3]) // 3, "triangles")
