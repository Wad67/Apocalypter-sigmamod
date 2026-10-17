#!/usr/bin/env python3
"""Colliders for an object of a SigmaMod .glb, derived from its own mesh.

    python3 colliders.py <in.glb> <object> [tolerance] [out.glb] [--into <object>]   (out defaults to in, overwritten)

The object's mesh is split into convex pieces by approximate convex decomposition (CoACD: cut where the surface is
concave, until no piece strays further than the tolerance from it, relative to the object's size). On a car body 0.05
(the default) is about seventy pieces with a seventh of their volume more than 5 cm from the body; 0.1 is about thirty
with two fifths so, pieces bridging the openings like curtains; 0.03 is about 130, nearly exact. A mesh that is its own
mirror image across its X = 0 plane (every corner has a twin within 1 mm) is split one half at a time and the pieces
mirrored, so the two sides match exactly. Each piece is written into the file as a child of the object (or of another
object, --into: an empty under it that holds them, such as "Body Colliders"), marked as
a convex mesh collider, the way exports carry the game's colliders: SigmaMod builds them as the object's colliders, and
Blender shows them as objects to look at, move or delete. Run it again and the pieces it wrote before are replaced;
colliders of your own stay. Pieces have at most 128 corners, under Unity's 255-face limit for a convex collider.

Needs coacd and numpy:  python3 -m venv tools/.venv && tools/.venv/bin/pip install coacd numpy
"""
import json, struct, sys
import numpy as np
import coacd

MARK = 'colliders.py'   # extras.generated on the pieces this writes


def read(path):
    b = open(path, 'rb').read()
    magic, _, length = struct.unpack('<III', b[:12])
    if magic != 0x46546C67:
        sys.exit(path + ': not a .glb')
    off, g, binary = 12, None, b''
    while off < length:
        clen, ctype = struct.unpack('<II', b[off:off + 8])
        chunk = b[off + 8:off + 8 + clen]
        if ctype == 0x4E4F534A:
            g = json.loads(chunk)
        elif ctype == 0x004E4942:
            binary = chunk
        off += 8 + clen
    return g, bytearray(binary)


def write(path, g, binary):
    while len(binary) % 4:
        binary += b'\0'
    g['buffers'][0]['byteLength'] = len(binary)
    js = json.dumps(g, separators=(',', ':')).encode()
    js += b' ' * (-len(js) % 4)
    total = 12 + 8 + len(js) + 8 + len(binary)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, total))
        f.write(struct.pack('<II', len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack('<II', len(binary), 0x004E4942)); f.write(binary)


def accessor(g, binary, i, comps):
    a = g['accessors'][i]
    bv = g['bufferViews'][a['bufferView']]
    fmt = {5121: 'B', 5123: 'H', 5125: 'I', 5126: 'f'}[a['componentType']]
    size = struct.calcsize(fmt)
    stride = bv.get('byteStride') or size * comps
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    return np.array([struct.unpack_from('<' + fmt * comps, binary, off + k * stride) for k in range(a['count'])])


def mesh_of(g, binary, node):
    verts, faces = [], []
    for p in g['meshes'][node['mesh']]['primitives']:
        if p.get('mode', 4) != 4:
            continue
        v = accessor(g, binary, p['attributes']['POSITION'], 3)
        f = accessor(g, binary, p['indices'], 1).reshape(-1, 3) if 'indices' in p else np.arange(len(v)).reshape(-1, 3)
        faces.append(f + sum(len(x) for x in verts)); verts.append(v)
    v, f = np.concatenate(verts), np.concatenate(faces)
    # weld vertices the UV seams split, so the decomposition sees one surface
    keys, inverse = np.unique(np.round(v, 5), axis=0, return_inverse=True)
    return keys, inverse.reshape(-1)[f]


def trs(node):
    # a node's transform as a 4 x 4 matrix (glTF: translation, rotation x y z w, scale)
    if 'matrix' in node:
        return np.array(node['matrix'], float).reshape(4, 4).T
    x, y, z, w = node.get('rotation', [0, 0, 0, 1])
    r = np.array([[1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w)],
                  [2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w)],
                  [2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y)]])
    m = np.eye(4); m[:3, :3] = r * node.get('scale', [1, 1, 1]); m[:3, 3] = node.get('translation', [0, 0, 0])
    return m


def below(nodes, top, target):
    # the transform from target's space to top's (target under top), None when it isn't under it
    if top == target:
        return np.eye(4)
    for c in nodes[top].get('children', []):
        m = below(nodes, c, target)
        if m is not None:
            return trs(nodes[c]) @ m
    return None


