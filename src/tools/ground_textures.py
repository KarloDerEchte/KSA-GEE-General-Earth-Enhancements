"""Builds Textures/GEE_GroundDetail.ktx2, the small ground detail texture array of the terrain shader (GroundTexture.cs).

Usage:  python -I src/tools/ground_textures.py [--size 512]

Three CC0 materials from ambientCG (https://ambientcg.com, "Grass001", "Ground037", "Rock030", 1K-JPG zips, downloaded to
src/tools/data/ambientcg/ and not part of the repository) become three layers (0 grass, 1 dirt, 2 rock) of one mipmapped
RGBA8 UNORM KTX2 array, zlib supercompressed (the format the game's libktx loads; see landmark_assets.write_ktx2):
  R  albedo luminance relative to the tile's mean, / 3 (so the mean is 1/3 and mips keep the mean)
  G  tangent normal x  (0.5 + 0.5 n.x)
  B  tangent normal y  (0.5 + 0.5 n.y, OpenGL convention: up in the image)
  A  roughness minus the layer's mean roughness, + 0.5 (the shader only adds the deviation to the game's roughness)
The shader multiplies the satellite colour by the luminance ratio and tilts the shading normal, so only the DETAIL of
the photographed ground is used: the colour and brightness of the terrain stay the satellite's.
"""
import argparse
import io
import os
import struct
import sys
import urllib.request
import zipfile
import zlib

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data", "ambientcg")
OUT = os.path.join(HERE, "..", "..", "Textures", "GEE_GroundDetail.ktx2")
USER_AGENT = "GEE-mod-asset-tool/1.0 (Kitten Space Agency mod; CC0 textures; +https://github.com/)"

# (layer, ambientCG asset id)
MATERIALS = [("grass", "Grass001"), ("dirt", "Ground037"), ("rock", "Rock030")]


def download(asset):
    path = os.path.join(DATA, f"{asset}_1K-JPG.zip")
    if not os.path.exists(path):
        os.makedirs(DATA, exist_ok=True)
        url = f"https://ambientcg.com/get?file={asset}_1K-JPG.zip"
        print("downloading", url)
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=120) as r, open(path + ".part", "wb") as f:
            f.write(r.read())
        os.replace(path + ".part", path)
    return path


def read_map(zf, asset, kind):
    name = f"{asset}_1K-JPG_{kind}.jpg"
    # Read the member by exact name into memory; nothing is extracted to disk.
    return Image.open(io.BytesIO(zf.read(name)))


def box_down(a, factor):
    h, w = a.shape[:2]
    return a.reshape(h // factor, factor, w // factor, factor, *a.shape[2:]).mean(axis=(1, 3))


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def make_layer(asset, size):
    with zipfile.ZipFile(download(asset)) as zf:
        color = np.asarray(read_map(zf, asset, "Color").convert("RGB"), dtype=np.float32) / 255
        normal = np.asarray(read_map(zf, asset, "NormalGL").convert("RGB"), dtype=np.float32) / 255
        rough = np.asarray(read_map(zf, asset, "Roughness").convert("L"), dtype=np.float32) / 255
    n = color.shape[0]
    assert color.shape[:2] == (n, n) and n % size == 0, (asset, color.shape)
    f = n // size
    lin = box_down(srgb_to_linear(color), f)
    lum = lin @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    ratio = lum / max(float(lum.mean()), 1e-4)
    # Stored as ratio / 3 in 8 bits; rescale until the mean survives the clipping at 3.
    s = 1.0
    for _ in range(6):
        stored = np.clip(ratio * s / 3, 0, 1)
        s *= 1.0 / max(float(stored.mean()) * 3, 1e-4)
    stored = np.clip(ratio * s / 3, 0, 1)
    nxy = box_down(normal[..., :2] * 2 - 1, f)
    r = box_down(rough, f)
    # Float "data" of the layer: R ratio/3, G/B normal xy in -1..1, A roughness.
    return np.dstack([stored, nxy[..., 0], nxy[..., 1], r - float(r.mean()) + 0.5]).astype(np.float32)


def quantize(level):
    out = np.empty(level.shape, np.uint8)
    out[..., 0] = np.clip(level[..., 0] * 255 + 0.5, 0, 255)
    out[..., 1] = np.clip((level[..., 1] * 0.5 + 0.5) * 255 + 0.5, 0, 255)
    out[..., 2] = np.clip((level[..., 2] * 0.5 + 0.5) * 255 + 0.5, 0, 255)
    out[..., 3] = np.clip(level[..., 3] * 255 + 0.5, 0, 255)
    return out


def write_ktx2(path, layers):
    """layers: list of float HxWx4 arrays. RGBA8 UNORM, full mip chain, array of len(layers), zlib supercompressed."""
    size = layers[0].shape[0]
    levels = []                                   # per level: bytes of all layers, layer after layer
    cur = [l for l in layers]
    while True:
        levels.append(b"".join(quantize(l).tobytes() for l in cur))
        if cur[0].shape[0] == 1:
            break
        cur = [box_down(l, 2) for l in cur]
    data = [zlib.compress(b, 9) for b in levels]
    n = len(levels)
    samples = b"".join(struct.pack("<IIII", (8 * i) | (7 << 16) | (channel << 24), 0, 0, 255)
                       for i, channel in enumerate((0, 1, 2, 15)))
    block = struct.pack("<IIIIII", 0, 2 | ((24 + len(samples)) << 16), 1 | (1 << 8) | (1 << 16), 0, 0, 0) + samples
    dfd = struct.pack("<I", 4 + len(block)) + block
    dfd_offset = 80 + 24 * n
    offset = dfd_offset + len(dfd)
    index, chunks = [None] * n, []
    for i in reversed(range(n)):                  # smallest level first in the file
        chunks.append(data[i])
        index[i] = struct.pack("<QQQ", offset, len(data[i]), len(levels[i]))
        offset += len(data[i])
    header = bytes([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A])
    header += struct.pack("<9I", 37, 1, size, size, 0, len(layers), 1, n, 3)   # VK_FORMAT_R8G8B8A8_UNORM, zlib
    header += struct.pack("<IIIIQQ", dfd_offset, len(dfd), 0, 0, 0, 0)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + b"".join(index) + dfd + b"".join(chunks))
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=512, choices=(256, 512, 1024))
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()
    layers = [make_layer(asset, args.size) for _, asset in MATERIALS]
    for (name, asset), l in zip(MATERIALS, layers):
        print(f"{name:6} {asset}: ratio sd {float(np.std(l[..., 0] * 3)):.3f}, normal rms {float(np.sqrt((l[..., 1] ** 2 + l[..., 2] ** 2).mean())):.3f}, roughness sd {float(np.std(l[..., 3])):.3f}")
    n = write_ktx2(args.out, layers)
    print(f"wrote {args.out}: {args.size}x{args.size}, {len(layers)} layers, {n} mips, {os.path.getsize(args.out)} bytes")


if __name__ == "__main__":
    sys.exit(main())
