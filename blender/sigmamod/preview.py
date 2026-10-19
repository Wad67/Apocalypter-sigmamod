# Seeing the car as the game will have it, without changing it: the game's parts on its slots (a door on each door
# slot, an engine on the engine mount...; picked at random on request), the game's wheels on its hubs where the
# springs hold them up under the car's weight, the ground they stand on, and doors, hoods and lids swung open as far
# as each part's own FSM swings it. All of it in one collection that is never exported (Export clears it first) and
# can't be clicked by mistake.
import json
import math
import os
import random

import bpy
from mathutils import Matrix, Quaternion, Vector

from . import gamedata, jobs, paths, script, sigmasrc

COLLECTION = "SigmaMod preview"
LIBRARY = "SigmaMod preview parts"
TAG = "sigma_preview"            # on every preview object: what it shows ("part", "wheel", "ground", "ghost")
SLOT = "sigma_preview_slot"      # the slot (or hub) object it is on
SKIP = {"wheel"}                 # kinds shown by the wheels, not as parts on slots


# ---- the game's way of turning things, in Blender's terms

def unity_euler(e):
    """Blender rotation for a local rotation the game gives as Unity Euler angles (degrees): Unity turns about Z, then
    X, then Y; its X is Blender's -X (the X mirror) and its up (Y) is Blender's Z."""
    ex, ey, ez = (math.radians(a) for a in e)
    qx = Quaternion((math.cos(ex / 2), math.sin(ex / 2), 0, 0))
    qy = Quaternion((math.cos(ey / 2), 0, math.sin(ey / 2), 0))
    qz = Quaternion((math.cos(ez / 2), 0, 0, math.sin(ez / 2)))
    u = qy @ qx @ qz   # in Unity's axes (w, x, y, z)
    return Quaternion((u.w, u.x, u.z, -u.y))


def open_rotation(donor):
    """The local rotation (Unity Euler degrees) a part's DoorOpen FSM turns its slot to, or None: from the part's own
    export (<donor>.fsm.json), so a hood that opens upwards and a door that swings out are each as the game has them."""
    d = paths.export_dir()
    if not d or not donor:
        return None
    cache = open_rotation.cache
    path = os.path.join(d, donor + ".fsm.json")
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return None
    if cache.get(path, (None,))[0] == mtime:
        return cache[path][1]
    rot = None
    try:
        fsms = json.load(open(path, encoding="utf-8")).get("root") or {}
        for st in ((fsms.get("DoorOpen") or {}).get("states") or {}).values():
            for a in st.get("actions") or []:
                v = (a.get("iTweenRotateTo") or {}).get("vectorRotation")
                if isinstance(v, list) and len(v) == 3:
                    rot = [float(x) for x in v]
    except (OSError, ValueError, AttributeError):
        rot = None
    cache[path] = (mtime, rot)
    return rot


open_rotation.cache = {}


# ---- where things are

def collection(context, name=COLLECTION, hidden=False):
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name)
        c.hide_select = True
        c.color_tag = 'COLOR_05'
        if hidden:
            c.hide_viewport = True
            c.hide_render = True
    if name == LIBRARY:
        parent = collection(context)
        if c.name not in parent.children:
            parent.children.link(c)
    elif c.name not in context.scene.collection.children:
        context.scene.collection.children.link(c)
    return c


def objects():
    return [o for o in bpy.data.objects if o.get(TAG)]


def slots(context):
    """[(slot object, kind it takes)] for the car's slots and mounts that have something picked, from the kit's roles
    (doors, hood, engine mount, headlights...), wheels aside."""
    from . import extras, kit
    s = context.scene.sigma
    k = sigmasrc.kit()
    if not k or s.root is None:
        return []
    out = []
    for r in s.roles:
        if r.obj is None or r.obj.name not in context.scene.objects:
            continue
        sec = k["roles"].get(r.role)
        role = sec.role(r.role) if sec else None
        if role is None or role.kind not in ('spot', 'marker'):
            continue
        kind = sigmasrc.accepts_of(sec.code, r.role)
        if kind and kind not in SKIP and extras.for_slot(s, r.obj, None if role.multi else r.role) is None:
            out.append((r.obj, kind))
    return out


