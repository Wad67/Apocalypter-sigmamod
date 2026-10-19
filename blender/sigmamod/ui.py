# The SigmaMod tab in the 3D view's sidebar (N): Clunky at the top saying what's wrong, a guide of numbered steps, each
# ticked off when it is done, the current one open with what to do and the buttons that do it; then the tools on their
# own, and the advanced ones.
import os
import sys

import blf
import bpy

from . import assets, attach, checks, export, extras, gamedata, jobs, kit, mascot, paths, preview, script, sigmasrc, spawn, tuning


def _scale(context):
    return context.preferences.system.ui_scale or 1


def _lines(context, text, width):
    sf = _scale(context)
    try:
        blf.size(0, context.preferences.ui_styles[0].widget.points * sf)
        measure = lambda t: blf.dimensions(0, t)[0] * 1.25   # Blender draws labels wider than blf measures them
    except Exception:   # no font to measure with (background): about 6.5 px a character
        measure = lambda t: len(t) * 6.5 * sf
    out = []
    for para in text.split("\n"):
        line = ""
        for word in para.split(" "):
            test = (line + " " + word) if line else word
            if line and measure(test) > width:
                out.append(line)
                line = word
            else:
                line = test
        out.append(line)
    return out


def wrap(layout, context, text, icon='NONE', boxes=1, frac=1.0):
    """Text wrapped to the panel. boxes: how many boxes it sits in; frac: the share of the panel's width its column
    has (a split)."""
    sf = _scale(context)
    # each box takes its padding off both sides; the region's scroll bar and the panel's own margins come off too
    width = context.region.width * frac - (40 + 20 * boxes) * sf - (24 * sf if icon != 'NONE' else 0)
    col = layout.column(align=True)
    col.scale_y = 0.8
    for i, line in enumerate(_lines(context, text, max(width, 80 * sf))):
        col.label(text=line, icon=icon if i == 0 else ('BLANK1' if icon != 'NONE' else 'NONE'))


def set_args(op, args):
    for k, v in args.items():
        setattr(op, k, v)


# ---- the steps: (title, done(context), draw(layout, context))

def setup_done(c):
    return bool(paths.game_dir()) and paths.sigmamod_installed() and jobs.tools_ready()


def draw_setup(l, c):
    p = paths.prefs()
    g = paths.game_dir()
    l.label(text="Game: " + (g if g else "not found"), icon='CHECKMARK' if g else 'ERROR')
    if not g:
        wrap(l, c, "Where is Apocalypter? Press Find, or click the folder icon and pick the folder with "
                   "Apocalypter_Data in it.")
        l.operator("sigma.find_game", icon='VIEWZOOM')
    if p:
        l.prop(p, "game_dir", text="")
    if g and not paths.sigmamod_installed():
        wrap(l, c, "SigmaMod isn't in the game yet. Unzip SigmaMod's release into Apocalypter/BepInEx/plugins, then "
                   "come back.", icon='ERROR')
    if jobs.tools_ready():
        l.label(text="Helper tools: installed", icon='CHECKMARK')
    else:
        wrap(l, c, "The helper tools make colliders from your meshes and get the game's real meshes when you import "
                   "its objects. Install them once (needs the internet, a minute or two).")
        l.operator("sigma.install_tools", icon='IMPORT')


def start_done(c):
    return c.scene.sigma.root is not None


def draw_start(l, c):
    s = c.scene.sigma
    if s.root:
        l.label(text=f"Your asset: {s.root.name}", icon='OBJECT_DATA')
        l.prop(s, "kind", text="It's a")
        wrap(l, c, "Everything under that object is your asset. Starting another? One of these:")
    else:
        wrap(l, c, "An asset is one top object with everything of it under it. Start one of these ways:")
    col = l.column(align=True)
    col.operator("sigma.import_game_object", icon='IMPORT')
    col.prop(s, "show_live", text="Include live exports (with parts fitted)")
    if not gamedata.exports():
        wrap(l, c, "Nothing exported from the game yet. In game: Mods menu > SigmaMod > Tools > Export All, then wait "
                   "for the game to come back (minutes). The log says 'export: done'.", icon='INFO')
    col = l.column(align=True)
    col.operator("sigma.new_root", icon='EMPTY_AXIS')
    col.operator("sigma.new_variant", icon='DUPLICATE')
    col.operator("sigma.use_active_as_root", icon='RESTRICT_SELECT_OFF')


