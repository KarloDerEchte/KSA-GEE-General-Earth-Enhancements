"""Generates the OsmBuildings render assets: a unit box mesh, one material per building colour and Assets.xml.

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


def road_line(y):
    """Lane markings on the road top. Texture rows run across the road (v = width), so lines are rows
    and stay sharp however long a road piece is stretched."""
    edge = TEX_H * 0.09
    return abs(y - edge) < 1.2 or abs(y - (TEX_H - 1 - edge)) < 1.2 or abs(y - (TEX_H - 1) / 2) < 0.6


def road_diffuse_pixel(rng):
    def pixel(x, y):
        if x >= TEX_W // 2 and road_line(y):
            v = 215 + 15 * rng.random()
            return bytes((clamp(v), clamp(v), clamp(v * 0.97), 255))
        v = 52 + 10 * rng.random()
        return bytes((clamp(v), clamp(v), clamp(v * 1.04), 255))
    return pixel


def road_pbr_pixel(x, y):
    return bytes((255, 200 if x >= TEX_W // 2 and road_line(y) else 240, 0, 255))


ROAD_SIZE = 256


def _noise(rng, size, blur):
    """Zero-mean, unit-ish noise with features about `blur` pixels wide."""
    import numpy as np
    from PIL import Image, ImageFilter
    a = rng.random((size, size)).astype("float32")
    im = Image.fromarray((a * 255).astype("uint8")).filter(ImageFilter.GaussianBlur(blur))
    b = np.asarray(im, dtype="float32")
    return (b - b.mean()) / (b.std() + 1e-6)


def road_images():
    """Asphalt: grain, patches, darker wheel tracks, worn off-white edge and centre lines and a dusty shoulder
    that fades into the ground. Rows run across the road (the top face's v), columns along it (stretched per
    road piece), so all detail that must stay sharp is a function of the row. The left half is for the low
    side faces."""
    import numpy as np
    from PIL import Image
    rng = np.random.default_rng(7)
    n = ROAD_SIZE
    grain = _noise(rng, n, 0.7)
    patches = _noise(rng, n, 9)
    wear = _noise(rng, n, 5)
    m = 0.03
    ys = (np.arange(n)[:, None] / (n - 1) - m) / (1 - 2 * m)          # 0..1 across the top face
    xs = np.broadcast_to(np.arange(n)[None, :], (n, n))
    t = np.broadcast_to(np.clip(ys, 0, 1), (n, n))
    top = xs >= n // 2

    base = 74 + 5 * grain + 6 * patches
    # Wheel tracks: darker, polished bands; the middle and the edges stay lighter and coarser.
    for c in (0.30, 0.70):
        track = np.exp(-((t - c) / 0.07) ** 2)
        base = base - 9 * track - 3 * track * wear
    # Dusty, gravelly shoulder fading out toward the road edge.
    shoulder = np.clip((0.075 - np.minimum(t, 1 - t)) / 0.075, 0, 1) ** 1.5
    rgb = np.stack([base * 1.0, base * 1.0, base * 1.05], axis=-1)
    dust = np.array([110, 100, 84], dtype="float32") + 8 * grain[..., None]
    rgb = rgb * (1 - 0.8 * shoulder[..., None]) + dust * (0.8 * shoulder[..., None])

    def line(centre, half):
        return np.clip(1 - np.abs(t - centre) / half, 0, 1)

    # Lines are worn: patchy, broken up along the road, never fully opaque.
    worn = np.clip(0.85 + 0.45 * wear + 0.3 * patches, 0.15, 1.0)
    edge = np.maximum(line(0.115, 0.016), line(0.885, 0.016)) * worn
    centre = line(0.5, 0.012) * np.clip(worn * (0.8 + 0.5 * _noise(rng, n, 14)), 0, 1)
    paint = np.maximum(edge, centre * 0.9)
    white = np.array([206, 204, 194], dtype="float32") + 10 * grain[..., None]
    rgb = rgb * (1 - 0.88 * paint[..., None]) + white * (0.88 * paint[..., None])
    rgb = np.where(top[..., None], rgb, np.array([66, 66, 70], dtype="float32"))

    rough = 238 - 14 * np.exp(-((t - 0.30) / 0.07) ** 2) - 14 * np.exp(-((t - 0.70) / 0.07) ** 2)
    rough = rough - 36 * paint + 10 * shoulder
    rough = np.where(top, rough, 240)
    pbr = np.stack([np.full((n, n), 255.0), rough, np.zeros((n, n)), np.full((n, n), 255.0)], axis=-1)
    diffuse = np.concatenate([rgb, np.full((n, n, 1), 255.0)], axis=-1)
    to_image = lambda a: Image.fromarray(np.clip(a, 0, 255).astype("uint8"), "RGBA")
    return to_image(diffuse), to_image(pbr)


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
            return bytes((clamp(g), clamp(g), clamp(g * 0.96), 255))
        base = 46 if kind == "runway" else 56
        g = base + 10 * rng.random()
        if x >= TEX_W // 2:
            if kind == "runway" and (abs(v - 0.05) < 0.012 or abs(v - 0.95) < 0.012 or (abs(v - 0.5) < 0.01 and u < 0.6)):
                w = 215 + 15 * rng.random()
                return bytes((clamp(w), clamp(w), clamp(w * 0.97), 255))
            if kind == "taxiway" and (abs(v - 0.5) < 0.012 or abs(v - 0.06) < 0.01 or abs(v - 0.94) < 0.01):
                return bytes((clamp(220 + 20 * rng.random()), clamp(170 + 15 * rng.random()), 40, 255))
        return bytes((clamp(g), clamp(g), clamp(g * 1.04), 255))
    return pixel


def airfield_pbr_pixel(x, y):
    return bytes((255, 235, 0, 255))


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


def unit_box(up_normals=False):
    """Box with x in [0, 1] (up) and y, z in [-0.5, 0.5]; no bottom face. Facade uses u in [0, 0.5], roof u in [0.5, 1].

    With up_normals every face is shaded as if it faced up. Road slabs use that: their low sides would
    otherwise catch grazing sunlight at dawn and dusk and outline every road in bright orange."""
    m = 0.03
    faces = []
    # top (+x)
    faces.append(((1, 0, 0), [(1, y, z) for y, z in ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5))],
                  lambda p: (0.5 + m + (0.5 - 2 * m) * (p[1] + .5), m + (1 - 2 * m) * (p[2] + .5))))
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
                      lambda p, o=other: (m + (0.5 - 2 * m) * (p[o] + .5), m + (1 - 2 * m) * (1 - p[0]))))

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
        "asset": {"version": "2.0", "generator": "OsmBuildings make_assets.py"},
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
    box_mesh, road_mesh, gable_mesh, tank_mesh = "OsmBuildings_UnitBox", "OsmBuildings_RoadSlab", "OsmBuildings_Gable", "OsmBuildings_Tank"
    write_glb(os.path.join(MOD_DIR, "Meshes", "OsmBuildings_MeshAtlas.glb"),
              [(box_mesh, unit_box()), (road_mesh, unit_box(up_normals=True)), (gable_mesh, gable()),
               (tank_mesh, structure_assets.tank_mesh()), ("OsmBuildings_TankDome", structure_assets.dome_mesh()),
               ("OsmBuildings_HTank", structure_assets.horizontal_tank()), ("OsmBuildings_Tent", structure_assets.tent()),
               ("OsmBuildings_CrewArm", structure_assets.crew_arm())]
              + landmark_assets.meshes())
    write_png(os.path.join(MOD_DIR, "Textures", "OsmBuildings_Normal.png"), 4, 4, lambda x, y: bytes((128, 128, 255, 255)))

    xml = ['<?xml version="1.0" encoding="utf-8"?>',
           "<!-- Generated by src/tools/make_assets.py. -->",
           "<Assets>",
           '  <MeshAtlas Path="Meshes/OsmBuildings_MeshAtlas.glb" />']
    textures = [(name, None, None) for name, *_ in VARIANTS]
    # Variant 8 (Buildings.RoadVariant in C#): the road asset.
    textures.append(("road", None, None))
    for i, (name, diffuse, pbr) in enumerate(textures):
        if name == "road":
            xml += road_material(i, road_mesh)
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
    xml += ktx_material(i, "tank dome", tank_images, "OsmBuildings_TankDome")
    i += 1
    xml += ktx_material(i, "horizontal tank", tank_images, "OsmBuildings_HTank")
    i += 1
    xml += ktx_material(i, "tent", facade_assets.facade("tent", (236, 236, 232), (226, 226, 222), 200, 0, 11), "OsmBuildings_Tent")
    i += 1
    # Low metal roof of the launch complex halls (Buildings.MetalRoofVariant).
    xml += ktx_material(i, "roof metal", facade_assets.metal_roof(0), gable_mesh)
    i += 1
    # The swinging crew access arm (Buildings.CrewArmVariant), with the fixed service structure's palette.
    xml += [f'  <!-- variant {i}: LC-39A crew access arm -->',
            f'  <StaticObject Id="OsmBuildings_Box{i}">',
            f'    <PartModel Id="OsmBuildings_Box{i}_Model">',
            '      <Mesh Id="OsmBuildings_CrewArm" />',
            '      <Material Id="OsmBuildings_Fss_Material" />',
            '    </PartModel>',
            '  </StaticObject>']
    i += 1
    xml.append("</Assets>")
    with open(os.path.join(MOD_DIR, "Assets.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(xml) + "\n")
    print(f"wrote assets for {i} variants to {MOD_DIR}")


def road_material(i, mesh):
    """The road asset: mipmapped KTX2 textures, so the lane lines do not shimmer at a distance."""
    diffuse, pbr = road_images()
    landmark_assets.write_ktx2(os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_Diffuse.ktx2"), diffuse)
    landmark_assets.write_ktx2(os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_PBR.ktx2"), pbr)
    for stale in (f"OsmBuildings_V{i}_Diffuse.png", f"OsmBuildings_V{i}_PBR.png"):
        path = os.path.join(MOD_DIR, "Textures", stale)
        if os.path.exists(path):
            os.remove(path)
    return [line.replace("_Diffuse.png", "_Diffuse.ktx2").replace("_PBR.png", "_PBR.ktx2") for line in material_xml(i, "road", mesh)]


def ktx_material(i, name, images, mesh):
    """Variant i with mipmapped KTX2 textures (diffuse, AoRoughMetal); returns its Assets.xml lines."""
    for kind, img in zip(("Diffuse", "PBR"), images):
        landmark_assets.write_ktx2(os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_{kind}.ktx2"), img)
        stale = os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_{kind}.png")
        if os.path.exists(stale):
            os.remove(stale)
    return [line.replace("_Diffuse.png", "_Diffuse.ktx2").replace("_PBR.png", "_PBR.ktx2") for line in material_xml(i, name, mesh)]


def material_xml(i, name, mesh):
    return [f'  <!-- variant {i}: {name} -->',
            f'  <PbrMaterial Id="OsmBuildings_V{i}_Material">',
            f'    <Diffuse Path="Textures/OsmBuildings_V{i}_Diffuse.png" Category="Vessel" />',
            '    <Normal Path="Textures/OsmBuildings_Normal.png" Category="Vessel" />',
            f'    <AoRoughMetal Path="Textures/OsmBuildings_V{i}_PBR.png" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="OsmBuildings_Box{i}">',
            f'    <PartModel Id="OsmBuildings_Box{i}_Model">',
            f'      <Mesh Id="{mesh}" />',
            f'      <Material Id="OsmBuildings_V{i}_Material" />',
            '    </PartModel>',
            '  </StaticObject>']


def material(i, name, diffuse, pbr, mesh):
    """Writes the textures of variant i; returns its material and static object for Assets.xml."""
    write_png(os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_Diffuse.png"), TEX_W, TEX_H, diffuse)
    write_png(os.path.join(MOD_DIR, "Textures", f"OsmBuildings_V{i}_PBR.png"), TEX_W, TEX_H, pbr)
    return [f'  <!-- variant {i}: {name} -->',
            f'  <PbrMaterial Id="OsmBuildings_V{i}_Material">',
            f'    <Diffuse Path="Textures/OsmBuildings_V{i}_Diffuse.png" Category="Vessel" />',
            '    <Normal Path="Textures/OsmBuildings_Normal.png" Category="Vessel" />',
            f'    <AoRoughMetal Path="Textures/OsmBuildings_V{i}_PBR.png" Category="Vessel" />',
            '  </PbrMaterial>',
            f'  <StaticObject Id="OsmBuildings_Box{i}">',
            f'    <PartModel Id="OsmBuildings_Box{i}_Model">',
            f'      <Mesh Id="{mesh}" />',
            f'      <Material Id="OsmBuildings_V{i}_Material" />',
            '    </PartModel>',
            '  </StaticObject>']


if __name__ == "__main__":
    main()
