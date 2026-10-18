# What would go wrong in game, found before it does: each problem in Clunky's words, how bad it is, and the button that
# fixes it when there is one. ERROR stops the export; WARNING and INFO don't. Run every couple of seconds while
# Blender is idle (mascot.py), so the hints follow what the artist is doing.
import os
import re

import bpy

from . import assets, gamedata, jobs, paths, script

ERROR, WARNING, INFO = 'ERROR', 'WARNING', 'INFO'
ID_RE = re.compile(r"^[a-z0-9_]+$")
GAME_IDS = ("es3local", "es3prefab")
SCRATCH = ('CAR', 'ATTACHABLE')


class Problem:
    def __init__(self, level, text, fix=None, fix_text="", fix_args=None, key=None, also=None):
        self.level, self.text, self.fix, self.fix_text, self.fix_args = level, text, fix, fix_text, fix_args or {}
        self.key = key or text[:60]
        self.also = also or []   # more buttons: (operator, text, args)


def asset_objects(s):
    return [s.root] + list(s.root.children_recursive) if s.root else []


def image_bytes(img):
    if img.packed_file:
        return img.packed_file.size
    try:
        return os.path.getsize(bpy.path.abspath(img.filepath))
    except OSError:
        return 0


def material_images(mat):
    if not mat.node_tree:
        return set()
    return {n.image for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}


def _names(objs, n=3):
    return ", ".join(f"'{o.name}'" for o in objs[:n]) + (f" and {len(objs) - n} more" if len(objs) > n else "")


