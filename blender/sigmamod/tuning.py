# The car's settings an artist tunes: its mass, which wheels drive, steering, brakes, each axle's springs, the gears
# and the differential. Kept in the scene, written into the car's script as a "-- ## Tuning" block just before the
# car's template line (after every wheel is made, so it can reach them), and read back from it. Where nothing says
# otherwise the values are the Sigma's (its script in SigmaMod), then parts.lua's defaults, then NWH's own.
import re

import bpy

from . import script, sigmasrc

TITLE = "Tuning"
HEADER = "-- ## " + TITLE
NOTE = "-- Written by the SigmaMod add-on from Build > Tuning. Change the values there: export writes this again."
WC = "NWH.WheelController3D.WheelController"
DIFF_TYPES = [
    ('Open', "Open", "Each wheel of the axle turns freely: the one with less grip spins (a wheel in the air takes "
                     "it all). A road car's"),
    ('Locked', "Locked", "Both wheels turn as one: the most traction anywhere, but it fights you in corners. The "
                         "Sigma's"),
    ('LimitedSlip', "Limited slip", "Open until one wheel slips more than the slip torque lets it, then it locks "
                                    "up as far as the ramps say: traction without the fight"),
]
DRIVES = [
    ('rear', "Rear wheels", "The engine drives the rear axle (the Sigma's)"),
    ('front', "Front wheels", "The engine drives the front axle"),
    ('all', "All wheels", "Both axles, through a centre differential"),
]
# NWH's own defaults, for what neither the Sigma nor parts.lua sets (from NWH Vehicle Physics 2's components)
NWH = {"steer": 25.0, "brakes": 7000.0, "bias": 0.5, "diff_stiffness": 0.5, "power_ramp": 1.0, "coast_ramp": 0.5,
       "slip_torque": 400.0}


def _num(pat, text, default):
    m = re.search(pat, text or "")
    return float(m.group(1)) if m else default


def defaults():
    """The Sigma's settings, and parts.lua's defaults for what the Sigma leaves alone."""
    lua, _ = sigmasrc.kit_files()
    sigma = open(lua, encoding="utf-8").read() if lua else ""
    lib = sigmasrc.parts_lua()
    lib = open(lib, encoding="utf-8").read() if lib else ""
    gears = re.search(r'\["powertrain\.transmission\.gears"\]\s*=\s*\{([^}]*)\}', sigma)
    gears = [float(x) for x in re.findall(r"-?[\d.]+", gears.group(1))] if gears else [-3.4, 0, 3.4, 2.0, 1.4, 1.0, 0.86]
    dt = re.search(r'DifferentialType"\]\s*=\s*"(\w+)"', sigma)
    d = re.search(r'\bdrive\s*=\s*"(\w+)"', sigma)
    return {
        "mass": _num(r"\bmass\s*=\s*([\d.]+)", sigma, 1000), "drive": d.group(1) if d else "rear",
        "steer": NWH["steer"], "brakes": NWH["brakes"],
        "spring": _num(r"o\.spring or ([\d.]+)", lib, 0.2), "stiffness": _num(r"o\.stiffness or ([\d.]+)", lib, 12000),
        "damper": _num(r"o\.damper or ([\d.]+)", lib, 6000),
        "reverse": next((g for g in gears if g < 0), -3.4), "gears": [g for g in gears if g > 0],
        "diff": dt.group(1) if dt else "Open",
        "diff_stiffness": _num(r'differentials\.0\.stiffness"\]\s*=\s*([\d.]+)', sigma, NWH["diff_stiffness"]),
        "bias": NWH["bias"], "power_ramp": NWH["power_ramp"], "coast_ramp": NWH["coast_ramp"], "slip_torque": NWH["slip_torque"],
        "centre": "Open", "centre_bias": 0.5,
    }


def _changed(self, context):
    s = context.scene.sigma
    s.tuning.dirty = True
    from . import preview
    preview.update(context)


def _gear_changed(self, context):
    _changed(self, context)


class SigmaGear(bpy.types.PropertyGroup):
    ratio: bpy.props.FloatProperty(name="Ratio", min=0.1, max=20, precision=3, update=_gear_changed,
                                   description="How many times the engine turns for one turn of the gearbox's output. "
                                               "Bigger: more pull, less top speed")


