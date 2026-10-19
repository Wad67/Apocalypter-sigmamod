# The kit: what a car needs, taken from the Sigma (SigmaMod's own car). Each section of its script is made of roles,
# the objects it needs. Your objects are matched to them first, by name, within the whole asset or a group you point
# at; a role you can't fill takes the Sigma's object, brought out of its .glb and placed in proportion to your body
# (the Sigma's front wheel a quarter of the way along its body lands a quarter of the way along yours). The section's
# lines go into your script written for what was picked (sigmasrc.section_code).
import os
import re

import bpy
from mathutils import Vector

from . import checks, paths, script, sigmasrc

TAG, TAG_NAME = "sigma_kit", "sigma_kit_name"
BLOCK_NOTE = "-- (written by the SigmaMod add-on from the objects picked for it: change the picks, not these lines)"
_busy = set()   # roles whose picker is being set from code: not a user's pick


def _picked(self, context):
    if self.as_pointer() in _busy:
        return
    self.auto = False
    k = sigmasrc.kit()
    role = k["roles"][self.role].role(self.role) if k and self.role in k["roles"] else None
    self.none = self.obj is None and role is not None and role.optional and not role.multi   # cleared: left out
    if role and role.kind == 'collider' and self.obj is not None:
        show_as_collider(self.obj)
    claim(context.scene.sigma, self.role)
    if role and role.multi and self.obj is not None:
        kids = fitting_children(self.obj, role, k)
        if kids:   # a group picked for a role of any number: what's in it that fits
            s = context.scene.sigma
            _set(self, kids[0])
            for o in kids[1:]:
                if not any(r.obj == o for r in s.roles if r.role == self.role):
                    add_item(s, self.section, self.role, o, auto=False)
    from . import mascot
    mascot.poke()


def claim(s, role):
    # the artist has added, removed or picked something in this role: all of it is theirs, detection leaves it be
    for r in s.roles:
        if r.role == role:
            r.auto = False


def _scoped(self, context):
    s = context.scene.sigma
    _remove_where(s, lambda r: r.section == self.title and r.auto)
    detect(context)


class SigmaRole(bpy.types.PropertyGroup):
    section: bpy.props.StringProperty()
    role: bpy.props.StringProperty()
    obj: bpy.props.PointerProperty(type=bpy.types.Object, update=_picked, description="The object that does this (click, or "
                                   "use the eyedropper). A group picked for one of any number: everything in it that fits")
    auto: bpy.props.BoolProperty(description="Found by name, not picked")
    none: bpy.props.BoolProperty(description="Left out on purpose")


class SigmaSectionState(bpy.types.PropertyGroup):
    title: bpy.props.StringProperty()
    scope: bpy.props.PointerProperty(type=bpy.types.Object, update=_scoped, name="Look in",
                                     description="Where your objects for this are: a group (an empty with them under it). "
                                                 "Empty: the whole asset")


def _set(item, obj):
    _busy.add(item.as_pointer())
    try:
        item.obj = obj
    finally:
        _busy.discard(item.as_pointer())


def show_as_collider(o):
    # a box only: wireframe in Blender, so what's behind it shows; not rendered
    if o.type == 'MESH':
        o.display_type = 'WIRE'
        o.hide_render = True


def role_of(k, name):
    sec = k["roles"].get(name)
    return sec.role(name) if sec else None


def collider_objects(s):
    """Everything that is only a collider: pieces made from meshes, the game's collider nodes, and the objects picked
    for collider roles (click boxes, get-in boxes, lock volumes)."""
    k = sigmasrc.kit()
    out = [o for o in bpy.context.scene.objects if o.get("sigma") == "collider"]
    if k:
        out += [r.obj for r in s.roles if r.obj is not None and getattr(role_of(k, r.role), "kind", None) == 'collider']
    return list(dict.fromkeys(out))


def made_colliders(o):
    """(pieces made from it, how many are out of date). Pieces tools/colliders.py wrote into a .glb (named
    "<it> collider N", under it) count too; they carry no shape to compare, so they count as up to date."""
    from .colliders import shape_key
    pieces = [c for c in bpy.context.scene.objects if c.get("generated") and
              (c.get("sigma_from") == o.name or (not c.get("sigma_from") and c.name.startswith(o.name + " collider ")))]
    return len(pieces), sum(1 for c in pieces if c.get("sigma_shape") and c.get("sigma_shape") != shape_key(o))


