# Starting an asset (a game object imported, an empty of your own, a game object under a new name), the game's
# materials, and talking to the game (its settings file, its log).
import importlib.util
import os
import re

import bpy

from . import gamedata, jobs, paths
from .script import KINDS


def clean_id(name):
    s = re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_")
    return s or "my_asset"


def set_root(context, obj, kind=None):
    s = context.scene.sigma
    s.root = obj
    if not s.asset_id:
        s.asset_id = "my_" + clean_id(obj.name)
    if not s.display_name:
        s.display_name = obj.name
    if kind:
        s.kind = kind


def organize(scene):
    # the tools' own blender_organize.py: colliders, inactive, far and undrawn objects into hidden collections
    path = os.path.join(paths.tools_dir(), "blender_organize.py")
    spec = importlib.util.spec_from_file_location("sigma_blender_organize", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    mod.organize(scene)


def export_items(self, context):
    items = [(p, n, p) for n, p in gamedata.exports(include_live=context.scene.sigma.show_live)]
    export_items.cache = items or [("", "(nothing exported yet)", "")]
    return paths.stable("exports", export_items.cache)


def is_vehicle(path):
    # the game's cars have wheel hinges and a driver's trigger
    js = gamedata.glb_json(path) or {}
    return any((n.get("name") or "").startswith(("hinge_wheel", "DriveTrigger")) for n in js.get("nodes", []))


class SIGMA_OT_find_game(bpy.types.Operator):
    """Look for Apocalypter where Steam keeps games: Steam's own folder (on Windows, where the registry says it is),
every Steam library folder, and the folder the game's Steam manifest names. Not found: pick the folder by hand below"""
    bl_idname = "sigma.find_game"
    bl_label = "Find the game"

    def execute(self, context):
        found = paths.find_game()
        if not found:
            self.report({'ERROR'}, "Couldn't find Apocalypter. Click the folder icon and pick the game's folder (the one with Apocalypter_Data in it)")
            return {'CANCELLED'}
        paths.prefs().game_dir = found
        self.report({'INFO'}, "Found the game: " + found)
        return {'FINISHED'}


class SIGMA_OT_import_game_object(jobs.JobMixin, bpy.types.Operator):
    """Bring in one of the game's own objects (a car, a part, an item) to change. They come from SigmaMod's Export All,
done once in game"""
    bl_idname = "sigma.import_game_object"
    bl_label = "Import a game object"
    bl_property = "prefab"
    bl_options = {'REGISTER', 'UNDO'}

    prefab: bpy.props.EnumProperty(name="Game object", items=export_items,
                                   description="One of the game's objects, from Export All")

    @classmethod
    def poll(cls, context):
        return not jobs.busy() and bool(gamedata.exports())

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        path = self.prefab
        if not path or not os.path.isfile(path):
            return {'CANCELLED'}
        if not gamedata.needs_fill(path):
            self.load(context, path)
            return {'FINISHED'}
        if not jobs.tools_ready():
            self.report({'WARNING'}, "The helper tools aren't installed, so most of its meshes are plain boxes. Install them (step 1) and import again")
            self.load(context, path)
            return {'FINISHED'}
        argv = [jobs.venv_python(), os.path.join(paths.tools_dir(), "fill_meshes.py"), paths.data_file(), path]
        return self.start_job(context, "Getting the game's meshes", [("reading the game's files", argv)],
                              lambda ok, out: ok and self.load(bpy.context, path))

    def load(self, context, path):
        scene = context.scene
        before = set(scene.objects)
        bpy.ops.import_scene.gltf(filepath=path)
        new = [o for o in scene.objects if o not in before]
        organize(scene)
        roots = [o for o in new if o.parent is None]
        if len(roots) == 1:
            set_root(context, roots[0], 'CHANGED_CAR' if is_vehicle(path) else 'CHANGED')
        self.report({'INFO'}, f"Imported {os.path.basename(path)}: {len(new)} objects. Colliders and hidden parts are in hidden collections")
        return True


class SIGMA_OT_new_root(bpy.types.Operator):
    """Start an asset of your own from nothing: an empty that everything of it goes under"""
    bl_idname = "sigma.new_root"
    bl_label = "Start from scratch"
    bl_options = {'REGISTER', 'UNDO'}

    name: bpy.props.StringProperty(name="Name", default="My Asset",
                                   description="The name of your asset's top object in Blender (the id and the name "
                                               "players see are set in step 3)")
    kind: bpy.props.EnumProperty(name="What is it?", items=[k for k in KINDS if k[0] in ('CAR', 'ATTACHABLE')],
                                 description="A car of your own, or an item that bolts onto cars")

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=380)

    def execute(self, context):
        root = bpy.data.objects.new(self.name, None)
        root.empty_display_type = 'ARROWS'
        root.location = context.scene.cursor.location
        context.collection.objects.link(root)
        for o in context.selected_objects:
            o.select_set(False)
        root.select_set(True)
        context.view_layer.objects.active = root
        context.scene.sigma.asset_id = ""
        context.scene.sigma.display_name = ""
        set_root(context, root, self.kind)
        self.report({'INFO'}, f"Made '{root.name}'. Put everything of your asset under it (Ctrl+P > Object)")
        return {'FINISHED'}


