# The SigmaMod tab in the 3D view's sidebar (N): Clunky at the top saying what's wrong, a guide of numbered steps, each
# ticked off when it is done, the current one open with what to do and the buttons that do it; then the tools on their
# own, and the advanced ones.
import os
import sys

import blf
import bpy

from . import assets, checks, export, gamedata, jobs, kit, mascot, paths, script, sigmasrc


def _scale(context):
    return context.preferences.system.ui_scale or 1


def _lines(context, text, width):
    sf = _scale(context)
    try:
        blf.size(0, context.preferences.ui_styles[0].widget.points * sf)
        measure = lambda t: blf.dimensions(0, t)[0] * 1.18   # Blender draws labels wider than blf measures them
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
    width = context.region.width * frac - (30 + 14 * boxes) * sf - (24 * sf if icon != 'NONE' else 0) - 16 * sf
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
    l.prop(s, "mod_folder", text="Folder")
    if paths.asset_dir(s):
        wrap(l, c, paths.asset_dir(s), icon='FILE_FOLDER')
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


def draw_role(b, c, sec, role, p, k):
    s = c.scene.sigma
    its = kit.items(s, role.name)
    got = p.get(role.name)
    left_out = any(r.none for r in its)
    icon = 'CHECKMARK' if got else ('X' if left_out else ('RADIOBUT_OFF' if role.optional else 'ERROR'))
    need = ("any number" if role.multi else "") if role.optional else ("required: the part doesn't work without it" if not role.multi
                                                                       else "at least one, required")
    head = b.row(align=True)
    head.label(text=role.label, icon=icon)
    if got:
        op = head.operator("sigma.select_kit", text="", icon='RESTRICT_SELECT_OFF')
        op.role, op.section = role.name, sec.title
    kind = k["kinds"].get(role.kind, "")
    what = f"{role.kind.capitalize()}: {kind}" if kind else ""
    wrap(b, c, (role.help + ". " if role.help else "") + (f"({need}) " if need else "") + what, icon=KIND_ICON.get(role.kind, 'DOT'), boxes=3)
    if not its:
        b.label(text="Looking for yours...", icon='VIEWZOOM')
    for i, it in enumerate(its):
        if it.none:
            continue
        row = b.row(align=True)
        row.prop(it, "obj", text="")
        if role.multi and len(its) > 1:
            op = row.operator("sigma.role_slot", text="", icon='X')
            op.role, op.section, op.remove = role.name, sec.title, i
        if it.obj is not None and role.kind == 'solid' and it.obj.type == 'MESH':
            made, stale = kit.made_colliders(it.obj)
            crow = b.row(align=True)
            if made and not stale:
                crow.label(text=f"Colliders: {made} pieces, up to date", icon='CHECKMARK')
            else:
                crow.label(text="Colliders: " + ("out of date" if made else "none yet"), icon='ERROR')
                crow.operator("sigma.make_colliders", text="Make them again" if made else "Make its colliders",
                              icon='MOD_MESHDEFORM').target = it.obj.name
    row = b.row(align=True)
    if not got:
        row.operator("sigma.bring_role", text="The Sigma's", icon='IMPORT').role = role.name
        if role.kind in ('spot', 'marker', 'collider'):
            row.operator("sigma.make_role_object", text="Make one here", icon='ADD').role = role.name
        if role.optional and not left_out:
            op = row.operator("sigma.role_slot", text="Leave out", icon='X')
            op.role, op.section, op.leave_out = role.name, sec.title, True
    if role.multi:
        op = row.operator("sigma.role_slot", text="Another", icon='ADD')
        op.role, op.section = role.name, sec.title
    if left_out:
        op = row.operator("sigma.role_slot", text="I have one after all", icon='LOOP_BACK')
        op.role, op.section = role.name, sec.title


def draw_section(b, c, sec, p, ins, k):
    s = c.scene.sigma
    wrap(b, c, sec.desc, boxes=2)
    if ins == "hand":
        wrap(b, c, "Your script already does this its own way, so the add-on leaves those lines alone. The picks below "
                   "are only for finding and selecting the objects.", icon='INFO', boxes=2)
    st = _state(s, sec.title)
    if st is not None:
        wrap(b, c, "Look in: a group (an empty with them under it) to find this part's objects in. Empty: the whole "
                   "asset.", boxes=2)
        b.prop(st, "scope", text="", icon='OUTLINER_OB_EMPTY')
    for role in sec.roles:
        draw_role(b.box(), c, sec, role, p, k)
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
    wrap(l, c, "The Sigma's objects land where the Sigma has them, stretched to your body. Move each where it belongs: "
               "wheel hubs at the wheel centres, the seat where a bum goes, the engine mount in the engine bay. Move "
               "and turn them; never scale the hinges.", icon='INFO')


def draw_build(l, c):
    s = c.scene.sigma
    if s.kind == 'VARIANT':
        wrap(l, c, "A copy of a game object: nothing goes under it. Its settings go in the script.")
        return
    if s.kind == 'CAR':
        draw_car_kit(l, c)
    elif s.kind == 'ATTACHABLE':
        wrap(l, c, "An item that bolts onto cars: one mesh (or a few) under its top object. The script lists which "
                   "meshes it collides with, and its mass. That's it. Enjoy the simplicity while it lasts.")
    else:
        wrap(l, c, "Change meshes and move things freely, but leave the custom properties on the game's objects "
                   "alone: they are how SigmaMod knows what each one is. Delete a game object and it stops drawing, but "
                   "its logic stays.")
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


def current_step(c):
    done = [d(c) for _, d, _ in STEPS]
    return done, next((i for i, d in enumerate(done) if not d), len(STEPS))


class SIGMA_OT_open_build(bpy.types.Operator):
    """Show it in the Build step"""
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
        else:
            left.label(icon='QUESTION')
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
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 2

    def draw(self, context):
        col = self.layout.column(align=True)
        for op, icon in (("sigma.import_game_object", 'IMPORT'), ("sigma.game_material", 'MATERIAL'),
                         ("sigma.restore_material_ids", 'MATERIAL_DATA'), ("sigma.make_colliders", 'MOD_MESHDEFORM'),
                         ("sigma.remove_colliders", 'TRASH'), ("sigma.toggle_colliders", 'HIDE_OFF'),
                         ("sigma.open_script", 'TEXT'), ("sigma.add_part", 'ADD'), ("sigma.recheck", 'VIEWZOOM'),
                         ("sigma.export", 'EXPORT'), ("sigma.game_log", 'CONSOLE'), ("sigma.open_asset_folder", 'FILEBROWSER')):
            col.operator(op, icon=icon)


class SIGMA_OT_car_input(bpy.types.Operator):
    """For SigmaMod's own Lua library: write lua/sigma/car_input.lua again from a car's FSM export
(BepInEx/SigmaMod-export/<Car>.fsm.json). Nothing an asset needs"""
    bl_idname = "sigma.car_input"
    bl_label = "Rebuild car_input.lua"

    filepath: bpy.props.StringProperty(subtype='FILE_PATH')
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