def hubs(context, src):
    """[(wheel name, hub object, rim rotation)] from the script's wheels, else the kit's (as the picks write them)."""
    from . import kit, tuning
    s = context.scene.sigma
    if not tuning.wheels_in(src):
        k = sigmasrc.kit()
        sec = next((x for x in (k or {}).get("sections", []) if any(r.name.startswith("wheel_hub") for r in x.roles)), None)
        if sec is not None:
            src = sigmasrc.section_code(sec, kit.picks(s, k), k["roles"])
    return [(w, bpy.data.objects.get(at), rot) for w, at, rot in tuning.wheels_in(src) if bpy.data.objects.get(at)]


# ---- the game's parts, read once each into a hidden library and shown as copies

def _library_root(donor):
    return next((o for o in bpy.data.objects if o.get(TAG) == "library" and o.get("sigma_donor") == donor and o.parent is None), None)


def _load_library(context, donor):
    """The game part, imported into the hidden library: its look only (no colliders, no game ids)."""
    root = _library_root(donor)
    if root is not None:
        return root
    path = os.path.join(paths.export_dir(), donor + ".glb")
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    lib = collection(context, LIBRARY, hidden=True)
    for o in new:
        if o.get("sigma") == "collider" or (o.type == 'MESH' and not o.data.polygons):
            bpy.data.objects.remove(o)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        lib.objects.link(o)
        for k in list(o.keys()):
            if k.startswith(("es3", "sigma")):
                del o[k]
        o[TAG] = "library"
        o["sigma_donor"] = donor
    roots = [o for o in new if o.parent is None]
    for c in [c for c in bpy.data.collections if not c.objects and not c.children and c.name.startswith(("colliders", "hidden", "inactive"))]:
        bpy.data.collections.remove(c)
    return roots[0] if roots else None


def _copy(context, root, tag, slot):
    """A copy of the library part (sharing its meshes) in the preview collection."""
    col = collection(context)
    made = {}
    for o in [root] + list(root.children_recursive):
        if o.get("sigma") == "collider":
            continue
        c = o.copy()
        for k in list(c.keys()):   # a look, not a game object: no ids, no tags of the car's
            if k.startswith(("es3", "sigma", "generated")):
                del c[k]
        c[TAG] = tag
        c[SLOT] = slot.name
        c.hide_select = True
        col.objects.link(c)
        made[o] = c
    for o, c in made.items():
        if o.parent in made:
            c.parent = made[o.parent]
            c.matrix_parent_inverse = o.matrix_parent_inverse.copy()
    top = made[root]
    top["sigma_closed"] = [list(r) for r in root.matrix_world]
    return top


def chosen(context, kind, slot, seed):
    """Which of the game's parts of this kind goes on this slot: the first (the same side first for a door), or one at
    random for this slot and seed."""
    names = list(gamedata.part_ids().get(kind, []))
    if not names:
        return None
    side = slot.name.rsplit("_", 1)[-1].upper() if "_" in slot.name else ""
    if side in ("L", "R"):   # a left door on a left slot: the other side's would open the wrong way round
        names = [n for n in names if not n.upper().endswith("_" + ("R" if side == "L" else "L"))] or names
    if seed:
        return random.Random(f"{seed}/{slot.name}").choice(names)
    names.sort(key=lambda n: not n.lower().startswith(kind + "_"))   # the plain one first: wheel_1, hood_1
    return names[0]


def wanted(context):
    """{donor: [(slot, tag)]}: what the preview shows now."""
    s = context.scene.sigma
    out = {}
    if s.preview_parts:
        for slot, kind in slots(context):
            d = chosen(context, kind, slot, s.preview_seed)
            if d:
                out.setdefault(d, []).append((slot, "part"))
    if s.preview_wheels:
        for w, hub, _ in hubs(context, script.read_script(s) or ""):
            d = chosen(context, "wheel", hub, s.preview_seed)
            if d:
                out.setdefault(d, []).append((hub, "wheel"))
    return out