class SigmaAxle(bpy.types.PropertyGroup):
    spring: bpy.props.FloatProperty(name="Travel", unit='LENGTH', min=0.01, max=1, precision=3, update=_changed,
                                    description="How far the wheel can drop below its hub (the hub is the wheel with "
                                                "the suspension pushed all the way up). More: softer over bumps, a "
                                                "taller car")
    stiffness: bpy.props.FloatProperty(name="Spring force", min=100, max=200000, step=10000, precision=0, update=_changed,
                                       description="The spring's force, in newtons, pushed all the way up. Stiffer: "
                                                   "the car sits higher and rolls less, but bounces over bumps")
    damper: bpy.props.FloatProperty(name="Damping", min=0, max=100000, step=10000, precision=0, update=_changed,
                                    description="How hard the shock absorbers resist moving. Too little: the car "
                                                "wallows and bounces on. Too much: every bump goes straight into it")


class SigmaTuning(bpy.types.PropertyGroup):
    loaded: bpy.props.BoolProperty(description="The values have been read from the script (or the Sigma's)")
    dirty: bpy.props.BoolProperty(description="Changed since the script was last written")
    mass: bpy.props.FloatProperty(name="Mass", min=10, max=100000, precision=0, update=_changed,
                                  description="What the car weighs, in kg, without its parts (engine, wheels and "
                                              "doors add their own). The Sigma: 1115. Heavier sits lower on its springs")
    drive: bpy.props.EnumProperty(name="Drive", items=DRIVES, update=_changed,
                                  description="Which wheels the engine turns")
    steer: bpy.props.FloatProperty(name="Steering lock", min=1, max=60, precision=1, update=_changed,
                                   description="How far the front wheels turn, in degrees, at full lock. More: tighter "
                                               "turns, twitchier at speed")
    brakes: bpy.props.FloatProperty(name="Brake torque", min=100, max=100000, step=10000, precision=0, update=_changed,
                                    description="The brakes' strength at each wheel, in N m. Too much locks the wheels")
    front: bpy.props.PointerProperty(type=SigmaAxle)
    rear: bpy.props.PointerProperty(type=SigmaAxle)
    reverse: bpy.props.FloatProperty(name="Reverse", max=-0.1, min=-20, precision=3, update=_changed,
                                     description="The reverse gear's ratio (negative: it turns the other way)")
    gears: bpy.props.CollectionProperty(type=SigmaGear)
    diff: bpy.props.EnumProperty(name="Differential", items=DIFF_TYPES, update=_changed,
                                 description="How the driven axle splits the engine's turn between its two wheels")
    diff_stiffness: bpy.props.FloatProperty(name="Lock stiffness", min=0, max=1, update=_changed,
                                            description="How hard a locked or limited-slip differential holds both "
                                                        "wheels to one speed. Too high can judder (the drivetrain winds up)")
    bias: bpy.props.FloatProperty(name="Left / right split", min=0, max=1, update=_changed,
                                  description="The share of the torque the left wheel gets: 0.5 is even")
    slip_torque: bpy.props.FloatProperty(name="Slip torque", min=0, max=5000, precision=0, update=_changed,
                                         description="Limited slip: the difference in torque (N m) between the wheels "
                                                     "it allows before it starts to lock")
    power_ramp: bpy.props.FloatProperty(name="Power ramp", min=0, max=1, update=_changed,
                                        description="Limited slip: how much it locks under power (accelerating)")
    coast_ramp: bpy.props.FloatProperty(name="Coast ramp", min=0, max=1, update=_changed,
                                        description="Limited slip: how much it locks off the throttle (coasting, braking "
                                                    "with the engine)")
    centre: bpy.props.EnumProperty(name="Centre differential", items=DIFF_TYPES, update=_changed,
                                   description="All-wheel drive: how the engine's turn is split between the axles")
    centre_bias: bpy.props.FloatProperty(name="Front / rear split", min=0, max=1, update=_changed,
                                         description="All-wheel drive: the front axle's share of the torque (0.5: even)")


# ---- the block in the script

def wheels_in(src):
    """[(wheel object, hub marker, rim rotation or None)] for each parts.wheel line in the script."""
    out = []
    for m in re.finditer(r'parts\.wheel\(\s*g\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*\{(.*?)\}\s*\)', src or "", re.S):
        body = m.group(2)
        at = re.search(r'\bat\s*=\s*"((?:[^"\\]|\\.)*)"', body)
        rot = re.search(r'rim_rotation\s*=\s*\{([^}]*)', body)
        out.append((m.group(1), at.group(1) if at else None,
                    [float(x) for x in re.findall(r"-?[\d.]+", rot.group(1))] if rot else None))
    return out


