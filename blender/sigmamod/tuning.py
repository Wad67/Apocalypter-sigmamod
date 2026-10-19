# The car's settings an artist tunes: its mass, which wheels drive, steering, brakes, each axle's springs, the gears
# and the differential. They start as the car's script has them (the last value it sets for each, as at runtime;
# parts.lua's defaults for what it doesn't set), are kept in the scene, and only what the artist changes is written
# into the script: a "-- ## Tuning" block just before the car's template line (after every wheel is made, so it can
# reach them), read back from next time. A car nobody tuned keeps its script as it is.
import re

import bpy

from . import paths, script, sigmasrc

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
    ('none', "None", "The engine drives no wheels (a trailer, a generator: the wheels roll free)"),
    ('script', "As the script has it", "The script names an axle by number: left as it is"),
]
# what each group of settings is in the script; a group is written once the artist changes any of it
GROUPS = ("mass", "drive", "steer", "brakes", "gears", "diff", "centre", "front", "rear", "lift")
VC = {"steer": "steering.maximumSteerAngle", "brakes": "brakes.maxTorque", "gears": "powertrain.transmission.gears"}
DIFF = {"diff": "DifferentialType", "diff_stiffness": "stiffness", "bias": "biasAB", "slip_torque": "slipTorque",
        "power_ramp": "powerRamp", "coast_ramp": "coastRamp"}
# NWH's own defaults, for what neither the script nor parts.lua sets (from NWH Vehicle Physics 2's components)
NWH = {"steer": 25.0, "brakes": 7000.0, "diff": "Open", "diff_stiffness": 0.5, "bias": 0.5, "slip_torque": 400.0,
       "power_ramp": 1.0, "coast_ramp": 0.5, "centre": "Open", "centre_bias": 0.5}
NUM = r"-?\d+(?:\.\d+)?"


def _touch(group):
    def update(self, context):
        t = context.scene.sigma.tuning
        got = set(filter(None, t.touched.split(",")))
        if group not in got:
            t.touched = ",".join(sorted(got | {group}))
        t.dirty = True
        from . import preview
        preview.update(context)
    return update


def _axle_touch(self, context):
    t = context.scene.sigma.tuning
    _touch("front" if self == t.front else "rear")(self, context)


class SigmaGear(bpy.types.PropertyGroup):
    ratio: bpy.props.FloatProperty(name="Ratio", min=0.1, max=20, precision=3, update=_touch("gears"),
                                   description="How many times the engine turns for one turn of the gearbox's output. "
                                               "Bigger: more pull, less top speed")


class SigmaAxle(bpy.types.PropertyGroup):
    spring: bpy.props.FloatProperty(name="Travel", unit='LENGTH', min=0.01, max=1, precision=3, update=_axle_touch,
                                    description="How far the wheel can drop below its hub (the hub is the wheel with "
                                                "the suspension pushed all the way up). More: softer over bumps, a "
                                                "taller car")
    stiffness: bpy.props.FloatProperty(name="Spring force", min=100, max=200000, step=10000, precision=0, update=_axle_touch,
                                       description="The spring's force, in newtons, pushed all the way up. Stiffer: "
                                                   "the car sits higher and rolls less, but bounces over bumps")
    damper: bpy.props.FloatProperty(name="Damping", min=0, max=100000, step=10000, precision=0, update=_axle_touch,
                                    description="How hard the shock absorbers resist moving. Too little: the car "
                                                "wallows and bounces on. Too much: every bump goes straight into it")


