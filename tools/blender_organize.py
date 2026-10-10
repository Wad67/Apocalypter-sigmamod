# Sorts an imported SigmaMod template into collections so the model can be inspected:
#   Colliders  - nodes with custom property sigma = "collider"            (hidden)
#   Inactive   - objects inactive in game (active = false) and their children (hidden)
#   Far        - objects more than FAR metres from their root, e.g. live audio sources or cargo
#                locked to the car (hidden)
#   Undrawn    - meshes whose renderer is disabled in game (rendererEnabled = false). A fitted part
#                hides its own renderer and the vehicle draws it through its own slot (wheel_hub,
#                windshield_model/...), so without this the part shows twice (hidden)
# Parenting is untouched and glTF export ignores collections, so a re-export is unchanged
# (leave "Include > Visible Objects" off so the hidden ones are exported too).
#
# In Blender: Scripting workspace > Open this file > Run Script (works on the current scene).
# Headless:   blender -b --factory-startup -P tools/blender_organize.py -- in.glb out.blend
import sys

import bpy

FAR = 50.0
GROUPS = ("Colliders", "Inactive", "Far", "Undrawn")


def ancestors(o):
    p = o.parent
    while p:
        yield p
        p = p.parent


def root_of(o):
    r = o
    for p in ancestors(o):
        r = p
    return r


def organize(scene):
    cols = {}
    for name in GROUPS:
        c = bpy.data.collections.get(name)
        if c is None:
            c = bpy.data.collections.new(name)
        if c.name not in scene.collection.children:
            scene.collection.children.link(c)
        cols[name] = c

    counts = dict.fromkeys(GROUPS, 0)
    for o in scene.objects:
        if o.get("sigma") == "collider":
            group = "Colliders"
        elif o.get("active") is False or any(p.get("active") is False for p in ancestors(o)):
            group = "Inactive"
        elif (o.matrix_world.translation - root_of(o).matrix_world.translation).length > FAR:
            group = "Far"
        elif o.get("rendererEnabled") is False:
            group = "Undrawn"
        else:
            continue
        for c in list(o.users_collection):
            if c is not cols[group]:
                c.objects.unlink(o)
        if o.name not in cols[group].objects:
            cols[group].objects.link(o)
        counts[group] += 1

    # far objects take their children with them
    for o in list(cols["Far"].objects):
        for ch in o.children_recursive:
            if ch.get("sigma") == "collider" or ch.name in cols["Far"].objects or ch.name in cols["Inactive"].objects:
                continue
            for c in list(ch.users_collection):
                c.objects.unlink(ch)
            cols["Far"].objects.link(ch)
            counts["Far"] += 1

    layer = bpy.context.view_layer.layer_collection
    for name in GROUPS:
        lc = layer.children.get(cols[name].name)
        if lc is not None:
            lc.hide_viewport = True
    print("organized:", ", ".join(f"{k} {v}" for k, v in counts.items()))


if "--" in sys.argv:
    src, dst = sys.argv[sys.argv.index("--") + 1:][:2]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=src)
    organize(bpy.context.scene)
    bpy.ops.wm.save_as_mainfile(filepath=dst)
else:
    organize(bpy.context.scene)