def axles(context, src):
    """(front wheel names, rear wheel names): by where their hubs are, front of the car's middle or behind it (the
    car faces -Y in Blender)."""
    s = context.scene.sigma
    ws = [(w, bpy.data.objects.get(at)) for w, at, _ in wheels_in(src)]
    ys = [o.matrix_world.translation.y for _, o in ws if o is not None]
    if not ys:
        return [], []
    mid = (min(ys) + max(ys)) / 2
    front = [w for w, o in ws if o is not None and o.matrix_world.translation.y < mid - 1e-4]
    rear = [w for w, o in ws if o is not None and o.matrix_world.translation.y >= mid - 1e-4]
    if not front:   # one axle: it's the front one, as NWH counts
        front, rear = rear, []
    return front, rear


def _f(x):
    return f"{x:.4f}".rstrip("0").rstrip(".") if x != int(x) else str(int(x))


def block(context, src):
    t = context.scene.sigma.tuning
    gears = [t.reverse, 0] + [g.ratio for g in t.gears]
    lines = [HEADER, NOTE,
             f"g.Rigidbody.mass = {_f(t.mass)}   -- kg",
             f'g.drive = "{t.drive}"',
             "local tune = g.VehicleController",
             f'tune["steering.maximumSteerAngle"] = {_f(t.steer)}',
             f'tune["brakes.maxTorque"] = {_f(t.brakes)}',
             'tune["powertrain.transmission.gears"] = { ' + ", ".join(_f(x) for x in gears) + " }   -- reverse, neutral, "
             + ("1st" if len(t.gears) == 1 else f"1st - {_ordinal(len(t.gears))}")]
    axle_diffs = [0, 1] if t.drive == "all" else [0]
    for i in axle_diffs:
        p = f'tune["powertrain.differentials.{i}.'
        lines += [f'{p}DifferentialType"] = "{t.diff}"', f'{p}stiffness"] = {_f(t.diff_stiffness)}',
                  f'{p}biasAB"] = {_f(t.bias)}', f'{p}slipTorque"] = {_f(t.slip_torque)}',
                  f'{p}powerRamp"] = {_f(t.power_ramp)}', f'{p}coastRamp"] = {_f(t.coast_ramp)}']
    if t.drive == "all":
        lines += [f'tune["powertrain.differentials.2.DifferentialType"] = "{t.centre}"',
                  f'tune["powertrain.differentials.2.biasAB"] = {_f(t.centre_bias)}   -- the front axle\'s share']
    front, rear = axles(context, src)
    for names, a, what in ((front, t.front, "front axle"), (rear, t.rear, "rear axle")):
        if not names:
            continue
        lines += ["for _, wheel in ipairs({ " + ", ".join(script._q(n) for n in names) + " }) do   -- " + what,
                  f'  local wc = g.nodes[wheel] and g.nodes[wheel]["{WC}"]',
                  "  if wc then",
                  f'    wc["spring.maxLength"], wc["spring.maxForce"] = {_f(a.spring)}, {_f(a.stiffness)}',
                  f'    wc["damper.bumpRate"], wc["damper.reboundRate"] = {_f(a.damper)}, {_f(a.damper)}',
                  "  end",
                  "end"]
    return "\n".join(lines)


def _ordinal(n):
    return f"{n}" + ("st" if n % 10 == 1 and n != 11 else "nd" if n % 10 == 2 and n != 12 else "rd" if n % 10 == 3 and n != 13 else "th")


def span(lines):
    """(start, end) of the Tuning block in the script's lines, or None. It ends at its first blank line."""
    try:
        a = lines.index(HEADER)
    except ValueError:
        return None
    b = a + 1
    while b < len(lines) and lines[b].strip():
        b += 1
    return a, b


def write(context):
    """The block, written again just before the car's template line (so it comes after every wheel made)."""
    s = context.scene.sigma
    if s.kind != 'CAR':
        return False
    from . import paths
    path = paths.asset_lua(s)
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        return False
    load(context, src)
    lines = src.split("\n")
    sp = span(lines)
    if sp:
        a, b = sp
        if b < len(lines) and not lines[b].strip():
            b += 1
        lines = lines[:a] + lines[b:]
    new = script.insert("\n".join(lines), block(context, src), s.asset_id, end=True)
    if new != src:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new)
        script.reload_text(path)
    s.tuning.dirty = False
    return True


