# Parts that go with the car, in the same .glb and the same script: a door, a hood, a seat for the car's slots. Each
# starts as the game's own part of that kind (so its logic is the game's: a door opens, a seat is sat in), set where the
# car's slot for it is, and takes the artist's mesh in place of the game's. Each is a template of its own: a top object
# of its own in the .glb, a "-- ### Part:" block in the script.
import os
import re

import bpy
from mathutils import Matrix

from . import assets, checks, gamedata, jobs, paths, script, sigmasrc

BLOCK = "-- ### Part: "
NOTE = "-- (written by the SigmaMod add-on for a part made in Blender: change it there, not here)"
SWAPPED = "sigma_mesh_swapped"        # on the mesh the artist's was put into
PART_SWAPPED = "sigma_part_swapped"   # on the part's top object: some mesh of it is the artist's


def _poke():
    from . import mascot
    mascot.poke()


def _clean(self, context):
    cleaned = assets.clean_id(self.asset_id) if self.asset_id else ""
    if cleaned != self.asset_id:
        self.asset_id = cleaned
    _poke()


class SigmaExtra(bpy.types.PropertyGroup):
    root: bpy.props.PointerProperty(type=bpy.types.Object, name="Part",
                                    description="The part's top object: it, and everything under it, is the part")
    asset_id: bpy.props.StringProperty(name="Id", update=_clean,
                                       description="What the game knows the part by: lowercase, digits and _")
    display_name: bpy.props.StringProperty(name="Name", description="What players see when they look at the part")
    slot: bpy.props.StringProperty(description="The car's slot it was made at (the slot object's name)")
    hinge: bpy.props.PointerProperty(type=bpy.types.Object, description="The car's slot it was made at")
    accepts: bpy.props.StringProperty(description="What kind of part it is (door, hood, seat...)")
    donor: bpy.props.StringProperty(description="The game part it started as")


def for_slot(s, hinge, role=None):
    """The part made at this slot object (or, from a file made before a role could have many slots, at the role)."""
    return next((e for e in s.extras if e.hinge == hinge), None) or \
        next((e for e in s.extras if e.hinge is None and e.slot in (hinge.name, role)), None)


def objects(s):
    out = []
    for e in s.extras:
        if e.root is not None:
            out += [e.root] + list(e.root.children_recursive)
    return out


# ---- the script's part blocks

def block(e):
    from .script import _q
    return "\n".join([BLOCK + e.asset_id, NOTE,
                      f'template {_q(e.asset_id)} {{ model = {_q(e.root.name)}, name = {_q(e.display_name or e.asset_id)} }}', ""])


def _spans(lines):
    """part id -> (first line, line after the block)"""
    out = {}
    for i, l in enumerate(lines):
        if l.startswith(BLOCK):   # the header, down to its template line, and the blank line after it
            j = i + 1
            while j < len(lines) and not re.match(r'\s*template\s*"', lines[j]) and not lines[j].startswith(BLOCK):
                j += 1
            j = j + 1 if j < len(lines) and re.match(r'\s*template\s*"', lines[j]) else j
            if j < len(lines) and not lines[j].strip():
                j += 1
            out[l[len(BLOCK):].strip()] = (i, j)
    return out


def write_blocks(s):
    """Every part's block in the script as it is now, and the blocks of parts no longer here gone."""
    path = paths.asset_lua(s)
    if not path or not os.path.isfile(path):
        return
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    src = open(path, encoding="utf-8").read()
    lines = src.rstrip("\n").split("\n") + [""]
    want = {e.asset_id: block(e) for e in s.extras if e.root is not None and e.asset_id}
    for pid, (a, b) in sorted(_spans(lines).items(), key=lambda kv: -kv[1][0]):
        if pid not in want:
            del lines[a:b]
    spans = _spans(lines)
    for pid, text in want.items():
        new = text.split("\n")
        if pid in spans:
            a, b = spans[pid]
            lines[a:b] = new
        else:
            while lines and not lines[-1].strip():
                lines.pop()
            lines += [""] + new
        spans = _spans(lines)
    out = "\n".join(lines).rstrip("\n") + "\n"
    if out != src:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(out)
        script.reload_text(path)


# ---- making one