def unfilled(context):
    """The game exports the preview needs whose meshes are still boxes (Export All can't read most of the game's
    meshes; the helper tools fill them from the game's files)."""
    d = paths.export_dir()
    return [os.path.join(d, n + ".glb") for n in wanted(context) if _library_root(n) is None
            and gamedata.needs_fill(os.path.join(d, n + ".glb"))]


def build(context):
    """The preview made again from what it should show."""
    clear(context, library=False)
    for donor, places in wanted(context).items():
        root = _load_library(context, donor)
        if root is None:
            continue
        for slot, tag in places:
            if tag == "wheel":
                mesh = root if root.type == 'MESH' else max((o for o in root.children_recursive if o.type == 'MESH'),
                                                            key=lambda o: len(o.data.vertices), default=None)
                if mesh is None:
                    continue
                o = bpy.data.objects.new(f"{donor} on {slot.name}", mesh.data)
                o[TAG], o[SLOT], o["sigma_donor"], o.hide_select = "wheel", slot.name, donor, True
                collection(context).objects.link(o)
            else:
                o = _copy(context, root, "part", slot)
                o["sigma_donor"] = donor
    s = context.scene.sigma
    for e in s.extras:   # the car's own parts swing too: a ghost of each, posed open
        if e.root is None or e.hinge is None or not open_rotation(e.donor):
            continue
        g = _copy(context, e.root, "ghost", e.hinge)
        g["sigma_donor"] = e.donor
        for x in [g] + list(g.children_recursive):
            x.display_type = 'WIRE'
    if s.preview_wheels:
        me = bpy.data.meshes.new("Ground")
        h = 3.0
        me.from_pydata([(-h, -h, 0), (h, -h, 0), (h, h, 0), (-h, h, 0)], [], [(0, 1, 2, 3)])
        g = bpy.data.objects.new("Ground", me)
        g[TAG], g.hide_select, g.display_type = "ground", True, 'WIRE'
        collection(context).objects.link(g)
    update(context)


def update(context):
    """Every preview object put where it belongs now (the slots and hubs may have moved, the springs changed, the
    doors opened): cheap, no importing."""
    s = context.scene.sigma
    objs = objects()
    if not objs:
        return
    context.view_layer.update()
    src = script.read_script(s) or ""
    from . import tuning
    drop = tuning.rest_drop(context, src) if s.preview_wheels else {}
    rims = {hub.name: (w, rot) for w, hub, rot in hubs(context, src)}
    ground = None
    for o in objs:
        if o.parent is not None:   # moves with the copy's top
            continue
        tag, slot = o.get(TAG), bpy.data.objects.get(o.get(SLOT, ""))
        if tag in ("part", "ghost") and slot is not None:
            hinge = _open_hinge(slot, open_rotation(o.get("sigma_donor")), s.preview_open)
            if tag == "part":
                loc, rot, _ = hinge.decompose()
                o.matrix_world = Matrix.LocRotScale(loc, rot, o.matrix_world.to_scale())
            else:   # the car's own part, from where it sits closed, turned with its slot
                closed = Matrix(o["sigma_closed"]) if "sigma_closed" in o else o.matrix_world
                o.matrix_world = hinge @ slot.matrix_world.inverted() @ closed
                o.hide_viewport = s.preview_open <= 0.001
        elif tag == "wheel" and slot is not None:
            w, rot = rims.get(slot.name, (None, None))
            m = slot.matrix_world.normalized()
            centre = m @ Vector((0, 0, -drop.get(w, 0.0)))   # down its own up, as the spring is
            r = m.to_quaternion() @ (unity_euler(rot) if rot else Quaternion())
            o.matrix_world = Matrix.LocRotScale(centre, r, Vector((1, 1, 1)))
            low = min((o.matrix_world @ Vector(c)).z for c in o.bound_box)
            ground = low if ground is None else min(ground, low)
    for o in objs:
        if o.get(TAG) == "ground":
            from . import kit
            box = kit.body_box(s)
            if ground is not None and box is not None:
                c = (box[0] + box[1]) / 2
                o.matrix_world = Matrix.Translation((c.x, c.y, ground))
            o.hide_viewport = ground is None
    s.preview_ground = ground if ground is not None else 0.0
    s.preview_has_ground = ground is not None