class SigmaTuning(bpy.types.PropertyGroup):
    loaded: bpy.props.BoolProperty(description="The values have been read from the script")
    source: bpy.props.StringProperty(description="The script the values were read from: another asset reads its own")
    touched: bpy.props.StringProperty(description="The groups of settings the artist changed: only those are written")
    dirty: bpy.props.BoolProperty(description="Changed since the script was last written")
    drive_script: bpy.props.StringProperty(description="The drive the script gives when it's an axle number")
    mass: bpy.props.FloatProperty(name="Mass", min=10, max=100000, precision=0, update=_touch("mass"),
                                  description="What the car weighs, in kg, without its parts (engine, wheels and "
                                              "doors add their own). The Sigma: 1115. Heavier sits lower on its springs")
    drive: bpy.props.EnumProperty(name="Drive", items=DRIVES, update=_touch("drive"),
                                  description="Which wheels the engine turns")
    steer: bpy.props.FloatProperty(name="Steering lock", min=1, max=60, precision=1, update=_touch("steer"),
                                   description="How far the front wheels turn, in degrees, at full lock. More: tighter "
                                               "turns, twitchier at speed")
    brakes: bpy.props.FloatProperty(name="Brake torque", min=100, max=100000, step=10000, precision=0, update=_touch("brakes"),
                                    description="The brakes' strength at each wheel, in N m. Too much locks the wheels")
    front: bpy.props.PointerProperty(type=SigmaAxle)
    rear: bpy.props.PointerProperty(type=SigmaAxle)
    reverse: bpy.props.FloatProperty(name="Reverse", max=-0.1, min=-20, precision=3, update=_touch("gears"),
                                     description="The reverse gear's ratio (negative: it turns the other way)")
    gears: bpy.props.CollectionProperty(type=SigmaGear)
    gears_known: bpy.props.BoolProperty(description="The script sets the gears (else NWH's own are used, shown here as "
                                                    "the Sigma's until you change them)")
    diff: bpy.props.EnumProperty(name="Differential", items=DIFF_TYPES, update=_touch("diff"),
                                 description="How the driven axle splits the engine's turn between its two wheels")
    diff_stiffness: bpy.props.FloatProperty(name="Lock stiffness", min=0, max=1, update=_touch("diff"),
                                            description="How hard a locked or limited-slip differential holds both "
                                                        "wheels to one speed. Too high can judder (the drivetrain winds up)")
    bias: bpy.props.FloatProperty(name="Left / right split", min=0, max=1, update=_touch("diff"),
                                  description="The share of the torque the left wheel gets: 0.5 is even")
    slip_torque: bpy.props.FloatProperty(name="Slip torque", min=0, max=5000, precision=0, update=_touch("diff"),
                                         description="Limited slip: the difference in torque (N m) between the wheels "
                                                     "it allows before it starts to lock")
    power_ramp: bpy.props.FloatProperty(name="Power ramp", min=0, max=1, update=_touch("diff"),
                                        description="Limited slip: how much it locks under power (accelerating)")
    coast_ramp: bpy.props.FloatProperty(name="Coast ramp", min=0, max=1, update=_touch("diff"),
                                        description="Limited slip: how much it locks off the throttle (coasting, braking "
                                                    "with the engine)")
    centre: bpy.props.EnumProperty(name="Centre differential", items=DIFF_TYPES, update=_touch("centre"),
                                   description="All-wheel drive: how the engine's turn is split between the axles")
    centre_bias: bpy.props.FloatProperty(name="Front / rear split", min=0, max=1, update=_touch("centre"),
                                         description="All-wheel drive: the front axle's share of the torque (0.5: even)")
    lift_spring: bpy.props.FloatProperty(name="Lifted travel", unit='LENGTH', min=0.01, max=1.5, precision=3, update=_touch("lift"),
                                         description="With a lift kit fitted: how far each wheel can drop below its hub. "
                                                     "More than stock: the car sits higher (the game's: 0.2 to 0.31)")
    lift_damper: bpy.props.FloatProperty(name="Lifted damping", min=0, max=100000, step=10000, precision=0, update=_touch("lift"),
                                         description="With a lift kit fitted: the shock absorbers (the game's: halved, "
                                                     "3000 to 1500: it floats over rough ground)")
    lift_widen: bpy.props.FloatProperty(name="Wheels out by", unit='LENGTH', min=0, max=1, precision=3, update=_touch("lift"),
                                        description="With a lift kit fitted: how far each wheel moves outward (a wider "
                                                    "track, steadier when tall; the game's: 0.22 m)")


# ---- what the script says

def _q_str():
    return r'(?:"((?:[^"\\]|\\.)*)"|\'((?:[^\'\\]|\\.)*)\')'