def donor_items(self, context):
    names = gamedata.part_ids().get(self.accepts, [])
    side = (self.slot.rsplit("_", 1)[-1] if "_" in self.slot else "").upper()
    if side in ("L", "R"):   # the same side's first
        names = sorted(names, key=lambda n: not n.upper().endswith(("_" + side, side)))
    donor_items.cache = [(n, n, f"Start from the game's {n}") for n in names] or [("", f"(no {self.accepts} in the game's exports)", "")]
    return donor_items.cache


class SIGMA_OT_make_slot_part(jobs.JobMixin, bpy.types.Operator):
    """Make a part for this slot: start from one of the game's parts of that kind (pick which), put where the slot is.
Then swap your mesh in with "Use my mesh for it". It's a part of its own, in the same file and script as the car"""
    bl_idname = "sigma.make_slot_part"
    bl_label = "Make a part for it"
    bl_property = "donor"
    bl_options = {'REGISTER', 'UNDO'}

    slot: bpy.props.StringProperty()
    role: bpy.props.StringProperty()
    accepts: bpy.props.StringProperty()
    donor: bpy.props.EnumProperty(name="Start from", items=donor_items,
                                  description="The game's part this one starts as: it lends its workings (how it opens, "
                                              "clips on, is saved) and, until you put yours on, its look")
    mesh: bpy.props.StringProperty(options={'SKIP_SAVE'},
                                   description="Your mesh: the part is made of it, the game's part only lends what it does")

    @classmethod
    def description(cls, context, props):
        if props.mesh:
            return (f"Make '{props.mesh}' this slot's {props.accepts}: a part of its own that works as the game's "
                    f"{props.accepts}s do (one of them lends it its workings, you never see it), looking like your mesh, "
                    "where your mesh is, with colliders made from it")
        return (f"Fit one of the game's {props.accepts}s here as it is (pick which), to see it on your car or to start "
                "from. Its own look stays until you put your mesh on it")

    @classmethod
    def poll(cls, context):
        return not jobs.busy() and context.scene.sigma.root is not None

    def invoke(self, context, event):
        if not gamedata.part_ids().get(self.accepts):
            self.report({'ERROR'}, f"No {self.accepts} in the game's exports. In game: Export All first (step 2 says how)")
            return {'CANCELLED'}
        if self.mesh:   # its look is the artist's: which game part lends the workings hardly matters, so no question
            self.donor = donor_items(self, context)[0][0]
            return self.execute(context)
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        s = context.scene.sigma
        if not s.asset_id:
            self.report({'ERROR'}, "Give the car an id first (step 3): the part's id is made from it")
            return {'CANCELLED'}
        path = os.path.join(paths.export_dir(), self.donor + ".glb")
        if not self.donor or not os.path.isfile(path):
            return {'CANCELLED'}
        if gamedata.needs_fill(path) and jobs.tools_ready():
            argv = [jobs.venv_python(), os.path.join(paths.tools_dir(), "fill_meshes.py"), paths.data_file(), path]
            return self.start_job(context, "Getting the game's meshes", [("reading the game's files", argv)],
                                  lambda ok, out: ok and self.load(bpy.context, path))
        self.load(context, path)
        return {'FINISHED'}

    def load(self, context, path):
        from . import kit
        s = context.scene.sigma
        k = sigmasrc.kit()
        context.view_layer.update()
        hinge = context.scene.objects.get(self.slot)
        before = set(context.scene.objects)
        bpy.ops.import_scene.gltf(filepath=path)
        new = [o for o in context.scene.objects if o not in before]
        assets.organize(context.scene)
        roots = [o for o in new if o.parent is None]
        if not roots:
            raise RuntimeError("The game part came in empty")
        root = roots[0]
        if hinge is not None:   # where it sits when fitted: on the slot, turned as it is
            loc, rot, _ = hinge.matrix_world.decompose()
            root.matrix_world = Matrix.LocRotScale(loc, rot, root.matrix_world.to_scale())   # its own scale stays
        role = kit.role_of(k, self.role or self.slot) if k else None
        label = (role.label if role else self.accepts).replace(" slot", "")
        e = s.extras.add()
        e.slot, e.hinge, e.accepts, e.donor = self.slot, hinge, self.accepts, self.donor
        base = s.asset_id + "_" + assets.clean_id(self.slot.replace("hinge_", ""))
        pid, n = base, 2
        while any(x.asset_id == pid for x in s.extras) or pid == s.asset_id:
            pid, n = f"{base}_{n}", n + 1
        e.asset_id = pid
        e.display_name = f"{s.display_name or s.asset_id} {label[:1].lower() + label[1:]}"
        e.root = root
        root.name = e.display_name
        kit._ensure_script(s)
        write_blocks(s)
        for o in context.selected_objects:
            o.select_set(False)
        root.select_set(True)
        context.view_layer.objects.active = root
        mine = context.scene.objects.get(self.mesh) if self.mesh else None
        if mine is not None:
            made = self.make_mine(context, root, mine)
            _poke()
            self.report({'INFO'}, f"'{root.name}' is your mesh now, working as the game's {self.accepts} does" +
                        ("; its colliders are being made from it" if made else
                         ". Its colliders are still the game's shape: install the helper tools (step 1), then Replace its colliders"))
            return True
        _poke()
        self.report({'INFO'}, f"Made '{root.name}' from the game's {self.donor}, at the slot. Now your mesh: select it, "
                              "then the game part's mesh (last), and Use my mesh for it")
        return True

    def make_mine(self, context, root, mine):
        """The part takes the artist's mesh: on the game part's main mesh, the game's other meshes (handle, trim, glass)
        gone (the game stops drawing what the template hasn't got, its workings stay), its colliders made from it."""
        visual = [o for o in [root] + list(root.children_recursive) if o.type == 'MESH' and o.get("sigma") != "collider"]
        target = max(visual, key=lambda o: (o is root or o.parent is root, len(o.data.vertices)), default=None)
        if target is None:
            raise RuntimeError(f"The game's {self.donor} has no mesh to put yours on")
        for o in visual:
            if o is target or o is root or target in o.children_recursive:
                continue
            for c in list(o.children):   # anything under it that isn't its look stays, where it is
                w = c.matrix_world.copy()
                c.parent = o.parent
                c.matrix_world = w
        for o in visual:
            if o is not target and o is not root and target not in o.children_recursive and o.name in bpy.data.objects:
                if not o.children:
                    bpy.data.objects.remove(o)
        swap_mesh(context, mine, target)
        if not jobs.tools_ready():
            return False
        bpy.ops.sigma.make_colliders('EXEC_DEFAULT', target=target.name, replace_in=root.name)
        return True