def name_done(c):
    s = c.scene.sigma
    return bool(s.asset_id) and checks.ID_RE.match(s.asset_id) is not None and bool(s.mod_folder.strip())


def draw_name(l, c):
    s = c.scene.sigma
    wrap(l, c, "The id is what the game knows it by. Pick it once: change it later and the copies in people's saves "
               "vanish.")
    l.prop(s, "asset_id", text="Id")
    l.prop(s, "display_name", text="Name in game")
    wrap(l, c, "Your mod's folder in the game. Anything but SigmaMod: updates replace that one.")
    col = l.column(align=True)
    col.prop(s, "mod_folder", text="Folder")
    sub = col.column(align=True)
    sub.enabled = not s.out_dir.strip()
    sub.prop(s, "out_subfolder")
    col.prop(s, "out_name", text="File name")
    col.prop(s, "out_dir")
    if paths.asset_dir(s):
        wrap(l, c, paths.asset_glb(s) or paths.asset_dir(s), icon='FILE_FOLDER')
        ok, why, mod = paths.loads(s)
        if not ok:
            wrap(l, c, f"The game won't load it there: {why}.", icon='ERROR')
            if mod:
                l.operator("sigma.load_folder", icon='CHECKMARK')
        if s.last_path and os.path.normcase(s.last_path) != os.path.normcase(paths.asset_glb(s)) and os.path.isfile(s.last_path):
            wrap(l, c, f"Your last export is still at {s.last_path}: the game would load both.", icon='ERROR')
            l.operator("sigma.move_asset", icon='FORWARD')
        l.operator("sigma.open_asset_folder", icon='FILEBROWSER')


def kit_state(c):
    """(kit, picks, [(section, all it needs picked, in the script, script up to date)]) for a car made from scratch."""
    s = c.scene.sigma
    k = sigmasrc.kit()
    if s.kind != 'CAR' or not k or s.root is None:
        return k, {}, []
    p, src = kit.picks(s, k), script.read_script(s) or ""
    return k, p, [(sec,) + kit.status(s, sec, p, src) for sec in k["sections"]]


def build_done(c):
    s = c.scene.sigma
    if s.root is None:
        return False
    if s.kind == 'VARIANT':
        return True
    if s.kind == 'CAR':
        _, _, rows = kit_state(c)
        return kit.body_box(s) is not None and bool(rows) and all(ok and ins for sec, ok, ins, _ in rows if sec.required)
    return bool(s.root.children) or s.root.type == 'MESH'


def _state(s, title):
    return next((st for st in s.sections if st.title == title), None)


KIND_ICON = {"spot": 'EMPTY_ARROWS', "marker": 'EMPTY_SINGLE_ARROW', "mesh": 'MESH_DATA', "clickable": 'RESTRICT_SELECT_OFF',
             "collider": 'MOD_WIREFRAME', "volume": 'MESH_CUBE', "solid": 'MOD_PHYSICS', "hull": 'MOD_PHYSICS'}


def part_mesh(c):
    """The selected mesh, if it could be made into a part: one of the artist's meshes, not picked for anything else."""
    s = c.scene.sigma
    o = c.active_object
    if o is None or o.type != 'MESH' or not o.select_get() or o.get("sigma") == "collider" or o == s.root:
        return None
    if any(r.obj == o for r in s.roles) or o in extras.objects(s):
        return None
    return o