def add_item(s, section, role, obj=None, auto=True):
    it = s.roles.add()
    it.section, it.role, it.auto = section, role, auto
    _set(it, obj)
    return it


def _remove_where(s, pred):
    # by index, from the end: removing an entry moves the ones after it, so references to them can't be kept
    for i in range(len(s.roles) - 1, -1, -1):
        if pred(s.roles[i]):
            s.roles.remove(i)


def items(s, role):
    return [r for r in s.roles if r.role == role]


def state(s, title):
    for st in s.sections:
        if st.title == title:
            return st
    st = s.sections.add()
    st.title = title
    return st


def picks(s, k):
    """role -> [object names] picked (in the scene)."""
    out = {}
    names = set(bpy.context.scene.objects.keys())
    for r in s.roles:
        if r.obj is not None and r.obj.name in names:
            out.setdefault(r.role, []).append(r.obj.name)
    return out


# ---- finding the asset's own objects for roles

def norm(name):
    n = re.sub(r"\.\d{3}$", "", name.lower())
    n = re.sub(r"\s*\(\d+\)$", "", n)
    return re.sub(r"[^a-z0-9]", "", n)


def fits(obj, role, k):
    """Whether an object could be this role, by the role's type: a spot is an empty, a marker an empty or a mesh,
    everything else a mesh."""
    if obj.get("sigma") == "collider" or obj.type not in ('MESH', 'EMPTY'):
        return False
    if role.kind == 'marker':
        return True
    if role.kind == 'spot':
        return obj.type == 'EMPTY'
    return obj.type == 'MESH'


def fitting_children(obj, role, k):
    kids = [c for c in obj.children if fits(c, role, k)]
    return kids if kids else []


def detect(context):
    """Pick for every role nobody has picked for, from the asset's objects: the object brought from the Sigma for that
    role, else one named exactly as it, else one named as it with capitals, spaces and .001 aside. A role of any number
    in a group pointed at takes what in it fits. Worked out again each time (so a file saved with older picks gets
    today's), changing only what differs; what the artist picked or left out stays. One object, one role."""
    s = context.scene.sigma
    k = sigmasrc.kit()
    if not k or s.root is None:
        return
    objs = checks.asset_objects(s)
    mine = {r.role for r in s.roles if not r.auto}   # touched by the artist: theirs, as they left it
    _remove_where(s, lambda r: r.auto and r.role in mine)   # (a file from before this kept guesses beside picks)
    used = {r.obj for r in s.roles if not r.auto and r.obj is not None}
    want = {}
    for sec in k["sections"]:
        scope = next((st.scope for st in s.sections if st.title == sec.title), None)
        pool = [o for o in (([scope] + list(scope.children_recursive)) if scope else objs) if o != s.root]
        for role in sec.roles:
            if role.name in mine:
                continue
            names = sigmasrc.sigma_list(sec.code, role.name) if role.multi else [role.name]
            known = {norm(n) for n in names}
            free = [o for o in pool if o not in used]
            found = [o for o in free if o.get(TAG_NAME) == role.name] or \
                    [o for o in free if o.name in names and fits(o, role, k)] or \
                    [o for o in free if norm(o.name) in known and fits(o, role, k)]
            if role.multi and scope and not found:
                found = [o for o in scope.children if o not in used and fits(o, role, k)]
            if not role.multi:
                found = found[:1]
            used.update(found)
            want[role.name] = (sec.title, found)
    for sec in k["sections"]:
        if not any(st.title == sec.title for st in s.sections):
            state(s, sec.title)
    for name, (title, found) in want.items():
        have = [r.obj for r in s.roles if r.role == name]
        if [o.name if o else None for o in have] == ([o.name for o in found] or [None]):
            continue
        _remove_where(s, lambda r, n=name: r.role == n)
        for o in found:
            add_item(s, title, name, o)
        if not found:
            add_item(s, title, name)


def by_hand(src, sec, p):
    """Whether a script without the section's block uses it all the same: it names every object picked for what the
    section can't do without (any of them, when it can do without all)."""
    if not src:
        return False
    needed = [r for r in sec.roles if not r.optional]
    names = [n for r in (needed or sec.roles) for n in p.get(r.name, [])]
    if not names:
        return False
    code = "\n".join(sigmasrc.code_of(l) for l in src.splitlines())
    used = set(sigmasrc.names_in(code, set(names)))
    return all(n in used for n in names) if needed else bool(used)