def swap_mesh(context, mine, target):
    """target (the game part's mesh) takes mine's look, where mine is; mine goes, what was under it moves to target."""
    context.view_layer.update()   # every matrix current before anything is moved by one
    m = target.matrix_world.inverted() @ mine.matrix_world
    # as it exports: its modifiers applied (Mirror, Solidify...), not just the mesh under them
    me = bpy.data.meshes.new_from_object(mine.evaluated_get(context.evaluated_depsgraph_get()),
                                         preserve_all_data_layers=True, depsgraph=context.evaluated_depsgraph_get())
    me.transform(m)
    if m.determinant() < 0:   # mirrored on the way: faces would point inwards
        me.flip_normals()
    old = target.data
    target.data = me
    if old.users == 0:
        bpy.data.meshes.remove(old)
    top = target
    while top.parent:
        top = top.parent
    target[SWAPPED] = True
    top[PART_SWAPPED] = True
    for c in list(mine.children):   # what was under the artist's mesh stays where it is, under the part
        w = c.matrix_world.copy()
        c.parent = target
        c.matrix_world = w
    mesh_of_mine = mine.data
    bpy.data.objects.remove(mine)
    if mesh_of_mine.users == 0:
        bpy.data.meshes.remove(mesh_of_mine)


class SIGMA_OT_use_my_mesh(bpy.types.Operator):
    """Put your mesh in place of the game's: select your mesh first, then the game part's mesh last (the active one).
The game's object keeps everything that makes it the game's part; it just looks like yours, where yours is"""
    bl_idname = "sigma.use_my_mesh"
    bl_label = "Use my mesh for it"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        a = context.active_object
        others = [o for o in context.selected_objects if o != a]
        return a is not None and a.type == 'MESH' and len(others) == 1 and others[0].type == 'MESH'

    def execute(self, context):
        target = context.active_object
        swap_mesh(context, next(o for o in context.selected_objects if o != target), target)
        _poke()
        self.report({'INFO'}, f"'{target.name}' looks like yours now. Its colliders are still the game part's shape: "
                              "Clunky will offer to replace them")
        return {'FINISHED'}