def wheels_in(src):
    """[(wheel object, hub marker or None, rim rotation or None, its options)] for each parts.wheel line."""
    out = []
    q = _q_str()
    for m in re.finditer(r'parts\.wheel\(\s*g\s*,\s*' + q + r'\s*,\s*\{(.*?)\}\s*\)', src or "", re.S):
        name, body = m.group(1) if m.group(1) is not None else m.group(2), m.group(3)
        at = re.search(r'\bat\s*=\s*' + q, body)
        rot = re.search(r'rim_rotation\s*=\s*\{([^}]*)', body)
        opts = {k: float(v) for k, v in re.findall(r'\b(spring|stiffness|damper)\s*=\s*(' + NUM + ')', body)}
        out.append((name, (at.group(1) if at.group(1) is not None else at.group(2)) if at else None,
                    [float(x) for x in re.findall(NUM, rot.group(1))] if rot else None, opts))
    return out


def lift_slot(src):
    """The lift kit slot's name in the script (its parts.lift_kit line), or None."""
    m = re.search(r"parts\.lift_kit\(\s*g\s*(?:,\s*\{(.*?)\}\s*)?\)", src or "", re.S)
    if not m:
        return None
    s = re.search(r'\bslot\s*=\s*"((?:[^"\\]|\\.)*)"', m.group(1) or "")
    return s.group(1) if s else "hinge_suspension"


def lift_lists(src):
    """(lifted, stock): the object names the lift kit line shows with a kit fitted, and hides."""
    m = re.search(r"parts\.lift_kit\(\s*g\s*,\s*\{(.*?)\}\s*\)\s*$", src or "", re.S | re.M)
    body = m.group(1) if m else ""
    out = []
    for key in ("lifted", "stock"):
        mm = re.search(r"\b" + key + r"\s*=\s*\{([^}]*)\}", body)
        out.append(re.findall(r'"((?:[^"\\]|\\.)*)"', mm.group(1)) if mm else [])
    return out


def lift_opts(src):
    """The lift kit's own spring, damper, widen in its parts.lift_kit line (what it has without Tuning)."""
    m = re.search(r"parts\.lift_kit\(\s*g\s*,\s*\{(.*?)\}\s*\)\s*$", src or "", re.S | re.M)
    body = m.group(1) if m else ""
    return {k: float(v) for k, v in re.findall(r"\b(spring|damper|widen)\s*=\s*(" + NUM + ")", body)}


def hub_of(name, at):
    """The object a wheel is at in Blender: its marker, else an object named as the wheel; None if neither exists."""
    return bpy.data.objects.get(at) if at else bpy.data.objects.get(name)


def axles(context, src):
    """(front wheel names, rear wheel names): by where their hubs are, front of the car's middle or behind it (the
    car faces -Y in Blender). A wheel whose hub isn't in Blender is in neither."""
    ws = [(w, hub_of(w, at)) for w, at, _, _ in wheels_in(src)]
    ws = [(w, o) for w, o in ws if o is not None]
    if not ws:
        return [], []
    ys = [o.matrix_world.translation.y for _, o in ws]
    mid = (min(ys) + max(ys)) / 2
    front = [w for w, o in ws if o.matrix_world.translation.y < mid - 1e-4]
    rear = [w for w, o in ws if o.matrix_world.translation.y >= mid - 1e-4]
    if not front:   # one axle: it's the front one, as NWH counts
        front, rear = rear, []
    return front, rear


# the lines the add-on writes in the block: anything else (a line of the artist's, a template) ends it
OWN = re.compile(r'(-- (?!#)|g\.Rigidbody\b|g\.drive\b|g\.VehicleController\b|local tune = |tune\[|for _, wheel in |  |end$|local lift = |if lift then )')


def span(lines):
    """(start, end) of the Tuning block in the script's lines, or None. It ends at the first line that isn't one
    the add-on writes there: a blank line, the next section or part, a template line, a line of the artist's."""
    try:
        a = lines.index(HEADER)
    except ValueError:
        return None
    b = a + 1
    while b < len(lines) and lines[b].strip() and OWN.match(lines[b]):
        b += 1
    return a, b


def _block_text(src):
    lines = (src or "").split("\n")
    sp = span(lines)
    return ("\n".join(lines[sp[0]:sp[1]]), "\n".join(lines[:sp[0]] + lines[sp[1]:])) if sp else ("", src or "")