class SIGMA_OT_new_variant(bpy.types.Operator):
    """A game object as a new thing of its own, unchanged (an engine sold under another name, to tune in the script)"""
    bl_idname = "sigma.new_variant"
    bl_label = "Copy a game object"
    bl_property = "prefab"
    bl_options = {'REGISTER', 'UNDO'}

    prefab: bpy.props.EnumProperty(name="Game object", items=export_items,
                                   description="One of the game's objects, from Export All")

    @classmethod
    def poll(cls, context):
        return bool(gamedata.exports())

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        roots = [(n, ex) for n, ex in gamedata.root_extras(self.prefab) if ex.get("es3prefab")]
        if not roots:
            self.report({'ERROR'}, "That export has no game id on its root; pick another")
            return {'CANCELLED'}
        name, ex = roots[0]
        root = bpy.data.objects.new(name + " Variant", None)
        root["es3prefab"] = ex["es3prefab"]
        context.collection.objects.link(root)
        context.scene.sigma.asset_id = ""
        context.scene.sigma.display_name = ""
        set_root(context, root, 'VARIANT')
        self.report({'INFO'}, f"Made '{root.name}': the game's {name}, under a name of its own. Nothing else goes under it")
        return {'FINISHED'}


class SIGMA_OT_use_active_as_root(bpy.types.Operator):
    """Make the active object the asset's root: the asset is it and everything under it"""
    bl_idname = "sigma.use_active_as_root"
    bl_label = "Use the selected object"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return context.active_object is not None

    def execute(self, context):
        o = context.active_object
        while o.parent:   # the top of what it's under
            o = o.parent
        set_root(context, o)
        return {'FINISHED'}


def lost_ids(context):
    """[(material, game material name)]: materials named as a game material that have lost its id."""
    game = gamedata.game_materials()
    out = []
    for m in bpy.data.materials:
        if m.users == 0 or m.get("es3global") or m.get("sigma_own"):
            continue
        base = gamedata.base_name(m.name)
        if base in game:
            out.append((m, base))
    return out


def give_id(mat, extras):
    for k, v in extras.items():
        try:
            mat[k] = v
        except (TypeError, ValueError):
            pass   # only es3global matters to SigmaMod; the rest is for reading


class SIGMA_OT_restore_material_ids(bpy.types.Operator):
    """Give game materials that have lost the game's id (appending or copying a material in Blender loses it) their id
back. With it, SigmaMod uses the game's own material and its textures stay out of your file"""
    bl_idname = "sigma.restore_material_ids"
    bl_label = "Use the game's materials"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        game = gamedata.game_materials()
        lost = lost_ids(context)
        for m, base in lost:
            give_id(m, game[base][0])
        self.report({'INFO'}, f"{len(lost)} materials are the game's again")
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


class SIGMA_OT_keep_own_material(bpy.types.Operator):
    """Keep this material as your own (its textures go into the file), though it's named like a game material"""
    bl_idname = "sigma.keep_own_material"
    bl_label = "Keep it mine"
    bl_options = {'REGISTER', 'UNDO'}

    material: bpy.props.StringProperty()

    def execute(self, context):
        m = bpy.data.materials.get(self.material)
        if m:
            m["sigma_own"] = True
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


def material_items(self, context):
    material_items.cache = [(n, n, os.path.basename(p)) for n, (_, p) in sorted(gamedata.game_materials().items(), key=lambda e: e[0].lower())] \
        or [("", "(nothing exported yet)", "")]
    return paths.stable("materials", material_items.cache)