def symmetric(v, within=0.001):
    # every corner has a twin across X = 0, within the given distance (a 1 mm grid, its neighbouring cells included)
    cells = {tuple(c) for c in np.floor(v / within).astype(int)}
    near = [np.array(d) - 1 for d in np.ndindex(3, 3, 3)]
    for c in np.floor(v * [-1, 1, 1] / within).astype(int):
        if not any(tuple(c + d) in cells for d in near):
            return False
    return True


def append(g, binary, data, target=None):
    while len(binary) % 4:
        binary += b'\0'
    g['bufferViews'].append({'buffer': 0, 'byteOffset': len(binary), 'byteLength': len(data), **({'target': target} if target else {})})
    binary += data
    return len(g['bufferViews']) - 1


def main():
    args, into = sys.argv[1:], None
    if '--into' in args:
        k = args.index('--into'); into = args[k + 1]; del args[k:k + 2]
    if len(args) < 2:
        sys.exit(__doc__)
    src, name = args[0], args[1]
    tolerance = float(args[2]) if len(args) > 2 else 0.05
    out = args[3] if len(args) > 3 else src
    g, binary = read(src)
    nodes = g['nodes']
    index = next((i for i, n in enumerate(nodes) if n.get('name') == name), None)
    if index is None or 'mesh' not in nodes[index]:
        sys.exit(f'{src}: no object named {name!r} with a mesh')

    obj = nodes[index]
    holder, to_holder = obj, np.eye(4)
    if into is not None:
        hi = next((i for i, n in enumerate(nodes) if n.get('name') == into), None)
        if hi is None:
            sys.exit(f'{src}: no object named {into!r} to put the colliders in')
        holder = nodes[hi]
        into_obj = below(nodes, index, hi)
        if into_obj is None:
            sys.exit(f'{src}: {into!r} is not under {name!r}')
        to_holder = np.linalg.inv(into_obj)   # the object's space to the holder's
    # the pieces written before go, wherever they were put (their nodes are left as empty, nameless objects nothing
    # points to: glTF has no way to remove a node without renumbering every reference, and Blender drops unreferenced
    # ones on import)
    old = []
    for n in nodes:
        mine = [c for c in n.get('children', []) if nodes[c].get('extras', {}).get('generated') == MARK
                and nodes[c].get('name', '').startswith(name + ' collider ')]
        if mine:
            old += mine
            n['children'] = [c for c in n['children'] if c not in mine]
            if not n['children']:
                del n['children']
    for c in old:
        nodes[c] = {'name': ''}

    v, f = mesh_of(g, binary, obj)
    coacd.set_log_level('error')
    mirrored = symmetric(v)
    if mirrored:
        # one half, its centreline seam included; the other half is its mirror image
        keep = (v[f][:, :, 0] >= -0.0005).all(1)
        used = np.unique(f[keep]); remap = -np.ones(len(v), int); remap[used] = np.arange(len(used))
        v, f = v[used], remap[f[keep]]
    parts = coacd.run_coacd(coacd.Mesh(v, f), threshold=tolerance, decimate=True, max_ch_vertex=128, preprocess_mode='auto')
    parts = [(np.asarray(pv), np.asarray(pf)) for pv, pf in parts]
    if mirrored:
        parts += [(pv * [-1, 1, 1], pf[:, ::-1]) for pv, pf in parts]   # the twin, wound the other way round

    for k, (pv, pf) in enumerate(parts):
        pv = (np.c_[pv, np.ones(len(pv))] @ to_holder.T)[:, :3].astype(np.float32); pf = np.asarray(pf, np.uint32)
        pos = append(g, binary, pv.tobytes(), 34962)
        idx = append(g, binary, pf.tobytes(), 34963)
        g['accessors'].append({'bufferView': pos, 'componentType': 5126, 'count': len(pv), 'type': 'VEC3',
                               'min': pv.min(0).tolist(), 'max': pv.max(0).tolist()})
        g['accessors'].append({'bufferView': idx, 'componentType': 5125, 'count': pf.size, 'type': 'SCALAR'})
        g['meshes'].append({'name': f'{name} collider {k + 1}',
                            'primitives': [{'attributes': {'POSITION': len(g['accessors']) - 2}, 'indices': len(g['accessors']) - 1}]})
        nodes.append({'name': f'{name} collider {k + 1}', 'mesh': len(g['meshes']) - 1,
                      'extras': {'sigma': 'collider', 'shape': 'mesh', 'convex': True, 'trigger': False, 'generated': MARK}})
        holder.setdefault('children', []).append(len(nodes) - 1)

    write(out, g, binary)
    print(f'{out}: {name!r}: {len(parts)} convex colliders at tolerance {tolerance}' + (' (one half, mirrored)' if mirrored else '')
          + (f', in {into!r}' if into else '')
          + (f', replacing {len(old)}' if old else '') + f'; corners per piece at most {max(len(p[0]) for p in parts)}')


if __name__ == '__main__':
    main()