def _vc_values(text):
    """["key"] = value assignments, the last of each (as the script runs: later wins)."""
    out = {}
    for k, v in re.findall(r'\["([\w.]+)"\]\s*=\s*(\{[^}]*\}|"[^"]*"|' + NUM + ')', text or ""):
        out[k] = v
    return out


def _val(v):
    if v.startswith("{"):
        return [float(x) for x in re.findall(NUM, v)]
    if v.startswith('"'):
        return v[1:-1]
    return float(v)


def read(src):
    """Everything the script sets that Tuning covers: {name: value}, and the groups its Tuning block has."""
    block, rest = _block_text(src)
    out, in_block = {}, set()
    lib = sigmasrc.parts_lua()
    lib = open(lib, encoding="utf-8").read() if lib else ""
    # parts.lua's defaults, which a parts.chassis / parts.wheel line gets for what it leaves out
    m = re.search(r"Rigidbody = \{ mass = o\.mass or (" + NUM + ")", lib)
    out["mass"] = float(m.group(1)) if m else 500.0
    m = re.search(r'drive = o\.drive or "(\w+)"', lib)
    out["drive"] = m.group(1) if m else "rear"
    for k, dflt in (("spring", 0.2), ("stiffness", 12000.0), ("damper", 6000.0)):
        m = re.search(r"o\." + k + r" or (" + NUM + ")", lib)
        out[k] = float(m.group(1)) if m else dflt
    for text, mine in ((rest, False), (block, True)):   # the block last: it's what runs last
        chassis = re.search(r"parts\.chassis\s*\{([^\n]*)\}|parts\.chassis\s*\{(.*?)\n\}", text, re.S)
        body = (chassis.group(1) or chassis.group(2) or "") if chassis else ""
        for pat in (r"\bmass\s*=\s*(" + NUM + ")", r"Rigidbody\s*=\s*\{\s*mass\s*=\s*(" + NUM + ")",
                    r"g\.Rigidbody\.mass\s*=\s*(" + NUM + ")"):
            mm = re.search(pat, body if pat.startswith(r"\bmass") else text)
            if mm:
                out["mass"] = float(mm.group(1))
                in_block |= {"mass"} if mine else set()
        for pat in (r'\bdrive\s*=\s*(?:"(\w+)"|(\d+))\s*[,}\n]', r'g\.drive\s*=\s*(?:"(\w+)"|(\d+))'):
            mm = re.search(pat, (body or text) if pat.startswith(r"\bdrive") else text)
            if mm:
                out["drive"] = mm.group(1) or mm.group(2)
                in_block |= {"drive"} if mine else set()
        vc = _vc_values(text)
        for name, key in VC.items():
            if key in vc:
                out[name] = _val(vc[key])
                in_block |= {name} if mine else set()
        for name, key in DIFF.items():
            k0 = f"powertrain.differentials.0.{key}"
            if k0 in vc:
                out[name] = _val(vc[k0])
                in_block |= {"diff"} if mine else set()
        for name, key in (("centre", "DifferentialType"), ("centre_bias", "biasAB")):
            k2 = f"powertrain.differentials.2.{key}"
            if k2 in vc:
                out[name] = _val(vc[k2])
                in_block |= {"centre"} if mine else set()
    mm = re.search(r"lift\.spring, lift\.damper, lift\.widen = (" + NUM + "), (" + NUM + "), (" + NUM + ")", block)
    if mm:
        out["lift"] = tuple(float(x) for x in mm.groups())
        in_block.add("lift")
    for what, key in (("front axle", "front"), ("rear axle", "rear")):
        mm = re.search(r"--\s*" + what + r"\n.*?spring\.maxForce\"\] = (" + NUM + "), (" + NUM + r")\n.*?reboundRate\"\] = (" + NUM + "), (" + NUM + ")",
                       block, re.S)
        if mm:
            out[key] = (float(mm.group(1)), float(mm.group(2)), float(mm.group(3)))
            in_block.add(key)
    return out, in_block


# ---- the block

def _f(x):
    x = float(x)
    return f"{x:.4f}".rstrip("0").rstrip(".") if x != int(x) else str(int(x))


