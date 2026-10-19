# An item that bolts onto vehicles (parts.attachable): what it's made of and how it behaves, set in the Build step and
# written into the script's parts.attachable call. The values start as the script's call has them; the call is
# written again only once something here is changed, so a script nobody touched here stays as it is.
import re

import bpy

from . import paths, script, sigmasrc

# the game's layers that make sense for it (from the game's own layer list and what its grab and tools look for)
LOOSE = [
    ('Item', "Item", "What loose things are on: the player can pick it up, look at it, and it bumps into things"),
    ('Default', "Default", "The world's layer: it can't be picked up (the grab only finds Item)"),
    ('Car', "Car", "A vehicle's layer: it can't be picked up (the grab only finds Item)"),
]
FITTED = [
    ('Car', "Car", "As the game's fitted parts: part of the vehicle, its name shown, taken off as set below"),
    ('Item', "Item", "Collides as a loose thing would, its name shown, taken off as set below"),
    ('SeeTrough', "SeeTrough", "Can be taken off, but its name doesn't show when looked at"),
    ('Default', "Default", "Collides as the world does. The tools don't see it: it can never be taken off again"),
    ('Ignore Raycast', "Ignore Raycast", "Nothing looks at it: it can never be taken off again, and its name never shows"),
]
LISTS = (
    ("solid", "Seen and solid", "Meshes you see and bump into: each gets a convex collider of its own shape", 'MESH_DATA'),
    ("colliders", "Collider only", "Meshes that are only colliders (not drawn): simpler shapes round what you see", 'MOD_WIREFRAME'),
    ("collider_groups", "Collider groups", "Objects whose children are its colliders already (an export's \"colliders\")", 'OUTLINER_OB_EMPTY'),
    ("fitted_off", "Hidden when fitted", "Switched off while it's fitted, back on when it comes off: a handle, packaging, "
                                       "a collider that would get in the way on the car", 'HIDE_ON'),
    ("fitted_on", "Shown only when fitted", "Switched on only while it's fitted: bolts, a strap, a bracket", 'HIDE_OFF'),
    ("lock_volumes", "Lock volumes", "Boxes that loose things inside ride along in (a crate, a roof basket): their "
                                     "mesh's box is the space", 'MOD_PHYSICS'),
)


# what an object is to it: one of these each (they make different colliders of it); and what the grab switches on and
# off while it's loose can't also be shown only when fitted
EXCLUSIVE = ("solid", "colliders", "collider_groups", "lock_volumes", "zones")
HELD = ("solid", "colliders", "collider_groups")


def _touch(self, context):
    a = context.scene.sigma.attach
    a.touched = True


class SigmaObjRef(bpy.types.PropertyGroup):
    obj: bpy.props.PointerProperty(type=bpy.types.Object, update=_touch, description="The object")


class SigmaZone(bpy.types.PropertyGroup):
    obj: bpy.props.PointerProperty(type=bpy.types.Object, update=_touch,
                                   description="The zone: its mesh's shape is where it hurts (not drawn)")
    by_speed: bpy.props.BoolProperty(name="By speed", update=_touch,
                                     description="Hurts by how fast the vehicle goes, as a car's bumper does (over 5 m/s). "
                                                 "Off: a fixed hit and a second's rest, as the game's spikes")
    amount: bpy.props.FloatProperty(name="Damage", min=0, max=1000, default=4, update=_touch,
                                    description="By speed: damage per m/s (a car's bumper: 3). Fixed: per hit (the spikes: 4)")