def status(s, sec, p, src):
    """(every role it can't do without picked, in the script ("block": written by the add-on, "hand": the script uses
    it its own way, None), the block as the picks would write it now)"""
    ok = all(p.get(r.name) for r in sec.roles if not r.optional)
    block = block_of(src, sec)
    if block is not None:
        return ok, "block", block.strip() == section_block(sec, p).strip()
    return ok, ("hand" if by_hand(src, sec, p) else None), True


# ---- the script's sections

def header(sec):
    return "-- ## " + sec.title


def section_block(sec, p):
    k = sigmasrc.kit()
    lines = [header(sec) + (" [required]" if sec.required else "")] + ["-- " + l for l in _wrap(sec.desc)] + [BLOCK_NOTE]
    code = "\n".join(l for l in sigmasrc.section_code(sec, p, k["roles"]).split("\n") if l.strip())
    return "\n".join(lines + [code, ""])   # no blank line inside: a block ends at its first one


def _span(lines, sec):
    start = next((i for i, l in enumerate(lines) if l.startswith(header(sec)) and
                  (len(l) == len(header(sec)) or l[len(header(sec))] == " ")), None)
    if start is None:
        return None
    # down to the first blank line (the add-on writes none inside a block), and never past another block or a template
    end = next((i for i in range(start + 1, len(lines)) if not lines[i].strip() or lines[i].startswith(("-- ## ", "-- ### ")) or
                re.match(r'\s*template\s*"', lines[i])), len(lines))
    return start, end


def block_of(src, sec):
    if not src:
        return None
    lines = src.split("\n")
    sp = _span(lines, sec)
    return "\n".join(lines[sp[0]:sp[1]]) if sp else None


def write_sections(s, k, secs, remove=()):
    """These sections' lines into the script as the picks now write them (replaced where they are, else in the
    Sigma's order before the template line); the sections in remove taken out."""
    path = paths.asset_lua(s)
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    src = open(path, encoding="utf-8").read()
    p = picks(s, k)
    order = k["sections"]
    for sec in list(secs) + list(remove):
        lines = src.split("\n")
        sp = _span(lines, sec)
        block = [] if sec in remove else section_block(sec, p).split("\n")
        if sp:
            lines[sp[0]:sp[1]] = block
        elif block:
            later = [x for x in order[order.index(sec) + 1:] if _span(lines, x)]
            at = _span(lines, later[0])[0] if later else script.car_template_at(lines, s.asset_id)
            lines[at:at] = block
        src = "\n".join(lines)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(src)
    script.reload_text(path)


def sync(context):
    """Every kit section the script has, written again from the picks (before export)."""
    s = context.scene.sigma
    k = sigmasrc.kit()
    path = paths.asset_lua(s)
    if s.kind != 'CAR' or not k or not path or not os.path.isfile(path):
        return
    src = script.read_script(s)
    p = picks(s, k)
    stale = [sec for sec in k["sections"] if block_of(src, sec) is not None and block_of(src, sec).strip() != section_block(sec, p).strip()]
    if stale:
        write_sections(s, k, stale)


def _wrap(text, width=110):
    import textwrap
    return textwrap.wrap(text, width) or [""]


# ---- the Sigma's objects, brought in

def body_box(s):
    pts = []
    picked = {r.obj for r in s.roles if r.obj is not None}
    for o in checks.asset_objects(s):
        if o.type == 'MESH' and not o.get(TAG) and o.get("sigma") != "collider" and o not in picked:
            pts += [o.matrix_world @ Vector(c) for c in o.bound_box]
    if not pts:
        return None
    lo = Vector([min(p[i] for p in pts) for i in range(3)])
    hi = Vector([max(p[i] for p in pts) for i in range(3)])
    return lo, hi


def with_needs(k, titles):
    want = []

    def add(sec):
        for d in sec.needs:
            add(d)
        if sec not in want:
            want.append(sec)
    for sec in k["sections"]:
        if sec.title in titles:
            add(sec)
    return [sec for sec in k["sections"] if sec in want]   # in the Sigma's order