def parse(src):
    """The values the script's Tuning block has, by name."""
    lines = (src or "").split("\n")
    sp = span(lines)
    if not sp:
        return {}
    text = "\n".join(lines[sp[0]:sp[1]])
    out = {}
    num = r"(-?[\d.]+)"
    for key, pat in (("mass", r"g\.Rigidbody\.mass = " + num), ("steer", r"maximumSteerAngle\"\] = " + num),
                     ("brakes", r"brakes\.maxTorque\"\] = " + num), ("diff_stiffness", r"differentials\.0\.stiffness\"\] = " + num),
                     ("bias", r"differentials\.0\.biasAB\"\] = " + num), ("slip_torque", r"differentials\.0\.slipTorque\"\] = " + num),
                     ("power_ramp", r"differentials\.0\.powerRamp\"\] = " + num), ("coast_ramp", r"differentials\.0\.coastRamp\"\] = " + num),
                     ("centre_bias", r"differentials\.2\.biasAB\"\] = " + num)):
        m = re.search(pat, text)
        if m:
            out[key] = float(m.group(1))
    for key, pat in (("drive", r'g\.drive = "(\w+)"'), ("diff", r'differentials\.0\.DifferentialType"\] = "(\w+)"'),
                     ("centre", r'differentials\.2\.DifferentialType"\] = "(\w+)"')):
        m = re.search(pat, text)
        if m:
            out[key] = m.group(1)
    m = re.search(r'transmission\.gears"\] = \{([^}]*)\}', text)
    if m:
        gears = [float(x) for x in re.findall(r"-?[\d.]+", m.group(1))]
        out["reverse"] = next((g for g in gears if g < 0), None)
        out["gears"] = [g for g in gears if g > 0]
    for what, key in (("front axle", "front"), ("rear axle", "rear")):
        m = re.search(r"--\s*" + what + r"\n.*?spring\.maxForce\"\] = " + num + r", " + num + r"\n.*?reboundRate\"\] = " + num,
                      text, re.S)
        if m:
            out[key] = (float(m.group(1)), float(m.group(2)), float(m.group(3)))
    return out


def load(context, src=None, force=False):
    """The values into the scene, once: from the script's block, else its chassis line, else the Sigma's."""
    t = context.scene.sigma.tuning
    if t.loaded and not force:
        return
    d = defaults()
    src = src if src is not None else script.read_script(context.scene.sigma)
    m = re.search(r"parts\.chassis\s*\{([^}]*)", src or "")
    if m:   # the car's own chassis line
        mm = re.search(r"\bmass\s*=\s*([\d.]+)", m.group(1))
        dd = re.search(r'\bdrive\s*=\s*"(\w+)"', m.group(1))
        d["mass"] = float(mm.group(1)) if mm else d["mass"]
        d["drive"] = dd.group(1) if dd else d["drive"]
    p = parse(src)
    for k, v in p.items():
        if k in ("front", "rear"):
            d[k] = v
        elif v is not None:
            d[k] = v
    t.mass, t.steer, t.brakes = d["mass"], d["steer"], d["brakes"]
    t.drive = d["drive"] if d["drive"] in {x[0] for x in DRIVES} else "rear"
    for name in ("front", "rear"):
        a = getattr(t, name)
        if name in d:
            a.spring, a.stiffness, a.damper = d[name]
        else:
            a.spring, a.stiffness, a.damper = d["spring"], d["stiffness"], d["damper"]
    t.reverse = d["reverse"]
    t.gears.clear()
    for g in d["gears"]:
        t.gears.add().ratio = g
    t.diff = d["diff"] if d["diff"] in {x[0] for x in DIFF_TYPES} else "Open"
    t.centre = d["centre"] if d["centre"] in {x[0] for x in DIFF_TYPES} else "Open"
    for k in ("diff_stiffness", "bias", "slip_torque", "power_ramp", "coast_ramp", "centre_bias"):
        setattr(t, k, d[k])
    t.loaded, t.dirty = True, False


# ---- what the springs do, worked out the way NWH's wheel does it (the spring's force rises evenly with how far
# it is pushed up, to its full force at the top), for the preview

def rest_drop(context, src=None):
    """{wheel name: how far below its hub it sits with the car standing still}: the car's weight shared between the
    axles by where its middle is (its body's), and between each axle's wheels evenly."""
    s = context.scene.sigma
    t = s.tuning
    src = src if src is not None else (script.read_script(s) or "")
    front, rear = axles(context, src)
    hubs = {w: bpy.data.objects.get(at) for w, at, _ in wheels_in(src)}
    from . import kit
    box = kit.body_box(s)
    if not front or box is None:
        return {}
    yc = (box[0].y + box[1].y) / 2
    yf = sum(hubs[w].matrix_world.translation.y for w in front if hubs.get(w)) / len(front)
    yr = sum(hubs[w].matrix_world.translation.y for w in rear if hubs.get(w)) / len(rear) if rear else yf
    share_f = 1.0 if not rear or abs(yr - yf) < 1e-6 else min(1.0, max(0.0, (yr - yc) / (yr - yf)))
    weight = t.mass * 9.81
    out = {}
    for names, share, a in ((front, share_f, t.front), (rear, 1 - share_f, t.rear)):
        for w in names:
            load = weight * share / len(names)
            pushed = min(1.0, load / max(a.stiffness, 1.0))   # the share of the travel the weight pushes up
            out[w] = a.spring * (1 - pushed)
    return out


