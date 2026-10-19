# Colliders from an object's own mesh (tools/colliders.py's decomposition), made as Blender objects: children of the
# object (or of an empty under it), marked as the game's convex mesh colliders, so they are kept in the .blend and
# exported with everything else. Making them again replaces the ones made before; colliders of your own stay.
import os
import tempfile

import bpy
import numpy as np

from . import jobs, paths

MARK = "colliders.py"   # the "generated" property tools/colliders.py puts on its pieces too

DETAIL = [
    ('0.1', "Rough (about 30 pieces)", "Fast and light. Pieces bridge window and door openings"),
    ('0.05', "Normal (about 70 pieces)", "The usual choice for a car body"),
    ('0.03', "Fine (about 130 pieces)", "Nearly exact. Heavier for the physics"),
]


def shape_key(obj):
    # what the colliders were made from (its corners, summed two ways): when it changes, they are out of date
    co = np.empty(len(obj.data.vertices) * 3, np.float64)
    obj.data.vertices.foreach_get("co", co)
    return f"{len(obj.data.vertices)}|{len(obj.data.polygons)}|{co.sum():.4f}|{(co * co).sum():.4f}"


def generated(holder, name):
    return [c for c in holder.children if c.get("generated") == MARK and c.name.startswith(name + " collider ")]