class SigmaAttachable(bpy.types.PropertyGroup):
    loaded: bpy.props.BoolProperty()
    source: bpy.props.StringProperty(description="The script the settings were read from")
    touched: bpy.props.BoolProperty(description="Changed here since the script was last written")
    mass: bpy.props.FloatProperty(name="Mass", min=0.1, max=10000, default=10, update=_touch,
                                  description="What it weighs, in kg (loose; fitted, it adds to the vehicle)")
    tools: bpy.props.BoolProperty(name="Needs the toolbox to come off", update=_touch,
                                  description="Off: it comes off by hand, as the game's metal plates do. On: only with the "
                                              "toolbox, as the game's car parts (it gets another item ID than "
                                              "\"attachable\", which is all the game's by-hand removal looks for)")
    item_id: bpy.props.StringProperty(name="Item ID", update=_touch,
                                      description="The game's item ID. Empty: \"attachable\" (by hand) or "
                                                  "\"attachable_bolted\" (toolbox). Only \"attachable\" comes off by hand")
    layer_loose: bpy.props.EnumProperty(name="Loose", items=LOOSE, update=_touch,
                                        description="Its layer (and its colliders') while it's loose")
    layer_fitted: bpy.props.EnumProperty(name="Fitted", items=FITTED, update=_touch,
                                         description="Its layer (and its colliders') while it's fitted to a vehicle")
    layers_set: bpy.props.BoolProperty(description="The script sets its layers")
    grip: bpy.props.FloatProperty(name="Grip radius", min=0.01, max=2, default=0.1, update=_touch,
                                  description="With colliders of its own: the small solid sphere at its origin the game "
                                              "holds it by (made a trigger while held)")
    solid: bpy.props.CollectionProperty(type=SigmaObjRef)
    colliders: bpy.props.CollectionProperty(type=SigmaObjRef)
    collider_groups: bpy.props.CollectionProperty(type=SigmaObjRef)
    fitted_off: bpy.props.CollectionProperty(type=SigmaObjRef)
    fitted_on: bpy.props.CollectionProperty(type=SigmaObjRef)
    lock_volumes: bpy.props.CollectionProperty(type=SigmaObjRef)
    zones: bpy.props.CollectionProperty(type=SigmaZone)


# ---- the call in the script

def call_span(src):
    """(start, end) of the parts.attachable(...) call in the script text, or None."""
    m = re.search(r"parts\.attachable\s*\(", src or "")
    if not m:
        return None
    depth, i = 0, m.end() - 1
    while i < len(src):
        c = src[i]
        if c == '"' or c == "'":
            j = i + 1
            while j < len(src) and src[j] != c:
                j += 2 if src[j] == "\\" else 1
            i = j
        elif c in "({[":
            depth += 1
        elif c in ")}]":
            depth -= 1
            if depth == 0:
                return m.start(), i + 1
        elif c == "-" and src[i:i + 2] == "--":
            while i < len(src) and src[i] != "\n":
                i += 1
        i += 1
    return None


STR = r'"((?:[^"\\]|\\.)*)"'


def _list(body, key):
    m = re.search(r"\b" + key + r"\s*=\s*\{", body)
    if not m:
        return ""
    depth, i = 1, m.end()
    while i < len(body) and depth:
        depth += {"{": 1, "}": -1}.get(body[i], 0)
        i += 1
    return body[m.end():i - 1]


def read(src):
    sp = call_span(src)
    if not sp:
        return None
    body = src[sp[0]:sp[1]]
    out = {}
    m = re.search(r"\bmass\s*=\s*(-?[\d.]+)", body)
    out["mass"] = float(m.group(1)) if m else 10.0
    out["tools"] = bool(re.search(r"\btools\s*=\s*true", body))
    m = re.search(r"\bid\s*=\s*" + STR, body)
    out["id"] = m.group(1) if m else ""
    for key, _, _, _ in LISTS:
        out[key] = re.findall(STR, _list(body, key))
    zones = []
    zl = _list(body, "damage_zones")
    for entry in re.finditer(r"\{([^{}]*)\}|" + STR, zl):
        if entry.group(1) is not None:
            name = re.search(STR, entry.group(1))
            ps = re.search(r"per_speed\s*=\s*([\d.]+)", entry.group(1))
            dm = re.search(r"\bdamage\s*=\s*([\d.]+)", entry.group(1))
            if name:
                zones.append((name.group(1), bool(ps), float(ps.group(1)) if ps else float(dm.group(1)) if dm else 4.0))
        else:
            zones.append((entry.group(2), False, 4.0))
    out["zones"] = zones
    ly = _list(body, "layers")
    out["layers_set"] = bool(ly)
    for k in ("loose", "fitted"):
        m = re.search(r"\b" + k + r"\s*=\s*" + STR, ly)
        out["layer_" + k] = m.group(1) if m else None
    m = re.search(r"\bgrip\s*=\s*\{[^}]*radius\s*=\s*([\d.]+)", body)
    out["grip"] = float(m.group(1)) if m else 0.1
    return out


def current(context):
    s = context.scene.sigma
    return s.attach.loaded and s.attach.source == (paths.asset_lua(s) or "")