def draw_slot_part(row, c, role, it, accepts):
    """On a slot's row: its part, or the buttons that make one."""
    s = c.scene.sigma
    e = extras.for_slot(s, it.obj, None if role.multi else role.name)
    if e is not None:
        op = row.operator("sigma.select_objects", text="", icon='CHECKMARK')
        op.names = e.root.name
        if e.open_set or preview.open_rotation(e.donor):
            row.operator("sigma.flip_swing", text="", icon='ARROW_LEFTRIGHT').part = e.asset_id
        row.operator("sigma.clear_slot_part", text="", icon='TRASH').part = e.asset_id
        return
    if not gamedata.part_ids().get(accepts):
        return
    mine = part_mesh(c)
    sub = row.row(align=True)
    sub.enabled = mine is not None
    op = sub.operator("sigma.make_slot_part", text="", icon='MESH_DATA')
    op.slot, op.role, op.accepts, op.mesh = it.obj.name, role.name, accepts, mine.name if mine else ""
    op = row.operator("sigma.make_slot_part", text="", icon='IMPORT')
    op.slot, op.role, op.accepts = it.obj.name, role.name, accepts


def draw_role(b, c, sec, role, p, k):
    """One role, compact: its name (hover: what it is, in full; click: the details), what's picked, small buttons with
    tooltips. Open, the same in words."""
    s = c.scene.sigma
    its = kit.items(s, role.name)
    got = p.get(role.name)
    left_out = any(r.none for r in its)
    opened = s.open_role == role.name
    icon = 'CHECKMARK' if got else ('X' if left_out else ('RADIOBUT_OFF' if role.optional else 'ERROR'))
    accepts = sigmasrc.accepts_of(sec.code, role.name) if role.kind == 'spot' else None
    shown = [(i, it) for i, it in enumerate(its) if not it.none]
    split = b.split(factor=0.45, align=True)
    name = role.label + (f" ({len(got)})" if role.multi and got else "")
    split.operator("sigma.open_role", text=name, icon=icon, emboss=False).role = role.name
    right = split.row(align=True)
    if role.multi or not shown:   # buttons on the name's line; each entry gets a line of its own
        if not got and not role.option:   # the Sigma hasn't got one to give
            right.operator("sigma.bring_role", text="", icon='IMPORT').role = role.name
        if role.kind in ('spot', 'marker', 'collider') and not role.multi:
            right.operator("sigma.make_role_object", text="", icon='ADD').role = role.name
        if not got and role.optional and not left_out:
            op = right.operator("sigma.role_slot", text="", icon='X')
            op.role, op.section, op.leave_out = role.name, sec.title, True
        if left_out:
            op = right.operator("sigma.role_slot", text="Have one", icon='LOOP_BACK')
            op.role, op.section = role.name, sec.title
        if got:
            op = right.operator("sigma.select_kit", text="", icon='RESTRICT_SELECT_OFF')
            op.role, op.section = role.name, sec.title
    for n, (i, it) in enumerate(shown):
        row = (right if not role.multi else b.row(align=True)).row(align=True)
        if role.multi:
            row.separator(factor=2)
        row.prop(it, "obj", text="")
        if role.multi and len(its) > 1:
            op = row.operator("sigma.role_slot", text="", icon='X')
            op.role, op.section, op.remove = role.name, sec.title, i
        if it.obj is not None and accepts:
            draw_slot_part(row, c, role, it, accepts)
        if it.obj is not None and role.kind == 'solid' and it.obj.type == 'MESH':
            made, stale = kit.made_colliders(it.obj)
            if made and not stale:
                row.label(text="", icon='MOD_PHYSICS')
            else:
                row.operator("sigma.make_colliders", text="", icon='ERROR').target = it.obj.name
    if role.multi:   # more of them: spelled out, it's how a car gets its third door
        what = role.label[:1].lower() + role.label[1:]
        row = b.row(align=True)
        row.separator(factor=2)
        if role.kind in ('spot', 'marker', 'collider'):
            row.operator("sigma.make_role_object", text=f"Add a {what}", icon='ADD').role = role.name
        op = row.operator("sigma.role_slot", text="Pick one I have", icon='EYEDROPPER')
        op.role, op.section = role.name, sec.title
    if opened:
        col = b.box().column()
        wrap(col, c, kit.explain(k, role), icon=KIND_ICON.get(role.kind, 'DOT'), boxes=4)
        if role.kind == 'solid':
            wrap(col, c, "The warning sign by it: its colliders are missing or out of date. Press it to make them.", boxes=4)
        if accepts:
            wrap(col, c, f"On each slot: the mesh button makes the selected mesh this slot's {accepts}; the import button "
                         f"fits one of the game's {accepts}s as it is. A tick: it has its {accepts} (click it to select it).",
                 boxes=4)
        wrap(col, c, "Buttons: import takes the Sigma's, + makes one at the 3D cursor, the dropper adds a row to pick one "
                     "you have, x leaves it out. Hover any of them to be told again.", boxes=4)


