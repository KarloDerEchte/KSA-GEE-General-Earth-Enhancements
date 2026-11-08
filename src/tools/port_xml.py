"""Converts GEE's asset XML (the format up to KSA 2026.11.9) to the format of KSA 2026.11.10.5801.

Changes, as learnt from the game's own Content/Core/*Assets.xml and its loader errors:
  * nested rows may not carry Id: a definition takes Key (PartModel, Capsule, Sphere), a use takes Ref (Mesh, Material);
  * textures are top-level <Texture Id Path Category/> rows, materials point at them with Ref;
  * ClutterObject: the atlas is a child <Atlas Ref/>, a LOD lists <Meshes> and <Materials>, substances are
    <Substances><Substance Ref Phase="Solid"><Volume/></Substance></Substances>.
Used by the generators (convert_text before writing) and standalone: python port_xml.py FILE...
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

KEY_TAGS = {"PartModel", "Capsule", "Sphere", "Box", "ConvexHull"}


def _indent(e, level=0):
    pad = "\n" + "  " * level
    if len(e):
        if not (e.text or "").strip():
            e.text = pad + "  "
        for c in e:
            _indent(c, level + 1)
            if not (c.tail or "").strip():
                c.tail = pad + "  "
        if not (e[-1].tail or "").strip():
            e[-1].tail = pad
    if level and not (e.tail or "").strip():
        e.tail = pad


def convert_text(text):
    root = ET.fromstring(text, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
    textures = {}  # path -> id
    out = []

    def tex_id(path, category):
        if path not in textures:
            stem = os.path.splitext(os.path.basename(path))[0]
            textures[path] = (stem, category)
        return textures[path][0]

    def fix(e, top):
        if not isinstance(e.tag, str):
            return
        for c in list(e):
            fix(c, False)
        if not top:
            if "Path" in e.attrib and "Category" in e.attrib:
                tid = tex_id(e.attrib.pop("Path"), e.attrib.pop("Category"))
                e.set("Ref", tid)
            if "Id" in e.attrib and e.tag not in ("Substance",):
                v = e.attrib.pop("Id")
                e.set("Key" if e.tag in KEY_TAGS else "Ref", v)
        if top and e.tag == "MeshAtlas" and "Id" not in e.attrib:
            e.set("Id", os.path.splitext(os.path.basename(e.attrib["Path"]))[0])
        if e.tag == "ClutterObject":
            atlas = e.attrib.pop("Atlas", None)
            if atlas is not None:
                a = ET.Element("Atlas", {"Ref": atlas})
                e.insert(0, a)
            for sub in e.findall("Substance"):
                e.remove(sub)
                sid = sub.attrib.pop("Id")
                subs = e.find("Substances")
                if subs is None:
                    subs = ET.SubElement(e, "Substances")
                sub.set("Ref", sid)
                sub.set("Phase", "Solid")
                subs.append(sub)
            for lod in e.iter("LOD"):
                meshes = [c for c in lod if c.tag == "Mesh"]
                mats = [c for c in lod if c.tag == "Material"]
                if meshes or mats:
                    for c in meshes + mats:
                        lod.remove(c)
                    m = ET.SubElement(lod, "Meshes")
                    m.extend(meshes)
                    t = ET.SubElement(lod, "Materials")
                    t.extend(mats)

    for row in list(root):
        old = dict(textures)
        fix(row, True)
        if isinstance(row.tag, str) and row.tag == "StaticObject" and row.find("PartModel") is not None:
            # A static object is now a list of sub objects, each a SubPart that holds the part models.
            sid = row.attrib["Id"]
            models = row.findall("PartModel")
            sub = ET.Element("StaticSubObject", {"Id": sid + "_Subpart"})
            comp = ET.SubElement(sub, "PartModels")
            for m in models:
                row.remove(m)
                comp.append(m)
            subs = ET.SubElement(row, "SubObjects")
            ET.SubElement(subs, "SubObject", {"Key": sid + "_Sub", "Ref": sid + "_Subpart"})
            out.append(sub)
        for path, (tid, cat) in textures.items():
            if path not in old:
                out.append(ET.Element("Texture", {"Id": tid, "Path": path, "Category": cat}))
        out.append(row)
    # the first MeshAtlas rows stay in front of the textures
    for c in list(root):
        root.remove(c)
    atlases = [e for e in out if isinstance(e.tag, str) and e.tag == "MeshAtlas"]
    rest = [e for e in out if e not in atlases]
    root.extend(atlases + rest)
    _indent(root)
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(root, encoding="unicode") + "\n"


if __name__ == "__main__":
    for path in sys.argv[1:]:
        text = open(path, encoding="utf-8").read()
        if re.search(r"<Texture Id=", text):
            print("already converted:", path)
            continue
        open(path, "w", encoding="utf-8", newline="\n").write(convert_text(text))
        print("converted", path)