def load(context, force=False):
    s = context.scene.sigma
    a = s.attach
    if current(context) and not force:
        return
    v = read(script.read_script(s) or "") or {}
    a.mass = v.get("mass", 10.0)
    a.tools = v.get("tools", False)
    a.item_id = v.get("id", "")
    for key, _, _, _ in LISTS:
        coll = getattr(a, key)
        coll.clear()
        for n in v.get(key, []):
            coll.add().obj = bpy.data.objects.get(n)
    a.zones.clear()
    for n, by_speed, amount in v.get("zones", []):
        z = a.zones.add()
        z.obj, z.by_speed, z.amount = bpy.data.objects.get(n), by_speed, amount
    a.layers_set = v.get("layers_set", False)
    a.layer_loose = v.get("layer_loose") if v.get("layer_loose") in {x[0] for x in LOOSE} else 'Item'
    a.layer_fitted = v.get("layer_fitted") if v.get("layer_fitted") in {x[0] for x in FITTED} else 'Car'
    a.grip = v.get("grip", 0.1)
    a.loaded, a.touched, a.source = True, False, paths.asset_lua(s) or ""


def _names(coll):
    return [r.obj.name for r in coll if r.obj is not None]


def call(context):
    """The parts.attachable call as the settings write it."""
    a = context.scene.sigma.attach
    q = script._q
    lines = [f"  mass = {a.mass:g},   -- kg"]
    if a.tools:
        lines.append("  tools = true,   -- comes off only with the toolbox")
    if a.item_id.strip():
        lines.append(f"  id = {q(a.item_id.strip())},")
    for key, label, _, _ in LISTS:
        names = _names(getattr(a, key))
        if names:
            lines.append(f"  {key} = {{ " + ", ".join(q(n) for n in names) + f" }},   -- {label.lower()}")
    zones = []
    for z in a.zones:
        if z.obj is not None:
            zones.append(f"{{ {q(z.obj.name)}, " + (f"per_speed = {z.amount:g} }}" if z.by_speed else f"damage = {z.amount:g} }}"))
    if zones:
        lines.append("  damage_zones = { " + ", ".join(zones) + " },")
    if a.layers_set or a.layer_loose != 'Item' or a.layer_fitted != 'Car':
        lines.append(f"  layers = {{ loose = {q(a.layer_loose)}, fitted = {q(a.layer_fitted)} }},")
    if abs(a.grip - 0.1) > 1e-6:
        lines.append(f"  grip = {{ radius = {a.grip:g} }},")
    return "parts.attachable(g, {\n" + "\n".join(lines) + "\n})"


def write(context):
    """The call written again, where it is in the script (only once something was changed here)."""
    s = context.scene.sigma
    path = paths.asset_lua(s)
    if s.kind != 'ATTACHABLE' or not path or not current(context) or not s.attach.touched:
        return False
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        return False
    sp = call_span(src)
    new = src[:sp[0]] + call(context) + src[sp[1]:] if sp else script.insert(src, call(context), s.asset_id)
    if new != src:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new)
        script.reload_text(path)
    s.attach.touched = False
    return True


# ---- buttons

class SIGMA_OT_attach_list(bpy.types.Operator):
    """Add the selected objects to this list, or take one off it"""
    bl_idname = "sigma.attach_list"
    bl_label = "List"
    bl_options = {'REGISTER', 'UNDO'}

    key: bpy.props.StringProperty(options={'HIDDEN'})
    remove: bpy.props.IntProperty(default=-1, options={'HIDDEN', 'SKIP_SAVE'})

    @classmethod
    def description(cls, context, props):
        label = next((l for k, l, _, _ in LISTS if k == props.key), "damage zones" if props.key == "zones" else props.key)
        return f"Take it off \"{label}\" (the object stays)" if props.remove >= 0 else \
            f"Add the selected objects to \"{label}\""

    def execute(self, context):
        a = context.scene.sigma.attach
        coll = getattr(a, self.key)
        if self.remove >= 0:
            coll.remove(self.remove)
        else:
            have = {r.obj for r in coll}
            new = [o for o in context.selected_objects if o not in have and o != context.scene.sigma.root]
            others = [k for k in EXCLUSIVE if k != self.key] if self.key in EXCLUSIVE else []
            others += {"fitted_on": list(HELD)}.get(self.key, []) + (["fitted_on"] if self.key in HELD else [])
            for k in others:   # an object is one thing to it: off the lists it can't be on with this one
                c = getattr(a, k)
                for i in range(len(c) - 1, -1, -1):
                    if c[i].obj in new:
                        c.remove(i)
            for o in new:
                coll.add().obj = o
        a.touched = True
        return {'FINISHED'}