class SIGMA_OT_game_material(bpy.types.Operator):
    """Put one of the game's materials on the selected meshes. SigmaMod uses the game's own, so it costs nothing in
your file"""
    bl_idname = "sigma.game_material"
    bl_label = "Use a game material"
    bl_property = "material"
    bl_options = {'REGISTER', 'UNDO'}

    material: bpy.props.EnumProperty(name="Material", items=material_items,
                                     description="One of the game's materials: the game uses its own textures for it, so it "
                                                 "costs nothing in your file")

    @classmethod
    def poll(cls, context):
        return any(o.type == 'MESH' for o in context.selected_objects)

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        game = gamedata.game_materials()
        if self.material not in game:
            return {'CANCELLED'}
        extras, path = game[self.material]
        mat = next((m for m in bpy.data.materials if m.get("es3global") == extras["es3global"]), None)
        if mat is None:
            mat = self.load(context, path, self.material)
            give_id(mat, extras)
        for o in context.selected_objects:
            if o.type == 'MESH':
                o.data.materials.clear()
                o.data.materials.append(mat)
        return {'FINISHED'}

    def load(self, context, path, name):
        # the material as the export has it (textures and all, to see it in Blender); what came with it goes again
        selected = list(context.selected_objects)
        before_obj, before_mat = set(bpy.data.objects), set(bpy.data.materials)
        bpy.ops.import_scene.gltf(filepath=path)
        new_mats = [m for m in bpy.data.materials if m not in before_mat]
        mat = next((m for m in new_mats if gamedata.base_name(m.name) == name), None)
        for o in [o for o in bpy.data.objects if o not in before_obj]:
            data = o.data
            bpy.data.objects.remove(o)
            if data is not None and data.users == 0 and isinstance(data, bpy.types.Mesh):
                bpy.data.meshes.remove(data)
        for m in new_mats:
            if m is not mat and m.users == 0:
                bpy.data.materials.remove(m)
        for o in selected:
            o.select_set(True)
        if mat is None:
            mat = bpy.data.materials.new(name)
        return mat


class SIGMA_OT_toggle_colliders(bpy.types.Operator):
    """Show or hide every collider: pieces made from meshes, the game's colliders, and every object picked as a box
(click boxes, get-in and get-out boxes, lock volumes)"""
    bl_idname = "sigma.toggle_colliders"
    bl_label = "Show / hide colliders"

    def execute(self, context):
        from .kit import collider_objects
        cols = [o for o in collider_objects(context.scene.sigma) if o.name in context.view_layer.objects]
        lc = context.view_layer.layer_collection.children.get("Colliders")
        hide = not all(o.hide_get() for o in cols) if cols else False
        if lc is not None:
            lc.hide_viewport = False
        for o in cols:
            o.hide_set(hide)
        return {'FINISHED'}