def _open_hinge(slot, rot, frac):
    """The slot's world matrix with its part swung frac of the way open (the game turns the slot itself to the part's
    open rotation, from wherever it is, and back to none when it shuts)."""
    if not rot or frac <= 0.001:
        return slot.matrix_world.copy()
    parent = slot.parent.matrix_world if slot.parent else Matrix()
    local = parent.inverted() @ slot.matrix_world
    loc, q, sc = local.decompose()
    q = q.slerp(unity_euler(rot), frac)
    return parent @ Matrix.LocRotScale(loc, q, sc)


def clear(context, library=True):
    """The preview gone (the library of imported game parts too, unless kept for the next build)."""
    for o in objects():
        if o.get(TAG) == "library" and not library:
            continue
        me = o.data if o.type == 'MESH' and o.get(TAG) == "ground" else None
        bpy.data.objects.remove(o)
        if me is not None and me.users == 0:
            bpy.data.meshes.remove(me)
    for name in ((LIBRARY, COLLECTION) if library else ()):
        c = bpy.data.collections.get(name)
        if c is not None and not c.all_objects:
            bpy.data.collections.remove(c)
    if library:
        for me in [m for m in bpy.data.meshes if m.users == 0]:
            bpy.data.meshes.remove(me)


def ground_clearance(context):
    """How far the body's lowest point is above the ground, in metres, or None."""
    from . import kit
    s = context.scene.sigma
    box = kit.body_box(s)
    if box is None or not s.preview_has_ground:
        return None
    return box[0].z - s.preview_ground


# ---- buttons

def _open_changed(self, context):
    update(context)


class SIGMA_OT_preview_build(jobs.JobMixin, bpy.types.Operator):
    """Show the game's parts on your slots and its wheels on your hubs (from Export All). Nothing of it is exported,
    and it can't be clicked: it's only to look at"""
    bl_idname = "sigma.preview_build"
    bl_label = "Show the preview"
    bl_options = {'REGISTER'}

    @classmethod
    def poll(cls, context):
        return not jobs.busy() and context.scene.sigma.root is not None

    def execute(self, context):
        if not gamedata.exports():
            self.report({'ERROR'}, "Nothing exported from the game yet (step 2 says how): the preview shows the game's parts")
            return {'CANCELLED'}
        todo = unfilled(context)
        if todo and jobs.tools_ready():
            steps = [(f"getting the game's meshes for {os.path.basename(p)[:-4]}",
                      [jobs.venv_python(), os.path.join(paths.tools_dir(), "fill_meshes.py"), paths.data_file(), p]) for p in todo]
            return self.start_job(context, "Preview", steps, lambda ok, out: build(bpy.context))
        build(context)
        return {'FINISHED'}


class SIGMA_OT_preview_toggle(bpy.types.Operator):
    """Show or hide part of the preview"""
    bl_idname = "sigma.preview_toggle"
    bl_label = "Preview"
    bl_options = {'REGISTER'}

    what: bpy.props.StringProperty(options={'HIDDEN'})

    @classmethod
    def description(cls, context, props):
        if props.what == "wheels":
            return ("Show the game's wheels on your hubs, where the springs hold them with the car standing still, and "
                    "the ground under them (with the ground clearance). Tuning's mass and springs move them")
        return ("Show the game's parts on your slots and mounts: a door on each door slot, an engine on the engine "
                "mount, seats, hood, lights... Your own parts (made with the mesh button) show as themselves")

    def execute(self, context):
        s = context.scene.sigma
        key = "preview_wheels" if self.what == "wheels" else "preview_parts"
        s[key] = not s.get(key, False)
        if s.preview_parts or s.preview_wheels:
            return bpy.ops.sigma.preview_build('EXEC_DEFAULT')
        clear(context, library=False)
        return {'FINISHED'}


