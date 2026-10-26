# Rust that eats through paint: the "Sigma Clean" graph (a paint texture over a rust texture, where object-space noise
# and the mesh's edges decide which shows, and the worst of the rust is holes) as a node group with its colour ramps
# made into settings, and the bake the game needs: it can't run a node graph, so paint, rust and holes go into one
# image with alpha, on a plain material whose alpha is clipped (exported as glTF MASK: SigmaMod makes it the Standard
# shader's cutout). The live material is kept, to go back to and bake again.
import os

import bpy
import numpy as np

from . import paths

GROUP = "SigmaMod Rust"
VERSION = 4
LIVE, BAKED = "sigma_rust_baked", "sigma_rust_live"   # on a live material: its baked one; on a baked one: its live one
SIZES = [("1024", "1024", "1024 x 1024: light, blurry up close"), ("2048", "2048", "2048 x 2048: the usual choice"),
         ("4096", "4096", "4096 x 4096: sharp, four times the file size")]


# ---- the graph

def group():
    """The node group, made (or made again, when this add-on changed it) as needed. Where rust grows: noise, plus
    crevices (ambient occlusion), inside corners (concavity), undersides, low down, and sharp edges (chipped, or
    keeping their paint); the curvature and the occlusion need Cycles (rendered view, and the bake)."""
    g = bpy.data.node_groups.get(GROUP)
    if g is not None and g.get("sigma_version") == VERSION:
        return g
    if g is None:
        g = bpy.data.node_groups.new(GROUP, "ShaderNodeTree")
    g.nodes.clear()   # the inside made again; its sockets kept (what materials link to them stays linked)
    sock = lambda name, kind, io, default=None, lo=None, hi=None, desc="": _socket(g, name, kind, io, default, lo, hi, desc)
    sock("Paint", "NodeSocketColor", "INPUT", (0.8, 0.8, 0.8, 1), desc="The paint: your texture (or a colour)")
    sock("Rust", "NodeSocketColor", "INPUT", (0.35, 0.15, 0.05, 1), desc="What's under it: a rust texture")
    sock("Coverage", "NodeSocketFloat", "INPUT", 0.5, 0, 1, "How much has rusted through the paint, overall")
    sock("Holes", "NodeSocketFloat", "INPUT", 0.7, 0, 1, "How much of the worst rust has gone right through: holes "
                                                          "(0: none). Holes only ever open in solid rust")
    sock("Rust before holes", "NodeSocketFloat", "INPUT", 0.08, 0.0, 0.5, "How much solid rust there is between the "
                                                                          "paint and a hole: a ring of rust round "
                                                                          "every hole")
    sock("Blend", "NodeSocketFloat", "INPUT", 0.154, 0.005, 1, "How softly the paint fades into rust")
    sock("Crevices", "NodeSocketFloat", "INPUT", 0.35, 0, 1, "Rust where water sits: crevices, seams, behind and under "
                                                              "things (ambient occlusion: Cycles)")
    sock("Crevice reach", "NodeSocketFloat", "INPUT", 0.15, 0.005, 2, "How far (m) a crevice reaches out")
    sock("Corners", "NodeSocketFloat", "INPUT", 0.3, 0, 1, "Rust in inside corners and folds (the mesh's concavity: Cycles)")
    sock("Edges", "NodeSocketFloat", "INPUT", -0.3, -1, 1, "Sharp outside edges: above 0 they chip and rust first, below "
                                                            "0 they keep their paint (the mesh's curvature: Cycles)")
    sock("Undersides", "NodeSocketFloat", "INPUT", 0.3, 0, 1, "Rust on what faces down: sills, the floor, under lips")
    sock("Low down", "NodeSocketFloat", "INPUT", 0.25, 0, 1, "Rust the nearer the ground, where the road throws water and salt")
    sock("Low down height", "NodeSocketFloat", "INPUT", 0.6, 0.01, 5, "How high (m above the car's origin) Low down reaches")
    sock("Scale", "NodeSocketFloat", "INPUT", 4.5, 0.1, 100, "The size of the rust patches: bigger is smaller patches")
    sock("Detail", "NodeSocketFloat", "INPUT", 4.9, 0, 15, "How ragged the patches' edges are")
    sock("Roughness", "NodeSocketFloat", "INPUT", 1.0, 0, 1, "How broken up the patches are")
    sock("Seed", "NodeSocketFloat", "INPUT", 0.0, -1000, 1000, "Another pattern, the same amount of rust")
    sock("Color", "NodeSocketColor", "OUTPUT")
    sock("Alpha", "NodeSocketFloat", "OUTPUT")
    sock("Rust mask", "NodeSocketFloat", "OUTPUT")
    N, L = g.nodes, g.links
    gi, go = N.new("NodeGroupInput"), N.new("NodeGroupOutput")

    def math(op, a, b=None, clamp=False):
        n = N.new("ShaderNodeMath")
        n.operation, n.use_clamp = op, clamp
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            else:
                L.new(v, n.inputs[i])
        return n.outputs[0]

    def ramp(x, lo, hi, smooth=False):   # 0 at lo, 1 at hi, clamped
        n = N.new("ShaderNodeMapRange")
        n.interpolation_type = 'SMOOTHSTEP' if smooth else 'LINEAR'
        n.clamp = True
        L.new(x, n.inputs["Value"])
        for sock_, v in (("From Min", lo), ("From Max", hi)):
            if isinstance(v, (int, float)):
                n.inputs[sock_].default_value = v
            else:
                L.new(v, n.inputs[sock_])
        return n.outputs["Result"]

    tc = N.new("ShaderNodeTexCoord")
    noise = N.new("ShaderNodeTexNoise")
    noise.noise_dimensions = '4D'
    L.new(tc.outputs["Object"], noise.inputs["Vector"])
    for k in ("Scale", "Detail", "Roughness"):
        L.new(gi.outputs[k], noise.inputs[k])
    L.new(gi.outputs["Seed"], noise.inputs["W"])
    geo = N.new("ShaderNodeNewGeometry")
    ao = N.new("ShaderNodeAmbientOcclusion")
    ao.samples = 16
    L.new(gi.outputs["Crevice reach"], ao.inputs["Distance"])
    sep = N.new("ShaderNodeSeparateXYZ")
    L.new(geo.outputs["Normal"], sep.inputs[0])
    pos = N.new("ShaderNodeSeparateXYZ")
    L.new(tc.outputs["Object"], pos.inputs[0])
    p = geo.outputs["Pointiness"]
    crevice = math('SUBTRACT', 1.0, ao.outputs["AO"])                   # 1 deep in a crevice
    corner = ramp(p, 0.5, 0.42)                                          # 1 in a sharp inside corner
    edge = ramp(p, 0.5, 0.58)                                            # 1 on a sharp outside edge
    under = ramp(sep.outputs["Z"], 0.0, -0.8)                            # 1 facing straight down
    low = ramp(pos.outputs["Z"], gi.outputs["Low down height"], 0.0)     # 1 at the origin's height, 0 at Low down height
    drive = math('ADD', math('ADD', math('MULTIPLY', crevice, gi.outputs["Crevices"]),
                                    math('MULTIPLY', corner, gi.outputs["Corners"])),
                 math('ADD', math('ADD', math('MULTIPLY', edge, gi.outputs["Edges"]),
                                  math('MULTIPLY', under, gi.outputs["Undersides"])),
                      math('MULTIPLY', low, gi.outputs["Low down"])))
    # the noise, pushed down (towards rust) by all of that
    # (flat panels kept towards paint as the original's curvature ramp kept them: mix(noise, 1, 0.18))
    n = noise.outputs["Fac"]
    v = math('SUBTRACT', math('ADD', n, math('MULTIPLY', math('SUBTRACT', 1.0, n), 0.18)), math('MULTIPLY', drive, 0.5))
    # where the paint turns to rust, and the rust to holes: the original ramp's places (0.491, 0.523, 0.677),
    # moved by Coverage, Blend and Holes
    r0 = math('ADD', 0.523, math('MULTIPLY', math('SUBTRACT', gi.outputs["Coverage"], 0.5), 0.6))
    p1 = math('ADD', r0, gi.outputs["Blend"])
    # holes only in solid rust: their top is always "Rust before holes" below where the rust is solid (r0), and
    # Holes moves them further down into it (fewer) or up to that ring (more)
    hole_top = math('SUBTRACT', math('SUBTRACT', r0, gi.outputs["Rust before holes"]),
                    math('MULTIPLY', math('SUBTRACT', 1.0, gi.outputs["Holes"]), 0.35))
    paint = ramp(v, r0, p1, smooth=True)                                   # 0 rust, 1 paint
    alpha = ramp(v, math('SUBTRACT', hole_top, 0.02), hole_top)            # 0 a hole
    mix = N.new("ShaderNodeMix")
    mix.data_type = 'RGBA'
    L.new(paint, mix.inputs["Factor"])
    L.new(gi.outputs["Rust"], mix.inputs["A"])
    L.new(gi.outputs["Paint"], mix.inputs["B"])
    L.new(mix.outputs["Result"], go.inputs["Color"])
    L.new(alpha, go.inputs["Alpha"])
    L.new(math('SUBTRACT', 1.0, paint), go.inputs["Rust mask"])
    for i, nd in enumerate(N):
        nd.location = (200 * (i % 8), -180 * (i // 8))
    g["sigma_version"] = VERSION
    return g


def _socket(g, name, kind, io, default, lo, hi, desc):
    s = next((x for x in g.interface.items_tree if getattr(x, "item_type", "") == 'SOCKET' and x.name == name
              and x.in_out == io), None)
    new = s is None
    if new:
        s = g.interface.new_socket(name, in_out=io, socket_type=kind)
    if default is not None and new:
        s.default_value = default
    if lo is not None:
        s.min_value, s.max_value = lo, hi
    if desc:
        s.description = desc
    return s


def upgrade():
    """Rust made by an earlier version of this add-on brought up to date: the group's inside (and new settings), and
    each rusty material's rust texture on its own coordinates. Writes only when there's something to do."""
    g = bpy.data.node_groups.get(GROUP)
    if g is None or g.get("sigma_version") == VERSION:
        return
    group()
    for mat in bpy.data.materials:
        rn = _image_node(mat, "Rust") if rust_node(mat) is not None else None
        if rn is not None and not mat.node_tree.nodes.get(MAPPING):
            _rust_coords(mat, rn)


def rust_node(mat):
    """The rust group node in a material, or None."""
    if mat is None or not mat.use_nodes:
        return None
    return next((n for n in mat.node_tree.nodes if n.type == 'GROUP' and n.node_tree and n.node_tree.name.startswith(GROUP)), None)


def _image_node(mat, socket_name):
    g = rust_node(mat)
    if g is None or not g.inputs[socket_name].is_linked:
        return None
    n = g.inputs[socket_name].links[0].from_node
    return n if n.type == 'TEX_IMAGE' else None


def _principled(mat):
    return next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)


def make_rusty(mat, rust_image=None):
    """mat made a live rust material: its own base colour (texture or colour) as the paint, rust_image under it."""
    mat.use_nodes = True
    N, L = mat.node_tree.nodes, mat.node_tree.links
    bsdf = _principled(mat)
    if bsdf is None:
        bsdf = N.new("ShaderNodeBsdfPrincipled")
        out = next((n for n in N if n.type == 'OUTPUT_MATERIAL'), None) or N.new("ShaderNodeOutputMaterial")
        L.new(bsdf.outputs[0], out.inputs["Surface"])
    gn = rust_node(mat) or N.new("ShaderNodeGroup")
    gn.node_tree = group()
    gn.location = (bsdf.location.x - 300, bsdf.location.y)
    base = bsdf.inputs["Base Color"]
    if base.is_linked and base.links[0].from_node != gn:   # the paint: whatever fed the base colour
        L.new(base.links[0].from_socket, gn.inputs["Paint"])
    elif not base.is_linked:
        gn.inputs["Paint"].default_value = base.default_value
    if rust_image is not None:
        rn = _image_node(mat, "Rust") or N.new("ShaderNodeTexImage")
        rn.image = rust_image
        rn.location = (gn.location.x - 320, gn.location.y - 300)
        L.new(rn.outputs["Color"], gn.inputs["Rust"])
        _rust_coords(mat, rn)
    L.new(gn.outputs["Color"], base)
    L.new(gn.outputs["Alpha"], bsdf.inputs["Alpha"])
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = 'DITHERED'   # holes shown in the viewport
    return gn


TILING, MAPPING, FOLLOW = "Rust tiling", "Rust mapping", "sigma_rust_follow"


def paint_uv(mat):
    """The UV map the paint texture follows (its UV Map node's), or "" for the mesh's active one."""
    pn = _image_node(mat, "Paint")
    if pn is not None and pn.inputs["Vector"].is_linked:
        n = pn.inputs["Vector"].links[0].from_node
        while n.type == 'MAPPING' and n.inputs["Vector"].is_linked:
            n = n.inputs["Vector"].links[0].from_node
        if n.type == 'UVMAP':
            return n.uv_map
    return ""


def _rust_coords(mat, rn, follow=None):
    """The rust texture's own coordinates: the paint's UV map, repeated "Rust tiling" times across it (a rust
    texture is a small tile: once across a whole car it's stretched); or projected from the object (follow
    'OBJECT': no UV seams, no stretching), its tiling per metre."""
    N, L = mat.node_tree.nodes, mat.node_tree.links
    follow = follow or mat.get(FOLLOW, 'UV')
    mat[FOLLOW] = follow
    tiling = N.get(TILING)
    if tiling is None:
        tiling = N.new("ShaderNodeValue")
        tiling.name = tiling.label = TILING
        tiling.outputs[0].default_value = 8.0
    mapping = N.get(MAPPING)
    if mapping is None:
        mapping = N.new("ShaderNodeMapping")
        mapping.name = mapping.label = MAPPING
    L.new(tiling.outputs[0], mapping.inputs["Scale"])
    for n in [n for n in N if n.get("sigma_rust_coord")]:
        N.remove(n)
    if follow == 'OBJECT':
        src = N.new("ShaderNodeTexCoord")
        L.new(src.outputs["Object"], mapping.inputs["Vector"])
        rn.projection, rn.projection_blend = 'BOX', 0.25
    else:
        src = N.new("ShaderNodeUVMap")
        src.uv_map = paint_uv(mat)
        L.new(src.outputs["UV"], mapping.inputs["Vector"])
        rn.projection = 'FLAT'
    src["sigma_rust_coord"] = True
    L.new(mapping.outputs["Vector"], rn.inputs["Vector"])
    x, y = rn.location
    tiling.location, mapping.location, src.location = (x - 420, y - 200), (x - 220, y), (x - 420, y)


def rust_textures():
    """[(name, file name, path)] of the game's rust colour textures from Export All (its textures folder; not their
    normal or metallic maps)."""
    d = os.path.join(paths.export_dir() or "", "textures")
    if not os.path.isdir(d):
        return []
    out = []
    for f in os.listdir(d):
        low = f.lower()
        if low.endswith(".png") and "rust" in low and not any(k in low for k in ("normal", "metallic", "gloss", "_ao_", "height")):
            out.append((f.rsplit("_", 1)[0], f, os.path.join(d, f)))
    return sorted(out, key=lambda x: [int(t) if t.isdigit() else t for t in __import__("re").split(r"(\d+)", x[0])])


# ---- the bake

def users(mat):
    """The meshes using this material (in the scene)."""
    return [o for o in bpy.context.scene.objects if o.type == 'MESH' and any(s.material == mat for s in o.material_slots)]


# ---- what's left floating: a piece of metal (or rust) with holes all round it, inside one UV island, can't be held
# up by anything; a pinhole in solid metal is noise. Found on the baked image, as connected areas (4-connected),
# in rows of runs (numpy can't label areas by itself; Blender's Python has no scipy)

def _areas(mask):
    """(runs: rows, starts, ends; area id per run; number of areas) for the True pixels of a 2D mask."""
    h, w = mask.shape
    pad = np.zeros((h, w + 2), np.int8)
    pad[:, 1:-1] = mask
    d = np.diff(pad, axis=1)
    rs, ss = np.nonzero(d == 1)
    re_, es = np.nonzero(d == -1)
    es = es - 1
    n = len(rs)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    row_start = np.searchsorted(rs, np.arange(h + 1))
    for r in range(h - 1):
        a, a_end = row_start[r], row_start[r + 1]
        b, b_end = row_start[r + 1], row_start[r + 2]
        while a < a_end and b < b_end:
            if ss[a] <= es[b] and ss[b] <= es[a]:   # overlap: the same area
                pa, pb = find(a), find(b)
                if pa != pb:
                    parent[pb] = pa
            if es[a] < es[b]:
                a += 1
            else:
                b += 1
    roots = np.array([find(i) for i in range(n)], np.int64)
    ids, comp = np.unique(roots, return_inverse=True)
    return (rs, ss, es), comp, len(ids)


def _enclosed(mask, cover, max_px):
    """The pixels of mask's areas that don't reach the edge of their UV island (so holes surround them, inside it),
    up to max_px each."""
    h, w = mask.shape
    out_ = ~cover
    edge = np.zeros_like(mask)   # mask pixels next to a pixel outside the UVs
    edge[:-1] |= out_[1:]
    edge[1:] |= out_[:-1]
    edge[:, :-1] |= out_[:, 1:]
    edge[:, 1:] |= out_[:, :-1]
    edge &= mask
    (rs, ss, es), comp, n = _areas(mask)
    if n == 0:
        return np.zeros_like(mask)
    cs = np.cumsum(edge, axis=1)
    touch_run = (cs[rs, es] - np.where(ss > 0, cs[rs, np.maximum(ss - 1, 0)], 0)) > 0
    touches = np.zeros(n, bool)
    np.logical_or.at(touches, comp, touch_run)
    size = np.zeros(n, np.int64)
    np.add.at(size, comp, es - ss + 1)
    drop = ~touches & (size <= max_px)
    res = np.zeros_like(mask)
    for i in np.nonzero(drop[comp])[0]:
        res[rs[i], ss[i]:es[i] + 1] = True
    return res


def clean(alpha, cover, island_frac, pinhole_px):
    """alpha (2D, 0..1) with the floating pieces cut out (up to island_frac of the UVs' area each) and the pinholes
    up to pinhole_px filled. Returns (alpha, pieces cut, holes filled) in pixels."""
    solid = (alpha >= 0.5) & cover
    cut = _enclosed(solid, cover, int(island_frac * cover.sum())) if island_frac > 0 else np.zeros_like(solid)
    a = alpha.copy()
    a[cut] = 0.0
    filled = _enclosed(cover & (a < 0.5), cover, pinhole_px) if pinhole_px > 0 else np.zeros_like(solid)
    a[filled] = 1.0
    return a, int(cut.sum()), int(filled.sum())


def _dilate(values, cover, steps):
    """values spread out of the UVs by steps pixels (the bake margin), each from a neighbour already set."""
    v, have = values.copy(), cover.copy()
    for _ in range(steps):
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            src_have = np.roll(np.roll(have, dy, 0), dx, 1)
            take = src_have & ~have
            v[take] = np.roll(np.roll(v, dy, 0), dx, 1)[take]
            have |= take
    return v


def bake(context, mat, size, margin=16):
    """Paint, rust and holes into one RGBA image, on a new plain material that uses it, put in mat's place on every
    object using it. Returns the baked material."""
    gn = rust_node(mat)
    if gn is None:
        raise RuntimeError(f"'{mat.name}' isn't a rust material")
    objs = users(mat)
    if not objs:
        raise RuntimeError(f"Nothing in the scene uses '{mat.name}'")
    for o in objs:
        if not o.data.uv_layers:
            raise RuntimeError(f"'{o.name}' has no UV map: the bake goes into an image by its UVs (unwrap it first)")
    scene = context.scene
    s_ = scene.sigma
    N, L = mat.node_tree.nodes, mat.node_tree.links
    out = next(n for n in N if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
    surface = out.inputs["Surface"].links[0].from_socket if out.inputs["Surface"].is_linked else None
    emit = N.new("ShaderNodeEmission")
    target = N.new("ShaderNodeTexImage")
    imgs = {}
    for what in ("Color", "Alpha", "Cover"):
        name = f"{mat.name} bake {what.lower()}"
        old = bpy.data.images.get(name)
        if old is not None:
            bpy.data.images.remove(old)
        imgs[what] = bpy.data.images.new(name, size, size, alpha=True)
    was = {"engine": scene.render.engine, "sel": list(context.selected_objects), "act": context.view_layer.objects.active}
    try:
        scene.render.engine = 'CYCLES'
        was["samples"] = scene.cycles.samples
        scene.cycles.samples = max(1, s_.bake_samples)   # the occlusion is sampled: enough to be smooth
        for o in context.selected_objects:
            o.select_set(False)
        for o in objs:
            o.hide_set(False)
            o.select_set(True)
        context.view_layer.objects.active = objs[0]
        N.active = target
        L.new(emit.outputs[0], out.inputs["Surface"])
        for what in ("Color", "Alpha", "Cover"):
            target.image = imgs[what]
            if what == "Cover":   # where the UVs are: white
                for lk in list(emit.inputs["Color"].links):
                    L.remove(lk)
                emit.inputs["Color"].default_value = (1, 1, 1, 1)
            else:
                L.new(gn.outputs[what], emit.inputs["Color"])
            bpy.ops.object.bake(type='EMIT', margin=margin if what == "Color" else 0, use_clear=True)
        # one image: the colour, and the alpha, cleaned of floating pieces and pinholes
        px = np.empty(size * size * 4, np.float32)
        imgs["Color"].pixels.foreach_get(px)
        a = np.empty(size * size * 4, np.float32)
        imgs["Alpha"].pixels.foreach_get(a)
        c = np.empty(size * size * 4, np.float32)
        imgs["Cover"].pixels.foreach_get(c)
        alpha = a[0::4].reshape(size, size)
        cover = c[0::4].reshape(size, size) > 0.5
        alpha, cut, filled = clean(alpha, cover, s_.bake_islands / 100.0 if s_.bake_islands_on else 0.0,
                                   s_.bake_pinholes)
        alpha = _dilate(alpha, cover, margin)
        px[3::4] = alpha.reshape(-1)
        bake.report = (cut, filled)
        name = f"{mat.name} baked"
        result = bpy.data.images.get(name)
        if result is None or tuple(result.size) != (size, size):
            if result is not None:
                bpy.data.images.remove(result)
            result = bpy.data.images.new(name, size, size, alpha=True)
        result.pixels.foreach_set(px)
        result.alpha_mode = 'STRAIGHT'
        result.update()   # the viewport (and Cycles' image cache) shows the new pixels, not the last bake's
        _save(context, result)
    finally:
        N.remove(emit)
        N.remove(target)
        if surface is not None:
            L.new(surface, out.inputs["Surface"])
        for im in imgs.values():
            bpy.data.images.remove(im)
        scene.render.engine = was["engine"]
        if "samples" in was:
            scene.cycles.samples = was["samples"]
        for o in context.selected_objects:
            o.select_set(False)
        for o in was["sel"]:
            if o.name in context.view_layer.objects:
                o.select_set(True)
        context.view_layer.objects.active = was["act"]
    baked = _baked_material(mat, result)
    for o in objs:
        for s in o.material_slots:
            if s.material == mat:
                s.material = baked
    return baked


def _save(context, img):
    """The baked image written to a PNG beside the .blend (or the asset's folder), and packed into the .blend so it
    goes where the file goes."""
    d = bpy.path.abspath("//") if bpy.data.filepath else paths.asset_dir(context.scene.sigma)
    if d:
        d = os.path.join(d, "baked")
        os.makedirs(d, exist_ok=True)
        img.filepath_raw = os.path.join(d, bpy.path.clean_name(img.name) + ".png")
        img.file_format = 'PNG'
        img.save()
    img.pack()


def _baked_material(live, img):
    """The plain material the game gets: the baked image as the colour, its alpha clipped (glTF MASK)."""
    name = f"{live.name} baked"
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    N, L = m.node_tree.nodes, m.node_tree.links
    N.clear()
    out = N.new("ShaderNodeOutputMaterial")
    bsdf = N.new("ShaderNodeBsdfPrincipled")
    tex = N.new("ShaderNodeTexImage")
    clip = N.new("ShaderNodeMath")
    clip.operation = 'ROUND'   # the exporter's alpha clip: MASK, cutoff 0.5
    tex.image = img
    L.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    L.new(tex.outputs["Alpha"], clip.inputs[0])
    L.new(clip.outputs[0], bsdf.inputs["Alpha"])
    L.new(bsdf.outputs[0], out.inputs["Surface"])
    src = _principled(live)
    if src is not None:   # the rest as the live material has it
        for k in ("Metallic", "Roughness"):
            if not src.inputs[k].is_linked:
                bsdf.inputs[k].default_value = src.inputs[k].default_value
    tex.location, clip.location, bsdf.location, out.location = (-600, 0), (-300, -200), (-100, 0), (250, 0)
    if hasattr(m, "surface_render_method"):
        m.surface_render_method = 'DITHERED'
    m[BAKED] = live.name
    live[LIVE] = m.name
    live.use_fake_user = True   # kept with the file while the objects use the baked one
    return m


def swap(context, mat):
    """Between a live rust material and its baked one, on every object using either."""
    other = bpy.data.materials.get(mat.get(LIVE) or mat.get(BAKED) or "")
    if other is None:
        return None
    for o in users(mat):
        for s in o.material_slots:
            if s.material == mat:
                s.material = other
    return other


# ---- buttons

def _active_mat(context):
    o = context.active_object
    return o.active_material if o is not None and o.type == 'MESH' else None


def rust_items(self, context):
    # identified by file name: Blender takes no identifier over 64 characters (a full path is)
    rust_items.cache = [(f, n, f"The game's {n} texture") for n, f, _ in rust_textures()] or \
        [("", "(no rust textures: Export All from the game first)", "")]
    return paths.stable("rust", rust_items.cache)


rust_items.cache = []


def _load_pick(context):
    """The rust texture picked in the panel, loaded (or the one already in the file)."""
    pick = context.scene.sigma.rust_pick
    path = next((p for _, f, p in rust_textures() if f == pick), None)
    return bpy.data.images.load(path, check_existing=True) if path else None


def picked(self, context):
    """Another rust texture picked in the panel: under the paint of a material that rusts already."""
    mat = _active_mat(context)
    if mat is not None and rust_node(mat) is not None:
        img = _load_pick(context)
        if img is not None:
            make_rusty(mat, img)


class SIGMA_OT_rust_make(bpy.types.Operator):
    """Make the active material rust through: its own texture (or colour) stays as the paint, over the game's rust
    texture picked above; then set how much, where and how holed in this panel"""
    bl_idname = "sigma.rust_make"
    bl_label = "Make it rusty"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        mat = _active_mat(context)
        if mat is None:
            self.report({'ERROR'}, "Select a mesh with a material first")
            return {'CANCELLED'}
        make_rusty(mat, _load_pick(context))
        self.report({'INFO'}, f"'{mat.name}' rusts now. Edges show in Cycles (rendered view) and in the bake")
        return {'FINISHED'}


class SIGMA_OT_rust_follow(bpy.types.Operator):
    """How the rust texture is laid on"""
    bl_idname = "sigma.rust_follow"
    bl_label = "Rust follows"
    bl_options = {'REGISTER', 'UNDO'}

    follow: bpy.props.StringProperty(options={'HIDDEN'})

    @classmethod
    def description(cls, context, props):
        return ("The rust follows the paint's UV map, repeated Rust tiling times across it" if props.follow == 'UV' else
                "The rust is projected from the object, from all sides: no UV seams or stretching; Rust tiling is "
                "then repeats per metre")

    def execute(self, context):
        mat = _active_mat(context)
        rn = _image_node(mat, "Rust") if mat is not None else None
        if rn is None:
            return {'CANCELLED'}
        _rust_coords(mat, rn, self.follow)
        return {'FINISHED'}


class SIGMA_OT_rust_bake(bpy.types.Operator):
    """Bake it for the game: paint, rust and holes into one image (by the objects' UVs), on a plain material the
    game can show, its holes cut out. The objects get that one; the live one is kept to edit and bake again"""
    bl_idname = "sigma.rust_bake"
    bl_label = "Bake it"
    bl_options = {'REGISTER', 'UNDO'}

    material: bpy.props.StringProperty(options={'HIDDEN', 'SKIP_SAVE'})

    def execute(self, context):
        mat = bpy.data.materials.get(self.material) if self.material else _active_mat(context)
        if mat is not None and mat.get(BAKED):   # the baked one: bake its live one again
            mat = bpy.data.materials.get(mat[BAKED])
        if mat is None or rust_node(mat) is None:
            self.report({'ERROR'}, "Not a rust material")
            return {'CANCELLED'}
        try:
            baked = bake(context, mat, int(context.scene.sigma.bake_size))
        except RuntimeError as e:
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        cut, filled = getattr(bake, "report", (0, 0))
        self.report({'INFO'}, f"Baked into '{baked.name}' ({context.scene.sigma.bake_size} px): the objects use it now. "
                              f"Floating pieces cut out: {cut} px; pinholes filled: {filled} px")
        return {'FINISHED'}


class SIGMA_OT_rust_cycles(bpy.types.Operator):
    """See the rust as the bake will make it: the live material back on the objects (if they show the bake), the
    scene rendering with Cycles and this 3D view in the rendered view (the crevices' occlusion and the mesh's
    curvature only exist in Cycles)"""
    bl_idname = "sigma.rust_cycles"
    bl_label = "See it in Cycles"

    def execute(self, context):
        mat = _active_mat(context)
        if mat is not None and mat.get(BAKED):   # the objects show the bake: the live one on them, to see it change
            swap(context, mat)
        elif mat is not None and mat.get(LIVE):   # the panel's live one, its objects showing the bake
            baked = bpy.data.materials.get(mat[LIVE])
            if baked is not None and users(baked):
                swap(context, baked)
        context.scene.render.engine = 'CYCLES'
        sp = context.space_data
        if sp is not None and sp.type == 'VIEW_3D':
            sp.shading.type = 'RENDERED'
        return {'FINISHED'}


class SIGMA_OT_rust_swap(bpy.types.Operator):
    """Swap between the live rust material (to change it) and its baked one (what the game gets) on every object
    using it"""
    bl_idname = "sigma.rust_swap"
    bl_label = "Swap"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        mat = _active_mat(context)
        other = swap(context, mat) if mat is not None else None
        if other is None:
            return {'CANCELLED'}
        return {'FINISHED'}


class SIGMA_PT_materials(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "SigmaMod"
    bl_label = "Materials"
    bl_description = "Rust that eats through the paint, set here and baked for the game"
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 2

    def draw(self, context):
        from .ui import wrap
        l = self.layout
        s = context.scene.sigma
        mat = _active_mat(context)
        if mat is None:
            wrap(l, context, "Select a mesh: its active material is shown here.", icon='INFO')
            return
        l.label(text=mat.name, icon='MATERIAL')
        if mat.get(BAKED):
            wrap(l, context, "Baked: the game gets this one. To change the rust, swap back to the live one, change it, "
                             "and bake again.", icon='CHECKMARK')
            img = next((n.image for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image), None)
            if img:
                l.label(text=f"{img.name}: {img.size[0]} x {img.size[1]}", icon='IMAGE_DATA')
            row = l.row(align=True)
            row.operator("sigma.rust_swap", text="Back to the live one", icon='LOOP_BACK')
            row.operator("sigma.rust_bake", text="Bake again", icon='RENDER_STILL')
            return
        gn = rust_node(mat)
        if gn is None:
            wrap(l, context, "Rust through the paint: its texture (or colour) stays as the paint, over one of the "
                             "game's rust textures; how much, where and how holed is set here, then baked for the game.")
            if rust_textures():
                l.prop(s, "rust_pick", text="Rust")
            else:
                wrap(l, context, "No rust textures from the game yet (Export All, step 2): a rust colour is used.", icon='INFO')
            l.operator("sigma.rust_make", icon='SHADING_TEXTURE')
            return
        paint, rust = _image_node(mat, "Paint"), _image_node(mat, "Rust")
        if mat.get(LIVE) and not users(mat):
            wrap(l, context, "Your objects show the baked one, so changes here don't show on them. See it in Cycles "
                             "puts this one back on them.", icon='ERROR', boxes=1)
        box = l.box().column(align=True)
        box.label(text="1. The paint and what's under it", icon='TEXTURE')
        if paint is not None:
            box.prop(paint, "image", text="Paint")
        else:
            box.prop(gn.inputs["Paint"], "default_value", text="Paint")
        if rust is not None and mat.node_tree.nodes.get(TILING):
            row = box.row(align=True)
            row.prop(mat.node_tree.nodes[TILING].outputs[0], "default_value", text="Rust tiling")
            for mode, label in (('UV', "UV map"), ('OBJECT', "Object")):
                op = row.operator("sigma.rust_follow", text=label, depress=mat.get(FOLLOW, 'UV') == mode)
                op.follow = mode
        if rust_textures():
            box.prop(s, "rust_pick", text="Rust")
        elif rust is not None:
            box.prop(rust, "image", text="Rust")
        else:
            box.prop(gn.inputs["Rust"], "default_value", text="Rust")
        box = l.box().column(align=True)
        box.label(text="2. Where it rusts", icon='MOD_DISPLACE')
        for name in ("Crevices", "Crevice reach", "Corners", "Edges", "Undersides", "Low down", "Low down height"):
            box.prop(gn.inputs[name], "default_value", text=name, slider=name not in ("Crevice reach", "Low down height"))
        if context.scene.render.engine != 'CYCLES':
            wrap(box, context, "Crevices, corners and edges only show in Cycles (and in the bake).", icon='INFO', boxes=2)
        box.operator("sigma.rust_cycles", icon='SHADING_RENDERED')
        box = l.box().column(align=True)
        box.label(text="3. How much", icon='MOD_OPACITY')
        for name in ("Coverage", "Holes", "Rust before holes", "Blend"):
            box.prop(gn.inputs[name], "default_value", text=name, slider=True)
        box.label(text="The pattern")
        for name in ("Scale", "Detail", "Roughness", "Seed"):
            box.prop(gn.inputs[name], "default_value", text=name)
        box = l.box().column(align=True)
        box.label(text="4. Bake it for the game", icon='RENDER_STILL')
        row = box.row(align=True)
        row.prop(s, "bake_size", text="")
        row.prop(s, "bake_samples")
        row = box.row(align=True)
        row.prop(s, "bake_islands_on")
        sub = row.row(align=True)
        sub.enabled = s.bake_islands_on
        sub.prop(s, "bake_islands")
        box.prop(s, "bake_pinholes")
        box.operator("sigma.rust_bake", icon='RENDER_STILL')
        if mat.get(LIVE):
            box.operator("sigma.rust_swap", text="Use the baked one", icon='FORWARD')


classes = (SIGMA_OT_rust_make, SIGMA_OT_rust_follow, SIGMA_OT_rust_bake, SIGMA_OT_rust_cycles, SIGMA_OT_rust_swap,
           SIGMA_PT_materials)
