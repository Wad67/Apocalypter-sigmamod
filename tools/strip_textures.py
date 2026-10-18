#!/usr/bin/env python3
"""Strip the game's own textures out of a SigmaMod .glb.

    python3 strip_textures.py <in.glb> [out.glb] [--ids-from other.glb ...]     (out defaults to in, overwritten)

A material that carries the game's id (extras.es3global, written by Export All and kept by Blender when it
exports custom properties) is replaced by the game's own material when SigmaMod loads the template, so the
textures Blender embeds for it are never used: on the Sigma they were 158 of its 159 MB. This removes the
texture references from those materials, then every image, texture and sampler nothing uses any more, and packs
the binary again. Materials without the id (your own) keep their textures. Nothing else changes.

--ids-from: a material that has lost the id (a material appended or copied in Blender drops its custom properties)
takes the extras of the material of the same name in that .glb (an Export All file, or an earlier export that
still had them). Repeatable; the first file that has the name wins.

From Blender directly: File > Export > glTF 2.0, Data > Material > Images: None leaves every image out (only
right when every material is a game one). Standard library only.
"""
import json, struct, sys

TEX_KEYS = ('baseColorTexture', 'metallicRoughnessTexture')
MAT_KEYS = ('normalTexture', 'occlusionTexture', 'emissiveTexture')


def read(path):
    b = open(path, 'rb').read()
    magic, version, length = struct.unpack('<III', b[:12])
    if magic != 0x46546C67:
        sys.exit(path + ': not a .glb')
    off, js, binary = 12, None, b''
    while off < length:
        clen, ctype = struct.unpack('<II', b[off:off + 8])
        chunk = b[off + 8:off + 8 + clen]
        if ctype == 0x4E4F534A:
            js = json.loads(chunk)
        elif ctype == 0x004E4942:
            binary = chunk
        off += 8 + clen
    return js, binary


def write(path, g, binary):
    js = json.dumps(g, separators=(',', ':')).encode()
    js += b' ' * (-len(js) % 4)
    binary += b'\0' * (-len(binary) % 4)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + (8 + len(binary) if binary else 0)))
        f.write(struct.pack('<II', len(js), 0x4E4F534A) + js)
        if binary:
            f.write(struct.pack('<II', len(binary), 0x004E4942) + binary)


def main():
    args, refs_from = [], []
    it = iter(sys.argv[1:])
    for a in it:
        if a == '--ids-from':
            refs_from.append(next(it, None) or sys.exit('--ids-from needs a .glb'))
        else:
            args.append(a)
    if not args:
        print(__doc__)
        sys.exit(1)
    src = args[0]
    dst = args[1] if len(args) > 1 else src
    g, binary = read(src)
    before = len(binary)

    # ids back from other files, by material name
    known = {}
    for path in refs_from:
        for m in read(path)[0].get('materials', []):
            ex = m.get('extras') or {}
            if ex.get('es3global') and m.get('name') not in known:
                known[m.get('name')] = ex
    restored = 0
    for m in g.get('materials', []):
        if not (m.get('extras') or {}).get('es3global') and m.get('name') in known:
            m['extras'] = dict(known[m['name']], **(m.get('extras') or {}))
            restored += 1
    if refs_from:
        print('%d materials took their game id from %s' % (restored, ', '.join(refs_from)))

    # the game's materials drop their texture references
    stripped = 0
    for m in g.get('materials', []):
        if not (m.get('extras') or {}).get('es3global'):
            continue
        pbr = m.get('pbrMetallicRoughness', {})
        for k in TEX_KEYS:
            if pbr.pop(k, None) is not None:
                stripped += 1
        for k in MAT_KEYS:
            if m.pop(k, None) is not None:
                stripped += 1

    # what is still used: textures by materials, images and samplers by those textures
    def refs(m):
        pbr = m.get('pbrMetallicRoughness', {})
        for k in TEX_KEYS:
            if k in pbr:
                yield pbr[k]
        for k in MAT_KEYS:
            if k in m:
                yield m[k]
    used_tex = sorted({r['index'] for m in g.get('materials', []) for r in refs(m)})
    tex_map = {old: new for new, old in enumerate(used_tex)}
    textures = [g['textures'][i] for i in used_tex] if 'textures' in g else []
    used_img = sorted({t['source'] for t in textures if 'source' in t})
    img_map = {old: new for new, old in enumerate(used_img)}
    used_smp = sorted({t['sampler'] for t in textures if 'sampler' in t})
    smp_map = {old: new for new, old in enumerate(used_smp)}
    for m in g.get('materials', []):
        for r in refs(m):
            r['index'] = tex_map[r['index']]
    for t in textures:
        if 'source' in t:
            t['source'] = img_map[t['source']]
        if 'sampler' in t:
            t['sampler'] = smp_map[t['sampler']]
    images = [g['images'][i] for i in used_img] if 'images' in g else []
    samplers = [g['samplers'][i] for i in used_smp] if 'samplers' in g else []
    for key, val in (('textures', textures), ('images', images), ('samplers', samplers)):
        if val:
            g[key] = val
        else:
            g.pop(key, None)

    # buffer views still referenced (accessors, the images left, sparse accessors), packed again in order
    live = set()
    for a in g.get('accessors', []):
        if 'bufferView' in a:
            live.add(a['bufferView'])
        sp = a.get('sparse')
        if sp:
            live.add(sp['indices']['bufferView'])
            live.add(sp['values']['bufferView'])
    for im in images:
        if 'bufferView' in im:
            live.add(im['bufferView'])
    views = g.get('bufferViews', [])
    view_map, new_views, out = {}, [], bytearray()
    for i, v in enumerate(views):
        if i not in live:
            continue
        if v.get('buffer', 0) != 0:
            sys.exit('only single-buffer .glb files are handled')
        start = v.get('byteOffset', 0)
        data = binary[start:start + v['byteLength']]
        out += b'\0' * (-len(out) % 4)
        nv = dict(v)
        nv['byteOffset'] = len(out)
        out += data
        view_map[i] = len(new_views)
        new_views.append(nv)
    for a in g.get('accessors', []):
        if 'bufferView' in a:
            a['bufferView'] = view_map[a['bufferView']]
        sp = a.get('sparse')
        if sp:
            sp['indices']['bufferView'] = view_map[sp['indices']['bufferView']]
            sp['values']['bufferView'] = view_map[sp['values']['bufferView']]
    for im in images:
        if 'bufferView' in im:
            im['bufferView'] = view_map[im['bufferView']]
    g['bufferViews'] = new_views
    if g.get('buffers'):
        g['buffers'][0]['byteLength'] = len(out)
    write(dst, g, bytes(out))
    print('%s: %d texture references on game materials removed, %d images left, binary %.1f MB -> %.1f MB'
          % (dst, stripped, len(images), before / 1e6, len(out) / 1e6))


if __name__ == '__main__':
    main()