def draw_section(b, c, sec, p, ins, k):
    s = c.scene.sigma
    wrap(b, c, sec.desc, boxes=2)
    if ins == "hand":
        wrap(b, c, "Your script already does this its own way, so the add-on leaves those lines alone. The picks below "
                   "are only for finding and selecting the objects.", icon='INFO', boxes=2)
    st = _state(s, sec.title)
    if st is not None:
        row = b.split(factor=0.45, align=True)
        row.label(text="Look in", icon='VIEWZOOM')
        row.prop(st, "scope", text="")
    col = b.column(align=True)
    for role in sec.roles:
        draw_role(col, c, sec, role, p, k)
    row = b.row(align=True)
    if ins == "block":
        row.operator("sigma.remove_kit", icon='TRASH').section = sec.title
    elif not ins:
        row.operator("sigma.add_kit", text="Add to car", icon='ADD').sections = sec.title


def draw_car_kit(l, c):
    s = c.scene.sigma
    k, p, rows = kit_state(c)
    if not k:
        wrap(l, c, "This SigmaMod has no Sigma kit in it (it comes with SigmaMod 2.4 and later). Update it and I'll "
                   "hand you wheels.", icon='ERROR')
        return
    box = kit.body_box(s)
    if box is None:
        wrap(l, c, "First the body: model it (or bring it in) under your top object. Everything else gets bolted to "
                   "it, in proportion, so it helps if it exists.", icon='INFO')
        return
    size = box[1] - box[0]
    l.label(text=f"Body: {size.y:.2f} m long, {size.x:.2f} m wide, {size.z:.2f} m tall", icon='MESH_CUBE')
    wrap(l, c, "Each part below is made of objects. Yours are found by name; open a part to see what was found, "
               "pick others (or point it at a group they're in), or take the Sigma's for what you haven't got.")
    if any(sec.required and not ins for sec, ok, ins, _ in rows):
        row = l.row()
        row.scale_y = 1.4
        row.operator("sigma.add_kit", text="Add everything a car needs", icon='ADD').sections = ""
    if any(ins == "block" and not fresh for sec, ok, ins, fresh in rows):
        l.operator("sigma.update_kit_script", icon='FILE_REFRESH')
    for required in (True, False):
        col = l.column(align=True)
        col.label(text="What a car needs" if required else "Nice to have")
        for sec, ok, ins, fresh in rows:
            if sec.required != required:
                continue
            b = col.box()
            row = b.row(align=True)
            opened = s.open_kit == sec.title
            row.operator("sigma.open_kit", text=sec.title, icon='DOWNARROW_HLT' if opened else 'RIGHTARROW',
                         emboss=False).section = sec.title
            icon = 'CHECKMARK' if ok and ins else ('ERROR' if (required or ins) and not ok else
                                                   ('ERROR' if required else 'RADIOBUT_OFF'))
            row.label(text={"block": "in car", "hand": "in car (by hand)"}.get(ins, ""), icon=icon)
            if not ins:
                row.operator("sigma.add_kit", text="Add", icon='ADD').sections = sec.title
            op = row.operator("sigma.select_kit", text="", icon='RESTRICT_SELECT_OFF')
            op.section = sec.title
            if opened:
                draw_section(b, c, sec, p, ins, k)
    draw_parts(l, c)
    for prop, icon, draw in (("open_preview", 'HIDE_OFF', preview.draw), ("open_tuning", 'PREFERENCES', tuning.draw)):
        box = l.box()
        box.prop(s, prop, icon='DOWNARROW_HLT' if getattr(s, prop) else 'RIGHTARROW', emboss=False)
        if getattr(s, prop):
            draw(box.column(), c, wrap)
    wrap(l, c, "The Sigma's objects land where the Sigma has them, stretched to your body. Move each where it belongs: "
               "wheel hubs at the wheel centres, the seat where a bum goes, the engine mount in the engine bay. Move "
               "and turn them; never scale the hinges.", icon='INFO')