# ---- the panel

class SIGMA_OT_gear(bpy.types.Operator):
    """Add a gear (one taller than the last), or take the top one off"""
    bl_idname = "sigma.gear"
    bl_label = "Gear"
    bl_options = {'REGISTER', 'UNDO'}

    remove: bpy.props.BoolProperty(options={'SKIP_SAVE'})

    @classmethod
    def description(cls, context, props):
        return "Take the top gear off" if props.remove else \
            "Add a gear on top, a little taller than the last (more top speed, less pull)"

    def execute(self, context):
        t = context.scene.sigma.tuning
        if self.remove:
            if len(t.gears) > 1:
                t.gears.remove(len(t.gears) - 1)
        elif len(t.gears) < 12:
            last = t.gears[-1].ratio if t.gears else 3.5
            t.gears.add().ratio = round(last * 0.8, 3)
        _changed(self, context)
        return {'FINISHED'}


class SIGMA_OT_write_tuning(bpy.types.Operator):
    """Write the tuning into the car's script now (export does it too). With Hot reload on, the game picks it up"""
    bl_idname = "sigma.write_tuning"
    bl_label = "Write it to the script"
    bl_options = {'REGISTER'}

    def execute(self, context):
        if not write(context):
            self.report({'ERROR'}, "No script yet (step 6 makes it)")
            return {'CANCELLED'}
        self.report({'INFO'}, "Written. Export (or Hot reload) to feel it in game")
        return {'FINISHED'}


class SIGMA_OT_reset_tuning(bpy.types.Operator):
    """Read the tuning again from the script (or the Sigma's values, if the script has none), dropping changes not
    written yet"""
    bl_idname = "sigma.reset_tuning"
    bl_label = "Read it again"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        load(context, force=True)
        return {'FINISHED'}


def draw(l, c, wrap):
    s = c.scene.sigma
    t = s.tuning
    if not t.loaded:
        load(c)
    col = l.column(align=True)
    col.prop(t, "mass")
    col.prop(t, "drive")
    col.prop(t, "steer")
    col.prop(t, "brakes")
    for name, label in (("front", "Front axle"), ("rear", "Rear axle")):
        a = getattr(t, name)
        b = l.box().column(align=True)
        b.label(text=label, icon='MOD_PHYSICS')
        b.prop(a, "spring")
        b.prop(a, "stiffness")
        b.prop(a, "damper")
    b = l.box().column(align=True)
    b.label(text="Gearbox", icon='SETTINGS')
    b.prop(t, "reverse")
    for i, g in enumerate(t.gears):
        b.prop(g, "ratio", text=_ordinal(i + 1))
    row = b.row(align=True)
    row.operator("sigma.gear", text="Add a gear", icon='ADD')
    row.operator("sigma.gear", text="Remove the top", icon='REMOVE').remove = True
    wrap(b, c, "Final drive: set by the engine fitted (each of the game's engines sets its own), so it isn't here.",
         icon='INFO', boxes=4)
    b = l.box().column(align=True)
    b.label(text="Differential", icon='DRIVER_ROTATIONAL_DIFFERENCE')
    b.prop(t, "diff", text="")
    if t.diff != 'Open':
        b.prop(t, "diff_stiffness")
    b.prop(t, "bias")
    if t.diff == 'LimitedSlip':
        b.prop(t, "slip_torque")
        b.prop(t, "power_ramp")
        b.prop(t, "coast_ramp")
    if t.drive == 'all':
        b.prop(t, "centre")
        b.prop(t, "centre_bias")
    row = l.row(align=True)
    row.operator("sigma.write_tuning", icon='FILE_REFRESH' if t.dirty else 'CHECKMARK',
                 text="Write it to the script" if t.dirty else "In the script")
    row.operator("sigma.reset_tuning", text="", icon='LOOP_BACK')


classes = (SIGMA_OT_gear, SIGMA_OT_write_tuning, SIGMA_OT_reset_tuning)
props = (SigmaGear, SigmaAxle, SigmaTuning)