def _import(glb, context):
    """The Sigma's objects by their names in its .glb, imported into a collection of their own."""
    existing = {o.name: o for o in bpy.data.objects}
    clash = sigmasrc.node_names(glb) & set(existing)
    for n in clash:   # out of the way, so what comes in keeps its own name
        existing[n].name = n + "~sigma"
    tmp = bpy.data.collections.new("~sigma kit")
    context.scene.collection.children.link(tmp)
    layer = context.view_layer.layer_collection.children[tmp.name]
    old_active, old_sel = context.view_layer.active_layer_collection, list(context.selected_objects)
    context.view_layer.active_layer_collection = layer
    try:
        bpy.ops.import_scene.gltf(filepath=glb)
    finally:
        context.view_layer.active_layer_collection = old_active
        for o in context.selected_objects:
            o.select_set(False)
        for o in old_sel:
            if o.name in context.view_layer.objects:
                o.select_set(True)
    got = {o.name: o for o in tmp.all_objects}
    for o in got.values():
        o[TAG_NAME] = o.name
    for n in clash:   # theirs back; what came in takes the next free name
        if n in got:
            got[n].name = n + "~kit"
        existing[n].name = n
        if n in got:
            got[n].name = n
    return tmp, got


def _drop_rest(tmp, keep):
    for o in list(tmp.all_objects):
        if o not in keep:
            data = o.data
            bpy.data.objects.remove(o)
            if isinstance(data, bpy.types.Mesh) and data.users == 0:
                bpy.data.meshes.remove(data)
    bpy.data.collections.remove(tmp)
    for coll in (bpy.data.materials, bpy.data.images):
        for d in [d for d in coll if d.users == 0]:
            coll.remove(d)


def bring(context, k, roles):
    """The Sigma's objects for these roles, under the root, placed in proportion to the body, and picked for them."""
    s = context.scene.sigma
    want = []   # (section, role, Sigma object name)
    for name in roles:
        sec = k["roles"][name]
        role = sec.role(name)
        for n in (sigmasrc.sigma_list(sec.code, name) if role.multi else [name]):
            want.append((sec, role, n))
    if not want:
        return 0
    context.view_layer.update()   # every matrix current before positions are read off them
    box = body_box(s)
    if box is None:
        raise RuntimeError("Model a body first. I can't fit wheels to a vibe")
    parents = sigmasrc.kit_parents(k["glb"])
    names = {n for _, _, n in want}
    tmp, got = _import(k["glb"], context)
    ref = got.get(k["model"])
    if ref is None:
        _drop_rest(tmp, set())
        raise RuntimeError(f"The Sigma's .glb has no '{k['model']}'")
    rlo = Vector([min((ref.matrix_world @ Vector(c))[i] for c in ref.bound_box) for i in range(3)])
    rhi = Vector([max((ref.matrix_world @ Vector(c))[i] for c in ref.bound_box) for i in range(3)])
    lo, hi = box
    cols = s.root.users_collection or [context.scene.collection]
    keep, brought, hidden = set(), 0, set()
    for sec, role, n in want:
        o = got.get(n)
        if o is None:
            continue
        p_ = parents.get(n)
        while p_ and p_ not in names:
            p_ = parents.get(p_)
        if p_ is None:   # placed where the Sigma has it, in proportion; under another brought object it moves with it
            m = o.matrix_world.copy()
            rel = [(m.translation[i] - rlo[i]) / max(rhi[i] - rlo[i], 1e-6) for i in range(3)]
            m.translation = Vector([lo[i] + rel[i] * (hi[i] - lo[i]) for i in range(3)])
            o.parent = s.root
            o.matrix_world = m
        for x in [o] + list(o.children_recursive):
            keep.add(x)
            x[TAG] = sec.title
            if role.kind == 'hidden':
                hidden.add(x)
            if role.kind == 'collider' or (x.type == 'MESH' and (not x.data.materials or all(
                    mt is None or mt.name.startswith("COLLIDER") for mt in x.data.materials))):
                show_as_collider(x)
        empty = [r for r in items(s, role.name) if r.obj is None]
        if empty:
            empty[0].none = False
            _set(empty[0], o)
        else:
            add_item(s, sec.title, role.name, o, auto=False)
        claim(s, role.name)
        brought += 1
    hide = hidden_collection(context) if hidden else None
    for x in keep:
        for c in list(x.users_collection):
            c.objects.unlink(x)
        for c in ([hide] if x in hidden else cols):
            c.objects.link(x)
    _drop_rest(tmp, keep)
    return brought