def run(context):
    s = context.scene.sigma
    out = []

    def add(*a, **k):
        out.append(Problem(*a, **k))

    if not paths.game_dir():
        add(ERROR, "I can't find Apocalypter. Did you install it, or just think about it really hard? "
                   "Step 1: find it, or pick its folder.", "sigma.find_game", "Find the game", key="nogame")
        return out
    if not paths.sigmamod_installed():
        add(ERROR, "SigmaMod isn't in the game. I'm a SigmaMod helper. Without SigmaMod I'm just a paperclip. "
                   "Unzip it into Apocalypter/BepInEx/plugins.", key="nosigmamod")
    if s.root is None:
        add(ERROR, "It looks like you're trying to make an asset! Would you like to start one? Step 2: import a game "
                   "object, start from scratch, or pick your object.", key="noroot")
        return out
    if s.root.name not in context.scene.objects:
        add(ERROR, f"Your asset's top object '{s.root.name}' left this scene. Bring it back, or pick another.",
            "sigma.use_active_as_root", "Use the selected object", key="rootgone")
    if not s.asset_id:
        add(ERROR, "Your asset has no id. The game can't load 'that thing I made'. Step 3: name it, like my_truck.", key="noid")
    elif not ID_RE.match(s.asset_id):
        add(ERROR, f"'{s.asset_id}' isn't an id. Lowercase letters, digits and _ only.", key="badid")
    if not (s.mod_folder or "").strip() or re.search(r'[\\/:*?"<>|]', s.mod_folder):
        add(ERROR, "That mod folder name has characters no folder can have. Or nothing at all.", key="badfolder")
    elif s.mod_folder.strip().lower() == "sigmamod":
        add(WARNING, "You're saving into SigmaMod's own folder. The next SigmaMod update deletes it along with your "
                     "afternoon. Use a folder of your own.", key="sigmafolder")

    objs = asset_objects(s)
    kind = s.kind

    # game ids: what makes SigmaMod build an asset as a changed copy of a game object
    with_ids = [o for o in objs if any(o.get(k) for k in GAME_IDS)]
    if with_ids:
        owners = gamedata.id_owners()
        donors = {}
        for o in with_ids:
            for k in GAME_IDS:
                if o.get(k):
                    donors.setdefault(owners.get(str(o[k]), "a game object"), []).append(o)
        names = sorted(donors)
        if len(names) > 1:
            add(ERROR, f"Frankenstein alert: your asset has objects from {' AND '.join(names)}. SigmaMod takes one game "
                       f"object to change, not a buffet. {_names(donors[names[-1]])} came from {names[-1]}.",
                "sigma.strip_game_ids", "Make them plain objects", {"names": "\n".join(o.name for o in donors[names[-1]])},
                key="frankenstein")
        elif kind in SCRATCH:
            donor = names[0]
            add(WARNING, f"{_names(with_ids)} still carr{'ies' if len(with_ids) == 1 else 'y'} the game's ids from "
                         f"{donor}. Are you sure you want your {'car' if kind == 'CAR' else 'item'} to be a clone of the "
                         f"{donor}? Because that's what SigmaMod will build.",
                "sigma.strip_game_ids", "No, make them plain objects", {"names": "\n".join(o.name for o in with_ids)},
                key="clone:" + donor,
                also=[("sigma.set_kind", f"Yes, it's a changed {donor}", {"kind": 'CHANGED_CAR' if kind == 'CAR' else 'CHANGED'})])
    if kind == 'VARIANT' and s.root.children:
        add(WARNING, f"A copy of a game object has nothing under it, and you've put {len(s.root.children)} things under "
                     "it. That makes it a changed game object.", "sigma.set_kind", "It's a changed game object",
            {"kind": 'CHANGED'}, key="variantkids")
    if kind in ('CHANGED', 'CHANGED_CAR', 'VARIANT') and not with_ids and not s.root.get("es3prefab"):
        add(WARNING, "This is meant to be a changed game object, but nothing in it has the game's ids. So it's built "
                     "from scratch. Did you strip them, or is this something you made?", "sigma.set_kind",
            "It's my own car", {"kind": 'CAR'}, key="changednoids",
            also=[("sigma.set_kind", "It's my own item", {"kind": 'ATTACHABLE'})])

    # the shape of a car
    if kind in ('CAR', 'CHANGED_CAR'):
        from . import kit
        box = kit.body_box(s)
        if box:
            size = box[1] - box[0]
            length = max(size.x, size.y)
            if size.x > size.y * 1.15:
                add(WARNING, f"Your car is {size.x:.1f} m wide and {size.y:.1f} m long. Either it's lying sideways (the "
                             "game's cars face -Y in Blender: look from the front, numpad 1), or it's a very strange car.",
                    key="sideways")
            if length > 15 or length < 0.8:
                add(WARNING, f"Your car is {length:.1f} m long. Blender's units are metres. Just checking.", key="size")
        if s.root.rotation_mode == 'QUATERNION' or any(abs(a) > 1e-4 for a in s.root.rotation_euler):
            add(WARNING, "Your top object is rotated. The car drives along its -Y, rotation or not. Apply the rotation "
                         "(Ctrl+A > Rotation) unless you enjoy crab-walking.", key="rootrot")
        if any(abs(x - 1) > 1e-4 for x in s.root.scale):
            add(WARNING, "Your top object is scaled. Physics hates that. Apply the scale (Ctrl+A > Scale).", key="rootscale")
        if kind == 'CAR':
            _kit_checks(s, add)

    # what's lying around outside the asset
    loose = [o for o in context.scene.objects if o not in objs and o.parent is None and o != s.root
             and (o.name.startswith("hinge_") or o.get("sigma_kit"))]
    if loose:
        one = len(loose) == 1
        add(WARNING, f"{_names(loose)} {'is' if one else 'are'} not under your asset, so as far as the game knows, "
                     f"{'it doesn' if one else 'they don'}'t exist.",
            "sigma.parent_to_root", "Put them under it", {"names": "\n".join(o.name for o in loose)}, key="loose")

    # the objects
    for o in objs:
        if (o.name.startswith("hinge_") or "wheel_hub" in o.name) and o.get("sigma") != "collider" \
                and any(abs(abs(x) - 1) > 1e-4 for x in o.scale):   # mirrored (-1) is fine: the game's right-hand hubs are
            add(WARNING, f"'{o.name}' is scaled ({o.scale.x:.2f}, {o.scale.y:.2f}, {o.scale.z:.2f}). Whatever fits on "
                         "it gets squashed the same. Hinges stay at scale 1.", key="hingescale:" + o.name)
        if o.get("sigma") == "collider" and o.get("convex") and o.type == 'MESH' and len(o.data.polygons) > 255:
            add(WARNING, f"Collider '{o.name}' has {len(o.data.polygons)} faces. The game's limit for one is 255. Make "
                         "colliders from the mesh instead.", key="bigcollider:" + o.name)
    stale = {}
    for o in objs:
        src = o.get("sigma_from")
        if o.get("generated") and src and src in bpy.data.objects:
            from .colliders import shape_key
            if o.get("sigma_shape") != shape_key(bpy.data.objects[src]):
                stale[src] = True
    for src in stale:
        add(WARNING, f"You changed '{src}' since I made its colliders. They're the old shape now. Collisions will be "
                     "wherever the old one was.", "sigma.make_colliders", "Make them again", {"target": src},
            key="stale:" + src)
    excluded = {lc.collection for lc in _layer_collections(context.view_layer.layer_collection) if lc.exclude}
    lost = [o for o in objs if o.users_collection and all(c in excluded for c in o.users_collection)]
    if lost:
        add(WARNING, f"{_names(lost)} {'is' if len(lost) == 1 else 'are'} in collections you unticked. Unticked means "
                     "not exported. Not exported means not in the game.", key="excluded")
    others = [o for o in context.scene.objects if o.parent is None and o != s.root and o not in loose
              and (o.type == 'MESH' or o.children)]
    if others:
        add(INFO, f"{_names(others)} {'is' if len(others) == 1 else 'are'} exported too. The game ignores "
                  f"{'it' if len(others) == 1 else 'them'}, but the file gets bigger.", key="others")
    if kind in ('CAR',) and not any(o.get("sigma") == "collider" for o in objs) and "colliders" not in (script.read_script(s) or ""):
        add(WARNING, "Your car has no colliders. It will fall through the planet. Majestically. Step 5.", key="nocolliders")

    # the materials
    lost_ids = assets.lost_ids(context)
    if lost_ids:
        add(WARNING, f"{_names([m for m, _ in lost_ids])} {'is' if len(lost_ids) == 1 else 'are'} the game's, but lost "
                     "the game's id somewhere (appending does that). So the textures go into your file. All "
                     f"{len(lost_ids)} of them.", "sigma.restore_material_ids", "Use the game's", key="lostids",
            also=[("sigma.keep_own_material", f"'{m.name}' is mine", {"material": m.name}) for m, _ in lost_ids[:2]])
    used = {m for o in objs if o.type == 'MESH' for m in o.data.materials if m}
    inherited = sorted((m for m in used if m.get("es3global")), key=lambda m: m.name)
    if inherited:
        owners = gamedata.game_materials()
        m = inherited[0]
        src = owners.get(gamedata.base_name(m.name), (None, ""))[1]
        whence = f" (from {os.path.basename(src)[:-4]})" if src else ""
        add(INFO, f"'{m.name}' is inherited from the game{whence}"
                  + (f", like {len(inherited) - 1} other material{'s' if len(inherited) > 2 else ''}" if len(inherited) > 1 else "")
                  + ". Edit it all you like in Blender: the game uses its own. Want your edits? Make it yours (its "
                    "textures then go into your file).",
            "sigma.make_mine", "Make it mine", {"material": m.name}, key="inherited:" + m.name)
    lost_set = {m for m, _ in lost_ids}
    own = [m for m in used if not m.get("es3global") and m not in lost_set]   # the lost ones have a hint of their own
    size = sum(image_bytes(i) for i in {i for m in own for i in material_images(m)})
    if size > 50e6:
        add(WARNING, f"Your textures weigh {size / 1e6:.0f} MB. This is a car, not a texture pack. 2048 px is plenty.",
            key="bigtex")
    missing = [i.name for m in own for i in material_images(m) if not i.packed_file and i.source == 'FILE'
               and not os.path.isfile(bpy.path.abspath(i.filepath))]
    if missing:
        add(WARNING, f"Texture '{missing[0]}' points at a file that isn't there. It exports as nothing.", key="missingtex")

    # the script
    src = script.read_script(s)
    if src is None:
        add(ERROR, "No script yet. The model is the body; the script is what makes it do anything. Step 6.",
            "sigma.make_script", "Make the script", key="noscript")
    else:
        if script.unsaved(s):
            add(WARNING, "The script has unsaved changes. The game reads the file, not your hopes.",
                "sigma.save_script", "Save the script", key="scriptdirty")
        ids = script.template_ids(src)
        if not ids:
            add(ERROR, 'The script has no  template "..." (g)  line. It describes a car and then never makes it.', key="notemplate")
        elif s.asset_id and s.asset_id not in ids:
            add(WARNING, f"The script makes '{', '.join(ids)}', but the asset is called '{s.asset_id}'. Pick one.", key="idmismatch")
        miss = script.missing_names(src, context.scene)
        if miss:
            line, name = miss[0]
            add(WARNING, f"Script line {line} talks about '{name}'. There's nothing called that. Capitals and spaces "
                         "matter, and so do typos." + (f" ({len(miss) - 1} more like it.)" if len(miss) > 1 else ""),
                "sigma.open_script", "Open the script", key="missing:" + name)
        m = re.search(r'\bmodel\s*=\s*"([^"]+)"', src)
        if m and m.group(1) != s.root.name and m.group(1) in context.scene.objects:
            add(WARNING, f"The script says the asset is '{m.group(1)}', the panel says '{s.root.name}'. One of you is wrong.",
                key="modelmismatch")
        if not m and len([o for o in context.scene.objects if o.parent is None]) > 1 and s.root.name not in ids:
            add(WARNING, f'There\'s more than one top object and the script doesn\'t say which is the asset. Add  '
                         f'g.model = "{s.root.name}"', key="nomodel")

    if not bpy.data.is_saved:   # last: everything else is more interesting
        add(WARNING, "This .blend has never been saved. Blender crashes. So do artists. Save it (Ctrl+S).",
            "wm.save_mainfile", "Save", key="unsaved")
    if not jobs.tools_ready():
        add(INFO, "The helper tools aren't installed. Colliders from meshes and the game's real meshes need them. "
                  "Step 1, one button.", "sigma.install_tools", "Install", key="notools")
    return out