class SIGMA_OT_replace_part_colliders(bpy.types.Operator):
    """Delete the game part's colliders (the game's shape, not yours) and make new ones from your mesh"""
    bl_idname = "sigma.replace_part_colliders"
    bl_label = "Replace its colliders"
    bl_options = {'REGISTER', 'UNDO'}

    root: bpy.props.StringProperty()

    def execute(self, context):
        top = bpy.data.objects.get(self.root)
        if top is None:
            return {'CANCELLED'}
        meshes = [o for o in [top] + list(top.children_recursive) if o.get(SWAPPED) and o.type == 'MESH']
        swapped = next((o for o in meshes if o != top), meshes[0] if meshes else None)
        if swapped is None:
            self.report({'ERROR'}, "Put your mesh in first (Use my mesh for it): the new colliders are made from it")
            return {'CANCELLED'}
        # the game's go once the new ones exist, not before: if they're never made, the old ones stay
        return bpy.ops.sigma.make_colliders('INVOKE_DEFAULT', target=swapped.name, replace_in=top.name)


class SIGMA_OT_remove_extra(bpy.types.Operator):
    """Take this part out of the script (its objects stay in Blender, as plain objects)"""
    bl_idname = "sigma.remove_extra"
    bl_label = "Take out"
    bl_options = {'REGISTER', 'UNDO'}

    index: bpy.props.IntProperty()

    def execute(self, context):
        s = context.scene.sigma
        if 0 <= self.index < len(s.extras):
            s.extras.remove(self.index)
            write_blocks(s)
        _poke()
        return {'FINISHED'}


class SIGMA_OT_select_objects(bpy.types.Operator):
    """Select these (and show them, if they were hidden)"""
    bl_idname = "sigma.select_objects"
    bl_label = "Select them"

    names: bpy.props.StringProperty()

    @classmethod
    def description(cls, context, props):
        names = [n for n in props.names.split("\n") if n]
        listed = ", ".join(names[:8]) + (f" and {len(names) - 8} more" if len(names) > 8 else "")
        return "Select and show: " + listed + "" if names else cls.__doc__

    def execute(self, context):
        objs = [bpy.data.objects[n] for n in self.names.split("\n") if n in bpy.data.objects]
        objs = [o for o in objs if o.name in context.view_layer.objects]
        for o in context.selected_objects:
            o.select_set(False)
        for o in objs:
            o.hide_set(False)
            o.hide_viewport = False
            o.select_set(True)
        if objs:
            context.view_layer.objects.active = objs[0]
        return {'FINISHED'}


class SIGMA_OT_delete_objects(bpy.types.Operator):
    """Delete these objects (Ctrl+Z brings them back)"""
    bl_idname = "sigma.delete_objects"
    bl_label = "Delete them"
    bl_options = {'REGISTER', 'UNDO'}

    names: bpy.props.StringProperty()

    @classmethod
    def description(cls, context, props):
        names = [n for n in props.names.split("\n") if n]
        listed = ", ".join(names[:8]) + (f" and {len(names) - 8} more" if len(names) > 8 else "")
        return "Delete: " + listed + " (Ctrl+Z brings them back)" if names else cls.__doc__

    def execute(self, context):
        n = 0
        for name in self.names.split("\n"):
            o = bpy.data.objects.get(name)
            if o is not None:
                bpy.data.objects.remove(o)
                n += 1
        c = bpy.data.collections.get("~sigma kit")
        if c is not None and not c.all_objects:
            bpy.data.collections.remove(c)
        _poke()
        self.report({'INFO'}, f"{n} deleted")
        return {'FINISHED'}


classes = (SIGMA_OT_make_slot_part, SIGMA_OT_use_my_mesh, SIGMA_OT_replace_part_colliders, SIGMA_OT_remove_extra,
           SIGMA_OT_select_objects, SIGMA_OT_delete_objects)
props = (SigmaExtra,)