class SIGMA_OT_attach_write(bpy.types.Operator):
    """Write these settings into the item's script now (export does it too)"""
    bl_idname = "sigma.attach_write"
    bl_label = "Write it to the script"

    def execute(self, context):
        if not write(context):
            self.report({'INFO'}, "Nothing changed here, or no script yet (step 6 makes it)")
            return {'CANCELLED'}
        return {'FINISHED'}


class SIGMA_OT_attach_read(bpy.types.Operator):
    """Read the settings again from the item's script, dropping changes not written yet"""
    bl_idname = "sigma.attach_read"
    bl_label = "Read the item's settings"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        load(context, force=True)
        return {'FINISHED'}


def problems(context):
    """[(level, text)] about the settings, for the panel and Clunky."""
    a = context.scene.sigma.attach
    out = []
    if a.layer_loose != 'Item':
        out.append(('WARNING', f"Loose on {a.layer_loose}, it can't be picked up: the game's grab only finds Item."))
    if a.layer_fitted in ('Default', 'Ignore Raycast'):
        out.append(('WARNING', f"Fitted on {a.layer_fitted}, it can never be taken off again: the tools don't see it."))
    if a.tools and a.item_id.strip() == "attachable":
        out.append(('WARNING', "Item ID \"attachable\" always comes off by hand: clear it, or give another, for the toolbox."))
    seen = {}
    for key in EXCLUSIVE:
        for r in getattr(a, key):
            if r.obj is not None:
                seen.setdefault(r.obj.name, []).append(key)
    for r in a.fitted_on:
        if r.obj is not None and any(k in HELD for k in seen.get(r.obj.name, [])):
            seen.setdefault(r.obj.name, []).append("fitted_on")
    twice = [n for n, ks in seen.items() if len(ks) > 1]
    if twice:
        out.append(('WARNING', f"'{twice[0]}' is in more than one list that makes it a collider (or shows it only fitted): "
                               "one each. Adding it to a list takes it off the others."))
    objs = [r.obj for key, _, _, _ in LISTS for r in getattr(a, key)]
    if any(o is None for o in objs) or any(z.obj is None for z in a.zones):
        out.append(('WARNING', "A list has an empty or deleted object: pick it again or take it off."))
    return out


def draw(l, c, wrap):
    s = c.scene.sigma
    a = s.attach
    if not current(c):
        wrap(l, c, "The item's settings start from what its script says.", boxes=2)
        l.operator("sigma.attach_read", icon='IMPORT')
        return
    col = l.column(align=True)
    col.prop(a, "mass")
    col.prop(a, "tools")
    if a.tools:
        col.prop(a, "item_id")
    for key, label, help_, icon in LISTS:
        b = l.box().column(align=True)
        row = b.row(align=True)
        row.label(text=label, icon=icon)
        row.operator("sigma.attach_list", text="", icon='ADD').key = key
        wrap(b, c, help_, boxes=3)
        for i, r in enumerate(getattr(a, key)):
            row = b.row(align=True)
            row.prop(r, "obj", text="")
            op = row.operator("sigma.attach_list", text="", icon='X')
            op.key, op.remove = key, i
    b = l.box().column(align=True)
    row = b.row(align=True)
    row.label(text="Damage zones", icon='FORCE_FORCE')
    row.operator("sigma.attach_list", text="", icon='ADD').key = "zones"
    wrap(b, c, "Meshes whose shape hurts the NPCs and animals they hit once it's fitted to a vehicle (not drawn).", boxes=3)
    for i, z in enumerate(a.zones):
        row = b.row(align=True)
        row.prop(z, "obj", text="")
        row.prop(z, "by_speed", toggle=True, icon='SORTTIME')
        row.prop(z, "amount", text="")
        op = row.operator("sigma.attach_list", text="", icon='X')
        op.key, op.remove = "zones", i
    b = l.box().column(align=True)
    b.label(text="Layers", icon='RENDERLAYERS')
    b.prop(a, "layer_loose")
    b.prop(a, "layer_fitted")
    if a.solid or a.colliders or a.collider_groups:
        l.prop(a, "grip")
    for level, text in problems(c):
        wrap(l, c, text, icon='ERROR', boxes=2)
    row = l.row(align=True)
    row.operator("sigma.attach_write", icon='FILE_REFRESH' if a.touched else 'CHECKMARK',
                 text="Write it to the script" if a.touched else "In the script")
    row.operator("sigma.attach_read", text="", icon='LOOP_BACK')


classes = (SIGMA_OT_attach_list, SIGMA_OT_attach_write, SIGMA_OT_attach_read)
props = (SigmaObjRef, SigmaZone, SigmaAttachable)