def draw_parts(l, c):
    s = c.scene.sigma
    box = l.box()
    box.label(text="Parts that go with your car", icon='MOD_BUILD')
    wrap(box, c, "Doors, hoods, trunk lids, seats: parts of their own, in the same file and script, working as the game's "
                 "do. Make one on a slot in Seats, doors and body panels: select your mesh, press the mesh button by the "
                 "slot.", boxes=2)
    if not s.extras:
        return
    for i, e in enumerate(s.extras):
        b = box.box()
        row = b.row(align=True)
        row.label(text=f"{e.accepts.capitalize()}, from the game's {e.donor}", icon='OBJECT_DATA')
        if e.root is not None:
            row.operator("sigma.select_objects", text="", icon='RESTRICT_SELECT_OFF').names = e.root.name
        row.operator("sigma.remove_extra", text="", icon='TRASH').index = i
        b.prop(e, "root", text="Object")
        b.prop(e, "asset_id", text="Id")
        b.prop(e, "display_name", text="Name in game")
        if e.root is not None and not e.root.get(extras.PART_SWAPPED):
            wrap(b, c, "Now your mesh: select it, then the game part's mesh (last, so it's the active one), and:", boxes=3)
            b.operator("sigma.use_my_mesh", icon='MOD_DATA_TRANSFER')


def draw_build(l, c):
    s = c.scene.sigma
    if s.kind == 'VARIANT':
        wrap(l, c, "A copy of a game object: nothing goes under it. Its settings go in the script.")
        box = l.box()
        box.prop(s, "open_spawning", icon='DOWNARROW_HLT' if s.open_spawning else 'RIGHTARROW', emboss=False)
        if s.open_spawning:
            spawn.draw(box.column(), c, wrap)
        return
    if s.kind == 'CAR':
        draw_car_kit(l, c)
    elif s.kind == 'ATTACHABLE':
        wrap(l, c, "An item that bolts onto vehicles: its meshes under its top object. Below: what it's made of, what "
                   "changes when it's fitted, what hurts, and how it comes off.")
        attach.draw(l.box().column(), c, wrap)
    else:
        wrap(l, c, "Change meshes and move things freely, but leave the custom properties on the game's objects "
                   "alone: they are how SigmaMod knows what each one is. Delete a game object and it stops drawing, but "
                   "its logic stays.")
    box = l.box()
    box.prop(s, "open_spawning", icon='DOWNARROW_HLT' if s.open_spawning else 'RIGHTARROW', emboss=False)
    if s.open_spawning:
        spawn.draw(box.column(), c, wrap)
    tips = ["Faces show from one side only in game. Thin panels need thickness (Solidify).",
            "The game's own materials cost nothing in your file: Use a game material."]
    for t in tips:
        wrap(l, c, t, icon='DOT')
    row = l.row(align=True)
    row.operator("sigma.game_material", icon='MATERIAL')
    row.operator("sigma.toggle_colliders", icon='HIDE_OFF', text="Colliders")


def colliders_done(c):
    s = c.scene.sigma
    if s.kind in ('CHANGED', 'CHANGED_CAR', 'VARIANT'):
        return True   # the game's objects come with theirs
    src = script.read_script(s) or ""
    return any(o.get("generated") for o in checks.asset_objects(s)) or "colliders" in src