def mesh_arrays(context, obj):
    ev = obj.evaluated_get(context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    me.calc_loop_triangles()
    v = np.empty(len(me.vertices) * 3, np.float32)
    me.vertices.foreach_get("co", v)
    f = np.empty(len(me.loop_triangles) * 3, np.int32)
    me.loop_triangles.foreach_get("vertices", f)
    ev.to_mesh_clear()
    return v.reshape(-1, 3), f.reshape(-1, 3)


def holder_items(self, context):
    o = context.active_object
    items = [("SELF", "Directly under the object", "The colliders go straight under the mesh they're made from")]
    if o:
        items += [("OBJ:" + c.name, c.name, "An empty under the object") for c in o.children_recursive if c.type == 'EMPTY']
    holder_items.cache = items
    return items


class SIGMA_OT_make_colliders(jobs.JobMixin, bpy.types.Operator):
    """Make solid colliders that follow the shape of the active mesh (a car body, a cabin). Do it again any time:
the old ones are replaced"""
    bl_idname = "sigma.make_colliders"
    bl_label = "Make colliders from mesh"
    bl_options = {'REGISTER', 'UNDO'}

    detail: bpy.props.EnumProperty(name="Detail", items=DETAIL, default='0.05',
                                   description="How closely the colliders follow the mesh: finer is more pieces, slower to "
                                               "make and a little slower in game")
    target: bpy.props.StringProperty(options={'HIDDEN', 'SKIP_SAVE'}, description="The mesh (default: the active object)")
    replace_in: bpy.props.StringProperty(options={'HIDDEN', 'SKIP_SAVE'},
                                         description="A part whose game colliders go once these are made")
    into: bpy.props.EnumProperty(name="Put them", items=holder_items,
                                 description="Which object the colliders go under: they move with it in game (a door's "
                                             "colliders under the door swing with it)")

    @classmethod
    def poll(cls, context):
        return not jobs.busy()

    def mesh(self, context):
        o = bpy.data.objects.get(self.target) if self.target else context.active_object
        return o if o is not None and o.type == 'MESH' and o.get("sigma") != "collider" else None

    def invoke(self, context, event):
        if self.mesh(context) is None:
            self.report({'ERROR'}, "Select a mesh first. The body, ideally. Not the camera")
            return {'CANCELLED'}
        if not jobs.tools_ready():
            self.report({'ERROR'}, "Install the helper tools first (step 1 of the guide)")
            return {'CANCELLED'}
        return context.window_manager.invoke_props_dialog(self, width=360)

    def execute(self, context):
        obj = self.mesh(context)
        if obj is None:
            self.report({'ERROR'}, "Select a mesh first")
            return {'CANCELLED'}
        self.obj_name, self.into_name = obj.name, self.into[4:] if self.into.startswith("OBJ:") else ""
        self.replace_name = self.replace_in
        v, f = mesh_arrays(context, obj)
        if len(f) == 0:
            self.report({'ERROR'}, f"'{obj.name}' has no faces")
            return {'CANCELLED'}
        tmp = tempfile.mkdtemp(prefix="sigma_colliders_")
        self.src, self.dst = os.path.join(tmp, "mesh.npz"), os.path.join(tmp, "pieces.npz")
        np.savez(self.src, v=v, f=f)
        argv = [jobs.venv_python(), os.path.join(os.path.dirname(__file__), "run_colliders.py"), paths.tools_dir(),
                self.src, self.detail, self.dst]
        return self.start_job(context, f"Colliders for {obj.name}", [("cutting the mesh into convex pieces", argv)], self.done)

    def done(self, ok, out):
        if not ok:
            return
        obj = bpy.data.objects.get(self.obj_name)
        if obj is None:
            raise RuntimeError(f"'{self.obj_name}' is gone")
        holder = bpy.data.objects.get(self.into_name) if self.into_name else obj
        for old in generated(holder, obj.name):
            me = old.data
            bpy.data.objects.remove(old)
            if me and me.users == 0:
                bpy.data.meshes.remove(me)
        d = np.load(self.dst)
        count = sum(1 for k in d.files if k.startswith("v"))
        cols = holder.users_collection or [bpy.context.scene.collection]
        for k in range(count):
            name = f"{obj.name} collider {k + 1}"
            me = bpy.data.meshes.new(name)
            me.from_pydata(d[f"v{k}"].tolist(), [], d[f"f{k}"].tolist())
            me.update()
            piece = bpy.data.objects.new(name, me)
            for key, val in (("sigma", "collider"), ("shape", "mesh"), ("convex", True), ("trigger", False), ("generated", MARK),
                             ("sigma_from", obj.name), ("sigma_shape", shape_key(obj))):
                piece[key] = val
            piece.display_type = 'WIRE'
            piece.hide_render = True
            for c in cols:
                c.objects.link(piece)
            piece.parent = holder
            piece.matrix_world = obj.matrix_world.copy()
        for p in (self.src, self.dst):
            try:
                os.remove(p)
            except OSError:
                pass
        gone = 0
        top = bpy.data.objects.get(self.replace_name) if self.replace_name and count else None
        if top is not None:   # the new ones exist: the game part's solid shape can go (its click and fit boxes stay)
            for o in [o for o in [top] + list(top.children_recursive) if game_shape(o)]:
                bpy.data.objects.remove(o)
                gone += 1
        self.report({'INFO'}, f"{count} colliders for '{obj.name}'" + (" (mirrored halves)" if bool(d["mirrored"]) else "")
                    + (f"; the game's {gone} old ones deleted" if gone else ""))


def game_shape(o):
    """One of a game part's colliders that only stand for its shape: solid boxes and meshes. Not its trigger boxes
    (where it's clicked, or held to fit it) nor its sphere (how it rolls about loose), which work whatever it looks like."""
    return o.get("sigma") == "collider" and not o.get("generated") and not o.get("trigger") and o.get("shape") in ("box", "mesh")


class SIGMA_OT_remove_colliders(bpy.types.Operator):
    """Delete the colliders made from the active mesh (your own colliders stay)"""
    bl_idname = "sigma.remove_colliders"
    bl_label = "Remove made colliders"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return context.active_object is not None

    def execute(self, context):
        obj = context.active_object
        gone = 0
        for holder in [obj] + list(obj.children_recursive):
            for c in generated(holder, obj.name):
                bpy.data.objects.remove(c)
                gone += 1
        self.report({'INFO'}, f"{gone} colliders removed")
        return {'FINISHED'}


classes = (SIGMA_OT_make_colliders, SIGMA_OT_remove_colliders)