def config_value(section, key):
    try:
        with open(paths.config_file(), encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return None
    m = re.search(r"^\[" + section + r"\](.*?)(?=^\[|\Z)", text, re.M | re.S)
    v = m and re.search(r"^" + key + r"\s*=\s*(\S+)", m.group(1), re.M)
    return v.group(1).lower() if v else None


class SIGMA_OT_hot_reload(bpy.types.Operator):
    """Turn on SigmaMod's Hot reload: an asset you export is rebuilt in the running game in seconds, no reloading the
save. Takes effect the next time the game starts (or switch it on in game: Mods menu > SigmaMod > Tools)"""
    bl_idname = "sigma.hot_reload"
    bl_label = "Turn on Hot reload"

    @classmethod
    def poll(cls, context):
        return config_value("Tools", "HotReload") == "false"

    def execute(self, context):
        p = paths.config_file()
        with open(p, encoding="utf-8") as f:
            text = f.read()
        m = re.search(r"^\[Tools\](.*?)(?=^\[|\Z)", text, re.M | re.S)
        body = re.sub(r"^(HotReload\s*=\s*)\S+", r"\1true", m.group(1), count=1, flags=re.M)
        with open(p, "w", encoding="utf-8") as f:
            f.write(text[:m.start(1)] + body + text[m.end(1):])
        self.report({'INFO'}, "Hot reload is on from the next game start")
        return {'FINISHED'}


def game_log_lines(s, limit=40):
    try:
        with open(paths.log_file(), encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
    except OSError:
        return []
    # the last session that mentioned it: from the last start of the game
    start = max((i for i, l in enumerate(lines) if "BepInEx" in l and "Chainloader" in l and "ready" in l), default=0)
    keys = [k for k in (s.asset_id, s.asset_id + ".lua", s.asset_id + ".glb") if k]
    hits = [l for l in lines[start:] if any(k in l for k in keys) or ("SigmaMod" in l and ("Error" in l or "Warning" in l))]
    return hits[-limit:]


class SIGMA_OT_game_log(bpy.types.Operator):
    """What the game's log says about this asset since the game last started (errors, and the line saying it loaded)"""
    bl_idname = "sigma.game_log"
    bl_label = "What did the game say?"

    @classmethod
    def poll(cls, context):
        return os.path.isfile(paths.log_file() or "")

    def invoke(self, context, event):
        self.lines = game_log_lines(context.scene.sigma)
        return context.window_manager.invoke_popup(self, width=900)

    def draw(self, context):
        col = self.layout.column(align=True)
        if not self.lines:
            col.label(text="Nothing about it in the log yet. Is the game running, with the asset exported?")
        for l in self.lines:
            icon = 'ERROR' if "Error" in l else 'INFO' if "Info" in l else 'DOT'
            col.label(text=l[:180], icon=icon)
        col.separator()
        col.operator("wm.path_open", text="Open the whole log", icon='TEXT').filepath = paths.log_file()

    def execute(self, context):
        return {'FINISHED'}


def _objects(names):
    return [bpy.data.objects[n] for n in names.split("\n") if n in bpy.data.objects]


class SIGMA_OT_strip_game_ids(bpy.types.Operator):
    """Take the game's ids off these objects: they become plain objects of your own, and your asset stops being a
changed copy of the game object they came from"""
    bl_idname = "sigma.strip_game_ids"
    bl_label = "Make them plain objects"
    bl_options = {'REGISTER', 'UNDO'}

    names: bpy.props.StringProperty()

    def execute(self, context):
        objs = _objects(self.names)
        for o in objs:
            for k in ("es3local", "es3prefab"):
                if k in o:
                    del o[k]
        self.report({'INFO'}, f"{len(objs)} objects are yours now")
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


class SIGMA_OT_set_kind(bpy.types.Operator):
    """Say what kind of asset this is: a car of your own, a changed game car, an item that bolts on, another changed
game object, or a game object under a new name. It decides what the Build step asks of you"""
    bl_idname = "sigma.set_kind"
    bl_label = "Set the kind"
    bl_options = {'REGISTER', 'UNDO'}

    kind: bpy.props.EnumProperty(items=KINDS, description="What kind of asset this is")

    def execute(self, context):
        context.scene.sigma.kind = self.kind
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


class SIGMA_OT_parent_to_root(bpy.types.Operator):
    """Put these objects under the asset's top object, where they stay as they are now"""
    bl_idname = "sigma.parent_to_root"
    bl_label = "Put them under it"
    bl_options = {'REGISTER', 'UNDO'}

    names: bpy.props.StringProperty()

    def execute(self, context):
        root = context.scene.sigma.root
        for o in _objects(self.names):
            if o != root:
                m = o.matrix_world.copy()
                o.parent = root
                o.matrix_world = m
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


class SIGMA_OT_make_mine(bpy.types.Operator):
    """Make this game material your own: your edits show in game, and its textures go into your file"""
    bl_idname = "sigma.make_mine"
    bl_label = "Make it mine"
    bl_options = {'REGISTER', 'UNDO'}

    material: bpy.props.StringProperty()

    def execute(self, context):
        m = bpy.data.materials.get(self.material)
        if m is None:
            return {'CANCELLED'}
        for k in [k for k in m.keys() if k.startswith("es3") or k in ("shader", "renderQueue", "keywords", "properties")]:
            del m[k]
        m["sigma_own"] = True
        self.report({'INFO'}, f"'{m.name}' is yours. Its look in Blender is its look in game now")
        from . import mascot
        mascot.poke()
        return {'FINISHED'}


classes = (SIGMA_OT_find_game, SIGMA_OT_import_game_object, SIGMA_OT_new_root, SIGMA_OT_new_variant,
           SIGMA_OT_use_active_as_root, SIGMA_OT_restore_material_ids, SIGMA_OT_keep_own_material, SIGMA_OT_game_material,
           SIGMA_OT_toggle_colliders, SIGMA_OT_hot_reload, SIGMA_OT_game_log, SIGMA_OT_strip_game_ids, SIGMA_OT_set_kind,
           SIGMA_OT_parent_to_root, SIGMA_OT_make_mine)
