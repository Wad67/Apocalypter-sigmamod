# Where an asset turns up in the world, and how often. The game spawns cars, trailers, loot and merchants' stock by
# drawing from its spawn lists; SigmaMod adds an asset to every list that holds the game objects it "spawns like"
# (g.spawn_like: their Easy Save ids; by default the game object it was made from), and g.spawn weighs it there: 1 as
# likely as each other entry, 0.25 a quarter, 0 (or false) never. Set in the Build step, written as a "-- ## Spawning"
# block (only once changed here), read back from the script.
import re

import bpy

from . import gamedata, paths, script

HEADER = "-- ## Spawning"
NOTE = "-- Written by the SigmaMod add-on from Build > Spawning. Change it there: export writes this again."
OWN = re.compile(r"(-- (?!#)|g\.spawn(_like)?\s*=)")


def _touch(self, context):
    context.scene.sigma.spawning.touched = True


class SigmaSpawnLike(bpy.types.PropertyGroup):
    name: bpy.props.StringProperty(description="The game object it spawns where")
    prefab: bpy.props.StringProperty(description="Its Easy Save prefab id (what the script names it by)")


class SigmaSpawning(bpy.types.PropertyGroup):
    loaded: bpy.props.BoolProperty()
    source: bpy.props.StringProperty(description="The script the settings were read from")
    touched: bpy.props.BoolProperty(description="Changed here since the script was last written")
    spawns: bpy.props.BoolProperty(name="Spawns in the world", default=True, update=_touch,
                                   description="Turns up where the game objects below do (on the road, in camps, in "
                                               "merchants' stock, as loot). Off: only from a spawner tool or a script")
    weight: bpy.props.FloatProperty(name="How likely", min=0.01, max=100, default=1, update=_touch,
                                    description="Next to each other thing in the same spawn list: 1 as likely, 0.25 a "
                                                "quarter as likely, 2 twice. Where the game weighs its loot, it gets "
                                                "the weight of what it spawns like, times this")
    like: bpy.props.CollectionProperty(type=SigmaSpawnLike)


def prefabs():
    """[(export name, Easy Save prefab id)] of the game's objects from Export All, by name."""
    key = gamedata.folder_key(paths.export_dir())
    if prefabs.cache and prefabs.cache[0] == key:
        return prefabs.cache[1]
    out = []
    for name, path in gamedata.exports(include_live=False):
        ids = [ex.get("es3prefab") for _, ex in gamedata.root_extras(path) if ex.get("es3prefab")]
        if ids:
            out.append((name, str(ids[0])))
    prefabs.cache = (key, out)
    return out


prefabs.cache = None


def span(lines):
    try:
        a = lines.index(HEADER)
    except ValueError:
        return None
    b = a + 1
    while b < len(lines) and lines[b].strip() and OWN.match(lines[b]):
        b += 1
    return a, b


def read(src):
    """(spawn value or None, [prefab ids]): the last the script sets, as it runs."""
    spawn, like = None, []
    for m in re.finditer(r"^\s*g\.spawn\s*=\s*(true|false|-?[\d.]+)", src or "", re.M):
        spawn = m.group(1)
    for m in re.finditer(r"^\s*g\.spawn_like\s*=\s*\{([^}]*)\}", src or "", re.M):
        like = re.findall(r'"(\d+)"', m.group(1))
    return spawn, like


def current(context):
    s = context.scene.sigma
    return s.spawning.loaded and s.spawning.source == (paths.asset_lua(s) or "")


def load(context, force=False):
    s = context.scene.sigma
    sp = s.spawning
    if current(context) and not force:
        return
    spawn, like = read(script.read_script(s) or "")
    names = {pid: n for n, pid in prefabs()}
    sp.spawns = spawn not in ("false", "0")
    try:
        sp.weight = float(spawn) if spawn not in (None, "true", "false") and float(spawn) > 0 else 1.0
    except ValueError:
        sp.weight = 1.0
    sp.like.clear()
    for pid in like:
        x = sp.like.add()
        x.prefab, x.name = pid, names.get(pid, f"game object {pid}")
    sp.loaded, sp.touched, sp.source = True, False, paths.asset_lua(s) or ""


def block(context):
    sp = context.scene.sigma.spawning
    lines = [HEADER, NOTE]
    if sp.like:
        lines.append("g.spawn_like = { " + ", ".join(f'"{x.prefab}"' for x in sp.like) + " }   -- where these spawn: "
                     + ", ".join(x.name for x in sp.like))
    if not sp.spawns:
        lines.append("g.spawn = false   -- never on its own")
    elif abs(sp.weight - 1) > 1e-6:
        lines.append(f"g.spawn = {sp.weight:g}   -- {sp.weight:g} times as likely as each other thing there")
    return "\n".join(lines)


