# The asset's Lua script: a starting script for each kind of asset, opened in Blender's text editor, parts added for
# the selected objects, and the names it mentions checked against the scene.
import os
import re

import bpy

from . import paths

KINDS = [
    ('CAR', "Car made from scratch", "A drivable vehicle of your own: body, wheels, seat, engine mount, all from the parts library"),
    ('CHANGED_CAR', "Changed game car", "A game car you imported and changed: it drives on its own setup (mass, drive) from its wheels"),
    ('ATTACHABLE', "Item that bolts onto vehicles", "A loose item you pick up and fix to a vehicle with Use, like the game's metal plates"),
    ('CHANGED', "Other changed game object", "Any game object you imported and changed (a part, an item, a prop)"),
    ('VARIANT', "Game object under a new name", "A game object, unchanged, as a new thing of its own (tune it in the script)"),
]

HEAD = '''-- {id}.lua: {name}. Every name in quotes is the name of an object in Blender (capitals matter).
-- SigmaMod's README lists every part; SigmaMod/sigma_assets/apocasigma.lua and sigma_test.lua use most of them.
local parts = require "sigma.parts"

'''

BODIES = {
    'CAR': '''local g = parts.chassis {{ mass = 1000, drive = "rear", speed = true, flip_over = true }}   -- kg; "front", "rear" or "all"
g.model = {q_root}
g.name = {q_name}

-- Parts go here. The SigmaMod panel's Build step adds the Sigma's (wheels, seat, engine bay...) as sections;
-- Script > Add a part adds one part for the selected objects.

template "{id}" (g)
''',
    'CHANGED_CAR': '''local g = {{
  model = {q_root},
  name = {q_name},
  type = "chassis",                -- drives on its own setup, made from its wheels
  Rigidbody = {{ mass = 1000 }},     -- kg
  drive = "rear",                  -- "front", "rear" or "all"
  nodes = {{}}, playmaker = {{}},
}}

-- Parts go here. In Blender: select objects, then SigmaMod panel > Script > Add a part.

template "{id}" (g)
''',
    'ATTACHABLE': '''local g = {{ model = {q_root}, name = {q_name} }}
parts.attachable(g, {{
  mass = 10,   -- kg
  solid = {{ {colliders} }},   -- seen and solid
}})

template "{id}" (g)
''',
    'CHANGED': '''local g = {{ model = {q_root}, name = {q_name}, nodes = {{}}, playmaker = {{}} }}

-- Parts go here. In Blender: select objects, then SigmaMod panel > Script > Add a part.

template "{id}" (g)
''',
    'VARIANT': '''local g = {{ model = {q_root}, name = {q_name} }}

template "{id}" (g)
''',
}


def starting_script(kind, asset_id, name, root):
    meshes = [o.name for o in [root] + list(root.children_recursive) if o.type == 'MESH' and o.get("sigma") != "collider"]
    colliders = ", ".join(_q(n) for n in meshes[:8]) or _q(root.name)
    return HEAD.format(id=asset_id, name=name) + BODIES[kind].format(id=asset_id, name=name, root=root.name, colliders=colliders,
                                                                   q_root=_q(root.name), q_name=_q(name))


# ---- the text in Blender

def text_for(path, load=True):
    path = os.path.normpath(path)
    for t in bpy.data.texts:
        if t.filepath and os.path.normpath(bpy.path.abspath(t.filepath)) == path:
            return t
    return bpy.data.texts.load(path) if load and os.path.isfile(path) else None


def _editors():
    for w in bpy.context.window_manager.windows:
        for a in w.screen.areas:
            if a.type == 'TEXT_EDITOR':
                yield w, a


def show_text(context, text):
    for _, a in _editors():
        a.spaces.active.text = text
        return True
    # the Scripting workspace has a text editor
    ws = bpy.data.workspaces.get("Scripting")
    if ws is not None:
        context.window.workspace = ws
        for scr in ws.screens:
            for a in scr.areas:
                if a.type == 'TEXT_EDITOR':
                    a.spaces.active.text = text
                    return True
    # a window of its own
    bpy.ops.wm.window_new()
    w = context.window_manager.windows[-1]
    a = max(w.screen.areas, key=lambda a: a.width * a.height)
    a.type = 'TEXT_EDITOR'
    a.spaces.active.text = text
    return True


def reload_text(path):
    # the text as the file now is, without Blender asking which version to keep
    old = text_for(path, load=False)
    new = bpy.data.texts.load(path)
    if old is not None:
        for _, a in _editors():
            if a.spaces.active.text == old:
                a.spaces.active.text = new
        name = old.name
        bpy.data.texts.remove(old)
        new.name = name
    return new


def save_text(text):
    path = bpy.path.abspath(text.filepath)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text.as_string())
    return reload_text(path)


def unsaved(s):
    t = text_for(paths.asset_lua(s), load=False) if paths.asset_lua(s) else None
    return t is not None and t.is_dirty