def _ordinal(n):
    return f"{n}" + ("st" if n % 10 == 1 and n != 11 else "nd" if n % 10 == 2 and n != 12 else "rd" if n % 10 == 3 and n != 13 else "th")


def written(t, in_block=()):
    """The groups the block holds: what the artist changed, and what an earlier block already had."""
    return set(filter(None, t.touched.split(","))) | set(in_block)


def block(context, src, groups):
    """The Tuning block for these groups ("" when there's nothing to write)."""
    t = context.scene.sigma.tuning
    if t.drive == 'script':
        groups = groups - {"drive"}
    lines = []
    if "mass" in groups:
        lines += ["g.Rigidbody = g.Rigidbody or {}", f"g.Rigidbody.mass = {_f(t.mass)}   -- kg"]
    if "drive" in groups:
        lines.append(f'g.drive = "{t.drive}"')
    vc = []
    if "steer" in groups:
        vc.append(f'tune["{VC["steer"]}"] = {_f(t.steer)}')
    if "brakes" in groups:
        vc.append(f'tune["{VC["brakes"]}"] = {_f(t.brakes)}')
    if "gears" in groups:
        gears = [t.reverse, 0] + [g.ratio for g in t.gears]
        vc.append(f'tune["{VC["gears"]}"] = {{ ' + ", ".join(_f(x) for x in gears) + " }   -- reverse, neutral, "
                  + ("1st" if len(t.gears) == 1 else f"1st - {_ordinal(len(t.gears))}"))
    if "diff" in groups:
        for i in ([0, 1] if t.drive == "all" else [0]):
            for name, key in DIFF.items():
                v = getattr(t, name)
                vc.append(f'tune["powertrain.differentials.{i}.{key}"] = ' + (f'"{v}"' if isinstance(v, str) else _f(v)))
    if "centre" in groups and t.drive == "all":
        vc += [f'tune["powertrain.differentials.2.DifferentialType"] = "{t.centre}"',
               f'tune["powertrain.differentials.2.biasAB"] = {_f(t.centre_bias)}   -- the front axle\'s share']
    if vc:
        lines += ["g.VehicleController = g.VehicleController or {}", "local tune = g.VehicleController"] + vc
    slot = lift_slot(src)
    if "lift" in groups and slot:
        q = script._q(slot)
        lines += [f'local lift = g.nodes[{q}] and g.nodes[{q}]["SigmaMod.Parts.LiftKit"]',
                  f"if lift then lift.spring, lift.damper, lift.widen = {_f(t.lift_spring)}, {_f(t.lift_damper)}, "
                  f"{_f(t.lift_widen)} end   -- with a lift kit fitted"]
    front, rear = axles(context, src)
    for names, a, what, key in ((front, t.front, "front axle", "front"), (rear, t.rear, "rear axle", "rear")):
        if not names or key not in groups:
            continue
        lines += ["for _, wheel in ipairs({ " + ", ".join(script._q(n) for n in names) + " }) do   -- " + what,
                  f'  local wc = g.nodes and g.nodes[wheel] and g.nodes[wheel]["{WC}"]',
                  "  if wc then",
                  f'    wc["spring.maxLength"], wc["spring.maxForce"] = {_f(a.spring)}, {_f(a.stiffness)}',
                  f'    wc["damper.bumpRate"], wc["damper.reboundRate"] = {_f(a.damper)}, {_f(a.damper)}',
                  "  end",
                  "end"]
    return "\n".join([HEADER, NOTE] + lines) if lines else ""


def write(context):
    """The block, written again just before the car's template line (so it comes after every wheel made), with what
    the artist changed. Nothing changed and no block: the script is left alone."""
    s = context.scene.sigma
    path = paths.asset_lua(s)
    if s.kind != 'CAR' or not path:
        return False
    t = script.text_for(path, load=False)
    if t is not None and t.is_dirty:
        script.save_text(t)
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        return False
    load(context, src)
    _, in_block = read(src)
    groups = written(s.tuning, in_block)
    lines = src.split("\n")
    sp = span(lines)
    if not groups and not sp:
        s.tuning.dirty = False
        return True
    if sp:
        a, b = sp
        if b < len(lines) and not lines[b].strip():
            b += 1
        lines = lines[:a] + lines[b:]
    code = block(context, src, groups)
    new = script.insert("\n".join(lines), code, s.asset_id, end=True) if code else "\n".join(lines)
    if new != src:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new)
        script.reload_text(path)
    s.tuning.dirty = False
    return True