def draw_colliders(l, c):
    s = c.scene.sigma
    wrap(l, c, "Colliders are what's solid: what you stand on, bump into, and what holds the car off the ground. "
               "Select the body and make colliders that follow its shape. Changed the shape? Make them again; I'll "
               "nag you if you forget.")
    if s.kind in ('CHANGED', 'CHANGED_CAR'):
        wrap(l, c, "A game object already has the game's colliders; this is for parts of your own.", icon='INFO')
    col = l.column(align=True)
    col.operator("sigma.make_colliders", icon='MOD_MESHDEFORM')
    col.operator("sigma.remove_colliders", icon='TRASH')
    col.operator("sigma.toggle_colliders", icon='HIDE_OFF')
    wrap(l, c, "Something simple (a box, a plank)? Its own mesh can be its collider: Script > Add a part > colliders.")


def script_done(c):
    p = paths.asset_lua(c.scene.sigma)
    return bool(p) and os.path.isfile(p)


def draw_script(l, c):
    s = c.scene.sigma
    wrap(l, c, "The script says what each object does: which is the engine mount, where wheels go, what's the fuel "
               "tank. A text file next to the model. The Build step's parts write theirs in already.")
    p = paths.asset_lua(s)
    col = l.column(align=True)
    if p and os.path.isfile(p):
        col.operator("sigma.open_script", icon='TEXT')
        col.operator("sigma.add_part", icon='ADD')
        if script.unsaved(s):
            col.operator("sigma.save_script", icon='FILE_TICK')
        wrap(l, c, "Select objects in the viewport, then Add a part: SigmaMod's parts, written the way SigmaMod's own "
                   "assets use them, your objects' names filled in.", icon='INFO')
    else:
        col.operator("sigma.make_script", icon='FILE_NEW')


def export_done(c):
    return bool(c.scene.sigma.last_export)


def draw_export(l, c):
    s = c.scene.sigma
    row = l.row(align=True)
    row.scale_y = 1.4
    row.operator("sigma.export", icon='EXPORT')
    bad = checks.errors(export.last_problems)
    if bad:
        wrap(l, c, f"{len(bad)} thing{'s' if len(bad) > 1 else ''} stop{'' if len(bad) > 1 else 's'} the export. "
                   "Clunky, up top, has the details. He's thrilled.", icon='CANCEL')
    if s.last_export:
        l.label(text=f"Exported at {s.last_export}", icon='CHECKMARK')
    hot = assets.config_value("Tools", "HotReload")
    box = l.box()
    box.label(text="In the game", icon='PLAY')
    if hot == "true":
        wrap(box, c, "Hot reload is on: a running game rebuilds the asset a moment after you export.", boxes=2)
    else:
        wrap(box, c, "Load your save again to see it. Or turn on Hot reload, and the running game rebuilds it each "
                     "time you export.", boxes=2)
        if hot == "false":
            box.operator("sigma.hot_reload", icon='FILE_REFRESH')
    wrap(box, c, "Spawn it from Apocaspawner, or find it where the game spawns its kind.", boxes=2)
    box.operator("sigma.game_log", icon='CONSOLE')


STEPS = [
    ("Set up", setup_done, draw_setup),
    ("Start an asset", start_done, draw_start),
    ("Name it", name_done, draw_name),
    ("Build it", build_done, draw_build),
    ("Colliders", colliders_done, draw_colliders),
    ("Script", script_done, draw_script),
    ("Put it in the game", export_done, draw_export),
]


STEP_HELP = [
    "Set up: find the game and install the helper tools (once). A tick: done",
    "Start an asset: one top object with everything of it under it. Import a game object, start from an empty, or "
    "copy a game object under a new name",
    "Name it: the id the game knows it by (pick it once), the name players see, and your mod's folder",
    "Build it: what your asset is made of. For a car: the parts a car needs and the nice-to-haves, each found among "
    "your objects or taken from the Sigma",
    "Colliders: what things bump into. Made from your meshes, as objects you can see and keep",
    "Script: the Lua file next to the model that tells SigmaMod what each object does",
    "Put it in the game: checks, then the export into your mod's folder, and what the game said about it",
]