def write(context):
    """The block written again (only once something was changed here); the script's own g.spawn and g.spawn_like
    lines go into it."""
    s = context.scene.sigma
    path = paths.asset_lua(s)
    if not path or not current(context) or not s.spawning.touched:
        return False
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        return False
    lines = src.split("\n")
    sp = span(lines)
    if sp:
        a, b = sp
        if b < len(lines) and not lines[b].strip():
            b += 1
        lines = lines[:a] + lines[b:]
    lines = [l for l in lines if not re.match(r"\s*g\.spawn(_like)?\s*=", l)]
    new = script.insert("\n".join(lines), block(context), s.asset_id)
    if new != src:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new)
        script.reload_text(path)
    s.spawning.touched = False
    return True


def like_items(self, context):
    have = {x.prefab for x in context.scene.sigma.spawning.like}
    like_items.cache = [(pid, n, f"Spawn wherever the game's {n} spawns") for n, pid in prefabs() if pid not in have] \
        or [("", "(nothing exported from the game yet)", "")]
    return paths.stable("spawn_like", like_items.cache)


like_items.cache = []


class SIGMA_OT_spawn_like(bpy.types.Operator):
    """Add a game object: yours then also spawns wherever it does (its spawn lists: the road, camps, merchants,
    loot). Pick by name"""
    bl_idname = "sigma.spawn_like"
    bl_label = "Spawns where"
    bl_property = "prefab"
    bl_options = {'REGISTER', 'UNDO'}

    prefab: bpy.props.EnumProperty(name="Game object", items=like_items,
                                   description="One of the game's objects, from Export All")

    def invoke(self, context, event):
        if not prefabs():
            self.report({'ERROR'}, "Nothing exported from the game yet (step 2 says how)")
            return {'CANCELLED'}
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        sp = context.scene.sigma.spawning
        if not self.prefab:
            return {'CANCELLED'}
        x = sp.like.add()
        x.prefab, x.name = self.prefab, dict((pid, n) for n, pid in prefabs()).get(self.prefab, self.prefab)
        sp.touched = True
        return {'FINISHED'}


class SIGMA_OT_spawn_unlike(bpy.types.Operator):
    """Take this game object off: yours no longer joins its spawn lists"""
    bl_idname = "sigma.spawn_unlike"
    bl_label = "Remove"
    bl_options = {'REGISTER', 'UNDO'}

    index: bpy.props.IntProperty(options={'HIDDEN'})

    def execute(self, context):
        sp = context.scene.sigma.spawning
        if 0 <= self.index < len(sp.like):
            sp.like.remove(self.index)
            sp.touched = True
        return {'FINISHED'}


class SIGMA_OT_spawn_read(bpy.types.Operator):
    """Read where it spawns again from the script, dropping changes not written yet"""
    bl_idname = "sigma.spawn_read"
    bl_label = "Read where it spawns"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        load(context, force=True)
        return {'FINISHED'}


class SIGMA_OT_spawn_write(bpy.types.Operator):
    """Write where it spawns into the script now (export does it too)"""
    bl_idname = "sigma.spawn_write"
    bl_label = "Write it to the script"

    def execute(self, context):
        if not write(context):
            self.report({'INFO'}, "Nothing changed here, or no script yet (step 6 makes it)")
            return {'CANCELLED'}
        return {'FINISHED'}


def draw(l, c, wrap):
    s = c.scene.sigma
    sp = s.spawning
    if not current(c):
        wrap(l, c, "Where it spawns starts from what its script says.", boxes=2)
        l.operator("sigma.spawn_read", icon='IMPORT')
        return
    l.prop(sp, "spawns")
    if not sp.spawns:
        wrap(l, c, "It won't turn up on its own: only from a spawner tool (Apocaspawner) or your own script.", icon='INFO', boxes=2)
    else:
        l.prop(sp, "weight", slider=False)
        b = l.box().column(align=True)
        row = b.row(align=True)
        row.label(text="Spawns where these do", icon='WORLD')
        row.operator("sigma.spawn_like", text="", icon='ADD')
        for i, x in enumerate(sp.like):
            row = b.row(align=True)
            row.label(text=x.name, icon='OBJECT_DATA')
            row.operator("sigma.spawn_unlike", text="", icon='X').index = i
        if not sp.like:
            scratch = s.kind in ('CAR', 'ATTACHABLE')
            wrap(b, c, "None yet: made from scratch, it's like nothing in the game, so it spawns nowhere. Add the game "
                       "objects it should turn up with (a car: the Outrider; a plate: metal_plate_1)." if scratch else
                 "None: it spawns where the game object it's made from does.", icon='ERROR' if scratch else 'INFO', boxes=3)
    row = l.row(align=True)
    row.operator("sigma.spawn_write", icon='FILE_REFRESH' if sp.touched else 'CHECKMARK',
                 text="Write it to the script" if sp.touched else "In the script")
    row.operator("sigma.spawn_read", text="", icon='LOOP_BACK')


classes = (SIGMA_OT_spawn_like, SIGMA_OT_spawn_unlike, SIGMA_OT_spawn_read, SIGMA_OT_spawn_write)
props = (SigmaSpawnLike, SigmaSpawning)