def current(context):
    """Whether the values in the scene are this asset's script's."""
    s = context.scene.sigma
    return s.tuning.loaded and s.tuning.source == (paths.asset_lua(s) or "")


def load(context, src=None, force=False):
    """The values into the scene from the script (once per script: another asset, or another id, reads again)."""
    s = context.scene.sigma
    t = s.tuning
    if current(context) and not force:
        return
    src = src if src is not None else (script.read_script(s) or "")
    v, _ = read(src)
    lua, _ = sigmasrc.kit_files()
    sigma = open(lua, encoding="utf-8").read() if lua else ""
    t.mass = v["mass"]
    d = str(v["drive"])
    t.drive_script = d if d.isdigit() else ""
    t.drive = 'script' if d.isdigit() else (d if d in {x[0] for x in DRIVES} else "rear")
    t.steer, t.brakes = v.get("steer", NWH["steer"]), v.get("brakes", NWH["brakes"])
    wheels = wheels_in(src)
    front, rear = axles(context, src)
    for name, names in (("front", front), ("rear", rear)):
        a = getattr(t, name)
        if name in v:
            a.spring, a.stiffness, a.damper = v[name]
        else:   # as its wheels' lines have it (the first of the axle), else parts.lua's defaults
            opts = next((o for w, _, _, o in wheels if w in names), {})
            a.spring, a.stiffness, a.damper = (opts.get("spring", v["spring"]), opts.get("stiffness", v["stiffness"]),
                                               opts.get("damper", v["damper"]))
    gears = v.get("gears")
    t.gears_known = gears is not None
    if gears is None:   # the script leaves NWH's: shown as the Sigma's, a start, written only once changed
        sg = _vc_values(sigma).get(VC["gears"])
        gears = _val(sg) if sg else [-3.4, 0, 3.4, 2.0, 1.4, 1.0, 0.86]
    t.reverse = next((g for g in gears if g < 0), -3.4)
    t.gears.clear()
    for g in gears:
        if g > 0:
            t.gears.add().ratio = g
    for name in ("diff", "centre"):
        val = v.get(name, NWH[name])
        setattr(t, name, val if val in {x[0] for x in DIFF_TYPES} else "Open")
    for name in ("diff_stiffness", "bias", "slip_torque", "power_ramp", "coast_ramp", "centre_bias"):
        setattr(t, name, v.get(name, NWH[name]))
    lo = lift_opts(src)   # lifted: the block's, else the lift kit line's, else the game's ratios on the front's stock
    t.lift_spring, t.lift_damper, t.lift_widen = v.get("lift") or (lo.get("spring") or t.front.spring * 1.55,
                                                                    lo.get("damper") or t.front.damper * 0.5, lo.get("widen", 0.22))
    t.touched, t.loaded, t.dirty, t.source = "", True, False, paths.asset_lua(s) or ""


# ---- what the springs do, worked out the way NWH's wheel does it (the spring's force rises evenly with how far
# it is pushed up, to its full force at the top), for the preview

def rest_drop(context, src=None):
    """{wheel name: how far below its hub it sits with the car standing still}: the car's weight shared between the
    axles by where its middle is (its body's), and between each axle's wheels evenly."""
    s = context.scene.sigma
    t = s.tuning
    src = src if src is not None else (script.read_script(s) or "")
    front, rear = axles(context, src)
    hubs = {w: hub_of(w, at) for w, at, _, _ in wheels_in(src)}
    from . import kit
    box = kit.body_box(s)
    if not front or box is None:
        return {}
    yc = (box[0].y + box[1].y) / 2
    yf = sum(hubs[w].matrix_world.translation.y for w in front) / len(front)
    yr = sum(hubs[w].matrix_world.translation.y for w in rear) / len(rear) if rear else yf
    share_f = 1.0 if not rear or abs(yr - yf) < 1e-6 else min(1.0, max(0.0, (yr - yc) / (yr - yf)))
    weight = t.mass * 9.81
    lifted = s.preview_lifted and lift_slot(src)
    out = {}
    for names, share, a in ((front, share_f, t.front), (rear, 1 - share_f, t.rear)):
        for w in names:
            load_ = weight * share / len(names)
            pushed = min(1.0, load_ / max(a.stiffness, 1.0))   # the share of the travel the weight pushes up
            out[w] = (t.lift_spring if lifted else a.spring) * (1 - pushed)
    return out