def current_step(c):
    done = [d(c) for _, d, _ in STEPS]
    return done, next((i for i, d in enumerate(done) if not d), len(STEPS))


class SIGMA_OT_open_build(bpy.types.Operator):
    """Open the Build step at this part of the car, to see what it needs and what's missing"""
    bl_idname = "sigma.open_build"
    bl_label = "Show me"

    section: bpy.props.StringProperty()

    def execute(self, context):
        s = context.scene.sigma
        s.open_step = 3
        s.open_kit = self.section
        return {'FINISHED'}


class SIGMA_OT_open_step(bpy.types.Operator):
    """Open this step"""
    bl_idname = "sigma.open_step"
    bl_label = "Open step"

    @classmethod
    def description(cls, context, props):
        return STEP_HELP[props.step] if 0 <= props.step < len(STEP_HELP) else cls.__doc__

    step: bpy.props.IntProperty()

    def execute(self, context):
        s = context.scene.sigma
        s.open_step = -1 if s.open_step == self.step else self.step
        return {'FINISHED'}


class SIGMA_PT_clunky(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "SigmaMod"
    bl_label = "Clunky, the Apoca Sigma helper for coughing babies"
    bl_description = "Says what's wrong with what you're doing, one thing at a time, with the button that fixes it"
    bl_order = 0

    def draw(self, context):
        l = self.layout
        _, step = current_step(context)
        text, prob, n = mascot.say(context, step)
        mascot.heard(text)
        icon_px = 4.5 * 20 * _scale(context)
        frac = min(0.45, (icon_px + 8) / max(context.region.width, 1))
        split = l.split(factor=frac)
        left = split.column()
        if mascot.icon():
            left.template_icon(icon_value=mascot.icon(), scale=4.5)
        left.operator("sigma.about_clunky", text="Clunky", icon='QUESTION', emboss=False)   # hover: who he is
        bubble = split.box()
        icon = {'ERROR': 'CANCEL', 'WARNING': 'ERROR', 'INFO': 'INFO'}.get(prob.level if prob else "", 'NONE')
        wrap(bubble, context, text, icon=icon, boxes=1, frac=1 - frac)
        if prob:
            col = bubble.column(align=True)
            if prob.fix:
                set_args(col.operator(prob.fix, text=prob.fix_text), prob.fix_args)
            for op, label, args in prob.also:
                set_args(col.operator(op, text=label), args)
            row = bubble.row(align=True)
            if n > 1:
                row.operator("sigma.hint_next", text=f"Next ({context.window_manager.sigma_hint % n + 1}/{n})")
            row.operator("sigma.hint_dismiss", text="Shut up, Clunky").key = prob.key
        elif mascot.dismissed:
            bubble.operator("sigma.hint_undismiss", icon='HEART')
        wm = context.window_manager
        if wm.sigma_status:
            row = l.row()
            row.label(text=wm.sigma_status, icon='SORTTIME' if wm.sigma_busy else 'INFO')
            if wm.sigma_busy:
                row.label(text="Esc cancels")
            elif "failed" in wm.sigma_status:
                row.operator("sigma.show_log", text="", icon='TEXT')


class SIGMA_PT_guide(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "SigmaMod"
    bl_label = "Guide"
    bl_description = "Seven steps from nothing to your asset in the game; each is ticked off when it's done"
    bl_order = 1

    def draw(self, context):
        l = self.layout
        done, current = current_step(context)
        current = min(current, len(STEPS) - 1)
        s = context.scene.sigma
        open_step = s.open_step if s.open_step >= 0 else current
        for i, (title, _, draw) in enumerate(STEPS):
            box = l.box()
            row = box.row()
            icon = 'CHECKMARK' if done[i] else ('PLAY' if i == current else 'RADIOBUT_OFF')
            row.operator("sigma.open_step", text=f"{i + 1}. {title}", icon=icon, emboss=False).step = i
            row.label(icon='DOWNARROW_HLT' if i == open_step else 'RIGHTARROW')
            if i == open_step:
                draw(box, context)


class SIGMA_PT_tools(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "SigmaMod"
    bl_label = "Tools"
    bl_description = "Every button of the guide in one place, for when you know what you want"
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 2

    def draw(self, context):
        col = self.layout.column(align=True)
        for op, icon in (("sigma.import_game_object", 'IMPORT'), ("sigma.game_material", 'MATERIAL'),
                         ("sigma.restore_material_ids", 'MATERIAL_DATA'), ("sigma.make_colliders", 'MOD_MESHDEFORM'),
                         ("sigma.remove_colliders", 'TRASH'), ("sigma.toggle_colliders", 'HIDE_OFF'),
                         ("sigma.open_script", 'TEXT'), ("sigma.add_part", 'ADD'), ("sigma.use_my_mesh", 'MOD_DATA_TRANSFER'),
                         ("sigma.recheck", 'VIEWZOOM'),
                         ("sigma.export", 'EXPORT'), ("sigma.game_log", 'CONSOLE'), ("sigma.open_asset_folder", 'FILEBROWSER')):
            col.operator(op, icon=icon)


class SIGMA_OT_car_input(bpy.types.Operator):
    """For SigmaMod's own Lua library: write lua/sigma/car_input.lua again from a car's FSM export
(BepInEx/SigmaMod-export/<Car>.fsm.json). Nothing an asset needs"""
    bl_idname = "sigma.car_input"
    bl_label = "Rebuild car_input.lua"

    filepath: bpy.props.StringProperty(subtype='FILE_PATH', description="Where to write it")
    filter_glob: bpy.props.StringProperty(default="*.fsm.json", options={'HIDDEN'})

    def invoke(self, context, event):
        self.filepath = os.path.join(paths.export_dir(), "Outrider.fsm.json") if paths.export_dir() else ""
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        import subprocess
        out = os.path.join(paths.sigmamod_dir(), "lua", "sigma", "car_input.lua")
        r = subprocess.run([sys.executable, "-I", os.path.join(paths.tools_dir(), "car_input.py"), self.filepath, out],
                           capture_output=True, text=True, **jobs.no_window())
        jobs.log_text().write(r.stdout + r.stderr)
        if r.returncode:
            self.report({'ERROR'}, "car_input.py failed; see the tools' output")
            return {'CANCELLED'}
        self.report({'INFO'}, "Wrote " + out)
        return {'FINISHED'}


class SIGMA_PT_advanced(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "SigmaMod"
    bl_label = "Advanced"
    bl_description = "Things you shouldn't need: for SigmaMod's own library, and fixing files by hand"
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 3

    def draw(self, context):
        l = self.layout
        s = context.scene.sigma
        l.prop(s, "root", text="Root")
        l.prop(s, "kind", text="Kind")
        col = l.column(align=True)
        col.operator("sigma.show_log", icon='TEXT')
        col.operator("sigma.install_tools", text="Reinstall helper tools", icon='FILE_REFRESH')
        col.operator("sigma.car_input", icon='SCRIPT')
        col.operator("sigma.hint_refresh", icon='FILE_REFRESH')
        if paths.export_dir():
            col.operator("wm.path_open", text="Open the game's exports", icon='FILEBROWSER').filepath = paths.export_dir()
        wrap(l, context, "Tools from: " + (paths.tools_dir() or "not found"))
        wrap(l, context, "Parts and kit from: " + (sigmasrc.parts_lua() or "not found"))


classes = (SIGMA_OT_open_build, SIGMA_OT_open_step, SIGMA_OT_car_input, SIGMA_PT_clunky, SIGMA_PT_guide, SIGMA_PT_tools, SIGMA_PT_advanced)