class SIGMA_OT_preview_randomize(bpy.types.Operator):
    """Put different game parts on every slot and different wheels on the hubs, picked at random from all the game
    has of each kind (as a player might fit them)"""
    bl_idname = "sigma.preview_randomize"
    bl_label = "Randomize"
    bl_options = {'REGISTER'}

    def execute(self, context):
        s = context.scene.sigma
        s.preview_seed = random.randint(1, 1 << 30)
        if not (s.preview_parts or s.preview_wheels):
            s["preview_parts"] = s["preview_wheels"] = True
        return bpy.ops.sigma.preview_build('EXEC_DEFAULT')


class SIGMA_OT_preview_clear(bpy.types.Operator):
    """Take the preview away (and forget the game parts it read in)"""
    bl_idname = "sigma.preview_clear"
    bl_label = "Clear the preview"
    bl_options = {'REGISTER'}

    def execute(self, context):
        s = context.scene.sigma
        s["preview_parts"] = s["preview_wheels"] = False
        s.preview_seed = 0
        clear(context)
        return {'FINISHED'}


class SIGMA_OT_straighten(bpy.types.Operator):
    """Set this slot straight (no rotation against what it's under), where it is. The game turns a door's, hood's or
    lid's slot back to straight every time the part shuts, so a turned slot makes the part jump"""
    bl_idname = "sigma.straighten"
    bl_label = "Straighten it"
    bl_options = {'REGISTER', 'UNDO'}

    names: bpy.props.StringProperty(options={'HIDDEN'})

    def execute(self, context):
        context.view_layer.update()
        for n in self.names.split("\n"):
            o = bpy.data.objects.get(n)
            if o is None:
                continue
            parent = o.parent.matrix_world if o.parent else Matrix()
            loc, _, sc = (parent.inverted() @ o.matrix_world).decompose()
            o.matrix_world = parent @ Matrix.LocRotScale(loc, Quaternion(), sc)
        update(context)
        return {'FINISHED'}


def turned_slots(context):
    """Slots of parts the game swings open (doors, hoods, lids) that are turned: they'd jump straight when shut."""
    from . import extras
    s = context.scene.sigma
    out = []
    for slot, kind in [(r.obj, sigmasrc.accepts_of(k_sec.code, r.role)) for r, k_sec in _slot_roles(s)]:
        if kind not in ("door", "hood", "trunk", "enginedoor"):
            continue
        parent = slot.parent.matrix_world if slot.parent else Matrix()
        q = (parent.inverted() @ slot.matrix_world).to_quaternion()
        if q.angle > math.radians(1):
            out.append(slot)
    return list(dict.fromkeys(out))


def _slot_roles(s):
    k = sigmasrc.kit()
    if not k:
        return []
    out = []
    for r in s.roles:
        sec = k["roles"].get(r.role)
        if r.obj is not None and sec is not None and sec.role(r.role).kind == 'spot':
            out.append((r, sec))
    return out


def draw(l, c, wrap):
    s = c.scene.sigma
    row = l.row(align=True)
    row.operator("sigma.preview_toggle", text="Game parts", icon='MOD_BUILD', depress=s.preview_parts).what = "parts"
    row.operator("sigma.preview_toggle", text="Wheels", icon='MESH_CYLINDER', depress=s.preview_wheels).what = "wheels"
    row = l.row(align=True)
    row.operator("sigma.preview_randomize", icon='FILE_REFRESH')
    row.operator("sigma.preview_clear", text="", icon='X')
    l.prop(s, "preview_open", slider=True)
    if s.preview_wheels:
        gc = ground_clearance(c)
        if gc is not None:
            l.label(text=f"Ground clearance: {gc * 100:.0f} cm (standing still, on its springs)", icon='MOD_FLOOR')
        if not hubs(c, script.read_script(s) or ""):
            wrap(l, c, "No wheel hubs yet: the wheels show once Wheels and suspension is in the car.", icon='INFO', boxes=2)
    wrap(l, c, "Only to look at: none of it is exported, and Export takes it away first. Change Tuning below and the "
               "wheels move as the springs would.", boxes=2)


classes = (SIGMA_OT_preview_build, SIGMA_OT_preview_toggle, SIGMA_OT_preview_randomize, SIGMA_OT_preview_clear, SIGMA_OT_straighten)
