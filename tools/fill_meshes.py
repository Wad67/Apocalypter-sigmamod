#!/usr/bin/env python3
# Fills the geometry of non-readable vanilla meshes into a template exported in game.
#
# A Unity player keeps most mesh data on the GPU only, so the in-game export writes those meshes
# as bounding boxes with extras {unreadable, vertexCount, subMeshCount, bounds, submeshMaterials}.
# This reads the real vertex data from the game's asset files (UnityPy) and replaces each box.
# Matching uses mesh name, vertex count, submesh count and local bounds together. Meshes copied at
# runtime carry another name (e.g. "wheel_5_armored(Clone)908"); those fall back to vertex count,
# submesh count and bounds alone.
#
#   python3 -m venv ~/.venv/unitypy && ~/.venv/unitypy/bin/pip install UnityPy
#   ~/.venv/unitypy/bin/python tools/fill_meshes.py <Apocalypter_Data/data.unity3d> in.glb [out.glb]
#
# Conversion to glTF matches GltfWriter: X mirrored, winding reversed, V flipped.
import json, struct, sys, time

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

GLB_MAGIC, CHUNK_JSON, CHUNK_BIN = 0x46546C67, 0x4E4F534A, 0x004E4942


def read_glb(path):
    d = open(path, "rb").read()
    magic, _, _ = struct.unpack_from("<III", d, 0)
    if magic != GLB_MAGIC:
        sys.exit(path + ": not a GLB")
    off, js, bin_ = 12, None, b""
    while off + 8 <= len(d):
        ln, ty = struct.unpack_from("<II", d, off)
        chunk = d[off + 8: off + 8 + ln]
        if ty == CHUNK_JSON:
            js = json.loads(chunk.decode("utf-8"))
        elif ty == CHUNK_BIN:
            bin_ = bytes(chunk)
        off += 8 + ln
    return js, bytearray(bin_)


def write_glb(path, js, bin_):
    while len(bin_) % 4:
        bin_.append(0)
    js["buffers"] = [{"byteLength": len(bin_)}]
    j = json.dumps(js, separators=(",", ":")).encode("utf-8")
    j += b" " * ((4 - len(j) % 4) % 4)
    total = 12 + 8 + len(j) + 8 + len(bin_)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", GLB_MAGIC, 2, total))
        f.write(struct.pack("<II", len(j), CHUNK_JSON)); f.write(j)
        f.write(struct.pack("<II", len(bin_), CHUNK_BIN)); f.write(bin_)


def add_accessor(js, bin_, fmt, comps, rows, target, gl_type, minmax=False):
    while len(bin_) % 4:
        bin_.append(0)
    off = len(bin_)
    for r in rows:
        bin_ += struct.pack("<" + fmt * comps, *r) if comps > 1 else struct.pack("<" + fmt, r)
    js.setdefault("bufferViews", []).append({"buffer": 0, "byteOffset": off, "byteLength": len(bin_) - off, "target": target})
    acc = {"bufferView": len(js["bufferViews"]) - 1, "componentType": 5126 if fmt == "f" else 5125,
           "count": len(rows), "type": gl_type}
    if minmax and rows:
        acc["min"] = [min(r[i] for r in rows) for i in range(comps)]
        acc["max"] = [max(r[i] for r in rows) for i in range(comps)]
    js.setdefault("accessors", []).append(acc)
    return len(js["accessors"]) - 1


