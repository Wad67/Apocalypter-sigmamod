# What the game's exports hold, read straight from the .glb files' JSON (no import): the export list, the game's
# material ids by name, whether a file still has box meshes to fill, a root's donor id.
import json
import os
import re
import struct

from . import paths

_json_cache = {}   # path -> (mtime, json)


def glb_json(path):
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return None
    hit = _json_cache.get(path)
    if hit and hit[0] == mtime:
        return hit[1]
    try:
        with open(path, "rb") as f:
            head = f.read(20)
            if len(head) < 20 or struct.unpack("<I", head[:4])[0] != 0x46546C67:
                return None
            length, kind = struct.unpack("<II", head[12:20])
            if kind != 0x4E4F534A:
                return None
            js = json.loads(f.read(length))
    except (OSError, ValueError):
        return None
    _json_cache[path] = (mtime, js)
    return js


def exports(include_live=False):
    """(name, path) of every game object exported by Export All, sorted by name."""
    d = paths.export_dir()
    if not d or not os.path.isdir(d):
        return []
    out = []
    for f in os.listdir(d):
        if not f.endswith(".glb") or (not include_live and f.endswith("-live.glb")):
            continue
        out.append((f[:-4], os.path.join(d, f)))
    return sorted(out, key=lambda e: e[0].lower())


def material_sources():
    files = []
    md = paths.materials_dir()
    if md and os.path.isfile(os.path.join(md, "materials.glb")):
        files.append(os.path.join(md, "materials.glb"))
    files += [p for _, p in exports(include_live=True)]
    return files


def base_name(name):
    return re.sub(r"\.\d{3}$", "", name)


def game_materials():
    """name -> (extras, file) of every game material the exports know, materials.glb first."""
    out = {}
    for path in material_sources():
        js = glb_json(path)
        for m in (js or {}).get("materials", []):
            ex = m.get("extras") or {}
            if ex.get("es3global") and m.get("name") and m["name"] not in out:
                out[m["name"]] = (ex, path)
    return out


def needs_fill(path):
    js = glb_json(path) or {}
    return any((m.get("extras") or {}).get("unreadable") for m in js.get("meshes", []))


def root_extras(path):
    js = glb_json(path) or {}
    scene = js.get("scenes", [{}])[js.get("scene", 0)]
    nodes = js.get("nodes", [])
    return [(nodes[i].get("name", ""), nodes[i].get("extras") or {}) for i in scene.get("nodes", [])]


def id_owners():
    """es3local / es3prefab id -> the export it came from (its name), over every export. Read once, kept while the
    files don't change."""
    files = exports(include_live=False)
    key = tuple((n, os.path.getmtime(p)) for n, p in files)
    if id_owners.cache and id_owners.cache[0] == key:
        return id_owners.cache[1]
    out = {}
    for name, path in files:
        for n in (glb_json(path) or {}).get("nodes", []):
            ex = n.get("extras") or {}
            for k in ("es3local", "es3prefab"):
                if ex.get(k):
                    out.setdefault(str(ex[k]), name)
    id_owners.cache = (key, out)
    return out


id_owners.cache = None