def travel(context, src=None):
    """{wheel name: its spring's whole travel}: how far below its hub it hangs fully extended (lifted, with Show it
    lifted on and a lift kit slot)."""
    s = context.scene.sigma
    t = s.tuning
    src = src if src is not None else (script.read_script(s) or "")
    front, rear = axles(context, src)
    lifted = s.preview_lifted and lift_slot(src)
    out = {}
    for names, a in ((front, t.front), (rear, t.rear)):
        for w in names:
            out[w] = t.lift_spring if lifted else a.spring
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
        _touch("gears")(self, context)
        return {'FINISHED'}


class SIGMA_OT_write_tuning(bpy.types.Operator):
    """Write what you changed in Tuning into the car's script now (export does it too). With Hot reload on, the game
    picks it up"""
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
    """Read the tuning again from the car's script, dropping changes not written to it yet"""
    bl_idname = "sigma.reset_tuning"
    bl_label = "Read it again"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        load(context, force=True)
        from . import preview
        preview.update(context)
        return {'FINISHED'}


def opened(self, context):
    """Tuning opened: its values read from the script now (a panel's drawing can't change them)."""
    if self.open_tuning and self.kind == 'CAR':
        load(context)


def draw(l, c, wrap):
    s = c.scene.sigma
    t = s.tuning
    if not current(c):
        wrap(l, c, "Tuning starts from what your car's script says.", boxes=2)
        l.operator("sigma.reset_tuning", text="Read the car's tuning", icon='IMPORT')
        return
    col = l.column(align=True)
    col.prop(t, "mass")
    col.prop(t, "drive")
    if t.drive == 'script':
        col.label(text=f"The script drives axle {t.drive_script}: pick one above to change it", icon='INFO')
    col.prop(t, "steer")
    col.prop(t, "brakes")
    row = l.row(align=True)
    row.prop(s, "preview_suspension", expand=True)
    for name, label in (("front", "Front axle"), ("rear", "Rear axle")):
        a = getattr(t, name)
        b = l.box().column(align=True)
        b.label(text=label, icon='MOD_PHYSICS')
        b.prop(a, "spring")
        b.prop(a, "stiffness")
        b.prop(a, "damper")
    b = l.box().column(align=True)
    b.label(text="Lift kit", icon='TRIA_UP_BAR')
    if lift_slot(script.read_script(s) or ""):
        b.prop(t, "lift_spring")
        b.prop(t, "lift_damper")
        b.prop(t, "lift_widen")
        b.prop(s, "preview_lifted", toggle=True, icon='HIDE_OFF')
    elif re.search(r'parts\.slot\(\s*g\s*,\s*"(?:[^"\\]|\\.)*"\s*,\s*"suspension"', script.read_script(s) or ""):
        wrap(b, c, "Your script has the lift kit slot as an older kit wrote it: a lift kit clips in, but doesn't lift the "
                   "car. Update the script writes the section again as the kit has it now (your picks stay); export "
                   "does it too.", icon='ERROR', boxes=4)
        b.operator("sigma.update_kit_script", icon='FILE_REFRESH')
    else:
        wrap(b, c, "No lift kit slot: Wheels and suspension's Lift kit slot adds one (the game's lift kits clip in "
                   "there and lift the car).", icon='INFO', boxes=4)
    b = l.box().column(align=True)
    b.label(text="Gearbox", icon='SETTINGS')
    if not t.gears_known and "gears" not in t.touched:
        wrap(b, c, "The script doesn't set gears, so the car has NWH's own. Shown: the Sigma's, written once you change "
                   "any.", icon='INFO', boxes=4)
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