HIDDEN = "Hidden in game"


def hidden_collection(context):
    """The collection for what the game keeps hidden (the lifted suspension): hidden in Blender too, but exported."""
    c = bpy.data.collections.get(HIDDEN)
    if c is None:
        c = bpy.data.collections.new(HIDDEN)
    if c.name not in context.scene.collection.children:
        context.scene.collection.children.link(c)
    lc = context.view_layer.layer_collection.children.get(c.name)
    if lc is not None:
        lc.hide_viewport = True
    return c


def _ensure_script(s):
    path = paths.asset_lua(s)
    if not os.path.isfile(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(script.starting_script('CAR', s.asset_id, s.display_name or s.asset_id, s.root))


def _kit_or_report(op):
    k = sigmasrc.kit()
    if not k or not k["sections"]:
        op.report({'ERROR'}, "This SigmaMod's Sigma has no kit in it. Update SigmaMod")
    return k


class SIGMA_OT_add_kit(bpy.types.Operator):
    """Put this in your car: the Sigma's objects for what you haven't got (placed to fit your body: move them where
they belong), and its lines in the script, written for what's picked. What it needs comes along"""
    bl_idname = "sigma.add_kit"
    bl_label = "Add to car"
    bl_options = {'REGISTER', 'UNDO'}

    sections: bpy.props.StringProperty(description="Section titles, one per line; empty: every required one")

    @classmethod
    def poll(cls, context):
        return context.scene.sigma.root is not None

    def execute(self, context):
        s = context.scene.sigma
        k = _kit_or_report(self)
        if not k:
            return {'CANCELLED'}
        titles = set(self.sections.split("\n")) if self.sections else {sec.title for sec in k["sections"] if sec.required}
        secs = with_needs(k, titles)
        if not s.asset_id:
            self.report({'ERROR'}, "Give the asset an id first (step 3)")
            return {'CANCELLED'}
        if context.object and context.object.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        detect(context)
        p = picks(s, k)
        missing = [r.name for sec in secs for r in sec.roles
                   if not p.get(r.name) and not any(it.none for it in items(s, r.name))]
        try:
            n = bring(context, k, missing)
        except RuntimeError as e:
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        _ensure_script(s)
        src, p = script.read_script(s), picks(s, k)
        write_sections(s, k, [sec for sec in secs if not (block_of(src, sec) is None and by_hand(src, sec, p))])
        self.report({'INFO'}, f"In your car: {', '.join(sec.title for sec in secs)}" + (f" ({n} of the Sigma's objects brought)" if n else ""))
        return {'FINISHED'}


class SIGMA_OT_bring_role(bpy.types.Operator):
    """Take the Sigma's object for this (placed to fit your body: move it where it belongs)"""
    bl_idname = "sigma.bring_role"
    bl_label = "The Sigma's"
    bl_options = {'REGISTER', 'UNDO'}

    role: bpy.props.StringProperty()

    def execute(self, context):
        k = _kit_or_report(self)
        if not k:
            return {'CANCELLED'}
        try:
            bring(context, k, [self.role])
        except RuntimeError as e:
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        return {'FINISHED'}


class SIGMA_OT_role_slot(bpy.types.Operator):
    """Another one (or, with remove, this one less)"""
    bl_idname = "sigma.role_slot"
    bl_label = "Another"
    bl_options = {'REGISTER', 'UNDO'}

    role: bpy.props.StringProperty()
    section: bpy.props.StringProperty()
    remove: bpy.props.IntProperty(default=-1)
    leave_out: bpy.props.BoolProperty(description="Leave it out on purpose")

    def execute(self, context):
        s = context.scene.sigma
        if self.leave_out:   # an empty entry marked as such: nothing is looked for again
            _remove_where(s, lambda r: r.role == self.role)
            it = add_item(s, self.section, self.role, auto=False)
            it.none = True
        elif self.remove >= 0:
            at = [i for i, r in enumerate(s.roles) if r.role == self.role]
            if self.remove < len(at):
                s.roles.remove(at[self.remove])
            if not items(s, self.role):
                add_item(s, self.section, self.role, auto=False)
        else:
            _remove_where(s, lambda r: r.role == self.role and r.none)   # had one after all
            add_item(s, self.section, self.role, auto=False)
        claim(s, self.role)
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


class SIGMA_OT_make_role_object(bpy.types.Operator):
    """Make it here, at the 3D cursor, under your asset, named for what it is, and pick it (then move it into place)"""
    bl_idname = "sigma.make_role_object"
    bl_label = "Make one here"
    bl_options = {'REGISTER', 'UNDO'}

    role: bpy.props.StringProperty()

    def execute(self, context):
        s = context.scene.sigma
        k = _kit_or_report(self)
        role = role_of(k, self.role) if k else None
        if role is None or s.root is None:
            return {'CANCELLED'}
        name = role.label
        if role.kind == 'collider':   # a box: a cube of 20 cm, to scale to size
            me = bpy.data.meshes.new(name)
            h = 0.1
            v = [(x, y, z) for x in (-h, h) for y in (-h, h) for z in (-h, h)]
            me.from_pydata(v, [], [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)])
            o = bpy.data.objects.new(name, me)
            show_as_collider(o)
        else:   # a spot or a marker: an empty
            o = bpy.data.objects.new(name, None)
            o.empty_display_type = 'ARROWS' if role.kind == 'spot' else 'CUBE'
            o.empty_display_size = 0.1
        for c in s.root.users_collection or [context.scene.collection]:
            c.objects.link(o)
        o.parent = s.root
        o.matrix_world.translation = context.scene.cursor.location
        sec = k["roles"][self.role]
        empty = [r for r in items(s, self.role) if r.obj is None]
        if empty:
            empty[0].none = False
            _set(empty[0], o)
        else:
            add_item(s, sec.title, self.role, o, auto=False)
        claim(s, self.role)
        for x in context.selected_objects:
            x.select_set(False)
        o.select_set(True)
        context.view_layer.objects.active = o
        self.report({'INFO'}, f"Made '{o.name}' at the 3D cursor. Move it where it belongs" +
                    (" and scale it to size (it's a box: only its size and place count)" if role.kind == 'collider' else ""))
        return {'FINISHED'}