def _kit_checks(s, add):
    from . import kit, sigmasrc
    k = sigmasrc.kit()
    if not k:
        return
    p = kit.picks(s, k)
    src = script.read_script(s) or ""
    objs = set(asset_objects(s))
    for sec in k["sections"]:
        ok, in_script, _ = kit.status(s, sec, p, src)
        unpicked = [r for r in sec.roles if not r.optional and not p.get(r.name)]
        if in_script and unpicked:
            r = unpicked[0]
            add(WARNING, f"The {sec.title.lower()} needs a {r.label.lower()} ({r.help.rstrip('.')}). Nothing's picked for "
                         "it. Pick yours in the Build step, or take the Sigma's.",
                "sigma.bring_role", "Take the Sigma's", {"role": r.name}, key="unpicked:" + r.name,
                also=[("sigma.open_build", "Show me", {"section": sec.title})])
        elif sec.required and not in_script:
            add(WARNING, f"Your car has no {sec.title.lower()}. {sec.desc.split('. ')[0]}. Cars tend to need that.",
                "sigma.add_kit", f"Add the {sec.title.lower()}", {"sections": sec.title}, key="kitmissing:" + sec.title,
                also=[("sigma.open_build", "Show me", {"section": sec.title})])
    for r in s.roles:
        role = kit.role_of(k, r.role)
        if r.obj is None or role is None or role.kind != 'solid' or r.obj.type != 'MESH':
            continue
        made, stale = kit.made_colliders(r.obj)
        if not made:
            add(WARNING, f"'{r.obj.name}' is your {role.label.lower()}: you see it and should bump into it, but it has no "
                         "colliders yet. Without them your hand goes straight through it.",
                "sigma.make_colliders", "Make its colliders", {"target": r.obj.name}, key="solidnocol:" + r.obj.name)
    outside = [r for r in s.roles if r.obj is not None and r.obj not in objs and r.obj.name in bpy.context.scene.objects]
    if outside:
        o = outside[0].obj
        add(WARNING, f"'{o.name}' is picked as a {k['roles'][outside[0].role].role(outside[0].role).label.lower()}, but it "
                     "isn't under your asset. The game won't find it there.",
            "sigma.parent_to_root", "Put it under the asset", {"names": "\n".join(r.obj.name for r in outside)},
            key="pickoutside:" + o.name)


def _layer_collections(lc):
    yield lc
    for c in lc.children:
        yield from _layer_collections(c)


def errors(problems):
    return [p for p in problems if p.level == ERROR]


classes = ()