def read_script(s):
    p = paths.asset_lua(s)
    if not p:
        return None
    t = text_for(p, load=False)
    if t is not None:
        return t.as_string()
    try:
        with open(p, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return None


# ---- what the script refers to

REF_KEYS = ("at", "collider", "tank", "filler", "switch", "enter", "exit", "sit", "hitch", "joint", "model", "parent",
            "handle", "horn_at", "engine", "seat", "mount", "headlights", "handbrake")
CREATES = ("wheel", "free_wheel", "flywheel", "toolset", "coupling", "belt", "pedal", "repairable", "tooltip")
CREATED_BY_PARTS = {"root", "RSound", "EngineSound", "hitch", "hitch_raycast", "Ignition Key"}


def _code(line):
    # the line without its comment (a "--" outside quotes)
    q = None
    for i, ch in enumerate(line):
        if q:
            if ch == q:
                q = None
        elif ch in "\"'":
            q = ch
        elif line.startswith("--", i):
            return line[:i]
    return line


def references(src):
    """[(line, name)] of the objects the script names, and the names it makes itself."""
    from .sigmasrc import made_names
    suffixes = made_names()
    refs, made = [], set(CREATED_BY_PARTS)
    for n, raw in enumerate(src.splitlines(), 1):
        line = _code(raw)
        for m in re.finditer(r'\b(\w+)\s*=\s*"([^"]+)"', line):   # what a part makes out of an option's value
            made.update(m.group(2) + sfx for sfx in suffixes.get(m.group(1), ()))
        for m in re.finditer(r'parts\.\w+\s*\(\s*g\s*,\s*"([^"]+)"', line):
            made.update(m.group(1) + sfx for sfx in suffixes.get("name", ()))
        for m in re.finditer(r'g\.nodes\[\s*"([^"]+)"\s*\]\s*=|g\.nodes\.(\w+)\s*=\s*\{', line):
            made.add(m.group(1) or m.group(2))
        for m in re.finditer(r'parts\.(\w+)\s*\(\s*g\s*,\s*"([^"]+)"', line):
            if m.group(1) in CREATES:
                made.add(m.group(2))
        for m in re.finditer(r'\b(lamp|pivot|hub|rim|hose|key|spin)\s*=\s*"([^"]+)"', line):
            made.add(m.group(2))
        for m in re.finditer(r'\b(' + "|".join(REF_KEYS) + r')\s*=\s*"([^"]+)"', line):
            refs.append((n, m.group(2)))
        for m in re.finditer(r'parts\.(colliders|lock_volumes)\s*\(\s*g\s*,([^)]*)\)', line):
            refs += [(n, s) for s in re.findall(r'"([^"]+)"', m.group(2))]
        for m in re.finditer(r'\b(colliders|collider_groups)\s*=\s*\{([^}]*)\}', line):
            refs += [(n, s) for s in re.findall(r'"([^"]+)"', m.group(2))]
        for m in re.finditer(r'parts\.(mount|slot)\s*\(\s*g\s*,\s*"([^"]+)"', line):
            if re.search(r'\bat\s*=', line):   # made at a marker: the name is new
                made.add(m.group(2))
            else:
                refs.append((n, m.group(2)))
    return [(n, r) for n, r in refs if ":" not in r and "/" not in r], made


def missing_names(src, scene):
    names = {o.name for o in scene.objects}
    refs, made = references(src)
    return [(n, r) for n, r in refs if r not in names and r not in made]


def template_ids(src):
    return re.findall(r'^\s*template\s*"([^"]+)"', src, re.M)


# ---- parts added for the selected objects, from SigmaMod's own: the catalog is parts.lua's, the code a call its
# shipped scripts make, its object names swapped for the selected objects

def _q(s):
    # a Lua string: backslashes and quotes escaped, so any name is safe in a script
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'


SKIP = {"chassis", "item", "numbered", "add", "pose"}   # the script's start, whole templates, and helpers


def part_items(self, context):
    from . import sigmasrc
    cat = [p for p in sigmasrc.parts_catalog() if p.name not in SKIP]
    part_items.cache = [(p.name, f"{p.name}: {p.summary}", p.doc[:400]) for p in cat] or [("", "(SigmaMod's parts.lua not found)", "")]
    return part_items.cache


def part_code(name, sel, act):
    from . import sigmasrc
    objs = [act] + [o for o in sel if o != act]
    ex = sigmasrc.examples().get(name)
    if not ex:
        part = next((p for p in sigmasrc.parts_catalog() if p.name == name), None)
        args = ["g"] + [_q(objs[0].name) if p == "name" else '"?"' if p == "accepts" else "{ }" for p in (part.params[1:] if part else [])]
        return f"parts.{name}(" + ", ".join(args) + f")   -- see parts.lua for what goes in it"
    code, names = ex[0]
    used = sigmasrc.names_in(code, names)
    mapping = {old: o.name for old, o in zip(used, objs)}
    code = sigmasrc.rename(code, mapping)
    left = [n for n in used if n not in mapping]
    if left:
        code += "   -- still the example's names, make them yours: " + ", ".join(left)
    return code


def car_template_at(lines, asset_id):
    """The line the car's own template is made on: new code goes before it. Not a part's (they come after it, in
    "-- ### Part:" blocks)."""
    if asset_id:
        for i, l in enumerate(lines):
            if re.match(r'\s*template\s*"' + re.escape(asset_id) + '"', l):
                return i
    first_part = next((i for i, l in enumerate(lines) if l.startswith("-- ### Part: ")), len(lines))
    tmpl = [i for i, l in enumerate(lines[:first_part]) if re.match(r'\s*template\s*"', l)]
    return tmpl[-1] if tmpl else first_part


def code_end(lines, asset_id):
    """Where new code goes: before the add-on's Tuning block (which has to come after every wheel), else before the
    car's template line."""
    at = car_template_at(lines, asset_id)
    tune = next((i for i, l in enumerate(lines[:at]) if l == "-- ## Tuning"), None)
    return tune if tune is not None else at


def insert(src, code, asset_id=None, end=False):
    """code into the script before the car's template line: before the Tuning block too, unless it is that block (end)."""
    lines = src.split("\n")
    at = car_template_at(lines, asset_id) if end else code_end(lines, asset_id)
    before = [""] if at > 0 and lines[at - 1].strip() else []   # never against a kit block: it ends at a blank line
    return "\n".join(lines[:at] + before + code.split("\n") + [""] + lines[at:])


class SIGMA_OT_make_script(bpy.types.Operator):
    """Write a starting script for this asset next to where it is exported, and open it"""
    bl_idname = "sigma.make_script"
    bl_label = "Make the script"

    kind: bpy.props.EnumProperty(name="What is it?", items=KINDS,
                                 description="What kind of asset the script is for: each starts with what that kind needs")

    @classmethod
    def poll(cls, context):
        s = context.scene.sigma
        return s.root is not None and bool(paths.asset_lua(s))

    def invoke(self, context, event):
        self.kind = context.scene.sigma.kind
        return context.window_manager.invoke_props_dialog(self, width=420)

    def draw(self, context):
        self.layout.label(text="What are you making?")
        self.layout.prop(self, "kind", expand=True)

    def execute(self, context):
        s = context.scene.sigma
        s.kind = self.kind
        path = paths.asset_lua(s)
        if os.path.exists(path):
            self.report({'WARNING'}, f"{os.path.basename(path)} already exists: opened it instead of writing over it")
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(starting_script(self.kind, s.asset_id, s.display_name or s.asset_id, s.root))
        show_text(context, reload_text(path))
        return {'FINISHED'}


class SIGMA_OT_open_script(bpy.types.Operator):
    """Open the asset's script in Blender's text editor (Text > Save, or Alt+S, saves it)"""
    bl_idname = "sigma.open_script"
    bl_label = "Open the script"

    @classmethod
    def poll(cls, context):
        p = paths.asset_lua(context.scene.sigma)
        return bool(p) and os.path.isfile(p)

    def execute(self, context):
        p = paths.asset_lua(context.scene.sigma)
        show_text(context, text_for(p) or reload_text(p))
        return {'FINISHED'}


class SIGMA_OT_save_script(bpy.types.Operator):
    """Save the changes made to the script in Blender"""
    bl_idname = "sigma.save_script"
    bl_label = "Save the script"

    @classmethod
    def poll(cls, context):
        return unsaved(context.scene.sigma)

    def execute(self, context):
        save_text(text_for(paths.asset_lua(context.scene.sigma), load=False))
        return {'FINISHED'}


class SIGMA_OT_add_part(bpy.types.Operator):
    """Add a part to the script, made from the selected objects (the names go in for you). The list is SigmaMod's
parts library; the code, how SigmaMod's own assets use each part"""
    bl_idname = "sigma.add_part"
    bl_label = "Add a part"
    bl_property = "part"

    part: bpy.props.EnumProperty(name="Part", items=part_items,
                                 description="One of SigmaMod's parts library: written into your script with the selected "
                                             "objects' names")

    @classmethod
    def poll(cls, context):
        p = paths.asset_lua(context.scene.sigma)
        return bool(p) and os.path.isfile(p) and context.active_object is not None

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        s = context.scene.sigma
        sel = [o for o in context.selected_objects] or [context.active_object]
        act = context.active_object if context.active_object in sel else sel[0]
        code = part_code(self.part, sel, act)
        t = text_for(paths.asset_lua(s))
        if t.is_dirty:
            t = save_text(t)
        with open(paths.asset_lua(s), "w", encoding="utf-8", newline="\n") as f:
            f.write(insert(t.as_string(), code, s.asset_id))
        show_text(context, reload_text(paths.asset_lua(s)))
        self.report({'INFO'}, "Added: " + code.split("\n")[0])
        return {'FINISHED'}


classes = (SIGMA_OT_make_script, SIGMA_OT_open_script, SIGMA_OT_save_script, SIGMA_OT_add_part)