class SIGMA_OT_select_kit(bpy.types.Operator):
    """Select what's picked for this (to move it where it belongs)"""
    bl_idname = "sigma.select_kit"
    bl_label = "Select"

    section: bpy.props.StringProperty()
    role: bpy.props.StringProperty()

    def execute(self, context):
        s = context.scene.sigma
        objs = [r.obj for r in s.roles if r.obj is not None and r.obj.name in context.view_layer.objects
                and (r.role == self.role if self.role else r.section == self.section)]
        if not objs:
            self.report({'INFO'}, "Nothing's picked for it yet")
            return {'CANCELLED'}
        for o in context.selected_objects:
            o.select_set(False)
        for o in objs:
            o.hide_set(False)
            o.select_set(True)
        context.view_layer.objects.active = objs[0]
        return {'FINISHED'}


class SIGMA_OT_update_kit_script(bpy.types.Operator):
    """Write the script's car sections again from what's picked now (export does this too)"""
    bl_idname = "sigma.update_kit_script"
    bl_label = "Update the script"
    bl_options = {'REGISTER'}

    def execute(self, context):
        sync(context)
        return {'FINISHED'}


class SIGMA_OT_remove_kit(bpy.types.Operator):
    """Take this out of your car's script (its objects stay)"""
    bl_idname = "sigma.remove_kit"
    bl_label = "Take out"
    bl_options = {'REGISTER', 'UNDO'}

    section: bpy.props.StringProperty()

    def execute(self, context):
        k = _kit_or_report(self)
        if not k:
            return {'CANCELLED'}
        sec = next((x for x in k["sections"] if x.title == self.section), None)
        if sec:
            write_sections(context.scene.sigma, k, [], remove=[sec])
        return {'FINISHED'}


class SIGMA_OT_open_kit(bpy.types.Operator):
    """Show or hide what this is made of"""
    bl_idname = "sigma.open_kit"
    bl_label = "Open"

    section: bpy.props.StringProperty()

    def execute(self, context):
        s = context.scene.sigma
        s.open_kit = "" if s.open_kit == self.section else self.section
        return {'FINISHED'}


classes = (SIGMA_OT_add_kit, SIGMA_OT_bring_role, SIGMA_OT_make_role_object, SIGMA_OT_role_slot, SIGMA_OT_select_kit, SIGMA_OT_update_kit_script,
           SIGMA_OT_remove_kit, SIGMA_OT_open_kit)
props = (SigmaRole, SigmaSectionState)