def main():
    data, src = sys.argv[1], sys.argv[2]
    dst = sys.argv[3] if len(sys.argv) > 3 else src
    js, bin_ = read_glb(src)

    want = {}
    for i, m in enumerate(js.get("meshes", [])):
        ex = m.get("extras") or {}
        if ex.get("unreadable"):
            want.setdefault(m.get("name"), []).append(i)
    if not want:
        print("nothing to fill"); return
    print(f"{sum(len(v) for v in want.values())} meshes to fill")

    t = time.time()
    env = UnityPy.load(data)
    found = {}
    by_count = None  # vertex count -> meshes, built only if a name fails to match

    def match(cands, ex):
        b = ex.get("bounds")
        for m in cands:
            if m.m_VertexData.m_VertexCount != int(ex.get("vertexCount", -1)) or len(m.m_SubMeshes) != int(ex.get("subMeshCount", -1)):
                continue
            if b:
                c, e = m.m_LocalAABB.m_Center, m.m_LocalAABB.m_Extent
                if max(abs(c.x - b[0]), abs(c.y - b[1]), abs(c.z - b[2]), abs(e.x - b[3]), abs(e.y - b[4]), abs(e.z - b[5])) > 1e-3:
                    continue
            h = MeshHandler(m)
            h.process()
            return h
        return None

    for obj in env.objects:
        if obj.type.name != "Mesh":
            continue
        try:
            name = obj.peek_name()
        except Exception:
            name = None
        if name is not None and name not in want:
            continue
        m = obj.read()
        if m.m_Name in want:
            found.setdefault(m.m_Name, []).append(m)
    print(f"read asset files in {time.time() - t:.1f}s")

    # Easy Save material id -> glTF material index
    mat_by_ref = {}
    for i, mt in enumerate(js.get("materials", [])):
        r = (mt.get("extras") or {}).get("es3global")
        if r:
            mat_by_ref[r] = i

    filled = missing = 0
    for name, idxs in want.items():
        for i in idxs:
            mesh = js["meshes"][i]
            ex = mesh["extras"]
            cand = match(found.get(name, []), ex)
            if cand is None and ex.get("bounds"):
                if by_count is None:
                    by_count = {}
                    for obj in env.objects:
                        if obj.type.name == "Mesh":
                            m = obj.read()
                            by_count.setdefault(m.m_VertexData.m_VertexCount, []).append(m)
                cand = match(by_count.get(int(ex.get("vertexCount", -1)), []), ex)
                if cand is not None:
                    print(f"  '{name}' matched by vertex count and bounds")
            if cand is None:
                print(f"  no match for '{name}' ({ex.get('vertexCount')} verts)"); missing += 1
                continue

            h = cand
            pos = [(-v[0], v[1], v[2]) for v in h.m_Vertices]
            attrs = {"POSITION": add_accessor(js, bin_, "f", 3, pos, 34962, "VEC3", True)}
            if h.m_Normals and len(h.m_Normals) == len(pos):
                attrs["NORMAL"] = add_accessor(js, bin_, "f", 3, [(-n[0], n[1], n[2]) for n in h.m_Normals], 34962, "VEC3")
            if h.m_UV0 and len(h.m_UV0) == len(pos):
                attrs["TEXCOORD_0"] = add_accessor(js, bin_, "f", 2, [(u[0], 1.0 - u[1]) for u in h.m_UV0], 34962, "VEC2")

            old_prims = mesh.get("primitives", [])
            refs = ex.get("submeshMaterials") or []
            prims = []
            for s, tris in enumerate(h.get_triangles()):
                idx = []
                for a, b_, c in tris:
                    idx += [a, c, b_]  # mirror flips handedness: reverse winding
                p = {"attributes": attrs, "indices": add_accessor(js, bin_, "I", 1, idx, 34963, "SCALAR"), "mode": 4}
                mat = mat_by_ref.get(refs[s]) if s < len(refs) and refs[s] else None
                if mat is None and old_prims and "material" in old_prims[0]:
                    mat = old_prims[0]["material"]
                if mat is not None:
                    p["material"] = mat
                prims.append(p)
            mesh["primitives"] = prims
            for k in ("unreadable", "bounds"):
                ex.pop(k, None)
            ex["filled"] = True
            filled += 1

    write_glb(dst, js, bin_)
    print(f"filled {filled}, unmatched {missing} -> {dst}")


if __name__ == "__main__":
    main()
